using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Developer tool, not a test. Films what a real game client draws while a bot casts skills next to it, on a running
/// server (RO3EMU_RIG_HTTP, default http://192.168.1.175:5380). The bot account needs GM commands (!job !lvl !skill
/// !dummy !cd !jump); the GM line goes through the server's loopback tool, so RO3EMU_RIG_GM points at an ssh tunnel
/// (ssh -N -L 15380:127.0.0.1:5380 host). Set RO3EMU_VIDEO_RIG=1; commands are lines appended to &lt;work&gt;/rig/cmd.txt
/// (viewer | wait-client | setup &lt;job&gt; | rec &lt;skillId&gt; | family &lt;name&gt; | gm &lt;line&gt; | stop); answers go to out.log.
/// </summary>
public sealed class VideoRigTests
{
    private static readonly string Work = Environment.GetEnvironmentVariable("RO3EMU_RIG_DIR") ?? @"D:\RO3Emu\work\rig";
    private static readonly string Videos = Environment.GetEnvironmentVariable("RO3EMU_VIDEO_OUT") ?? @"D:\RO3Emu\videos";
    private static readonly string Http = Environment.GetEnvironmentVariable("RO3EMU_RIG_HTTP") ?? "http://192.168.1.175:5380";
    private static readonly string GmHttp = Environment.GetEnvironmentVariable("RO3EMU_RIG_GM") ?? "http://127.0.0.1:15380";
    private static readonly string CasterAccount = Environment.GetEnvironmentVariable("RO3EMU_RIG_CASTER") ?? "videocaster";
    private static readonly string ViewerAccount = Environment.GetEnvironmentVariable("RO3EMU_RIG_VIEWER") ?? "videoviewer";
    private BotClient? caster;
    private GameData data = null!;

    private static bool Enabled => Environment.GetEnvironmentVariable("RO3EMU_VIDEO_RIG") == "1" && ClientData.Available;

    private static void Log(string line)
    {
        Directory.CreateDirectory(Work);
        File.AppendAllText(Path.Combine(Work, "out.log"), $"{DateTime.Now:HH:mm:ss} {line}\n");
    }

