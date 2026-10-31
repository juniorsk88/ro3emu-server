using System;
using System.Collections.Generic;
using Ro3Emu.Server.Game;

namespace Ro3Emu.ClientHooks
{
    internal static class HuntClient
    {
        internal const long Npc = 990034, Dialog = 99003401, Accept = 99003402, Deliver = 99003403;
        private const long FirstTask = 99010001, TextBase = 99130003000;

        internal static bool Inject(Obf_R.Obf_ff dbm, Obf_R.Obf_ff global)
        {
            var npcs = PassChallenges.Values(dbm, "m_kCFG_NPCConfig");
            var interactions = PassChallenges.Values(dbm, "m_kCFG_NPCInteractConfig");
            var dialogues = PassChallenges.Values(dbm, "m_kCFG_DialogueConfig");
            var tasks = PassChallenges.Values(dbm, "m_kCFG_TaskConfig");
            var maps = PassChallenges.Values(dbm, "m_kCFG_MapConfig");
            var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            var trans = global.Obf_RuA<Obf_R.Obf_ff>("Language")?.Obf_RuA<Obf_R.Obf_Ef>("Trans");
            if (npcs == null || interactions == null || dialogues == null || tasks == null || maps == null || texts == null || trans == null) return false;
            var prontera = maps.Obf_ruA<long, Obf_R.Obf_ff>(10100L);
            var sceneName = prontera?.Obf_RuA<string>("_kResSceneEditorName");
            if (string.IsNullOrEmpty(sceneName)) return false;
            var loadedScene = global.Obf_LuA<Obf_R.Obf_Ef>("require").Obf_HuA(sceneName);
            var scene = loadedScene != null && loadedScene.Length > 0 ? loadedScene[0] as Obf_R.Obf_ff : null;
            var sceneNpcs = scene?.Obf_RuA<Obf_R.Obf_ff>("kNpcList");
            if (sceneNpcs == null) return false;
            var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
            if (!CustomNpcClient.Exists(rawget, sceneNpcs, HuntTargets.HunterPoint))
            {
                var nearby = sceneNpcs.Obf_ruA<long, Obf_R.Obf_ff>(100015L);
                if (nearby == null) return false;
                var point = CustomNpcClient.Inherit(global, nearby);
                PassChallenges.RawSet(point, "Id", (long)HuntTargets.HunterPoint);
                PassChallenges.RawSet(point, "NpcId", Npc);
                PassChallenges.RawSet(point, "x", HuntTargets.HunterX);
                PassChallenges.RawSet(point, "y", HuntTargets.HunterY);
                PassChallenges.RawSet(point, "z", HuntTargets.HunterZ);
                PassChallenges.RawSet(point, "FaceDirection", 90L);
                PassChallenges.RawSet(point, "TalkPositions", PassChallenges.List());
                PassChallenges.RawSet(sceneNpcs, (long)HuntTargets.HunterPoint, point);
            }
            if (CustomNpcClient.Exists(rawget, npcs, Npc) && CustomNpcClient.Exists(rawget, interactions, Dialog)
                && CustomNpcClient.Exists(rawget, interactions, Accept) && CustomNpcClient.Exists(rawget, interactions, Deliver)
                && CustomNpcClient.Exists(rawget, dialogues, Dialog) && CustomNpcClient.Exists(rawget, tasks, FirstTask)
                && CustomNpcClient.Exists(rawget, tasks, FirstTask + 98) && CustomNpcClient.Exists(rawget, texts, TextBase + 99)) return true;
            var template = npcs.Obf_ruA<long, Obf_R.Obf_ff>(400034L);
            var interaction = interactions.Obf_ruA<long, Obf_R.Obf_ff>(40003401L);
            var taskTemplate = tasks.Obf_ruA<long, Obf_R.Obf_ff>(201000L);
            if (template == null || interaction == null || taskTemplate == null) return false;
            PassChallenges.Text(texts, TextBase, "Caçador");
            PassChallenges.Text(texts, TextBase + 100, "Complete caçadas para ganhar EXP de base e de classe.");
            PassChallenges.Text(texts, TextBase + 101, "Aceitar caçada");
            PassChallenges.Text(texts, TextBase + 102, "Concluir caçada");
            PassChallenges.Text(texts, TextBase + 103, "[Caça]");
            var npc = CustomNpcClient.Inherit(global, template);
            PassChallenges.RawSet(npc, "_iID", Npc);
            PassChallenges.RawSet(npc, "_kName", PassChallenges.List(TextBase));
            PassChallenges.RawSet(npc, "_iInteractID", PassChallenges.List(Dialog));
            PassChallenges.RawSet(npcs, Npc, npc);
            foreach (long id in new[] { Dialog, Accept, Deliver })
            {
                var row = CustomNpcClient.Inherit(global, interaction);
                PassChallenges.RawSet(row, "_iInterID", id);
                PassChallenges.RawSet(row, "_iEXECAction", id == Dialog
                    ? PassChallenges.List(PassChallenges.List("53", Dialog.ToString())) : PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(interactions, id, row);
            }
            var line = PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.LineFields(Dialog, Npc, TextBase + 100)));
            var accept = Option(TextBase + 101, Accept, 2);
            var deliver = Option(TextBase + 102, Deliver, 3);
            var leave = new NpcOption(NpcServiceTable.LeaveText, NpcServiceTable.LeaveIcon, new[] { new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
            var close = PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, 4, leave)));
            PassChallenges.RawSet(dialogues, Dialog, PassChallenges.List(line, accept, deliver, close));
            var waypoints = new Dictionary<(int, int), long>();
            for (int level = 1; level <= 99; level++)
            {
                var (monster, map) = HuntTargets.ForLevel(level);
                var monsterRow = npcs.Obf_ruA<long, Obf_R.Obf_ff>(monster);
                var mapRow = maps.Obf_ruA<long, Obf_R.Obf_ff>(map);
                var monsterName = Translate(trans, monsterRow?.Obf_RuA<Obf_R.Obf_ff>("_kName"));
                var mapName = Translate(trans, mapRow?.Obf_RuA<Obf_R.Obf_ff>("_iName"));
                if (monsterName == null || mapName == null) return false;
                if (!waypoints.TryGetValue((monster, map), out long waypoint))
                {
                    waypoint = FindWaypoint(global, mapRow, map, monster);
                    if (waypoint <= 0) return false;
                    waypoints[(monster, map)] = waypoint;
                }
                long text = TextBase + level;
                long targetText = TextBase + 200 + level;
                PassChallenges.Text(texts, text, "Caçada: " + monsterName);
                PassChallenges.Text(texts, targetText, mapName + ": Derrote " + monsterName + "*");
                var task = CustomNpcClient.Inherit(global, taskTemplate);
                PassChallenges.RawSet(task, "_iTaskID", FirstTask + level - 1);
                PassChallenges.RawSet(task, "_iName", PassChallenges.List(text));
                PassChallenges.RawSet(task, "_iTaskDescribe", PassChallenges.List(text));
                PassChallenges.RawSet(task, "_iTargetDescribe", PassChallenges.List(targetText));
                PassChallenges.RawSet(task, "_iShowTag", PassChallenges.List(TextBase + 103));
                PassChallenges.RawSet(task, "_iTaskType", 2L);
                PassChallenges.RawSet(task, "_iTaskSonType", 0L);
                PassChallenges.RawSet(task, "_iCondition", 0L);
                PassChallenges.RawSet(task, "_iAward", 0L);
                PassChallenges.RawSet(task, "_iIfRepeat", 1L);
                PassChallenges.RawSet(task, "_iIfGiveUp", 0L);
                PassChallenges.RawSet(task, "_iIfReset", 0L);
                PassChallenges.RawSet(task, "_iIsGlobalTask", 0L);
                PassChallenges.RawSet(task, "_kPreTask", PassChallenges.List());
                PassChallenges.RawSet(task, "_kAcceptTask", PassChallenges.List(3L));
                PassChallenges.RawSet(task, "_kDeliverTask", PassChallenges.List(2L, Npc, 10100L, (long)HuntTargets.HunterPoint, Dialog));
                PassChallenges.RawSet(task, "_kAcceptAction", PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(task, "_kEXECAction", PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(task, "_kTimeLimit", 0L);
                PassChallenges.RawSet(task, "_kProgressTarget", PassChallenges.List(PassChallenges.List(104002L, (long)monster, 30L)));
                var track = PassChallenges.List(PassChallenges.List("142", map.ToString(), waypoint.ToString(), "-1"));
                PassChallenges.RawSet(task, "_kTrack", track);
                PassChallenges.RawSet(task, "_kPCTrack", track);
                PassChallenges.RawSet(task, "_kEXP", PassChallenges.List());
                PassChallenges.RawSet(tasks, FirstTask + level - 1, task);
            }
            return true;
        }

        private static string Translate(Obf_R.Obf_Ef trans, Obf_R.Obf_ff key)
        {
            if (key == null) return null;
            var result = trans.Obf_HuA(key);
            return result != null && result.Length > 0 ? result[0] as string : null;
        }

        private static long FindWaypoint(Obf_R.Obf_ff global, Obf_R.Obf_ff mapRow, int map, int monster)
        {
            var name = mapRow?.Obf_RuA<string>("_kResSceneEditorName");
            if (string.IsNullOrEmpty(name)) return 0;
            var module = global.Obf_LuA<Obf_R.Obf_Ef>("require").Obf_HuA(name);
            var scene = module != null && module.Length > 0 ? module[0] as Obf_R.Obf_ff : null;
            var points = scene?.Obf_RuA<Obf_R.Obf_ff>("m_kWorldMonsterSpawnPoints");
            var groups = scene?.Obf_RuA<Obf_R.Obf_ff>("m_kWorldMonsterSpawnGroups");
            var next = global.Obf_LuA<Obf_R.Obf_Ef>("next");
            var loadedActions = global.Obf_LuA<Obf_R.Obf_Ef>("require").Obf_HuA("lc_interactactions");
            var actions = loadedActions != null && loadedActions.Length > 0 ? loadedActions[0] as Obf_R.Obf_ff : null;
            var getPosition = actions?.Obf_RuA<Obf_R.Obf_Ef>("GetPosByTargetID");
            if (points == null || groups == null || next == null || getPosition == null) return 0;
            object key = null;
            while (true)
            {
                var pair = next.Obf_HuA(points, key);
                if (pair == null || pair.Length < 2 || pair[0] == null) break;
                key = pair[0];
                var point = pair[1] as Obf_R.Obf_ff;
                if (point == null || !ContainsMonster(groups, point, monster)) continue;
                long id = LuaPatches.ToLong(key);
                var position = getPosition.Obf_HuA((long)map, id);
                if (position != null && position.Length > 1 && LuaPatches.ToLong(position[1]) == 5) return id;
            }
            return 0;
        }

        private static bool ContainsMonster(Obf_R.Obf_ff groups, Obf_R.Obf_ff point, int monster)
        {
            long groupId = LuaPatches.ToLong(point.Obf_RuA<object>("monsterGroupId"));
            var group = groups.Obf_ruA<long, Obf_R.Obf_ff>(groupId);
            if (group == null) return false;
            if (!(group.Obf_RuA<object>("m_bUseMaxMinRefreshId") is bool pooled) || !pooled)
                return LuaPatches.ToLong(point.Obf_RuA<Obf_R.Obf_ff>("m_kConfigIds")?.Obf_ruA<long, object>(1L)) == monster;
            var limits = group.Obf_RuA<Obf_R.Obf_ff>("m_kMonsterRefreshLimitDatas");
            if (limits == null) return false;
            for (long i = 1; ; i++)
            {
                var limit = limits.Obf_ruA<long, Obf_R.Obf_ff>(i);
                if (limit == null) return false;
                if (LuaPatches.ToLong(limit.Obf_RuA<object>("m_kConfigId")) == monster &&
                    LuaPatches.ToLong(limit.Obf_RuA<object>("m_iLimitCount_max")) > 0) return true;
            }
        }

        private static Obf_R.Obf_ff Option(long text, long action, long sort)
        {
            var option = new NpcOption(text, NpcServiceTable.ChatIcon, new[] { new[] { "211", Npc.ToString(), action.ToString() }, new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
            return PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, sort, option)));
        }
    }
}
