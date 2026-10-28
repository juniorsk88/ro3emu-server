using System.Collections.Concurrent;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What the server owes the client to draw one skill. The client plays the cast timeline (skill_N.json
/// m_kShows) by itself; the server only picks the hit effect (SkillHit.ShowPoint) and, for projectile skills
/// that the timeline does not spawn, sends the projectile (SkillFixTimeBulletNotify, 31120).
/// </summary>
/// <param name="HitEffect">Effect id with m_kActionDamge (DamageType 3): hit gfx, flinch animation, hit sound.</param>
/// <param name="BulletEffect">Effect id with m_kBulletData (DamageType 2), or 0 when the client needs no server projectile.</param>
/// <param name="Bullet">The m_kBulletData of <paramref name="BulletEffect"/>; flight time and track come from it.</param>
/// <param name="HitHasGfx">The skill's own hit effect draws a gfx (Blessing's angel glow, Increase AGI's swirl); the generic hit never counts.</param>
internal sealed record SkillVisual(int HitEffect, int BulletEffect, JsonElement? Bullet, bool HitHasGfx = false)
{
    public const int GenericHitEffect = 1000002;
    public static readonly SkillVisual Generic = new(GenericHitEffect, 0, null);

    /// <summary>
    /// Client facts (re-derived from the decompiled Lua, see admin/CONHECIMENTO-SKILLS.md):
    /// effect ids live in [scriptShow*100, scriptShow*100+99]; Lua_Config_EffectData.m_kDamage.m_kDamageType
    /// says what each one is (2 projectile, 3 hit reaction). LC_DamageHelper.CalcDamageHitPoint draws a hit
    /// only when ShowPoint is a type-3 effect, so the first m_kDamage of the range is wrong for projectile skills.
    /// </summary>
    internal static SkillVisual Build(GameData data, JsonElement row)
    {
        long scriptShow = GameData.Int(row, "_iScriptShow");
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        var actions = new List<int>();
        var bullets = new List<(int Id, JsonElement Config)>();
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
        {
            if (!effects.TryGetValue(id.ToString(), out var effect)
                || !effect.TryGetProperty("m_kDamage", out var damage) || damage.ValueKind != JsonValueKind.Object) continue;
            int type = (int)(damage.TryGetProperty("m_kDamageType", out var t) ? GameData.Number(t) ?? 0 : 0);
            if (type == 3 && damage.TryGetProperty("m_kActionDamge", out _)) actions.Add((int)id);
            else if (type == 2 && damage.TryGetProperty("m_kBulletData", out var bullet) && bullet.ValueKind == JsonValueKind.Object)
                bullets.Add(((int)id, bullet));
        }

        // SkillConfig._kLogicData is the original script's own effect list: a type-2 id there is its projectile and a
        // type-3 id its hit (Soul Destroyer [1531002, 1531003]); about 190 skills carry one, some outside the range.
        int scriptBullet = 0, scriptHit = 0;
        if (row.TryGetProperty("_kLogicData", out var logic) && logic.ValueKind == JsonValueKind.Array)
            foreach (var item in logic.EnumerateArray())
            {
                if (GameData.Number(item) is not { } number || number < 1_000_000 || !effects.TryGetValue(((long)number).ToString(), out var named)
                    || !named.TryGetProperty("m_kDamage", out var namedDamage) || namedDamage.ValueKind != JsonValueKind.Object) continue;
                int type = (int)(namedDamage.TryGetProperty("m_kDamageType", out var nt) ? GameData.Number(nt) ?? 0 : 0);
                if (type == 2 && scriptBullet == 0 && namedDamage.TryGetProperty("m_kBulletData", out var nb) && nb.ValueKind == JsonValueKind.Object)
                { scriptBullet = (int)number; if (!bullets.Any(b => b.Id == scriptBullet)) bullets.Add((scriptBullet, nb)); }
                else if (type == 3 && scriptHit == 0 && namedDamage.TryGetProperty("m_kActionDamge", out _))
                { scriptHit = (int)number; if (!actions.Contains(scriptHit)) actions.Add(scriptHit); }
            }

        // The timeline's own m_kClientPoints make the client launch its projectiles (LC_Skill_RunTimeContext.PlayClientEffect);
        // a server projectile on top would draw them twice.
        bool clientBullets = data.SkillTemplate(scriptShow) is { } template
            && template.TryGetProperty("m_kClientPoints", out var points) && points.ValueKind == JsonValueKind.Array && points.GetArrayLength() > 0;

        // Variants share a range (Fire Ball has an empty-prefab projectile and a real one); an empty
        // m_kBulletName draws nothing, so prefer the first projectile that has a prefab.
        (int Id, JsonElement Config)? chosen = null;
        if (!clientBullets && bullets.Count > 0)
            chosen = scriptBullet != 0 ? bullets.First(b => b.Id == scriptBullet)
                : bullets.FirstOrDefault(b => !string.IsNullOrEmpty(BulletName(b.Config)), bullets[0]);

        // The hit that belongs to the projectile is the next type-3 effect after it (1200503 -> 1200504); a skill
        // with only one such effect, or a projectile that comes last (Fire Bolt 1200411), uses the first one.
        // A range with no type-3 effect (only projectile / anim-group / pet-show data) gets the generic hit, because
        // CalcDamageHitPoint ignores any other kind.
        int hit = scriptHit != 0 ? scriptHit : actions.Count == 0 ? GenericHitEffect
            : chosen is { } c ? actions.FirstOrDefault(a => a > c.Id, actions[0]) : actions[0];
        bool gfx = hit != GenericHitEffect && effects.TryGetValue(hit.ToString(), out var hitEffect)
            && hitEffect.TryGetProperty("m_kDamage", out var hitDamage) && hitDamage.ValueKind == JsonValueKind.Object
            && hitDamage.TryGetProperty("m_kActionDamge", out var action) && action.ValueKind == JsonValueKind.Object
            && action.TryGetProperty("m_kGfxList", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;
        return new SkillVisual(hit, chosen?.Id ?? 0, chosen?.Config, gfx);
    }

    private static string BulletName(JsonElement bullet) =>
        bullet.TryGetProperty("m_kBulletName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
}

public sealed partial class CombatService
{
    private const int MinFlightMs = 120; // LC_Bullet_Helper clamps the client flight time to 0.12 s
    private const int MaxFlightMs = 2500;

    private readonly ConcurrentDictionary<long, SkillVisual> visuals = new();

    /// <summary>The effect plan of one SkillConfig row (cached by row id; rows of a skill share a script show).</summary>
    internal SkillVisual VisualOf(JsonElement row) =>
        GameData.Int(row, "_iScriptShow") <= 0 && !row.TryGetProperty("_kLogicData", out _) ? SkillVisual.Generic
            : visuals.GetOrAdd(GameData.Int(row, "_iID"), _ => SkillVisual.Build(_data, row));

    /// <summary>
    /// SkillConfig._kShowDmg[0] is how many hits the client draws (Fire Bolt level 5 = [5]). The client divides the
    /// damage by SkillHit.split and replays the hit effect at m_fDisplayInterval (LC_DamageHelper._ActionJumpDamage),
    /// and spawns that many projectiles from one SkillFixTimeBullet spaced by m_fBulletHatchInterval.
    /// </summary>
    internal static int HitSplit(JsonElement row)
    {
        if (!row.TryGetProperty("_kShowDmg", out var show) || show.ValueKind != JsonValueKind.Array || show.GetArrayLength() == 0) return 1;
        return (int)Math.Clamp(GameData.Number(show[0]) ?? 1, 1, 20);
    }

    /// <summary>Projectile flight in ms: LC_Bullet_Helper uses m_fBulletLifeTime for time-controlled bullets, distance / m_fBulletSpeed otherwise.</summary>
    internal static int FlightMs(JsonElement bullet, float distance)
    {
        double control = bullet.TryGetProperty("m_iBulletControlType", out var c) ? GameData.Number(c) ?? 0 : 0;
        double seconds;
        if (control == 0)
            seconds = (bullet.TryGetProperty("m_fBulletLifeTime", out var life) ? GameData.Number(life) ?? 0 : 0) / 1000;
        else
        {
            double speed = bullet.TryGetProperty("m_fBulletSpeed", out var v) ? GameData.Number(v) ?? 0 : 0;
            seconds = speed > 0 ? distance / speed : 0;
        }
        return (int)Math.Clamp(seconds * 1000, MinFlightMs, MaxFlightMs);
    }

    /// <summary>
    /// Optional tuning, read on every cast (no restart): &lt;data&gt;/skills/bullet-timing.json
    /// { "sendBullet": true, "extraDelayMs": 0 }. Defaults keep the client's own timing.
    /// </summary>
    private static (bool Send, int ExtraDelay) BulletSettings()
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("Ro3Emu__DataDir") ?? "data";
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "skills", "bullet-timing.json")));
            var root = doc.RootElement;
            bool send = !(root.TryGetProperty("sendBullet", out var s) && s.ValueKind == JsonValueKind.False);
            int extra = root.TryGetProperty("extraDelayMs", out var e) && e.TryGetInt32(out int n) ? Math.Clamp(n, 0, 3000) : 0;
            return (send, extra);
        }
        catch
        {
            return (true, 0);
        }
    }

    /// <summary>
    /// Sends the projectile for a cast and returns the ms the damage should wait so the numbers land with it
    /// (0 when nothing was sent). One notify draws <c>split</c> projectiles; the client spaces them.
    /// A Sag track (Fire Bolt) is dropped onto a point: the client takes SourcePosition only when SourceId is 0.
    /// </summary>
    private async Task<int> SendProjectileAsync(GameSession s, ActiveSkillCast action, SkillVisual visual)
    {
        if (visual.Bullet is not { } bullet || visual.BulletEffect == 0) return 0;
        var settings = BulletSettings();
        if (!settings.Send) return 0;
        var (targetId, to) = ProjectileTarget(s, action);
        if (to == null) return 0;
        var point = new Vec3 { X = to.X, Y = to.Y, Z = to.Z };
        double track = bullet.TryGetProperty("m_eBulletType", out var tr) ? GameData.Number(tr) ?? 0 : 0;
        bool dropped = track == 2;
        int flight = FlightMs(bullet, Distance(s.Position, to));
        int split = HitSplit(action.Row);
        try
        {
            await SendSkillBulletAsync(s, new SkillFixTimeBulletNotify
            {
                SourceId = dropped ? 0 : s.Role!.Id,
                TargetId = targetId,
                EffectId = visual.BulletEffect,
                FixedTime = flight,
                Split = split,
                SkillConfigId = action.Request.SkillId,
                MasterId = s.Role!.Id,
                SourcePosition = dropped ? point : new Vec3 { X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z },
                TargetPosition = point,
                StartTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Projectile for skill {Skill} failed", action.Request.SkillId);
            return 0;
        }
        return flight + settings.ExtraDelay;
    }

    /// <summary>The unit the projectile follows (monster, ally, self) or, for ground casts, the cast point.</summary>
    private (long Id, Vec3? Pos) ProjectileTarget(GameSession s, ActiveSkillCast action)
    {
        long id = action.Request.TargetId;
        if (id != 0)
        {
            if (FindMonster(s.WorldScene, id) is { } monster && monster.Hp > 0) return (monster.Uid, monster.Pos);
            if (id == s.Role!.Id) return (id, s.Position);
            if (FindPlayer(s, id) is { } player) return (id, player.Position);
        }
        return action.Request.TargetPos is { } ground ? (0, ground) : (0, null);
    }

    private int ChantFor(GameSession s, JsonElement row)
    {
        if (!options.Value.ChantSpeedReduction) return ChantOf(row, 0);
        var stats = _character.Stats(s);
        return options.Value.CastAttributes ? ChantOf(row, stats[ChantSpeedAttr], stats[202], stats[204]) : ChantOf(row, stats[ChantSpeedAttr]);
    }

    /// <summary>AttributeConfig 116 ("cspd"). AttributeConversionConfig gives DEX +2 per point, equipment and buffs add to it.</summary>
    private const int ChantSpeedAttr = 116;

    /// <summary>Cast time in ms with no ChantSpeed: <c>_iChantFix + _iChantVar</c> as configured.</summary>
    internal static int ChantOf(JsonElement row) => ChantOf(row, 0);

    /// <summary>
    /// Cast time in ms as the client's skill tooltip computes it (UI_Com_Skill_PopupItemWidgetCtrl): only the variable
    /// part shrinks, by <c>max(0, 1 - sqrt(ChantSpeed / 600))</c>; the fixed part is untouched. With ChantSpeed 0 the
    /// result is the configured value, which is what the CBT video shows for Fire Bolt Lv.10 ("CT 1.0s (0.0s+1.0s)").
    /// </summary>
    internal static int ChantOf(JsonElement row, long chantSpeed) => ChantOf(row, chantSpeed, 0, 0);

    /// <summary>
    /// The skill tooltip's cast time (UI_Com_Skill_PopupItemWidgetCtrl): variable = (var) * max(0, 1 - sqrt(ChantSpeed / 600) + Variable CT% / 10000),
    /// fixed = (fix) * (1 + Fixed CT% / 10000); the percent attributes are 202 and 204 (a negative value shortens the cast).
    /// </summary>
    internal static int ChantOf(JsonElement row, long chantSpeed, long variablePercent, long fixedPercent)
    {
        double fix = Math.Max(0, GameData.Int(row, "_iChantFix"));
        double variable = Math.Max(0, GameData.Int(row, "_iChantVar"));
        double variableFactor = Math.Max(0, 1 - Math.Sqrt(Math.Max(0, chantSpeed) / 600.0) + variablePercent / 10000.0);
        double fixedFactor = Math.Max(0, 1 + fixedPercent / 10000.0);
        return (int)Math.Round(fix * fixedFactor + variable * variableFactor);
    }

    /// <summary>
    /// The player's own damage-bonus attributes (cards, gear, buffs) on a hit it deals to a monster. The damage formula only knows
    /// ATK/MATK, so without this a card with "Magic Damage Bonus%" (attribute 213) changed nothing. (inferred) The bonuses of
    /// the hit's kind add up (all in 1/10000): Damage Increase% 215, Physical 211 or Magic 213, Melee/Ranged Physical 227/229 or
    /// Melee/Ranged Magic 250/252 (SkillConfig._iDistanceType 2 = ranged), and Skill Damage Increase% 225 for every skill but the
    /// normal attack; a critical hit also scales by (15000 + Crit Damage% 209) / 15000.
    /// </summary>
    private long PlayerBonusDamage(GameSession s, Ro3Emu.Server.Maps.MonsterUnit target, JsonElement row, DamageHitResult result, long amount)
    {
        if (!options.Value.DamageBonusAttributes || amount <= 0) return amount;
        var stats = _character.Stats(s);
        return ApplyDamageBonuses(amount, GameData.Int(row, "_iDmgType") == 2, GameData.Int(row, "_iDistanceType") == 2,
            GameData.Int(row, "_iIsNormalAtk") == 1, result == DamageHitResult.Critical, id => stats[id], SideOf(target), HitElement(s, row));
    }

    /// <summary>
    /// Damage a monster deals to the player, after the Damage Reduction% of the player's own attributes (armor, cards, resonance). (inferred)
    /// Reductions add up (all in 1/10000): Damage Reduction% 216, Physical 212 or Magic 214 (by the hit's _iDmgType) and Hunting Protection 232,
    /// capped at 90% so nothing makes a player immune; a critical hit is also cut by (15000 - Crit Damage Reduction% 210) / 15000.
    /// </summary>
    private long ReduceIncomingDamage(GameSession p, long damage, JsonElement? row, DamageHitResult result, StatSheet stats, Ro3Emu.Server.Maps.MonsterUnit? source = null)
    {
        if (!options.Value.DamageBonusAttributes || damage <= 0) return damage;
        bool magic = row is { } r && GameData.Int(r, "_iDmgType") == 2;
        foreach (var negate in IncomingHitNegators) if (negate(p, magic)) return 0;
        bool ranged = row is { } rr && GameData.Int(rr, "_iDistanceType") == 2;
        int element = row is { } er && GameData.Int(er, "_iEleType") is > 1 and var e ? (int)e : source?.Template.Element ?? 1;
        return ApplyDamageReductions(damage, magic, ranged, result == DamageHitResult.Critical, id => stats[id], source is null ? null : SideOf(source), element);
    }

    /// <summary>The element, race, size and boss class of a monster, the keys of the 3xx/4xx/5xx/6xx damage attributes.</summary>
    internal readonly record struct HitSide(int Element, int Race, int Size, bool Boss);

    /// <summary>Boss: NPCConfig._iNPCSubType 3, 4 or 6 (the server's boss rule elsewhere).</summary>
    private HitSide SideOf(Ro3Emu.Server.Maps.MonsterUnit m) => new(m.Template.Element, m.Template.Race, m.Template.Size,
        _data.Row("NPCConfig", m.Template.NpcId) is { } npc && new Ro3Emu.Server.Data.Config.NPCConfig(npc).NPCSubType is 3 or 4 or 6);

    /// <summary>
    /// 301..310 / 311..320: damage dealt / reduction taken by the hit's skill element (neutral, wind, earth, fire, water, poison, holy,
    /// shadow, ghost, undead = ids 1..10); 321..330 / 331..340 by the monster's element; 401..410 / 411..420 by its race (EnumSelectRaceType
    /// bit order: brute, humanoid, demon, plant, undead, formless, fish, angel, insect, dragon); 501..506 by size (small, medium, large:
    /// dealt then taken); 601/602 boss, 603/604 non-boss. All in basis points; the AttributeConfig tips say "Affects your damage against / taken from".
    /// </summary>
    internal static double SideBonus(Func<int, long> attr, HitSide side, int skillElement, bool taken)
    {
        int offset = taken ? 10 : 0;
        double sum = attr(300 + offset + Math.Clamp(skillElement, 1, 10)) + attr(320 + offset + Math.Clamp(side.Element, 1, 10));
        if (side.Race is >= 1 and <= 10) sum += attr(400 + offset + side.Race);
        if (side.Size is >= 1 and <= 3) sum += attr(500 + 2 * side.Size - 1 + (taken ? 1 : 0));
        sum += attr(side.Boss ? 601 + (taken ? 1 : 0) : 603 + (taken ? 1 : 0));
        return sum;
    }

    internal static long ApplyDamageReductions(long damage, bool magic, bool critical, Func<int, long> attr) =>
        ApplyDamageReductions(damage, magic, false, critical, attr, null);

    /// <summary>Also the melee/ranged reductions (228/230 physical, 251/253 magic) and the second "12xx" bucket (1212/1214/1216/1228/1230/1251/1253), capped like the first.</summary>
    internal static long ApplyDamageReductions(long damage, bool magic, bool ranged, bool critical, Func<int, long> attr, HitSide? source = null, int skillElement = 1, bool pvp = false)
    {
        double cut = Math.Min(9000, (source is { } from ? SideBonus(attr, from, skillElement, true) : 0) + attr(216) + attr(pvp ? 234 : 232) + attr(magic ? 214 : 212) + attr(magic ? (ranged ? 253 : 251) : (ranged ? 230 : 228))) / 10000d;
        double poolCut = Math.Min(9000, attr(1216) + attr(magic ? 1214 : 1212) + attr(magic ? (ranged ? 1253 : 1251) : (ranged ? 1230 : 1228))) / 10000d;
        double factor = Math.Max(0, 1 - cut) * Math.Max(0, 1 - poolCut);
        if (critical) factor *= Math.Max(0, (15000 - attr(210)) / 15000d);
        return Math.Max(1, (long)Math.Round(damage * factor));
    }

    /// <summary>Healing Done% 207 of the caster and Healing Taken% 208 of the recipient (inferred: they add up).</summary>
    internal static long ScaleHealing(long amount, long done, long taken, long poolDone = 0, long poolTaken = 0) =>
        (long)Math.Round(amount * Math.Max(0, 1 + (done + taken) / 10000d) * Math.Max(0, 1 + (poolDone + poolTaken) / 10000d));

    /// <summary>Chance of a critical heal: FormulaParamHealCriA * Crit / ParCri (A = 0.5, ParCri 10000), plus the chance extras 200/237 (client LC_PlayerInfoHelper._doFormulaHealsCriticalHits).</summary>
    internal static double HealCritChance(StatSheet stats) => Math.Clamp(0.5 * stats[111] / 10000.0 + (stats[200] + stats[237]) / 10000.0, 0, 1);

    internal static long ApplyDamageBonuses(long amount, bool magic, bool ranged, bool normalAttack, bool critical, Func<int, long> attr, HitSide? target = null, int skillElement = 1, bool pvp = false)
    {
        double bonus = (target is { } to ? SideBonus(attr, to, skillElement, false) : 0) + attr(215) + attr(pvp ? 233 : 231) + (magic ? attr(213) + attr(ranged ? 252 : 250) : attr(211) + attr(ranged ? 229 : 227));
        if (!normalAttack) bonus += attr(225);
        // The "12xx" attributes (1211 = 1000 + 211 ...) are a second, separate bucket: weapons and buffs that say "Damage +X%" use these. (inferred: multiplicative)
        double pool = attr(1215) + (magic ? attr(1213) + attr(ranged ? 1252 : 1250) : attr(1211) + attr(ranged ? 1229 : 1227));
        double factor = Math.Max(0, 1 + bonus / 10000d) * Math.Max(0, 1 + pool / 10000d);
        if (critical) factor *= Math.Max(0, (15000 + attr(209)) / 15000d);
        return (long)Math.Round(amount * factor);
    }
}
