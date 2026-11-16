using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class Ro1RenewalAggressionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [ClientDataFact]
    public async Task RenewalMonsterAttacksWhilePassiveMonsterDoesNot()
    {
        await using var host = await Host.StartAsync();
        var monsters = host.Service<MonsterService>();
        Assert.True(monsters.Template(5000064)!.Aggressive); // Pirate Skeleton
        Assert.False(monsters.Template(5000019)!.Aggressive); // Familiar
        Assert.False(monsters.Template(5000022)!.Aggressive); // Scorpion
        Assert.False(monsters.Template(5000001)!.Aggressive); // Poring

        using var stream = typeof(MonsterService).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.ro1-renewal-aggression.json")!;
        using var reference = JsonDocument.Parse(stream);
        var entries = reference.RootElement.GetProperty("monsters");
        Assert.Equal(223, entries.EnumerateObject().Count());
        foreach (var entry in entries.EnumerateObject())
            Assert.Equal(entry.Value.GetProperty("aggressive").GetBoolean(), monsters.Template(int.Parse(entry.Name))!.Aggressive);

        var unmatched = host.Service<GameData>().Table("NPCConfig")
            .Where(e => !entries.TryGetProperty(e.Key, out _) && GameData.Int(e.Value, "_iNPCType") == 1)
            .GroupBy(e => GameData.Int(e.Value, "_iAI") == 20000004)
            .Select(g => g.First()).ToArray();
        Assert.NotEmpty(unmatched);
        foreach (var entry in unmatched)
            Assert.Equal(GameData.Int(entry.Value, "_iAI") == 20000004, monsters.Template(int.Parse(entry.Key))!.Aggressive);

        await using var alice = await host.InWorld("Alice");
        var s = await host.SessionOf(alice);
        var pirate = await host.SpawnBeside(s, 5000064, 1.5f);
        var familiar = await host.SpawnBeside(s, 5000019, -1.5f);
        await host.Until(() => ReferenceEquals(pirate.Target, s), "pirate takes Alice as target");
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill)
            && Romsg.UseInSkillInfo.Parser.ParseFrom(e.Body).SrcID == pirate.Uid, Wait);
        await Task.Delay(GameLoop.TickInterval * 6);
        Assert.Null(await host.OnLoop(() => familiar.Target));
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ro1aggr-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        private string _http = "";

        public static async Task<Host> StartAsync()
        {
            var host = new Host();
            var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
            host._app = ServerHost.Create([], o =>
            {
                o.DataDir = host._dataDir;
                o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
                o.HttpPort = httpPort;
                o.MicroGatePort = microPort;
                o.GameGatePort = gamePort;
            });
            await host._app.StartAsync();
            host._http = $"http://127.0.0.1:{httpPort}";
            return host;
        }

        public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

        public Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

        public async Task<BotClient> InWorld(string name)
        {
            var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
            await bot.LoginAsync(name.ToLowerInvariant(), "pw");
            await bot.EnterGateAsync();
            var role = await bot.CreateRoleAsync(name);
            await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
            using var http = new HttpClient();
            // Sturdy enough to outlast the swings while the test watches.
            (await http.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
            return bot;
        }

        public Task<GameSession> SessionOf(BotClient bot) =>
            OnLoop(() => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null));

        /// <summary>A monster <paramref name="dx"/> metres beside the player, inside its view (AIConfig _iViewRange 500).</summary>
        public Task<MonsterUnit> SpawnBeside(GameSession s, int npcId, float dx) =>
            Service<GameLoop>().InvokeAsync(async () =>
                (await Service<MonsterService>().SpawnSingleAsync(s.RuntimeMap, npcId,
                    new MapPoint(s.Position.X + dx, s.Position.Y, s.Position.Z, 0)))!);

        public async Task Until(Func<bool> condition, string what)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!await OnLoop(condition))
            {
                if (elapsed.Elapsed >= Wait) throw new TimeoutException(what);
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataDir, true); } catch (IOException) { }
        }
    }
}
