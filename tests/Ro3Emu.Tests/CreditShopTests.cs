using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// In-game purchases paid with the web shop's credits (ServerOptions.CreditShop): the game button makes a pending order and opens the
/// approval page; the panel's approval (debit + status aprovado, simulated here in SQL) is what CreditShop.DeliverApprovedAsync delivers.
/// </summary>
public sealed class CreditShopTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-credits-" + Guid.NewGuid().ToString("N"));
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
            o.CreditShop = true;
            o.StoreUrl = "https://shop.example.test/painel?tab=shop";
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        // the admin panel owns these tables; a bare server has none
        using var db = Open();
        Exec(db, "CREATE TABLE panel_credit(account text primary key collate nocase, amount real not null default 0, updated text)");
        Exec(db, "CREATE TABLE panel_ledger(id integer primary key autoincrement, kind text not null, account text not null collate nocase, role_id integer default 0, " +
                 "role_name text default '', entry integer default 0, product text default '', price integer default 0, currency integer default 0, " +
                 "credits real default 0, balance_after real, status text default 'ok', actor text default '', created text)");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")};Pooling=False");
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    private double Credits(string account)
    {
        using var db = Open();
        return Convert.ToDouble(Scalar(db, "SELECT amount FROM panel_credit WHERE account = $a", ("$a", account)) ?? 0.0);
    }

    private void SetCredits(string account, double amount)
    {
        using var db = Open();
        Exec(db, "INSERT INTO panel_credit(account, amount) VALUES ($a, $v) ON CONFLICT(account) DO UPDATE SET amount = $v", ("$a", account), ("$v", amount));
    }

    private int OrderCount()
    {
        using var db = Open();
        return Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM panel_order"));
    }

    /// <summary>Delivers the approved order. The server's own 2 s delivery round may take it first, so what counts is that it ends up delivered (once).</summary>
    private async Task DeliverAsync(CreditShop shop, string orderId)
    {
        await shop.DeliverApprovedAsync();
        for (int i = 0; i < 100 && OrderStatus(orderId) != "entregue"; i++) await Task.Delay(50);
        Assert.Equal("entregue", OrderStatus(orderId));
    }

    private string OrderStatus(string id)
    {
        using var db = Open();
        return (string)Scalar(db, "SELECT status FROM panel_order WHERE id = $i", ("$i", id))!;
    }

    /// <summary>What the panel does when the player approves on the site: debit, ledger row, status aprovado.</summary>
    private void ApproveLikeThePanel(string orderId, string account, double price)
    {
        using var db = Open();
        double balance = Convert.ToDouble(Scalar(db, "SELECT amount FROM panel_credit WHERE account = $a", ("$a", account)))  - price;
        Exec(db, "UPDATE panel_credit SET amount = $b WHERE account = $a", ("$b", balance), ("$a", account));
        Exec(db, "INSERT INTO panel_ledger(kind, account, product, credits, balance_after, status, actor, created) VALUES ('purchase', $a, 'x', $p, $b, 'aguardando entrega', 'site', 'now')",
            ("$a", account), ("$p", price), ("$b", balance));
        long ledger = Convert.ToInt64(Scalar(db, "SELECT last_insert_rowid()"));
        Exec(db, "UPDATE panel_order SET status = 'aprovado', ledger_id = $l, balance_after = $b WHERE id = $i", ("$l", ledger), ("$b", balance), ("$i", orderId));
    }

    private async Task<(BotClient Bot, GameSession Session)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name, 1)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == id));
    }

    private static async Task<CreateOrderResp> Order(BotClient bot, long chargeType, long chargeId)
    {
        await bot.SendGameAsync(2001, new CreateOrderReq { ChargeType = chargeType, ChargeId = chargeId, Amount = 1 });
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(2001), TimeSpan.FromSeconds(10));
        return CreateOrderResp.Parser.ParseFrom(message.Body);
    }

    private static string OrderIdOf(CreateOrderResp reply)
    {
        string url = JsonDocument.Parse(reply.OrderData).RootElement.GetProperty("data").GetProperty("transaction_url").GetString()!;
        Assert.StartsWith("https://shop.example.test/painel?tab=shop&approve=", url);
        return url[(url.IndexOf("approve=", StringComparison.Ordinal) + "approve=".Length)..];
    }

    [Fact]
    public async Task CrystalPackWaitsForTheApprovalThenIsDelivered()
    {
        var (bot, session) = await InWorld("buyer");
        await using var _ = bot;
        var character = _app.Services.GetRequiredService<CharacterService>();
        var shop = _app.Services.GetRequiredService<CreditShop>();
        SetCredits("buyer", 20);

        var reply = await Order(bot, chargeType: 1, chargeId: 15); // PC pack: 14.99 USD, 980 crystals + 110 bonus
        Assert.Equal(0, reply.Result);
        Assert.StartsWith("credits-", reply.OrderId);
        string id = OrderIdOf(reply);
        Assert.Equal("pendente", OrderStatus(id));
        // nothing is charged or given before the player approves on the site
        Assert.Equal(20, Credits("buyer"), 2);
        Assert.Equal(0, character.CountOf(session, 3));
        Assert.Equal(0, await shop.DeliverApprovedAsync());

        ApproveLikeThePanel(id, "buyer", 14.99);
        await DeliverAsync(shop, id);
        Assert.Equal(980, character.CountOf(session, 3));
        Assert.Equal(110, character.CountOf(session, 16));
        Assert.Equal(5.01, Credits("buyer"), 2);
        Assert.Equal("entregue", OrderStatus(id));
        Assert.Equal(0, await shop.DeliverApprovedAsync()); // once only

        using var db = Open();
        Assert.Equal("entregue", Scalar(db, "SELECT status FROM panel_ledger"));
    }

    [Fact]
    public async Task WithoutBalanceNoOrderIsCreated()
    {
        var (bot, session) = await InWorld("broke");
        await using var _ = bot;
        var character = _app.Services.GetRequiredService<CharacterService>();
        SetCredits("broke", 3.2);

        var reply = await Order(bot, chargeType: 1, chargeId: 15);
        Assert.Equal(ErrorCodes.CurrencyShort, reply.Result);
        Assert.Contains("insuficientes", reply.ErrMsg);
        Assert.Equal(3.2, Credits("broke"), 2);
        Assert.Equal(0, OrderCount());
        Assert.Equal(0, character.CountOf(session, 3));
    }

    [Fact]
    public async Task MonthCardProductIsAnOrderAtItsPriceAndUnknownOnesAreRefused()
    {
        var (bot, _) = await InWorld("passer");
        await using var _bot = bot;
        SetCredits("passer", 5);

        var reply = await Order(bot, chargeType: 2, chargeId: 4); // ProductConfig 4: standard month card, 1.45 USD
        Assert.Equal(0, reply.Result);
        string id = OrderIdOf(reply);
        using (var db = Open()) Assert.Equal(1.45, Convert.ToDouble(Scalar(db, "SELECT price FROM panel_order WHERE id = $i", ("$i", id))), 2);
        Assert.Equal(5, Credits("passer"), 2);

        var unknown = await Order(bot, chargeType: 2, chargeId: 99999);
        // not a product the credit shop sells: the button just opens the shop (no approval link), no order
        Assert.DoesNotContain("approve=", JsonDocument.Parse(unknown.OrderData).RootElement.GetProperty("data").GetProperty("transaction_url").GetString());
        Assert.Equal(1, OrderCount());
    }

    [Fact]
    public async Task QuickBuyOrdersBothMonthCardTokens()
    {
        var (bot, session) = await InWorld("quick");
        await using var _ = bot;
        var character = _app.Services.GetRequiredService<CharacterService>();
        var shop = _app.Services.GetRequiredService<CreditShop>();
        SetCredits("quick", 10);

        var reply = await Order(bot, chargeType: 2, chargeId: 6); // ProductConfig 6: "Compra rapida", 5.90 USD, both cards
        Assert.Equal(0, reply.Result);
        string id = OrderIdOf(reply);
        using (var db = Open()) Assert.Equal(5.9, Convert.ToDouble(Scalar(db, "SELECT price FROM panel_order WHERE id = $i", ("$i", id))), 2);
        ApproveLikeThePanel(id, "quick", 5.9);
        await DeliverAsync(shop, id);
        // both tokens are in the bag or already turned into days by the activity service
        Assert.Equal(4.1, Credits("quick"), 2);
        Assert.Equal("entregue", OrderStatus(id));
    }

    [Fact]
    public async Task ApprovedOrderOfAPlayerWhoLeftGoesThroughTheDeliveryQueue()
    {
        var (bot, _) = await InWorld("leaver");
        await using var _bot = bot;
        var shop = _app.Services.GetRequiredService<CreditShop>();
        SetCredits("leaver", 20);
        string id = OrderIdOf(await Order(bot, chargeType: 1, chargeId: 15));
        ApproveLikeThePanel(id, "leaver", 14.99);
        using (var db = Open()) Exec(db, "UPDATE panel_order SET role_id = 987654321 WHERE id = $i", ("$i", id)); // that role is not in the game

        await DeliverAsync(shop, id);
        using var check = Open();
        Assert.Equal(2L, Scalar(check, "SELECT COUNT(*) FROM panel_delivery WHERE role_id = 987654321 AND kind = 'currency' AND applied = 0"));
        Assert.Equal(980L, Scalar(check, "SELECT amount FROM panel_delivery WHERE currency = 3"));
        Assert.Equal("aguardando entrega", Scalar(check, "SELECT status FROM panel_ledger"));
        Assert.Equal("entregue", OrderStatus(id));
    }

    [Fact]
    public async Task OldPendingOrdersExpire()
    {
        var (bot, _) = await InWorld("slow");
        await using var _bot = bot;
        var shop = _app.Services.GetRequiredService<CreditShop>();
        SetCredits("slow", 20);
        string id = OrderIdOf(await Order(bot, chargeType: 1, chargeId: 15));
        using (var db = Open()) Exec(db, "UPDATE panel_order SET expires_at = 1 WHERE id = $i", ("$i", id));
        await shop.DeliverApprovedAsync();
        Assert.Equal("expirado", OrderStatus(id));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
