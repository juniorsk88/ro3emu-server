using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Real game-wire casts with deterministic owned-world fixtures and negative lifecycle checks.</summary>
[Collection("MonsterAi")]
public sealed partial class SkillCombatTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skillcombat-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private CharacterService Character => _app.Services.GetRequiredService<CharacterService>();
    private GameSession Session(string name) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == name.ToLowerInvariant());

    public async Task InitializeAsync()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync(); _http = $"http://127.0.0.1:{ports.Item1}";
    }
    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name); await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }
    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }
    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }
    private Task Teach(string name, int group) => Loop.InvokeAsync(() => { Session(name).Progress!.Skills[group] = 1; return Task.CompletedTask; });
    private Task<MonsterUnit> Target(string name, bool stationary = false, int npcId = 5000001) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        MonsterTemplate? template = null;
        if (stationary)
        {
            template = monsters.Template(npcId);
            Assert.NotNull(template);
            template = template with { RoamDistance = 0 };
        }
        var result = await monsters.SpawnSingleAsync(s.SceneEntry, npcId,
            new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template);
        Assert.NotNull(result);
        return result;
    });

    [ClientDataFact]
    public async Task CancelledAttachmentDuringFirstAreaHitCannotDamageNextTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11002);
        // Stable positions preserve first-then-second AoE ordering while the cast waits.
        var first = await Target("alice", stationary: true);
        var second = await Target("alice", stationary: true);
        var player = Session("alice");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealt = 0;
        Task? detach = null;
        await Loop.InvokeAsync(() =>
        {
            first.Hp = second.Hp = 100000;
            _app.Services.GetRequiredService<RoleHandover>().BeforeCleanupForTest = session =>
                session == player ? releaseCleanup.Task : Task.CompletedTask;
            _app.Services.GetRequiredService<CombatService>().DealtAsync.Add(async (source, target, _, _) =>
            {
                if (source != player || target != first && target != second) return;
                dealt++;
                if (target == first) { entered.TrySetResult(); await release.Task; }
            });
            return Task.CompletedTask;
        });
        long secondHp = second.Hp;
        long generation = player.SceneGeneration;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            await Loop.InvokeAsync(() =>
            {
                detach = _app.Services.GetRequiredService<RoleHandover>().Begin(player);
                Assert.True(player.Attachment!.Cancelled);
                Assert.Equal(generation, player.SceneGeneration); // cleanup has not changed the scene yet
                return Task.CompletedTask;
            });
            release.TrySetResult();
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            Assert.Equal(1, dealt);
            Assert.Equal(secondHp, second.Hp);
        }
        finally { release.TrySetResult(); releaseCleanup.TrySetResult(); }
        await detach!.WaitAsync(Wait);
        Assert.True(first.Hp < 100000);
    }

    [ClientDataFact]
    public async Task CancelledLeaseAfterLethalAreaHitKeepsCommittedKillConsequences()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11002);
        // AoE sorts by distance then UID; roaming must not move the second target ahead.
        var first = await Target("alice", stationary: true);
        var second = await Target("alice", stationary: true);
        var player = Session("alice");
        var lease = player.Attachment!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealt = 0, applied = 0, killCount = 0;
        await Loop.InvokeAsync(() =>
        {
            first.Hp = 1;
            second.Hp = 100000;
            var combat = _app.Services.GetRequiredService<CombatService>();
            combat.MonsterDamageApplied.Add((source, target, _) =>
            {
                if (source == player && target == first) applied++;
                return Task.CompletedTask;
            });
            combat.Killed.Add((source, target) =>
            {
                if (source == player && target == first) { killCount++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            combat.DealtAsync.Add(async (source, target, _, _) =>
            {
                if (source != player) return;
                dealt++;
                if (target == first) { entered.TrySetResult(); await release.Task; }
            });
            return Task.CompletedTask;
        });
        long secondHp = second.Hp;
        long generation = player.SceneGeneration;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            // Isolate the attachment fence while the owner still admits its already-committed kill callbacks.
            await Loop.InvokeAsync(() =>
            {
                Assert.Equal(1, dealt);
                Assert.Equal(secondHp, second.Hp);
                lease.Cancelled = true;
                Assert.Same(lease, player.Attachment);
                Assert.Equal(generation, player.SceneGeneration);
                return Task.CompletedTask;
            });
            release.TrySetResult();
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await killed.Task.WaitAsync(Wait);
            Assert.Equal(1, dealt);
            Assert.Equal(1, applied);
            Assert.Equal(1, killCount);
            Assert.Equal(secondHp, second.Hp);
            Assert.True(first.Dead);
        }
        finally
        {
            release.TrySetResult();
            await Loop.InvokeAsync(() => { lease.Cancelled = false; return Task.CompletedTask; });
        }
    }

    [ClientDataFact]
    public async Task AcceptedCastHitsAndPassiveUnownedAndWrongActorAreRefused()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var learn = new SkillGrowthSkillPlanUpdateReq();
        learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        var target = await Target("alice");
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        var reply = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, reply.Result);
        var use = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, p => p.SessionId == reply.SessionId);
        Assert.Equal(target.Uid, use.TargetId);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == reply.SessionId);
        Assert.Equal(1100101, hit.ShowPoint); // master's skill-specific EffectData entry for Bash
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        Assert.True(target.Hp < before);
        var statistics = await Next(alice, CombatService.DamageStatistics, DamageStatisticsNotify.Parser,
            n => n.Damages.Any(d => d.DamageTargetId == target.Uid));
        Assert.Equal(hit.Dsts[0].Damage, Assert.Single(statistics.Damages).DamageValue);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, OpUnitId = long.MaxValue, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Teach("alice", 11009);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100901, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillPassive, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task InterruptInvalidatesDelayedHitAndEchoesAutoCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12004);
        var target = await Target("alice");
        long hp = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid, IsAutoCast = true });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result); Assert.True(accepted.IsAutoCast);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, x => x.SessionId == accepted.SessionId);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = 1200401 });
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, x => x.SessionId == accepted.SessionId);
        Assert.True(end.ClearEffect);
        await Task.Delay(1600);
        await Loop.InvokeAsync(() => { Assert.Equal(hp, target.Hp); Assert.Null(Session("alice").SkillCast); return Task.CompletedTask; });
    }

    /// <summary>
    /// Ro3Emu:SkillNoCancel: another skill neither replaces the cast in progress (even a _bCancelCast 1 skill) nor interrupts it
    /// through the client's "interrupt and cast next" request; the first cast lands its hit.
    /// </summary>
    [ClientDataFact]
    public async Task ASkillInProgressIsNotCancelledByAnotherSkill()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12004); await Teach("alice", 12103);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        long hp = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, first.Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210301, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq
        { InterruptSkillId = 1200401, NxtSkill = new CastSkillReq { SkillId = 1210301, TargetId = target.Uid } });
        Assert.False((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId);
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        await Loop.InvokeAsync(() => { Assert.True(target.Hp < hp); return Task.CompletedTask; });
    }

    /// <summary>
    /// Holding the key of a chanted skill (Fire Bolt: 1.0 s chant, 0.5 s cooldown, _bCancelCast 1) asks for the same
    /// skill again 0.5 s into its chant. That repeat must not cancel the cast in progress: if it did, every cast
    /// would be cancelled before its hit and nothing would ever land.
    /// </summary>
    [ClientDataFact]
    public async Task RepeatingTheSameChantedSkillDoesNotCancelTheCastInProgress()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12004);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        long hp = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid, IsAutoCast = true });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, first.Result);
        await Task.Delay(650);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid, IsAutoCast = true });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId);
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        await Loop.InvokeAsync(() => { Assert.True(target.Hp < hp); return Task.CompletedTask; });
    }

    /// <summary>
    /// The key held down for seconds (the client repeats the request every ~120 ms): every cast the server accepted must
    /// land its hit. A repeat arriving after a cast's impact but before its projectile lands used to cancel that cast,
    /// so the chant bar kept filling and nothing was ever damaged.
    /// </summary>
    [ClientDataFact]
    public async Task HoldingAChantedSkillLandsEveryAcceptedCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12004);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 10_000_000; target.Template = target.Template with { MaxHp = 10_000_000 }; return Task.CompletedTask; });
        var until = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < until)
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid, IsAutoCast = true });
            await Task.Delay(120);
        }
        await Task.Delay(2500);
        var events = alice.Inbox.Snapshot();
        var accepted = events.Where(e => e.IsGame(CombatService.UseSkill)).Select(e => CastSkillRsp.Parser.ParseFrom(e.Body))
            .Where(r => r.Result == 0).Select(r => r.SessionId).Distinct().ToList();
        var landed = events.Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Where(h => h.Dsts.Any(d => d.Damage > 0)).Select(h => h.SessionId).ToHashSet();
        Assert.True(accepted.Count >= 3, $"accepted {accepted.Count}");
        Assert.Equal(accepted.Count, accepted.Count(landed.Contains));
    }

    /// <summary>
    /// A pulled field monster is not tied to its spawn point: it keeps chasing while the player is near it, and when it
    /// does give up (the player is beyond its leash range from the monster) it stays where it is, with its HP, instead
    /// of walking home and healing (which would undo pulling a pack for somebody else to kill).
    /// </summary>
    [ClientDataFact]
    public async Task FieldMonsterKeepsChasingFarFromHomeAndStaysWhereItGaveUp()
    {
        await using var alice = await InWorld("Alice");
        var m = await Target("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var scenes = _app.Services.GetRequiredService<SceneService>();
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            var map = _app.Services.GetRequiredService<World>().Map(s.WorldScene);
            m.NextRoamMs = long.MaxValue; m.NextSwingMs = long.MaxValue;
            m.Hp = m.Template.MaxHp / 2;
            long hurt = m.Hp;
            float leash = m.Template.LeashDistance;
            // The chase took it far beyond the leash range of its spawn point, and the player is right beside it.
            m.X = m.Home.X + leash * 2; m.Moving = false; map.Moved(m);
            await scenes.TeleportInMapAsync(s, new MapPoint(m.X, m.Y, m.Z, 0));
            monsters.Engage(m, s);
            monsters.Tick(m, map);
            Assert.Same(s, m.Target);                                      // still chasing: only the distance to the player counts
            Assert.False(m.Returning);

            // The player gets away: out of the monster's leash range, the monster gives up where it stands.
            float x = m.X, z = m.Z;
            await scenes.TeleportInMapAsync(s, new MapPoint(m.X + leash + 5, m.Y, m.Z, 0));
            monsters.Tick(m, map);
            Assert.Null(m.Target);
            Assert.False(m.Returning);                                     // it does not walk home
            Assert.Equal(hurt, m.Hp);                                      // and it does not heal
            Assert.Equal((x, z), (m.X, m.Z));
            Assert.Equal((x, z), m.RoamAnchor);                            // it roams around where it is now
            for (int i = 0; i < 5; i++) monsters.Tick(m, map);
            Assert.Equal(hurt, m.Hp);
        });
    }

    /// <summary>The time-based give-up (AIConfig _iUnFightType 2): a monster that has not been hit nor hit anything for the
    /// configured time lets go of its target, so one stuck behind an obstacle does not hold it for ever; 0 turns it off.</summary>
    [ClientDataFact]
    public async Task FieldMonsterGivesUpAfterTheConfiguredTimeWithoutCombat()
    {
        await using var alice = await InWorld("Alice");
        var m = await Target("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        try
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                var map = _app.Services.GetRequiredService<World>().Map(s.WorldScene);
                m.NextRoamMs = long.MaxValue; m.NextSwingMs = long.MaxValue;
                monsters.Engage(m, s);
                monsters.Tick(m, map);
                Assert.Same(s, m.Target);
                m.ChaseActiveMs = MonsterService.NowMs - 29_000;                // 29 s: still chasing (default 30 s)
                monsters.Tick(m, map);
                Assert.Same(s, m.Target);
                m.ChaseActiveMs = MonsterService.NowMs - 31_000;                // 31 s without a hit either way: it lets go
                monsters.Tick(m, map);
                Assert.Null(m.Target);
                Assert.False(m.Returning);

                MonsterAiSettings.PinForTest(new MonsterAiProfiles(MonsterAiSettings.FieldDefault with { GiveUpSeconds = 0 }, MonsterAiSettings.BossDefault));
                monsters.Engage(m, s);
                m.ChaseActiveMs = MonsterService.NowMs - 3_600_000;             // off: never
                monsters.Tick(m, map);
                Assert.Same(s, m.Target);
                return Task.CompletedTask;
            });
        }
        finally { MonsterAiSettings.PinForTest(null); }
    }

    /// <summary>
    /// Killing a player makes the monster level up, recover HP and grow by the scale (kills x step up to the cap, with the EXP and
    /// item bonus on the same scale), and it then stays where it is instead of walking home and healing.
    /// </summary>
    [ClientDataFact]
    public async Task AMonsterThatKillsPlayersGrowsOnTheScaleUpToTheCap()
    {
        await using var alice = await InWorld("Alice");
        var m = await Target("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        try
        {
            // Return home and full heal are on for this profile: a monster that just killed must still do neither.
            MonsterAiSettings.PinForTest(new MonsterAiProfiles(MonsterAiSettings.FieldDefault with { ReturnHome = true, HealOnReset = true }, MonsterAiSettings.BossDefault));
            var b = await Loop.InvokeAsync(() => Task.FromResult(m.Template));
            await Loop.InvokeAsync(async () => { m.Hp = 1; await Task.CompletedTask; });
            for (int kill = 1; kill <= 12; kill++)
            {
                await Loop.InvokeAsync(async () =>
                {
                    long before = m.Hp;
                    await monsters.OnKilledPlayerAsync(m);
                    double pct = Math.Min(500, kill * 50);
                    Assert.Equal(kill, m.PlayerKills);
                    Assert.Equal(pct, m.GrowthPct);
                    Assert.Equal(b.Level + Math.Min(kill, 10), m.Template.Level);                         // one level per kill until the cap
                    Assert.Equal((long)Math.Round(b.MaxHp * (1 + pct / 100)), m.Template.MaxHp);
                    Assert.Equal((long)Math.Round(b.Atk * (1 + pct / 100)), m.Template.Atk);
                    Assert.Equal(pct, m.ExpBonusPct); Assert.Equal(pct, m.DropBonusPct);                  // 500 at the cap, on the same scale
                    Assert.Equal(Math.Min(m.Template.MaxHp, before + (long)Math.Round(m.Template.MaxHp * 0.2)), m.Hp);   // 20 % of its HP per kill
                });
            }
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                var map = _app.Services.GetRequiredService<World>().Map(s.WorldScene);
                m.NextRoamMs = long.MaxValue; m.NextSwingMs = long.MaxValue;
                m.Hp = m.Template.MaxHp / 2; long hp = m.Hp;
                monsters.Engage(m, s);
                s.Dead = true;                                            // its target died
                monsters.Tick(m, map);
                Assert.Null(m.Target);
                Assert.False(m.Returning);                                // it stays where it is
                Assert.Equal(hp, m.Hp);                                   // and does not heal fully
                s.Dead = false;
                return Task.CompletedTask;
            });
            // Off: nothing changes.
            MonsterAiSettings.PinForTest(new MonsterAiProfiles(MonsterAiSettings.FieldDefault with { KillGrowth = false }, MonsterAiSettings.BossDefault));
            var other = await Target("alice");
            await Loop.InvokeAsync(async () => { await monsters.OnKilledPlayerAsync(other); Assert.Equal(0, other.PlayerKills); Assert.Equal(0, other.GrowthPct); });
        }
        finally { MonsterAiSettings.PinForTest(null); }
    }

    /// <summary>The same progression through the real swing: the monster's own attack kills the player (not a direct call).</summary>
    [ClientDataFact]
    public async Task AMonsterThatKillsAPlayerWithItsOwnAttackLevelsUpAndHeals()
    {
        await using var alice = await InWorld("Alice");
        var m = await Target("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var (level, hurt) = await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            m.Hp = m.Template.MaxHp / 2;
            s.CurrentHp = 1;
            monsters.Engage(m, s);
            return Task.FromResult((m.Template.Level, m.Hp));
        });
        for (int i = 0; i < 100 && !await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Dead)); i++) await Task.Delay(100);
        await Task.Delay(800);
        await Loop.InvokeAsync(() =>
        {
            Assert.True(Session("alice").Dead);
            Assert.Equal(1, m.PlayerKills);
            Assert.Equal(50, m.GrowthPct);
            Assert.Equal(level + 1, m.Template.Level);
            Assert.True(m.Hp > hurt, $"hp {m.Hp} was {hurt}");
            return Task.CompletedTask;
        });
        // and the dead player's client is told: new level, new max HP, the recovered HP, and the effect
        var seen = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.AoiAttrChange)).Select(e => AOIAttrChange.Parser.ParseFrom(e.Body))
            .Where(c => c.EntityUuid == (ulong)m.Uid).SelectMany(c => c.Property).ToList();
        Assert.Contains(seen, p => p.Idx == 2002 && p.Data == level + 1);
        Assert.Contains(seen, p => p.Idx == 101);
        Assert.Contains(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEffect) && SkillEffectDisplayNotify.Parser.ParseFrom(e.Body).SourceId == m.Uid);
        // a client that never said it has the level-up sprite (the original data) still gets only effects that exist in it
        var effects = EvolutionEffects(alice, m);
        Assert.DoesNotContain(MonsterService.LevelUpFxId, effects);
        Assert.NotEmpty(effects);
    }

    private static List<int> EvolutionEffects(Ro3Emu.Bot.BotClient bot, MonsterUnit m) => bot.Inbox.Snapshot()
        .Where(e => e.IsGame(CombatService.SkillEffect)).Select(e => SkillEffectDisplayNotify.Parser.ParseFrom(e.Body))
        .Where(x => x.SourceId == m.Uid).Select(x => (int)x.EffectId).ToList();

    /// <summary>
    /// A client with Ro3Emu.ClientHooks.LevelUpEffect says so (77017); the monster's evolution then plays the character's own
    /// level-up sprite for it (the row of our own id) instead of the stand-in effects, and nobody else's view changes.
    /// </summary>
    [ClientDataFact]
    public async Task AnEvolvingMonsterPlaysTheLevelUpSpriteForClientsThatHaveIt()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameBytesAsync(MonsterService.LevelUpFxHello, "fx"u8.ToArray());
        for (int i = 0; i < 50 && !await Loop.InvokeAsync(() => Task.FromResult(Session("alice").LevelUpFx)); i++) await Task.Delay(100);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Session("alice").LevelUpFx)), "the hello was not recorded");
        var m = await Target("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            s.CurrentHp = 1;
            monsters.Engage(m, s);
            return Task.CompletedTask;
        });
        for (int i = 0; i < 100 && !await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Dead)); i++) await Task.Delay(100);
        await Task.Delay(800);
        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(m.PlayerKills)));
        Assert.Equal([MonsterService.LevelUpFxId], EvolutionEffects(alice, m));
    }

    [ClientDataFact]
    public async Task GroundNpcUsesRequestedPositionAndDespawnsOnTeardown()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010);
        var point = Session("alice").Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var spawn = await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.Bases.Any(b => b.EntityTemplateID == 1200004));
        var field = Assert.Single(spawn.Bases, b => b.EntityTemplateID == 1200004);
        Assert.Equal((long)alice.Self!.Uuid, field.Owner); Assert.Equal(point, field.MovementInfo.Pos);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<CombatService>().ClearSkillsAsync(Session("alice")));
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.LeaveAoi.Contains(field.Uuid));
        Assert.Empty(Session("alice").SkillObjects);
    }

    /// <summary>
    /// Magnus Exorcismus (14109) is the Exorciser's cross on the floor: its script was not a ground family, so no NPC 22000009
    /// spawned and the player saw no cross. It now spawns for 4 s (_kLogicData[1]) and hits every 0.5 s (_kDescData[4]).
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusLeavesItsCrossOnTheFloorForFourSeconds()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var point = Session("alice").Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var spawn = await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.Bases.Any(b => b.EntityTemplateID == 22000009));
        var cross = Assert.Single(spawn.Bases, b => b.EntityTemplateID == 22000009);
        Assert.Equal((long)alice.Self!.Uuid, cross.Owner); Assert.Equal(point, cross.MovementInfo.Pos);
        var unit = Assert.Single(Session("alice").SkillObjects);
        Assert.Equal(500, unit.IntervalMs);
        Assert.InRange(unit.ExpiresMs - unit.NextTickMs, 3000, 4500);
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.LeaveAoi.Contains(cross.Uuid));
        Assert.Empty(Session("alice").SkillObjects);
    }

    /// <summary>
    /// The cross hits every 0.5 s for 4 s whatever the skill level (_kDescData[3] and [4] are the same on all 10 levels; the level
    /// only changes the damage), so one cast lands about 8 hits on a monster that stays inside it.
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusHitsEveryHalfSecondForFourSeconds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 10_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = Session("alice").Position.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Task.Delay(5500);
        int hits = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Count(h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.InRange(hits, 7, 9);
    }

    [ClientDataFact]
    public async Task HealSelectsAllyAndInsufficientSpDoesNotStartCooldown()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007); await Teach("alice", 14001);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("bob"), 1, null));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = (long)bob.Self!.Uuid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal((long)bob.Self.Uuid, Assert.Single(hit.Dsts).DstID); Assert.Equal(DamageType.Heal, hit.Dsts[0].DamageType);
        await Task.Delay(600);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, 0));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101 });
        Assert.Equal(ErrorCodes.SkillCostShort, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.False(Session("alice").SkillCds.ContainsKey(14001));
    }

    /// <summary>
    /// Blessing and Increase AGI deal no damage, so their glow (the type-3 hit effect: fx_buff_angelblessing,
    /// fx_buff_increaseagi) reaches the client only through a SkillHit naming the buffed player. DamageShare with a
    /// Normal result draws the effect and no number.
    /// </summary>
    [ClientDataFact]
    public async Task BuffSkillsDrawTheirOwnGlowOnTheBuffedAlly()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14001); await Teach("alice", 14010);
        foreach (var (skill, show) in new[] { (1400101, 1400102), (1401001, 1401004) })
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = (long)bob.Self!.Uuid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            foreach (var viewer in new[] { alice, bob })
            {
                var hit = await Next(viewer, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
                Assert.Equal(show, hit.ShowPoint);
                // The buff lands on every ally in range, the caster included.
                Assert.Contains(hit.Dsts, d => d.DstID == (long)bob.Self.Uuid);
                Assert.All(hit.Dsts, d =>
                {
                    Assert.Equal(DamageType.DamageShare, d.DamageType);
                    Assert.Equal(DamageHitResult.Normal, d.HitResult);
                    Assert.Equal(0, d.Damage);
                });
            }
            await Loop.InvokeAsync(() =>
            {
                var buff = Assert.Single(Session("bob").Buffs, b => b.ConfigId == skill);
                if (skill == 1400101)
                    Assert.Equal(new[] { (1L, 2d), (5L, 2d), (4L, 2d) }, buff.Changes);
                else
                    Assert.Equal(new[] { (2L, 2d), (1131L, 200d), (1141L, 200d) }, buff.Changes);
                return Task.CompletedTask;
            });
            await Task.Delay(700);
        }
    }

    [ClientDataFact]
    public async Task LethalHitBlocksReviveUntilDeathPublicationCompletes()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var monster = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(player, 1, null);
            combat.TakenAsync.Add(async (_, target, _) =>
            {
                if (target != player || !target.Dead) return;
                entered.TrySetResult(); await release.Task;
            });
            combat.PlayerDied.Add(target =>
            {
                if (target == player) settled.TrySetResult();
                return Task.CompletedTask;
            });
            await _app.Services.GetRequiredService<MonsterService>().Attack!(monster, player);
        });
        try
        {
            await entered.Task.WaitAsync(Wait);
            await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
            Assert.True(player.Dead);
            Assert.Equal(0, Character.Stats(player)[2000]);
        }
        finally { release.TrySetResult(); }
        await settled.Task.WaitAsync(Wait);
        await Loop.InvokeAsync(() => Task.CompletedTask);
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, m => !m.BDead);
        Assert.False(player.Dead);
        Assert.True(Character.Stats(player)[2000] > 0);
    }

    [ClientDataFact]
    public async Task PendingLethalHitCannotDeliverDuplicateKillCallbacks()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1100");
        await Gm("bob", "!job 1100");
        await Teach("alice", 11001);
        await Teach("bob", 11001);
        var target = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealtCount = 0, killCount = 0;
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1;
            combat.DealtAsync.Add(async (source, monster, _, _) =>
            {
                if (monster != target) return;
                dealtCount++;
                if (source == Session("alice")) { entered.TrySetResult(); await release.Task; }
            });
            combat.Killed.Add((_, monster) =>
            {
                if (monster == target) { killCount++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
            Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var afterSecondImpact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Loop.InvokeAsync(() =>
            {
                var cast = Session("bob").SkillCast!;
                Loop.Schedule(TimeSpan.FromMilliseconds(cast.ChantMs + cast.ActionMs + 100),
                    () => { afterSecondImpact.TrySetResult(); return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            await afterSecondImpact.Task.WaitAsync(Wait);
            Assert.Equal(1, dealtCount);
            Assert.Equal(0, killCount);
        }
        finally { release.TrySetResult(); }
        await killed.Task.WaitAsync(Wait);
        Assert.Equal(1, killCount);
        Assert.True(target.Dead);
    }

    [ClientDataFact]
    public async Task EventCastRechecksRangeAndUsesTheSharedLethalProtection()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 2100");
        await Teach("alice", 12004);
        var a = Session("alice");
        var b = Session("bob");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(a.SceneEntry, [a, b])));
        foreach (var (bot, session) in new[] { (alice, a), (bob, b) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((source, target) => source == a && target == b, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        long hp = Character.Stats(b)[2000];
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role!.Id });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, m => m.SessionId == accepted.SessionId);
        var afterImpact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            var cast = a.SkillCast!;
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
                new MapPoint(b.Position.X + 40, b.Position.Y, b.Position.Z, b.Face));
            Loop.Schedule(TimeSpan.FromMilliseconds(cast.ChantMs + cast.ActionMs + 100),
                () => { afterImpact.TrySetResult(); return Task.CompletedTask; });
        });
        await afterImpact.Task.WaitAsync(Wait);
        Assert.Equal(hp, Character.Stats(b)[2000]);
        Assert.False(b.Dead);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit && hit.SessionId == accepted.SessionId
            && hit.Dsts.Any(d => d.DstID == b.Role.Id));

        await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
                new MapPoint(a.Position.X, a.Position.Y, a.Position.Z, b.Face));
            await _app.Services.GetRequiredService<BuffService>().AddAsync(b, 2052101, 60000, b.Role.Id);
            await Character.SetHpSpAsync(b, 1, null);
            await Character.SetHpSpAsync(a, null, long.MaxValue);
            a.SkillCds.Clear(); a.SkillModeCds.Clear();
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role.Id });
        accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        Assert.Equal(2052101, (await Next(bob, BuffService.ImmuneDeathNotify, PlayerOnEnterImmuneDeadNotify.Parser)).BuffConfigId);
        var protectedHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.False(Assert.Single(protectedHit.Dsts).IsDead);
        Assert.False(b.Dead);
        Assert.NotNull(b.ImmuneDeathOffer);
        Assert.Equal(1, Character.Stats(b)[2000]);
    }

    [ClientDataFact]
    public async Task UnifiedCastCallsMasterDamageAndHealingHooksOnce()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        await Teach("alice", 11001);
        var target = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        int landed = 0, adjusted = 0, extra = 0, dealt = 0, healed = 0;
        long delivered = 0;
        await Loop.InvokeAsync(() =>
        {
            combat.CastLanded.Add((_, _) => { landed++; return Task.CompletedTask; });
            combat.DamageAdjusters.Add((_, _, _, _, _) => { adjusted++; return 17; });
            combat.ExtraHitDamage.Add((_, _, _, _) => { extra++; return 3; });
            combat.DealtAsync.Add((_, _, _, amount) => { dealt++; delivered = amount; return Task.CompletedTask; });
            combat.ExtraHeal.Add((_, _) => { healed++; return 23; });
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(20, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(20, delivered);
        Assert.Equal((1, 1, 1, 1), (landed, adjusted, extra, dealt));

        await Gm("alice", "!lvl 6"); // First Aid's configured activation level.
        await Loop.InvokeAsync(async () =>
        {
            await combat.ClearSkillsAsync(Session("alice"));
            await Character.SetHpSpAsync(Session("alice"), 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000301 });
        accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(DamageType.Heal, Assert.Single(hit.Dsts).DamageType);
        Assert.Equal(23, hit.Dsts[0].Damage);
        Assert.Equal(1, healed);
        Assert.Equal(2, landed);
    }

    [ClientDataFact]
    public async Task WorldTreeConsumesOneLeafAndRevivesDeadAlly()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!item 1002004 2");
        await Loop.InvokeAsync(async () => { Session("bob").Dead = true; await Character.SetHpSpAsync(Session("bob"), 0, 0); });
        var combat = _app.Services.GetRequiredService<CombatService>();
        var ownerCheck = combat.ReviveCheck;
        await Loop.InvokeAsync(() => { combat.ReviveCheck = _ => ErrorCodes.FeatureUnavailable; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = (long)bob.Self!.Uuid });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(Session("bob").Dead);
        Assert.Equal(2, Session("alice").Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
        await Loop.InvokeAsync(() => { combat.ReviveCheck = ownerCheck; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = await Next(alice, BuffService.WorldTreeRevive, ItemWorldTreeReviveNtf.Parser);
        Assert.Equal("Bob", notice.WorldTreePlayerName);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(Session("bob").Dead); Assert.True(Character.Stats(Session("bob"))[2000] > 0);
            Assert.Equal(1, Session("alice").Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task BackSlideMovesTheActorAndItsAuthoritativePosition()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15104);
        var session = Session("alice");
        var start = session.Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        int direction = Enumerable.Range(0, 8).Select(i => i * 45).First(angle => nav == null ||
            nav.CanWalk(start.X, start.Y, start.Z, start.X + 2 * MathF.Sin(angle * MathF.PI / 180),
                start.Z + 2 * MathF.Cos(angle * MathF.PI / 180)));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401, Face = direction - 180 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser,
            m => m.EntityID == alice.Self!.Uuid);
        Assert.Equal(start, moved.StartPos);
        Assert.Equal(1510403, moved.EffectId); // the client's SkillStartMove ignores effectId 0
        Assert.Equal(180, moved.MoveTimeMs);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z), 0.1f, 10.01f); // "${1}=10 meters"
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, session.Position); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ACasterStillWalkingIntoRangeIsAcceptedAndAStandingOneOutOfRangeIsNot()
    {
        // In-game 11.10: the client sends the attack on reaching range and its stop 13 ms later; the server measured
        // from the last walk's start and refused 3024 on most approaches.
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var start = Session("alice").Position.Clone();
        // 5 m (beyond the 3 m basic attack) in a direction the nav grid lets the attack through.
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var away = new[] { (5f, 0f), (-5f, 0f), (0f, 5f), (0f, -5f), (3.6f, 3.6f), (-3.6f, -3.6f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        Assert.Equal(ErrorCodes.SkillDistance, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        long speed = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Speed));
        Assert.True(speed >= 400, $"speed {speed}"); // 0.5 s below covers the 1.5 m beyond range at ≥ 4 m/s
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Dir = 180, Face = 180, SpeedRate = 1 });
        await Task.Delay(500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        // The client's stop follows at once, where it really was: the cast continues and hits.
        var arrived = new Vec3 { X = start.X + (away.X - start.X) * 0.5f, Y = start.Y, Z = start.Z + (away.Z - start.Z) * 0.5f };
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = arrived, Face = 180, BStop = true, SpeedRate = 1 });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        Assert.Equal(target.Uid, Assert.Single(hit.Dsts).DstID);
    }

    [ClientDataFact]
    public async Task ACasterOnALongStraightApproachKeepsItsCastWhenItStops()
    {
        // In-game 22.10: the client sends a walk only when its direction changes; Sonic Blow cast after a 0.86 s straight
        // leg was accepted, then its stop 6 m from the leg's start interrupted it (the stop allowed 0.75 s of walking).
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var away = new[] { (9f, 0f), (-9f, 0f), (0f, 9f), (0f, -9f), (6.4f, 6.4f), (-6.4f, -6.4f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        long speed = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Speed));
        // Walk until 2 m from the target: 7 m, longer than 0.75 s at this speed.
        int walkMs = (int)(7 / (speed / 100.0) * 1000);
        Assert.True(walkMs > 900, $"speed {speed}");
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Dir = 180, Face = 180, SpeedRate = 1 });
        await Task.Delay(walkMs);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var arrived = new Vec3 { X = start.X + (away.X - start.X) * 2 / 9f, Y = start.Y, Z = start.Z + (away.Z - start.Z) * 2 / 9f };
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = arrived, Face = 180, BStop = true, SpeedRate = 1 });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        Assert.Equal(target.Uid, Assert.Single(hit.Dsts).DstID);
    }

    [ClientDataFact]
    public async Task ArcherCastAtClientRangeBoundaryHitsAnExactlyRepresentedFieldMonster()
    {
        // ClientB skill_13000.m_fAtkRange is 18 m. NPCConfig[5000007]._iPrefabRadius is 0,
        // and UnitConfig[31007]._iCollisionType is None, so the client's target radius is 0.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100");
        var target = await Target("alice", stationary: true, npcId: 5000007);
        var aoi = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(b => b.Uuid == (ulong)target.Uid));
        var wireUid = Assert.Single(aoi.Bases, b => b.Uuid == (ulong)target.Uid).Uuid;
        Assert.Equal(target.Uid, (long)(double)wireUid); // the client's shared-memory path uses doubles

        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var approach = Enumerable.Range(0, 32).Select(i => i * MathF.Tau / 32)
            .Select(angle => (
                Far: new Vec3
                {
                    X = start.X + 18.7f * MathF.Cos(angle), Y = start.Y,
                    Z = start.Z + 18.7f * MathF.Sin(angle)
                },
                Near: new Vec3
                {
                    X = start.X + 18f * MathF.Cos(angle), Y = start.Y,
                    Z = start.Z + 18f * MathF.Sin(angle)
                }))
            .First(p => nav.IsWalkable(p.Far.X, p.Far.Y, p.Far.Z)
                && nav.IsWalkable(p.Near.X, p.Near.Y, p.Near.Z)
                && nav.CanWalk(start.X, start.Y, start.Z, p.Far.X, p.Far.Z));

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = approach.Far, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300001, TargetId = (long)wireUid });
        Assert.Equal(ErrorCodes.SkillDistance, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = approach.Near, BStop = true, SpeedRate = 1 });
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300001, TargetId = (long)wireUid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        var damage = Assert.Single(hit.Dsts);
        Assert.Equal(target.Uid, damage.DstID);
        Assert.True(damage.Damage > 0);
        Assert.True(target.Hp < before);
    }
}
