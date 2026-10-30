using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, запрос владельца «Общая аналитика по бонусам (десктоп)»: расчёт вкладки «Бонусы» раздела «Аналитика»
/// программы владельца и её выгрузки Excel/Word.
///
/// Откуда цифры (только чтение, деньги кассы не затрагиваются):
///  • остаток бонусов у клиентов (обязательство магазина, 1 бонус = 1 сом) — <c>bonus_balance</c> каждого клиента
///    (GET api/main/clients/, как «Бонусы на счетах» в «Клиентах»);
///  • чеки периода — GET api/main/pos/sales/ (то же, что «Продажи и финансы»): выручка = оплаченные и частично
///    возвращённые чеки (<c>paid</c> + <c>partially_returned</c>, отменённые не входят), скидка = <c>discount_total</c>,
///    клиент чека = поле <c>client</c>. Заголовочную выручку и число чеков окно берёт у «Сводки» (<c>ServerSalesSummary</c>) и
///    передаёт сюда — одно число на все экраны; список чеков нужен только для долей и среднего чека;
///  • списано бонусами — «оплачено бонусами» из отчётов смен сервера (GET api/construction/shifts/{id}/report/, поле
///    <c>bonus_redeemed</c>), смены периода по дате открытия; если сервер даёт 0, а на этом компьютере в журнале кассы есть
///    оплата бонусами — берётся журнал (подписывается). Иначе — «нет данных на сервере»: сервер пишет оплату бонусами в ту же
///    скидку чека, и отделить её от обычной скидки нельзя;
///  • процент начисления и «баллы включены» — настройка магазина (ServerLoyalty).
/// НЕ выдумываем то, чего сервер не отдаёт: «начислено», «сгорело» (срока у бонусов нет), траты бонусов по клиентам, число чеков
/// с оплатой бонусами и средний чек «с бонусами» — «нет данных на сервере» (потребность записана как Б-99 в ТЗ Умару).</summary>
public sealed class BonusAnalyticsData
{
    public sealed record DayRow(DateTime Day, double Revenue, double ClientRevenue, int Receipts, int ClientReceipts, double? Redeemed);

    public sealed record ClientRow(string Id, string Name, string Phone, double Balance, int Receipts, double Spent);

    /// <summary>2026-10-28: строка «По филиалам»: выручка и чеки (по полю филиала чека) и списано бонусами (по филиалу смены).</summary>
    public sealed record BranchRow(string Name, double Revenue, int Receipts, double? Redeemed);

    /// <summary>Откуда «списано бонусами»: None — нет данных; Server — отчёты смен сервера; Local — журнал кассы этого компьютера.</summary>
    public enum RedeemSource { None, Server, Local }

    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>Выручка и число чеков «как в Сводке» (ServerSalesSummary) — заголовочные цифры; null — сводка не ответила.</summary>
    public double? SummaryRevenue { get; init; }
    public int? SummaryReceipts { get; init; }

    // ---- список чеков периода
    public bool SalesLoaded { get; init; }
    public bool SalesTruncated { get; init; }
    public int Receipts { get; init; }
    public double Revenue { get; init; }
    public double Discounts { get; init; }

    /// <summary>В ответе сервера у чеков есть поле клиента; нет — доли «с карточкой» показывать нельзя.</summary>
    public bool ClientFieldKnown { get; init; }
    public int ClientReceipts { get; init; }
    public double ClientRevenue { get; init; }
    public int BuyersCount { get; init; }
    public double AvgReceiptAll => Receipts > 0 ? Revenue / Receipts : 0;
    public double AvgReceiptClient => ClientReceipts > 0 ? ClientRevenue / ClientReceipts : 0;
    public double AvgReceiptNoClient => Receipts - ClientReceipts > 0 ? (Revenue - ClientRevenue) / (Receipts - ClientReceipts) : 0;
    public double ClientShareRevenuePct => Revenue > 0 ? ClientRevenue / Revenue * 100 : 0;
    public double ClientShareReceiptsPct => Receipts > 0 ? ClientReceipts * 100.0 / Receipts : 0;

    /// <summary>Выручка списка чеков сходится со «Сводкой» (допуск 0,5 % или 1 сом); нет одного из чисел — не проверено.</summary>
    public bool? MatchesSummary => SummaryRevenue is { } s && SalesLoaded && !SalesTruncated
        ? Math.Abs(s - Revenue) <= Math.Max(1, Math.Abs(s) * 0.005)
        : null;

    public IReadOnlyList<DayRow> Days { get; init; } = [];

    // ---- по филиалам (2026-10-28): есть, только если сервер дал филиал (название) у чека или смены
    public bool BranchKnown { get; init; }
    public IReadOnlyList<BranchRow> Branches { get; init; } = [];

    // ---- клиенты
    public bool ClientsLoaded { get; init; }
    public int ClientsTotal { get; init; }
    public int ClientsWithBonus { get; init; }
    public double BonusBalanceTotal { get; init; }
    public int ActiveWithBonus { get; init; }
    public IReadOnlyList<ClientRow> TopBalances { get; init; } = [];
    public IReadOnlyList<ClientRow> TopBuyers { get; init; } = [];

    // ---- списано бонусами
    public RedeemSource RedeemedSource { get; init; }
    public double Redeemed { get; init; }
    public int ShiftsChecked { get; init; }

    /// <summary>2026-10-28: доля стоимости продаж, закрытая бонусами, % = списано / (выручка + списано). Выручка сервера уже за вычетом
    /// оплаты бонусами (она лежит в скидке чека), поэтому знаменатель — стоимость до бонусов, и доля не бывает больше 100 %.
    /// null — списано неизвестно или продаж нет.</summary>
    public double? RedeemedSharePct
    {
        get
        {
            var revenue = SummaryRevenue ?? (SalesLoaded ? Revenue : 0);
            var before = revenue + Redeemed;
            return RedeemedSource != RedeemSource.None && before > 0 ? Redeemed / before * 100 : null;
        }
    }

    // ---- настройка магазина
    public bool? PointsEnabled { get; init; }
    public double? PointsPercent { get; init; }

    public static string NoDataText => Tr.T("нет данных на сервере", "серверде маалымат жок", "no data on the server", "sunucuda veri yok", "serverda ma'lumot yo'q");

