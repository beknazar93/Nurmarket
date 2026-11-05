namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 3): автозаказ v2. Всё включается настройкой (ShopAutomationSettings), по умолчанию расчёт как раньше.
// Идеи: Odoo reordering rules + lead times, ERPNext auto material request, методы Кростона/TSB для редких товаров (statsforecast) — формулы свои, на C#.
//  • потерянный спрос: дни, когда товара не было на полке, не считаются в среднюю за день (иначе заказ занижается);
//  • кратность упаковки и минимальная партия: рекомендация округляется вверх до упаковки и не меньше минимальной;
//  • срок поставки поставщика (дней) добавляется к горизонту закупки;
//  • редкие товары (продажи реже чем в трети дней): прогноз TSB — вероятность спроса × размер;
//  • день недели: спрос на ближайшие дни берётся с весами по дням недели магазина;
//  • класс ABC×XYZ меняет страховой запас (AX — выше, CZ — без запаса).

/// <summary>Что включено в расчёте v2.</summary>
public sealed record PlannerV2Options(bool LostDemand = false, bool PackRules = false, bool Weekday = false, bool Tsb = false, bool LeadTime = false, bool XyzSafety = false)
{
    public bool Any => LostDemand || PackRules || Weekday || Tsb || LeadTime || XyzSafety;

    /// <summary>2026-11-05 (ревью волн 0–3, С2): эти четыре функции работают только в расчёте кассы (по продажам этого ПК); серверная рекомендация их не знает.
    /// Упаковка, минимальная партия и срок поставки действуют и на серверные строки.</summary>
    public bool NeedsLocalCalc => LostDemand || Weekday || Tsb || XyzSafety;

    public static PlannerV2Options From(ShopAutomationData d) =>
        new(d.PlanLostDemand, d.PlanPackRules, d.PlanWeekday, d.PlanTsb, d.PlanLeadTime, d.PlanXyzSafety);
}

/// <summary>Приход товара (для восстановления остатка по дням назад).</summary>
public sealed record PlannerReceipt(string ProductId, double Qty, DateTime AtUtc);

/// <summary>Кратность упаковки и минимальная партия по товару (0 — не задано).</summary>
public sealed record PlannerOrderRule(double Pack, double MinQty);

/// <summary>Всё, что нужно расчёту v2 сверх обычного: настройки, срок поставки, приходы, правила заказа и классы ABC×XYZ («AX»…).</summary>
public sealed record PlannerV2(PlannerV2Options Options, int LeadDays, IReadOnlyList<PlannerReceipt> Receipts,
    IReadOnlyDictionary<string, PlannerOrderRule> Rules, IReadOnlyDictionary<string, string> Classes);

public static class PlannerV2Math
{
    /// <summary>Доля дней с продажами, ниже которой товар считается редким (прогноз TSB).</summary>
    public const double RareShare = 0.30;

    /// <summary>2026-11-05 (ревью волн 0–3, С3): «дни без товара» поднимают среднюю за день не больше чем в столько раз.</summary>
    public const double LostDemandMaxBoost = 1.5;

    /// <summary>Сколько дней из последних <paramref name="periodDays"/> товара не было на полке. Остаток по дням восстанавливается назад от нынешнего:
    /// остаток на конец дня = сейчас + проданное позже − принятое позже. День считается «без остатка», если на его конец остаток ≤ 0 и продаж в этот день не было.
    /// Списания и перемещения в расчёте не видны — но продаж в такой день всё равно нет, а ошибка идёт в сторону «чуть лишних пустых дней».</summary>
    public static int StockoutDays(double stockNow, IReadOnlyList<PlannerSale> sales, IReadOnlyList<PlannerReceipt> receipts, DateTime todayLocal, int periodDays, DateTime? historyStartUtc)
    {
        var today = todayLocal.Date;
        var soldByDay = new Dictionary<DateTime, double>();
        foreach (var s in sales)
        {
            var d = s.SoldAtUtc.ToLocalTime().Date;
            soldByDay[d] = soldByDay.GetValueOrDefault(d) + s.Qty;
        }
        var receivedByDay = new Dictionary<DateTime, double>();
        foreach (var r in receipts)
        {
            var d = r.AtUtc.ToLocalTime().Date;
            receivedByDay[d] = receivedByDay.GetValueOrDefault(d) + r.Qty;
        }
        var firstDay = historyStartUtc is { } hs ? hs.ToLocalTime().Date : today.AddDays(-(periodDays - 1));
        var count = 0;
        var stock = stockNow; // остаток на конец дня d: идём от сегодня назад
        for (var back = 0; back < periodDays; back++)
        {
            var day = today.AddDays(-back);
            if (day < firstDay)
                break;
            var sold = soldByDay.GetValueOrDefault(day);
            if (stock <= 0.0005 && sold <= 0.0005)
                count++;
            // переходим к концу предыдущего дня: возвращаем проданное в этот день и убираем принятое в этот день
            stock += sold - receivedByDay.GetValueOrDefault(day);
        }
        return count;
    }

