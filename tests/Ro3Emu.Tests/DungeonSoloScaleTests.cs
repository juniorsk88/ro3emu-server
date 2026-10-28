using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>dungeon-solo.json: how much weaker a team boss is for fewer players than its minimum.</summary>
public sealed class DungeonSoloScaleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-solo-scale-" + Guid.NewGuid().ToString("N"));

    public DungeonSoloScaleTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private DungeonSoloScale Scale(string? json)
    {
        if (json != null) File.WriteAllText(Path.Combine(dir, "dungeon-solo.json"), json);
        return new DungeonSoloScale(Options.Create(new ServerOptions { DataDir = dir }));
    }

    [Fact]
    public void WithoutAFileTheBossIsUnchanged()
    {
        var scale = Scale(null);
        Assert.Null(scale.For(1, 3));
        Assert.Null(scale.For(2, 3));
    }

    [Fact]
    public void ASoloPlayerGetsTheFilePercentAndAPairSomethingInBetween()
    {
        var scale = Scale("""{ "hpPercent": 50, "attackPercent": 40 }""");
        Assert.Equal((50, 40), scale.For(1, 3));
        Assert.Equal((75, 70), scale.For(2, 3));
        Assert.Equal((50, 40), scale.For(1, 5));
        Assert.Equal((87, 85), scale.For(4, 5));
    }

    [Fact]
    public void APartyWithItsMinimumButNotItsMaximumMeetsAWeakerBossPerMissingMember()
    {
        var scale = Scale(null);
        Assert.Null(scale.For(5, 3, 5)); // a full party
        Assert.Equal((90, 90), scale.For(3, 3, 5)); // minimum met, two missing: 5% each
        Assert.Equal((95, 95), scale.For(4, 3, 5));
        Assert.Null(scale.For(3, 3)); // no maximum known: unchanged
        Assert.Null(new DungeonSoloScale(Options.Create(new ServerOptions { DataDir = dir, DungeonMissingMemberPercent = 0 })).For(3, 3, 5));
    }

    [Fact]
    public void AFullTeamOrMoreIsNeverChanged()
    {
        var scale = Scale("""{ "hpPercent": 20, "attackPercent": 20 }""");
        Assert.Null(scale.For(3, 3));
        Assert.Null(scale.For(5, 3));
        Assert.Null(scale.For(1, 1)); // a boss that needs one player
    }

    [Theory]
    [InlineData("""{ "hpPercent": 3, "attackPercent": 50 }""", 100, 50)]   // below 5: ignored
    [InlineData("""{ "hpPercent": 150, "attackPercent": 50 }""", 100, 50)]  // above 100: ignored
    [InlineData("""{ "hpPercent": "x", "attackPercent": 50 }""", 100, 50)]
    [InlineData("not json", 100, 100)]
    [InlineData("""{ "attackPercent": 60 }""", 100, 60)]
    public void InvalidOrMissingKeysKeepTheirDefault(string json, int hp, int attack)
    {
        Assert.Equal((hp, attack), DungeonSoloScale.Read(WriteTemp(json)));
    }

    private string WriteTemp(string json)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
