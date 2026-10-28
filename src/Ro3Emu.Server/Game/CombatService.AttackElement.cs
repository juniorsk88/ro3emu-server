using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>The words the client's own text uses for an arrow that changes the attack element (ItemConfig 1002012: "Equipping Fire Arrow
    /// with a bow changes your attack property to Fire.").</summary>
    private const string ArrowPropertyText = "attack property";

    /// <summary>
    /// The element a player's attack carries on its own: a skill the data marks as shot from the equipped arrow (SkillConfig._iAffectByArrow,
    /// the bow skills) takes the arrow's element, ItemConfig._kParam1 being the EleConfig element id (Fire Arrow 4, Crystal Arrow 5 water,
    /// Stone Arrow 3 earth, Silver Arrow 7 holy); every other attack is neutral (1). (inferred) The arrow must still be in the bag, and only an
    /// arrow whose text says it changes the attack property counts: Steel Arrow's _kParam1 is 6, yet its text names no property.
    /// </summary>
    internal int AttackElement(GameSession s, JsonElement row)
    {
        if (new SkillConfig(row).AffectByArrow == 0) return 1;
        long arrow = s.BagState.Info.CurrentArrowID;
        if (arrow <= 0 || !s.Items.Any(i => i.Grid == 0 && i.Entry == arrow && i.Stack > 0)) return 1;
        return _data.Row("ItemConfig", arrow) is { } item
            ? ArrowElement(_data.TextOf(item, "_iDescription"), new ItemConfig(item).Param1) : 1;
    }

    /// <summary>The element an arrow gives (its text and _kParam1), 1 when its text names no change of the attack property.</summary>
    internal static int ArrowElement(string description, string param1) =>
        description.Contains(ArrowPropertyText, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(param1, out int element) && element is > 1 and <= 10 ? element : 1;

    /// <summary>The element of one hit: the skill's own (_iEleType above neutral), else the attacker's (<see cref="AttackElement"/>).</summary>
    internal int HitElement(GameSession s, JsonElement row) =>
        GameData.Int(row, "_iEleType") is > 1 and var own ? (int)own : AttackElement(s, row);
}
