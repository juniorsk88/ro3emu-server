using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>Absorption shields that player skills grant with their first _kBuff1 buff.</summary>
public sealed partial class CombatService
{
    /// <summary>BuffConfig._iBuffID of the shields that only absorb Physical damage (Kyrie Eleison: "This shield absorbs Physical damage ...").</summary>
    internal const int KyrieEleisonGroup = 14117;

    /// <summary>
    /// The shield a skill grants with its first buff, from the skill's description (all inferred as the description words them):
    /// Kyrie Eleison "a Physical damage absorption shield equal to your Magic ATK * ${2}%", Radiant Holy Light "a Shield equal to your MATK * ${4}%",
    /// Harmony: Psalm (S1_Poem) "a Shield equal to MATK * ${3}%", Shield Cast "equal to ${3}% of your Max HP" and High-Energy Impact "${1}% of your Max HP".
    /// </summary>
    internal long SkillShield(string script, JsonElement row, StatSheet stats)
    {
        var desc = new SkillConfig(row).DescData;
        double Part(int index) => index < desc.Length && double.TryParse(desc[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        double amount = script switch
        {
            "Kyrie_Eleison" => stats[104] * Part(1) / 100,
            "ColuseoHeal" => stats[104] * Part(3) / 100,
            "S1_Poem" => stats[104] * Part(2) / 100,
            "ShieldSpell" => stats[101] * Part(2) / 100,
            "AxeLeapAttack" => stats[101] * Part(0) / 100,
            _ => 0,
        };
        return Math.Max(0, (long)Math.Floor(amount));
    }
}
