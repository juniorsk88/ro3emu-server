using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>Owner-selected Prontera NPC settings. Changes apply after restarting the game server.</summary>
internal sealed class CustomNpcSettings(IOptions<ServerOptions> options)
{
    internal const int HealerNpc = 990031, HunterNpc = 990034;
    internal const int HealerTemplate = 400031, HunterTemplate = 400034, HealerDialogue = 99003101, HealerInteraction = 99003102;
    internal const int HunterDialogue = 99003401, HunterAccept = 99003402, HunterDeliver = 99003403;
    internal const long Prontera = 10100;
    internal sealed record Snapshot(bool Healer, bool Hunter, int BuffLevel, bool Job50);

    internal Snapshot Read()
    {
        var path = Path.Combine(options.Value.DataDir, "custom-npcs.json");
        try
        {
            if (!File.Exists(path)) return new(true, true, 10, false);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            bool Flag(string name, bool fallback) => root.TryGetProperty(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            int level = root.TryGetProperty("buffLevel", out var value) && value.TryGetInt32(out int n) && n is 5 or 10 ? n : 10;
            return new(Flag("healer", true), Flag("hunter", true), level, Flag("job50", false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Invalid owner settings do not enable custom NPCs or extended job levels.
            return new(false, false, 10, false);
        }
    }
}
