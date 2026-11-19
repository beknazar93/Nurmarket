using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Партия товара (<c>GET products/{id}/batches/</c>): приход, остаток, себестоимость, срок, номер партии, поставщик.</summary>
public sealed record StockBatch(string Id, double QtyIn, double QtyLeft, double? UnitCost, DateTime? ExpiresAt, DateTime? ProducedAt, string LotNo, string Supplier, DateTime? ReceivedAt, string Kind)
{
    /// <summary>Просрочена: срок раньше сегодняшнего дня и в партии что-то осталось.</summary>
    public bool IsExpired(DateTime today) => ExpiresAt is { } e && e.Date < today.Date && QtyLeft > 0.0005;

    /// <summary>Скоро истекает: срок не позже чем через <see cref="ServerStock.ExpiringDays"/> дней (и ещё не прошёл).</summary>
    public bool IsExpiring(DateTime today) => ExpiresAt is { } e && e.Date >= today.Date && (e.Date - today.Date).TotalDays <= ServerStock.ExpiringDays && QtyLeft > 0.0005;
}

/// <summary>Резерв товара (<c>GET reservations/</c>).</summary>
public sealed record StockReservation(string Id, string ProductId, string ProductName, double Qty, string Comment, DateTime? ExpiresAt, DateTime? CreatedAt, string Status);

/// <summary>Кусок строки чека, взятый из партии (<c>allocations[]</c> строки <c>GET pos/sales/{id}/</c>).</summary>
public sealed record SaleAllocation(string BatchId, double Qty, double? UnitCost, DateTime? ExpiresAt, string LotNo);

/// <summary>Настройки склада компании (<c>GET settings/stock/</c>). Касса только читает; включает их владелец на сервере (<c>PUT</c> из кассы не вызывается).</summary>
public sealed record StockPolicies(string CostingMethod, bool Batches, string ExpiredSalePolicy, string ReservedSalePolicy, string NegativeStockPolicy = "")
{
    public const string Allow = "allow", Warn = "warn", Block = "block";
}

/// <summary>Строка отчёта «Скоро истекает» (<c>GET stock/expiring/</c>): по партиям.</summary>
public sealed record StockExpiringRow(string ProductId, string Name, DateTime Date, int DaysLeft, bool Expired, double Quantity);

public enum StockSaleDecision { Ok, Warn, Block }

/// <summary>
/// 2026-11-19 (ответ бэкенда «склад К-4 К-5 К-8 и ELQR», раздел 1): разбор ответов склада «10 из 10» — партии, резервы, allocations, настройки, 409 insufficient_available.
/// Всё включается ПРИЗНАКАМИ capabilities (stock_batches, costing_fifo, expiry_batches, expired_sale_policy, reservations): без признака ничего не показывается
/// и не отправляется — касса и программа владельца ведут себя как раньше.
/// </summary>
public static class ServerStock
{
    /// <summary>«Скоро истекает» — срок в ближайшие 30 дней.</summary>
    public const int ExpiringDays = 30;

    public static bool BatchesOn => ServerCapabilities.Has(ServerCapabilities.StockBatches);
    /// <summary>Поля «Срок годности» и «Партия №» в строке прихода: сервер принимает expires_at/produced_at/shelf_life_days/lot_no.</summary>
    public static bool ExpiryFieldsOn => ServerCapabilities.Has(ServerCapabilities.ExpiryBatches) || ServerCapabilities.Has(ServerCapabilities.StockBatches);
    public static bool FifoOn => ServerCapabilities.Has(ServerCapabilities.CostingFifo);
    public static bool ReservationsOn => ServerCapabilities.Has(ServerCapabilities.Reservations);

    // ───────────── разбор ─────────────

