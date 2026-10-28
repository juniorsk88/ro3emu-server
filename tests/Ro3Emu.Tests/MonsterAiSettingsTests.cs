using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>&lt;data&gt;/monster-ai.json: how field monsters and bosses give up a chase (admin panel, no restart).</summary>
[Collection("MonsterAi")]   // the settings are process-wide; the combat tests that read them run in the same collection
public class MonsterAiSettingsTests : IDisposable
{
    public void Dispose()
    {
        MonsterAiSettings.PinForTest(null);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WithoutAFileFieldMonstersStayWhereTheyGiveUpAndBossesReturnAndHeal()
    {
        var (field, boss) = MonsterAiSettings.Parse("{}");
        Assert.Equal(MonsterAiSettings.FieldDefault, field);
        Assert.Equal(MonsterAiSettings.BossDefault, boss);
        Assert.False(field.GiveUpFromHome); Assert.False(field.ReturnHome); Assert.False(field.HealOnReset);
        Assert.True(field.Pathfinding); Assert.Equal(30, field.GiveUpSeconds);          // walks around walls; gives up after 30 s without combat
        Assert.True(boss.Pathfinding); Assert.Equal(0, boss.GiveUpSeconds);
        Assert.True(boss.GiveUpFromHome); Assert.True(boss.ReturnHome); Assert.True(boss.HealOnReset);
    }

    [Fact]
    public void EachKeyOverridesOnlyItself()
    {
        var (field, boss) = MonsterAiSettings.Parse("""
            { "field": { "giveUp": "home", "leash": 2.5, "returnHome": true, "heal": true, "pathfinding": false, "giveUpSeconds": 12.5 }, "boss": { "returnHome": false } }
            """);
        Assert.Equal(MonsterAiSettings.FieldDefault with { GiveUpFromHome = true, LeashMultiplier = 2.5, ReturnHome = true, HealOnReset = true, Pathfinding = false, GiveUpSeconds = 12.5 }, field);
        Assert.Equal(MonsterAiSettings.BossDefault with { ReturnHome = false }, boss);   // the rest of the boss keeps its defaults
    }

    [Fact]
    public void InvalidValuesKeepTheDefaultsAndTheLeashIsClamped()
    {
        var (field, _) = MonsterAiSettings.Parse("""{ "field": { "giveUp": "sideways", "leash": "far", "returnHome": "yes", "heal": 1 } }""");
        Assert.Equal(MonsterAiSettings.FieldDefault, field);
        Assert.Equal(20, MonsterAiSettings.Parse("""{ "field": { "leash": 9999 } }""").Field.LeashMultiplier);
        Assert.Equal(0.1, MonsterAiSettings.Parse("""{ "field": { "leash": 0 } }""").Field.LeashMultiplier);
        Assert.Equal(3600, MonsterAiSettings.Parse("""{ "field": { "giveUpSeconds": 99999 } }""").Field.GiveUpSeconds);
        Assert.Equal(0, MonsterAiSettings.Parse("""{ "field": { "giveUpSeconds": -5 } }""").Field.GiveUpSeconds);
        Assert.Equal(new MonsterAiProfiles(MonsterAiSettings.FieldDefault, MonsterAiSettings.BossDefault), MonsterAiSettings.Parse("[1,2]"));
    }

    [Fact]
    public void TheFileIsReadAgainWhenItChanges()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3-monai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "monster-ai.json");
        try
        {
            Assert.Equal(MonsterAiSettings.FieldDefault, MonsterAiSettings.Read(path).Field);            // no file
            File.WriteAllText(path, """{ "field": { "returnHome": true } }""");
            Assert.True(MonsterAiSettings.Read(path).Field.ReturnHome);
            File.WriteAllText(path, "not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(MonsterAiSettings.FieldDefault, MonsterAiSettings.Read(path).Field);            // invalid: defaults
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheKillProgressionScaleHasDefaultsAndLimits()
    {
        var (field, boss) = MonsterAiSettings.Parse("{}");
        Assert.True(field.KillGrowth); Assert.False(boss.KillGrowth);                     // on for field monsters, off for bosses and MVPs
        Assert.Equal((50, 500, 20, 500, 500), (field.KillStepPct, field.KillMaxPct, field.KillHealPct, field.KillExpMaxPct, field.KillDropMaxPct));
        var set = MonsterAiSettings.Parse("""
            { "boss": { "killGrowth": true, "killStepPct": 25, "killMaxPct": 300, "killHealPct": 10, "killExpMaxPct": 200, "killDropMaxPct": 100 },
              "field": { "killStepPct": 9999, "killHealPct": 250, "killMaxPct": -3, "killGrowth": "yes" } }
            """);
        Assert.Equal(MonsterAiSettings.BossDefault with { KillGrowth = true, KillStepPct = 25, KillMaxPct = 300, KillHealPct = 10, KillExpMaxPct = 200, KillDropMaxPct = 100 }, set.Boss);
        Assert.Equal((500, 100, 0, true), (set.Field.KillStepPct, set.Field.KillHealPct, set.Field.KillMaxPct, set.Field.KillGrowth));   // clamped; a wrong type keeps the default
    }
}
