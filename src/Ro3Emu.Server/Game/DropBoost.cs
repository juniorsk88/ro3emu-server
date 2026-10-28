using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// "Drop melhorado" of the admin panel: lifts the chance of the items a monster rarely drops, in proportion to what it was, so that
/// the rarest item of the game's data ends at <see cref="Settings.MinPct"/> (0.01 % by default). An item whose chance is
/// <see cref="Settings.ThresholdPct"/> (0.01 % by default, the same as the minimum: only what is below it is lifted, up to the minimum) or
/// more is not touched, and every chance, boosted or not, still follows the server's drop rate (Ajustes → Taxas): the rate multiplies the
/// result (a 0.1 % item at x2 is 0.2 %). Read without a restart (once per second) from
/// &lt;data&gt;/drop-boost.json, written by the panel:
/// <c>{ "enabled": true, "minPct": 0.01, "thresholdPct": 0.01, "capPct": 0, "rate": 5 }</c>. A missing or invalid file, or "enabled": false, leaves every
/// chance as the data has it. "rate" is the drop rate the panel applied to the tables (the server only sees the multiplied chances, so the
/// panel tells it the multiplier); a missing "rate" is x1. "capPct" is the ceiling the panel put on the rate (Ajustes → Taxas, "Teto da
/// taxa de drop"; 0 = none): the rate never takes a drop slot above it, one already above it keeps its chance, and below it the rate
/// saturates smoothly (see <see cref="Settings.Rated"/>). The server needs it only to undo the rate when it works out the original chance.
/// <para>
/// Everything is computed on the chance <b>before</b> the rate, <c>p</c> = the item's chance per kill in the original data. With T = max
/// (threshold, minimum): a chance of T or more stays as it is. Below T the curve is a power law through two fixed points: a chance of
/// <see cref="Floor0"/> (the lowest one in the exported data, 0.00001 %) becomes <c>minPct</c>, and a chance of T stays itself. In
/// between, p becomes <c>T * (p / T)^e</c> with <c>e = ln(minPct / T) / ln(Floor0 / T)</c>: a rarer item stays rarer than a commoner one,
/// the boost is large for the rarest items and fades to nothing at T (with T = 1 % and minPct = 0.01 %: 0.001 % becomes about 0.06 %,
/// 0.01 % about 0.16 %, 0.1 % about 0.4 %). With T equal to the minimum (the default) e is 0: every chance below the minimum becomes the
/// minimum, a flat floor. The server's rate then multiplies the result. The result is never below the chance the server already gave.
/// </para>
/// <para>
/// The chance of an item is its total for the kill (all the pool slots of the monster that can give it to this killer). It is applied as
/// one extra independent roll per item and kill (see <see cref="RewardService"/>) that lifts the item from <c>p</c> to <c>p'</c>:
/// it succeeds with <c>(p' - p) / (1 - p)</c>, so the two together land on <c>p'</c> exactly.
/// </para>
/// </summary>
public static class DropBoost
{
    /// <summary>Lowest chance (fraction) of the game's data: the item that ends at minPct.</summary>
    public const double Floor0 = 1e-7;

    public const double DefaultMinPct = 0.01, MinMinPct = 0.001, MaxMinPct = 0.5;
    public const double DefaultThresholdPct = 0.01, MinThresholdPct = 0.002, MaxThresholdPct = 100;
    public const double MinRate = 0.1, MaxRate = 100;

    /// <param name="Enabled">The switch of the panel.</param>
    /// <param name="MinPct">Percent per kill the rarest item ends at (before the server's rate).</param>
    /// <param name="ThresholdPct">Chances of this percent or more are not touched.</param>
    /// <param name="Rate">The server's drop multiplier already in the tables' chances.</param>
    /// <param name="CapPct">The panel's ceiling for the rate, in percent; 0 = none.</param>
    public sealed record Settings(bool Enabled, double MinPct, double ThresholdPct, double Rate, double CapPct = 0)
    {
        public static readonly Settings Off = new(false, DefaultMinPct, DefaultThresholdPct, 1);
        public double Floor => MinPct / 100.0;
        public double Threshold => ThresholdPct / 100.0;
        public double Cap => CapPct / 100.0;

        /// <summary>
        /// The chance a drop slot (or an item) ends at in the tables when its original chance is <paramref name="x"/> (a fraction): the rate
        /// multiplies it, never above 100 %. With a ceiling and a rate above 1 (what the panel's "Aplicar" writes): a chance already at or
        /// above the ceiling is left alone, and below it the rate saturates, <c>cap * (1 - (1 - x / cap)^rate)</c> (about rate * x for a small x,
        /// and it only reaches the ceiling asymptotically, so the order of the items is kept).
        /// </summary>
        public double Rated(double x)
        {
            if (!(x > 0)) return 0;
            if (Rate <= 1 || !(Cap > 0)) return Math.Min(1, x * Rate);
            return x >= Cap ? x : Cap * (1 - Math.Pow(1 - x / Cap, Rate));
        }

