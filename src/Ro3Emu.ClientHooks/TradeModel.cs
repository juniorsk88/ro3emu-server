using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Ro3Emu.ClientHooks
{
    /// <summary>One item descriptor of the trade protocol (docs/systems/player-trade-vault.md, "Item descriptor I").</summary>
    internal sealed class TradeItem
    {
        /// <summary>Bag instance id (the identity used in trade / send / vault.put requests).</summary>
        public long Guid;
        /// <summary>Vault entry id (vault.take requests); 0 for bag and offer items.</summary>
        public long VaultId;
        public long Entry;
        public string Name = "";
        public int Stack = 1;
        public int Quality = 1;
        public int Level;
        /// <summary>Vault entries: the character that deposited it.</summary>
        public string From = "";

        /// <summary>The id selections and requests use: the vault id for vault entries, otherwise the bag guid.</summary>
        public long Key { get { return VaultId != 0 ? VaultId : Guid; } }

        /// <summary>IMGUI rich-text label "+lv name xN", the name coloured by quality.</summary>
        public string Rich(long count)
        {
            string name = Name;
            if (string.IsNullOrEmpty(name)) name = "Item #" + Entry;
            var sb = new StringBuilder(name.Length + 40);
            sb.Append("<color=").Append(QualityHex(Quality)).Append('>');
            if (Level > 0) sb.Append('+').Append(Level).Append(' ');
            foreach (char c in name)
                if (c != '<' && c != '>') sb.Append(c); // never let a server-supplied name open a rich-text tag
            sb.Append("</color> x").Append(count);
            return sb.ToString();
        }

        /// <summary>ItemConfig quality 1..6: grey, white, green, blue, purple, orange/gold.</summary>
        public static string QualityHex(int q)
        {
            switch (q)
            {
                case 2: return "#ffffff";
                case 3: return "#4cd964";
                case 4: return "#4aa3ff";
                case 5: return "#c27bff";
                case 6: return "#ffb82e";
                default: return "#a0a0a0";
            }
        }
    }

    internal sealed class TradeSide
    {
        public readonly List<TradeItem> Items = new List<TradeItem>();
        public long Zeny;
        public bool Ready;
    }

    internal sealed class TradeState
    {
        /// <summary>"none", "invited" or "open".</summary>
        public string State = "none";
        public string Peer = "";
        /// <summary>invited: true when I was invited, false when I invited.</summary>
        public bool Incoming;
        public TradeSide Mine = new TradeSide();
        public TradeSide Theirs = new TradeSide();
        public long ReadyAfterMs;
    }

    internal sealed class VaultState
    {
        public List<TradeItem> Items = new List<TradeItem>();
        public long Zeny;
        public int Capacity;
        public int Used;
    }

    /// <summary>An item chosen with a count (send basket, my trade offer).</summary>
    internal sealed class Pick
    {
        public TradeItem Item;
        public int N;
    }

    /// <summary>Tolerant readers over Newtonsoft's JToken (unknown keys are ignored, wrong types fall back to defaults).</summary>
    internal static class TradeJson
    {
        public static long L(JToken obj, string key, long def = 0)
        {
            var o = obj as JObject;
            JToken v = o != null ? o[key] : null;
            if (v == null) return def;
            switch (v.Type)
            {
                case JTokenType.Integer: return (long)v;
                case JTokenType.Float: return (long)(double)v;
                case JTokenType.String:
                    long parsed;
                    return long.TryParse((string)v, out parsed) ? parsed : def;
                case JTokenType.Boolean: return (bool)v ? 1 : 0;
                default: return def;
            }
        }

        public static string S(JToken obj, string key, string def = "")
        {
            var o = obj as JObject;
            JToken v = o != null ? o[key] : null;
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.String ? (string)v : v.ToString();
        }

        public static bool B(JToken obj, string key, bool def = false)
        {
            var o = obj as JObject;
            JToken v = o != null ? o[key] : null;
            if (v == null) return def;
            switch (v.Type)
            {
                case JTokenType.Boolean: return (bool)v;
                case JTokenType.Integer: return (long)v != 0;
                case JTokenType.String: return string.Equals((string)v, "true", StringComparison.OrdinalIgnoreCase);
                default: return def;
            }
        }

        private static int ClampInt(long v, int min)
        {
            if (v > int.MaxValue) return int.MaxValue;
            return v < min ? min : (int)v;
        }

        /// <summary>Bag / offer item {guid, entry, name, stack, q, lv} or vault entry {id, entry, name, stack, q, from}.</summary>
        public static TradeItem Item(JToken t)
        {
            if (!(t is JObject)) return null;
            var item = new TradeItem
            {
                Guid = L(t, "guid"),
                VaultId = L(t, "id"),
                Entry = L(t, "entry"),
                Name = S(t, "name"),
                Stack = ClampInt(L(t, "stack", 1), 1),
                Quality = ClampInt(L(t, "q", 1), 1),
                Level = ClampInt(L(t, "lv", 0), 0),
                From = S(t, "from"),
            };
            return item.Key == 0 ? null : item;
        }

        public static List<TradeItem> Items(JToken array)
        {
            var list = new List<TradeItem>();
            var a = array as JArray;
            if (a == null) return list;
            foreach (var t in a)
            {
                var item = Item(t);
                if (item != null) list.Add(item);
            }
            return list;
        }

        public static TradeSide Side(JToken t)
        {
            var side = new TradeSide();
            if (!(t is JObject)) return side;
            side.Items.AddRange(Items(((JObject)t)["items"]));
            side.Zeny = Math.Max(0, L(t, "zeny"));
            side.Ready = B(t, "ready");
            return side;
        }

        public static TradeState Trade(JObject o)
        {
            var s = new TradeState
            {
                State = S(o, "state", "none"),
                Peer = S(o, "peer"),
                Incoming = B(o, "incoming"),
                ReadyAfterMs = Math.Max(0, L(o, "readyAfterMs")),
            };
            if (s.State == "open")
            {
                s.Mine = Side(o["mine"]);
                s.Theirs = Side(o["theirs"]);
            }
            return s;
        }

        public static VaultState Vault(JObject o)
        {
            return new VaultState
            {
                Items = Items(o["items"]),
                Zeny = Math.Max(0, L(o, "zeny")),
                Capacity = ClampInt(L(o, "capacity"), 0),
                Used = ClampInt(L(o, "used"), 0),
            };
        }

        /// <summary>Keeps digits only, at most <paramref name="max"/> of them.</summary>
        public static string Digits(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (c < '0' || c > '9') continue;
                if (sb.Length >= max) break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s ?? "", out v) && v > 0 ? v : 0;
        }

        /// <summary>1..stack from the count field (empty or invalid counts as 1).</summary>
        public static int ClampCount(string text, int stack)
        {
            long n = ParseLong(text);
            if (n < 1) n = 1;
            if (n > stack) n = stack;
            return (int)n;
        }

        /// <summary>[{"guid":n,"n":count}] for trade.offer / send / vault.put.</summary>
        public static JArray GuidRows(List<Pick> picks)
        {
            var a = new JArray();
            foreach (var p in picks)
                a.Add(new JObject { ["guid"] = p.Item.Guid, ["n"] = p.N });
            return a;
        }
    }
}
