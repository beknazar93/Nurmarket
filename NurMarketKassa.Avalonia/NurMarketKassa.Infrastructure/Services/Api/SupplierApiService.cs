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
public sealed record SupplierReceiptLine(string Id, string ProductId, string ProductName, string Code, double Qty, double PurchasePrice, double ReturnedQty, string Unit,
    // 2026-10-30 (нумерация и наценка в накладной): серверные поля строки, если сервер их начнёт отдавать (ТЗ Умару, пункт 13); сейчас null.
    double? MarkupPercent = null, double? SalePrice = null, double? PreviousSalePrice = null);

/// <summary>Приход от поставщика (GET suppliers/receipts/). Способа оплаты в приходе сервер не отдаёт (вопрос бэкенду 6.8).</summary>
public sealed record SupplierReceipt(
    string Id, string SupplierId, string SupplierName, string CreatedBy, DateTime? CreatedAt, int ItemsCount, double Total, double ReturnedAmount,
    IReadOnlyList<SupplierReceiptLine> Lines,
    // 2026-10-30 (нумерация закупок): служебный комментарий прихода («№ ПР-2026-000123», если сервер его хранит), номер сервера (number),
    // номер и дата накладной поставщика (document_number / document_date). Сервер этих полей пока не отдаёт — тогда пусто.
    string Comment = "", string ServerNumber = "", string SupplierDocNumber = "", DateTime? SupplierDocDate = null,
    // 2026-11-01 (ответ Умара: PATCH прихода): версия документа (для If-Match / version), оплата и скидка накладной, если сервер их отдаёт.
    int Version = 0, string PaymentType = "", double PaidAmount = 0, double Discount = 0, double DiscountPercent = 0,
    // 2026-11-01 (проверка бэкенда): готовый номер документа от сервера («ПР-2026-000073») — для показа, если поле пришло.
    string NumberDisplay = "");

/// <summary>2026-11-06 (аудит №5, Л5-13): приход изменён на другом устройстве между показом и удалением (412). <see cref="Current"/> — перечитанный приход (null, если перечитать не вышло);
/// повторять удаление можно только после повторного подтверждения пользователя: <c>DeleteReceiptAsync(..., knownVersion: CurrentVersion)</c> с новым ключом.</summary>
public sealed class ReceiptChangedException : Exception
{
    public string ReceiptId { get; }
    public int KnownVersion { get; }
    public int CurrentVersion { get; }
    public SupplierReceipt? Current { get; }

    public ReceiptChangedException(string receiptId, int knownVersion, int currentVersion, SupplierReceipt? current, Exception inner)
        : base("Приход изменён на другом устройстве (412).", inner)
    {
        ReceiptId = receiptId;
        KnownVersion = knownVersion;
        CurrentVersion = currentVersion;
        Current = current;
    }
}

/// <summary>2026-11-01: версия прихода из серверной истории (GET suppliers/receipts/{id}/history/ — то же, что versions/).</summary>
public sealed record ServerEditVersion(int Version, DateTime? At, string User, string Device, string Reason, double Total, string Summary);

/// <summary>2026-11-01: признаки GET main/capabilities/ для редактора накладных: receipt_edit (PATCH прихода), receipt_edit_discount, receipt_edit_sale_price.</summary>
public sealed record ReceiptEditCaps(bool Edit, bool Discount, bool SalePrice)
{
    public static readonly ReceiptEditCaps None = new(false, false, false);
}

/// <summary>Товар поставщика (GET suppliers/{id}/products/ — товары с полем client = этот поставщик).</summary>
public sealed record SupplierProduct(string Id, string Name, string Code, string Article, double Price, double PurchasePrice, double Quantity);

/// <summary>Возврат поставщику (GET suppliers/returns/): compensation — cash / debt_offset / none.</summary>
public sealed record SupplierReturnLine(string ProductName, double Qty, string Unit, double PurchasePrice, double Total,
    // 2026-11-05 (накладная на возврат поставщику): артикул и штрихкод строки, если сервер их отдаёт; иначе пусто.
    string Article = "", string Barcode = "",
    // 2026-11-10 (правка возврата 24 ч): id строки и товара, скидка и примечание строки — для PATCH возврата (ТЗ Умару, п. 2); сервер пока их может не отдавать.
    string Id = "", string ProductId = "", double Discount = 0, string Note = "");

