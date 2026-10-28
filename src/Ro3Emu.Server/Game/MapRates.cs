using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Optional per-map reward multipliers, read on every kill (no restart) from &lt;data&gt;/map-rates.json:
/// <c>{ "10110": { "exp": 2.0, "drop": 1.5 } }</c> keyed by the map (scene entry). Absent maps and a missing/invalid file are x1.
/// EXP scales the kill's Base/Job EXP; drop scales the expected number of item drops of a kill: a rate of 2.5 repeats every
/// dropped item stack twice and once more with 50 % probability, a rate below 1 keeps each stack with that probability.
/// </summary>
public static class MapRates
{
    private static readonly object Gate = new();
    private static string _path = "";
    private static DateTime _stamp;
    private static volatile Dictionary<long, (double Exp, double Drop)> _rates = new();

    private static long _checkedAt = long.MinValue;

    /// <summary>The file is looked at once per second, not per kill (a stat per kill would run under a lock).</summary>
    public static (double Exp, double Drop) For(long scene)
    {
        var rates = _rates;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _checkedAt) >= 1000)
        {
            string dir = Environment.GetEnvironmentVariable("Ro3Emu__DataDir") ?? "data";
            rates = Read(Path.Combine(dir, "map-rates.json"));
            Volatile.Write(ref _checkedAt, now);
        }
        return rates.TryGetValue(scene, out var r) ? r : (1.0, 1.0);
    }

    internal static Dictionary<long, (double Exp, double Drop)> Read(string path)
    {
        lock (Gate)
        {
            try
            {
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (path == _path && stamp == _stamp) return _rates;
                _path = path; _stamp = stamp;
                _rates = stamp == DateTime.MinValue ? new() : Parse(File.ReadAllText(path));
            }
            catch
            {
                _rates = new();
            }
            return _rates;
        }
    }

    internal static Dictionary<long, (double Exp, double Drop)> Parse(string json)
    {
        var map = new Dictionary<long, (double, double)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!long.TryParse(p.Name, out long scene) || p.Value.ValueKind != JsonValueKind.Object) continue;
            map[scene] = (Factor(p.Value, "exp"), Factor(p.Value, "drop"));
        }
        return map;
    }

    private static double Factor(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)
            ? Math.Clamp(d, 0, 100) : 1.0;

    /// <summary>Scales item stacks of a kill (EXP entries pass through); <paramref name="next"/> is a uniform [0,1) source.</summary>
    public static List<(int Entry, int Count)> ScaleItems(List<(int Entry, int Count)> drops, double rate, Func<int, bool> isExp, Func<double> next)
    {
        if (rate == 1.0) return drops;
        var result = new List<(int, int)>(drops.Count);
        foreach (var d in drops)
        {
            if (isExp(d.Entry)) { result.Add(d); continue; }
            int copies = (int)Math.Floor(rate);
            if (next() < rate - copies) copies++;
            for (int i = 0; i < copies; i++) result.Add(d);
        }
        return result;
    }
}
