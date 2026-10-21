using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

// 2026-10-20, этап П0 (перенос «Поставщиков» с сайта NurCRM, docs/«Поставщики и долги поставщикам в NurCRM — исследование 2026-10-20.md»):
// модели и запросы ЧТЕНИЯ по поставщикам + создание/правка карточки поставщика (реквизиты). Денежных запросов здесь нет
// (оплаты, сделки, расходы, приходы — этапы П3–П5).

/// <summary>Поставщик — контрагент NurCRM с type="suppliers" (GET clients/?type=suppliers). Score — расчётный счёт (так поле
/// называет сайт), Llc — «ОсОО / компания».</summary>
public sealed record SupplierInfo(
    string Id, string Name, string Phone, string Email, string Llc, string Inn, string Okpo, string Score, string Bik, string Address,
    DateTime? CreatedAt)
{
    /// <summary>Вторая строка в списке: компания и ИНН, если заданы.</summary>
    public string Details => string.Join(" · ", new[] { Llc, Inn.Length > 0 ? "ИНН " + Inn : "" }.Where(x => x.Length > 0));
}

/// <summary>Реквизиты для «+ Поставщик» / «Изменить» — те же поля, что форма сайта («Новый поставщик» → «Реквизиты и адрес»).</summary>
public sealed record SupplierRequisites(string Name, string Phone, string Email, string Llc, string Inn, string Okpo, string Score, string Bik, string Address);

/// <summary>GET clients/{id}/kpis/: у поставщика debt — наш долг ему (сумма remaining_debt сделок-долгов), prepayment — предоплаты.</summary>
public sealed record SupplierKpis(double Debt, int DebtCount, double Prepayment, int PrepaymentCount, double Sales, int SalesCount);

/// <summary>Взнос графика сделки-долга (installments).</summary>
public sealed record SupplierInstallment(
    string Id, int Number, DateTime? DueDate, double Amount, double PaidAmount, double Remaining, bool IsOverdue, int OverdueDays,
    double OverdueAmount, DateTime? PaidOn);

/// <summary>Оплата по сделке (payments): kind "pay" — оплата взноса.</summary>
public sealed record SupplierPayment(
    string Id, string DealId, string DealTitle, int InstallmentNumber, string Kind, double Amount, DateTime? PaidDate, DateTime? CreatedAt,
    string Method, string Note);

/// <summary>Движение денег, привязанное к сделке (cashflows): касса, сумма, название.</summary>
public sealed record SupplierCashflow(string Id, string Type, string Name, double Amount, string CashboxName, DateTime? CreatedAt);

/// <summary>Сделка контрагента (deals). У поставщика: debt — закупка в долг (мы должны), prepayment — предоплата поставщику,
/// sale — оплаченная закупка (её создаёт карточка товара сайта; это не долг).</summary>
public sealed record SupplierDeal(
    string Id, string Title, string Kind, double Amount, double Prepayment, double Remaining, DateTime? FirstDueDate, DateTime? CreatedAt,
    string Note, IReadOnlyList<SupplierInstallment> Installments, IReadOnlyList<SupplierPayment> Payments, IReadOnlyList<SupplierCashflow> Cashflows)
{
    public bool IsDebt => Kind == "debt";
    public bool IsPrepayment => Kind == "prepayment";
    public bool IsOpen => Remaining > 0.005;
    public double Paid => Math.Max(0, Amount - Remaining);
    /// <summary>Просрочено по графику (сумма overdue_amount взносов).</summary>
    public double Overdue => IsOpen ? Installments.Sum(i => i.OverdueAmount) : 0;
    public int OverdueDays => IsOpen && Installments.Count > 0 ? Installments.Max(i => i.OverdueDays) : 0;
    /// <summary>Ближайший неоплаченный взнос (по сроку).</summary>
    public SupplierInstallment? NextInstallment => IsOpen
        ? Installments.Where(i => i.Remaining > 0.005 && i.DueDate.HasValue).OrderBy(i => i.DueDate).FirstOrDefault()
        : null;
}

