using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The refine resonance level the 5007 dataset carries for the client's resonance panel.</summary>
public sealed class RefineResonanceTests : IAsyncLifetime
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-refine-res-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
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

    [ClientDataFact]
    public async Task SevenGridsAtPlusElevenGiveResonanceLevelThreeAndTheClientIsToldSo()
    {
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("resonance", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Resonance")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        var character = Service<CharacterService>();
        session.EquipLevels = new EquipLevels();
        for (int g = 1; g <= 7; g++) session.EquipLevels.Refine[g] = 11;
        session.EquipLevels.Refine[1] = 14;
        Assert.Equal(3, character.Resonance(session, "RefiningEffectTable"));

        await character.EquipLevelsChangedAsync(session, persist: false);
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.EquipSync)
            && DBEquipInfoIncrement.Parser.ParseFrom(e.Body) is { HasRefineEffectLv: true, RefineEffectLv: 3 }, TimeSpan.FromSeconds(5));
    }

    [ClientDataFact]
    public async Task ALoginSendsTheResonanceLevelInTheFullEquipmentSync()
    {
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("relogin", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Relogin")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        session.EquipLevels = new EquipLevels();
        for (int g = 1; g <= 7; g++) session.EquipLevels.Refine[g] = 11;
        await Service<CharacterService>().EquipLevelsChangedAsync(session, persist: true);
        await bot.DisposeAsync();

        await using var again = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await again.LoginAsync("relogin", "pw");
        await again.EnterGateAsync();
        await again.EnterWorldAsync(again.Roles.Single().Id);
        var full = again.Inbox.Snapshot().Where(e => e.IsGame(CharacterService.EquipSync))
            .Select(e => DBEquipInfoIncrement.Parser.ParseFrom(e.Body)).First(m => m.SyncAllMark);
        Assert.True(full.ItemsRefine.ContainsKey(1));
        Assert.True(full.HasRefineEffectLv, "the full 5007 carries no refineEffectLv");
        Assert.Equal(3, full.RefineEffectLv);
    }

    [ClientDataFact]
    public async Task WearingOrRemovingGearKeepsTheResonanceLevelInTheReplyTheClientCopies()
    {
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("wearer", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Wearer")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        session.EquipLevels = new EquipLevels();
        for (int g = 1; g <= 7; g++) session.EquipLevels.Refine[g] = 11;
        var worn = session.Items.First(i => i.Pack == 2);

        foreach (var remove in new[] { false, true })
        {
            int seen = bot.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.EquipItemOpr));
            await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = worn.Guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)(remove ? 1 : 0) });
            var reply = await bot.Inbox.WaitAsync(p => p.IsGame(CharacterService.EquipItemOpr) && bot.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.EquipItemOpr)) > seen, TimeSpan.FromSeconds(5));
            var rsp = EquipItemOprResp.Parser.ParseFrom(reply.Body);
            Assert.Equal(0, rsp.Result);
            Assert.Equal(3, rsp.RefineEffectLv);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
