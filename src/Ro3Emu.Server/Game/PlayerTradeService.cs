using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Direct trade between two online players, sends to any character and the account vault (docs/systems/player-trade-vault.md).
/// Not original behaviour: the original client has no such windows; Ro3Emu.ClientHooks.TradeWindow talks to this service with
/// JSON game messages <see cref="UpMsg"/> / <see cref="DownMsg"/>. Every move of goods is one MarketStore transaction (bags,
/// currency, the vault record and the recipients' mail), so the economy ledger sees ordinary transfers; received goods arrive
/// as system mail, like stall purchases, and the claim checks the bag capacity.
/// </summary>
public sealed class PlayerTradeService : Messaging.IMessageModule
{
    /// <summary>Client → server: a JSON object with "op".</summary>
    public const uint UpMsg = 77020;
    /// <summary>Server → client: a JSON object with "t".</summary>
    public const uint DownMsg = 77021;

    internal const string VaultKind = "vault";
    private const int MaxRows = 10, VaultSlots = 60, ZenyId = 1;
    private static readonly TimeSpan ReadyDelay = TimeSpan.FromSeconds(2), InviteLife = TimeSpan.FromSeconds(60),
        TradeLife = TimeSpan.FromMinutes(5);

    private readonly MarketStore market;
    private readonly GameData data;
    private readonly RoleStore roles;
    private readonly SessionRegistry sessions;
    private readonly TimeProvider clock;
    private readonly string logPath;
    private readonly ILogger<PlayerTradeService> log;
    private readonly object gate = new();
    private readonly Dictionary<long, Trade> trades = new(); // by role id (both parties)
    private readonly Dictionary<long, Invite> invites = new(); // by invited role id
    private readonly object logGate = new();

    public PlayerTradeService(MarketStore market, GameData data, RoleStore roles, SessionRegistry sessions, SceneService scenes,
        ChatService chat, TimeProvider clock, IOptions<ServerOptions> options, ILogger<PlayerTradeService> log)
    {
        this.market = market; this.data = data; this.roles = roles; this.sessions = sessions; this.clock = clock; this.log = log;
        logPath = Path.Combine(options.Value.DataDir, "player-trade-log.jsonl");
        scenes.Leaving.Add(s => LeftAsync(s));
        chat.PlayerCommands["/troca"] = (s, args) => CommandAsync(s, "trade", args);
        chat.PlayerCommands["/enviar"] = (s, args) => CommandAsync(s, "send", args);
        chat.PlayerCommands["/cofre"] = (s, args) => CommandAsync(s, "vault", args);
    }

    public void Map(Messaging.MessageRoutes routes)
    {
        // The handler reserves what it changes through MarketStore itself (like trade and stalls).
        routes.AdmitEconomic(id => id == UpMsg);
        routes.Map(UpMsg, "player-trade", HandleAsync);
    }

    // ---- state ----

    private sealed class Side
    {
        public List<(long Guid, int Count)> Items { get; set; } = new();
        public long Zeny { get; set; }
        public bool Ready { get; set; }
    }

    private sealed class Trade
    {
        public long A, B;
        public Side SideA { get; } = new();
        public Side SideB { get; } = new();
        public DateTimeOffset Created, Changed;
        public bool Committing;
        public Side Of(long role) => role == A ? SideA : SideB;
        public long Other(long role) => role == A ? B : A;
    }

    private sealed record Invite(long From, DateTimeOffset Expires);

    public sealed class VaultRow
    {
        public string Account { get; set; } = "";
        public long NextId { get; set; } = 1;
        public List<VaultEntry> Entries { get; set; } = new();
        public Dictionary<long, long> Zeny { get; set; } = new();
    }

    public sealed class VaultEntry
    {
        public long Id { get; set; }
        public long Owner { get; set; }
        public InvItem Item { get; set; } = new();
    }

    // ---- messages ----

    private async Task CommandAsync(GameSession s, string tab, string args)
    {
        if (s.Role == null || s.Progress == null) return;
        await PushAsync(s, new { t = "show", tab });
        await SendAllAsync(s);
        if (tab == "trade" && args.Trim().Length > 0) await InviteAsync(s, args.Trim());
    }

