using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    [ClientDataFact]
    public void EveryHuntTargetHasARealFieldSpawn()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var world = _app.Services.GetRequiredService<WorldData>();
        foreach (int level in Enumerable.Range(1, 99))
        {
            var (npc, mapId) = HuntCatalog.ForLevel(level);
            var task = data.Row("TaskConfig", HuntCatalog.EasierFirstTask + level - 1);
            if (level >= 3) Assert.True(GameData.Int(data.Row("NPCConfig", npc)!.Value, "_iLevel") < level, $"Level {level}, NPC {npc}");
            Assert.NotNull(task);
            var target = task.Value.GetProperty("_kProgressTarget")[0];
            Assert.Equal(npc, target[1].GetInt32());
            Assert.Equal(30, target[2].GetInt32());
            var row = data.Row("NPCConfig", npc);
            Assert.NotNull(row);
            Assert.Equal(1, GameData.Int(row.Value, "_iNPCType"));
            Assert.Equal(1, GameData.Int(data.Row("MapConfig", mapId)!.Value, "_iSceneType"));
            var map = world.Map(mapId);
            Assert.NotNull(map);
            Assert.Contains(map.SpawnGroups, g => g.Points.Count > 0 && (g.Pool
                ? g.Limits.Any(l => l.NpcId == npc && l.Max > 0)
                : g.Points.Any(p => p.ConfigId == npc)));
        }
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HunterAcceptsAndPaysRepeatableQuestsOncePerCompletion(bool easier)
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        await Gm("alice", "!lvl 20 15");
        await Gm("alice", "!job 1100");
        await Gm("alice", "!warp 10100 169.5 202.5");
        await FollowSceneChange(alice);
        await alice.SendGameAsync(SceneService.CustomNpcHello, new NullArg());
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(n => n.EntityTemplateID == CustomNpcSettings.HunterNpc));
        var hunter = Assert.Single(view.Bases, n => n.EntityTemplateID == CustomNpcSettings.HunterNpc);
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var session = sessions.All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var tasks = _app.Services.GetRequiredService<TaskService>();
        var data = _app.Services.GetRequiredService<GameData>();
        long baseNeed = GameData.Int(data.Row("BaseLvConfig", 20)!.Value, "_iBaseExp");
        long jobRank = GameData.Int(data.Row("JobConfig", 1100)!.Value, "_iJobRank");
        long jobNeed = GameData.Int(data.Row("JobLvConfig", jobRank * 100 + 15)!.Value, "_iJobeExp");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var onKill = combat.Killed.Single(h => h.Target is TaskService);
        await loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>().CustomNpcHelloAsync(session, easier));
        var (targetNpc, targetMap) = easier ? HuntTargets.ForLevel(20) : HuntTargets.LegacyForLevel(20);
        var before = await loop.InvokeAsync(() => Task.FromResult((session.Progress!.Exp, session.Progress.JobExp,
            character.HuntReward(session.Progress), character.HuntReward(session.Progress, percent: 20))));
        long taskId = HuntCatalog.FirstFor(easier) + 19;
        await alice.SendGameAsync(TaskService.TaskAccept, new TaskAcceptReq { Id = taskId, NpcId = 0 });
        var refused = await Next(alice, TaskService.ApiError, APIErrorMsg.Parser, e => e.OriginMsgId == TaskService.TaskAccept);
        Assert.Equal(ErrorCodes.TaskRequirements, refused.ErrorCode);
        for (int count = 1; count <= 2; count++)
        {
            int messageStart = alice.Inbox.Snapshot().Count;
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { NpcUid = (long)hunter.Uuid, InteractionId = CustomNpcSettings.HunterAccept, SceneConfigId = Prontera });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            var accepted = await Next(alice, TaskService.TaskAccept, TaskAcceptResp.Parser);
            Assert.Equal(0, accepted.Result);
            Assert.Equal(taskId, accepted.Id);
            var events = alice.Inbox.Snapshot().Skip(messageStart).ToArray();
            int syncIndex = Array.FindIndex(events, e => e.IsGame(TaskService.TaskInfoSync));
            int acceptIndex = Array.FindIndex(events, e => e.IsGame(TaskService.TaskAccept));
            Assert.True(syncIndex >= 0 && acceptIndex > syncIndex);
            await loop.InvokeAsync(() =>
            {
                Assert.Equal(TaskState.Running, ((DBTaskInfo)session.Synced[TaskService.TaskInfoSync]).Infos[taskId].Status);
                return Task.CompletedTask;
            });
            await loop.InvokeAsync(async () =>
            {
                session.CustomNpcRows = false;
                await tasks.SyncTasksAsync(session);
                Assert.False(((DBTaskInfo)session.Synced[TaskService.TaskInfoSync]).Infos.ContainsKey(taskId));
                Assert.True(session.Tasks!.Infos.ContainsKey(taskId));
                session.CustomNpcRows = true;
                await tasks.SyncTasksAsync(session);
                Assert.True(((DBTaskInfo)session.Synced[TaskService.TaskInfoSync]).Infos.ContainsKey(taskId));
            });
            await Gm("alice", $"!warp {targetMap}");
            await FollowSceneChange(alice);
            await loop.InvokeAsync(async () =>
            {
                async Task Kill(int id, int sequence)
                {
                    var monster = new MonsterUnit
                    {
                        Uid = 900000 + sequence, SceneEntry = session.SceneEntry, RuntimeMap = session.RuntimeMap, Dead = true,
                        Template = monsters.Template(id)!, Home = new SpawnPoint(0, 0, 0, 0, 0, id),
                        Group = new SpawnGroup(0, false, 1, 0, [], [])
                    };
                    await onKill(session, monster);
                }
                await Kill(targetNpc == 5000001 ? 5000002 : 5000001, 0);
                Assert.Equal(0, session.Tasks!.Infos[taskId].CompleteNum[0]);
                for (int i = 1; i <= 29; i++) await Kill(targetNpc, i);
                Assert.Equal(29, session.Tasks.Infos[taskId].CompleteNum[0]);
                Assert.Equal(TaskState.Running, session.Tasks.Infos[taskId].Status);
                await Kill(targetNpc, 30);
                Assert.Equal(30, session.Tasks.Infos[taskId].CompleteNum[0]);
                Assert.Equal(TaskState.Finish, session.Tasks.Infos[taskId].Status);
                Assert.Equal(before.Exp + before.Item3.Base * (count - 1) + before.Item4.Base * count, session.Progress!.Exp + (session.Progress.Level > 20 ? baseNeed : 0));
                Assert.Equal(before.JobExp + before.Item3.Job * (count - 1) + before.Item4.Job * count, session.Progress.JobExp + (session.Progress.JobLevel > 15 ? jobNeed : 0));
                Assert.Equal(30, DBTaskInfo.Parser.ParseFrom(roles.LoadTasks(session.Role!.Id)!).Infos[taskId].CompleteNum[0]);
            });
            await Gm("alice", "!warp 10100 169.5 202.5");
            view = (await FollowSceneChange(alice)).Npcs;
            if (!view.Bases.Any(n => n.EntityTemplateID == CustomNpcSettings.HunterNpc))
                view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                    v => v.Bases.Any(n => n.EntityTemplateID == CustomNpcSettings.HunterNpc));
            hunter = Assert.Single(view.Bases, n => n.EntityTemplateID == CustomNpcSettings.HunterNpc);
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { NpcUid = (long)hunter.Uuid, InteractionId = CustomNpcSettings.HunterDeliver, SceneConfigId = Prontera });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            var delivered = await Next(alice, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
            if (delivered.Data.Count == 0)
                Assert.Fail($"Delivery refused with {(await Next(alice, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode}");
            Assert.Single(delivered.Data);
            await loop.InvokeAsync(() =>
            {
                Assert.Equal(count, session.Tasks!.CompleteTaskNum[taskId]);
                Assert.Equal(count, DBTaskInfo.Parser.ParseFrom(roles.LoadTasks(session.Role!.Id)!).CompleteTaskNum[taskId]);
                Assert.Equal(before.Exp + (before.Item3.Base + before.Item4.Base) * count, session.Progress!.Exp + (session.Progress.Level > 20 ? baseNeed : 0));
                Assert.Equal(before.JobExp + (before.Item3.Job + before.Item4.Job) * count, session.Progress.JobExp + (session.Progress.JobLevel > 15 ? jobNeed : 0));
                return Task.CompletedTask;
            });
        }
        await alice.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq
        { Req = { new TaskDeliverySingleReq { Id = taskId, NpcId = (long)hunter.Uuid } } });
        Assert.Empty((await Next(alice, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);
        await loop.InvokeAsync(() =>
        {
            Assert.Equal(2, session.Tasks!.CompleteTaskNum[taskId]);
            Assert.Equal(before.Exp + (before.Item3.Base + before.Item4.Base) * 2, session.Progress!.Exp + (session.Progress.Level > 20 ? baseNeed : 0));
            return Task.CompletedTask;
        });
    }
}