    /// <summary>Веса дней недели (индекс — DayOfWeek, среднее = 1) по продажам последних дней магазина. Мало истории (меньше 14 дней или 20 продаж) — все веса 1.</summary>
    public static double[] WeekdayWeights(IEnumerable<PlannerSale> sales, DateTime todayLocal, int days = 56, DateTime? historyStartUtc = null)
    {
        var weights = Enumerable.Repeat(1.0, 7).ToArray();
        var today = todayLocal.Date;
        var from = today.AddDays(-(days - 1));
        if (historyStartUtc is { } hs && hs.ToLocalTime().Date > from)
            from = hs.ToLocalTime().Date;
        var span = (today - from).Days + 1;
        var list = sales.Where(s => s.SoldAtUtc.ToLocalTime().Date >= from).ToList();
        if (span < 14 || list.Count < 20)
            return weights;
        var total = new double[7];
        var daysOf = new int[7];
        for (var d = from; d <= today; d = d.AddDays(1))
            daysOf[(int)d.DayOfWeek]++;
        foreach (var s in list)
            total[(int)s.SoldAtUtc.ToLocalTime().DayOfWeek] += s.Qty;
        var overall = total.Sum() / span;
        if (overall <= 0)
            return weights;
        for (var i = 0; i < 7; i++)
            weights[i] = daysOf[i] > 0 ? Math.Clamp(total[i] / daysOf[i] / overall, 0.4, 2.5) : 1.0;
        var mean = weights.Average();
        for (var i = 0; i < 7; i++)
            weights[i] /= mean;
        return weights;
    }

    /// <summary>Сколько «средних дней» спроса в ближайшие <paramref name="horizonDays"/> дней (со следующего дня), с весами дней недели. Без весов — просто число дней.</summary>
    public static double HorizonDays(double[]? weekdayWeights, DateTime todayLocal, double horizonDays)
    {
        if (horizonDays <= 0)
            return 0;
        if (weekdayWeights is null)
            return horizonDays;
        var whole = (int)Math.Floor(horizonDays);
        var sum = 0.0;
        for (var i = 1; i <= whole; i++)
            sum += weekdayWeights[(int)todayLocal.Date.AddDays(i).DayOfWeek];
        var frac = horizonDays - whole;
        if (frac > 0)
            sum += frac * weekdayWeights[(int)todayLocal.Date.AddDays(whole + 1).DayOfWeek];
        return sum;
    }

    /// <summary>Прогноз спроса в день по TSB (Teunter–Syntetos–Babai): вероятность спроса p и размер z обновляются экспоненциально, прогноз = p × z.
    /// Ряд — продажи по дням от старых к новым.</summary>
    public static double Tsb(IReadOnlyList<double> daily, double alpha = 0.2, double beta = 0.1)
    {
        if (daily.Count == 0)
            return 0;
        var positive = daily.Where(x => x > 0).ToList();
        if (positive.Count == 0)
            return 0;
        var p = (double)positive.Count / daily.Count;
        var z = positive.Average();
        foreach (var x in daily)
        {
            p += beta * ((x > 0 ? 1.0 : 0.0) - p);
            if (x > 0)
                z += alpha * (x - z);
        }
        return p * z;
    }

    /// <summary>Продажи по дням за последние <paramref name="days"/> дней (от старых к новым), не раньше начала истории.</summary>
    public static double[] DailySeries(IEnumerable<PlannerSale> sales, DateTime todayLocal, int days, DateTime? historyStartUtc)
    {
        var today = todayLocal.Date;
        var from = today.AddDays(-(days - 1));
        if (historyStartUtc is { } hs && hs.ToLocalTime().Date > from)
            from = hs.ToLocalTime().Date;
        var n = Math.Max(1, (today - from).Days + 1);
        var series = new double[n];
        foreach (var s in sales)
        {
            var d = s.SoldAtUtc.ToLocalTime().Date;
            if (d >= from && d <= today)
                series[(d - from).Days] += s.Qty;
        }
        return series;
    }

    /// <summary>Рекомендованное количество → не меньше минимальной партии и кратно упаковке (вверх). 0 — заказывать нечего.</summary>
    public static double ApplyRule(double qty, PlannerOrderRule? rule, bool weighted)
    {
        if (qty <= 0)
            return 0;
        if (rule is null)
            return qty;
        if (rule.MinQty > 0 && qty < rule.MinQty)
            qty = rule.MinQty;
        if (rule.Pack > 0)
            qty = Math.Ceiling(Math.Round(qty / rule.Pack, 6)) * rule.Pack;
        return weighted ? Math.Round(qty, 3) : Math.Ceiling(Math.Round(qty, 6));
    }

    /// <summary>Правила кратности/минимума к готовым строкам (в том числе посчитанным сервером). Статус и остальное не трогаются.</summary>
    public static List<PlannerRow> ApplyOrderRules(IEnumerable<PlannerRow> rows, IReadOnlyDictionary<string, PlannerOrderRule> rules) =>
        rows.Select(r => rules.TryGetValue(r.Product.Id, out var rule) && r.Recommended > 0
            ? r with { Recommended = ApplyRule(r.Recommended, rule, r.Product.IsWeighted) }
            : r).ToList();

    /// <summary>Правила из настроек для товаров поставщика.</summary>
    public static Dictionary<string, PlannerOrderRule> RulesFrom(ShopAutomationData d) =>
        d.Products.Where(p => p.Value.Pack > 0 || p.Value.MinQty > 0)
            .ToDictionary(p => p.Key, p => new PlannerOrderRule(p.Value.Pack, p.Value.MinQty), StringComparer.OrdinalIgnoreCase);

    /// <summary>Срок поставки поставщика из настроек (0 — не задан).</summary>
    public static int LeadDaysOf(ShopAutomationData d, string? supplierId) =>
        !string.IsNullOrEmpty(supplierId) && d.Suppliers.TryGetValue(supplierId, out var s) ? s.LeadDays : 0;
}
