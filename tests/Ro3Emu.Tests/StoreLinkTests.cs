using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>An in-game purchase button (CreateOrderReq) points the PC client at the server's web shop when ServerOptions.StoreUrl is set.</summary>
public sealed class StoreLinkTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-store-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.StoreUrl = "https://shop.example.test/painel?tab=shop";
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name, 1)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<CreateOrderResp> Order(BotClient bot, long chargeType, long chargeId)
    {
        await bot.SendGameAsync(2001, new CreateOrderReq { ChargeType = chargeType, ChargeId = chargeId, Amount = 1 });
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(2001), TimeSpan.FromSeconds(5));
        return CreateOrderResp.Parser.ParseFrom(message.Body);
    }

    private static string TransactionUrl(CreateOrderResp reply) =>
        JsonDocument.Parse(reply.OrderData).RootElement.GetProperty("data").GetProperty("transaction_url").GetString()!;

    [Fact]
    public async Task PurchaseButtonsOpenTheWebShop()
    {
        await using var bot = await InWorld("StoreLink");

        var recharge = await Order(bot, chargeType: 1, chargeId: 15);
        Assert.Equal(0, recharge.Result);
        Assert.Equal(15, recharge.ChargeId);
        Assert.StartsWith("shop-", recharge.OrderId);
        Assert.Equal("https://shop.example.test/painel?tab=shop&cat=Cristais", TransactionUrl(recharge));

        var product = await Order(bot, chargeType: 2, chargeId: 4);
        Assert.Equal(0, product.Result);
        Assert.Equal("https://shop.example.test/painel?tab=shop&cat=Passes", TransactionUrl(product));
        Assert.NotEqual(recharge.OrderId, product.OrderId);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
