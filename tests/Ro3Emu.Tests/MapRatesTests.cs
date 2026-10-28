using Ro3Emu.Server.Game;
using Xunit;

namespace Ro3Emu.Tests;

public sealed class MapRatesTests
{
    private static bool IsExp(int e) => e is 100 or 101;

    [Fact]
    public void Parse_reads_exp_and_drop_per_map_and_clamps()
    {
        var m = MapRates.Parse("""{ "10110": { "exp": 2, "drop": 1.5 }, "10111": { "drop": 500 }, "x": { "exp": 3 }, "10112": 4 }""");
        Assert.Equal((2.0, 1.5), m[10110]);
        Assert.Equal((1.0, 100.0), m[10111]);   // missing exp = x1, drop clamped to 100
        Assert.False(m.ContainsKey(10112));       // not an object
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void ScaleItems_repeats_stacks_and_leaves_exp_alone()
    {
        var drops = new List<(int Entry, int Count)> { (100, 40), (1001, 1), (1002, 3) };
        var doubled = MapRates.ScaleItems(drops, 2.0, IsExp, () => 0.99);
        Assert.Equal(new[] { (100, 40), (1001, 1), (1001, 1), (1002, 3), (1002, 3) }, doubled);
        var half = MapRates.ScaleItems(drops, 2.5, IsExp, () => 0.4);    // 2 copies + 1 with p=0.5 (0.4 < 0.5)
        Assert.Equal(3, half.Count(d => d.Entry == 1001));
        var none = MapRates.ScaleItems(drops, 2.5, IsExp, () => 0.9);    // 0.9 >= 0.5: only 2 copies
        Assert.Equal(2, none.Count(d => d.Entry == 1001));
    }

    [Fact]
    public void ScaleItems_below_one_keeps_a_stack_with_that_probability()
    {
        var drops = new List<(int Entry, int Count)> { (100, 10), (1001, 1) };
        Assert.Contains((1001, 1), MapRates.ScaleItems(drops, 0.5, IsExp, () => 0.3));
        Assert.DoesNotContain((1001, 1), MapRates.ScaleItems(drops, 0.5, IsExp, () => 0.7));
        Assert.Contains((100, 10), MapRates.ScaleItems(drops, 0.0, IsExp, () => 0.0));  // EXP never removed by the drop rate
        Assert.Same(drops, MapRates.ScaleItems(drops, 1.0, IsExp, () => 0.0));
    }

    [Fact]
    public void Read_missing_file_is_empty_and_for_defaults_to_one()
    {
        Assert.Empty(MapRates.Read(Path.Combine(Path.GetTempPath(), "no-such-map-rates-" + Guid.NewGuid() + ".json")));
    }
}