    private async Task HandleAsync(GameSession s, uint msgId, ReadOnlyMemory<byte> body)
    {
        if (s.Role == null || s.Progress == null || body.Length is 0 or > 16 * 1024) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            try
            {
                await SweepAsync();
                switch (Str(root, "op"))
                {
                    case "open":
                        if (Str(root, "tab") is { Length: > 0 } tab) await PushAsync(s, new { t = "show", tab });
                        await SendAllAsync(s);
                        break;
                    case "bag": await SendBagAsync(s); break;
                    case "trade.invite": await InviteAsync(s, Str(root, "name")); break;
                    case "trade.accept": await AcceptAsync(s); break;
                    case "trade.decline": await DeclineAsync(s); break;
                    case "trade.offer": await OfferAsync(s, root); break;
                    case "trade.ready": await ReadyAsync(s, Bool(root, "on")); break;
                    case "trade.cancel": await CancelAsync(s, "Troca cancelada."); break;
                    case "send": await SendToAsync(s, root); break;
                    case "vault.get": await SendVaultAsync(s); break;
                    case "vault.put": await VaultPutAsync(s, root); break;
                    case "vault.take": await VaultTakeAsync(s, root); break;
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Player trade request {Op} of {Role} failed", Str(root, "op"), s.Role.Name);
                await SayAsync(s, "Não foi possível concluir agora. Tente novamente.", false);
            }
        }
    }