/// <summary>Строка «Истории закупок» (GET suppliers/{id}/purchases/): товар, количество, сумма, способ оплаты cash/debt/prepayment.</summary>
public sealed record SupplierPurchaseLine(string Id, string ProductName, double Quantity, string Unit, double Amount, string PaymentType, DateTime? PurchasedAt);

/// <summary>Строка прихода (suppliers/receipts/{id}/ → items).</summary>
public sealed record SupplierReceiptLine(string Id, string ProductId, string ProductName, string Code, double Qty, double PurchasePrice, double ReturnedQty, string Unit);

/// <summary>Приход от поставщика (GET suppliers/receipts/). Способа оплаты в приходе сервер не отдаёт (вопрос бэкенду 6.8).</summary>
public sealed record SupplierReceipt(
    string Id, string SupplierId, string SupplierName, string CreatedBy, DateTime? CreatedAt, int ItemsCount, double Total, double ReturnedAmount,
    IReadOnlyList<SupplierReceiptLine> Lines);

/// <summary>Товар поставщика (GET suppliers/{id}/products/ — товары с полем client = этот поставщик).</summary>
public sealed record SupplierProduct(string Id, string Name, string Code, string Article, double Price, double PurchasePrice, double Quantity);

/// <summary>Возврат поставщику (GET suppliers/returns/): compensation — cash / debt_offset / none.</summary>
public sealed record SupplierReturnLine(string ProductName, double Qty, string Unit, double PurchasePrice, double Total);

public sealed record SupplierReturnInfo(
    string Id, string SupplierId, string SupplierName, string ReceiptId, DateTime? CreatedAt, string Reason, string Compensation, double Total,
    string CreatedBy, string Comment, IReadOnlyList<SupplierReturnLine> Lines);

/// <summary>Строка списка «Поставщики»: долг (kpis), предоплата, просрочка и ближайший срок (по сделкам), последняя закупка (приходы).
/// Loaded=false — долг по поставщику получить не удалось (строка показывает «не загрузилось»).</summary>
public sealed record SupplierSummary(
    SupplierInfo Info, double Debt, double Prepayment, double Overdue, DateTime? NextDue, double NextDueAmount, DateTime? LastPurchase, bool Loaded);

/// <summary>Закупка (одна отметка времени в истории закупок): строки и способ оплаты.</summary>
public sealed record SupplierPurchaseGroup(DateTime At, IReadOnlyList<SupplierPurchaseLine> Lines)
{
    public double Total => Lines.Sum(l => l.Amount);
    /// <summary>cash / debt / prepayment / mixed / "" (неизвестно).</summary>
    public string PaymentType
    {
        get
        {
            var kinds = Lines.Select(l => l.PaymentType).Where(k => k.Length > 0).Distinct().ToList();
            return kinds.Count switch { 0 => "", 1 => kinds[0], _ => "mixed" };
        }
    }
}

/// <summary>2026-10-20, этап П0: запросы к NurCRM по поставщикам — список и карточка, kpis, сделки (долги с графиком и оплатами),
/// история закупок, приходы, товары поставщика, возвраты. Список и создание — через ICatalogApiService (те же запросы, что у
/// приёмки и карточки товара), остальное — прямыми GET. Только чтение и реквизиты поставщика, денег не трогает.</summary>
public sealed class SupplierApiService
{
    private readonly NurMarketApiClient _api;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Не больше 4 запросов kpis/сделок одновременно (как уточнение долгов клиентов, DebtsView).</summary>
    private static readonly SemaphoreSlim Throttle = new(4, 4);

    public SupplierApiService(NurMarketApiClient api) => _api = api;

    private static ICatalogApiService Catalog => PosApp.CatalogApi;

    // ------------------------------------------------------------------ поставщики

