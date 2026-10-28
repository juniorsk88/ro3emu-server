using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:PlotStallSeconds: a role back at login with a story task still waiting for its cutscene has it counted as played.</summary>
public sealed class PlotStallTests : IAsyncLifetime
{
    private const int PlotTask = 10105050; // "Strange Occurences": [309002, 130061]
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3emu-plotstall-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, clock);
        SureHitRolls.Install(app);
        await app.StartAsync(); http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    [ClientDataFact]
    public async Task ARoleBackWithAStuckCutsceneTaskGoesOn()
    {
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("plotstall", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("PlotStall")).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(15));
        using (var client = new HttpClient())
            (await client.PostAsync($"{http}/ro3emu/gm?account=plotstall", new StringContent($"!quest accept {PlotTask}"))).EnsureSuccessStatusCode();
        var session = Service<SessionRegistry>().All().Single(s => s.Account == "plotstall" && s.Role != null);
        var tasks = Service<TaskService>();
        Assert.Equal(TaskState.Running, session.Tasks!.Infos[PlotTask].Status);

        // Back at once (a restart, a reconnect): the cutscene was probably never shown, so it stays.
        await OnLoop(() => tasks.LoginAsync(session));
        Assert.Equal(TaskState.Running, session.Tasks!.Infos[PlotTask].Status);

        // Back after the cutscene had time to play: counted as played, the story goes on.
        clock.Advance(TimeSpan.FromSeconds(60));
        await OnLoop(() => tasks.LoginAsync(session));
        Assert.Equal(TaskState.Delivery, session.Tasks!.Infos[PlotTask].Status);
        Assert.Contains(session.Tasks.Infos.Values, i => i.TaskId == 10105060 && i.Status == TaskState.Running);
    }

    [ClientDataFact]
    public async Task TheSwitchTurnsItOff()
    {
        Service<IOptions<ServerOptions>>().Value.PlotStallSeconds = 0;
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("plotstall2", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("PlotStallTwo")).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(15));
        using (var client = new HttpClient())
            (await client.PostAsync($"{http}/ro3emu/gm?account=plotstall2", new StringContent($"!quest accept {PlotTask}"))).EnsureSuccessStatusCode();
        var session = Service<SessionRegistry>().All().Single(s => s.Account == "plotstall2" && s.Role != null);
        clock.Advance(TimeSpan.FromSeconds(600));
        await OnLoop(() => Service<TaskService>().LoginAsync(session));
        Assert.Equal(TaskState.Running, session.Tasks!.Infos[PlotTask].Status);
    }
}
