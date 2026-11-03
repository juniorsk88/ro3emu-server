using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    [ClientDataFact]
    public void HuntOptionsUseRealLowerLevelMonstersAndRotateWithoutRepeating()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var world = _app.Services.GetRequiredService<WorldData>();
        for (int level = 1; level <= 99; level++)
        {
            var pool = HuntOptions.ForLevel(level);
            Assert.NotEmpty(pool);
            for (int slot = 0; slot < pool.Length; slot++)
            {
                var option = pool[slot];
                var npc = data.Row("NPCConfig", option.Npc)!.Value;
                if (level >= 3) Assert.True(GameData.Int(npc, "_iLevel") < level);
                Assert.Equal(option.Element, GameData.Int(npc, "_iElement"));
                Assert.Contains(world.Map(option.Map)!.SpawnGroups, g => g.Points.Count > 0 && (g.Pool
                    ? g.Limits.Any(l => l.NpcId == option.Npc && l.Max > 0)
                    : g.Points.Any(p => p.ConfigId == option.Npc)));
                var row = data.Row("TaskConfig", HuntOptions.TaskId(level, slot))!.Value;
                Assert.Equal(option.Npc, row.GetProperty("_kProgressTarget")[0][1].GetInt32());
            }
            for (int cycle = 0; cycle < 12; cycle++)
            foreach (int previous in pool.Select(x => x.Npc).Prepend(0))
            {
                int[] chosen = HuntOptions.Choose(level, cycle, previous);
                Assert.InRange(chosen.Length, 1, 2);
                if (level >= 5) Assert.Equal(2, chosen.Length);
                if (pool.Any(x => x.Npc != previous)) Assert.DoesNotContain(chosen, i => pool[i].Npc == previous);
                if (chosen.Length == 2)
                {
                    Assert.NotEqual(pool[chosen[0]].Npc, pool[chosen[1]].Npc);
                    Assert.NotEqual(pool[chosen[0]].Map, pool[chosen[1]].Map);
                    Assert.NotEqual(pool[chosen[0]].Element, pool[chosen[1]].Element);
                }
            }
        }
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HunterOffersTwoChoicesAndRotatesAfterDelivery(bool existingHunt)
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        await Gm("alice", "!lvl 65 15");
        await Gm("alice", "!job 1100");
        await Gm("alice", "!warp 10100 169.5 202.5");
        await FollowSceneChange(alice);
        await alice.SendGameBytesAsync(SceneService.CustomNpcHello, Encoding.UTF8.GetBytes(existingHunt ? "npc-v2" : "npc-v3"));
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(n => n.EntityTemplateID == CustomNpcSettings.HunterNpc));
        var hunter = Assert.Single(view.Bases, n => n.EntityTemplateID == CustomNpcSettings.HunterNpc);
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var session = sessions.All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var onKill = combat.Killed.Single(h => h.Target is TaskService);

        async Task<JsonElement> Open()
        {
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
                { NpcUid = (long)hunter.Uuid, InteractionId = CustomNpcSettings.HunterDialogue, SceneConfigId = Prontera });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            var packet = await alice.Inbox.WaitAsync(e => e.IsGame(TaskService.HuntChoiceDown), TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(packet.Body);
            return doc.RootElement.Clone();
        }
        long task;
        int npc;
        if (existingHunt)
        {
            task = HuntCatalog.EasierFirstTask + 64;
            npc = HuntTargets.ForLevel(65).Npc;
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
                { NpcUid = (long)hunter.Uuid, InteractionId = CustomNpcSettings.HunterAccept, SceneConfigId = Prontera });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            Assert.Equal(task, (await Next(alice, TaskService.TaskAccept, TaskAcceptResp.Parser)).Id);
        }
        else
        {
            var first = await Open();
            var options = first.GetProperty("choices").EnumerateArray().ToArray();
            Assert.Equal(2, options.Length);
            Assert.NotEqual(options[0].GetProperty("map").GetInt32(), options[1].GetProperty("map").GetInt32());
            Assert.NotEqual(options[0].GetProperty("element").GetInt32(), options[1].GetProperty("element").GetInt32());
            await alice.SendGameBytesAsync(TaskService.HuntChoiceUp, JsonSerializer.SerializeToUtf8Bytes(new
                { npcUid = (long)hunter.Uuid, task = HuntOptions.TaskId(1, 0) }));
            var refused = await Next(alice, TaskService.ApiError, APIErrorMsg.Parser, e => e.OriginMsgId == TaskService.TaskAccept);
            Assert.Equal(ErrorCodes.TaskRequirements, refused.ErrorCode);
            await alice.Inbox.WaitAsync(e => e.IsGame(TaskService.HuntChoiceDown), TimeSpan.FromSeconds(15));
            task = options[0].GetProperty("task").GetInt64();
            npc = options[0].GetProperty("npc").GetInt32();
            await alice.SendGameBytesAsync(TaskService.HuntChoiceUp, JsonSerializer.SerializeToUtf8Bytes(new { npcUid = (long)hunter.Uuid, task }));
            Assert.Equal(task, (await Next(alice, TaskService.TaskAccept, TaskAcceptResp.Parser)).Id);
            await alice.Inbox.WaitAsync(e => e.IsGame(TaskService.HuntChoiceDown), TimeSpan.FromSeconds(15));
        }
        await loop.InvokeAsync(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                var m = new MonsterUnit { Uid = 990000 + i, SceneEntry = session.SceneEntry, RuntimeMap = session.RuntimeMap,
                    Dead = true, Template = monsters.Template(npc)!, Home = new SpawnPoint(0, 0, 0, 0, 0, npc),
                    Group = new SpawnGroup(0, false, 1, 0, [], []) };
                await onKill(session, m);
            }
            Assert.Equal(TaskState.Finish, session.Tasks!.Infos[task].Status);
        });
        if (existingHunt)
            await loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>().CustomNpcHelloAsync(session, true, true));
        var next = await Open();
        Assert.Contains("Parabéns", next.GetProperty("message").GetString());
        Assert.All(next.GetProperty("choices").EnumerateArray(), x => Assert.NotEqual(npc, x.GetProperty("npc").GetInt32()));
        await loop.InvokeAsync(() =>
        {
            Assert.Equal(TaskState.Delivery, session.Tasks!.Infos[task].Status);
            Assert.Equal(1, session.Tasks.CompleteTaskNum[task]);
            return Task.CompletedTask;
        });
    }
}
