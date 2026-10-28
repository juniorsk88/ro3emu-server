using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using HappyEngine.Base;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Player trade / send-to-character / account vault window (docs/systems/player-trade-vault.md). The original client
    /// has no such UI and its Unity prefabs cannot be authored, so the window is drawn with IMGUI (OnGUI) by a
    /// DontDestroyOnLoad MonoBehaviour. It talks to the server only through game messages <see cref="UpMsgId"/> (up) and
    /// <see cref="DownMsgId"/> (down), UTF-8 JSON. Opened by the server message {"t":"show"} and by F9 (not while a text
    /// input has keyboard focus). Set RO3EMU_NO_TRADE_WINDOW=1 to disable the whole hook.
    /// Threading: the receive callback only queues the bytes; they are parsed and applied at the start of
    /// <c>Update</c> on the main thread, and button clicks only queue actions that run there too, so the state IMGUI
    /// lays out never changes between its Layout and Repaint events.
    /// </summary>
    public static class TradeWindow
    {
        /// <summary>Must equal the server's PlayerTradeService.UpMsg.</summary>
        public const uint UpMsgId = 77020;
        /// <summary>Must equal the server's PlayerTradeService.DownMsg.</summary>
        public const uint DownMsgId = 77021;

        private const int MaxQueued = 256;

        internal static readonly ConcurrentQueue<byte[]> Inbound = new ConcurrentQueue<byte[]>();
        private static TradeWindowBehaviour _behaviour;
        private static bool _loggedDisabled;

        /// <summary>RO3EMU_NO_TRADE_WINDOW=1 (kill switch): nothing is installed and server messages are dropped.</summary>
        public static bool Disabled
        {
            get
            {
                try { return Environment.GetEnvironmentVariable("RO3EMU_NO_TRADE_WINDOW") == "1"; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>From the game-message receive hook (any thread): queue the JSON body for the main thread.</summary>
        public static void OnDown(ReadOnlyMemory<byte> memory)
        {
            try
            {
                if (Disabled || memory.Length == 0) return;
                while (Inbound.Count >= MaxQueued && Inbound.TryDequeue(out _)) { }
                Inbound.Enqueue(memory.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Trade] receive failed: " + e);
            }
        }

        /// <summary>From LuaPatches.Tick (once per LuaEnv); creates the window object once.</summary>
        internal static void Install()
        {
            try
            {
                if (Disabled)
                {
                    if (!_loggedDisabled) { _loggedDisabled = true; Debug.Log("[Ro3Emu.Trade] disabled by RO3EMU_NO_TRADE_WINDOW"); }
                    return;
                }
                if (_behaviour != null) return; // Unity null check: a destroyed object is recreated
                var go = new GameObject("Ro3Emu_TradeWindow");
                go.hideFlags = HideFlags.HideInHierarchy;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _behaviour = go.AddComponent<TradeWindowBehaviour>();
                Debug.Log("[Ro3Emu.Trade] window installed (F9 or server request opens it)");
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Trade] install failed: " + e);
            }
        }

        /// <summary>Sends one request (a JSON object with "op"); false when the connection is not there.</summary>
        internal static bool Send(JObject request)
        {
            try
            {
                var json = request.ToString(Newtonsoft.Json.Formatting.None);
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(UpMsgId, Encoding.UTF8.GetBytes(json));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Trade] send failed: " + e);
                return false;
            }
        }
    }

    /// <summary>State, server-message handling and actions; the drawing is in TradeWindowGui.cs.</summary>
    internal sealed partial class TradeWindowBehaviour : MonoBehaviour
    {
        private static readonly string[] TabKeys = { "trade", "send", "vault" };
        private static readonly string[] TabNames = { "Troca", "Enviar", "Cofre" };

        // Window.
        private bool _visible;
        private int _tab;
        private Rect _rect;
        private bool _rectInit;
        private float _scale = 1f;
        private float _lastToggle = -10f;
        private bool _textFocused;
        private readonly TradeInputBlocker _blocker = new TradeInputBlocker();

        private void OnDestroy()
        {
            _blocker.Destroy();
        }

        // Actions queued by OnGUI, run at the start of Update.
        private readonly Queue<Action> _pending = new Queue<Action>();

        // Server state (replaced wholesale, only in Update).
        private List<TradeItem> _bag = new List<TradeItem>();
        private long _bagZeny;
        private TradeState _trade = new TradeState();
        private VaultState _vault = new VaultState();
        private string _msg = "";
        private bool _msgOk = true;
        private float _readyAt;       // realtimeSinceStartup when trade.ready is accepted again
        private bool _readyEnabled = true;
        private float _readyRemain;
        private float _lastOpen = -10f;
        private float _lastPush = -10f;
        private float _openCheckAt;   // after a server "show": ask for the state if no push followed

        // Selections and inputs.
        private long _selBag, _selVault;
        private string _countText = "1", _vaultCountText = "1";
        private string _tradeName = "", _sendName = "";
        private string _tradeZenyText = "", _sendZenyText = "", _putZenyText = "", _takeZenyText = "";
        private readonly List<Pick> _offer = new List<Pick>(); // my trade offer as I last sent / the server last echoed it
        private long _offerZeny;
        private readonly List<Pick> _basket = new List<Pick>(); // send tab

        private void Update()
        {
            try
            {
                var queue = TradeWindow.Inbound;
                byte[] body;
                int guard = 0;
                while (guard++ < 64 && queue.TryDequeue(out body)) Apply(body);

                guard = 0;
                while (guard++ < 32 && _pending.Count > 0)
                {
                    var action = _pending.Dequeue();
                    try { action(); }
                    catch (Exception e) { Debug.LogError("[Ro3Emu.Trade] action failed: " + e); }
                }

                float now = Time.realtimeSinceStartup;
                _readyRemain = Mathf.Max(0f, _readyAt - now);
                _readyEnabled = _readyRemain <= 0f;
                if (_openCheckAt > 0f && now >= _openCheckAt)
                {
                    _openCheckAt = 0f;
                    if (_visible && _lastPush < now - 1.5f) RequestOpen();
                }
                if (_visible) EnsureRect();
                _blocker.Update(_visible && _rectInit, _rect);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Trade] update failed: " + e);
            }
        }

        // ---------------------------------------------------------------- server messages

        private void Apply(byte[] body)
        {
            try
            {
                JObject o = JObject.Parse(Encoding.UTF8.GetString(body));
                _lastPush = Time.realtimeSinceStartup;
                switch (TradeJson.S(o, "t"))
                {
                    case "show":
                        {
                            bool wasVisible = _visible;
                            int tab = Array.IndexOf(TabKeys, TradeJson.S(o, "tab"));
                            if (tab >= 0) _tab = tab;
                            _visible = true;
                            EnsureRect();
                            // The server normally pushes the state with "show"; ask only if nothing follows.
                            if (!wasVisible) _openCheckAt = Time.realtimeSinceStartup + 0.6f;
                            break;
                        }
                    case "bag":
                        _bag = TradeJson.Items(o["items"]);
                        _bagZeny = Math.Max(0, TradeJson.L(o, "zeny"));
                        Revalidate();
                        break;
                    case "trade":
                        _trade = TradeJson.Trade(o);
                        if (_trade.State == "open")
                        {
                            _readyAt = Time.realtimeSinceStartup + _trade.ReadyAfterMs / 1000f;
                            ResyncOffer();
                        }
                        else
                        {
                            _offer.Clear();
                            _offerZeny = 0;
                        }
                        break;
                    case "vault":
                        _vault = TradeJson.Vault(o);
                        Revalidate();
                        break;
                    case "msg":
                        _msg = TradeJson.S(o, "text");
                        _msgOk = TradeJson.B(o, "ok", true);
                        if (!_msgOk && _trade.State == "open") ResyncOffer(); // a refused offer: show what the server really has
                        break;
                    case "done":
                        ClearAfterDone(TradeJson.S(o, "what"));
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.Trade] ignored message: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>My offer draft = what the server echoes (the count of an offered item is its "stack" in the descriptor).</summary>
        private void ResyncOffer()
        {
            _offer.Clear();
            foreach (var item in _trade.Mine.Items) _offer.Add(new Pick { Item = item, N = item.Stack });
            _offerZeny = _trade.Mine.Zeny;
        }

        /// <summary>A finished operation: selections and drafts are cleared (nothing accumulates across "done").</summary>
        private void ClearAfterDone(string what)
        {
            _selBag = 0;
            _selVault = 0;
            _countText = "1";
            _vaultCountText = "1";
            switch (what)
            {
                case "trade":
                    _offer.Clear();
                    _offerZeny = 0;
                    _tradeZenyText = "";
                    break;
                case "send":
                    _basket.Clear();
                    _sendZenyText = "";
                    break;
                case "vault":
                    _putZenyText = "";
                    _takeZenyText = "";
                    break;
                default:
                    _offer.Clear();
                    _offerZeny = 0;
                    _basket.Clear();
                    _tradeZenyText = _sendZenyText = _putZenyText = _takeZenyText = "";
                    break;
            }
            RequestOpen();
        }

        /// <summary>Drops selections / basket entries whose items are gone and clamps counts to the new stacks.</summary>
        private void Revalidate()
        {
            if (_selBag != 0 && FindBag(_selBag) == null) { _selBag = 0; _countText = "1"; }
            if (_selVault != 0 && FindVault(_selVault) == null) { _selVault = 0; _vaultCountText = "1"; }
            for (int i = _basket.Count - 1; i >= 0; i--)
            {
                var current = FindBag(_basket[i].Item.Guid);
                if (current == null) { _basket.RemoveAt(i); continue; }
                _basket[i].Item = current;
                if (_basket[i].N > current.Stack) _basket[i].N = current.Stack;
            }
        }

        private TradeItem FindBag(long guid)
        {
            foreach (var it in _bag) if (it.Guid == guid) return it;
            return null;
        }

        private TradeItem FindVault(long id)
        {
            foreach (var it in _vault.Items) if (it.VaultId == id) return it;
            return null;
        }

        // ---------------------------------------------------------------- requests (all run in Update)

        private static bool Req(string op, Action<JObject> fill = null)
        {
            var request = new JObject { ["op"] = op };
            if (fill != null) fill(request);
            return TradeWindow.Send(request);
        }

        private void Fail(string text)
        {
            _msg = text;
            _msgOk = false;
        }

        /// <summary>Asks for the full state, at most twice a second.</summary>
        private void RequestOpen()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastOpen < 0.5f) return;
            _lastOpen = now;
            string tab = TabKeys[Mathf.Clamp(_tab, 0, TabKeys.Length - 1)];
            if (!Req("open", r => r["tab"] = tab)) Fail("Sem conexao com o servidor.");
        }

        private void Toggle()
        {
            _visible = !_visible;
            if (_visible)
            {
                EnsureRect();
                RequestOpen();
            }
            else
            {
                _textFocused = false;
            }
        }

        private void Close()
        {
            _visible = false;
            _textFocused = false;
        }

        private void SetTab(int tab)
        {
            if (tab < 0 || tab >= TabKeys.Length || tab == _tab) return;
            _tab = tab;
            _selBag = 0;
            _selVault = 0;
            _countText = "1";
            _vaultCountText = "1";
            RequestOpen();
        }

        private void SelectBag(long guid)
        {
            if (_selBag == guid) return;
            _selBag = guid;
            _countText = "1";
        }

        private void SelectVault(long id)
        {
            if (_selVault == id) return;
            _selVault = id;
            _vaultCountText = "1";
        }

        // Trade.
        private void Invite(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) { Fail("Digite o nome do jogador."); return; }
            if (!Req("trade.invite", r => r["name"] = name)) Fail("Sem conexao com o servidor.");
        }

        private void Simple(string op)
        {
            if (!Req(op)) Fail("Sem conexao com o servidor.");
        }

        private void SendOffer()
        {
            var items = TradeJson.GuidRows(_offer);
            long zeny = _offerZeny;
            if (!Req("trade.offer", r => { r["items"] = items; r["zeny"] = zeny; })) Fail("Sem conexao com o servidor.");
        }

        private void AddToOffer(long guid, int n)
        {
            var item = FindBag(guid);
            if (item == null) return;
            n = Mathf.Clamp(n, 1, item.Stack);
            var existing = _offer.Find(p => p.Item.Guid == guid);
            if (existing != null) existing.N = n;
            else _offer.Add(new Pick { Item = item, N = n });
            SendOffer();
        }

        private void RemoveFromOffer(long guid)
        {
            if (_offer.RemoveAll(p => p.Item.Guid == guid) > 0) SendOffer();
        }

        private void SetOfferZeny(long zeny)
        {
            _offerZeny = Math.Max(0, Math.Min(zeny, _bagZeny));
            SendOffer();
        }

        private void SetReady(bool on)
        {
            if (!Req("trade.ready", r => r["on"] = on)) Fail("Sem conexao com o servidor.");
        }

        // Send.
        private void AddToBasket(long guid, int n)
        {
            var item = FindBag(guid);
            if (item == null) return;
            n = Mathf.Clamp(n, 1, item.Stack);
            var existing = _basket.Find(p => p.Item.Guid == guid);
            if (existing != null) existing.N = n;
            else _basket.Add(new Pick { Item = item, N = n });
        }

        private void RemoveFromBasket(long guid)
        {
            _basket.RemoveAll(p => p.Item.Guid == guid);
        }

        private void DoSend(string name, long zeny)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) { Fail("Digite o nome do personagem."); return; }
            if (_basket.Count == 0 && zeny <= 0) { Fail("Escolha itens ou informe zeny para enviar."); return; }
            zeny = Math.Min(zeny, _bagZeny);
            var items = TradeJson.GuidRows(_basket);
            if (!Req("send", r => { r["name"] = name; r["items"] = items; r["zeny"] = zeny; })) Fail("Sem conexao com o servidor.");
        }

        // Vault.
        private void VaultPut(long guid, int n, long zeny)
        {
            var items = new JArray();
            if (guid != 0)
            {
                var item = FindBag(guid);
                if (item == null) return;
                items.Add(new JObject { ["guid"] = guid, ["n"] = Mathf.Clamp(n, 1, item.Stack) });
            }
            zeny = Math.Min(zeny, _bagZeny);
            if (items.Count == 0 && zeny <= 0) { Fail("Escolha um item ou informe zeny para guardar."); return; }
            if (!Req("vault.put", r => { r["items"] = items; r["zeny"] = zeny; })) Fail("Sem conexao com o servidor.");
        }

        private void VaultTake(long id, int n, long zeny)
        {
            var items = new JArray();
            if (id != 0)
            {
                var item = FindVault(id);
                if (item == null) return;
                items.Add(new JObject { ["id"] = id, ["n"] = Mathf.Clamp(n, 1, item.Stack) });
            }
            zeny = Math.Min(zeny, _vault.Zeny);
            if (items.Count == 0 && zeny <= 0) { Fail("Escolha um item ou informe zeny para retirar."); return; }
            if (!Req("vault.take", r => { r["items"] = items; r["zeny"] = zeny; })) Fail("Sem conexao com o servidor.");
        }

        // ---------------------------------------------------------------- hotkey

        /// <summary>True while a Unity UI text input (uGUI or TextMeshPro, e.g. the chat box) has keyboard focus.</summary>
        private static bool UiTextInputFocused()
        {
            try
            {
                var system = EventSystem.current;
                var selected = system != null ? system.currentSelectedGameObject : null;
                if (selected == null) return false;
                var tmp = selected.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var legacy = selected.GetComponent<InputField>();
                return legacy != null && legacy.isFocused;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HandleHotkey(Event ev)
        {
            if (ev.type != EventType.KeyDown || ev.keyCode != KeyCode.F9) return;
            if (_textFocused || UiTextInputFocused()) return;
            float now = Time.unscaledTime;
            if (now - _lastToggle < 0.25f) return; // key auto-repeat
            _lastToggle = now;
            _pending.Enqueue(Toggle);
            ev.Use();
        }

        /// <summary>Window size follows the screen height (readable on 1080p and 1440p); the position is kept on screen.</summary>
        private void EnsureRect()
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            float w = Mathf.Min(1000f * scale, Screen.width - 20f);
            float h = Mathf.Min(700f * scale, Screen.height - 20f);
            if (!_rectInit)
            {
                _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                _rectInit = true;
            }
            else
            {
                _rect.width = w;
                _rect.height = h;
            }
            _scale = scale;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - w));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - h));
        }
    }
}
