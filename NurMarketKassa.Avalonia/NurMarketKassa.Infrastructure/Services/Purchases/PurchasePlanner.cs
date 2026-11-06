namespace NurMarketKassa.Services;

// 2026-10-22 (закупки как в вебе): «Рекомендуемый заказ» сайта NurCRM — «Анализ остатков и продаж поставщика перед созданием прихода».
// Формула сайта: Рекомендация = продажи в день × (запас до закупки + страховой запас) − текущий остаток. Её считает сервер NurCRM
// (GET suppliers/{id}/recommendations/, SupplierApiService.GetRecommendationsAsync — проверено чтением на NBS 22.10); этот расчёт — запасной,
// когда сервер не ответил или старый: по истории продаж этого ПК и приходам сервера.

/// <summary>Настройки расчёта: период анализа (дней продаж), запас до следующей закупки и страховой запас (дней) — как на сайте.</summary>
// 2026-10-24 (аудит, круг 8, С-10): период по умолчанию 30 дней — как в «Пополнении» (было 7).
public sealed record PlannerSettings(int PeriodDays = 30, int CoverDays = 14, int SafetyDays = 3);

/// <summary>Товар поставщика для расчёта: остаток и цена закупки — свежие (каталог кассы или сервер).</summary>
public sealed record PlannerProduct(string Id, string Name, string Barcode, string Code, string Unit, double Stock, double PurchasePrice, bool IsWeighted);

/// <summary>Продажа товара (строка чека): когда (UTC) и сколько.</summary>
public sealed record PlannerSale(string ProductId, double Qty, DateTime SoldAtUtc);

public enum PlannerStatus
{
    /// <summary>Продаж за период нет — рекомендации нет.</summary>
    NoSales,
    /// <summary>Хватает дольше, чем «запас до закупки + страховой».</summary>
    Enough,
    /// <summary>Низкий остаток: хватит меньше, чем «запас до закупки + страховой».</summary>
    Low,
    /// <summary>Скоро закончится: хватит не дольше «запаса до закупки».</summary>
    Soon,
    /// <summary>Критический остаток: хватит не дольше страхового запаса.</summary>
    Critical,
    /// <summary>Нет в наличии (остаток 0 или меньше), а товар продаётся.</summary>
    OutOfStock,
}

/// <summary>Строка рекомендуемого заказа. DaysLeft — на сколько дней хватит (null — продаж нет). TrendPercent — продажи периода против
/// прошлого такого же периода (+250 %); NewSales — в прошлом периоде продаж не было, сейчас есть.</summary>
public sealed record PlannerRow(
    PlannerProduct Product, double SalesPeriod, double Sales7, double Sales30, double PerDay, double? DaysLeft,
    DateTime? LastPurchaseAt, double LastPurchaseQty, PlannerStatus Status, double Recommended, double? TrendPercent, bool NewSales)
{
    public bool NeedsPurchase => Recommended > 0;
    public bool IsCritical => Status is PlannerStatus.Critical or PlannerStatus.OutOfStock;
}

/// <summary>2026-11-06 (В12, ЖП Н-5): когда товару ставить «Нет продаж». Живая приёмка: у товара «продано 0 / 11» (7 / 30 дней) стоял статус «Нет продаж» — средняя за выбранный
/// период (7 дней) была 0. Теперь «нет продаж» — только если не продавалось и за 30 дней.</summary>
public static class PlannerStatusRules
{
    public static bool HasNoSales(double perDayInPeriod, double sold30) => perDayInPeriod <= 0 && sold30 <= 0;

    /// <summary>Средняя в день для оценки запаса: за период, а если там 0 — за 30 дней. 0 — продаж нет совсем.</summary>
    public static double EffectivePerDay(double perDayInPeriod, double sold30) =>
        perDayInPeriod > 0 ? perDayInPeriod : sold30 > 0 ? sold30 / 30.0 : 0;
}

