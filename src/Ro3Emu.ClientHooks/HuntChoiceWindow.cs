using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    internal static class HuntChoiceWindow
    {
        internal const uint UpMsgId = 77025, DownMsgId = 77026;
        internal static readonly ConcurrentQueue<byte[]> Inbound = new ConcurrentQueue<byte[]>();
        internal static readonly Dictionary<(int, int), (string Monster, string Map)> Names = new Dictionary<(int, int), (string, string)>();
        private static HuntChoiceBehaviour _behaviour;
        internal static int ResetPending;
        internal static Obf_R.Obf_ff Dbm, Global;
        internal static void Remember(int npc, int map, string monster, string name) => Names[(npc, map)] = (monster, name);
        internal static void Configure(Obf_R.Obf_ff dbm, Obf_R.Obf_ff global) { Dbm = dbm; Global = global; }
        internal static void OnDown(ReadOnlyMemory<byte> body)
        {
            if (body.Length == 0 || body.Length > 4096) return;
            while (Inbound.Count >= 8 && Inbound.TryDequeue(out _)) { }
            Inbound.Enqueue(body.ToArray());
        }
        internal static void Reset() => Interlocked.Exchange(ref ResetPending, 1);
        internal static void Install()
        {
            if (_behaviour != null) return;
            var root = new GameObject("Ro3Emu_HuntChoices");
            root.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(root);
            _behaviour = root.AddComponent<HuntChoiceBehaviour>();
        }
    }

    internal sealed class HuntChoiceBehaviour : MonoBehaviour
    {
        private const long Dialog = 99003401, Npc = 990034, TextBase = 99130007000;
        private static readonly string[] Elements = { "", "Neutro", "Vento", "Terra", "Fogo", "Água", "Veneno", "Sagrado", "Sombrio", "Fantasma", "Morto-vivo" };
        private void Update()
        {
            if (Interlocked.Exchange(ref HuntChoiceWindow.ResetPending, 0) != 0)
                while (HuntChoiceWindow.Inbound.TryDequeue(out _)) { }
            while (HuntChoiceWindow.Inbound.TryDequeue(out var body))
            {
                try { Show(JObject.Parse(Encoding.UTF8.GetString(body))); }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hunt] native dialog: " + e.Message); }
            }
        }
        private static void Show(JObject state)
        {
            var dbm = HuntChoiceWindow.Dbm;
            var global = HuntChoiceWindow.Global;
            if (dbm == null || global == null || ((long?)state["npcUid"] ?? 0) <= 0) return;
            var dialogues = PassChallenges.Values(dbm, "m_kCFG_DialogueConfig");
            var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            var helper = global.Obf_RuA<Obf_R.Obf_ff>("LC_NpcDialogHelper");
            var trigger = helper?.Obf_RuA<Obf_R.Obf_Ef>("TriggerDialog");
            if (dialogues == null || texts == null || trigger == null) return;
            string message = (string)state["message"] ?? "Escolha sua próxima caçada.";
            PassChallenges.Text(texts, TextBase, message);
            var rows = new List<object> { PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.LineFields(Dialog, Npc, TextBase))) };
            int index = 0;
            foreach (var choice in state["choices"] as JArray ?? new JArray())
            {
                if (index >= 2) break;
                int npc = (int?)choice["npc"] ?? 0, map = (int?)choice["map"] ?? 0, elementId = (int?)choice["element"] ?? 0;
                var names = HuntChoiceWindow.Names.TryGetValue((npc, map), out var found) ? found : ("Monstro " + npc, "Mapa " + map);
                string element = elementId > 0 && elementId < Elements.Length ? Elements[elementId] : "desconhecido";
                long text = TextBase + 1 + index;
                PassChallenges.Text(texts, text, names.Item1 + " — " + names.Item2 + " (" + element + ")");
                long interaction = 99003404 + index;
                var option = new NpcOption(text, NpcServiceTable.ChatIcon, new[] { new[] { "211", Npc.ToString(), interaction.ToString() }, new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
                rows.Add(PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, 2 + index, option))));
                index++;
            }
            var leave = new NpcOption(NpcServiceTable.LeaveText, NpcServiceTable.LeaveIcon, new[] { new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } });
            rows.Add(PassChallenges.Row(NpcShops.ToPairs(NpcServiceTable.OptionFields(Dialog, Npc, 2 + index, leave))));
            PassChallenges.RawSet(dialogues, Dialog, PassChallenges.List(rows.ToArray()));
            trigger.Obf_HuA(Dialog);
        }
    }
}
