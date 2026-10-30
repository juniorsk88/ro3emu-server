using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Direct trade, sends and the account vault over the real game protocol (docs/systems/player-trade-vault.md).</summary>
[Collection("Market server")]
public sealed class PlayerTradeTests : IAsyncLifetime
{
    private const int Plain = 1000100, Other = 1000000;
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ptrade-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock clock = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private long Id(BotClient bot) => (long)bot.Self!.Uuid;
    private List<InvItem> Items(BotClient bot) => Service<ItemStore>().Load(Id(bot));
    private int Count(BotClient bot, int entry) => Items(bot).Where(i => i.Entry == entry).Sum(i => i.Stack);
    private long Zeny(BotClient bot) => Service<RoleStore>().LoadProgress(Id(bot)).Currency.GetValueOrDefault(1);

    public async Task InitializeAsync()
    {
        var (port, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        }, clock);
        await app.StartAsync(); http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private Task Up(BotClient bot, object request) =>
        bot.SendGameBytesAsync(PlayerTradeService.UpMsg, JsonSerializer.SerializeToUtf8Bytes(request));

    private static async Task<JsonElement> Down(BotClient bot, Func<JsonElement, bool> match)
    {
        var entry = await bot.Inbox.WaitAsync(e => e.IsGame(PlayerTradeService.DownMsg) && match(JsonDocument.Parse(e.Body).RootElement), Wait);
        return JsonDocument.Parse(entry.Body).RootElement.Clone();
    }

    private static bool Is(JsonElement e, string t) => e.GetProperty("t").GetString() == t;
    private static bool Open(JsonElement e, Func<JsonElement, bool>? more = null) =>
        Is(e, "trade") && e.GetProperty("state").GetString() == "open" && (more == null || more(e));
    private static bool Msg(JsonElement e, bool ok) => Is(e, "msg") && e.GetProperty("ok").GetBoolean() == ok;

