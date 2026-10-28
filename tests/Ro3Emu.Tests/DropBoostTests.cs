using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The panel's "Drop melhorado" (DropBoost): the curve, the settings file, the server's drop rate and the extra roll of a monster kill.</summary>
public sealed class DropBoostTests
{
    private const double Floor = 0.0001, Threshold = 0.01; // 0.01 % minimum, 1 % threshold

    [Fact]
    public void Parse_reads_the_switch_the_numbers_and_clamps_them()
    {
        Assert.Equal(new DropBoost.Settings(true, 0.01, 0.01, 1), DropBoost.Parse("""{ "enabled": true, "minPct": 0.01 }"""));
        Assert.Equal(new DropBoost.Settings(true, 0.02, 2, 5), DropBoost.Parse("""{ "enabled": true, "minPct": 0.02, "thresholdPct": 2, "rate": 5 }"""));
        Assert.Equal(new DropBoost.Settings(false, 0.05, 0.01, 1), DropBoost.Parse("""{ "enabled": false, "minPct": 0.05 }"""));
        Assert.Equal(DropBoost.MinMinPct, DropBoost.Parse("""{ "enabled": true, "minPct": 0 }""").MinPct);
        Assert.Equal(0.5, DropBoost.MaxMinPct);
        Assert.Equal(DropBoost.MaxMinPct, DropBoost.Parse("""{ "enabled": true, "minPct": 900 }""").MinPct);
        Assert.Equal(DropBoost.MaxThresholdPct, DropBoost.Parse("""{ "enabled": true, "thresholdPct": 5000 }""").ThresholdPct);
        Assert.Equal(DropBoost.MinRate, DropBoost.Parse("""{ "enabled": true, "rate": 0 }""").Rate);
        Assert.Equal(DropBoost.MaxRate, DropBoost.Parse("""{ "enabled": true, "rate": 1e9 }""").Rate);
        Assert.Equal(25, DropBoost.Parse("""{ "enabled": true, "capPct": 25 }""").CapPct);
        Assert.Equal(0, DropBoost.Parse("""{ "enabled": true }""").CapPct);
        Assert.Equal(100, DropBoost.Parse("""{ "enabled": true, "capPct": 150 }""").CapPct);
        Assert.Equal(0, DropBoost.Parse("""{ "enabled": true, "capPct": -3 }""").CapPct);
        Assert.False(DropBoost.Parse("""{ "enabled": "yes" }""").Enabled);
        Assert.False(DropBoost.Parse("[1]").Enabled);
        Assert.Equal(0.0001, DropBoost.Parse("""{ "enabled": true, "minPct": 0.01 }""").Floor, 12);
        Assert.Equal(0.0001, DropBoost.Parse("""{ "enabled": true }""").Threshold, 12);   // the default threshold is the minimum: a flat floor
    }

    [Fact]
    public void The_switch_needs_a_minimum_and_a_threshold_at_or_below_it_gives_a_flat_floor()
    {
        Assert.True(DropBoost.Parse("""{ "enabled": true, "minPct": 0.01 }""").Active);
        Assert.False(DropBoost.Parse("""{ "enabled": false }""").Active);
        var off = DropBoost.Parse("""{ "enabled": false }""");
        Assert.Equal(0.003, off.Target(0.003));
        // threshold = minimum (the default): everything below 0.01 % becomes 0.01 %, everything from 0.01 % up stays
        Assert.Equal(1e-4, DropBoost.Boost(1e-6, 1e-4, 1e-4), 15);
        Assert.Equal(1e-4, DropBoost.Boost(9.9e-5, 1e-4, 1e-4), 15);
        Assert.Equal(1e-4, DropBoost.Boost(1e-4, 1e-4, 1e-4), 15);
        Assert.Equal(2e-4, DropBoost.Boost(2e-4, 1e-4, 1e-4), 15);
        Assert.Equal(0.3, DropBoost.Boost(0.3, 1e-4, 1e-4));
        // a threshold under the minimum counts as the minimum
        Assert.Equal(1e-4, DropBoost.Boost(5e-5, 1e-4, 1e-5), 15);
        Assert.Equal(2e-4, DropBoost.Boost(2e-4, 1e-4, 1e-5), 15);
    }

