using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The month-card tokens a credit-shop purchase puts in the bag turn into days by themselves (no relog, no panel reopening).</summary>
public sealed class CreditShopMonthCardTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-mc-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.CreditShop = true;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static long Days(GameSession session, long card) =>
        session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos.TryGetValue(card, out var info) ? info.LastDays : 0;

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 60 && !done(); i++) await Task.Delay(100);
    }

    [ClientDataFact]
    public async Task EachTokenBoughtLaterBecomesDaysOnItsOwn()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var _ = bot;
        await bot.LoginAsync("mc", "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Mc", 1)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        await Gm(bot, "!lvl 20");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == id);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await _app.Services.GetRequiredService<NewActivityService>().ReconcileAsync(session);

        await loop.InvokeAsync(() => character.GrantAsync(session, [(701, 1)]));
        await Until(() => Days(session, 1001) > 0);
        Assert.True(Days(session, 1001) > 0, "standard card: token not converted");

        await loop.InvokeAsync(() => character.GrantAsync(session, [(702, 1)]));
        await Until(() => Days(session, 1002) > 0);
        Assert.True(Days(session, 1002) > 0, "premium card: token not converted");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
