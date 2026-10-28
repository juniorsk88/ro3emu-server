using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Rules this realm adds on the data: the optional level and career a worn piece asks for, the shields player skills grant (Kyrie Eleison
/// absorbs Physical only), and buffs that keep counting down while their owner is logged out.
/// </summary>
public sealed class RealmRulesTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-realm-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.EquipRequirements = true;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
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
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    [ClientDataFact]
    public async Task AWornPieceNeedsItsLevelWhenTheRealmEnforcesIt()
    {
        await using var bot = await InWorld("Wearer");
        long gear = GameData.Int(Service<GameData>().Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 5
            && GameData.Int(row, "_iEquipPart") == 4 && GameData.Int(row, "_iLevelNeed") >= 20), "_iID");
        await Gm(bot, $"!item {gear} 1");
        long guid = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Items.First(i => i.Entry == gear).Guid));
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)3 });
        Assert.Equal(CharacterService.ErrLevelNeed, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);

        await Gm(bot, "!lvl 99");
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)3 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task KyrieEleisonGrantsAMatkShieldThatOnlyAbsorbsPhysicalDamage()
    {
        await using var bot = await InWorld("Acolyte");
        var data = Service<GameData>();
        var combat = Service<CombatService>();
        var buffs = Service<BuffService>();
        var kyrie = data.Row("SkillConfig", 1411701)!.Value;
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var s = Session(bot);
            var stats = Service<CharacterService>().Stats(s);
            Assert.Equal((long)Math.Floor((double)stats[104] * 40 / 100), combat.SkillShield("Kyrie_Eleison", kyrie, stats)); // "equal to your Magic ATK * ${2}%"
            Assert.Equal(0, combat.SkillShield("Heal", kyrie, stats)); // other scripts grant none
            await buffs.AddSupportAsync(s, 1411701, 10000, s.Role!.Id, shieldAmount: 100);
            Assert.Equal(50, await buffs.AbsorbAsync(s, 50, magic: true)); // Magic damage passes the Physical-only shield
            Assert.Equal(0, await buffs.AbsorbAsync(s, 60)); // Physical damage is absorbed
            Assert.Equal(40, buffs.ShieldAmount(s));
        });
    }

    [ClientDataFact]
    public async Task FoodKeepsCountingDownWhileTheOwnerIsAway()
    {
        const int Food = 982301; // "Creamy Tomato Soup", BuffConfig._iOffline 2, two hours
        await using var bot = await InWorld("Eater");
        var buffs = Service<BuffService>();
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var s = Session(bot);
            await buffs.AddAsync(s, Food, 7_200_000, s.Role!.Id);
            await buffs.AddAsync(s, 1130109, 60_000, s.Role.Id); // a timed buff without the flag stays behind
            await buffs.SaveOfflineAsync(s);
            foreach (var buff in s.Buffs.Where(b => b.ConfigId is Food or 1130109).ToArray()) await buffs.RemoveAsync(s, buff);
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == Food);
            await buffs.RestoreOfflineAsync(s);
            var back = Assert.Single(s.Buffs, b => b.ConfigId == Food);
            Assert.InRange(back.ExpireMs - MonsterService.NowMs, 7_190_000, 7_200_000);
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == 1130109);
            await buffs.RestoreOfflineAsync(s); // the file is used up
            Assert.Single(s.Buffs, b => b.ConfigId == Food);
        });
    }
}