    [Fact]
    public void Read_missing_or_broken_file_is_off()
    {
        Assert.False(DropBoost.Read(Path.Combine(Path.GetTempPath(), "no-such-drop-boost-" + Guid.NewGuid() + ".json")).Enabled);
        string path = Path.Combine(Path.GetTempPath(), "drop-boost-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.False(DropBoost.Read(path).Enabled);
            File.WriteAllText(path, """{ "enabled": true, "minPct": 0.02, "rate": 3 }""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // a new stamp: the file is read again
            Assert.Equal(new DropBoost.Settings(true, 0.02, 0.01, 3), DropBoost.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_rarest_chance_ends_at_the_minimum_and_the_threshold_or_more_is_not_touched()
    {
        Assert.Equal(Floor, DropBoost.Boost(DropBoost.Floor0, Floor, Threshold), 12);
        Assert.Equal(Floor, DropBoost.Boost(1e-9, Floor, Threshold), 12);   // below the reference: still the minimum
        Assert.Equal(0.0, DropBoost.Boost(0.0, Floor, Threshold));
        Assert.Equal(0.3, DropBoost.Boost(0.3, 0.0, Threshold));              // no minimum: unchanged
        foreach (double p in new[] { 0.01, 0.0100001, 0.05, 0.1, 0.5, 0.9, 1.0 })
            Assert.Equal(p, DropBoost.Boost(p, Floor, Threshold));
        Assert.True(DropBoost.Boost(0.0099999, Floor, Threshold) <= Threshold);
        // a lower threshold (0.05 %) moves the point where nothing changes
        Assert.Equal(0.0006, DropBoost.Boost(0.0006, Floor, 0.0005));
        Assert.True(DropBoost.Boost(0.0003, Floor, 0.0005) > 0.0003);
    }

    [Fact]
    public void The_boost_is_proportional_never_lower_and_keeps_the_order()
    {
        double previous = 0;
        for (double p = 1e-9; p < 0.02; p *= 1.37)
        {
            double boosted = DropBoost.Boost(p, Floor, Threshold);
            Assert.True(boosted >= p, $"p={p}");
            Assert.True(boosted >= previous, $"order broken at p={p}");
            Assert.True(p >= Threshold ? boosted == p : boosted <= Threshold);
            previous = boosted;
        }
        // The values the panel shows to the user (0.01 % minimum, 1 % threshold): the boost fades to nothing at 1 %.
        Assert.InRange(DropBoost.Boost(1e-5, Floor, Threshold), 0.0005, 0.0008);   // 0.001 % -> about 0.06 %
        Assert.InRange(DropBoost.Boost(1e-4, Floor, Threshold), 0.0014, 0.0018);   // 0.01 %  -> about 0.16 %
        Assert.InRange(DropBoost.Boost(1e-3, Floor, Threshold), 0.0036, 0.0044);   // 0.1 %   -> about 0.4 %
        Assert.InRange(DropBoost.Boost(5e-3, Floor, Threshold), 0.0070, 0.0082);   // 0.5 %   -> about 0.76 %
        // The multiplier shrinks with the chance: the rarest gain the most.
        Assert.True(DropBoost.Boost(1e-5, Floor, Threshold) / 1e-5 > DropBoost.Boost(1e-3, Floor, Threshold) / 1e-3);
    }

    [Fact]
    public void The_servers_drop_rate_multiplies_the_boosted_chance_and_leaves_the_threshold_alone()
    {
        var x1 = new DropBoost.Settings(true, 0.01, 1, 1);
        var x2 = new DropBoost.Settings(true, 0.01, 1, 2);
        var x5 = new DropBoost.Settings(true, 0.01, 1, 5);
        // the target is rate x boost(original chance)
        foreach (double original in new[] { 1e-7, 1e-5, 1e-4, 1e-3, 5e-3 })
        {
            double boosted = DropBoost.Boost(original, Floor, Threshold);
            Assert.Equal(boosted, x1.Target(original), 12);
            Assert.Equal(2 * boosted, x2.Target(original), 12);
            Assert.Equal(5 * boosted, x5.Target(original), 12);
        }
        // 1 % or more: exactly what the server already gives (the rate alone), whatever the rate: 1 % at x5 is 5 %
        foreach (double original in new[] { 0.01, 0.02, 0.1, 0.15 })
        {
            Assert.Equal(2 * original, x2.Target(original), 12);
            Assert.Equal(5 * original, x5.Target(original), 12);
        }
        Assert.Equal(0.05, x5.Target(0.01), 12);
        Assert.Equal(1.0, x5.Target(1.0));
        // the default (threshold = minimum): 0.1 % at x2 is 0.2 %; 0.001 % is lifted to 0.01 % first, so 0.02 % at x2
        var flat2 = new DropBoost.Settings(true, 0.01, 0.01, 2);
        Assert.Equal(0.002, flat2.Target(0.001), 12);
        Assert.Equal(0.0002, flat2.Target(1e-5), 12);
        Assert.Equal(0.0002, flat2.Target(1e-9), 12);
        Assert.Equal(2 * 0.0001, flat2.Target(1e-4), 12);   // exactly 0.01 %: not touched
    }

    [Fact]
    public void The_ceiling_of_the_rate_leaves_big_chances_alone_and_saturates_below_it()
    {
        var capped = new DropBoost.Settings(true, 0.01, 0.01, 5, 25);   // rate x5, ceiling 25 %
        // at or above the ceiling: the rate does not touch it
        Assert.Equal(0.25, capped.Rated(0.25), 12);
        Assert.Equal(0.30, capped.Rated(0.30), 12);
        Assert.Equal(1.0, capped.Rated(1.0), 12);
        // below it the rate saturates: about rate x chance when small, and never reaches the ceiling
        Assert.Equal(0.25 * (1 - Math.Pow(0.8, 5)), capped.Rated(0.05), 12);   // 5 % -> 16.8 %
        Assert.Equal(0.25 * (1 - Math.Pow(0.6, 5)), capped.Rated(0.10), 12);   // 10 % -> 23.1 %
        Assert.InRange(capped.Rated(0.001), 0.00495, 0.00500);                  // 0.1 % -> about 0.5 %
        Assert.InRange(capped.Rated(0.001) / 0.001, 4.9, 5.0);
        for (double x = 1e-7; x < 0.25; x *= 1.4)
            Assert.InRange(capped.Rated(x), x, 0.25);                            // never lower, never above the ceiling
        double previous = 0;
        for (double x = 1e-7; x < 0.6; x *= 1.1)
        {
            double r = capped.Rated(x);
            Assert.True(r >= previous - 1e-15, $"order broken at x={x}");        // the order of the items is kept
            previous = r;
        }
        // without a ceiling the rate is a plain multiplier up to 100 %
        var open = new DropBoost.Settings(true, 0.01, 0.01, 5);
        Assert.Equal(1.0, open.Rated(0.25));
        Assert.Equal(0.05, open.Rated(0.01), 12);
        // a rate of 1 or less ignores the ceiling
        Assert.Equal(0.1, new DropBoost.Settings(true, 0.01, 0.01, 1, 25).Rated(0.1), 12);
        Assert.Equal(0.05, new DropBoost.Settings(true, 0.01, 0.01, 0.5, 25).Rated(0.1), 12);
        // Original undoes Rated, so the server can read the original chance back from its tables
        foreach (double x in new[] { 1e-7, 3e-6, 1e-4, 0.002, 0.03, 0.1, 0.2, 0.25, 0.4, 0.9 })   // (a value very close under the ceiling rounds to it once saturated: not recoverable, and not needed)
        {
            Assert.Equal(x, capped.Original(capped.Rated(x)), 9);
            if (open.Rated(x) < 1) Assert.Equal(x, open.Original(open.Rated(x)), 9);   // a plain rate is only invertible below 100 %
        }
        Assert.Equal(0.1, open.Original(0.5), 12);
    }

    [Fact]
    public void A_slot_chance_counts_rolls_and_picks_and_the_extra_roll_lands_on_the_target()
    {
        Assert.Equal(0.001 * 0.25, DropBoost.SlotChance(0.001, 1, 1, 0.25), 12);
        Assert.Equal(1 - Math.Pow(1 - 0.001 * 0.25, 30), DropBoost.SlotChance(0.001, 30, 1, 0.25), 12);
        Assert.Equal(0.5 * (1 - 0.75 * 0.75), DropBoost.SlotChance(0.5, 1, 2, 0.25), 12);   // two picks of one in four
        var s = new DropBoost.Settings(true, 0.01, 1, 5);
        foreach (double original in new[] { 1e-7, 3e-6, 1e-4, 0.005, 0.02, 0.4 })
        {
            double tuned = s.Rated(original);                 // what the tables give
            double target = s.Target(original);
            double extra = DropBoost.ExtraRoll(tuned, target);
            Assert.InRange(extra, 0, 1);
            Assert.Equal(Math.Max(target, tuned), tuned + (1 - tuned) * extra, 12);
        }
        Assert.Equal(0, DropBoost.ExtraRoll(1.0, 1.0));
        Assert.Equal(0, DropBoost.ExtraRoll(0.2, 0.2));
    }

    // ---- the extra roll of a kill, on the game's own data ----

    private const int SmashHammer = 2080350, HammerPool = 210023; // pool slot 3: [group 100003, 3163 per million, 1 roll, 1 pick]

    [ClientDataFact]
    public async Task A_kill_gives_a_rare_item_its_boosted_chance_only_while_the_switch_is_on()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-boost-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        await using var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        try
        {
            await app.StartAsync();
            var rewards = app.Services.GetRequiredService<RewardService>();
            // A Merchant-line character (job 4100) of level 35 is eligible for both hammer rows of the group (the generic one and its class one).
            var killer = new RoleProgress { Career = 4100, Level = 35 };
            var on = new DropBoost.Settings(true, 0.01, 1, 1);
            var off = DropBoost.Settings.Off;
            const int kills = 400_000;
            int Hits(DropBoost.Settings settings)
            {
                int hits = 0;
                var drops = new List<(int, int)>();
                for (int i = 0; i < kills; i++)
                {
                    drops.Clear();
                    rewards.RollPool(HammerPool, killer, drops, null, 35);                      // the normal roll of the kill
                    rewards.RollBoostedExtras([HammerPool], killer, drops, 35, settings);       // the panel's extra roll
                    hits += drops.Count(d => d.Item1 == SmashHammer);
                }
                return hits;
            }
            // without the boost: 3163 / 1e6 x (154 / 100002) = 0.00049 % -> about 2 hits in 400 000 kills
            Assert.True(Hits(off) < 12);
            // boosted at 0.01 % / 1 %: DropBoost.Boost(4.87e-6) = about 0.047 % -> about 190 hits
            double expected = DropBoost.Boost(0.003163 * (154.0 / 100002), 0.0001, 0.01) * kills;
            Assert.InRange(Hits(on), expected * 0.7, expected * 1.3);
            // the default (threshold = minimum): everything below 0.01 % becomes 0.01 % -> 40 hits in 400 000 kills
            Assert.InRange(Hits(new DropBoost.Settings(true, 0.01, 0.01, 1)), 40 * 0.55, 40 * 1.6);
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