    public static List<StockBatch> ParseBatches(JsonElement d)
    {
        var list = new List<StockBatch>();
        foreach (var b in Rows(d, "batches"))
            list.Add(new StockBatch(
                Str(b, "id"),
                Num(b, "qty_in") ?? 0,
                Num(b, "qty_left") ?? 0,
                Num(b, "unit_cost"),
                Date(b, "expires_at"),
                Date(b, "produced_at"),
                Str(b, "lot_no"),
                Named(b, "supplier"),
                Date(b, "received_at") ?? Date(b, "created_at"),
                Str(b, "kind")));
        // Сортировка по сроку: ближайший (в том числе просроченный) — первым, без срока — в конце, потом по приходу.
        return list.OrderBy(x => x.ExpiresAt ?? DateTime.MaxValue).ThenBy(x => x.ReceivedAt ?? DateTime.MaxValue).ToList();
    }

    public static List<StockReservation> ParseReservations(JsonElement d)
    {
        var list = new List<StockReservation>();
        foreach (var r in Rows(d, "reservations"))
        {
            var product = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var pid = product.ValueKind == JsonValueKind.Object ? Str(product, "id") : Str(r, "product_id") is { Length: > 0 } x ? x : Str(r, "product");
            var name = product.ValueKind == JsonValueKind.Object ? Str(product, "name") : Str(r, "product_name");
            list.Add(new StockReservation(Str(r, "id"), pid, name, Num(r, "qty") ?? Num(r, "quantity") ?? 0, Str(r, "comment"), Date(r, "expires_at"), Date(r, "created_at"), Str(r, "status")));
        }
        return list;
    }

    /// <summary><c>allocations</c> строки проданного чека; нет поля (старый сервер или партии выключены) — пусто.</summary>
    public static List<SaleAllocation> ParseAllocations(JsonElement line)
    {
        var list = new List<SaleAllocation>();
        if (line.ValueKind == JsonValueKind.Object && line.TryGetProperty("allocations", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object)
                    list.Add(new SaleAllocation(Str(a, "batch_id"), Num(a, "qty") ?? 0, Num(a, "unit_cost"), Date(a, "expires_at"), Str(a, "lot_no")));
        return list;
    }

    /// <summary>Себестоимость строки по партиям: сумма qty × unit_cost; null — стоимости нет ни у одной части.</summary>
    public static double? AllocationsCost(IReadOnlyList<SaleAllocation> list) =>
        list.Any(a => a.UnitCost is not null) ? list.Sum(a => a.Qty * (a.UnitCost ?? 0)) : null;

    /// <summary>Отчёт <c>stock/expiring/</c>: массив или {results|items|products: [...]}; строка — товар, срок (expires_at/expiration_date/expiry), остаток. null — форма ответа не распознана.</summary>
    public static List<StockExpiringRow>? ParseExpiring(JsonElement d, DateTime today) =>
        ParseExpiringRaw(d, today) is { } rows ? GroupExpiring(rows) : null;

    /// <summary>Строки одной страницы <c>stock/expiring/</c> без склейки по товару (склейка — <see cref="GroupExpiring"/> после всех страниц).
    /// 2026-11-19 (ответ Умара 19.11): строка партии {batch_id, product_id, name, remaining, expires_at, days_left, expired, received_at, lot_no}.</summary>
    internal static List<StockExpiringRow>? ParseExpiringRaw(JsonElement d, DateTime today)
    {
        if (d.ValueKind != JsonValueKind.Array
            && !(d.ValueKind == JsonValueKind.Object && new[] { "results", "items", "products", "expiring" }.Any(k => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)))
            return null;
        var rows = new List<StockExpiringRow>();
        foreach (var r in Rows(d, "expiring"))
        {
            var id = Str(r, "product_id") is { Length: > 0 } a ? a : Str(r, "id");
            var date = Date(r, "expires_at") ?? Date(r, "expiration_date") ?? Date(r, "expiry_date");
            if (id.Length == 0 || date is null)
                continue;
            var days = Num(r, "days_left") is { } dl ? (int)dl : (int)(date.Value.Date - today.Date).TotalDays;
            var expired = string.Equals(Str(r, "status"), "expired", StringComparison.OrdinalIgnoreCase) || days < 0
                || r.TryGetProperty("expired", out var ex) && ex.ValueKind == JsonValueKind.True;
            rows.Add(new StockExpiringRow(id, Str(r, "name") is { Length: > 0 } n ? n : Str(r, "product_name"), date.Value.Date, days, expired, Num(r, "remaining") ?? Num(r, "qty_left") ?? Num(r, "quantity") ?? Num(r, "qty") ?? 0));
        }
        return rows;
    }

    public static List<StockExpiringRow> GroupExpiring(List<StockExpiringRow> rows)
    {
        // Товар с несколькими партиями — одна запись с ближайшим сроком (так же, как справочник сроков ProductExpiryIndex).
        return rows.GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(x => x.Date).First() with { Quantity = g.Sum(x => x.Quantity) }).ToList();
    }

