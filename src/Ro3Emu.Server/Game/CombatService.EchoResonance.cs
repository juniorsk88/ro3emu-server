using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // SkillConfig descriptions specify these additions; corresponding enhanced rows confirm most of them.
    // Other effects of Pixel Thrust and Mixed Accompaniment still need their own triggers.
    private static readonly IReadOnlyDictionary<int, (int[] Groups, int DescriptionIndex)> EchoDamageSkills =
        new Dictionary<int, (int[], int)>
        {
            [11130101] = ([11301], 0), // Dragon's Descent: Rift Impact +50
            [11130501] = ([11308], 0), // Pixel Thrust: Phantom Thrust +150 percentage points
            [11130701] = ([11310], 0), // Electro Rock: all forms of Rain of Spears +25
            [11230701] = ([12316], 0), // Mixed Accompaniment: each Tetra Vortex hit +50
            [11230801] = ([12315], 0), // Disco Accent: Comet +200
            [11164101] = ([11628], 1), // Moonlit Echo: Crescent Moon +10 per hit
            [11164201] = ([11607], 0), // Radiant Bloom: Twilight +60
            [11164301] = ([11604], 0), // Golden Moon Ring: Blood Cross +4
            [11430801] = ([14303], 0), // Chorus: Radiant Holy Light healing coefficient +600
            [11531901] = ([15303], 2), // Thomas Flare: Rolling Cutter +10
            [11531801] = ([15302], 0), // Graffiti Mastery: Cross Ripper Slasher +10
            [11533101] = ([15320, 15322, 15323], 1), // Freestyle Rap: three shadow skills +15
            [11336001] = ([13305], 0), // Rhythmic Arrow: Focused Arrow Strike +3
        };
    private static readonly int[] EchoAreaGroups = [15303, 15320, 15322, 15323];

    private IReadOnlyList<SkillParameterRules.Modifier> EchoSkillModifiers(GameSession session)
    {
        if (_character.MatchAttributes?.Invoke(session) != null) return [];
        var result = new List<SkillParameterRules.Modifier>();
        foreach (int id in EmblemService.ResonanceSkills(session, _data))
            if (EchoDamageSkills.TryGetValue(id, out var mapping) && SkillConfig.Get(_data, id) is { } skill
                && skill.DescData.Length > mapping.DescriptionIndex
                && long.TryParse(skill.DescData[mapping.DescriptionIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out long percent))
            {
                if (id == 11531801 && SkillConfig.Get(_data, 1530201) is { } normal
                    && SkillConfig.Get(_data, 11530211) is { } enhanced
                    && normal.DamageParam1.Length == enhanced.DamageParam1.Length
                    && normal.DamageParam1.Length > 0 && enhanced.DamageParam1[0] - normal.DamageParam1[0] == percent * 100)
                {
                    for (int i = 0; i < normal.DamageParam1.Length; i++)
                        result.Add(new SkillParameterRules.Modifier(mapping.Groups[0], 29, 1, enhanced.DamageParam1[i] - normal.DamageParam1[i], i));
                }
                else if (id != 11531801)
                    foreach (int group in mapping.Groups)
                        result.Add(new SkillParameterRules.Modifier(group, 29, 1, percent * 100, group == 13305 ? 0 : -1));
                if (id == 11164101 && long.TryParse(skill.DescData[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
                    result.Add(new SkillParameterRules.Modifier(11628, 33, 2, seconds * 1000, 0, Column: 2));
            }
        return result;
    }

    // The Rift Impact season variant adds this Stun tuple to the original hostile buffs.
    private JsonElement EchoRiftStun(JsonElement row, int level)
    {
        if (!ConfiguredModeRows.TryGetValue((11301, level, 1), out int enhancedId)
            || SkillConfig.Get(_data, enhancedId) is not { } enhanced) return row;
        var stun = enhanced.Buff2.FirstOrDefault(tuple => tuple.Length >= 3 && tuple[0] == 901601);
        var skill = new SkillConfig(row);
        var buffs = skill.Buff2;
        if (stun == null || buffs.Any(tuple => tuple.Length > 0 && tuple[0] == stun[0])) return row;
        return skill.WithBuff2([.. buffs, stun]);
    }

    // The season variant supplies the actual area vector used by the client's skill indicator.
    private JsonElement EchoArea(JsonElement row, int group, int level)
    {
        if (!EchoAreaGroups.Contains(group) || !ConfiguredModeRows.TryGetValue((group, level, 1), out int enhancedId)
            || SkillConfig.Get(_data, enhancedId) is not { } enhanced || enhanced.Range.Length == 0) return row;
        var skill = new SkillConfig(row);
        var range = skill.Range;
        if (range.Length == 0) return row;
        range[0] = enhanced.Range[0];
        return skill.WithRange(range);
    }

    private IReadOnlyList<SkillParameterRules.Modifier> EchoBloodCrossSecondForm(JsonElement row, int level)
    {
        if (!ConfiguredModeRows.TryGetValue((11604, level, 5), out int enhancedId)
            || SkillConfig.Get(_data, enhancedId) is not { } enhanced) return [];
        var normal = new SkillConfig(row).DamageParam1;
        if (normal.Length == 0 || enhanced.DamageParam1.Length == 0) return [];
        return [new SkillParameterRules.Modifier(11604, 29, 1, enhanced.DamageParam1[0] - normal[0], 0)];
    }
}
