using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>How one kind of monster gives up a chase.</summary>
/// <param name="GiveUpFromHome">False: it gives up when the player is too far from the monster itself (so a pulled pack can
/// be led across the map). True: when the player is too far from the monster's spawn point (the leash).</param>
/// <param name="LeashMultiplier">Multiplies the give-up distance of the monster's AIConfig (_iChaseDistance, normally 18 m).</param>
/// <param name="ReturnHome">After giving up, walk back to the spawn point. False: stay where it is and roam there.</param>
/// <param name="HealOnReset">Recover all HP when it gives up (or reaches home, when it returns).</param>
/// <param name="Pathfinding">Walk around obstacles: when the straight line to the target (or home) is blocked, follow a route found on the navigation grid. False: only straight segments, and the monster stops at a wall.</param>
/// <param name="GiveUpSeconds">Give up when nothing happened between the monster and its target (no hit either way) for this long; 0 = never.</param>
/// <param name="KillGrowth">When the monster kills a player it levels up (with an effect), recovers <paramref name="KillHealPct"/> of its HP and stays where it is (no walk home, no full heal); each kill makes it stronger by <paramref name="KillStepPct"/> up to <paramref name="KillMaxPct"/>.</param>
/// <param name="KillStepPct">Percent of stronger (HP, attack, magic attack, defence) each kill adds, so the scale is kills x step until the cap: 50 reaches 500 in ten kills.</param>
/// <param name="KillMaxPct">The cap of the stat bonus (500 = up to six times the original).</param>
/// <param name="KillHealPct">Percent of its (new) max HP the monster recovers on each kill.</param>
/// <param name="KillExpMaxPct">EXP bonus when the stat bonus is at its cap (the bonus follows the same scale: half way, half of it).</param>
/// <param name="KillDropMaxPct">Item drop bonus at the cap, on the same scale.</param>
public sealed record MonsterAiProfile(bool GiveUpFromHome, double LeashMultiplier, bool ReturnHome, bool HealOnReset, bool Pathfinding, double GiveUpSeconds,
    bool KillGrowth, double KillStepPct, double KillMaxPct, double KillHealPct, double KillExpMaxPct, double KillDropMaxPct);

/// <summary>The two profiles: monsters without special skills and bosses/MVPs.</summary>
public sealed record MonsterAiProfiles(MonsterAiProfile Field, MonsterAiProfile Boss);

/// <summary>
/// Chase behaviour of monsters, read without a restart (once per second) from &lt;data&gt;/monster-ai.json, written by the
/// admin panel (IA dos monstros e MVPs):
/// <c>{ "field": { "giveUp": "player", "leash": 1.0, "returnHome": false, "heal": false, "pathfinding": true, "giveUpSeconds": 30,
///                  "killGrowth": true, "killStepPct": 50, "killMaxPct": 500, "killHealPct": 20, "killExpMaxPct": 500, "killDropMaxPct": 500 },
///      "boss":  { "giveUp": "home",   "leash": 1.0, "returnHome": true,  "heal": true,  "pathfinding": true, "giveUpSeconds": 0 } }</c>.
/// "field" is every monster without special skills; "boss" is bosses and MVPs (monsters with special skills). A missing
/// or invalid file, key or value keeps the default of that key.
/// </summary>
public static class MonsterAiSettings
{
    /// <summary>Field monsters are not tied to their spawn point; bosses keep their post, return and heal.</summary>
    public static readonly MonsterAiProfile FieldDefault = new(GiveUpFromHome: false, LeashMultiplier: 1.0, ReturnHome: false, HealOnReset: false, Pathfinding: true, GiveUpSeconds: 30,
        KillGrowth: true, KillStepPct: 50, KillMaxPct: 500, KillHealPct: 20, KillExpMaxPct: 500, KillDropMaxPct: 500);
    public static readonly MonsterAiProfile BossDefault = new(GiveUpFromHome: true, LeashMultiplier: 1.0, ReturnHome: true, HealOnReset: true, Pathfinding: true, GiveUpSeconds: 0,
        KillGrowth: false, KillStepPct: 50, KillMaxPct: 500, KillHealPct: 20, KillExpMaxPct: 500, KillDropMaxPct: 500);

    private static readonly MonsterAiProfiles Defaults = new(FieldDefault, BossDefault);
    private static readonly object Gate = new();
    private static string _path = "";
    private static DateTime _stamp;
    private static volatile MonsterAiProfiles _current = Defaults;
    private static long _checkedAt = long.MinValue;
    private static volatile bool _pinned;

    public static MonsterAiProfile For(bool boss)
    {
        var current = _current;
        if (!_pinned)
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref _checkedAt) >= 1000)
            {
                string dir = Environment.GetEnvironmentVariable("Ro3Emu__DataDir") ?? "data";
                current = Read(Path.Combine(dir, "monster-ai.json"));
                Volatile.Write(ref _checkedAt, now);
            }
        }
        return boss ? current.Boss : current.Field;
    }

    /// <summary>Tests pin the profiles (null releases them): the file is not read while pinned.</summary>
    internal static void PinForTest(MonsterAiProfiles? profiles)
    {
        lock (Gate)
        {
            _pinned = profiles != null;
            _current = profiles ?? Defaults;
            _path = ""; _stamp = default;
            Volatile.Write(ref _checkedAt, long.MinValue);
        }
    }

    internal static MonsterAiProfiles Read(string path)
    {
        lock (Gate)
        {
            try
            {
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (path == _path && stamp == _stamp) return _current;
                _path = path; _stamp = stamp;
                _current = stamp == DateTime.MinValue ? Defaults : Parse(File.ReadAllText(path));
            }
            catch
            {
                _current = Defaults;
            }
            return _current;
        }
    }

    internal static MonsterAiProfiles Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return Defaults;
        return new MonsterAiProfiles(Profile(doc.RootElement, "field", FieldDefault), Profile(doc.RootElement, "boss", BossDefault));
    }

    private static MonsterAiProfile Profile(JsonElement root, string name, MonsterAiProfile fallback)
    {
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return fallback;
        bool fromHome = p.TryGetProperty("giveUp", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString() switch { "home" => true, "player" => false, _ => fallback.GiveUpFromHome } : fallback.GiveUpFromHome;
        double leash = p.TryGetProperty("leash", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetDouble(out double d) && double.IsFinite(d)
            ? Math.Clamp(d, 0.1, 20) : fallback.LeashMultiplier;
        double seconds = p.TryGetProperty("giveUpSeconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out double sec) && double.IsFinite(sec)
            ? Math.Clamp(sec, 0, 3600) : fallback.GiveUpSeconds;
        return new MonsterAiProfile(fromHome, leash, Flag(p, "returnHome", fallback.ReturnHome), Flag(p, "heal", fallback.HealOnReset),
            Flag(p, "pathfinding", fallback.Pathfinding), seconds,
            Flag(p, "killGrowth", fallback.KillGrowth), Pct(p, "killStepPct", fallback.KillStepPct, 500), Pct(p, "killMaxPct", fallback.KillMaxPct, 5000),
            Pct(p, "killHealPct", fallback.KillHealPct, 100), Pct(p, "killExpMaxPct", fallback.KillExpMaxPct, 5000), Pct(p, "killDropMaxPct", fallback.KillDropMaxPct, 5000));
    }

    private static double Pct(JsonElement p, string name, double fallback, double max) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? Math.Clamp(d, 0, max) : fallback;

    private static bool Flag(JsonElement p, string name, bool fallback) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