public static class PurchasePlanner
{
    /// <summary>Строки рекомендуемого заказа по товарам поставщика. <paramref name="historyStartUtc"/> — с какого момента на ПК есть
    /// история продаж (новая касса): период короче истории — средняя считается по реальным дням, а не делится на пустые.</summary>
    public static List<PlannerRow> Build(IReadOnlyList<PlannerProduct> products, IReadOnlyList<PlannerSale> sales,
        IReadOnlyDictionary<string, (DateTime At, double Qty)> lastPurchases, PlannerSettings settings, DateTime nowLocal, DateTime? historyStartUtc,
        PlannerV2? v2 = null)
    {
        var period = Math.Clamp(settings.PeriodDays, 1, 365);
        var cover = Math.Clamp(settings.CoverDays, 0, 365);
        var safety = Math.Clamp(settings.SafetyDays, 0, 365);
        var today = nowLocal.Date;
        DateTime From(int days) => today.AddDays(-(days - 1)).ToUniversalTime();
        var fromPeriod = From(period);
        var fromPrev = From(period * 2);
        var from7 = From(7);
        var from30 = From(30);

        // Дней истории в периоде: касса стоит меньше периода — делим на то, что есть (не меньше 1 дня).
        var periodDays = (double)period;
        if (historyStartUtc is { } start && start > fromPeriod)
            periodDays = Math.Max(1, Math.Ceiling((nowLocal.ToUniversalTime() - start).TotalDays));
        var prevKnown = historyStartUtc is null || historyStartUtc <= fromPrev;

        var byProduct = sales.GroupBy(s => s.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // 2026-11-05 (волна 2-А, функция 3): автозаказ v2 — всё по настройке; v2 == null или всё выключено — расчёт ровно как раньше.
        var o = v2?.Options ?? new PlannerV2Options();
        var weights = o.Weekday ? PlannerV2Math.WeekdayWeights(sales, nowLocal, 56, historyStartUtc) : null;
        var lead = o.LeadTime ? Math.Max(0, v2?.LeadDays ?? 0) : 0;
        var receiptsBy = o.LostDemand && v2 is not null
            ? v2.Receipts.GroupBy(r => r.ProductId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (IReadOnlyList<PlannerReceipt>)g.ToList(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyList<PlannerReceipt>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PlannerRow>();
        foreach (var p in products)
        {
            var list = byProduct.TryGetValue(p.Id, out var l) ? l : new List<PlannerSale>();
            double Sum(DateTime from, DateTime? to = null) => list.Where(s => s.SoldAtUtc >= from && (to is null || s.SoldAtUtc < to)).Sum(s => s.Qty);
            var salesPeriod = Sum(fromPeriod);
            var salesPrev = Sum(fromPrev, fromPeriod);
            var perDay = salesPeriod / periodDays;
            if (o.LostDemand && salesPeriod > 0)
            {
                // Дни «без остатка» не считаем: средняя берётся по дням, когда товар мог продаваться (не меньше трети периода).
                var out0 = PlannerV2Math.StockoutDays(p.Stock, list, receiptsBy.GetValueOrDefault(p.Id) ?? Array.Empty<PlannerReceipt>(), nowLocal, period, historyStartUtc);
                if (out0 > 0)
                    // 2026-11-05 (ревью волн 0–3, С3): остаток восстанавливается только по продажам и приходам этой кассы, «пустых» дней может выйти слишком много —
                    // поправка не поднимает среднюю за день больше чем в 1,5 раза.
                    perDay = Math.Min(salesPeriod / Math.Max(periodDays - out0, Math.Max(1.0, periodDays / 3)), perDay * PlannerV2Math.LostDemandMaxBoost);
            }
            if (o.Tsb && salesPeriod > 0)
            {
                var series = PlannerV2Math.DailySeries(list, nowLocal, Math.Min(60, period * 2), historyStartUtc);
                if (series.Length >= 14 && (double)series.Count(x => x > 0) / series.Length < PlannerV2Math.RareShare)
                    perDay = PlannerV2Math.Tsb(series);
            }
            var safetyEff = o.XyzSafety && v2 is not null && v2.Classes.TryGetValue(p.Id, out var cls) ? safety * AbcXyz.SafetyMultiplier(cls) : safety;
            var horizon = cover + lead + safetyEff;
            double? daysLeft = perDay > 0 ? Math.Max(0, p.Stock) / perDay : null;
            var need = perDay * PlannerV2Math.HorizonDays(weights, nowLocal, horizon) - Math.Max(0, p.Stock);
            var recommended = need > 0.0005 ? RoundUp(need, p.IsWeighted) : 0;
            if (o.PackRules && v2 is not null && recommended > 0)
                recommended = PlannerV2Math.ApplyRule(recommended, v2.Rules.GetValueOrDefault(p.Id), p.IsWeighted);

            // 2026-11-06 (В12, ЖП Н-5): «Нет продаж» — только если не продавалось и за 30 дней; при нуле за выбранный период, но продажах за 30 дней запас оцениваем по средней за 30 дней
            // (число «Рекомендуется» и колонка «хватит на» не меняются — меняется только статус).
            var sold30Status = Sum(from30);
            var statusPerDay = PlannerStatusRules.EffectivePerDay(perDay, sold30Status);
            var statusDays = daysLeft ?? (statusPerDay > 0 ? Math.Max(0, p.Stock) / statusPerDay : (double?)null);
            PlannerStatus status;
            if (PlannerStatusRules.HasNoSales(perDay, sold30Status))
                status = p.Stock <= 0 && lastPurchases.ContainsKey(p.Id) ? PlannerStatus.OutOfStock : PlannerStatus.NoSales;
            else if (p.Stock <= 0)
                status = PlannerStatus.OutOfStock;
            else if (statusDays <= safetyEff)
                status = PlannerStatus.Critical;
            else if (statusDays <= cover + lead)
                status = PlannerStatus.Soon;
            else if (statusDays < horizon)
                status = PlannerStatus.Low;
            else
                status = PlannerStatus.Enough;

            double? trend = null;
            var newSales = false;
            if (prevKnown && salesPeriod > 0)
            {
                if (salesPrev > 0)
                    trend = Math.Round((salesPeriod - salesPrev) / salesPrev * 100);
                else
                    newSales = true;
            }

            var last = lastPurchases.TryGetValue(p.Id, out var lp) ? lp : default;
            rows.Add(new PlannerRow(p, salesPeriod, Sum(from7), Sum(from30), perDay, daysLeft,
                last.At == default ? null : last.At, last.Qty, status, recommended, trend, newSales));
        }

        return Order(rows);
    }

    /// <summary>Сначала то, что кончается раньше всего; товары без продаж — в конце, по названию (и для строк, посчитанных сервером).</summary>
    public static List<PlannerRow> Order(IEnumerable<PlannerRow> rows) =>
        rows.OrderBy(r => r.Status is PlannerStatus.NoSales ? 1 : 0)
            .ThenBy(r => r.Status == PlannerStatus.OutOfStock ? 0 : 1)
            .ThenBy(r => r.DaysLeft ?? double.MaxValue)
            .ThenBy(r => r.Product.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Штучный товар — до целого вверх; весовой — до 0,1 вверх.</summary>
    public static double RoundUp(double qty, bool weighted) =>
        weighted ? Math.Ceiling(Math.Round(qty * 10, 6)) / 10 : Math.Ceiling(Math.Round(qty, 6));
}
