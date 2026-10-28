using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The IMGUI drawing of the trade window. Rules this file keeps: nothing that decides which controls exist is
    /// changed here (server state and selections only change in Update, so Layout and Repaint see the same controls);
    /// clicks only queue actions (<see cref="_pending"/>); text fields change only their own strings.
    /// </summary>
    internal sealed partial class TradeWindowBehaviour
    {
        private const int WindowId = 0x52334D55; // arbitrary, unlikely to clash with another IMGUI window

        private Styles _st;
        private Vector2 _bagScroll, _vaultScroll, _mineScroll, _theirsScroll, _basketScroll;
        private bool _drawFailed;

        private void OnGUI()
        {
            try
            {
                var ev = Event.current;
                if (ev == null) return;
                HandleHotkey(ev);
                if (!_visible)
                {
                    _textFocused = false;
                    return;
                }
                if (!_rectInit) return;
                EnsureStyles();
                // A click outside the window ends text editing in it (IMGUI keeps the keyboard focus otherwise).
                if (ev.type == EventType.MouseDown && !_rect.Contains(ev.mousePosition))
                {
                    GUI.FocusControl("");
                    _textFocused = false;
                }
                _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none, _st.Window);
                if (ev.type == EventType.Repaint) _blocker.Update(true, _rect); // follows a drag without a frame of lag
                // Typing in a window text field: the field has handled the key by now; swallow what is left of it.
                if (_textFocused && (ev.type == EventType.KeyDown || ev.type == EventType.KeyUp)) ev.Use();
            }
            catch (Exception e)
            {
                if (!_drawFailed) Debug.LogError("[Ro3Emu.Trade] draw failed, window hidden: " + e);
                _drawFailed = true;
                _visible = false;
            }
        }

        // ---------------------------------------------------------------- window

        private void DrawWindow(int id)
        {
            var s = _st;
            float sc = s.Scale;
            float closeSize = 26f * sc;
            GUI.Box(new Rect(0f, 0f, _rect.width, s.TitleHeight), GUIContent.none, s.TitleBar);
            GUI.Label(new Rect(12f * sc, 0f, _rect.width - closeSize - 24f * sc, s.TitleHeight), "Troca, envio e cofre", s.Title);
            if (GUI.Button(new Rect(_rect.width - closeSize - 8f * sc, (s.TitleHeight - closeSize) / 2f, closeSize, closeSize), "X", s.Button))
            {
                GUI.FocusControl("");
                _pending.Enqueue(Close);
            }

            GUILayout.BeginVertical();
            int tab = GUILayout.Toolbar(_tab, TabNames, s.Tab, GUILayout.Height(32f * sc));
            if (tab != _tab)
            {
                GUI.FocusControl("");
                int t = tab;
                _pending.Enqueue(() => SetTab(t));
            }
            GUILayout.Space(6f * sc);
            switch (_tab)
            {
                case 0: DrawTradeTab(); break;
                case 1: DrawSendTab(); break;
                default: DrawVaultTab(); break;
            }
            GUILayout.Space(4f * sc);
            GUILayout.Label(string.IsNullOrEmpty(_msg) ? " " : _msg, _msgOk ? s.Ok : s.Err);
            GUILayout.EndVertical();

            // Sampled on Repaint only (the focused-control name is not reliable during the other events); F9 reads this cache.
            if (Event.current.type == EventType.Repaint)
            {
                string focused = GUI.GetNameOfFocusedControl();
                _textFocused = !string.IsNullOrEmpty(focused) && focused.StartsWith("rt_", StringComparison.Ordinal);
            }
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - closeSize - 16f * sc, s.TitleHeight));
        }

        // ---------------------------------------------------------------- widgets

        /// <summary>A button; a click queues <paramref name="action"/> for Update. Disabled buttons do nothing.</summary>
        private bool Btn(string text, bool enabled, Action action, params GUILayoutOption[] options)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            bool clicked = GUILayout.Button(text, _st.Button, options);
            GUI.enabled = previous;
            if (clicked)
            {
                GUI.FocusControl("");
                _pending.Enqueue(action);
            }
            return clicked;
        }

        private string TextBox(string control, string value, int maxLength, float width)
        {
            GUI.SetNextControlName(control);
            return GUILayout.TextField(value ?? "", maxLength, _st.Field, GUILayout.Width(width));
        }

        private string NumberBox(string control, string value, int maxLength, float width)
        {
            return TradeJson.Digits(TextBox(control, value, maxLength, width), maxLength);
        }

        /// <summary>Selectable scroll list of items; a click queues the selection.</summary>
        private void ItemList(List<TradeItem> items, ref Vector2 scroll, long selected, Action<long> onSelect, bool showFrom)
        {
            var s = _st;
            scroll = GUILayout.BeginScrollView(scroll, s.Panel, GUILayout.ExpandHeight(true));
            if (items.Count == 0) GUILayout.Label("(vazio)", s.Muted);
            foreach (var it in items)
            {
                string text = it.Rich(it.Stack);
                if (showFrom && !string.IsNullOrEmpty(it.From)) text += "  <color=#8a90a0>(de " + it.From.Replace('<', ' ').Replace('>', ' ') + ")</color>";
                bool isSelected = it.Key == selected;
                if (GUILayout.Button(text, isSelected ? s.RowSelected : s.Row))
                {
                    GUI.FocusControl("");
                    long key = it.Key;
                    _pending.Enqueue(() => onSelect(key));
                }
            }
            GUILayout.EndScrollView();
        }

        /// <summary>Rows of picks (offer, basket) with an optional remove button each.</summary>
        private void PickList(List<Pick> picks, ref Vector2 scroll, Action<long> onRemove)
        {
            var s = _st;
            scroll = GUILayout.BeginScrollView(scroll, s.Panel, GUILayout.ExpandHeight(true));
            if (picks.Count == 0) GUILayout.Label("(nenhum item)", s.Muted);
            foreach (var p in picks)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.Item.Rich(p.N), s.RichLabel);
                if (onRemove != null && GUILayout.Button("x", s.SmallButton, GUILayout.Width(26f * s.Scale)))
                {
                    GUI.FocusControl("");
                    long guid = p.Item.Guid;
                    _pending.Enqueue(() => onRemove(guid));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        /// <summary>Server-side side of a trade (read-only rows).</summary>
        private void SideList(List<TradeItem> items, ref Vector2 scroll)
        {
            var s = _st;
            scroll = GUILayout.BeginScrollView(scroll, s.Panel, GUILayout.ExpandHeight(true));
            if (items.Count == 0) GUILayout.Label("(nenhum item)", s.Muted);
            foreach (var it in items) GUILayout.Label(it.Rich(it.Stack), s.RichLabel);
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// The selected bag item, its count field and the action button. The structure depends only on
        /// <see cref="_selBag"/> and the bag, which change in Update.
        /// </summary>
        private void BagPicker(string actionText, Action<long, int> action)
        {
            var s = _st;
            var sel = _selBag != 0 ? FindBag(_selBag) : null;
            if (sel == null)
            {
                GUILayout.Label("Selecione um item da mochila.", s.Muted);
                return;
            }
            GUILayout.Label(sel.Rich(sel.Stack), s.RichLabel);
            int count = 1;
            if (sel.Stack > 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Quantidade:", s.Label, GUILayout.ExpandWidth(false));
                string text = NumberBox("rt_count", _countText, 9, 90f * s.Scale);
                if (TradeJson.ParseLong(text) > sel.Stack) text = sel.Stack.ToString();
                _countText = text;
                GUILayout.Label("de " + sel.Stack, s.Muted, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
                count = TradeJson.ClampCount(_countText, sel.Stack);
            }
            long guid = sel.Guid;
            int n = count;
            Btn(actionText, true, () => action(guid, n));
        }

        private void VaultPicker(string actionText, Action<long, int> action)
        {
            var s = _st;
            var sel = _selVault != 0 ? FindVault(_selVault) : null;
            if (sel == null)
            {
                GUILayout.Label("Selecione um item do cofre.", s.Muted);
                return;
            }
            GUILayout.Label(sel.Rich(sel.Stack), s.RichLabel);
            int count = 1;
            if (sel.Stack > 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Quantidade:", s.Label, GUILayout.ExpandWidth(false));
                string text = NumberBox("rt_vcount", _vaultCountText, 9, 90f * s.Scale);
                if (TradeJson.ParseLong(text) > sel.Stack) text = sel.Stack.ToString();
                _vaultCountText = text;
                GUILayout.Label("de " + sel.Stack, s.Muted, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
                count = TradeJson.ClampCount(_vaultCountText, sel.Stack);
            }
            long id = sel.VaultId;
            int n = count;
            Btn(actionText, true, () => action(id, n));
        }

        private float ContentWidth { get { return _rect.width - _st.Window.padding.horizontal; } }

        private void BagHeader()
        {
            GUILayout.Label("Mochila  -  Zeny: " + _bagZeny.ToString("N0"), _st.Header);
        }

        // ---------------------------------------------------------------- tabs

        private void DrawTradeTab()
        {
            var t = _trade;
            if (t.State == "open") DrawTradeOpen(t);
            else if (t.State == "invited") DrawTradeInvited(t);
            else DrawTradeIdle();
        }

        private void DrawTradeIdle()
        {
            var s = _st;
            GUILayout.Label("Convide um jogador online para uma troca segura. Os dois precisam estar na mesma area, fora de masmorras.", s.Label);
            GUILayout.Space(8f * s.Scale);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Jogador:", s.Label, GUILayout.ExpandWidth(false));
            _tradeName = TextBox("rt_tradename", _tradeName, 32, 260f * s.Scale);
            string name = _tradeName;
            Btn("Convidar", true, () => Invite(name), GUILayout.Width(120f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f * s.Scale);
            GUILayout.Label("Os itens trocados chegam pelo correio de cada jogador quando os dois confirmam.", s.Muted);
            GUILayout.FlexibleSpace();
        }

        private void DrawTradeInvited(TradeState t)
        {
            var s = _st;
            if (t.Incoming)
            {
                GUILayout.Label(Plain(t.Peer) + " convidou voce para uma troca.", s.Header);
                GUILayout.Space(8f * s.Scale);
                GUILayout.BeginHorizontal();
                Btn("Aceitar", true, () => Simple("trade.accept"), GUILayout.Width(140f * s.Scale));
                Btn("Recusar", true, () => Simple("trade.decline"), GUILayout.Width(140f * s.Scale));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("Convite enviado para " + Plain(t.Peer) + ". Aguardando resposta...", s.Header);
                GUILayout.Space(8f * s.Scale);
                GUILayout.BeginHorizontal();
                Btn("Cancelar convite", true, () => Simple("trade.cancel"), GUILayout.Width(180f * s.Scale));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            GUILayout.FlexibleSpace();
        }

        private void DrawTradeOpen(TradeState t)
        {
            var s = _st;
            float bagWidth = Mathf.Floor(ContentWidth * 0.36f);
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.Width(bagWidth));
            BagHeader();
            ItemList(_bag, ref _bagScroll, _selBag, k => SelectBag(k), false);
            BagPicker("Adicionar a oferta", (guid, n) => AddToOffer(guid, n));
            GUILayout.EndVertical();

            GUILayout.Space(8f * s.Scale);
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical();
            GUILayout.Label("Minha oferta", s.Header);
            GUILayout.Label(t.Mine.Ready ? "[x] Pronto" : "[  ] Aguardando", t.Mine.Ready ? s.Ok : s.Muted);
            PickList(_offer, ref _mineScroll, k => RemoveFromOffer(k));
            GUILayout.Label("Zeny: " + t.Mine.Zeny.ToString("N0"), s.Label);
            GUILayout.EndVertical();

            GUILayout.Space(6f * s.Scale);
            GUILayout.BeginVertical();
            GUILayout.Label("Oferta de " + Plain(t.Peer), s.Header);
            GUILayout.Label(t.Theirs.Ready ? "[x] Pronto" : "[  ] Aguardando", t.Theirs.Ready ? s.Ok : s.Muted);
            SideList(t.Theirs.Items, ref _theirsScroll);
            GUILayout.Label("Zeny: " + t.Theirs.Zeny.ToString("N0"), s.Label);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Zeny:", s.Label, GUILayout.ExpandWidth(false));
            _tradeZenyText = NumberBox("rt_tradezeny", _tradeZenyText, 12, 150f * s.Scale);
            long zeny = TradeJson.ParseLong(_tradeZenyText);
            Btn("Definir zeny", true, () => SetOfferZeny(zeny), GUILayout.Width(150f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool on = !t.Mine.Ready;
            string readyText = t.Mine.Ready ? "Desmarcar pronto" : "Estou pronto";
            if (!_readyEnabled) readyText += " (" + Mathf.CeilToInt(_readyRemain) + "s)";
            Btn(readyText, _readyEnabled, () => SetReady(on), GUILayout.Width(220f * s.Scale));
            Btn("Cancelar troca", true, () => Simple("trade.cancel"), GUILayout.Width(180f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawSendTab()
        {
            var s = _st;
            float bagWidth = Mathf.Floor(ContentWidth * 0.46f);
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.Width(bagWidth));
            BagHeader();
            ItemList(_bag, ref _bagScroll, _selBag, k => SelectBag(k), false);
            BagPicker("Adicionar ao envio", (guid, n) => AddToBasket(guid, n));
            GUILayout.EndVertical();

            GUILayout.Space(8f * s.Scale);
            GUILayout.BeginVertical();
            GUILayout.Label("Enviar para o personagem:", s.Header);
            _sendName = TextBox("rt_sendname", _sendName, 32, 300f * s.Scale);
            GUILayout.Space(4f * s.Scale);
            GUILayout.Label("Itens do envio:", s.Label);
            PickList(_basket, ref _basketScroll, k => RemoveFromBasket(k));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Zeny:", s.Label, GUILayout.ExpandWidth(false));
            _sendZenyText = NumberBox("rt_sendzeny", _sendZenyText, 12, 150f * s.Scale);
            GUILayout.EndHorizontal();
            string name = _sendName;
            long zeny = TradeJson.ParseLong(_sendZenyText);
            GUILayout.BeginHorizontal();
            Btn("Enviar", true, () => DoSend(name, zeny), GUILayout.Width(140f * s.Scale));
            Btn("Limpar", _basket.Count > 0, () => _basket.Clear(), GUILayout.Width(120f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label("O destinatario recebe pelo correio, mesmo offline.", s.Muted);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawVaultTab()
        {
            var s = _st;
            float half = Mathf.Floor((ContentWidth - 8f * s.Scale) * 0.5f);
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.Width(half));
            GUILayout.Label("Cofre da conta  -  " + _vault.Used + "/" + _vault.Capacity + "  -  Zeny: " + _vault.Zeny.ToString("N0"), s.Header);
            ItemList(_vault.Items, ref _vaultScroll, _selVault, k => SelectVault(k), true);
            VaultPicker("Retirar item", (id, n) => VaultTake(id, n, 0));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Zeny:", s.Label, GUILayout.ExpandWidth(false));
            _takeZenyText = NumberBox("rt_takezeny", _takeZenyText, 12, 150f * s.Scale);
            long take = TradeJson.ParseLong(_takeZenyText);
            Btn("Retirar zeny", true, () => VaultTake(0, 0, take), GUILayout.Width(150f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label("Retiradas chegam pelo correio do personagem atual.", s.Muted);
            GUILayout.EndVertical();

            GUILayout.Space(8f * s.Scale);
            GUILayout.BeginVertical(GUILayout.Width(half));
            BagHeader();
            ItemList(_bag, ref _bagScroll, _selBag, k => SelectBag(k), false);
            BagPicker("Guardar item", (guid, n) => VaultPut(guid, n, 0));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Zeny:", s.Label, GUILayout.ExpandWidth(false));
            _putZenyText = NumberBox("rt_putzeny", _putZenyText, 12, 150f * s.Scale);
            long put = TradeJson.ParseLong(_putZenyText);
            Btn("Guardar zeny", true, () => VaultPut(0, 0, put), GUILayout.Width(150f * s.Scale));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        /// <summary>Text for plain (non-rich) labels: the server's strings are never parsed as markup there.</summary>
        private static string Plain(string text)
        {
            return string.IsNullOrEmpty(text) ? "?" : text;
        }

        // ---------------------------------------------------------------- styles

        private void EnsureStyles()
        {
            if (_st != null && Mathf.Abs(_st.Scale - _scale) < 0.001f) return;
            _st = new Styles(_scale);
        }

        private static readonly Dictionary<uint, Texture2D> TextureCache = new Dictionary<uint, Texture2D>();

        /// <summary>3x3 texture, border pixels in <paramref name="border"/> (used with RectOffset(1,1,1,1)).</summary>
        private static Texture2D Tex(Color fill, Color border)
        {
            uint key = Pack(fill) * 31u + Pack(border);
            Texture2D t;
            if (TextureCache.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(3, 3, TextureFormat.RGBA32, false);
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    t.SetPixel(x, y, (x == 1 && y == 1) ? fill : border);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            t.Apply();
            TextureCache[key] = t;
            return t;
        }

        private static uint Pack(Color c)
        {
            Color32 k = c;
            return ((uint)k.r << 24) | ((uint)k.g << 16) | ((uint)k.b << 8) | k.a;
        }

        private static void Back(GUIStyle style, Texture2D normal, Texture2D hover, Texture2D active)
        {
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = active;
            style.focused.background = normal;
            style.onNormal.background = active;
            style.onHover.background = active;
            style.onActive.background = active;
            style.onFocused.background = active;
        }

        private static void TextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        /// <summary>Dark, readable GUIStyles scaled with the screen height; built lazily inside OnGUI (GUI.skin needs it).</summary>
        private sealed class Styles
        {
            public readonly float Scale;
            public readonly float TitleHeight;
            public readonly GUIStyle Window, TitleBar, Title, Label, RichLabel, Muted, Header, Ok, Err, Button, SmallButton, Tab, Field, Panel, Row, RowSelected;

            public Styles(float scale)
            {
                Scale = scale;
                int font = Mathf.RoundToInt(16f * scale);
                int pad = Mathf.RoundToInt(10f * scale);
                TitleHeight = 36f * scale;

                Color text = new Color(0.92f, 0.93f, 0.96f);
                Color muted = new Color(0.62f, 0.66f, 0.74f);
                Color edge = new Color(0.36f, 0.40f, 0.50f);
                var panelTex = Tex(new Color(0.11f, 0.12f, 0.16f, 1f), new Color(0.24f, 0.27f, 0.34f, 1f));
                var buttonTex = Tex(new Color(0.20f, 0.24f, 0.32f, 1f), edge);
                var buttonHover = Tex(new Color(0.27f, 0.33f, 0.45f, 1f), new Color(0.55f, 0.62f, 0.78f, 1f));
                var buttonDown = Tex(new Color(0.15f, 0.18f, 0.25f, 1f), edge);
                var tabOn = Tex(new Color(0.28f, 0.42f, 0.70f, 1f), new Color(0.55f, 0.68f, 0.95f, 1f));
                var rowTex = Tex(new Color(0.14f, 0.16f, 0.21f, 1f), new Color(0.14f, 0.16f, 0.21f, 1f));
                var rowHover = Tex(new Color(0.20f, 0.23f, 0.31f, 1f), new Color(0.20f, 0.23f, 0.31f, 1f));
                var rowSel = Tex(new Color(0.27f, 0.39f, 0.62f, 1f), new Color(0.45f, 0.58f, 0.85f, 1f));
                var fieldTex = Tex(new Color(0.05f, 0.06f, 0.08f, 1f), edge);
                var fieldFocus = Tex(new Color(0.07f, 0.08f, 0.11f, 1f), new Color(0.55f, 0.68f, 0.95f, 1f));
                var border1 = new RectOffset(1, 1, 1, 1);

                Window = new GUIStyle(GUI.skin.window) { border = border1, padding = new RectOffset(pad, pad, Mathf.RoundToInt(TitleHeight) + pad, pad) };
                Back(Window, Tex(new Color(0.08f, 0.09f, 0.12f, 0.98f), edge), null, null);
                Window.hover.background = Window.normal.background;
                Window.active.background = Window.normal.background;
                Window.onNormal.background = Window.normal.background;
                Window.onHover.background = Window.normal.background;
                Window.onActive.background = Window.normal.background;
                Window.onFocused.background = Window.normal.background;
                Window.focused.background = Window.normal.background;
                TextColor(Window, text);

                TitleBar = new GUIStyle(GUI.skin.box) { border = border1, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
                Back(TitleBar, Tex(new Color(0.15f, 0.18f, 0.26f, 1f), edge), null, null);
                TitleBar.hover.background = TitleBar.normal.background;
                TitleBar.active.background = TitleBar.normal.background;
                TitleBar.onNormal.background = TitleBar.normal.background;

                Title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(18f * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false };
                TextColor(Title, text);

                Label = new GUIStyle(GUI.skin.label) { fontSize = font, wordWrap = true, richText = false, alignment = TextAnchor.MiddleLeft };
                TextColor(Label, text);
                RichLabel = new GUIStyle(Label) { richText = true, wordWrap = false, clipping = TextClipping.Clip };
                Muted = new GUIStyle(Label);
                TextColor(Muted, muted);
                Header = new GUIStyle(Label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
                Ok = new GUIStyle(Label);
                TextColor(Ok, new Color(0.40f, 0.87f, 0.48f));
                Err = new GUIStyle(Label);
                TextColor(Err, new Color(1f, 0.45f, 0.42f));

                Button = new GUIStyle(GUI.skin.button)
                {
                    fontSize = font, border = border1, alignment = TextAnchor.MiddleCenter, wordWrap = false,
                    padding = new RectOffset(pad, pad, Mathf.RoundToInt(5f * scale), Mathf.RoundToInt(5f * scale)),
                    margin = new RectOffset(2, 2, 2, 2),
                };
                Back(Button, buttonTex, buttonHover, buttonDown);
                TextColor(Button, text);
                SmallButton = new GUIStyle(Button) { padding = new RectOffset(2, 2, 2, 2), fixedHeight = 0f };

                Tab = new GUIStyle(Button) { fontStyle = FontStyle.Bold };
                Back(Tab, buttonTex, buttonHover, tabOn);
                Tab.onNormal.background = tabOn;
                Tab.onHover.background = tabOn;
                Tab.onActive.background = tabOn;
                Tab.onFocused.background = tabOn;
                Tab.active.background = buttonDown;

                Field = new GUIStyle(GUI.skin.textField)
                {
                    fontSize = font, border = border1, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
                    padding = new RectOffset(pad / 2, pad / 2, Mathf.RoundToInt(4f * scale), Mathf.RoundToInt(4f * scale)),
                    margin = new RectOffset(2, 2, 2, 2),
                };
                Back(Field, fieldTex, fieldTex, fieldTex);
                Field.focused.background = fieldFocus;
                Field.onNormal.background = fieldTex;
                Field.onFocused.background = fieldFocus;
                TextColor(Field, text);

                Panel = new GUIStyle(GUI.skin.box) { border = border1, padding = new RectOffset(4, 4, 4, 4), margin = new RectOffset(2, 2, 2, 2) };
                Back(Panel, panelTex, panelTex, panelTex);
                TextColor(Panel, text);

                Row = new GUIStyle(GUI.skin.button)
                {
                    fontSize = font, richText = true, border = border1, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip,
                    padding = new RectOffset(pad, pad, Mathf.RoundToInt(3f * scale), Mathf.RoundToInt(3f * scale)),
                    margin = new RectOffset(0, 0, 1, 1),
                };
                Back(Row, rowTex, rowHover, rowSel);
                Row.onNormal.background = rowSel;
                TextColor(Row, text);
                RowSelected = new GUIStyle(Row);
                Back(RowSelected, rowSel, rowSel, rowSel);
                TextColor(RowSelected, text);
            }
        }
    }
}