    public static StockPolicies? ParsePolicies(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
            return null;
        var expired = Norm(Str(d, "expired_sale_policy"), StockPolicies.Allow, StockPolicies.Warn, StockPolicies.Block) ?? StockPolicies.Allow;
        var reserved = Norm(Str(d, "reserved_sale_policy"), StockPolicies.Warn, StockPolicies.Block) ?? StockPolicies.Warn;
        var batches = d.TryGetProperty("batches", out var b) && b.ValueKind == JsonValueKind.True;
        return new StockPolicies(Str(d, "costing_method"), batches, expired, reserved, Str(d, "negative_stock_policy")); // 2026-11-19 (ответ Умара 19.11): + negative_stock_policy
    }

    private static string? Norm(string value, params string[] allowed) =>
        allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>409 <c>insufficient_available</c> при резерве: свободного не хватило. available — сколько свободно (если сервер сообщил), иначе null.</summary>
    public static bool TryParseInsufficient(ApiException ex, out double? available)
    {
        available = null;
        if (ex.StatusCode != 409)
            return false;
        var raw = ex.Payload is { } p ? p.ToString() : "";
        if (!raw.Contains("insufficient_available", StringComparison.OrdinalIgnoreCase) && !ex.Message.Contains("insufficient_available", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ex.Payload is { ValueKind: JsonValueKind.Object } o)
        {
            available = Num(o, "available");
            if (available is null && o.TryGetProperty("detail", out var det) && det.ValueKind == JsonValueKind.Object)
                available = Num(det, "available");
        }
        return true;
    }

    /// <summary>Поля строки прихода К-5: <c>expires_at</c> (или <c>produced_at</c> + <c>shelf_life_days</c>) и <c>lot_no</c>. Срок годности главнее: дата выпуска со сроком хранения не шлются вместе с ним.
    /// Без признака (<see cref="ExpiryFieldsOn"/>) тело прихода не меняется.</summary>
    public static void ApplyReceiptBatchFields(IDictionary<string, object?> item, DateTime? expiresAt, DateTime? producedAt, int? shelfLifeDays, string? lotNo, bool? enabled = null)
    {
        if (!(enabled ?? ExpiryFieldsOn))
            return;
        if (expiresAt is { } e)
            item["expires_at"] = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (producedAt is { } p && shelfLifeDays is > 0)
        {
            item["produced_at"] = p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            item["shelf_life_days"] = shelfLifeDays.Value;
        }
        if (!string.IsNullOrWhiteSpace(lotNo))
            item["lot_no"] = lotNo.Trim();
    }

    // ───────────── правила продажи (политики настроек склада) ─────────────

    /// <summary>Продажа из просроченной партии по политике сервера: allow — молчим, warn — «Продать всё равно / Отмена», block — только сообщение.</summary>
    public static StockSaleDecision ExpiredSale(string? policy, bool expired) =>
        !expired ? StockSaleDecision.Ok
        : string.Equals(policy, StockPolicies.Block, StringComparison.OrdinalIgnoreCase) ? StockSaleDecision.Block
        : string.Equals(policy, StockPolicies.Warn, StringComparison.OrdinalIgnoreCase) ? StockSaleDecision.Warn
        : StockSaleDecision.Ok;

    /// <summary>Продажа сверх свободного остатка: <paramref name="available"/> — свободно (null — сервер поле не отдал: проверки нет), <paramref name="wanted"/> — сколько в чеке вместе с добавляемым.</summary>
    public static StockSaleDecision OverAvailable(string? policy, double? available, double wanted) =>
        available is not { } free || wanted <= free + 0.0005 ? StockSaleDecision.Ok
        : string.Equals(policy, StockPolicies.Block, StringComparison.OrdinalIgnoreCase) ? StockSaleDecision.Block
        : StockSaleDecision.Warn;

    // ───────────── кеш настроек склада ─────────────

    private static StockPolicies? _policies;
    private static string _policiesAccount = "";
    private static DateTime _policiesAt = DateTime.MinValue;
    private static int _policiesBusy;

    /// <summary>Настройки склада из памяти — только если у компании есть признак политики и настройки уже получены (иначе null = «как раньше»).</summary>
    public static StockPolicies? CachedPolicies =>
        (ServerCapabilities.Has(ServerCapabilities.ExpiredSalePolicy) || ServerCapabilities.Has(ServerCapabilities.Reservations))
        && _policiesAccount == ServerCapabilities.AccountKey() ? _policies : null;

    /// <summary>Запросить settings/stock/ в фоне (не чаще раза в 5 минут); сбой — прежние данные.</summary>
    public static void RefreshPoliciesInBackground()
    {
        if (DateTime.UtcNow - _policiesAt < TimeSpan.FromMinutes(5) && _policiesAccount == ServerCapabilities.AccountKey())
            return;
        if (Interlocked.Exchange(ref _policiesBusy, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try { await RefreshPoliciesAsync().ConfigureAwait(false); }
            catch { /* журнал пишет RefreshPoliciesAsync */ }
            finally { Volatile.Write(ref _policiesBusy, 0); }
        });
    }

    public static async Task<StockPolicies?> RefreshPoliciesAsync(CancellationToken ct = default)
    {
        var account = ServerCapabilities.AccountKey();
        _policiesAt = DateTime.UtcNow; // и неудача откладывает следующую попытку на 5 минут
        try
        {
            var p = ParsePolicies(await StockServerApi.SettingsAsync(null, ct).ConfigureAwait(false));
            _policies = p;
            _policiesAccount = account;
            return p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Склад: настройки settings/stock/ не получены ({ex.Message}) — политики не применяются.", "WARNING");
            return _policiesAccount == account ? _policies : null;
        }
    }

    /// <summary>Только для тестов: подставить настройки склада для текущего аккаунта.</summary>
    internal static void SetPoliciesForTests(StockPolicies? p)
    {
        _policies = p;
        _policiesAccount = ServerCapabilities.AccountKey();
        _policiesAt = DateTime.UtcNow;
    }

    // ───────────── резервы чека ─────────────

    /// <summary>Резервы, которые кассир продаёт этим чеком (id с сервера). Пусто — поле в чек не пишется.</summary>
    public static IReadOnlyList<string> ReservationIdsForSale { get; set; } = Array.Empty<string>();

    /// <summary>Дописывает <c>reservation_ids</c> в тело pos/checkout — только при признаке reservations и выбранном резерве. Уже заданное поле не трогает.</summary>
    public static void ApplyReservationIds(JsonObject body)
    {
        if (ReservationIdsForSale.Count == 0 || body["reservation_ids"] is not null || !ReservationsOn)
            return;
        var arr = new JsonArray();
        foreach (var id in ReservationIdsForSale)
            arr.Add(id);
        body["reservation_ids"] = arr;
    }

    // ───────────── подписи (5 языков) ─────────────

    public static string BatchState(StockBatch b, DateTime today) =>
        b.IsExpired(today) ? Tr.T("Просрочена", "Мөөнөтү өткөн", "Expired", "Süresi geçmiş", "Muddati o'tgan")
        : b.IsExpiring(today) ? Tr.T("Скоро истекает", "Жакында бүтөт", "Expires soon", "Yakında bitiyor", "Tez tugaydi")
        : "";

    public static string ExpiredSaleText(string title, DateTime date) =>
        Tr.T($"Товар «{title}» просрочен (срок до {date:dd.MM.yyyy}). Продать?", $"«{title}» товарынын мөөнөтү өткөн ({date:dd.MM.yyyy} чейин). Сатабызбы?",
            $"“{title}” is expired (best before {date:dd.MM.yyyy}). Sell it?", $"“{title}” ürününün süresi geçmiş (SKT {date:dd.MM.yyyy}). Satılsın mı?",
            $"“{title}” mahsulotining muddati o'tgan ({date:dd.MM.yyyy} gacha). Sotamizmi?");

    public static string ExpiredBlockedText(string title, DateTime date) =>
        Tr.T($"Товар «{title}» просрочен (срок до {date:dd.MM.yyyy}). Продажа запрещена настройками склада.", $"«{title}» товарынын мөөнөтү өткөн ({date:dd.MM.yyyy} чейин). Сатуу кампа жөндөөлөрү менен тыюу салынган.",
            $"“{title}” is expired (best before {date:dd.MM.yyyy}). Sale is blocked by the warehouse settings.", $"“{title}” ürününün süresi geçmiş (SKT {date:dd.MM.yyyy}). Satış depo ayarlarıyla engellendi.",
            $"“{title}” mahsulotining muddati o'tgan ({date:dd.MM.yyyy} gacha). Sotish ombor sozlamalari bilan taqiqlangan.");

    public static string OverAvailableText(string title, double free, double reserved) =>
        Tr.T($"Сверх свободного остатка: «{title}» — свободно {Q(free)}, в резерве {Q(reserved)}.", $"Бош калдыктан ашык: «{title}» — бош {Q(free)}, резервде {Q(reserved)}.",
            $"More than the free stock: “{title}” — free {Q(free)}, reserved {Q(reserved)}.", $"Serbest stoğun üstünde: “{title}” — serbest {Q(free)}, rezervde {Q(reserved)}.",
            $"Bo'sh qoldiqdan ortiq: “{title}” — bo'sh {Q(free)}, zaxirada {Q(reserved)}.");

    /// <summary>«Свободно 12 (в резерве 3)» — для таблицы товаров и карточки.</summary>
    public static string FreeText(double free, double reserved) =>
        Tr.T($"Свободно {Q(free)} (в резерве {Q(reserved)})", $"Бош {Q(free)} (резервде {Q(reserved)})", $"Free {Q(free)} (reserved {Q(reserved)})",
            $"Serbest {Q(free)} (rezervde {Q(reserved)})", $"Bo'sh {Q(free)} (zaxirada {Q(reserved)})");

    public static string Q(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    // ───────────── JSON ─────────────

    private static IEnumerable<JsonElement> Rows(JsonElement d, string alt)
    {
        var arr = d;
        if (d.ValueKind == JsonValueKind.Object)
            foreach (var k in new[] { "results", alt, "items", "products" })
                if (d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
                {
                    arr = v;
                    break;
                }
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object)
                    yield return e;
    }

    internal static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    internal static double? Num(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static DateTime? Date(JsonElement o, string name) => ServerSerials.ParseDate(Str(o, name));

    /// <summary>Поле-справочник: строка или объект {name|full_name}.</summary>
    private static string Named(JsonElement o, string name)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            return Str(v, "name") is { Length: > 0 } n ? n : Str(v, "full_name");
        return Str(o, name);
    }
}
