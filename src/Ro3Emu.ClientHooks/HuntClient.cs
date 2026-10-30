using System;
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
            var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            if (npcs == null || interactions == null || dialogues == null || tasks == null || texts == null) return false;
            var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
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
            for (int level = 1; level <= 99; level++)
            {
                var (monster, map) = HuntTargets.ForLevel(level);
                var monsterRow = npcs.Obf_ruA<long, Obf_R.Obf_ff>(monster);
                var nameIds = monsterRow?.Obf_RuA<Obf_R.Obf_ff>("_kName");
                long nameId = nameIds == null ? 0 : LuaPatches.ToLong(nameIds.Obf_ruA<long, object>(1L));
                var nameRow = nameId == 0 ? null : texts.Obf_ruA<long, Obf_R.Obf_ff>(nameId);
                string monsterName = nameRow?.Obf_RuA<string>("_kDes") ?? "monstros";
                long text = TextBase + level;
                PassChallenges.Text(texts, text, "Caça: derrote 30 " + monsterName);
                var task = CustomNpcClient.Inherit(global, taskTemplate);
                PassChallenges.RawSet(task, "_iTaskID", FirstTask + level - 1);
                PassChallenges.RawSet(task, "_iName", PassChallenges.List(text));
                PassChallenges.RawSet(task, "_iTaskDescribe", PassChallenges.List(text));
                PassChallenges.RawSet(task, "_iTargetDescribe", PassChallenges.List(text));
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
                PassChallenges.RawSet(task, "_kDeliverTask", PassChallenges.List(3L));
                PassChallenges.RawSet(task, "_kAcceptAction", PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(task, "_kEXECAction", PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(task, "_kTimeLimit", 0L);
                PassChallenges.RawSet(task, "_kProgressTarget", PassChallenges.List(PassChallenges.List(104002L, (long)monster, 30L)));
                var track = PassChallenges.List(PassChallenges.List("142", map.ToString(), monster.ToString(), "-1"));
                PassChallenges.RawSet(task, "_kTrack", track);
                PassChallenges.RawSet(task, "_kPCTrack", track);
                PassChallenges.RawSet(task, "_kEXP", PassChallenges.List());
                PassChallenges.RawSet(tasks, FirstTask + level - 1, task);
            }
            return true;
        }

        private static Obf_R.Obf_ff Option(long text, long action, long sort)
        {
            var option = new NpcOption(text, NpcServiceTable.ChatIcon, new[] { new[] { "211", Npc.ToString(), action.ToString() }, new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
            return PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, sort, option)));
        }
    }
}
