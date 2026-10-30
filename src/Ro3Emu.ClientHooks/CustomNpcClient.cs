using System;
using System.Text;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    internal static class CustomNpcClient
    {
        private const long Npc = 990031, Template = 400031, Dialog = 99003101, Heal = 99003102;
        private const long Name = 99130002001, Greeting = 99130002002, Action = 99130002003;
        private static int _helloPeriod;

        internal static void Hello(Obf_R.Obf_ff dbm, Obf_R.Obf_ff global)
        {
            var npcs = PassChallenges.Values(dbm, "m_kCFG_NPCConfig");
            var interactions = PassChallenges.Values(dbm, "m_kCFG_NPCInteractConfig");
            var dialogues = PassChallenges.Values(dbm, "m_kCFG_DialogueConfig");
            var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            if (npcs == null || interactions == null || dialogues == null || texts == null) return;
            var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
            if (!Exists(rawget, npcs, Npc) || !Exists(rawget, interactions, Dialog)
                || !Exists(rawget, interactions, Heal) || !Exists(rawget, dialogues, Dialog)
                || !Exists(rawget, texts, Name)) return;
            int period = ++_helloPeriod;
            if (period <= 3 || period % 4 == 0)
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(77022, Encoding.UTF8.GetBytes("npc-v1"));
        }

        internal static void Inject(Obf_R.Obf_ff dbm, Obf_R.Obf_ff global)
        {
            try
            {
                var npcs = PassChallenges.Values(dbm, "m_kCFG_NPCConfig");
                var interactions = PassChallenges.Values(dbm, "m_kCFG_NPCInteractConfig");
                var dialogues = PassChallenges.Values(dbm, "m_kCFG_DialogueConfig");
                var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
                if (npcs == null || interactions == null || dialogues == null || texts == null) return;
                var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
                if (Exists(rawget, npcs, Npc) && Exists(rawget, interactions, Dialog)
                    && Exists(rawget, interactions, Heal) && Exists(rawget, dialogues, Dialog)
                    && Exists(rawget, texts, Name)) return;
                var template = npcs.Obf_ruA<long, Obf_R.Obf_ff>(Template);
                var original = interactions.Obf_ruA<long, Obf_R.Obf_ff>(40003101L);
                if (template == null || original == null) return;
                PassChallenges.Text(texts, Name, "Curandeira");
                PassChallenges.Text(texts, Greeting, "Posso recuperar seu HP e SP e abençoar você por 10 minutos.");
                PassChallenges.Text(texts, Action, "Curar e receber buffs");
                var npc = Inherit(global, template);
                PassChallenges.RawSet(npc, "_iID", Npc);
                PassChallenges.RawSet(npc, "_kName", PassChallenges.List(Name));
                PassChallenges.RawSet(npc, "_iInteractID", PassChallenges.List(Dialog));
                PassChallenges.RawSet(npcs, Npc, npc);
                var talk = Inherit(global, original);
                PassChallenges.RawSet(talk, "_iInterID", Dialog);
                PassChallenges.RawSet(talk, "_iEXECAction", PassChallenges.List(PassChallenges.List("53", Dialog.ToString())));
                PassChallenges.RawSet(interactions, Dialog, talk);
                var heal = Inherit(global, original);
                PassChallenges.RawSet(heal, "_iInterID", Heal);
                PassChallenges.RawSet(heal, "_iEXECAction", PassChallenges.List(PassChallenges.List("")));
                PassChallenges.RawSet(interactions, Heal, heal);
                var line = PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.LineFields(Dialog, Npc, Greeting)));
                var option = new NpcOption(Action, NpcServiceTable.ChatIcon, new[] { new[] { "211", Npc.ToString(), Heal.ToString() }, new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
                var button = PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, 2, option)));
                var leave = new NpcOption(NpcServiceTable.LeaveText, NpcServiceTable.LeaveIcon, new[] { new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
                var close = PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, 3, leave)));
                PassChallenges.RawSet(dialogues, Dialog, PassChallenges.List(line, button, close));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.CustomNpc] " + e.Message); }
        }

        internal static bool Exists(Obf_R.Obf_Ef rawget, Obf_R.Obf_ff table, long key)
        {
            var value = rawget.Obf_HuA(table, key);
            return value != null && value.Length > 0 && value[0] != null;
        }

        internal static Obf_R.Obf_ff Inherit(Obf_R.Obf_ff global, Obf_R.Obf_ff original)
        {
            var row = PassChallenges.Row();
            global.Obf_LuA<Obf_R.Obf_Ef>("setmetatable").Obf_HuA(row, PassChallenges.Row("__index", original));
            return row;
        }
    }
}
