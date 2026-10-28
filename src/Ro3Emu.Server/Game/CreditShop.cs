using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// In-game purchases (CreateOrderReq 2001) paid with the credits of the account's web shop (tables <c>panel_credit</c>,
/// <c>panel_ledger</c> and <c>panel_order</c> of ro3emu.db, kept by the admin panel): 1 credit = the price in USD of the original
/// product. Only when <see cref="ServerOptions.CreditShop"/> is on.
/// The purchase is approved on the web shop: the game button only creates a pending order (<see cref="RequestAsync"/>) and sends the
/// player to the shop page that shows it (<c>?approve=id</c>). Approving there debits the credits (the panel's one transaction) and
/// marks the order <c>aprovado</c>; <see cref="DeliverApprovedAsync"/> (run every few seconds by <see cref="CreditShopDelivery"/>) then
/// puts the goods in the bag through <see cref="CharacterService.GrantAsync"/> and tells the player in the chat. A failed grant
/// refunds the credits; a player who left meanwhile gets the goods through the panel's delivery queue (panel_delivery).
/// Products: RechargeConfig (ChargeType 1: the crystal packs, award plus bonus) and ProductConfig rows that are month cards
/// (MonthCardConfig._iProduct): the month-card token item, which the activity service converts into days.
/// </summary>
public sealed class CreditShop(IOptions<ServerOptions> options, GameData data, CharacterService character, SessionRegistry sessions,
    GameLoop loop, ILogger<CreditShop> log)
{
    public enum Outcome { Disabled, Unknown, Insufficient, Pending, Failed }

    /// <param name="Message">What the player reads (Portuguese: the shop's players are served in Portuguese).</param>
    /// <param name="OrderId">The pending order (Outcome.Pending): its id is in the approval link.</param>
    public readonly record struct Result(Outcome Outcome, string Message, string OrderId = "");

    private sealed record Good(string Kind, int Entry, int Count);
    private sealed record Product(string Title, double Price, IReadOnlyList<Good> Goods);

    /// <summary>How long a pending order waits for the approval on the site.</summary>
    internal static readonly TimeSpan OrderLifetime = TimeSpan.FromMinutes(10);

    public bool Enabled => options.Value.CreditShop;

    private string DbPath => Path.Combine(options.Value.DataDir, "ro3emu.db");

    /// <summary>The shop page that approves <paramref name="orderId"/>; null when no shop address is configured.</summary>
    public string? ApprovalUrl(string orderId)
    {
        string? shop = options.Value.StoreUrl;
        if (string.IsNullOrWhiteSpace(shop)) return null;
        return shop + (shop.Contains('?') ? "&" : "?") + "approve=" + Uri.EscapeDataString(orderId);
    }

    public async Task<Result> RequestAsync(GameSession session, long chargeType, long chargeId)
    {
        log.LogInformation("CreateOrderReq from {Account}: chargeType {Type} chargeId {Id} (credit shop {State})", session.Account, chargeType, chargeId, Enabled ? "on" : "off");
        if (!Enabled) return new(Outcome.Disabled, "");
        if (Resolve(chargeType, chargeId) is not { } product)
        {
            log.LogInformation("CreateOrderReq chargeType {Type} chargeId {Id}: not a product the credit shop sells; the button opens the shop", chargeType, chargeId);
            return new(Outcome.Unknown, "");
        }
        if (session.Account is not { Length: > 0 } account || session.Role is not { } role)
            return new(Outcome.Failed, "Entre no jogo com um personagem para comprar.");
        double price = Math.Round(product.Price, 2);
        if (price <= 0) return new(Outcome.Unknown, "");
        if (ApprovalUrl("x") == null) return new(Outcome.Failed, "A loja do site não está configurada neste servidor.");
        string orderId = Guid.NewGuid().ToString("N");
        try
        {
            var have = await Task.Run(() => CreateOrder(orderId, account, role.Id, role.Name, product, price));
            if (have is { } short_)
                return new(Outcome.Insufficient, $"Créditos insuficientes: você tem {Money(short_)} e custa {Money(price)}. Veja a loja do site para saber o saldo.");
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            log.LogError(e, "Credit shop order failed for {Account}", account);
            return new(Outcome.Failed, "A loja de créditos não está disponível agora. Tente de novo em instantes.");
        }
        log.LogInformation("Credit shop: {Account}/{Role} asked to buy {Title} for {Price} credits (order {Order}, waiting for the approval on the site)", account, role.Name, product.Title, price, orderId);
        return new(Outcome.Pending, $"Pedido criado: {product.Title} por {Money(price)} créditos. Aprove no site (abriu no navegador) em até {(int)OrderLifetime.TotalMinutes} minutos.", orderId);
    }

    /// <summary>Delivers the orders approved on the site (status aprovado) and expires the old pending ones. Returns how many were delivered.</summary>
    public async Task<int> DeliverApprovedAsync()
    {
        if (!Enabled || !File.Exists(DbPath)) return 0;
        var approved = await Task.Run(() => TakeApproved());
        int delivered = 0;
        foreach (var order in approved)
        {
            try { if (await DeliverAsync(order)) delivered++; }
            catch (Exception e) { log.LogError(e, "Credit shop delivery of order {Order} failed", order.Id); }
        }
        return delivered;
    }

    private sealed record Order(string Id, string Account, long RoleId, string RoleName, string Title, double Price, List<Good> Goods, long LedgerId, double? Balance);

    private async Task<bool> DeliverAsync(Order order)
    {
        var session = sessions.All().FirstOrDefault(s => s.Role?.Id == order.RoleId && string.Equals(s.Account, order.Account, StringComparison.OrdinalIgnoreCase));
        string message = $"Compra concluída: {order.Title} por {Money(order.Price)} créditos." + (order.Balance is { } b ? $" Saldo: {Money(b)}." : "");
        if (session == null)
        {
            // left the game after approving: the panel's delivery queue gives it when the role is offline
            await Task.Run(() => QueueOffline(order));
            log.LogInformation("Credit shop: order {Order} of {Account} delivered through the queue (role offline)", order.Id, order.Account);
            return true;
        }
        bool granted;
        try
        {
            granted = await loop.InvokeAsync(async () =>
            {
                try
                {
                    await character.GrantAsync(session, order.Goods.Select(g => (g.Entry, g.Count)).ToList());
                    await GmCommands.ReplyAsync(session, message);
                    return true;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Credit shop grant failed for {Account}/{Role}", order.Account, order.RoleId);
                    return false;
                }
            });
        }
        catch (InvalidOperationException)
        {
            await Task.Run(() => SetStatus(order.Id, "aprovado")); // the loop is not taking work now: next round
            return false;
        }
        if (granted)
        {
            await Task.Run(() => Finish(order, "entregue", "entregue"));
            log.LogInformation("Credit shop: {Account}/{Role} bought {Title} for {Price} credits", order.Account, order.RoleName, order.Title, order.Price);
            return true;
        }
        try { await Task.Run(() => Refund(order)); }
        catch (Exception refund) { log.LogCritical(refund, "REFUND FAILED for {Account}: {Price} credits must be returned by hand (order {Order})", order.Account, order.Price, order.Id); }
        if (sessions.All().FirstOrDefault(s => s.Role?.Id == order.RoleId) is { } still)
            await loop.InvokeAsync(() => GmCommands.ReplyAsync(still, "Não foi possível entregar a compra; seus créditos foram devolvidos."));
        return false;
    }

    private static string Money(double value) => value.ToString(value == Math.Floor(value) ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    private Product? Resolve(long chargeType, long chargeId)
    {
        if (chargeType == 1)
        {
            if (data.Row("RechargeConfig", chargeId) is not { } row || GameData.Number(row.GetProperty("_iPrice")) is not { } price) return null;
            var goods = new List<Good>();
            foreach (var field in new[] { "_kAward", "_kAwardBonus" })
                if (Pair(row, field) is { } pair) goods.Add(new("currency", pair.Entry, pair.Count));
            if (goods.Count == 0) return null;
            return new Product($"{goods[0].Count} Cristais", price, goods);
        }
        if (data.Row("ProductConfig", chargeId) is not { } product || GameData.Number(product.GetProperty("_iPrice")) is not { } productPrice) return null;
        // "Compra rápida" of the month-card screen: PublicParamConfig.MonthCardAllBuyProductConfigID buys every card at once
        if (data.Table("PublicParamConfig").TryGetValue("MonthCardAllBuyProductConfigID", out var allBuy) && GameData.Number(allBuy) == chargeId)
        {
            var tokens = new List<Good>();
            foreach (var card in data.Table("MonthCardConfig").Values)
                if (GameData.Int(card, "_iItem") is > 0 and var item) tokens.Add(new("item", checked((int)item), 1));
            return tokens.Count == 0 ? null : new Product("Passes Mensais (Padrão + Premium)", productPrice, tokens);
        }
        foreach (var card in data.Table("MonthCardConfig").Values)
        {
            if (GameData.Int(card, "_iProduct") != chargeId) continue;
            int token = checked((int)GameData.Int(card, "_iItem"));
            if (token <= 0) return null;
            string name = token switch { 701 => "Passe Mensal Padrão", 702 => "Passe Mensal Premium", _ => data.ItemName(token) };
            return new Product(string.IsNullOrEmpty(name) ? "Passe Mensal" : name, productPrice, [new("item", token, 1)]);
        }
        return null;
    }

    private static (int Entry, int Count)? Pair(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2) return null;
        int entry = value[0].GetInt32(), count = value[1].GetInt32();
        return entry > 0 && count > 0 ? (entry, count) : null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 15000";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    /// <summary>The same definition the admin panel creates (either side may run first).</summary>
    private static void EnsureTables(SqliteConnection c)
    {
        Cmd(c, "CREATE TABLE IF NOT EXISTS panel_order(id text primary key, account text not null collate nocase, role_id integer not null, role_name text default '', " +
               "title text not null, price real not null, goods text not null, status text not null default 'pendente', created text, expires_at integer not null, " +
               "ledger_id integer default 0, balance_after real, note text default '')").ExecuteNonQuery();
        Cmd(c, "CREATE TABLE IF NOT EXISTS panel_delivery(id integer primary key autoincrement, role_id integer not null, kind text not null, " +
               "entry integer not null default 0, amount integer not null default 1, currency integer not null default 0, created text, applied integer not null default 0)").ExecuteNonQuery();
    }

    /// <summary>Stores the pending order. Returns what the account has when that does not cover the price (no order then), else null.</summary>
    private double? CreateOrder(string id, string account, long roleId, string roleName, Product product, double price)
    {
        using var c = Open();
        EnsureTables(c);
        double have = Convert.ToDouble(Cmd(c, "SELECT amount FROM panel_credit WHERE account = $a COLLATE NOCASE", ("$a", account)).ExecuteScalar() ?? 0.0, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Round(have, 2) < Math.Round(price, 2)) return have;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // an older pending order of the same role for the same thing is replaced by this one
        Cmd(c, "UPDATE panel_order SET status = 'expirado' WHERE status = 'pendente' AND account = $a COLLATE NOCASE", ("$a", account)).ExecuteNonQuery();
        string goods = JsonSerializer.Serialize(product.Goods.Select(g => new { k = g.Kind, e = g.Entry, n = g.Count }));
        Cmd(c, "INSERT INTO panel_order(id, account, role_id, role_name, title, price, goods, status, created, expires_at) VALUES ($id, $a, $rid, $rn, $t, $p, $g, 'pendente', $now, $exp)",
            ("$id", id), ("$a", account), ("$rid", roleId), ("$rn", roleName), ("$t", product.Title), ("$p", price), ("$g", goods),
            ("$now", DateTime.UtcNow.ToString("o")), ("$exp", now + (long)OrderLifetime.TotalSeconds)).ExecuteNonQuery();
        return null;
    }

    /// <summary>Expires the old pending orders and claims the approved ones (status aprovado to entregando) for this round.</summary>
    private List<Order> TakeApproved()
    {
        var found = new List<Order>();
        using var c = Open();
        EnsureTables(c);
        Cmd(c, "UPDATE panel_order SET status = 'expirado' WHERE status = 'pendente' AND expires_at < $now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ExecuteNonQuery();
        using (var rows = Cmd(c, "SELECT id, account, role_id, role_name, title, price, goods, ledger_id, balance_after FROM panel_order WHERE status = 'aprovado'").ExecuteReader())
        {
            while (rows.Read())
            {
                var goods = new List<Good>();
                try
                {
                    foreach (var g in JsonDocument.Parse(rows.GetString(6)).RootElement.EnumerateArray())
                        goods.Add(new(g.GetProperty("k").GetString() ?? "item", g.GetProperty("e").GetInt32(), g.GetProperty("n").GetInt32()));
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { log.LogError(e, "Credit shop order {Order} has unreadable goods", rows.GetString(0)); continue; }
                found.Add(new Order(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetString(3), rows.GetString(4), rows.GetDouble(5), goods,
                    rows.IsDBNull(7) ? 0 : rows.GetInt64(7), rows.IsDBNull(8) ? null : rows.GetDouble(8)));
            }
        }
        var claimed = new List<Order>();
        foreach (var o in found)
            if (Cmd(c, "UPDATE panel_order SET status = 'entregando' WHERE id = $id AND status = 'aprovado'", ("$id", o.Id)).ExecuteNonQuery() == 1) claimed.Add(o);
        return claimed;
    }

    private void SetStatus(string id, string status)
    {
        using var c = Open();
        Cmd(c, "UPDATE panel_order SET status = $s WHERE id = $id", ("$s", status), ("$id", id)).ExecuteNonQuery();
    }

    private void Finish(Order order, string status, string ledgerStatus)
    {
        using var c = Open();
        Cmd(c, "UPDATE panel_order SET status = $s WHERE id = $id", ("$s", status), ("$id", order.Id)).ExecuteNonQuery();
        if (order.LedgerId > 0) Cmd(c, "UPDATE panel_ledger SET status = $s WHERE id = $l", ("$s", ledgerStatus), ("$l", order.LedgerId)).ExecuteNonQuery();
    }

    /// <summary>The player left after approving: the goods go through panel_delivery, which the panel applies once the role is offline.</summary>
    private void QueueOffline(Order order)
    {
        using var c = Open();
        EnsureTables(c);
        Cmd(c, "BEGIN IMMEDIATE").ExecuteNonQuery();
        string now = DateTime.UtcNow.ToString("o");
        foreach (var g in order.Goods)
        {
            bool currency = g.Kind == "currency";
            Cmd(c, "INSERT INTO panel_delivery(role_id, kind, entry, amount, currency, created, applied) VALUES ($r, $k, $e, $n, $cur, $now, 0)",
                ("$r", order.RoleId), ("$k", currency ? "currency" : "item"), ("$e", currency ? 0 : g.Entry), ("$n", g.Count), ("$cur", currency ? g.Entry : 0), ("$now", now)).ExecuteNonQuery();
        }
        Cmd(c, "UPDATE panel_order SET status = 'entregue', note = 'fila de entrega (personagem offline)' WHERE id = $id", ("$id", order.Id)).ExecuteNonQuery();
        if (order.LedgerId > 0) Cmd(c, "UPDATE panel_ledger SET status = 'aguardando entrega' WHERE id = $l", ("$l", order.LedgerId)).ExecuteNonQuery();
        Cmd(c, "COMMIT").ExecuteNonQuery();
    }

    private void Refund(Order order)
    {
        using var c = Open();
        Cmd(c, "BEGIN IMMEDIATE").ExecuteNonQuery();
        string now = DateTime.UtcNow.ToString("o");
        Cmd(c, "UPDATE panel_credit SET amount = ROUND(amount + $p, 2), updated = $now WHERE account = $a COLLATE NOCASE", ("$p", order.Price), ("$now", now), ("$a", order.Account)).ExecuteNonQuery();
        double have = Convert.ToDouble(Cmd(c, "SELECT amount FROM panel_credit WHERE account = $a COLLATE NOCASE", ("$a", order.Account)).ExecuteScalar() ?? 0.0, System.Globalization.CultureInfo.InvariantCulture);
        Cmd(c, "INSERT INTO panel_ledger(kind, account, role_id, role_name, entry, product, price, currency, credits, balance_after, status, actor, created) " +
               "VALUES ('credit', $a, $rid, $rn, 0, $title, 0, 0, $p, $bal, 'estorno', 'jogo', $now)",
            ("$a", order.Account), ("$rid", order.RoleId), ("$rn", order.RoleName), ("$title", "Estorno: " + order.Title), ("$p", order.Price), ("$bal", have), ("$now", now)).ExecuteNonQuery();
        if (order.LedgerId > 0) Cmd(c, "UPDATE panel_ledger SET status = 'estornada' WHERE id = $l", ("$l", order.LedgerId)).ExecuteNonQuery();
        Cmd(c, "UPDATE panel_order SET status = 'falhou' WHERE id = $id", ("$id", order.Id)).ExecuteNonQuery();
        Cmd(c, "COMMIT").ExecuteNonQuery();
    }
}

/// <summary>Runs <see cref="CreditShop.DeliverApprovedAsync"/> every 2 seconds while the credit shop is on.</summary>
public sealed class CreditShopDelivery(CreditShop shop, ILogger<CreditShopDelivery> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!shop.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await shop.DeliverApprovedAsync(); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Credit shop delivery round failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