        /// <summary>The original chance of a slot whose chance in the tables is <paramref name="tuned"/>: the inverse of <see cref="Rated"/>.</summary>
        public double Original(double tuned)
        {
            if (!(tuned > 0)) return 0;
            if (Rate <= 1 || !(Cap > 0)) return Math.Min(1, tuned / Rate);
            return tuned >= Cap ? tuned : Cap * (1 - Math.Pow(1 - tuned / Cap, 1 / Rate));
        }
        /// <summary>The switch is on (and there is a minimum to lift to).</summary>
        public bool Active => Enabled && Floor > 0;

        /// <summary>
        /// The chance an item ends at when its original chance per kill is <paramref name="original"/> (a fraction): the boost is worked out on
        /// the original chance and the rate (and its ceiling) is applied on top, <see cref="Rated"/>. The caller never lowers a chance, so what
        /// the tables already give is the floor of the result.
        /// </summary>
        public double Target(double original)
        {
            if (!Active || !(original > 0)) return Rated(original);
            return Rated(Boost(original, Floor, Threshold));
        }
    }

    private static readonly object Gate = new();
    private static string _path = "";
    private static DateTime _stamp;
    private static volatile Settings _current = Settings.Off;
    private static long _checkedAt = long.MinValue;
    private static volatile bool _pinned;

    /// <summary>The current settings; the file is looked at once per second, not per kill.</summary>
    public static Settings Current()
    {
        var current = _current;
        if (_pinned) return current;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _checkedAt) >= 1000)
        {
            string dir = Environment.GetEnvironmentVariable("Ro3Emu__DataDir") ?? "data";
            current = Read(Path.Combine(dir, "drop-boost.json"));
            Volatile.Write(ref _checkedAt, now);
        }
        return current;
    }

    /// <summary>Tests pin the settings (null releases them): the file is not read while pinned.</summary>
    internal static void PinForTest(Settings? settings)
    {
        lock (Gate)
        {
            _pinned = settings != null;
            _current = settings ?? Settings.Off;
            _path = ""; _stamp = default;
            Volatile.Write(ref _checkedAt, long.MinValue);
        }
    }

    internal static Settings Read(string path)
    {
        lock (Gate)
        {
            try
            {
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (path == _path && stamp == _stamp) return _current;
                _path = path; _stamp = stamp;
                _current = stamp == DateTime.MinValue ? Settings.Off : Parse(File.ReadAllText(path));
            }
            catch
            {
                _current = Settings.Off;
            }
            return _current;
        }
    }

    internal static Settings Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Settings.Off;
        bool enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        return new Settings(enabled,
            Number(root, "minPct", DefaultMinPct, MinMinPct, MaxMinPct),
            Number(root, "thresholdPct", DefaultThresholdPct, MinThresholdPct, MaxThresholdPct),
            Number(root, "rate", 1, MinRate, MaxRate),
            Number(root, "capPct", 0, 0, 100));
    }

    private static double Number(JsonElement root, string name, double fallback, double min, double max) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)
            ? Math.Clamp(d, min, max) : fallback;

    /// <summary>
    /// The boosted chance of an item whose original chance per kill is <paramref name="p"/> (a fraction); <paramref name="floor"/> is the
    /// fraction the rarest item ends at and <paramref name="threshold"/> the chance from which nothing changes (never below the floor: a
    /// threshold under the floor counts as the floor). 0 stays 0; a chance of the threshold or more is returned as it is; the result is
    /// never below <paramref name="p"/>.
    /// </summary>
    public static double Boost(double p, double floor, double threshold)
    {
        if (!(p > 0) || !(floor > 0)) return p;
        double t = Math.Max(threshold, floor);
        if (p >= t) return p;
        double exponent = Math.Log(floor / t) / Math.Log(Floor0 / t); // 0 when the threshold is the floor: a flat floor
        double boosted = t * Math.Pow(p / t, exponent);
        // below the reference chance the curve would fall under the floor: the rarest items still end at the floor
        return Math.Clamp(Math.Max(boosted, Math.Max(floor, p)), p, t);
    }

    /// <summary>Probability that at least one pick of the item happens in a slot: <c>rolls</c> rolls of <c>chance</c>, each with <c>picks</c> picks at <c>share</c>.</summary>
    public static double SlotChance(double chance, int rolls, int picks, double share)
    {
        chance = Math.Clamp(chance, 0, 1);
        share = Math.Clamp(share, 0, 1);
        double perRoll = chance * (1 - Math.Pow(1 - share, Math.Max(1, picks)));
        return 1 - Math.Pow(1 - perRoll, Math.Max(1, rolls));
    }

    /// <summary>The success probability of the extra roll that lifts a chance from <paramref name="p"/> to <paramref name="boosted"/>.</summary>
    public static double ExtraRoll(double p, double boosted) => p >= 1 || boosted <= p ? 0 : (boosted - p) / (1 - p);
}
