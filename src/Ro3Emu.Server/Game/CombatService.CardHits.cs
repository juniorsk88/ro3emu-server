using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Hits that cards add to a player's own hit on monsters (splash, autocast skills): no cast of their own, but the ordinary
/// hit settlement (MVP/event damage, kill rewards to the player) and a SkillHit so the client draws the number.</summary>
public sealed partial class CombatService
{
    /// <summary>A player's skill hit another player (attacker, target, the skill row, damage dealt): card reflects.</summary>
    public List<Func<GameSession, GameSession, JsonElement, long, Task>> PvpHitTaken { get; } = new();

    /// <summary>Damage reflected to the attacker by a card. It can take the attacker down to 1 HP but never kill (inferred: the death path is the
    /// victim's own handling of a lethal hit).</summary>
    internal async Task ReflectToPlayerAsync(GameSession attacker, GameSession reflector, long amount)
    {
        if (attacker.Role == null || reflector.Role == null || attacker.Dead || attacker.SceneLoading || amount <= 0) return;
        long current = character.Stats(attacker)[2000], to = Math.Max(1, current - amount);
        if (to >= current) return;
        await character.SetHpSpAsync(attacker, to, null);
        var hit = new SkillHitInfo
        {
            SrcID = reflector.Role.Id, SessionId = ++_sessionIds, SrcElement = 1,
            Dsts = { new DamageInfo { DstID = attacker.Role.Id, Damage = current - to, DstElement = 1 } },
        };
        foreach (var viewer in world.Viewers(attacker).Append(attacker).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
    }

    /// <summary>Monsters the player may hit within <paramref name="radius"/> metres of <paramref name="center"/> (not the center itself), nearest first.</summary>
    internal List<MonsterUnit> MonstersAround(GameSession s, MonsterUnit center, float radius, int max) =>
        world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m != center && m.Hp > 0 && CanHitMonster(s, m) && MonsterService.Distance(center.X, center.Z, m.X, m.Z) <= radius)
            .OrderBy(m => MonsterService.Distance(center.X, center.Z, m.X, m.Z)).ThenBy(m => m.Uid).Take(max).ToList();

    /// <summary>The nearest monster the player may hit within <paramref name="radius"/> metres of the player, or null.</summary>
    internal MonsterUnit? NearestMonster(GameSession s, float radius) =>
        world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m.Hp > 0 && CanHitMonster(s, m) && MonsterService.Distance(m, s) <= radius)
            .OrderBy(m => MonsterService.Distance(m, s)).ThenBy(m => m.Uid).FirstOrDefault();

    /// <summary>
    /// A hit of <paramref name="amount"/> that a card adds on <paramref name="m"/>, shown as skill <paramref name="skillId"/> (0: no skill).
    /// The caller is on the game loop; the player must still be in that scene.
    /// </summary>
    internal async Task HitMonsterAsync(GameSession s, MonsterUnit m, long amount, int skillId, DamageHitResult result = DamageHitResult.Normal)
    {
        var role = s.Role;
        if (role == null || amount <= 0 || s.SceneLoading || s.Dead || s.WorldScene != m.WorldScene || m.Dead || m.Hp <= 0 || !CanHitMonster(s, m)) return;
        var attachment = s.Attachment;
        long generation = s.SceneGeneration, scene = s.WorldScene;
        bool Current() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment) && !s.SceneLoading
            && s.SceneGeneration == generation && s.WorldScene == scene && sessions.All().Contains(s);
        long before = m.Hp;
        m.Hp = Math.Max(0, before - amount);
        bool lethal = m.Hp == 0; // reserved before the awaited settlement, like a skill hit
        try
        {
            try { await SettleMonsterHitAsync(s, m, before, amount); }
            catch (Exception e) when (e is not EventSettlementOutcomeUnknownException)
            {
                log.LogDebug(e, "Card hit {Skill} on {Uid} not settled", skillId, m.Uid);
                return;
            }
            var hit = new SkillHitInfo
            {
                SrcID = role.Id, SkillId = skillId, SessionId = ++_sessionIds, SrcElement = 1,
                Dsts = { new DamageInfo { DstID = m.Uid, Damage = amount, HitResult = result, DstElement = m.Template.Element, IsDead = m.Hp == 0 } },
            };
            foreach (var viewer in m.Viewers.Append(s).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
            await RecordDamageStatisticAsync(s, new DamageStatistics { DamageSourceId = role.Id, DamageTargetId = m.Uid, DamageValue = amount, SkillId = skillId });
            if (m.Hp == 0 && !m.DeathDurable) return; // another hit is still committing that death
            await world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
            await FightingAsync(m);
            if (m.Hp > 0)
            {
                if (Current()) monsters.Engage(m, s);
                return;
            }
            log.LogInformation("{Name} killed {Monster} ({Npc}) with a card effect", role.Name, m.Uid, m.Template.NpcId);
            await monsters.KillAsync(m);
            foreach (var other in sessions.All().Where(o => o.Attack?.Target == m))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(s, m, Current);
        }
        finally
        {
            if (lethal && m.Hp == 0 && m.DeathDurable)
            {
                if (!m.Dead)
                    try { await monsters.KillAsync(m); }
                    catch (Exception e) { log.LogError(e, "Could not finalize card kill of {Uid}", m.Uid); }
                await PublishDurableKillAsync(s, m, Current);
                await FinalizeLethalPublicationAsync(m);
            }
        }
    }

    /// <summary>An autocast (a card triggers skill row <paramref name="skillRowId"/>) on <paramref name="target"/>: the skill's damage from the
    /// player's stats with the usual adjusters, no cooldown, cost or buffs. (inferred: the cards' text only names the skill and its level)</summary>
    internal async Task AutocastOnMonsterAsync(GameSession s, MonsterUnit target, int skillRowId)
    {
        if (s.Role == null || s.Progress == null || data.Row("SkillConfig", skillRowId) is not { } row) return;
        var stats = character.Stats(s);
        var attacker = new Fighter(stats[103], stats[104], stats[109], stats[111], s.Progress.Level, 1);
        var defender = new Fighter(0, 0, target.Template.Flee, 0, target.Template.Level, target.Template.Element)
            { Def = target.Template.Def, Mdef = target.Template.Mdef, Flee = target.Template.Flee };
        var (amount, result) = Damage(attacker, defender, row);
        if (amount <= 0) return;
        foreach (var modifier in DamageAdjusters) amount = Math.Max(0, modifier(s, target, row, result, amount));
        await HitMonsterAsync(s, target, amount, skillRowId, result);
    }
}