    // ------------------------------------------------------------------ загрузка

    private sealed record SaleRow(DateTime Day, double Total, double Discount, string? ClientId, string? Branch);

    /// <summary>Название филиала у чека или смены: branch_name / branch_display / cashbox_name; пусто — сервер филиал не дал (голый id не берём).</summary>
    private static string? BranchOf(JsonElement o)
    {
        foreach (var name in new[] { "branch_name", "branch_display", "cashbox_name" })
            if (Str(o, name) is { Length: > 0 } text && !Guid.TryParse(text, out _))
                return text.Trim();
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.Object
            && Str(b, "name") is { Length: > 0 } nested)
            return nested.Trim();
        return null;
    }

    private static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Num(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    /// <summary>Клиент чека: поле client (uuid или объект с id), запасные имена — client_id / customer_id. seen — поле вообще было в строке.</summary>
    private static string? ClientIdOf(JsonElement sale, out bool seen)
    {
        seen = false;
        foreach (var name in new[] { "client", "client_id", "customer", "customer_id" })
        {
            if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty(name, out var v))
                continue;
            seen = true;
            switch (v.ValueKind)
            {
                case JsonValueKind.String when !string.IsNullOrWhiteSpace(v.GetString()):
                    return v.GetString()!.Trim();
                case JsonValueKind.Number:
                    return v.GetRawText();
                case JsonValueKind.Object when Str(v, "id") is { Length: > 0 } id:
                    return id;
            }
        }
        return null;
    }

    /// <summary>Время чека — по часам сервера (как FinanceWindow.TryServerTime): день берётся из самой строки, без пересчёта в пояс компьютера.</summary>
    private static bool TryServerDay(string? text, out DateTime day)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            day = stamp.DateTime.Date;
            return true;
        }
        day = default;
        return false;
    }

    private static bool CountsAsRevenue(string status) =>
        status.Length == 0
        || string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "partially_returned", StringComparison.OrdinalIgnoreCase);

    private static async Task<(List<SaleRow>? Rows, bool Truncated, bool ClientField)> LoadSalesAsync(ISalesApiService sales, DateTime from, DateTime to, CancellationToken ct)
    {
        const int pageSize = 500;
        const int maxPages = 40;
        var rows = new List<SaleRow>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var clientField = false;
        // 2026-10-31: страницы чеков грузим волнами по 4 параллельно (раньше по одной — ~15 с на «Бонусах»); порядок разбора прежний.
        const int wave = 4;
        for (var first = 1; first <= maxPages; first = first == 1 ? 2 : first + wave)
        {
            ct.ThrowIfCancellationRequested();
            var count = first == 1 ? 1 : Math.Min(wave, maxPages - first + 1);
            var tasks = Enumerable.Range(first, count).Select(async pg =>
            {
                try
                {
                    return (Raw: await sales.PosSalesListAsync(pg, pageSize, null, ct, from.Date, to.Date.AddDays(1)).ConfigureAwait(false), Error: (Exception?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (Raw: (List<JsonElement>?)null, Error: ex);
                }
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var stop = false;
            for (var k = 0; k < results.Length && !stop; k++)
            {
                var page = first + k;
                var raw = results[k].Raw;
                if (raw is null)
                {
                    PosLogger.Log($"Аналитика бонусов: список чеков, страница {page}: {results[k].Error?.Message}", "WARNING");
                    if (page == 1)
                        return (null, false, false);
                    truncated = true;
                    stop = true;
                    break;
                }

                var fresh = 0;
                foreach (var el in raw)
                {
                    var id = Str(el, "id");
                    if (!string.IsNullOrEmpty(id) && !seenIds.Add(id))
                        continue;
                    fresh++;
                    var status = Str(el, "status") ?? "";
                    if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) || !CountsAsRevenue(status))
                        continue;
                    // 2026-10-31: «Выручка (без долга)» — продажи способом «в долг» в выручку не входят (как в Сводке и «Продажах и финансах»).
                    if (string.Equals(Str(el, "payment_method")?.Trim(), "debt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryServerDay(Str(el, "created_at"), out var day) || day < from.Date || day > to.Date)
                        continue;
                    var client = ClientIdOf(el, out var seen);
                    clientField |= seen;
                    rows.Add(new SaleRow(day, Num(el, "total"), Num(el, "discount_total"), client, BranchOf(el)));
                }

                if (raw.Count < pageSize || fresh == 0)
                {
                    stop = true;
                    break;
                }
                if (page == maxPages)
                    truncated = true;
            }
            if (stop)
                break;
        }
        return (rows, truncated, clientField);
    }

    /// <summary>Списано бонусами по отчётам смен сервера (смены, открытые в периоде). null — список смен не получен.</summary>
    private static async Task<(double Total, Dictionary<DateTime, double> ByDay, int Shifts, Dictionary<string, double> ByBranch)?> LoadRedeemedAsync(IShiftApiService shifts, DateTime from, DateTime to, CancellationToken ct)
    {
        JsonElement list;
        try
        {
            list = await shifts.ConstructionShiftsListAsync(false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Аналитика бонусов: список смен: {ex.Message}", "WARNING");
            return null;
        }

        var items = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList()
            : list.ValueKind == JsonValueKind.Object && list.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToList()
            : new List<JsonElement>();
        var inPeriod = new List<(string Id, DateTime Day, string? Branch)>();
        foreach (var row in items)
        {
            var id = Str(row, "id");
            if (!string.IsNullOrEmpty(id) && TryServerDay(Str(row, "opened_at"), out var day) && day >= from.Date && day <= to.Date)
                inPeriod.Add((id, day, BranchOf(row)));
        }
        inPeriod = inPeriod.OrderByDescending(s => s.Day).Take(200).ToList();

        var byDay = new Dictionary<DateTime, double>();
        double total = 0;
        var gate = new SemaphoreSlim(4);
        var reports = await Task.WhenAll(inPeriod.Select(async s =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return (s.Day, s.Branch, Report: await NurCrmReportsApi.GetShiftReportAsync(s.Id, ct).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        var counted = 0;
        var byBranch = new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var (day, branch, report) in reports)
        {
            if (report is null)
                continue;
            counted++;
            var value = (double)report.BonusRedeemed;
            total += value;
            byDay[day] = byDay.GetValueOrDefault(day) + value;
            if (branch != null)
                byBranch[branch] = byBranch.GetValueOrDefault(branch) + value;
        }
        return (total, byDay, counted, byBranch);
    }

    /// <summary>Собирает отчёт. summaryRevenue/summaryReceipts — выручка и чеки «Сводки» за тот же период (ServerSalesSummary), окно их передаёт.
    /// null — нет ни чеков, ни клиентов (нет связи с сервером).</summary>
    public static async Task<BonusAnalyticsData?> BuildAsync(DateTime from, DateTime to, ISalesApiService sales, IShiftApiService shifts, IClientsApiService clients,
        double? summaryRevenue, int? summaryReceipts, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;

        var salesTask = LoadSalesAsync(sales, from, to, ct);
        var shiftsTask = LoadRedeemedAsync(shifts, from, to, ct);
        var clientsTask = LoadClientsAsync(clients, ct);
        await Task.WhenAll(salesTask, shiftsTask, clientsTask).ConfigureAwait(false);
        var (saleRows, truncated, clientField) = await salesTask.ConfigureAwait(false);
        var redeemedServer = await shiftsTask.ConfigureAwait(false);
        var clientList = await clientsTask.ConfigureAwait(false);
        if (saleRows is null && clientList is null)
            return null;

        // Настройка магазина: баллы включены и процент (кэш ServerLoyalty; нет — один запрос, ошибка не мешает).
        if (ServerLoyalty.PointsEnabled is null && ServerLoyalty.PointsPercent is null)
        {
            try
            {
                await ServerLoyalty.RefreshShopSettingsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Аналитика бонусов: настройка магазина: {ex.Message}", "WARNING");
            }
        }

        var names = clientList?.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ClientRow>(StringComparer.OrdinalIgnoreCase);

        // ---- чеки
        var rows = saleRows ?? [];
        var withClient = rows.Where(r => r.ClientId != null).ToList();
        var buyers = withClient.GroupBy(r => r.ClientId!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                names.TryGetValue(g.Key, out var known);
                return new ClientRow(g.Key,
                    string.IsNullOrWhiteSpace(known?.Name) ? Tr.T("Клиент без имени", "Аты жок кардар", "Unnamed customer", "Adsız müşteri", "Nomsiz mijoz") : known!.Name,
                    known?.Phone ?? "", known?.Balance ?? 0, g.Count(), g.Sum(x => x.Total));
            })
            .ToList();

        // ---- списано бонусами: сервер → журнал этого компьютера → нет данных
        var source = BonusAnalyticsData.RedeemSource.None;
        double redeemed = 0;
        var shiftsChecked = 0;
        Dictionary<DateTime, double>? redeemedByDay = null;
        if (redeemedServer is { } rs)
        {
            shiftsChecked = rs.Shifts;
            if (rs.Total > 0.005)
            {
                source = BonusAnalyticsData.RedeemSource.Server;
                redeemed = rs.Total;
                redeemedByDay = rs.ByDay;
            }
        }
        if (source == BonusAnalyticsData.RedeemSource.None)
        {
            try
            {
                var local = ClientLoyaltyStore.AdjustmentsBetween(from.ToUniversalTime(), to.AddDays(1).ToUniversalTime());
                if (local.PointsRedeemed > 0.005)
                {
                    source = BonusAnalyticsData.RedeemSource.Local;
                    redeemed = local.PointsRedeemed;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Аналитика бонусов: журнал оплаты бонусами: {ex.Message}", "WARNING");
            }
        }

        var days = new List<DayRow>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = rows.Where(r => r.Day == d).ToList();
            days.Add(new DayRow(d, day.Sum(r => r.Total), day.Where(r => r.ClientId != null).Sum(r => r.Total), day.Count,
                day.Count(r => r.ClientId != null), redeemedByDay != null ? redeemedByDay.GetValueOrDefault(d) : null));
        }

        // ---- по филиалам: выручка/чеки — по филиалу чека, списано бонусами — по филиалу смены (только то, что сервер дал с названием)
        var branchSales = rows.Where(r => r.Branch != null).GroupBy(r => r.Branch!, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(x => x.Total), Receipts: g.Count()), StringComparer.CurrentCultureIgnoreCase);
        var branchRedeemed = redeemedServer?.ByBranch ?? new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
        var branches = branchSales.Keys.Union(branchRedeemed.Keys, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new BranchRow(n, branchSales.TryGetValue(n, out var bs) ? bs.Revenue : 0, branchSales.TryGetValue(n, out var bs2) ? bs2.Receipts : 0,
                redeemedServer != null ? branchRedeemed.GetValueOrDefault(n) : (double?)null))
            .OrderByDescending(b => b.Revenue).ThenByDescending(b => b.Redeemed ?? 0).ToList();

        var withBonus = clientList?.Where(c => c.Balance > 0.005).ToList() ?? [];
        var buyerIds = buyers.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new BonusAnalyticsData
        {
            From = from, To = to,
            SummaryRevenue = summaryRevenue, SummaryReceipts = summaryReceipts,
            SalesLoaded = saleRows != null, SalesTruncated = truncated,
            Receipts = rows.Count, Revenue = rows.Sum(r => r.Total), Discounts = rows.Sum(r => r.Discount),
            ClientFieldKnown = clientField,
            ClientReceipts = withClient.Count, ClientRevenue = withClient.Sum(r => r.Total), BuyersCount = buyers.Count,
            Days = days,
            BranchKnown = branches.Count > 0, Branches = branches,
            ClientsLoaded = clientList != null, ClientsTotal = clientList?.Count ?? 0,
            ClientsWithBonus = withBonus.Count, BonusBalanceTotal = withBonus.Sum(c => c.Balance),
            ActiveWithBonus = withBonus.Count(c => buyerIds.Contains(c.Id)),
            TopBalances = withBonus.OrderByDescending(c => c.Balance).Take(10).ToList(),
            TopBuyers = buyers.OrderByDescending(b => b.Spent).Take(10).ToList(),
            RedeemedSource = source, Redeemed = redeemed, ShiftsChecked = shiftsChecked,
            PointsEnabled = ServerLoyalty.PointsEnabled, PointsPercent = ServerLoyalty.PointsPercent,
        };
    }

    private static async Task<List<ClientRow>?> LoadClientsAsync(IClientsApiService clients, CancellationToken ct)
    {
        try
        {
            var raw = await clients.GetClientsAsync(null, ct).ConfigureAwait(false);
            var result = new List<ClientRow>(raw.Count);
            foreach (var el in raw)
            {
                var id = Str(el, "id");
                if (string.IsNullOrEmpty(id))
                    continue;
                result.Add(new ClientRow(id, (Str(el, "full_name") ?? "").Trim(), (Str(el, "phone") ?? "").Trim(), Num(el, "bonus_balance"), 0, 0));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Аналитика бонусов: список клиентов: {ex.Message}", "WARNING");
            return null;
        }
    }

    // ------------------------------------------------------------------ группировка рядов по дням

    /// <summary>Ряды для диаграммы: до 45 дней — по дням, до 200 — по неделям, дольше — по месяцам (подписи и значения).</summary>
    public IReadOnlyList<(string Label, double Revenue, double ClientRevenue, double? Redeemed)> Buckets()
    {
        var span = Days.Count;
        Func<DayRow, (DateTime Key, string Label)> keyOf;
        if (span <= 45)
            keyOf = d => (d.Day, d.Day.ToString("dd.MM", CultureInfo.InvariantCulture));
        else if (span <= 200)
            keyOf = d =>
            {
                var monday = d.Day.AddDays(-(((int)d.Day.DayOfWeek + 6) % 7));
                return (monday, monday.ToString("dd.MM", CultureInfo.InvariantCulture) + "–" + monday.AddDays(6).ToString("dd.MM", CultureInfo.InvariantCulture));
            };
        else
            keyOf = d => (new DateTime(d.Day.Year, d.Day.Month, 1), d.Day.ToString("MM.yyyy", CultureInfo.InvariantCulture));

        return Days.GroupBy(keyOf)
            .OrderBy(g => g.Key.Key)
            .Select(g => (g.Key.Label, g.Sum(x => x.Revenue), g.Sum(x => x.ClientRevenue),
                g.Any(x => x.Redeemed != null) ? g.Sum(x => x.Redeemed ?? 0) : (double?)null))
            .ToList();
    }
}

/// <summary>2026-10-28: правило отнесения расхода бонуса между филиалами сети — «расход бонуса несёт: магазин, где потратили / где начислили».
/// По умолчанию — где потратили. Хранится в своём JSON рядом с остальными данными программы (UserPreferences не трогаем).</summary>
public static class BonusBranchRule
{
    private static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "bonus-branch-rule.json");

    /// <summary>true — расход бонуса несёт филиал, где бонус потратили; false — где начислили (по умолчанию, решение владельца 28.10:
    /// филиал-начислитель должен филиалу, где бонусы потратили, сумму потраченных там бонусов, 1 бонус = 1 сом).</summary>
    public static bool BearsWhereSpent
    {
        get
        {
            try
            {
                if (File.Exists(PathOf) && JsonDocument.Parse(File.ReadAllText(PathOf)).RootElement.TryGetProperty("bearer", out var v))
                    return string.Equals(v.GetString(), "spent", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Бонусы: правило филиалов не прочитано ({ex.Message}) — по умолчанию «где начислили».", "WARNING");
            }
            return false;
        }
    }

    public static void Save(bool whereSpent)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(new { bearer = whereSpent ? "spent" : "earned" }));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: правило филиалов не сохранено ({ex.Message}).", "WARNING");
        }
    }
}

/// <summary>2026-10-28: выгрузка вкладки «Бонусы» в Excel и Word теми же построителями, что у других аналитик
/// (<see cref="CategoryAnalyticsExport"/>). Цифры — те, что на экране (<see cref="BonusAnalyticsData"/>), заново не считаются.</summary>
[SupportedOSPlatform("windows")]
public static class BonusAnalyticsExport
{
    public static string FileTitle => Tr.T("Аналитика бонусов", "Бонустар аналитикасы", "Bonus analytics", "Bonus analizi", "Bonuslar tahlili");

    private static string Cur => ReportLabels.Currency;
    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static string Period(BonusAnalyticsData d) => $"{ReportFormat.Date(d.From)} — {ReportFormat.Date(d.To)}";
    private static string NoData => BonusAnalyticsData.NoDataText;

    private static string Hint => L(
        "Остаток бонусов — по карточкам клиентов (1 бонус = 1 сом). Выручка периода — как в «Сводке» (оплаченные и частично возвращённые чеки). «Списано бонусами» — из отчётов смен сервера, если сервер их отдаёт; «начислено», «сгорело», траты бонусов по клиентам и средний чек с бонусами сервер пока не отдаёт — по ним «нет данных на сервере», цифры не выдуманы.",
        "Бонустардын калдыгы — кардарлардын карточкалары боюнча (1 бонус = 1 сом). Мезгилдин түшүмү — «Жыйынтыктагыдай» (төлөнгөн жана жарым-жартылай кайтарылган чектер). «Бонус менен төлөндү» — сервердин смена отчёттору боюнча, эгер сервер берсе; «эсептелди», «күйүп кетти», кардарлар боюнча бонус сарптоо жана бонустары бар орточо чек боюнча сервер азырынча бербейт — «серверде маалымат жок», сандар ойдон чыгарылган эмес.",
        "Bonus balance comes from customer cards (1 bonus = 1 som). Revenue for the period matches “Summary” (paid and partly returned receipts). “Paid with bonuses” comes from the server's shift reports when the server provides it; “earned”, “expired”, spending by customer and the average receipt with bonuses are not provided by the server yet — they show “no data on the server”, nothing is invented.",
        "Bonus bakiyesi müşteri kartlarındandır (1 bonus = 1 som). Dönem cirosu «Özet» ile aynıdır (ödenmiş ve kısmen iade edilmiş fişler). «Bonusla ödenen» sunucu verirse vardiya raporlarından alınır; «kazanılan», «yanan», müşteri başına harcama ve bonuslu ortalama fiş henüz sunucudan gelmiyor — «sunucuda veri yok» yazılır, rakam uydurulmaz.",
        "Bonus qoldig'i mijoz kartochkalaridan (1 bonus = 1 so'm). Davr tushumi «Xulosa» bilan bir xil (to'langan va qisman qaytarilgan cheklar). «Bonus bilan to'langan» server bersa, smena hisobotlaridan olinadi; «hisoblangan», «kuygan», mijozlar bo'yicha sarf va bonusli o'rtacha chekni server hozircha bermaydi — «serverda ma'lumot yo'q», raqam to'qilmagan.");

    private static string RedeemSourceNote(BonusAnalyticsData d) => d.RedeemedSource switch
    {
        BonusAnalyticsData.RedeemSource.Server => L("отчёты смен сервера", "сервердин смена отчёттору", "server shift reports", "sunucu vardiya raporları", "server smena hisobotlari"),
        BonusAnalyticsData.RedeemSource.Local => L("журнал кассы на этом компьютере", "бул компьютердеги касса журналы", "till log on this computer", "bu bilgisayardaki kasa günlüğü", "shu kompyuterdagi kassa jurnali"),
        _ => NoData,
    };

    private static List<(string Label, object? Value, XlKind Kind)> Indicators(BonusAnalyticsData d)
    {
        var revenue = d.SummaryRevenue ?? (d.SalesLoaded ? d.Revenue : (double?)null);
        var list = new List<(string, object?, XlKind)>();
        if (revenue is { } rv)
            list.Add((ReportLabels.WithUnit(L("Выручка периода (как в «Сводке»)", "Мезгилдин түшүмү («Жыйынтыктагыдай»)", "Revenue for the period (as in “Summary”)", "Dönem cirosu («Özet» ile aynı)", "Davr tushumi («Xulosa»dagidek)"), Cur), rv, XlKind.Money));
        if (d.SummaryReceipts is { } sr)
            list.Add((L("Чеков за период (как в «Сводке»)", "Мезгилдеги чектер («Жыйынтыктагыдай»)", "Receipts for the period (as in “Summary”)", "Dönemdeki fişler («Özet» ile aynı)", "Davrdagi cheklar («Xulosa»dagidek)"), sr, XlKind.Integer));
        if (d.PointsPercent is { } pp)
            list.Add((L("Процент начисления бонусов, %", "Бонус эсептөө пайызы, %", "Bonus accrual rate, %", "Bonus kazanım oranı, %", "Bonus hisoblash foizi, %"), pp, XlKind.Decimal2));
        if (d.PointsEnabled is { } pe)
            list.Add((L("Бонусы включены в настройке магазина", "Бонустар дүкөндүн жөндөөсүндө күйгүзүлгөн", "Bonuses enabled in the shop settings", "Bonuslar mağaza ayarında açık", "Bonuslar do'kon sozlamasida yoqilgan"), pe ? L("да", "ооба", "yes", "evet", "ha") : L("нет", "жок", "no", "hayır", "yo'q"), XlKind.Text));

        if (d.ClientsLoaded)
        {
            list.Add((ReportLabels.WithUnit(L("Остаток бонусов у клиентов (обязательство магазина)", "Кардарлардагы бонус калдыгы (дүкөндүн милдеттенмеси)", "Bonus balance held by customers (shop liability)", "Müşterilerdeki bonus bakiyesi (mağaza yükümlülüğü)", "Mijozlardagi bonus qoldig'i (do'kon majburiyati)"), Cur), d.BonusBalanceTotal, XlKind.Money));
            list.Add((L("Клиентов с остатком бонусов", "Бонус калдыгы бар кардарлар", "Customers with a bonus balance", "Bonus bakiyesi olan müşteriler", "Bonus qoldig'i bor mijozlar"), d.ClientsWithBonus, XlKind.Integer));
            list.Add((L("Клиентов всего", "Бардык кардарлар", "Customers in total", "Toplam müşteri", "Jami mijozlar"), d.ClientsTotal, XlKind.Integer));
            if (d.SalesLoaded)
                list.Add((L("Активных клиентов с бонусами (купили за период)", "Бонусу бар активдүү кардарлар (мезгилде сатып алган)", "Active customers with bonuses (bought in the period)", "Bonuslu aktif müşteriler (dönemde alışveriş yaptı)", "Bonusli faol mijozlar (davrda xarid qilgan)"), d.ActiveWithBonus, XlKind.Integer));
        }

        list.Add((ReportLabels.WithUnit(L("Списано бонусами за период", "Мезгилде бонус менен төлөндү", "Paid with bonuses in the period", "Dönemde bonusla ödenen", "Davrda bonus bilan to'langan"), Cur),
            d.RedeemedSource == BonusAnalyticsData.RedeemSource.None ? NoData : d.Redeemed, d.RedeemedSource == BonusAnalyticsData.RedeemSource.None ? XlKind.Text : XlKind.Money));
        list.Add((L("Источник «списано бонусами»", "«Бонус менен төлөндү» булагы", "Source of “paid with bonuses”", "«Bonusla ödenen» kaynağı", "«Bonus bilan to'langan» manbasi"), RedeemSourceNote(d), XlKind.Text));
        list.Add((L("Доля продаж, закрытая бонусами, %", "Бонус менен жабылган сатуулардын үлүшү, %", "Share of sales covered by bonuses, %", "Bonusla karşılanan satış payı, %", "Bonus bilan yopilgan sotuv ulushi, %"),
            d.RedeemedSharePct is { } sp ? sp : NoData, d.RedeemedSharePct != null ? XlKind.Decimal2 : XlKind.Text));
        list.Add((ReportLabels.WithUnit(L("Начислено бонусов за период", "Мезгилде бонус эсептелди", "Bonuses earned in the period", "Dönemde kazanılan bonus", "Davrda hisoblangan bonus"), Cur), NoData, XlKind.Text));
        list.Add((ReportLabels.WithUnit(L("Сгорело бонусов", "Күйүп кеткен бонустар", "Bonuses expired", "Yanan bonus", "Kuygan bonuslar"), Cur), NoData, XlKind.Text));
        list.Add((ReportLabels.WithUnit(L("Средний чек с оплатой бонусами", "Бонус менен төлөнгөн орточо чек", "Average receipt paid with bonuses", "Bonusla ödenen ortalama fiş", "Bonus bilan to'langan o'rtacha chek"), Cur), NoData, XlKind.Text));

        if (d.SalesLoaded)
        {
            list.Add((ReportLabels.WithUnit(L("Скидки в чеках (включая бонусы)", "Чектердеги арзандатуулар (бонустар менен)", "Receipt discounts (including bonuses)", "Fişlerdeki indirimler (bonuslar dahil)", "Cheklardagi chegirmalar (bonuslar bilan)"), Cur), d.Discounts, XlKind.Money));
            if (d.ClientFieldKnown)
            {
                list.Add((L("Чеков с карточкой клиента", "Кардар карточкасы бар чектер", "Receipts with a customer card", "Müşteri kartlı fişler", "Mijoz kartochkali cheklar"), d.ClientReceipts, XlKind.Integer));
                list.Add((L("Доля чеков с карточкой клиента, %", "Кардар карточкасы бар чектердин үлүшү, %", "Share of receipts with a customer card, %", "Müşteri kartlı fiş payı, %", "Mijoz kartochkali cheklar ulushi, %"), d.ClientShareReceiptsPct, XlKind.Decimal2));
                list.Add((ReportLabels.WithUnit(L("Выручка с карточкой клиента", "Кардар карточкасы менен түшүм", "Revenue with a customer card", "Müşteri kartlı ciro", "Mijoz kartochkali tushum"), Cur), d.ClientRevenue, XlKind.Money));
                list.Add((ReportLabels.WithUnit(L("Средний чек с карточкой клиента", "Кардар карточкасы бар орточо чек", "Average receipt with a customer card", "Müşteri kartlı ortalama fiş", "Mijoz kartochkali o'rtacha chek"), Cur), d.AvgReceiptClient, XlKind.Money));
                list.Add((ReportLabels.WithUnit(L("Средний чек без карточки клиента", "Кардар карточкасы жок орточо чек", "Average receipt without a customer card", "Müşteri kartsız ortalama fiş", "Mijoz kartochkasiz o'rtacha chek"), Cur), d.AvgReceiptNoClient, XlKind.Money));
            }
            list.Add((ReportLabels.WithUnit(L("Средний чек всех чеков", "Бардык чектердин орточо чеги", "Average receipt, all", "Tüm fişlerin ortalaması", "Barcha cheklar o'rtachasi"), Cur), d.AvgReceiptAll, XlKind.Money));
        }
        return list;
    }

    private static string Show(object? value, XlKind kind) => value switch
    {
        null => "",
        string s => s,
        double v => kind switch
        {
            XlKind.Money => ReportFormat.Money(v),
            XlKind.Integer => ReportFormat.Integer(v),
            _ => v.ToString("0.##", ReportFormat.Culture),
        },
        int n => ReportFormat.Integer(n),
        _ => value.ToString() ?? "",
    };

    public static void ExportToExcel(string path, BonusAnalyticsData d, string? shopName)
    {
        var meta = ReportMeta.Create(FileTitle, shopName, Period(d));
        using var book = new ExcelWorkbookBuilder(path, meta);

        var sum = book.AddSheet(L("Сводка", "Жыйынтык", "Summary", "Özet", "Xulosa"), FileTitle, Hint);
        sum.AddKeyValues(Indicators(d).Select(i => new XlKeyValue(i.Label, i.Value, i.Kind)).ToList(),
            L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), L("Значение", "Мааниси", "Value", "Değer", "Qiymati"));

        var daySheet = book.AddSheet(L("По дням", "Күндөр боюнча", "By day", "Güne göre", "Kunlar bo'yicha"),
            L("Динамика по дням", "Күндөр боюнча динамика", "Daily dynamics", "Günlük dinamik", "Kunlik dinamika"));
        var showRedeemed = d.Days.Any(x => x.Redeemed != null);
        var cols = new List<XlColumn>
        {
            new(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.Date),
            new(ReportLabels.WithUnit(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Cur), XlKind.Money, XlTotal.Sum),
            new(ReportLabels.WithUnit(L("в т. ч. с карточкой клиента", "анын ичинде кардар карточкасы менен", "incl. with a customer card", "bunun müşteri kartlı kısmı", "shu jumladan mijoz kartochkasi bilan"), Cur), XlKind.Money, XlTotal.Sum),
            new(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), XlKind.Integer, XlTotal.Sum),
        };
        if (showRedeemed)
            cols.Add(new XlColumn(ReportLabels.WithUnit(L("Списано бонусами (по дате открытия смены)", "Бонус менен төлөндү (смена ачылган күнү боюнча)", "Paid with bonuses (by shift opening date)", "Bonusla ödenen (vardiya açılış tarihine göre)", "Bonus bilan to'langan (smena ochilgan sana bo'yicha)"), Cur), XlKind.Money, XlTotal.Sum));
        var dayTable = daySheet.AddTable(cols,
            d.Days.Select(x => showRedeemed
                ? new object?[] { x.Day, x.Revenue, x.ClientRevenue, x.Receipts, x.Redeemed ?? 0 }
                : new object?[] { x.Day, x.Revenue, x.ClientRevenue, x.Receipts }));
        if (d.Days.Count > 1)
            daySheet.AddBarChart(L("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum"), dayTable, categoryColumn: 0, valueColumn: 1, maxPoints: 45);

        var balSheet = book.AddSheet(L("Топ по остатку", "Калдык боюнча топ", "Top by balance", "Bakiyeye göre ilk", "Qoldiq bo'yicha top"),
            L("Топ-10 клиентов по остатку бонусов (накоплено сейчас)", "Бонус калдыгы боюнча топ-10 кардар (азыр топтолгон)", "Top 10 customers by bonus balance (accumulated now)", "Bonus bakiyesine göre ilk 10 müşteri (şu an biriken)", "Bonus qoldig'i bo'yicha top-10 mijoz (hozir to'plangan)"));
        var balTable = balSheet.AddTable(
        [
            new XlColumn(ReportLabels.Number, XlKind.Index),
            new XlColumn(L("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), MaxWidth: 40),
            new XlColumn(L("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), XlKind.Code),
            new XlColumn(ReportLabels.WithUnit(L("Остаток бонусов", "Бонус калдыгы", "Bonus balance", "Bonus bakiyesi", "Bonus qoldig'i"), Cur), XlKind.Money, XlTotal.Sum),
        ],
        d.TopBalances.Select((c, i) => new object?[] { i + 1, c.Name, c.Phone, c.Balance }));
        if (d.TopBalances.Count > 0)
            balSheet.AddBarChart(L("Топ-10 по остатку", "Калдык боюнча топ-10", "Top 10 by balance", "Bakiyeye göre ilk 10", "Qoldiq bo'yicha top-10"), balTable, categoryColumn: 1, valueColumn: 3, maxPoints: 10, horizontal: true);

        if (d.BranchKnown)
        {
            var brSheet = book.AddSheet(L("По филиалам", "Филиалдар боюнча", "By branch", "Şubelere göre", "Filiallar bo'yicha"),
                L("Выручка и расход бонусов по филиалам", "Филиалдар боюнча түшүм жана бонус чыгымы", "Revenue and bonus spending by branch", "Şubelere göre ciro ve bonus harcaması", "Filiallar bo'yicha tushum va bonus sarfi"),
                L("Правило по умолчанию: расход бонуса несёт филиал, где начислили; он должен филиалу, где бонусы потратили, сумму потраченных там бонусов (1 бонус = 1 сом). Кто кому должен по филиалам начисления — нет данных на сервере.", "Демейки эреже: бонус чыгымын бонус эсептелген филиал көтөрөт; ал бонус сарпталган филиалга ошол жерде сарпталган бонустардын суммасын берет (1 бонус = 1 сом). Эсептөө филиалдары боюнча ким кимге карыздар — серверде маалымат жок.", "Default rule: the cost is borne by the branch where bonuses were earned; it owes the branch where they were spent the amount spent there (1 bonus = 1 som). Who owes whom by earning branch: no data on the server.", "Varsayılan kural: gideri bonusun kazanıldığı şube üstlenir; bonusların harcandığı şubeye orada harcanan tutarı borçludur (1 bonus = 1 som). Kazanılan şubeye göre kim kime borçlu: sunucuda veri yok.", "Standart qoida: xarajatni bonus hisoblangan filial ko'taradi; u bonus sarflangan filialga o'sha yerda sarflangan summani qarzdor (1 bonus = 1 so'm). Hisoblangan filial bo'yicha kim kimga qarzdor: serverda ma'lumot yo'q."));
            brSheet.AddTable(
            [
                new XlColumn(L("Филиал", "Филиал", "Branch", "Şube", "Filial"), MaxWidth: 40),
                new XlColumn(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), XlKind.Integer, XlTotal.Sum),
                new XlColumn(ReportLabels.WithUnit(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Cur), XlKind.Money, XlTotal.Sum),
                new XlColumn(ReportLabels.WithUnit(L("Потрачено бонусами", "Бонус менен сарпталган", "Spent in bonuses", "Bonusla harcanan", "Bonus bilan sarflangan"), Cur), XlKind.Money, XlTotal.Sum),
            ],
            d.Branches.Select(b => new object?[] { b.Name, b.Receipts, b.Revenue, b.Redeemed ?? 0 }));
        }

        var buySheet = book.AddSheet(L("Топ покупателей", "Топ сатып алуучулар", "Top buyers", "En çok alanlar", "Top xaridorlar"),
            L("Топ-10 клиентов по покупкам за период", "Мезгил боюнча сатып алуулар боюнча топ-10 кардар", "Top 10 customers by purchases in the period", "Dönemdeki alışverişe göre ilk 10 müşteri", "Davrdagi xaridlar bo'yicha top-10 mijoz"),
            L("Траты именно бонусов по клиентам сервер не отдаёт — здесь суммы покупок.", "Кардарлар боюнча так бонус сарптоону сервер бербейт — бул жерде сатып алуу суммалары.", "The server does not give bonus spending by customer — these are purchase totals.", "Sunucu müşteri başına bonus harcamasını vermiyor — burada alışveriş toplamları var.", "Server mijozlar bo'yicha bonus sarfini bermaydi — bu yerda xarid summalari."));
        buySheet.AddTable(
        [
            new XlColumn(ReportLabels.Number, XlKind.Index),
            new XlColumn(L("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), MaxWidth: 40),
            new XlColumn(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), XlKind.Integer, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(L("Сумма покупок", "Сатып алуулар суммасы", "Purchases", "Alışveriş toplamı", "Xaridlar summasi"), Cur), XlKind.Money, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(L("Остаток бонусов", "Бонус калдыгы", "Bonus balance", "Bonus bakiyesi", "Bonus qoldig'i"), Cur), XlKind.Money),
        ],
        d.TopBuyers.Select((c, i) => new object?[] { i + 1, c.Name, c.Receipts, c.Spent, c.Balance }));

        book.Save();
    }

    /// <summary>CSV (UTF-8 с BOM, разделитель «;» — Excel в русской раскладке открывает без мастера): показатели и ряд по дням.</summary>
    public static void ExportToCsv(string path, BonusAnalyticsData d)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        string N(double v) => v.ToString("0.##", ReportFormat.Culture);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Q(FileTitle) + ";" + Q(Period(d)));
        sb.AppendLine(Q(L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich")) + ";" + Q(L("Значение", "Мааниси", "Value", "Değer", "Qiymati")));
        foreach (var i in Indicators(d))
            sb.AppendLine(Q(i.Label) + ";" + Q(i.Value switch { string s => s, double v => N(v), int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "" }));
        sb.AppendLine();
        sb.AppendLine(Q(L("Дата", "Күнү", "Date", "Tarih", "Sana")) + ";" + Q(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")) + ";" + Q(L("С карточкой клиента", "Кардар карточкасы менен", "With a customer card", "Müşteri kartlı", "Mijoz kartochkasi bilan"))
            + ";" + Q(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar")) + ";" + Q(L("Списано бонусами", "Бонус менен төлөндү", "Paid with bonuses", "Bonusla ödenen", "Bonus bilan to'langan")));
        foreach (var x in d.Days)
            sb.AppendLine(ReportFormat.Date(x.Day) + ";" + N(x.Revenue) + ";" + N(x.ClientRevenue) + ";" + x.Receipts + ";" + (x.Redeemed is { } r ? N(r) : ""));
        sb.AppendLine();
        sb.AppendLine(Q(L("Топ-10 по остатку бонусов", "Бонус калдыгы боюнча топ-10", "Top 10 by bonus balance", "Bonus bakiyesine göre ilk 10", "Bonus qoldig'i bo'yicha top-10")));
        foreach (var c in d.TopBalances)
            sb.AppendLine(Q(c.Name) + ";" + Q(c.Phone) + ";" + N(c.Balance));
        sb.AppendLine();
        sb.AppendLine(Q(L("Топ-10 по покупкам за период", "Мезгилдеги сатып алуулар боюнча топ-10", "Top 10 by purchases in the period", "Dönemdeki alışverişe göre ilk 10", "Davrdagi xaridlar bo'yicha top-10")));
        foreach (var c in d.TopBuyers)
            sb.AppendLine(Q(c.Name) + ";" + c.Receipts + ";" + N(c.Spent) + ";" + N(c.Balance));
        if (d.BranchKnown)
        {
            sb.AppendLine();
            sb.AppendLine(Q(L("По филиалам", "Филиалдар боюнча", "By branch", "Şubelere göre", "Filiallar bo'yicha")) + ";" + Q(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar")) + ";" + Q(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")) + ";" + Q(L("Потрачено бонусами", "Бонус менен сарпталган", "Spent in bonuses", "Bonusla harcanan", "Bonus bilan sarflangan")));
            foreach (var b in d.Branches)
                sb.AppendLine(Q(b.Name) + ";" + b.Receipts + ";" + N(b.Revenue) + ";" + (b.Redeemed is { } r ? N(r) : ""));
        }
        File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
    }

    public static void ExportToWord(string path, BonusAnalyticsData d, string? shopName)
    {
        var meta = ReportMeta.Create(FileTitle, shopName, Period(d));
        using var doc = new WordReportBuilder(path, meta);
        doc.TitleBlock();
        doc.Note(Hint);

        doc.Heading1("1. " + L("Итоги бонусной программы", "Бонус программасынын жыйынтыгы", "Bonus programme summary", "Bonus programı özeti", "Bonus dasturi xulosasi"));
        doc.Table(
            [new WColumn(L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), 3), new WColumn(L("Значение", "Мааниси", "Value", "Değer", "Qiymati"), 1.6, Numeric: true)],
            Indicators(d).Select(i => new[] { i.Label, Show(i.Value, i.Kind) }).ToList());

        doc.Heading1("2. " + L("Динамика по дням", "Күндөр боюнча динамика", "Daily dynamics", "Günlük dinamik", "Kunlik dinamika"));
        var buckets = d.Buckets();
        if (buckets.Any(b => b.Revenue > 0))
        {
            doc.Image(AnalyticsChartRenderer.RenderBars(L("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum"),
                buckets.Select(b => (b.Label, b.Revenue)).ToList()), L("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum"));
            doc.Table(
                [new WColumn(L("Период", "Мезгил", "Period", "Dönem", "Davr"), 1.4), new WColumn(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), 1.4, Numeric: true),
                 new WColumn(L("С карточкой клиента", "Кардар карточкасы менен", "With a customer card", "Müşteri kartlı", "Mijoz kartochkasi bilan"), 1.6, Numeric: true),
                 new WColumn(L("Списано бонусами", "Бонус менен төлөндү", "Paid with bonuses", "Bonusla ödenen", "Bonus bilan to'langan"), 1.4, Numeric: true)],
                buckets.Select(b => new[] { b.Label, ReportFormat.Money(b.Revenue), ReportFormat.Money(b.ClientRevenue), b.Redeemed is { } r ? ReportFormat.Money(r) : "—" }).ToList());
        }
        else
            doc.Note(ReportLabels.NoData);

        doc.Heading1("3. " + L("Топ-10 клиентов по остатку бонусов", "Бонус калдыгы боюнча топ-10 кардар", "Top 10 customers by bonus balance", "Bonus bakiyesine göre ilk 10 müşteri", "Bonus qoldig'i bo'yicha top-10 mijoz"));
        if (d.TopBalances.Count > 0)
            doc.Table(
                [new WColumn(L("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), 3), new WColumn(L("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), 1.6),
                 new WColumn(ReportLabels.WithUnit(L("Остаток", "Калдык", "Balance", "Bakiye", "Qoldiq"), Cur), 1.4, Numeric: true)],
                d.TopBalances.Select(c => new[] { c.Name, c.Phone, ReportFormat.Money(c.Balance) }).ToList());
        else
            doc.Note(ReportLabels.NoData);

        doc.Heading1("4. " + L("Топ-10 клиентов по покупкам за период", "Мезгилдеги сатып алуулар боюнча топ-10 кардар", "Top 10 customers by purchases in the period", "Dönemdeki alışverişe göre ilk 10 müşteri", "Davrdagi xaridlar bo'yicha top-10 mijoz"));
        doc.Note(L("Траты именно бонусов по клиентам сервер не отдаёт — здесь суммы покупок.", "Кардарлар боюнча так бонус сарптоону сервер бербейт — бул жерде сатып алуу суммалары.", "The server does not give bonus spending by customer — these are purchase totals.", "Sunucu müşteri başına bonus harcamasını vermiyor — burada alışveriş toplamları var.", "Server mijozlar bo'yicha bonus sarfini bermaydi — bu yerda xarid summalari."));
        if (d.TopBuyers.Count > 0)
            doc.Table(
                [new WColumn(L("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), 3), new WColumn(L("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), 1, Numeric: true),
                 new WColumn(ReportLabels.WithUnit(L("Покупки", "Сатып алуулар", "Purchases", "Alışveriş", "Xaridlar"), Cur), 1.4, Numeric: true)],
                d.TopBuyers.Select(c => new[] { c.Name, ReportFormat.Integer(c.Receipts), ReportFormat.Money(c.Spent) }).ToList());
        else
            doc.Note(ReportLabels.NoData);

        doc.Save();
    }
}
