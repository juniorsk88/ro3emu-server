using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:PartyKillShare: a party shares a kill's EXP and its kill-quest credit; items stay with the killer.</summary>
public sealed class PartyKillShareTests : IAsyncLifetime
{
    private const int KillTask = 182050, KillNpc = 5000010; // TaskConfig: [104002, 5000010, 10]
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3emu-partyshare-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        SureHitRolls.Install(app);
        await app.StartAsync(); http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), Wait);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task Party(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = TeamCreateResp.Parser.ParseFrom((await leader.Inbox.WaitAsync(e => e.IsGame(TeamService.TeamCreate), Wait)).Body);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        await member.Inbox.WaitAsync(e => e.IsGame(TeamService.TeamNotifyInvitee), Wait);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        var joined = InviteeRespResp.Parser.ParseFrom((await member.Inbox.WaitAsync(e => e.IsGame(TeamService.TeamInviteeResp), Wait)).Body);
        Assert.Equal(0, joined.Result);
    }

    private static MonsterUnit Monster(GameSession where, int npc, long uid) => new()
    {
        Uid = uid, SceneEntry = where.SceneEntry, RuntimeMap = where.RuntimeMap,
        Template = new MonsterTemplate(npc, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, npc), Hp = 0
    };

    private Task Reward(GameSession player, MonsterUnit monster) => OnLoop(() => Service<CombatService>().Killed
        .Single(h => h.Target == Service<RewardService>())(player, monster));

    private static (int Level, long Exp) Exp(GameSession s) => (s.Progress!.Level, s.Progress.Exp);
    private static long Progress(GameSession s) => s.Tasks!.Infos[KillTask].CompleteNum[0];

    [ClientDataFact]
    public async Task PartyMembersShareTheKillExpAndKillQuestCredit()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await OnLoop(() =>
        {
            Service<RewardService>().ResolveKillForTest = (_, _) => ([], 3, 3);
            return Task.CompletedTask;
        });
        await Gm(bob, $"!quest accept {KillTask}");
        var a = Session(alice); var b = Session(bob);
        Assert.Equal(0, Progress(b));

        // Not in a party: only the killer is rewarded.
        var before = Exp(b);
        await Reward(a, Monster(a, KillNpc, 901));
        Assert.Equal(before, Exp(b));
        Assert.Equal(0, Progress(b));

        // In a party: Bob gets the EXP and the kill for his running quest, without hitting the monster.
        await Party(alice, bob);
        var aliceBefore = Exp(a);
        await Reward(a, Monster(a, KillNpc, 902));
        Assert.NotEqual(before, Exp(b));
        Assert.NotEqual(aliceBefore, Exp(a));
        Assert.Equal(1, Progress(b));

        // A monster Bob's quest does not ask for still gives him EXP but no quest credit.
        before = Exp(b);
        await Reward(a, Monster(a, 10001, 903));
        Assert.NotEqual(before, Exp(b));
        Assert.Equal(1, Progress(b));

        // Out of range or with the switch off, Bob gets nothing.
        var options = Service<IOptions<ServerOptions>>().Value;
        options.PartyKillShare = false;
        try
        {
            before = Exp(b);
            await Reward(a, Monster(a, KillNpc, 904));
            Assert.Equal(before, Exp(b));
            Assert.Equal(1, Progress(b));
        }
        finally { options.PartyKillShare = true; }
        options.PartyKillShareRange = 5;
        try
        {
            await OnLoop(() => { b.Position = new Vec3 { X = a.Position.X + 500, Y = a.Position.Y, Z = a.Position.Z }; return Task.CompletedTask; });
            before = Exp(b);
            await Reward(a, Monster(a, KillNpc, 905));
            Assert.Equal(before, Exp(b));
            Assert.Equal(1, Progress(b));
        }
        finally { options.PartyKillShareRange = 0; }

        // More than 20 levels apart from the killer: no share either way.
        await OnLoop(() => { b.Position = new Vec3 { X = a.Position.X, Y = a.Position.Y, Z = a.Position.Z }; return Task.CompletedTask; });
        await Gm(bob, "!lvl 40");
        before = Exp(b);
        await Reward(a, Monster(a, KillNpc, 906));
        Assert.Equal(before, Exp(b));
        Assert.Equal(1, Progress(b));
        await Gm(bob, "!lvl 15");
        await Reward(a, Monster(a, KillNpc, 907));
        Assert.Equal(2, Progress(b));
    }
}