public sealed record SupplierReturnInfo(
    string Id, string SupplierId, string SupplierName, string ReceiptId, DateTime? CreatedAt, string Reason, string Compensation, double Total,
    string CreatedBy, string Comment, IReadOnlyList<SupplierReturnLine> Lines,
    // 2026-11-05 (накладная на возврат поставщику): серверный номер («ВП-2026-000010» / «ВП-11»), номер прихода-основания и склад, если сервер их отдал.
    string NumberDisplay = "", string ReceiptNumber = "", string Warehouse = "",
    // 2026-11-10 (правка и аннулирование 24 ч): версия, статус, can_edit/can_void, сроки по времени сервера; нет полей — ReturnEditState.None (кнопок нет).
    ReturnEditState? Edit = null)
{
    public ReturnEditState EditState => Edit ?? ReturnEditState.None;
}

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

    // 2026-10-31 (стресс-тест 1.17.68, п. 5): «Поставщики» при каждом открытии слали 2 запроса на поставщика (kpis + сделки: 62 на 30 поставщиков).
    // Сводки держим 60 секунд; любая запись (оплата, возврат, правка, долг, приём) сбрасывает кэш, кнопка «Обновить» читает сервер заново.
    private static readonly TimeSpan SummaryTtl = TimeSpan.FromSeconds(60);
    private static readonly object SummarySync = new();
    private static readonly Dictionary<string, (DateTime At, SupplierSummary Summary)> SummaryCache = new(StringComparer.OrdinalIgnoreCase);

    static SupplierApiService()
    {
        SupplierDebtRecorder.Changed += InvalidateSummaries;
        PurchasesStore.Changed += InvalidateSummaries;
    }

    /// <summary>Сбросить кэш сводок поставщиков (после записи долга, оплаты, возврата, правки и по кнопке «Обновить»).</summary>
    public static void InvalidateSummaries()
    {
        lock (SummarySync)
        {
            SummaryCache.Clear();
            _summaryGeneration++; // 2026-10-31 (приёмка 1.17.68, С-6): загрузка, начатая до сброса, не должна положить в кэш долг «до оплаты»
        }
    }

    private static long _summaryGeneration;

    public SupplierApiService(NurMarketApiClient api) => _api = api;

    private static ICatalogApiService Catalog => PosApp.CatalogApi;

    // ------------------------------------------------------------------ поставщики

    /// <summary>Все поставщики компании (GET clients/?type=suppliers — тот же запрос, что у приёмки), по названию.</summary>
    public async Task<List<SupplierInfo>> ListAsync(CancellationToken ct = default)
    {
        // 2026-11-06 (баг 3 журнала клиента): сессия недействительна — запрос на сервер не шлём (тот же отказ, что и был бы), понятная ошибка вместо «пустого списка»; в журнал раз в 10 минут.
        if (SessionExpiryMonitor.IsExpired)
        {
            SessionExpiryMonitor.LogOnce("suppliers-list", "Поставщики: сессия недействительна — запрос списка не отправляю.");
            throw new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401);
        }
        var data = await Catalog.ListSuppliersAsync(ct).ConfigureAwait(false);
        return Rows(data).Select(ParseSupplier).Where(s => s is not null).Select(s => s!)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<SupplierInfo?> GetAsync(string id, CancellationToken ct = default) =>
        ParseSupplier(await GetJsonAsync($"api/main/clients/{Esc(id)}/", null, ct).ConfigureAwait(false));

    /// <summary>Новый поставщик — POST clients/ с type=suppliers (CatalogApiService.CreateSupplierAsync, как «Новый поставщик» сайта):
    /// имя и телефон обязательны, реквизиты — только заполненные.</summary>
    /// <remarks>2026-10-31 (приёмка 1.17.68, С-7): <paramref name="idempotencyKey"/> — заголовок Idempotency-Key (один ключ на попытку создания,
    /// повтор после обрыва уходит с тем же): сервер, который ключ знает, второго поставщика не создаст. Без ключа — как раньше.</remarks>
    public async Task<SupplierInfo?> CreateAsync(SupplierRequisites r, CancellationToken ct = default, string? idempotencyKey = null)
    {
        var extra = new Dictionary<string, string>();
        foreach (var (key, value) in Fields(r))
            if (key is not ("full_name" or "phone") && value.Length > 0)
                extra[key] = value;
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var plain = await Catalog.CreateSupplierAsync(r.Name, r.Phone, ct, extra).ConfigureAwait(false);
            return ParseSupplier(plain);
        }
        // Тело — как у CatalogApiService.CreateSupplierAsync (full_name, phone, type=suppliers + заполненные реквизиты).
        var body = new Dictionary<string, string>
        {
            ["full_name"] = r.Name.Trim(),
            ["phone"] = r.Phone.Trim(),
            ["type"] = "suppliers",
        };
        foreach (var (key, value) in extra)
            if (!body.ContainsKey(key))
                body[key] = value;
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        var (row, _) = await _api.RequestWithHeadersAsync(HttpMethod.Post, "api/main/clients/", body, headers, ct, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
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
        InvalidateSummaries(); // 2026-10-31: правка поставщика — кэш сводок устарел
        try
        {
            var row = await _api.RequestAsync(HttpMethod.Patch, $"api/main/clients/{Esc(old.Id)}/", body, null, ct, Timeout).ConfigureAwait(false);
            return ParseSupplier(row) ?? old;
        }
        finally
        {
            InvalidateSummaries(); // 2026-10-31 (приёмка 1.17.68, С-6): и после ответа — загрузка, начатая во время запроса, могла принести старое
        }
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
        // 2026-11-11 (сводка Умара): виртуальные строки старых долговых чеков («sale:…», is_check_debt) — это долг ПО ЧЕКУ перед нами, а не сделка с поставщиком:
        // без графика и оплат, оплата взноса (deals/{id}/pay/) к ним неприменима, в «наш долг поставщику» не входят — в список сделок не берём.
        return ClientCheckDebtDeals.RealDeals(Rows(d)).Select(ParseDeal).OrderByDescending(x => x.CreatedAt ?? DateTime.MinValue).ToList();
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Ж): оплата ОДНОГО взноса долга поставщику — POST clients/{id}/deals/{deal}/pay/ (тот же адрес,
    /// что у оплаты долга клиента и у окна сделки сайта): {idempotency_key, installment_id, date, note, cashbox_role}. Взнос гасится
    /// целиком — «amount» не передаётся (у долгов клиентов он ломал проверку сервера, SalesApiService.PosPayDebtAsync). Касса — расходов
    /// компании (cashbox_role "expense_variable", как у прихода «оплачено»); принимает ли это сервер для pay/, на NBS не подтверждено
    /// (исследование 2026-10-20, вопрос 6.3) — отказ придёт ApiException, деньги при этом не двигаются. Повтор с ТЕМ ЖЕ ключом
    /// сервер не проводит второй раз. Расход отдельным запросом (construction/cashflows/) НЕ пишем — чтобы не получить двойной расход.</summary>
    public Task<JsonElement> PayInstallmentAsync(string supplierId, string dealId, string installmentId, string idempotencyKey, string note, CancellationToken ct = default)
    {
        if (ClientCheckDebtDeals.RejectVirtualDealId(dealId) is { } virtualDeal) // 2026-11-11: «sale:…» — долг по чеку, не сделка
            return Task.FromException<JsonElement>(virtualDeal);
        var body = new Dictionary<string, string>
        {
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cashbox_role"] = "expense_variable",
            ["note"] = note,
            ["idempotency_key"] = idempotencyKey,
            ["installment_id"] = installmentId.Trim(),
        };
        InvalidateSummaries(); // 2026-10-31: оплата меняет долг — кэш сводок устарел
        return PayInstallmentCoreAsync(supplierId, dealId, body, ct);
    }

    /// <summary>2026-10-31 (приёмка 1.17.68, С-6): сброс кэша сводок ещё раз ПОСЛЕ ответа сервера (успех или сбой — оплата могла пройти).</summary>
    private async Task<JsonElement> PayInstallmentCoreAsync(string supplierId, string dealId, Dictionary<string, string> body, CancellationToken ct)
    {
        try
        {
            return await _api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Esc(supplierId)}/deals/{Esc(dealId)}/pay/", body, null, ct, Timeout).ConfigureAwait(false);
        }
        finally
        {
            InvalidateSummaries();
        }
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

    /// <summary>2026-10-22 (закупки как в вебе): страница «Истории закупок» — тот же запрос, что список «Закупки» сайта
    /// (GET suppliers/receipts/?page&amp;limit&amp;supplier_id&amp;date_from&amp;date_to): приходы страницы, всего приходов (count) и общая сумма
    /// по фильтру (meta.total_amount; нет — сумма страницы). Только чтение.</summary>
    public async Task<(List<SupplierReceipt> Receipts, int Count, double Total, bool HasNext)> GetReceiptsPageAsync(int page, int limit, string? supplierId,
        DateTime? from, DateTime? to, CancellationToken ct = default, string? productId = null)
    {
        var query = new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(supplierId))
            query["supplier_id"] = supplierId;
        if (from is { } f)
            query["date_from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (to is { } t)
            query["date_to"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // 2026-10-30 («Поступления и партии» товара): фильтр по товару — запрос серверу; пока сервер его не знает, лишний параметр
        // игнорируется, и вызывающий отбирает строки товара сам (ProductReceiptsService).
        if (!string.IsNullOrEmpty(productId))
            query["product_id"] = productId;
        var d = await GetJsonAsync("api/main/suppliers/receipts/", query, ct).ConfigureAwait(false);
        var list = Rows(d).Select(ParseReceipt).Where(r => string.IsNullOrEmpty(supplierId) || r.SupplierId.Length == 0 || r.SupplierId == supplierId).ToList();
        var count = d.ValueKind == JsonValueKind.Object && Has(d, "count") ? (int)Num(d, "count") : list.Count;
        var total = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("meta", out var m) && Has(m, "total_amount") ? Num(m, "total_amount") : list.Sum(r => r.Total);
        var hasNext = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
        return (list, count, total, hasNext);
    }

    /// <summary>2026-10-22 (закупки как в вебе): «Рекомендуемый заказ» сайта — его считает сервер: GET suppliers/{id}/recommendations/
    /// ?analysis_days&amp;coverage_days&amp;safety_days (проверено чтением на NBS 22.10). Ответ: settings, summary, results[] — товар
    /// (product_id, product_name, barcode, code, article, unit, is_weight, current_stock, purchase_price), sales (last_7/14/30_days,
    /// previous_*, average_per_day, trend_percent), stock_forecast (days_remaining, status), last_purchase (date, quantity), recommended_quantity.
    /// null — у сервера нет этого адреса (404): тогда «Закупки» считают сами (PurchasePlanner).</summary>
    public async Task<List<PlannerRow>?> GetRecommendationsAsync(string supplierId, PlannerSettings settings, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["analysis_days"] = settings.PeriodDays.ToString(CultureInfo.InvariantCulture),
            ["coverage_days"] = settings.CoverDays.ToString(CultureInfo.InvariantCulture),
            ["safety_days"] = settings.SafetyDays.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = "500",
        };
        // 2026-10-23 (сверка с вебом): сервер отдаёт страницами по page_size, у поставщика больше 500 товаров — читаем все страницы (не больше 10).
        var items = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            query["page"] = page.ToString(CultureInfo.InvariantCulture);
            JsonElement d;
            try
            {
                d = await GetJsonAsync($"api/main/suppliers/{Esc(supplierId)}/recommendations/", query, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
            items.AddRange(Rows(d).ToList());
            if (!(d.ValueKind == JsonValueKind.Object && d.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                break;
        }
        var rows = new List<PlannerRow>();
        foreach (var x in items)
        {
            var id = Str(x, "product_id");
            if (id.Length == 0)
                continue;
            var sales = x.TryGetProperty("sales", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var forecast = x.TryGetProperty("stock_forecast", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var last = x.TryGetProperty("last_purchase", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
            var stock = Num(x, "current_stock");
            var weighted = x.TryGetProperty("is_weight", out var w) && w.ValueKind == JsonValueKind.True;
            var product = new PlannerProduct(id, Str(x, "product_name"), Str(x, "barcode"), Str(x, "code") is { Length: > 0 } c ? c : Str(x, "article"),
                Str(x, "unit") is { Length: > 0 } u ? u : "шт", stock, Num(x, "purchase_price"), weighted);
            double S(string name) => sales.ValueKind == JsonValueKind.Object ? Num(sales, name) : 0;
            var period = settings.PeriodDays is 7 or 14 or 30 ? S($"last_{settings.PeriodDays}_days") : S("last_30_days");
            var perDay = S("average_per_day");
            double? daysLeft = forecast.ValueKind == JsonValueKind.Object && Has(forecast, "days_remaining") ? Num(forecast, "days_remaining") : null;
            var status = (forecast.ValueKind == JsonValueKind.Object ? Str(forecast, "status") : "").ToLowerInvariant();
            // 2026-10-23 (сверка с вебом, проверено чтением на NBS): stock_forecast.status сервера — ok / low / ending / critical.
            // low — хватит на меньше дней, чем «запас до закупки + страховой» (есть рекомендация); ending — «скоро закончится» (порог сервера
            // не зависит от настроек, между 4 и 12 днями); critical — товара нет (остаток 0). Свой «Нет в наличии» для строк сервера
            // убран: сайт показывает «Критический остаток». Статус не пришёл — считаем по дням сами, как раньше.
            PlannerStatus kind;
            if (status.Contains("crit") || status.Contains("out") || status.Contains("empty") || status.Contains("zero"))
                kind = PlannerStatus.Critical;
            else if (status.Contains("end") || status.Contains("soon") || status.Contains("run") || status.Contains("warn"))
                kind = PlannerStatus.Soon;
            else if (status.Contains("low"))
                kind = PlannerStatus.Low;
            // 2026-11-06 (В12, ЖП Н-5): «Нет продаж» — только если не продавалось и за 30 дней. Раньше средняя за выбранный период 0 (например, период 7 дней) давала
            // «Нет продаж» у товара с продажами «0 / 11». Если за период 0, а за 30 дней продавалось — оцениваем запас по средней за 30 дней.
            else if (PlannerStatusRules.HasNoSales(perDay, S("last_30_days")))
                kind = PlannerStatus.NoSales;
            else if (status == "ok")
                kind = PlannerStatus.Enough;
            else if (stock <= 0)
                kind = PlannerStatus.Critical;
            else
            {
                double? left = daysLeft ?? Math.Max(0, stock) / PlannerStatusRules.EffectivePerDay(perDay, S("last_30_days"));
                kind = left is { } dl && dl <= settings.SafetyDays ? PlannerStatus.Critical
                    : left <= settings.CoverDays ? PlannerStatus.Soon
                    : left < settings.CoverDays + settings.SafetyDays ? PlannerStatus.Low : PlannerStatus.Enough;
            }
            double? trend = sales.ValueKind == JsonValueKind.Object && Has(sales, "trend_percent") ? Math.Round(Num(sales, "trend_percent")) : null;
            var newSales = trend is null && period > 0;
            DateTime? lastAt = last.ValueKind == JsonValueKind.Object ? Date(Str(last, "date")) : null;
            rows.Add(new PlannerRow(product, period, S("last_7_days"), S("last_30_days"), perDay, daysLeft, lastAt,
                last.ValueKind == JsonValueKind.Object ? Num(last, "quantity") : 0, kind, Math.Max(0, Num(x, "recommended_quantity")), trend, newSales));
        }
        return rows;
    }

    /// <summary>2026-10-30 (редактор накладных, ТЗ 3.3-6): возврат поставщику POST suppliers/{id}/returns/ с заголовком Idempotency-Key (раньше ключ не слали —
    /// повтор после обрыва мог провести возврат дважды). Тот же ключ дублируется в теле (idempotency_key) — как у остальных денежных POST NurCRM.
    /// Возвращает id возврата, если сервер его вернул.</summary>
    public async Task<string?> PostReturnAsync(string supplierId, IDictionary<string, object?> body, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        body["idempotency_key"] = idempotencyKey.Trim();
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        InvalidateSummaries(); // 2026-10-31: возврат может уйти в счёт долга — кэш сводок устарел
        try
        {
            var (data, _) = await _api.RequestWithHeadersAsync(HttpMethod.Post, $"api/main/suppliers/{Esc(supplierId)}/returns/", body, headers, ct, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            return Str(data, "id") is { Length: > 0 } id ? id : null;
        }
        finally
        {
            InvalidateSummaries(); // 2026-10-31 (приёмка 1.17.68, С-6): и после ответа
        }
    }

    // 2026-10-31: признак receipt_delete из GET main/capabilities/ — кнопка «Удалить» в «Истории закупок» показывается только при нём (ТЗ бэкенду п. 30).
    // 2026-11-01 (контрольное ревью): запрос, разбор и кэш общие с остальными признаками — ServerCapabilities (кэш привязан к аккаунту).
    public async Task<bool> CanDeleteReceiptsAsync(CancellationToken ct = default) =>
        await ServerCapabilities.HasAsync(_api, ServerCapabilities.ReceiptDelete, ct).ConfigureAwait(false);

    /// <summary>2026-10-31: удаление (аннулирование) прихода DELETE suppliers/receipts/{id}/ с Idempotency-Key и причиной
    /// (ТЗ бэкенду п. 30). Сервер откатывает остаток и долг поставщику; отказы 409 — has_sales_after / has_returns /
    /// debt_already_paid / period_closed, 403 — нет права: текст берётся из ответа (ApiException.Message).</summary>
    /// <param name="knownVersion">2026-11-05 (сервер ceba67c, receipt_version_required): версия прихода, которую видит окно; 0 — неизвестна (тогда читаем приход). Касса шлёт её в If-Match и
    /// в теле (version): без неё сервер отвечает 428 version_required. 428 — перечитать приход и повторить с версией; 412 — версия устарела (приход изменён на другом устройстве): НЕ повторяется,
    /// бросается <see cref="ReceiptChangedException"/> (2026-11-06, Л5-13) — повтор с НОВЫМ ключом и новой версией только после повторного подтверждения пользователя.</param>
    public async Task DeleteReceiptAsync(string receiptId, string reason, string idempotencyKey, CancellationToken ct = default, int knownVersion = 0)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        InvalidateSummaries(); // долг поставщику меняется — кэш сводок устарел
        try
        {
            var version = knownVersion > 0 ? knownVersion : await ReadReceiptVersionAsync(receiptId, ct).ConfigureAwait(false);
            var key = idempotencyKey.Trim();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await SendDeleteReceiptAsync(receiptId, reason, key, version, ct).ConfigureAwait(false);
                    return;
                }
                catch (ApiException ex) when (attempt == 1 && ex.StatusCode == 428)
                {
                    // версия не была отправлена или не принята как обязательная: узнаём текущую (из ответа или чтением прихода) и повторяем тем же ключом — запрос отклонён до обработки
                    version = CurrentVersionFrom(ex) ?? await ReadReceiptVersionAsync(receiptId, ct).ConfigureAwait(false);
                    PosLogger.Log($"Закупки: удаление прихода {receiptId} — 428 version_required, повтор с версией {version}.", "INFO");
                }
                catch (ApiException ex) when (ex.StatusCode == 412)
                {
                    // 2026-11-06 (аудит №5, Л5-13): версия устарела — приход правили на другом устройстве. Раньше касса молча перечитывала версию и удаляла НОВЫЙ приход (в него могли добавить строки)
                    // с подтверждением на СТАРЫЙ. Теперь удаление НЕ повторяется: перечитываем приход и отдаём вызывающему расхождение — повтор (с новым ключом) только после повторного подтверждения пользователя.
                    SupplierReceipt? current = null;
                    try { current = await GetReceiptAsync(receiptId, ct).ConfigureAwait(false); }
                    catch (Exception readEx) when (readEx is not OperationCanceledException) { PosLogger.Log($"Закупки: приход {receiptId} после 412 не перечитан ({readEx.Message}).", "WARNING"); }
                    var currentVersion = CurrentVersionFrom(ex) ?? current?.Version ?? 0;
                    PosLogger.Log($"Закупки: удаление прихода {receiptId} — 412: приход изменён на другом устройстве (версия была {version}, стала {currentVersion}); удаление не повторено, нужно новое подтверждение.", "WARNING");
                    throw new ReceiptChangedException(receiptId, version, currentVersion, current, ex);
                }
            }
        }
        finally
        {
            InvalidateSummaries();
        }
    }

    private async Task SendDeleteReceiptAsync(string receiptId, string reason, string key, int version, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = key };
        var body = new Dictionary<string, object?> { ["reason"] = reason.Trim() };
        if (version > 0)
        {
            headers["If-Match"] = "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";
            body["version"] = version;
        }
        await _api.RequestWithHeadersAsync(HttpMethod.Delete, $"api/main/suppliers/receipts/{Esc(receiptId)}/", body, headers, ct, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
    }

    /// <summary>current_version из тела 428/412; null — не прислан.</summary>
    internal static int? CurrentVersionFrom(ApiException ex) =>
        ex.Payload is { } p && ServerErrorCodes.Number(p, "current_version") is { } n && n > 0 ? (int)n : null;

    /// <summary>Текущая версия прихода на сервере (GET suppliers/receipts/{id}/); 0 — сервер её не отдаёт.</summary>
    private async Task<int> ReadReceiptVersionAsync(string receiptId, CancellationToken ct)
    {
        try
        {
            return (await GetReceiptAsync(receiptId, ct).ConfigureAwait(false)).Version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Закупки: версия прихода {receiptId} не прочитана ({ex.Message}).", "WARNING");
            return 0;
        }
    }

    // 2026-11-01 (ответ Умара 01.11): PATCH suppliers/receipts/{id}/ уже на сервере. Признаки receipt_edit (раньше receipt_patch), receipt_edit_discount, receipt_edit_sale_price —
    // из общего ServerCapabilities; ошибка запроса = «нет» (работает запасной путь: возврат / доприход).
    public async Task<ReceiptEditCaps> GetReceiptEditCapsAsync(CancellationToken ct = default)
    {
        var features = await ServerCapabilities.LoadAsync(_api, ct).ConfigureAwait(false);
        return new ReceiptEditCaps(features.Contains("receipt_edit") || features.Contains("receipt_patch"), features.Contains("receipt_edit_discount"), features.Contains("receipt_edit_sale_price"));
    }

    /// <summary>PATCH suppliers/receipts/{id}/ с Idempotency-Key и If-Match: "версия". Тело — только изменяемые поля (reason обязателен). Повтор тем же ключом сервер
    /// отдаёт как replayed. Отказы (version_conflict 412/409, negative_stock, has_returns, debt_already_paid, period_closed, permission_denied) — ApiException с кодом в Payload.</summary>
    public async Task<JsonElement> PatchReceiptAsync(string receiptId, int version, IDictionary<string, object?> body, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        // 2026-11-05 (сервер ceba67c, receipt_version_required): PATCH без версии получает 428 — версия уходит ВСЕГДА (If-Match + version). Версия не известна окну — читаем приход.
        if (version <= 0 && ServerCapabilities.Cached(ServerCapabilities.ReceiptVersionRequired) != false)
            version = await ReadReceiptVersionAsync(receiptId, ct).ConfigureAwait(false);
        void ApplyVersion()
        {
            if (version <= 0)
                return;
            headers["If-Match"] = "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";
            body["version"] = version;
        }
        ApplyVersion();
        try
        {
            try
            {
                var (data, _) = await _api.RequestWithHeadersAsync(HttpMethod.Patch, $"api/main/suppliers/receipts/{Esc(receiptId)}/", body, headers, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                return data;
            }
            catch (ApiException ex) when (ex.StatusCode == 428)
            {
                // 428 version_required: запрос отклонён до обработки — узнаём версию (current_version в ответе или чтением прихода) и повторяем один раз тем же ключом
                version = CurrentVersionFrom(ex) ?? await ReadReceiptVersionAsync(receiptId, ct).ConfigureAwait(false);
                PosLogger.Log($"Закупки: правка прихода {receiptId} — 428 version_required, повтор с версией {version}.", "INFO");
                ApplyVersion();
                var (data, _) = await _api.RequestWithHeadersAsync(HttpMethod.Patch, $"api/main/suppliers/receipts/{Esc(receiptId)}/", body, headers, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                return data;
            }
        }
        finally
        {
            InvalidateSummaries();
        }
    }

    /// <summary>Код ошибки сервера из ответа ({"code": "..."}); пусто — нет.</summary>
    public static string ErrorCode(Exception ex) =>
        ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } p } && p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

    /// <summary>Серверная история правок прихода (GET suppliers/receipts/{id}/history/ = versions/). Не отдаёт — пустой список.</summary>
    public async Task<List<ServerEditVersion>> GetReceiptHistoryAsync(string receiptId, CancellationToken ct = default)
    {
        var d = await GetJsonAsync($"api/main/suppliers/receipts/{Esc(receiptId)}/history/", null, ct).ConfigureAwait(false);
        return Rows(d).Select(x => new ServerEditVersion((int)Num(x, "version"), Date(Str(x, "at")) ?? Date(Str(x, "created_at")),
            Str(x, "user") is { Length: > 0 } u ? u : x.TryGetProperty("user", out var uo) && uo.ValueKind == JsonValueKind.Object ? Str(uo, "name") : Str(x, "user_name"),
            Str(x, "device"), Str(x, "reason"), Num(x, "total"), Str(x, "lines_diff") is { Length: > 0 } ld ? ld : x.TryGetProperty("lines_diff", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.GetRawText() : ""))
            .OrderByDescending(v => v.Version).ToList();
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

    /// <summary>2026-10-30 (нумерация закупок): страница документов серии для индекса номеров — приходы (receipts) или возвраты (returns), без отбора.</summary>
    public DocPageFetcher PageFetcher(DocSeries series, DocNumberingSettings settings) => async (page, limit, ct) =>
    {
        var query = new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture),
        };
        // 2026-11-11: аннулированный возврат держит свой номер — индекс номеров берёт и его (иначе номер мог бы выдаться повторно)
        if (series == DocSeries.Return && ServerCapabilities.Has(ServerCapabilities.SupplierReturnEdit24h))
            query["status"] = "all";
        var d = await GetJsonAsync(series == DocSeries.Return ? "api/main/suppliers/returns/" : "api/main/suppliers/receipts/", query, ct).ConfigureAwait(false);
        return ParseDocPage(d, series, settings);
    };

    /// <summary>Ответ списка → страница документов для индекса номеров (id, дата создания, закреплённый номер из комментария/номера сервера).</summary>
    public static DocPage ParseDocPage(JsonElement d, DocSeries series, DocNumberingSettings settings)
    {
        var docs = new List<DocEntry>();
        foreach (var x in Rows(d))
        {
            var id = Str(x, "id");
            if (id.Length == 0 || Date(Str(x, "created_at")) is not { } at)
                continue;
            // 2026-11-06 (живой тест, Б-3): серверный number_display — первым: он главнее номера, который касса записала в комментарий при отправке (они расходились на единицу).
            var serverNo = Str(x, "number_display");
            var text = (serverNo.Length > 0 ? "№ " + serverNo + " " : "") + Str(x, "number") + " " + Str(x, "comment") + " " + Str(x, "note") + " " + Str(x, "description");
            // 2026-10-30: префикс из номера хранится с документом (смена префикса не перенумеровывает старые).
            var claim = DocumentNumberingCore.ClaimWithPrefixFromText(settings, series, text);
            docs.Add(new DocEntry(id, at, claim?.Year ?? 0, claim?.Seq ?? 0, claim?.Prefix ?? ""));
        }
        var hasNext = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
        var hasCount = d.ValueKind == JsonValueKind.Object && Has(d, "count");
        var count = hasCount ? (int)Num(d, "count") : docs.Count;
        return new DocPage(docs, count, hasNext, hasCount && (count > docs.Count || !hasNext));
    }

    /// <summary>2026-11-05: карточка одного возврата (GET suppliers/returns/{id}/) — для накладной на возврат: может нести номер, артикулы и склад,
    /// которых нет в строке списка.</summary>
    public async Task<SupplierReturnInfo> GetReturnAsync(string returnId, CancellationToken ct = default) =>
        ParseReturn(await GetJsonAsync($"api/main/suppliers/returns/{Esc(returnId)}/", null, ct).ConfigureAwait(false));

    /// <summary>Возвраты поставщику (GET suppliers/returns/ — те же, что в разделе «Возвраты поставщикам»).</summary>
    /// <summary>2026-11-11: есть ли у сервера правка/аннулирование возврата поставщику (признак supplier_return_edit_24h; ждёт не дольше 5 с, сбой — false).
    /// По нему списки просят и аннулированные (?status=all) — с плашкой «АННУЛИРОВАН».</summary>
    public async Task<bool> ReturnEditFeatureAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await ServerCapabilities.HasAsync(_api, ServerCapabilities.SupplierReturnEdit24h, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            return ServerCapabilities.Has(ServerCapabilities.SupplierReturnEdit24h);
        }
    }

    // 2026-11-11 (сервер Умара): по умолчанию сервер отдаёт список БЕЗ аннулированных; includeVoided — ?status=all (плашка «АННУЛИРОВАН»;
    // итоги вызывающий считает без EditState.IsVoided).
    public async Task<List<SupplierReturnInfo>> GetReturnsAsync(string? supplierId, CancellationToken ct = default, bool includeVoided = false)
    {
        var query = new Dictionary<string, string> { ["page"] = "1", ["limit"] = "200" };
        if (!string.IsNullOrEmpty(supplierId))
            query["supplier_id"] = supplierId;
        if (includeVoided)
            query["status"] = "all";
        var d = await GetJsonAsync("api/main/suppliers/returns/", query, ct).ConfigureAwait(false);
        return Rows(d).Select(ParseReturn).Where(r => string.IsNullOrEmpty(supplierId) || r.SupplierId.Length == 0 || r.SupplierId == supplierId)
            .OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue).ToList();
    }

    // ------------------------------------------------------------------ сводка для списка

    /// <summary>Долг, предоплата, просрочка и ближайший срок по каждому поставщику: kpis и сделки (не больше 4 запросов сразу),
    /// последняя закупка — по одной странице приходов. onEach — после каждого поставщика (для постепенной отрисовки).</summary>
    public async Task<List<SupplierSummary>> LoadSummariesAsync(IReadOnlyList<SupplierInfo> suppliers, Action<SupplierSummary>? onEach, CancellationToken ct = default, bool useCache = false)
    {
        // 2026-10-31: сводки моложе 60 секунд берём из кэша (повторное открытие окна), остальным — запросы; «Обновить» зовёт с useCache: false.
        var ready = new List<SupplierSummary>();
        var todo = new List<SupplierInfo>();
        long generation;
        lock (SummarySync)
        {
            generation = _summaryGeneration;
            foreach (var s in suppliers)
            {
                if (useCache && SummaryCache.TryGetValue(s.Id, out var hit) && DateTime.UtcNow - hit.At < SummaryTtl)
                    ready.Add(hit.Summary with { Info = s });
                else
                    todo.Add(s);
            }
        }
        foreach (var r in ready)
            onEach?.Invoke(r);
        if (todo.Count == 0)
            return ready;

        // 2026-10-23 (аудит, круг 6: «Поставщики» 8–12 с): приходы для «последней закупки» грузятся параллельно с kpis/сделками
        // (раньше все поставщики ждали их первыми); kpis и сделки одного поставщика — тоже вместе. Те же запросы, число не растёт.
        var last = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var receiptsTask = Task.Run(async () =>
        {
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
        }, ct);

        var tasks = todo.Select(async s =>
        {
            await Throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                SupplierKpis? kpis = null;
                List<SupplierDeal>? deals = null;
                async Task GetKpis()
                {
                    try { kpis = await GetKpisAsync(s.Id, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"Поставщики: kpis {s.Id} не получены ({ex.Message}).", "WARNING"); }
                }
                async Task GetDeals()
                {
                    try { deals = await GetDealsAsync(s.Id, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"Поставщики: сделки {s.Id} не получены ({ex.Message}).", "WARNING"); }
                }
                // 2026-10-31: сделки нужны, только если у поставщика есть долг или предоплата (иначе сводка из kpis та же) — на поставщиков без долга один запрос вместо двух.
                await GetKpis().ConfigureAwait(false);
                if (kpis is null || kpis.Debt > 0.005 || kpis.DebtCount > 0 || kpis.Prepayment > 0.005 || kpis.PrepaymentCount > 0)
                    await GetDeals().ConfigureAwait(false);
                await receiptsTask.ConfigureAwait(false);
                var lastAt = last.TryGetValue(s.Id, out var l) ? l : (DateTime?)null;
                var summary = Summarize(s, kpis, deals, lastAt);
                if (summary.Loaded)
                    lock (SummarySync)
                        if (generation == _summaryGeneration) // 2026-10-31 (С-6): за время загрузки была запись (оплата, возврат, правка) — в кэш не кладём
                            SummaryCache[s.Id] = (DateTime.UtcNow, summary);
                onEach?.Invoke(summary);
                return summary;
            }
            finally
            {
                Throttle.Release();
            }
        }).ToList();
        ready.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        return ready;
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
        var items = InLineOrder(Arr(x, "items") is { Count: > 0 } it ? it : Arr(x, "lines"));
        var lines = items.Select(i => new SupplierReceiptLine(Str(i, "id"), Str(i, "product_id") is { Length: > 0 } pid ? pid : Str(i, "product"), Str(i, "product_name"),
            Str(i, "product_code"), Num(i, "qty"), Num(i, "purchase_price"), Num(i, "returned_qty"), Str(i, "unit"),
            Has(i, "markup_percent") ? Num(i, "markup_percent") : null, Has(i, "sale_price") ? Num(i, "sale_price") : null,
            Has(i, "previous_sale_price") ? Num(i, "previous_sale_price") : null)).ToList();
        var comment = Str(x, "comment") is { Length: > 0 } c1 ? c1 : Str(x, "note") is { Length: > 0 } c2 ? c2 : Str(x, "description");
        return new SupplierReceipt(Str(x, "id"), Str(x, "supplier_id"), Str(x, "supplier_name"), Str(x, "created_by_name"), Date(Str(x, "created_at")),
            Has(x, "items_count") ? (int)Num(x, "items_count") : lines.Count, Num(x, "total_amount"), Num(x, "returned_amount"), lines,
            comment, Str(x, "number"),
            Str(x, "supplier_document_number") is { Length: > 0 } sdn ? sdn : Str(x, "document_number") is { Length: > 0 } dn ? dn : DocumentNumberingCore.SupplierDocFromText(comment).Number,
            Date(Str(x, "supplier_document_date")) ?? Date(Str(x, "document_date")) ?? DocumentNumberingCore.SupplierDocFromText(comment).Date,
            (int)Num(x, "version"), Str(x, "payment_type"), Num(x, "paid_amount"), Num(x, "discount"), Num(x, "discount_percent"), Str(x, "number_display"));
    }

    /// <summary>Возврат из ответа сервера (строка списка suppliers/returns/ или карточка suppliers/returns/{id}/). 2026-11-05: публичный — по нему же
    /// строится накладная на возврат (SupplierReturnDocuments), в том числе из окна «Возвраты поставщикам», которое держит строки как JSON.</summary>
    public static SupplierReturnInfo ParseReturn(JsonElement x)
    {
        string First(JsonElement e, params string[] names) => names.Select(n => Str(e, n)).FirstOrDefault(v => v.Length > 0) ?? "";
        var items = Arr(x, "items") is { Count: > 0 } it ? it : Arr(x, "lines");
        var lines = items.Select(i => new SupplierReturnLine(Str(i, "product_name"), Num(i, "qty"), Str(i, "unit"), Num(i, "purchase_price"),
            Num(i, "line_total") is > 0 and var lt ? lt : Num(i, "qty") * Num(i, "purchase_price"),
            First(i, "article", "product_article", "product_code"), First(i, "barcode", "product_barcode"),
            Str(i, "id"), First(i, "product_id", "product"), Num(i, "discount"), First(i, "note", "comment"))).ToList();
        // Номер: number_display («ВП-2026-000010»); нет — number (число у сервера) с префиксом «ВП-».
        var number = Str(x, "number_display");
        if (number.Length == 0 && Str(x, "number") is { Length: > 0 } plain)
            number = plain.Any(char.IsLetter) ? plain : "ВП-" + plain;
        var receiptNumber = First(x, "receipt_number_display", "receipt_display", "receipt_number");
        if (!receiptNumber.Any(char.IsLetter))
            receiptNumber = "";
        return new SupplierReturnInfo(Str(x, "id"), Str(x, "supplier_id"), Str(x, "supplier_name"), Str(x, "receipt_id"), Date(Str(x, "created_at")),
            Str(x, "reason"), Str(x, "compensation") is { Length: > 0 } c ? c : Str(x, "compensation_type"), Num(x, "total_amount"), Str(x, "created_by_name"),
            Str(x, "comment"), lines, number, receiptNumber, First(x, "warehouse_name", "warehouse"), ReturnEditState.Parse(x));
    }

    private static IEnumerable<JsonElement> Rows(JsonElement d) =>
        d.ValueKind == JsonValueKind.Array ? d.EnumerateArray()
        : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray()
        : Enumerable.Empty<JsonElement>();

    /// <summary>2026-11-07 (ТЗ «Порядковая нумерация товаров в накладной»): если у ВСЕХ строк прихода есть числовое поле порядка (line_no / position / sort_order) — строки идут по нему,
    /// иначе порядок как в ответе сервера (сортировка устойчивая). Номера «№» накладной = место строки в этом порядке; правка количества/цены его не меняет.</summary>
    internal static List<JsonElement> InLineOrder(List<JsonElement> items)
    {
        if (items.Count < 2)
            return items;
        foreach (var name in new[] { "line_no", "position", "sort_order" })
            if (items.All(i => Has(i, name) && i.GetProperty(name).ValueKind == JsonValueKind.Number))
                return items.Select((item, index) => (Item: item, Index: index)).OrderBy(t => Num(t.Item, name)).ThenBy(t => t.Index).Select(t => t.Item).ToList();
        return items;
    }

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