    /// <summary>Все поставщики компании (GET clients/?type=suppliers — тот же запрос, что у приёмки), по названию.</summary>
    public async Task<List<SupplierInfo>> ListAsync(CancellationToken ct = default)
    {
        var data = await Catalog.ListSuppliersAsync(ct).ConfigureAwait(false);
        return Rows(data).Select(ParseSupplier).Where(s => s is not null).Select(s => s!)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<SupplierInfo?> GetAsync(string id, CancellationToken ct = default) =>
        ParseSupplier(await GetJsonAsync($"api/main/clients/{Esc(id)}/", null, ct).ConfigureAwait(false));

    /// <summary>Новый поставщик — POST clients/ с type=suppliers (CatalogApiService.CreateSupplierAsync, как «Новый поставщик» сайта):
    /// имя и телефон обязательны, реквизиты — только заполненные.</summary>
    public async Task<SupplierInfo?> CreateAsync(SupplierRequisites r, CancellationToken ct = default)
    {
        var extra = new Dictionary<string, string>();
        foreach (var (key, value) in Fields(r))
            if (key is not ("full_name" or "phone") && value.Length > 0)
                extra[key] = value;
        var row = await Catalog.CreateSupplierAsync(r.Name, r.Phone, ct, extra).ConfigureAwait(false);
        return ParseSupplier(row);
    }

    /// <summary>«Изменить» — PATCH clients/{id}/ только изменённых полей (стёртое поле уходит пустой строкой).</summary>
    public async Task<SupplierInfo?> UpdateAsync(SupplierInfo old, SupplierRequisites r, CancellationToken ct = default)
    {
        var before = Fields(new SupplierRequisites(old.Name, old.Phone, old.Email, old.Llc, old.Inn, old.Okpo, old.Score, old.Bik, old.Address))
            .ToDictionary(x => x.Key, x => x.Value);
        var body = new Dictionary<string, string>();
        foreach (var (key, value) in Fields(r))
            if (!string.Equals(before.GetValueOrDefault(key) ?? "", value, StringComparison.Ordinal))
                body[key] = value;
        if (body.Count == 0)
            return old;
        var row = await _api.RequestAsync(HttpMethod.Patch, $"api/main/clients/{Esc(old.Id)}/", body, null, ct, Timeout).ConfigureAwait(false);
        return ParseSupplier(row) ?? old;
    }

    private static IEnumerable<(string Key, string Value)> Fields(SupplierRequisites r)
    {
        yield return ("full_name", r.Name.Trim());
        yield return ("phone", r.Phone.Trim());
        yield return ("email", r.Email.Trim());
        yield return ("llc", r.Llc.Trim());
        yield return ("inn", r.Inn.Trim());
        yield return ("okpo", r.Okpo.Trim());
        yield return ("score", r.Score.Trim());
        yield return ("bik", r.Bik.Trim());
        yield return ("address", r.Address.Trim());
    }

    // ------------------------------------------------------------------ долги (сделки)

    /// <summary>GET clients/{id}/kpis/ — null, если сервер не отдал (тогда долг считается по сделкам, как функция Mt сайта).</summary>
    public async Task<SupplierKpis?> GetKpisAsync(string supplierId, CancellationToken ct = default)
    {
        var d = await GetJsonAsync($"api/main/clients/{Esc(supplierId)}/kpis/", null, ct).ConfigureAwait(false);
        if (d.ValueKind != JsonValueKind.Object)
            return null;
        static (double, int) Part(JsonElement root, string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? (Num(p, "amount"), (int)Num(p, "count")) : (0, 0);
        var (debt, debtCount) = Part(d, "debt");
        var (pre, preCount) = Part(d, "prepayment");
        var (sale, saleCount) = Part(d, "sale");
        return new SupplierKpis(debt, debtCount, pre, preCount, sale, saleCount);
    }

    /// <summary>Сделки поставщика (GET clients/{id}/deals/) — с графиком взносов, оплатами и движениями денег; новые сверху.</summary>
    public async Task<List<SupplierDeal>> GetDealsAsync(string supplierId, CancellationToken ct = default)
    {
        var d = await GetJsonAsync($"api/main/clients/{Esc(supplierId)}/deals/", new Dictionary<string, string> { ["page_size"] = "500" }, ct).ConfigureAwait(false);
        return Rows(d).Select(ParseDeal).OrderByDescending(x => x.CreatedAt ?? DateTime.MinValue).ToList();
    }

    // ------------------------------------------------------------------ закупки, приходы, товары, возвраты

    /// <summary>«История закупок» поставщика (GET suppliers/{id}/purchases/). Available=false — у сервера нет этого адреса
    /// (сайт пишет «станет доступна после обновления сервера»).</summary>
    public async Task<(List<SupplierPurchaseLine> Lines, double Total, int Count, bool Available)> GetPurchasesAsync(string supplierId, CancellationToken ct = default)
    {
        try
        {
            var d = await GetJsonAsync($"api/main/suppliers/{Esc(supplierId)}/purchases/", new Dictionary<string, string> { ["page_size"] = "500" }, ct).ConfigureAwait(false);
            var lines = Rows(d).Select(x => new SupplierPurchaseLine(Str(x, "id"), Str(x, "product_name"), Num(x, "quantity"), Str(x, "unit"), Num(x, "amount"),
                Str(x, "payment_type"), Date(Str(x, "purchased_at")))).ToList();
            var summary = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("summary", out var s) ? s : default;
            var total = summary.ValueKind == JsonValueKind.Object ? Num(summary, "total_amount") : lines.Sum(l => l.Amount);
            var count = summary.ValueKind == JsonValueKind.Object ? (int)Num(summary, "total_count") : lines.Count;
            return (lines, total, count, true);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return (new List<SupplierPurchaseLine>(), 0, 0, false);
        }
    }

    /// <summary>Приходы (GET suppliers/receipts/, строки — внутри документа). supplierId — только его (фильтр сервера и ещё раз здесь).</summary>
    public async Task<(List<SupplierReceipt> Receipts, double Total)> GetReceiptsAsync(string? supplierId, int limit, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["page"] = "1", ["limit"] = limit.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(supplierId))
            query["supplier_id"] = supplierId;
        var d = await GetJsonAsync("api/main/suppliers/receipts/", query, ct).ConfigureAwait(false);
        var list = Rows(d).Select(ParseReceipt).Where(r => string.IsNullOrEmpty(supplierId) || r.SupplierId.Length == 0 || r.SupplierId == supplierId).ToList();
        var total = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("meta", out var m) ? Num(m, "total_amount") : list.Sum(r => r.Total);
        return (list, total);
    }

    public async Task<SupplierReceipt> GetReceiptAsync(string receiptId, CancellationToken ct = default) =>
        ParseReceipt(await GetJsonAsync($"api/main/suppliers/receipts/{Esc(receiptId)}/", null, ct).ConfigureAwait(false));

    /// <summary>Товары поставщика (GET suppliers/{id}/products/).</summary>
    public async Task<List<SupplierProduct>> GetProductsAsync(string supplierId, CancellationToken ct = default)
    {
        var d = await GetJsonAsync($"api/main/suppliers/{Esc(supplierId)}/products/", null, ct).ConfigureAwait(false);
        return Rows(d).Select(x => new SupplierProduct(Str(x, "id"), Str(x, "name"), Str(x, "code"), Str(x, "article"), Num(x, "price"), Num(x, "purchase_price"), Num(x, "quantity")))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Возвраты поставщику (GET suppliers/returns/ — те же, что в разделе «Возвраты поставщикам»).</summary>
    public async Task<List<SupplierReturnInfo>> GetReturnsAsync(string? supplierId, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["page"] = "1", ["limit"] = "200" };
        if (!string.IsNullOrEmpty(supplierId))
            query["supplier_id"] = supplierId;
        var d = await GetJsonAsync("api/main/suppliers/returns/", query, ct).ConfigureAwait(false);
        return Rows(d).Select(ParseReturn).Where(r => string.IsNullOrEmpty(supplierId) || r.SupplierId.Length == 0 || r.SupplierId == supplierId)
            .OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue).ToList();
    }

