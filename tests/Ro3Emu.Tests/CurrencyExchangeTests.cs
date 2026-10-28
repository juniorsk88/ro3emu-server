using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>ItemExchangeReq 2005, the currency exchange window (ItemExchangeConfig rows 1 and 3).</summary>
public sealed class CurrencyExchangeTests : IAsyncLifetime
{
    private const int Zeny = 1, Minted = 2;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-currencyexchange-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";

    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.SeasonStart = "2026-10-08";
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private long Owned(BotClient bot, int entry) => Service<CharacterService>().CountOf(Session(bot), entry);

    private static async Task<long> Exchange(BotClient bot, long itemId, long count)
    {
        await bot.SendGameAsync(2005, new ItemExchangeReq { ItemId = itemId, Count = count });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(2005), Wait);
        return ItemExchangeResp.Parser.ParseFrom(e.Body).Result;
    }

    [ClientDataFact]
    public async Task ExchangePaysTheCostAndGivesTheRateTimesTheCount()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!zeny 5000");
        long zeny = Owned(alice, Zeny), minted = Owned(alice, Minted);
        Assert.True(zeny >= 5000);

        // The window's own example: 2 paid, 200 received (rate 100).
        Assert.Equal(0, await Exchange(alice, Zeny, 2));
        Assert.Equal(zeny - 2, Owned(alice, Zeny));
        Assert.Equal(minted + 200, Owned(alice, Minted));
    }

    [ClientDataFact]
    public async Task AShortBalanceOrABadRequestChangesNothing()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        long zeny = Owned(alice, Zeny), minted = Owned(alice, Minted);

        Assert.Equal(ErrorCodes.CurrencyShort, await Exchange(alice, Zeny, zeny + 1));
        Assert.Equal(ErrorCodes.Parameter, await Exchange(alice, Zeny, 0));
        Assert.Equal(ErrorCodes.Parameter, await Exchange(alice, Zeny, -5));
        Assert.Equal(ErrorCodes.Parameter, await Exchange(alice, Zeny, 1_000_000)); // over _iSingleLimit
        Assert.Equal(ErrorCodes.Parameter, await Exchange(alice, 99, 1));           // no ItemExchangeConfig row
        Assert.Equal(zeny, Owned(alice, Zeny));
        Assert.Equal(minted, Owned(alice, Minted));
    }
}