    // ---- helpers ----

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    /// <summary>[{"guid"|"id":n,"n":count}] → rows (null when malformed, duplicated or over the row limit).</summary>
    private static List<(long Id, int Count)>? Rows(JsonElement root, string idName)
    {
        var rows = new List<(long, int)>();
        if (!root.TryGetProperty("items", out var items)) return rows;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxRows) return null;
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) return null;
            long id = Num(row, idName), count = Num(row, "n");
            if (id <= 0 || count is < 1 or > int.MaxValue || rows.Any(r => r.Item1 == id)) return null;
            rows.Add((id, (int)count));
        }
        return rows;
    }

    private GameSession? Online(long role) => sessions.All().FirstOrDefault(x => x.Role?.Id == role && x.Progress != null);

    private Task PushAsync(GameSession s, object payload) =>
        s.SendGameRawAsync(DownMsg, JsonSerializer.SerializeToUtf8Bytes(payload));

    private Task SayAsync(GameSession s, string text, bool ok) => PushAsync(s, new { t = "msg", text, ok });

    private int Quality(int entry) => data.Row("ItemConfig", entry) is { } row ? (int)GameData.Int(row, "_iQuality") : 1;

    private object View(InvItem i, int? stack = null) => new
    {
        guid = i.Guid, entry = i.Entry, name = data.ItemName(i.Entry), stack = stack ?? i.Stack, q = Quality(i.Entry),
    };

    /// <summary>
    /// Why an item cannot change hands (null when it can). The trading house's rules (bound, locked, worn, reserved totems,
    /// developed cards and Soul Echoes), except the one that keeps ordinary appraised gear off the market: players may give gear.
    /// </summary>
    private string? TransferError(GameSession s, InvItem item)
    {
        if (item.Grid != 0) return "item equipado";
        if (item.IsLocked) return "item bloqueado";
        long nowSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        if (item.BindType == 1 || item.BindType == 2 && item.BindExpire > nowSeconds) return "item vinculado ao personagem";
        if (item.Attrs?.GetValueOrDefault(10001) > 0) return "item em uso";
        if (TotemService.IsReserved(s, item)) return "totem em uso";
        if (data.Row("ItemConfig", item.Entry) is not { } row) return "item desconhecido";
        long type = GameData.Int(row, "_iType");
        if (type == 1 || item.Entry is 100 or 101) return "moeda";
        if (type == 4 && ((item.Attrs?.GetValueOrDefault(501) ?? 0) > 0 || (item.Attrs?.GetValueOrDefault(502) ?? 0) > 0))
            return "carta desenvolvida";
        if (type == 8 && ((item.Attrs?.GetValueOrDefault(1001) ?? 0) > 0 || (item.Attrs?.GetValueOrDefault(1003) ?? 0) > 0))
            return "eco de alma desenvolvido";
        return null;
    }

    private static long Zeny(GameSession s) => s.Progress!.Currency.GetValueOrDefault(ZenyId);

    private string Describe(IEnumerable<InvItem> items, long zeny)
    {
        var parts = items.Select(i => i.Stack > 1 ? $"{i.Stack}x {data.ItemName(i.Entry)}" : data.ItemName(i.Entry)).ToList();
        if (zeny > 0) parts.Add($"{zeny:N0} zeny".Replace(',', '.'));
        return parts.Count == 0 ? "nada" : string.Join(", ", parts);
    }

    private void Log(string kind, object entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(new { time = clock.GetUtcNow().ToString("O"), kind, entry });
            lock (logGate) File.AppendAllText(logPath, line + "\n", Encoding.UTF8);
        }
        catch (Exception e) { log.LogWarning(e, "Could not append to the player trade log"); }
    }

    // ---- bag / vault views ----

    private async Task SendAllAsync(GameSession s)
    {
        await SendBagAsync(s);
        await SendTradeAsync(s);
        await SendVaultAsync(s);
    }

    private Task SendBagAsync(GameSession s) => PushAsync(s, new
    {
        t = "bag", zeny = Zeny(s),
        items = s.Items.Where(i => TransferError(s, i) == null).OrderBy(i => i.Pack).ThenBy(i => i.Sort).Select(i => View(i)).ToArray(),
    });

    private async Task SendVaultAsync(GameSession s)
    {
        if (s.Account == null) return;
        VaultRow? row = null;
        int code = await market.RunAsync(s, tx => { row = tx.Get<VaultRow>(VaultKind, s.Account); return 0; }, command: "player-vault-view");
        if (code != 0) return;
        row ??= new VaultRow();
        await PushAsync(s, new
        {
            t = "vault", zeny = row.Zeny.Values.Sum(), capacity = VaultSlots, used = row.Entries.Count,
            items = row.Entries.Select(e => new
            {
                id = e.Id, entry = e.Item.Entry, name = data.ItemName(e.Item.Entry), stack = e.Item.Stack, q = Quality(e.Item.Entry),
                from = roles.Get(e.Owner)?.Name ?? "?",
            }).ToArray(),
        });
    }

    // ---- trade ----

    private object TradeView(Trade t, long me)
    {
        var mine = Online(me);
        var other = Online(t.Other(me));
        object SideView(Side side, GameSession? owner) => new
        {
            items = owner == null ? Array.Empty<object>() : side.Items
                .Select(r => (Item: owner.Items.FirstOrDefault(i => i.Guid == r.Guid), r.Count)).Where(x => x.Item != null)
                .Select(x => View(x.Item!, x.Count)).ToArray(),
            zeny = side.Zeny, ready = side.Ready,
        };
        long wait = (long)Math.Max(0, (ReadyDelay - (clock.GetUtcNow() - t.Changed)).TotalMilliseconds);
        return new
        {
            t = "trade", state = "open", peer = other?.Role?.Name ?? "?",
            mine = SideView(t.Of(me), mine), theirs = SideView(t.Of(t.Other(me)), other), readyAfterMs = wait,
        };
    }

    private async Task SendTradeAsync(GameSession s)
    {
        long me = s.Role!.Id;
        Trade? trade; Invite? invite;
        lock (gate) { trades.TryGetValue(me, out trade); invites.TryGetValue(me, out invite); }
        if (trade != null) { await PushAsync(s, TradeView(trade, me)); return; }
        if (invite != null && Online(invite.From) is { } from)
        {
            await PushAsync(s, new { t = "trade", state = "invited", peer = from.Role!.Name, incoming = true });
            return;
        }
        Invite? outgoing;
        long invitee = 0;
        lock (gate)
        {
            invitee = invites.FirstOrDefault(p => p.Value.From == me).Key;
            outgoing = invitee == 0 ? null : invites[invitee];
        }
        if (outgoing != null && Online(invitee) is { } to)
        {
            await PushAsync(s, new { t = "trade", state = "invited", peer = to.Role!.Name, incoming = false });
            return;
        }
        await PushAsync(s, new { t = "trade", state = "none" });
    }

    private async Task PushTradeAsync(Trade t)
    {
        foreach (long id in new[] { t.A, t.B })
            if (Online(id) is { } s) await PushAsync(s, TradeView(t, id));
    }

    private bool Busy(long role)
    {
        lock (gate) return trades.ContainsKey(role) || invites.ContainsKey(role) || invites.Values.Any(i => i.From == role);
    }

    private string? CanTrade(GameSession s) =>
        s.Dead ? "Você não pode trocar estando morto."
        : s.InstanceId != 0 ? "Não é possível trocar dentro de uma masmorra."
        : s.SceneLoading ? "Aguarde o mapa carregar."
        : null;

    private async Task InviteAsync(GameSession s, string name)
    {
        name = name.Trim();
        if (name.Length == 0) { await SayAsync(s, "Informe o nome do jogador.", false); return; }
        var target = sessions.All().FirstOrDefault(x => x.Progress != null && string.Equals(x.Role?.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target == null) { await SayAsync(s, "Jogador não está online.", false); return; }
        if (target.Role!.Id == s.Role!.Id) { await SayAsync(s, "Você não pode trocar com você mesmo.", false); return; }
        if (CanTrade(s) is { } mine) { await SayAsync(s, mine, false); return; }
        if (CanTrade(target) is not null) { await SayAsync(s, $"{target.Role.Name} não pode trocar agora.", false); return; }
        if (target.SceneEntry != s.SceneEntry || target.LineNumber != s.LineNumber)
        {
            await SayAsync(s, $"{target.Role.Name} precisa estar no mesmo mapa e linha.", false);
            return;
        }
        if (Busy(s.Role.Id)) { await SayAsync(s, "Você já tem uma troca ou convite em andamento.", false); return; }
        if (Busy(target.Role.Id)) { await SayAsync(s, $"{target.Role.Name} está ocupado com outra troca.", false); return; }
        lock (gate) invites[target.Role.Id] = new Invite(s.Role.Id, clock.GetUtcNow() + InviteLife);
        await SayAsync(s, $"Convite enviado a {target.Role.Name}.", true);
        await PushAsync(target, new { t = "show", tab = "trade" });
        await SayAsync(target, $"{s.Role.Name} convidou você para uma troca.", true);
        await SendTradeAsync(s);
        await SendTradeAsync(target);
    }

    private async Task AcceptAsync(GameSession s)
    {
        long me = s.Role!.Id;
        Invite? invite;
        lock (gate) { invites.TryGetValue(me, out invite); }
        var from = invite == null ? null : Online(invite.From);
        if (invite == null || invite.Expires < clock.GetUtcNow() || from == null)
        {
            lock (gate) invites.Remove(me);
            await SayAsync(s, "Nenhum convite pendente.", false);
            await SendTradeAsync(s);
            return;
        }
        if (CanTrade(s) is { } why) { await SayAsync(s, why, false); return; }
        if (from.SceneEntry != s.SceneEntry || from.LineNumber != s.LineNumber)
        {
            await SayAsync(s, "Vocês não estão mais no mesmo mapa.", false);
            return;
        }
        var trade = new Trade { A = from.Role!.Id, B = me, Created = clock.GetUtcNow(), Changed = clock.GetUtcNow() };
        lock (gate) { invites.Remove(me); trades[trade.A] = trade; trades[trade.B] = trade; }
        await PushTradeAsync(trade);
        await SayAsync(from, $"{s.Role.Name} aceitou. Monte sua oferta.", true);
        await SayAsync(s, "Troca iniciada. Monte sua oferta.", true);
        await SendBagAsync(from);
        await SendBagAsync(s);
    }

    private async Task DeclineAsync(GameSession s)
    {
        long me = s.Role!.Id;
        Invite? invite;
        lock (gate) { if (invites.Remove(me, out invite)) { } }
        if (invite == null) return;
        await SayAsync(s, "Convite recusado.", true);
        await SendTradeAsync(s);
        if (Online(invite.From) is { } from)
        {
            await SayAsync(from, $"{s.Role.Name} recusou a troca.", false);
            await SendTradeAsync(from);
        }
    }

    /// <summary>Ends the caller's trade or invitation (either side); the peer is told.</summary>
    private async Task CancelAsync(GameSession s, string text)
    {
        long me = s.Role!.Id;
        Trade? trade; long peer = 0;
        lock (gate)
        {
            trades.TryGetValue(me, out trade);
            if (trade != null) { trades.Remove(trade.A); trades.Remove(trade.B); peer = trade.Other(me); }
            else
            {
                invites.Remove(me);
                foreach (var key in invites.Where(p => p.Value.From == me).Select(p => p.Key).ToList()) { invites.Remove(key); peer = key; }
            }
        }
        await SayAsync(s, text, true);
        await SendTradeAsync(s);
        if (peer != 0 && Online(peer) is { } other)
        {
            await SayAsync(other, $"{s.Role.Name} cancelou a troca.", false);
            await SendTradeAsync(other);
        }
    }

    private async Task LeftAsync(GameSession s)
    {
        if (s.Role == null) return;
        bool involved;
        lock (gate) involved = trades.ContainsKey(s.Role.Id) || invites.ContainsKey(s.Role.Id) || invites.Values.Any(i => i.From == s.Role.Id);
        if (involved) await CancelAsync(s, "Troca cancelada: você saiu do mapa.");
    }

    /// <summary>Drops trades and invitations that outlived their time, telling whoever is still online.</summary>
    private async Task SweepAsync()
    {
        var now = clock.GetUtcNow();
        var ended = new List<Trade>();
        var lapsed = new List<long>();
        lock (gate)
        {
            foreach (var t in trades.Values.Distinct().ToList())
                if (!t.Committing && (now - t.Changed > TradeLife || Online(t.A) == null || Online(t.B) == null))
                { trades.Remove(t.A); trades.Remove(t.B); ended.Add(t); }
            foreach (var p in invites.Where(p => p.Value.Expires < now || Online(p.Key) == null || Online(p.Value.From) == null).ToList())
            { invites.Remove(p.Key); lapsed.Add(p.Key); lapsed.Add(p.Value.From); }
        }
        foreach (var t in ended)
            foreach (long id in new[] { t.A, t.B })
                if (Online(id) is { } s) { await SayAsync(s, "A troca foi encerrada.", false); await SendTradeAsync(s); }
        foreach (long id in lapsed.Distinct())
            if (Online(id) is { } s) await SendTradeAsync(s);
    }

    private async Task OfferAsync(GameSession s, JsonElement root)
    {
        long me = s.Role!.Id;
        Trade? trade;
        lock (gate) trades.TryGetValue(me, out trade);
        if (trade == null || trade.Committing) { await SayAsync(s, "Nenhuma troca aberta.", false); return; }
        var rows = Rows(root, "guid");
        long zeny = Num(root, "zeny");
        if (rows == null || zeny < 0) { await SayAsync(s, "Oferta inválida.", false); return; }
        foreach (var (guid, count) in rows)
        {
            var item = s.Items.FirstOrDefault(i => i.Guid == guid);
            if (item == null || count > item.Stack) { await SayAsync(s, "Item não encontrado na mochila.", false); await SendBagAsync(s); return; }
            if (TransferError(s, item) is { } why)
            {
                await SayAsync(s, $"{data.ItemName(item.Entry)}: {why}.", false);
                return;
            }
        }
        if (zeny > Zeny(s)) { await SayAsync(s, "Zeny insuficiente.", false); return; }
        lock (gate)
        {
            var side = trade.Of(me);
            side.Items = rows.Select(r => (r.Id, r.Count)).ToList();
            side.Zeny = zeny;
            trade.SideA.Ready = trade.SideB.Ready = false;
            trade.Changed = clock.GetUtcNow();
        }
        await PushTradeAsync(trade);
    }

    private async Task ReadyAsync(GameSession s, bool on)
    {
        long me = s.Role!.Id;
        Trade? trade;
        lock (gate) trades.TryGetValue(me, out trade);
        if (trade == null || trade.Committing) { await SayAsync(s, "Nenhuma troca aberta.", false); return; }
        bool both;
        lock (gate)
        {
            if (on && clock.GetUtcNow() - trade.Changed < ReadyDelay) both = false;
            else { trade.Of(me).Ready = on; both = trade.SideA.Ready && trade.SideB.Ready; }
        }
        await PushTradeAsync(trade);
        if (both) await CommitTradeAsync(s, trade);
    }

    private async Task CommitTradeAsync(GameSession s, Trade t)
    {
        lock (gate)
        {
            if (t.Committing || !(t.SideA.Ready && t.SideB.Ready)) return;
            t.Committing = true;
        }
        var sa = Online(t.A); var sb = Online(t.B);
        string? refusal = null;
        string textA = "", textB = "";
        long zenyA = 0, zenyB = 0;
        int code = sa == null || sb == null ? -1 : await market.RunAsync(s, tx =>
        {
            refusal = null;
            var pa = tx.Role(t.A); var pb = tx.Role(t.B);
            var goodsA = Take(sa, pa, t.SideA, out refusal);
            if (refusal != null) return ErrorCodes.TradeRequest;
            var goodsB = Take(sb, pb, t.SideB, out refusal);
            if (refusal != null) return ErrorCodes.TradeRequest;
            zenyA = t.SideA.Zeny; zenyB = t.SideB.Zeny;
            textA = Describe(goodsA!, zenyA); textB = Describe(goodsB!, zenyB);
            tx.DeliverRawMail(t.B, $"Troca com {sa!.Role!.Name}", $"{sa.Role.Name} deu a você: {textA}.", sa.Role.Name,
                goodsA, zenyA > 0 ? new Dictionary<int, long> { [ZenyId] = zenyA } : null);
            tx.DeliverRawMail(t.A, $"Troca com {sb!.Role!.Name}", $"{sb.Role.Name} deu a você: {textB}.", sb.Role.Name,
                goodsB, zenyB > 0 ? new Dictionary<int, long> { [ZenyId] = zenyB } : null);
            return 0;
        }, command: "player-trade");
        if (code == 0)
        {
            lock (gate) { trades.Remove(t.A); trades.Remove(t.B); }
            Log("trade", new { a = sa!.Role!.Name, aId = t.A, gave_a = textA, b = sb!.Role!.Name, bId = t.B, gave_b = textB });
            foreach (var p in new[] { sa, sb })
            {
                await SayAsync(p, "Troca concluída! Os itens foram enviados para o seu correio.", true);
                await PushAsync(p, new { t = "done", what = "trade" });
                await SendTradeAsync(p);
                await SendBagAsync(p);
            }
            return;
        }
        lock (gate) { t.Committing = false; t.SideA.Ready = t.SideB.Ready = false; t.Changed = clock.GetUtcNow(); }
        foreach (var p in new[] { sa, sb }.Where(x => x != null))
            await SayAsync(p!, refusal != null ? $"Troca recusada: {refusal}" : "Troca não concluída. Confira as ofertas.", false);
        await PushTradeAsync(t);
    }

    /// <summary>Removes one side's offer from its detached bag and returns the goods (null with <paramref name="refusal"/> set when it no longer holds).</summary>
    private List<InvItem>? Take(GameSession? owner, MarketParticipant p, Side side, out string? refusal)
    {
        refusal = null;
        if (owner == null) { refusal = "jogador desconectado."; return null; }
        var goods = new List<InvItem>();
        foreach (var (guid, count) in side.Items)
        {
            var item = p.Items.FirstOrDefault(i => i.Guid == guid);
            if (item == null || count < 1 || count > item.Stack) { refusal = $"{owner.Role!.Name} não tem mais um dos itens."; return null; }
            if (TransferError(owner, item) is { } why) { refusal = $"{data.ItemName(item.Entry)}: {why}."; return null; }
            var piece = item.Clone();
            piece.Stack = count;
            goods.Add(piece);
            if (count == item.Stack) p.Items.Remove(item); else item.Stack -= count;
        }
        if (side.Zeny > 0)
        {
            if (p.Currency.GetValueOrDefault(ZenyId) < side.Zeny) { refusal = $"{owner.Role!.Name} não tem zeny suficiente."; return null; }
            p.Currency[ZenyId] -= side.Zeny;
        }
        return goods;
    }

    // ---- send to a character ----

    private async Task SendToAsync(GameSession s, JsonElement root)
    {
        string name = Str(root, "name").Trim();
        var rows = Rows(root, "guid");
        long zeny = Num(root, "zeny");
        if (name.Length == 0) { await SayAsync(s, "Informe o nome do personagem.", false); return; }
        if (rows == null || zeny < 0 || rows.Count == 0 && zeny == 0) { await SayAsync(s, "Escolha o que enviar.", false); return; }
        var target = roles.FindByName(name);
        if (target == null) { await SayAsync(s, "Personagem não encontrado.", false); return; }
        if (target.Id == s.Role!.Id) { await SayAsync(s, "Escolha outro personagem.", false); return; }
        string? refusal = null, summary = "";
        int code = await market.RunAsync(s, tx =>
        {
            refusal = null;
            var me = tx.Role(s.Role.Id);
            var goods = Take(s, me, new Side { Items = rows.Select(r => (r.Id, r.Count)).ToList(), Zeny = zeny }, out refusal);
            if (refusal != null) return ErrorCodes.TradeRequest;
            summary = Describe(goods!, zeny);
            tx.DeliverRawMail(target.Id, $"Envio de {s.Role.Name}", $"{s.Role.Name} enviou para você: {summary}.", s.Role.Name,
                goods, zeny > 0 ? new Dictionary<int, long> { [ZenyId] = zeny } : null);
            return 0;
        }, command: "player-send");
        if (code != 0) { await SayAsync(s, refusal ?? "Não foi possível enviar.", false); return; }
        Log("send", new { from = s.Role.Name, fromId = s.Role.Id, to = target.Name, toId = target.Id, goods = summary });
        await SayAsync(s, $"Enviado para {target.Name}: {summary}.", true);
        await PushAsync(s, new { t = "done", what = "send" });
        await SendBagAsync(s);
    }

    // ---- account vault ----

    private async Task VaultPutAsync(GameSession s, JsonElement root)
    {
        var rows = Rows(root, "guid");
        long zeny = Num(root, "zeny");
        if (s.Account == null || rows == null || zeny < 0 || rows.Count == 0 && zeny == 0) { await SayAsync(s, "Escolha o que guardar.", false); return; }
        string? refusal = null, summary = "";
        int code = await market.RunAsync(s, tx =>
        {
            refusal = null;
            var me = tx.Role(s.Role!.Id);
            var goods = Take(s, me, new Side { Items = rows.Select(r => (r.Id, r.Count)).ToList(), Zeny = zeny }, out refusal);
            if (refusal != null) return ErrorCodes.TradeRequest;
            var vault = tx.Get<VaultRow>(VaultKind, s.Account) ?? new VaultRow { Account = s.Account };
            foreach (var piece in goods!)
            {
                var into = vault.Entries.FirstOrDefault(e => e.Owner == s.Role.Id && CanMerge(e.Item, piece));
                if (into != null) { into.Item.Stack += piece.Stack; continue; }
                if (vault.Entries.Count >= VaultSlots) { refusal = "o cofre está cheio."; return ErrorCodes.TradeRequest; }
                vault.Entries.Add(new VaultEntry { Id = vault.NextId++, Owner = s.Role.Id, Item = piece });
            }
            if (zeny > 0) vault.Zeny[s.Role.Id] = vault.Zeny.GetValueOrDefault(s.Role.Id) + zeny;
            tx.Put(VaultKind, s.Account, vault);
            summary = Describe(goods, zeny);
            return 0;
        }, command: "player-vault-put");
        if (code != 0) { await SayAsync(s, refusal != null ? $"Cofre: {refusal}" : "Não foi possível guardar.", false); return; }
        Log("vault.put", new { role = s.Role!.Name, roleId = s.Role.Id, account = s.Account, goods = summary });
        await SayAsync(s, $"Guardado no cofre: {summary}.", true);
        await PushAsync(s, new { t = "done", what = "vault" });
        await SendBagAsync(s);
        await SendVaultAsync(s);
    }

    private async Task VaultTakeAsync(GameSession s, JsonElement root)
    {
        var rows = Rows(root, "id");
        long zeny = Num(root, "zeny");
        if (s.Account == null || rows == null || zeny < 0 || rows.Count == 0 && zeny == 0) { await SayAsync(s, "Escolha o que retirar.", false); return; }
        string? refusal = null, summary = "";
        int code = await market.RunAsync(s, tx =>
        {
            refusal = null;
            var vault = tx.Get<VaultRow>(VaultKind, s.Account);
            if (vault == null) { refusal = "o cofre está vazio."; return ErrorCodes.TradeRequest; }
            var goods = new List<InvItem>();
            foreach (var (id, count) in rows)
            {
                var entry = vault.Entries.FirstOrDefault(e => e.Id == id);
                if (entry == null || count > entry.Item.Stack) { refusal = "item não está mais no cofre."; return ErrorCodes.TradeRequest; }
                var piece = entry.Item.Clone();
                piece.Stack = count;
                goods.Add(piece);
                if (count == entry.Item.Stack) vault.Entries.Remove(entry); else entry.Item.Stack -= count;
            }
            if (zeny > 0)
            {
                if (vault.Zeny.Values.Sum() < zeny) { refusal = "zeny insuficiente no cofre."; return ErrorCodes.TradeRequest; }
                long left = zeny;
                foreach (long owner in vault.Zeny.Keys.OrderBy(k => k).ToList())
                {
                    long take = Math.Min(left, vault.Zeny[owner]);
                    left -= take;
                    if (take == vault.Zeny[owner]) vault.Zeny.Remove(owner); else vault.Zeny[owner] -= take;
                    if (left == 0) break;
                }
            }
            tx.Put(VaultKind, s.Account, vault);
            tx.DeliverRawMail(s.Role!.Id, "Retirada do cofre", $"Itens retirados do cofre da conta: {Describe(goods, zeny)}.", "Cofre",
                goods, zeny > 0 ? new Dictionary<int, long> { [ZenyId] = zeny } : null);
            summary = Describe(goods, zeny);
            return 0;
        }, command: "player-vault-take");
        if (code != 0) { await SayAsync(s, refusal != null ? $"Cofre: {refusal}" : "Não foi possível retirar.", false); return; }
        Log("vault.take", new { role = s.Role!.Name, roleId = s.Role.Id, account = s.Account, goods = summary });
        await SayAsync(s, $"Retirado do cofre (veja o correio): {summary}.", true);
        await PushAsync(s, new { t = "done", what = "vault" });
        await SendVaultAsync(s);
    }

    /// <summary>Plain stackables of one entry, same binding, below the stack limit.</summary>
    private bool CanMerge(InvItem into, InvItem piece)
    {
        if (into.Entry != piece.Entry || into.IsLocked || piece.IsLocked || into.BindType != piece.BindType || into.BindExpire != piece.BindExpire) return false;
        if ((into.Attrs?.Count ?? 0) != 0 || (piece.Attrs?.Count ?? 0) != 0 || (into.StringAttrs?.Count ?? 0) != 0
            || (piece.StringAttrs?.Count ?? 0) != 0 || into.Enchantments.Count != 0 || piece.Enchantments.Count != 0) return false;
        if (data.Row("ItemConfig", into.Entry) is not { } row) return false;
        long limit = GameData.Int(row, "_iType") == 5 ? 1 : Math.Max(1, GameData.Int(row, "_iStackLimit"));
        return (long)into.Stack + piece.Stack <= limit;
    }
}