    // ------------------------------------------------------------------ сводка для списка

    /// <summary>Долг, предоплата, просрочка и ближайший срок по каждому поставщику: kpis и сделки (не больше 4 запросов сразу),
    /// последняя закупка — по одной странице приходов. onEach — после каждого поставщика (для постепенной отрисовки).</summary>
    public async Task<List<SupplierSummary>> LoadSummariesAsync(IReadOnlyList<SupplierInfo> suppliers, Action<SupplierSummary>? onEach, CancellationToken ct = default)
    {
        var last = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (receipts, _) = await GetReceiptsAsync(null, 200, ct).ConfigureAwait(false);
            foreach (var r in receipts)
                if (r.SupplierId.Length > 0 && r.CreatedAt is { } at && (!last.TryGetValue(r.SupplierId, out var was) || at > was))
                    last[r.SupplierId] = at;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Поставщики: приходы для «последней закупки» не получены ({ex.Message}).", "WARNING");
        }

        var tasks = suppliers.Select(async s =>
        {
            await Throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lastAt = last.TryGetValue(s.Id, out var l) ? l : (DateTime?)null;
                SupplierKpis? kpis = null;
                List<SupplierDeal>? deals = null;
                try { kpis = await GetKpisAsync(s.Id, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"Поставщики: kpis {s.Id} не получены ({ex.Message}).", "WARNING"); }
                try { deals = await GetDealsAsync(s.Id, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"Поставщики: сделки {s.Id} не получены ({ex.Message}).", "WARNING"); }
                var summary = Summarize(s, kpis, deals, lastAt);
                onEach?.Invoke(summary);
                return summary;
            }
            finally
            {
                Throttle.Release();
            }
        }).ToList();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    /// <summary>Сводка по одному поставщику: долг и предоплата — из kpis (сервер), без kpis — по сделкам; просрочка и ближайший
    /// срок — по графику сделок-долгов.</summary>
    public static SupplierSummary Summarize(SupplierInfo s, SupplierKpis? kpis, IReadOnlyList<SupplierDeal>? deals, DateTime? lastPurchase)
    {
        var open = (deals ?? Array.Empty<SupplierDeal>()).Where(d => d.IsDebt && d.IsOpen).ToList();
        var debt = kpis?.Debt ?? open.Sum(d => d.Remaining);
        var prepayment = Advance(kpis, deals);
        var next = open.Select(d => d.NextInstallment).Where(i => i is not null).OrderBy(i => i!.DueDate).FirstOrDefault();
        return new SupplierSummary(s, debt, prepayment, open.Sum(d => d.Overdue), next?.DueDate, next?.Remaining ?? 0, lastPurchase,
            kpis is not null || deals is not null);
    }

    /// <summary>2026-10-20, живой аудит владельца: «Аванс у поставщика» у «кума» был 31 640 при предоплате «сумма 31 628 · внесено 12»:
    /// kpis.prepayment сервера складывает сумму сделки-предоплаты (стоимость товара) и внесённое. Аванс — это деньги, которые мы
    /// реально отдали вперёд: «внесено» (поле prepayment) по сделкам kind=prepayment. Сделки не получены — как раньше, цифра kpis.</summary>
    public static double Advance(SupplierKpis? kpis, IReadOnlyList<SupplierDeal>? deals) =>
        deals is not null
            ? deals.Where(d => d.IsPrepayment).Sum(d => Math.Max(0, d.Prepayment))
            : kpis?.Prepayment ?? 0;

    // ------------------------------------------------------------------ сверка (только показ, ничего не исправляет)

    /// <summary>Закупки по истории, сгруппированные по времени (одна отметка времени = один приход), новые сверху.</summary>
    public static List<SupplierPurchaseGroup> GroupPurchases(IEnumerable<SupplierPurchaseLine> lines) =>
        lines.Where(l => l.PurchasedAt.HasValue)
            .GroupBy(l => new DateTime(l.PurchasedAt!.Value.Ticks - l.PurchasedAt.Value.Ticks % TimeSpan.TicksPerSecond))
            .Select(g => new SupplierPurchaseGroup(g.Key, g.ToList()))
            .OrderByDescending(g => g.At).ToList();

    /// <summary>2026-10-20, исследование 3.2: закупка «в долг», у которой нет сделки-долга (сайт создаёт её в ту же секунду, касса —
    /// нет). Ищем сделку kind=debt, созданную за 2 минуты до или 10 минут после закупки. Только для предупреждения — П3 исправит.</summary>
    /// <param name="markerOf">2026-10-20, этап П3: метка закупки (receipt:&lt;id прихода&gt;) — долг, записанный позже кнопкой «Записать долг»,
    /// создан не в те минуты: такую закупку узнаём по метке в сделке.</param>
    public static List<SupplierPurchaseGroup> DebtPurchasesWithoutDeal(IEnumerable<SupplierPurchaseGroup> groups, IReadOnlyList<SupplierDeal> deals,
        Func<SupplierPurchaseGroup, string?>? markerOf = null) =>
        groups.Where(g => g.Lines.Any(l => l.PaymentType == "debt"))
            .Where(g => !deals.Any(d => d.IsDebt && d.CreatedAt is { } at && at >= g.At.AddMinutes(-2) && at <= g.At.AddMinutes(10)))
            .Where(g => markerOf?.Invoke(g) is not { Length: > 0 } marker || !deals.Any(d => d.IsDebt && SupplierDebtRecorder.HasMarker(d, marker)))
            // 2026-10-21 (ревью денег, мелочь): долг, записанный позже с меткой «receipt-key:» (без номера прихода), — тоже записан.
            .Where(g => !deals.Any(d => SupplierDebtRecorder.IsReceiptKeyDealFor(d, g.At)))
            .ToList();

    /// <summary>Похоже на двойную запись: две сделки-долга с одним названием и суммой в пределах 5 минут (у NBS — «Долг кума» ×2).
    /// Возвращает вторую (более позднюю) сделку каждой пары.</summary>
    public static List<SupplierDeal> SuspectedDuplicates(IReadOnlyList<SupplierDeal> deals)
    {
        var debts = deals.Where(d => d.IsDebt && d.CreatedAt.HasValue).OrderBy(d => d.CreatedAt).ToList();
        var result = new List<SupplierDeal>();
        for (var i = 1; i < debts.Count; i++)
        {
            var a = debts[i - 1];
            var b = debts[i];
            if (a.Title == b.Title && Math.Abs(a.Amount - b.Amount) < 0.005 && (b.CreatedAt!.Value - a.CreatedAt!.Value) <= TimeSpan.FromMinutes(5))
                result.Add(b);
        }
        return result;
    }

    // ------------------------------------------------------------------ разбор ответов

    private Task<JsonElement> GetJsonAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct) =>
        _api.RequestAsync(HttpMethod.Get, path, null, query, ct, Timeout);

    private static string Esc(string id) => Uri.EscapeDataString(id.Trim());

    public static SupplierInfo? ParseSupplier(JsonElement x)
    {
        var id = Str(x, "id");
        if (id.Length == 0)
            return null;
        var name = Str(x, "full_name") is { Length: > 0 } n ? n : Str(x, "name") is { Length: > 0 } n2 ? n2 : Str(x, "llc") is { Length: > 0 } l ? l : Str(x, "phone");
        return new SupplierInfo(id, name.Trim(), Str(x, "phone").Trim(), Str(x, "email").Trim(), Str(x, "llc").Trim(), Str(x, "inn").Trim(), Str(x, "okpo").Trim(),
            Str(x, "score").Trim(), Str(x, "bik").Trim(), Str(x, "address").Trim(), Date(Str(x, "created_at")));
    }

    // 2026-10-20, этап П3: internal — им же разбирает сделки запись долга при приёмке (SupplierDebtRecorder).
    internal static SupplierDeal ParseDeal(JsonElement x)
    {
        var id = Str(x, "id");
        var title = Str(x, "title");
        var installments = Arr(x, "installments").Select(i => new SupplierInstallment(Str(i, "id"), (int)Num(i, "number"), Date(Str(i, "due_date")),
            Num(i, "amount"), Num(i, "paid_amount"), Has(i, "remaining_for_period") ? Num(i, "remaining_for_period") : Math.Max(0, Num(i, "amount") - Num(i, "paid_amount")),
            Bool(i, "is_overdue"), (int)Num(i, "overdue_days"), Num(i, "overdue_amount"), Date(Str(i, "paid_on")))).OrderBy(i => i.Number).ToList();
        var payments = Arr(x, "payments").Select(p => new SupplierPayment(Str(p, "id"), id, title, (int)Num(p, "installment_number"), Str(p, "kind"), Num(p, "amount"),
            Date(Str(p, "paid_date")), Date(Str(p, "created_at")), Str(p, "payment_method"), Str(p, "note"))).ToList();
        var cashflows = Arr(x, "cashflows").Select(c => new SupplierCashflow(Str(c, "id"), Str(c, "type"), Str(c, "name"), Num(c, "amount"), Str(c, "cashbox_name"),
            Date(Str(c, "created_at")))).ToList();
        var amount = Has(x, "debt_amount") && Num(x, "debt_amount") > 0 ? Num(x, "debt_amount") : Num(x, "amount");
        return new SupplierDeal(id, title, Str(x, "kind"), amount, Num(x, "prepayment"), Num(x, "remaining_debt"), Date(Str(x, "first_due_date")), Date(Str(x, "created_at")),
            Str(x, "note"), installments, payments, cashflows);
    }

    private static SupplierReceipt ParseReceipt(JsonElement x)
    {
        var items = Arr(x, "items") is { Count: > 0 } it ? it : Arr(x, "lines");
        var lines = items.Select(i => new SupplierReceiptLine(Str(i, "id"), Str(i, "product_id") is { Length: > 0 } pid ? pid : Str(i, "product"), Str(i, "product_name"),
            Str(i, "product_code"), Num(i, "qty"), Num(i, "purchase_price"), Num(i, "returned_qty"), Str(i, "unit"))).ToList();
        return new SupplierReceipt(Str(x, "id"), Str(x, "supplier_id"), Str(x, "supplier_name"), Str(x, "created_by_name"), Date(Str(x, "created_at")),
            Has(x, "items_count") ? (int)Num(x, "items_count") : lines.Count, Num(x, "total_amount"), Num(x, "returned_amount"), lines);
    }

    private static SupplierReturnInfo ParseReturn(JsonElement x)
    {
        var items = Arr(x, "items") is { Count: > 0 } it ? it : Arr(x, "lines");
        var lines = items.Select(i => new SupplierReturnLine(Str(i, "product_name"), Num(i, "qty"), Str(i, "unit"), Num(i, "purchase_price"),
            Num(i, "line_total") is > 0 and var lt ? lt : Num(i, "qty") * Num(i, "purchase_price"))).ToList();
        return new SupplierReturnInfo(Str(x, "id"), Str(x, "supplier_id"), Str(x, "supplier_name"), Str(x, "receipt_id"), Date(Str(x, "created_at")),
            Str(x, "reason"), Str(x, "compensation") is { Length: > 0 } c ? c : Str(x, "compensation_type"), Num(x, "total_amount"), Str(x, "created_by_name"),
            Str(x, "comment"), lines);
    }

    private static IEnumerable<JsonElement> Rows(JsonElement d) =>
        d.ValueKind == JsonValueKind.Array ? d.EnumerateArray()
        : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray()
        : Enumerable.Empty<JsonElement>();

    private static List<JsonElement> Arr(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new List<JsonElement>();

    private static bool Has(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    public static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Время сервера → местное. Без пояса (purchased_at «2026-10-15T14:57:59») — время NurCRM, Бишкек (+06:00);
    /// только дата (due_date) — эта дата.</summary>
    public static DateTime? Date(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        var t = s.IndexOf('T');
        var hasZone = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || (t > 0 && (s.IndexOf('+', t) > 0 || s.IndexOf('-', t) > 0));
        if (!hasZone)
            s += "+06:00";
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto.LocalDateTime : null;
    }
}