    private async Task<long> GuidOf(BotClient bot, int entry)
    {
        await Up(bot, new { op = "bag" });
        var bag = await Down(bot, e => Is(e, "bag") && e.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("entry").GetInt32() == entry));
        return bag.GetProperty("items").EnumerateArray().First(i => i.GetProperty("entry").GetInt32() == entry).GetProperty("guid").GetInt64();
    }

    private JsonElement Letters(long role) =>
        JsonDocument.Parse(Service<RoleStore>().LoadMailbox(role)!).RootElement.GetProperty("Letters").Clone();

    [ClientDataFact]
    public async Task TwoPlayersSwapGoodsAndZenyThroughMailOnceBothAreReady()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, $"!item {Plain} 5"); await Gm(alice, "!zeny 1000");
        await Gm(bob, $"!item {Other} 3");
        long aliceItem = await GuidOf(alice, Plain), bobItem = await GuidOf(bob, Other);
        long zenyBefore = Zeny(alice);
        int plainBefore = Count(alice, Plain), otherBefore = Count(bob, Other);

        await Up(alice, new { op = "trade.invite", name = "Bob" });
        await Down(bob, e => Is(e, "trade") && e.GetProperty("state").GetString() == "invited" && e.GetProperty("incoming").GetBoolean());
        await Up(bob, new { op = "trade.accept" });
        await Down(alice, e => Open(e));

        await Up(alice, new { op = "trade.offer", items = new[] { new { guid = aliceItem, n = 2 } }, zeny = 500 });
        await Up(bob, new { op = "trade.offer", items = new[] { new { guid = bobItem, n = 1 } }, zeny = 0 });
        var seen = await Down(alice, e => Open(e, x => x.GetProperty("theirs").GetProperty("items").GetArrayLength() == 1
            && x.GetProperty("mine").GetProperty("zeny").GetInt64() == 500));
        Assert.Equal(500, seen.GetProperty("mine").GetProperty("zeny").GetInt64());

        // Too soon after the last change: not accepted.
        await Up(alice, new { op = "trade.ready", on = true });
        Assert.False((await Down(alice, e => Open(e)))
            .GetProperty("mine").GetProperty("ready").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(3));
        await Up(alice, new { op = "trade.ready", on = true });
        await Down(alice, e => Open(e, x => x.GetProperty("mine").GetProperty("ready").GetBoolean()));
        // A changed offer clears both flags.
        await Up(bob, new { op = "trade.offer", items = new[] { new { guid = bobItem, n = 2 } }, zeny = 0 });
        Assert.False((await Down(alice, e => Open(e, x => x.GetProperty("theirs").GetProperty("items").GetArrayLength() == 1 && x.GetProperty("theirs").GetProperty("items")[0].GetProperty("stack").GetInt32() == 2)))
            .GetProperty("mine").GetProperty("ready").GetBoolean());
        clock.Advance(TimeSpan.FromSeconds(3));
        await Up(alice, new { op = "trade.ready", on = true });
        await Up(bob, new { op = "trade.ready", on = true });
        await Down(alice, e => Is(e, "done"));
        await Down(bob, e => Is(e, "done"));

        Assert.Equal(plainBefore - 2, Count(alice, Plain)); Assert.Equal(otherBefore - 2, Count(bob, Other));
        Assert.Equal(zenyBefore - 500, Zeny(alice));
        var toBob = Letters(Id(bob)).EnumerateArray().Single(l => l.GetProperty("Title").GetString()!.StartsWith("Troca com Alice"));
        Assert.Equal(2, toBob.GetProperty("Items")[0].GetProperty("Stack").GetInt32());
        Assert.Equal(500, toBob.GetProperty("Currency").GetProperty("1").GetInt64());
        var toAlice = Letters(Id(alice)).EnumerateArray().Single(l => l.GetProperty("Title").GetString()!.StartsWith("Troca com Bob"));
        Assert.Equal(2, toAlice.GetProperty("Items")[0].GetProperty("Stack").GetInt32());
        Assert.Contains("\"trade\"", File.ReadAllText(Path.Combine(dataDir, "player-trade-log.jsonl")));
    }

    [ClientDataFact]
    public async Task ATradeRefusesUnownedItemsAndTheOtherPlayerCanDecline()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(bob, $"!item {Other} 1");
        long bobItem = await GuidOf(bob, Other);
        int otherBefore = Count(bob, Other);
        await Up(alice, new { op = "trade.invite", name = "Bob" });
        await Down(bob, e => Is(e, "trade") && e.GetProperty("state").GetString() == "invited");
        await Up(bob, new { op = "trade.decline" });
        await Down(alice, e => Msg(e, false));
        await Up(alice, new { op = "trade.invite", name = "Bob" });
        await Down(bob, e => Is(e, "trade") && e.GetProperty("state").GetString() == "invited");
        await Up(bob, new { op = "trade.accept" });
        await Down(alice, e => Open(e));
        await Up(alice, new { op = "trade.offer", items = new[] { new { guid = bobItem, n = 1 } }, zeny = 0 }); // Bob's item
        await Down(alice, e => Msg(e, false));
        await Up(alice, new { op = "trade.cancel" });
        await Down(bob, e => Is(e, "trade") && e.GetProperty("state").GetString() == "none");
        Assert.Equal(otherBefore, Count(bob, Other));
    }

    [ClientDataFact]
    public async Task ASendReachesAnOfflineCharacterByMail()
    {
        await using var alice = await InWorld("Alice");
        long carol;
        await using (var c = await InWorld("Carol")) carol = Id(c);
        await Gm(alice, $"!item {Plain} 4"); await Gm(alice, "!zeny 300");
        long item = await GuidOf(alice, Plain);
        int plainBefore = Count(alice, Plain);
        await Up(alice, new { op = "send", name = "Carol", items = new[] { new { guid = item, n = 3 } }, zeny = 100 });
        await Down(alice, e => Is(e, "done"));
        Assert.Equal(plainBefore - 3, Count(alice, Plain));
        var letter = Letters(carol).EnumerateArray().Single(l => l.GetProperty("Title").GetString()!.StartsWith("Envio de Alice"));
        Assert.Equal(3, letter.GetProperty("Items")[0].GetProperty("Stack").GetInt32());
        Assert.Equal(100, letter.GetProperty("Currency").GetProperty("1").GetInt64());
        await Up(alice, new { op = "send", name = "Nobody", items = new[] { new { guid = item, n = 1 } }, zeny = 0 });
        await Down(alice, e => Msg(e, false));
    }

    [ClientDataFact]
    public async Task TheVaultKeepsGoodsPerAccountAndHandsThemBackByMail()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, $"!item {Plain} 4"); await Gm(alice, "!zeny 700");
        long item = await GuidOf(alice, Plain);
        int plainBefore = Count(alice, Plain);
        await Up(alice, new { op = "vault.put", items = new[] { new { guid = item, n = 3 } }, zeny = 200 });
        var vault = await Down(alice, e => Is(e, "vault") && e.GetProperty("items").GetArrayLength() == 1);
        Assert.Equal(200, vault.GetProperty("zeny").GetInt64());
        Assert.Equal(3, vault.GetProperty("items")[0].GetProperty("stack").GetInt32());
        Assert.Equal(plainBefore - 3, Count(alice, Plain));
        long id = vault.GetProperty("items")[0].GetProperty("id").GetInt64();
        await Up(alice, new { op = "vault.take", items = new[] { new { id, n = 2 } }, zeny = 50 });
        var after = await Down(alice, e => Is(e, "vault") && e.GetProperty("zeny").GetInt64() == 150);
        Assert.Equal(1, after.GetProperty("items")[0].GetProperty("stack").GetInt32());
        var letter = Letters(Id(alice)).EnumerateArray().Single(l => l.GetProperty("Title").GetString() == "Retirada do cofre");
        Assert.Equal(2, letter.GetProperty("Items")[0].GetProperty("Stack").GetInt32());
        Assert.Equal(50, letter.GetProperty("Currency").GetProperty("1").GetInt64());
        await Up(alice, new { op = "vault.take", items = new[] { new { id, n = 5 } }, zeny = 0 });
        await Down(alice, e => Msg(e, false));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
