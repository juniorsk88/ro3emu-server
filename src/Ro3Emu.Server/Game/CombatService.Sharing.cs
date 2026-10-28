using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Damage that a Crusader takes in place of an ally: Devotion ("you take ${2}% of the damage they receive. If the Distance between you exceeds ${4}
/// meters during this time, the effect ends") and King's Grace ("redirecting ${1}% of the DMG taken by up to ${2} allies within a ${7}-meter radius to
/// you ... taking DMG restores HP equal to ${4}% of the DMG taken"; "Damage Sharing does not affect allied targets under 【Devotion】").
/// The share is cut from the ally's hit before its shield; the Crusader takes it as a separate hit that leaves at least 1 HP. (inferred: the
/// original's order against shields, resistances and lethal checks is not recoverable)
/// </summary>
public sealed partial class CombatService
{
    private const int DevotionBuffGroup = 11504, DevotionSkill = 11413, KingsGraceBuffGroup = 11614, KingsGraceSkill = 11612;

    /// <summary>Takes the shares of <paramref name="damage"/> that Crusaders carry for <paramref name="ally"/> and returns what is left for the ally.
    /// The Crusaders' own hits are posted to the game loop.</summary>
    internal long ShareDamage(GameSession ally, long damage)
    {
        if (damage <= 0 || ally.Role == null) return damage;
        long now = Now, left = damage;
        bool devoted = false;
        foreach (var buff in ally.Buffs.Where(b => b.ExpireMs > now && b.ConfigId / 100 == DevotionBuffGroup).ToArray())
        {
            if (FindCrusader(ally, buff.SourceId) is not { } crusader || SkillConfig.Get(_data, DevotionSkill * 100L + buff.ConfigId % 100) is not { } skill
                || Distance(crusader.Position, ally.Position) > Math.Max(1, Part(skill.DescData, 3))) continue;
            long moved = (long)Math.Floor(damage * Math.Max(0, Part(skill.DescData, 1)) / 100);
            if (moved <= 0) continue;
            left -= Math.Min(left, moved);
            devoted = true;
            PostShare(crusader, ally, moved, 0);
        }
        if (devoted) return left;
        foreach (var other in _world.PlayersIn(ally.WorldScene).Where(p => p != ally && !p.Dead && p.Role != null).ToArray())
        {
            foreach (var buff in other.Buffs.Where(b => b.ExpireMs > now && b.ConfigId / 100 == KingsGraceBuffGroup && b.SourceId == other.Role!.Id))
            {
                if (SkillConfig.Get(_data, KingsGraceSkill * 100L + buff.ConfigId % 100) is not { } skill
                    || Distance(other.Position, ally.Position) > Math.Max(1, Part(skill.DescData, 6))) continue;
                long moved = (long)Math.Floor(damage * Math.Max(0, Part(skill.DescData, 0)) / 100);
                if (moved <= 0) continue;
                left -= Math.Min(left, moved);
                PostShare(other, ally, moved, (long)Math.Floor(moved * Math.Max(0, Part(skill.DescData, 3)) / 100));
            }
        }
        return left;
    }

    private GameSession? FindCrusader(GameSession ally, long roleId) =>
        _sessions.All().FirstOrDefault(s => s.Role?.Id == roleId && s != ally && !s.Dead && !s.SceneLoading && s.WorldScene == ally.WorldScene);

    private static double Part(string[] desc, int index) =>
        index < desc.Length && double.TryParse(desc[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

    private void PostShare(GameSession crusader, GameSession ally, long amount, long heal) =>
        _loop.PostRuntime(() => ApplyShareAsync(crusader, ally, amount, heal), "damage sharing");

    private async Task ApplyShareAsync(GameSession crusader, GameSession ally, long amount, long heal)
    {
        if (crusader.Role == null || ally.Role == null || crusader.Dead || crusader.SceneLoading) return;
        long current = _character.Stats(crusader)[2000];
        long to = Math.Clamp(current - amount + heal, 1, _character.Stats(crusader)[101]);
        if (to == current) return;
        await _character.SetHpSpAsync(crusader, to, null);
        if (amount <= 0) return;
        var hit = new SkillHitInfo
        {
            SrcID = ally.Role.Id, SessionId = ++_sessionIds, SrcElement = 1, Split = 1,
            Dsts = { new DamageInfo { DstID = crusader.Role.Id, Damage = amount, DstElement = 1 } },
        };
        foreach (var viewer in _world.Viewers(crusader).Append(crusader).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
    }
}
