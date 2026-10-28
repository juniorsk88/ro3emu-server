using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// (not original behaviour) How much weaker a team boss is when fewer players than the team minimum fight it, so a lone player
/// has a chance. Read without a restart (once per second) from &lt;data&gt;/dungeon-solo.json:
/// <c>{ "hpPercent": 50, "attackPercent": 50 }</c>, each 5 to 100 (100 = unchanged). A missing or invalid file, key or value
/// leaves the boss as the game defines it. With the minimum of N players and n fighting (n &lt; N), the percentage grows
/// linearly from the file's value at n = 1 to 100 at n = N, so a pair meets a boss between the solo and the full-team strength.
/// A party with its minimum but not its maximum (e.g. 3 or 4 of 5) meets a boss Ro3Emu:DungeonMissingMemberPercent weaker per missing member.
/// </summary>
public sealed class DungeonSoloScale(IOptions<ServerOptions> options)
{
    private readonly object gate = new();
    private (int Hp, int Attack) current = (100, 100);
    private long checkedAt = Environment.TickCount64 - 2000; // due at the first call

    /// <summary>Hp and attack percentages for <paramref name="players"/> fighting a boss that needs <paramref name="minPlayers"/>; null = unchanged.</summary>
    public (int Hp, int Attack)? For(int players, int minPlayers, int maxPlayers = 0)
    {
        if (players >= minPlayers && players < maxPlayers && options.Value.DungeonMissingMemberPercent is > 0 and var each)
        {
            int percent = Math.Max(50, 100 - each * (maxPlayers - players));
            return (percent, percent);
        }
        if (players >= minPlayers || minPlayers <= 1 || players < 1) return null;
        var (hp, attack) = Current();
        if (hp >= 100 && attack >= 100) return null;
        int Lerp(int solo) => solo + (100 - solo) * (players - 1) / (minPlayers - 1);
        return (Lerp(hp), Lerp(attack));
    }

    private (int Hp, int Attack) Current()
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (now - checkedAt >= 1000)
            {
                checkedAt = now;
                current = Read(Path.Combine(options.Value.DataDir, "dungeon-solo.json"));
            }
            return current;
        }
    }

    internal static (int Hp, int Attack) Read(string path)
    {
        int hp = 100, attack = 100;
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (Percent(doc.RootElement, "hpPercent") is int h) hp = h;
                if (Percent(doc.RootElement, "attackPercent") is int a) attack = a;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return (hp, attack);
    }

    private static int? Percent(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out double d) && d >= 5 && d <= 100 ? (int)Math.Round(d) : null;
}