    [Fact]
    public async Task Run()
    {
        if (!Enabled) return;
        data = new GameData(Microsoft.Extensions.Options.Options.Create(new Ro3Emu.Server.ServerOptions { DataDir = Work, GameDataDir = Path.Combine(RepoRoot(), "data") }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GameData>.Instance);
        Log("rig up: login " + Http + ", GM " + GmHttp);
        string cmdFile = Path.Combine(Work, "cmd.txt");
        try
        {
            while (true)
            {
                await Task.Delay(500);
                if (!File.Exists(cmdFile)) continue;
                string[] lines;
                try { lines = File.ReadAllLines(cmdFile); File.Delete(cmdFile); } catch (IOException) { continue; }
                foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
                {
                    Log("> " + line);
                    try { if (await Execute(line.Split(' ', StringSplitOptions.RemoveEmptyEntries))) return; }
                    catch (Exception ex) { Log("ERROR " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
        }
        finally { if (caster != null) await caster.DisposeAsync(); }
    }

    private async Task<bool> Execute(string[] a)
    {
        switch (a[0])
        {
            case "stop": Log("stopping"); return true;
            case "viewer": await CreateViewerAsync(); break;
            case "wait-client": await WaitViewerAsync(); break;
            case "setup": await SetupAsync(int.Parse(a[1])); break;
            case "rec": Log("clip: " + await RecordAsync(int.Parse(a[1]))); break;
            case "family": await FamilyAsync(a[1]); break;
            case "gm": Log(await GmAsync(string.Join(' ', a.Skip(1)))); break;
            default: Log("unknown command"); break;
        }
        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }

    /// <summary>Runs a GM line as the caster and returns the marquee text the server answers with.</summary>
    private async Task<string> GmAsync(string line, int waitMs = 700)
    {
        caster ??= await LoginAsync(CasterAccount, "Caster");
        int before = caster.Inbox.Snapshot().Count;
        using var client = new HttpClient();
        (await client.PostAsync($"{GmHttp}/ro3emu/gm?account={CasterAccount}", new StringContent(line))).EnsureSuccessStatusCode();
        await Task.Delay(waitMs);
        return string.Join(" | ", Marquees(before));
    }

    private async Task<BotClient> LoginAsync(string account, string role)
    {
        var bot = new BotClient(Http, File.ReadAllText(Path.Combine(Work, "data", "gate_rsa_public.pem")));
        await bot.LoginAsync(account, "videos"); await bot.EnterGateAsync();
        long id = bot.Roles.FirstOrDefault(r => r.Name == role)?.Id ?? (await bot.CreateRoleAsync(role)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private IEnumerable<string> Marquees(int from) => caster!.Inbox.Snapshot().Skip(from).Where(e => e.IsGame(GmCommands.BroadcastNotify)).Select(e => Printable(e.Body));

    private static string Printable(byte[] body) => new string(Encoding.UTF8.GetString(body).Where(c => !char.IsControl(c)).ToArray());

    /// <summary>The viewer's character is created here, so the real client only has to log in and press enter.</summary>
    private async Task CreateViewerAsync()
    {
        await using var bot = await LoginAsync(ViewerAccount, "Viewer");
        Log($"viewer role ready (client login: {ViewerAccount} / videos)");
    }

    private async Task WaitViewerAsync()
    {
        caster ??= await LoginAsync(CasterAccount, "Caster");
        var until = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < until)
        {
            string online = await GmAsync("!online", 600);
            if (online.Contains("Viewer")) { Log("client in world: " + online); return; }
            await Task.Delay(2000);
        }
        Log("TIMEOUT waiting for the client");
    }

    private async Task SetupAsync(int job)
    {
        caster ??= await LoginAsync(CasterAccount, "Caster");
        Log(await GmAsync("!job " + job)); Log(await GmAsync("!lvl 99", 1500)); Log(await GmAsync("!hp 99999999 99999999"));
        Log(await GmAsync("!where"));
    }

    private async Task FamilyAsync(string family)
    {
        var jobs = family switch
        {
            "mage" => new[] { 1000, 2100, 2200, 2210, 2300, 2400 },
            "swordsman" => [1000, 1100, 1200, 1210, 1300, 1310, 1400, 1410],
            "archer" => [1000, 3100, 3200, 3210, 3211, 3300, 3400],
            "acolyte" => [1000, 4100, 4200, 4210, 4300, 4400],
            "thief" => [1000, 5100, 5200, 5210, 5300, 5400],
            "merchant" => [1000, 6100, 6200, 6300, 6400],
            _ => throw new ArgumentException(family),
        };
        var rows = data.Table("SkillConfig").Values
            .Where(r => jobs.Contains((int)GameData.Int(r, "_iJob")) && GameData.Int(r, "_iMode") == 0 && GameData.Int(r, "_iBasicType") == 1 && GameData.Int(r, "_iExtraType") == 0)
            .GroupBy(r => (int)GameData.Int(r, "_iSkillID")).Select(g => g.OrderByDescending(r => GameData.Int(r, "_iLevel")).First())
            .OrderBy(r => GameData.Int(r, "_iJob")).ThenBy(r => GameData.Int(r, "_iSkillID")).ToList();
        Log($"family {family}: {rows.Count} skills");
        var manifest = new List<object>();
        foreach (var row in rows)
        {
            string file = await RecordAsync((int)GameData.Int(row, "_iID"));
            if (file != "") manifest.Add(new
            {
                skill = GameData.Int(row, "_iID"), group = GameData.Int(row, "_iSkillID"), level = GameData.Int(row, "_iLevel"),
                job = GameData.Int(row, "_iJob"), name = data.TextOf(row, "_iName"), script = row.GetProperty("_kScriptLogic").GetString(),
                file = Path.GetRelativePath(Videos, file).Replace('\\', '/')
            });
        }
        Directory.CreateDirectory(Path.Combine(Videos, family));
        File.WriteAllText(Path.Combine(Videos, family, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Log($"family {family} done: {manifest.Count} clips");
    }

    /// <summary>Films one cast. Returns the file, or "" when the cast was refused.</summary>
    private async Task<string> RecordAsync(int skillId)
    {
        var row = data.Row("SkillConfig", skillId)!.Value;
        int group = (int)GameData.Int(row, "_iSkillID");
        await GmAsync($"!skill {group} {GameData.Int(row, "_iLevel")}", 300); await GmAsync("!cd", 200); await GmAsync("!dummy", 900);
        long chant = GameData.Int(row, "_iChantFix") + GameData.Int(row, "_iChantVar"), action = GameData.Int(row, "_iActionTime");
        int seconds = (int)Math.Clamp(Math.Ceiling((chant + action) / 1000.0 + 3.2), 4, 14);
        string folder = Path.Combine(Videos, FamilyOf((int)GameData.Int(row, "_iJob")));
        Directory.CreateDirectory(folder);
        string script = (row.GetProperty("_kScriptLogic").GetString() ?? "skill").Replace('/', '_');
        string file = Path.Combine(folder, $"{skillId}_{script}.mp4");

        var ffmpeg = StartFfmpeg(file, seconds);
        await Task.Delay(1200); // encoder start-up
        var dummyId = DummyId();
        await caster!.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = dummyId });
        string result = "no reply";
        try
        {
            var e = await caster.Inbox.WaitAsync(x => x.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(5));
            int code = CastSkillRsp.Parser.ParseFrom(e.Body).Result;
            result = code == 0 ? "ok" : "refused " + code;
        }
        catch (TimeoutException) { }
        await Task.Delay(TimeSpan.FromSeconds(seconds - 1.2));
        await StopFfmpeg(ffmpeg);
        Log($"rec {skillId} {script}: {result}, {seconds}s -> {file}");
        if (result != "ok") { try { File.Delete(file); } catch (IOException) { } return ""; }
        return file;
    }

    /// <summary>The uid the last !dummy announced ("Dummy 123 (5000001) at ..").</summary>
    private long DummyId()
    {
        foreach (var line in Marquees(0).Reverse())
            if (line.Contains("Dummy ") && long.TryParse(line.Split("Dummy ")[1].Split(' ')[0], out long id)) return id;
        return 0;
    }

    private static string FamilyOf(int job) => job switch
    {
        >= 2000 and < 3000 => "mage",
        >= 1000 and < 2000 => "swordsman",
        >= 3000 and < 4000 => "archer",
        >= 4000 and < 5000 => "acolyte",
        >= 5000 and < 6000 => "thief",
        >= 6000 and < 7000 => "merchant",
        _ => "common"
    };

    private static Process StartFfmpeg(string file, int seconds)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "gfxcapture=window_exe=ro3\\.exe:max_framerate=30:capture_cursor=false,hwdownload,format=bgra,scale=1280:-2,format=yuv420p",
            "-t", seconds.ToString(), "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-movflags", "+faststart", file })
            psi.ArgumentList.Add(arg);
        var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        return p;
    }

    private static async Task StopFfmpeg(Process p)
    {
        try { if (!p.HasExited) { await p.StandardInput.WriteAsync("q"); await p.StandardInput.FlushAsync(); } } catch (IOException) { }
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(8000));
        if (!p.HasExited) p.Kill();
        string err = await p.StandardError.ReadToEndAsync();
        if (err.Length > 0) Log("ffmpeg: " + err.Trim().Split('\n')[0]);
    }
}
