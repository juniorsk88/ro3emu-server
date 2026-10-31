using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    [ClientDataFact]
    public async Task JobChangeAt40DoesNotGrantOptionalPoints()
    {
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await loop.InvokeAsync(() => character.SetJobAsync(session, 1100));
        await loop.InvokeAsync(() => character.SetLevelAsync(session, 99, 40));
        await loop.InvokeAsync(() => character.TransformJobAsync(session, new PlayerTransformJobReq { JobId = 1200 }));
        Assert.Empty(session.Progress!.CompletedJobLevels);
        Assert.Equal(55, skills.Build(session).SkillAssignPlans[0].SkillPoints[2]);
    }

    [ClientDataFact]
    public async Task Job50RequiresTheOptionAndPreservesCompletedRankOnJobChange()
    {
        string path = Path.Combine(_app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.DataDir, "custom-npcs.json");
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        try
        {
            await File.WriteAllTextAsync(path, "{\"healer\":true,\"hunter\":true,\"buffLevel\":10,\"job50\":false}");
            await loop.InvokeAsync(() => character.SetLevelAsync(session, 99, 50));
            Assert.Equal(10, session.Progress!.JobLevel);
            await loop.InvokeAsync(() => character.SetJobAsync(session, 1100));
            await loop.InvokeAsync(() => character.SetLevelAsync(session, 99, 50));
            Assert.Equal(40, session.Progress.JobLevel);
            Assert.Equal(40, skills.Build(session).SkillAssignPlans[0].SkillPoints[2]);
            await File.WriteAllTextAsync(path, "{\"healer\":true,\"hunter\":true,\"buffLevel\":10,\"job50\":true}");
            await loop.InvokeAsync(() => character.AddExpAsync(session, 0, 25613));
            Assert.Equal(40, session.Progress.JobLevel);
            await loop.InvokeAsync(() => character.SetLevelAsync(session, 99, 40));
            await alice.SendGameAsync(SceneService.Job50Hello, new NullArg());
            var state = await alice.Inbox.WaitAsync(e => e.IsGame(SceneService.Job50State), Wait);
            Assert.StartsWith("1|2|40|23964,25613|40|", System.Text.Encoding.UTF8.GetString(state.Body));
            Assert.True(session.Job50Client);
            await loop.InvokeAsync(() => character.AddExpAsync(session, 0, 25613));
            Assert.Equal(41, session.Progress.JobLevel);
            await loop.InvokeAsync(() => character.AddExpAsync(session, 0, 27261));
            Assert.Equal((41, 27261), (session.Progress.JobLevel, session.Progress.JobExp));
            await loop.InvokeAsync(() => character.AddExpAsync(session, 0, 1));
            Assert.Equal((42, 0), (session.Progress.JobLevel, session.Progress.JobExp));
            await loop.InvokeAsync(() => character.SetLevelAsync(session, 99, 50));
            Assert.Equal(50, skills.Build(session).SkillAssignPlans[0].SkillPoints[2]);
            await File.WriteAllTextAsync(path, "{\"healer\":true,\"hunter\":true,\"buffLevel\":10,\"job50\":false}");
            await loop.InvokeAsync(() => character.AddExpAsync(session, 0, 1));
            Assert.Equal(50, session.Progress.JobLevel);
            Assert.Equal(50, skills.Build(session).SkillAssignPlans[0].SkillPoints[2]);
            await alice.SendGameAsync(SceneService.Job50Hello, new NullArg());
            var capped = await alice.Inbox.WaitAsync(e => e.IsGame(SceneService.Job50State), Wait);
            Assert.StartsWith("0|2|50|", System.Text.Encoding.UTF8.GetString(capped.Body));
            await File.WriteAllTextAsync(path, "{\"healer\":true,\"hunter\":true,\"buffLevel\":10,\"job50\":true}");
            await loop.InvokeAsync(() => character.TransformJobAsync(session, new PlayerTransformJobReq { JobId = 1200 }));
            Assert.Equal(50, session.Progress.CompletedJobLevels[2]);
            Assert.Equal(50, roles.LoadProgress(session.Role!.Id).CompletedJobLevels[2]);
            Assert.Equal(65, skills.Build(session).SkillAssignPlans[0].SkillPoints[2]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
