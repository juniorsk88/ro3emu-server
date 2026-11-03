using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using HappyEngine.Base;
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

        internal static void Remember(int npc, int map, string monster, string name) => Names[(npc, map)] = (monster, name);
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
        internal static void Send(long npcUid, long task)
        {
            var payload = new JObject { ["npcUid"] = npcUid, ["task"] = task };
            SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(UpMsgId,
                Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
        }
    }

    internal sealed class HuntChoiceBehaviour : MonoBehaviour
    {
        private sealed class Option { internal long Task; internal int Npc, Map, Element; }
        private readonly List<Option> _choices = new List<Option>();
        private readonly TradeInputBlocker _blocker = new TradeInputBlocker(false);
        private Rect _rect;
        private bool _visible, _closePending;
        private long _npcUid, _pending;
        private string _message = "";
        private static readonly string[] Elements = { "", "Neutro", "Vento", "Terra", "Fogo", "Água", "Veneno", "Sagrado", "Sombrio", "Fantasma", "Morto-vivo" };

        private void OnDestroy() => _blocker.Destroy();
        private void Update()
        {
            if (Interlocked.Exchange(ref HuntChoiceWindow.ResetPending, 0) != 0)
            {
                _visible = false;
                while (HuntChoiceWindow.Inbound.TryDequeue(out _)) { }
            }
            while (HuntChoiceWindow.Inbound.TryDequeue(out var body))
            {
                try
                {
                    var state = JObject.Parse(Encoding.UTF8.GetString(body));
                    _npcUid = (long?)state["npcUid"] ?? 0;
                    _message = (string)state["message"] ?? "";
                    _choices.Clear();
                    foreach (var x in state["choices"] as JArray ?? new JArray())
                        _choices.Add(new Option { Task = (long?)x["task"] ?? 0, Npc = (int?)x["npc"] ?? 0,
                            Map = (int?)x["map"] ?? 0, Element = (int?)x["element"] ?? 0 });
                    float w = Mathf.Min(590f, Screen.width - 32f), h = 180f + _choices.Count * 64f;
                    _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                    _visible = _npcUid > 0;
                }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hunt] invalid choice state: " + e.Message); }
            }
            if (_pending != 0)
            {
                long task = _pending; _pending = 0;
                try { HuntChoiceWindow.Send(_npcUid, task); }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.Hunt] choice send failed: " + e.Message); }
            }
            _blocker.Update(_visible, _rect);
            if (_closePending) { _visible = false; _closePending = false; }
        }
        private void OnGUI()
        {
            if (!_visible) return;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            { _closePending = true; Event.current.Use(); }
            _rect = GUI.Window(0x52334D56, _rect, Draw, "Caçador");
            if (Event.current.type == EventType.Repaint) _blocker.Update(true, _rect);
        }
        private void Draw(int id)
        {
            GUILayout.Space(10f);
            var label = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 16 };
            var button = new GUIStyle(GUI.skin.button) { wordWrap = true, fontSize = 15 };
            GUILayout.Label(_message, label, GUILayout.Height(48f));
            foreach (var option in _choices)
            {
                var names = HuntChoiceWindow.Names.TryGetValue((option.Npc, option.Map), out var found)
                    ? found : ("Monstro " + option.Npc, "Mapa " + option.Map);
                string element = option.Element > 0 && option.Element < Elements.Length ? Elements[option.Element] : "desconhecido";
                if (GUILayout.Button(names.Item1 + " — " + names.Item2 + " (" + element + ")", button, GUILayout.Height(54f)))
                    _pending = option.Task;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fechar", button, GUILayout.Height(32f))) _closePending = true;
            GUI.DragWindow(new Rect(0, 0, _rect.width, 22));
        }
    }
}
