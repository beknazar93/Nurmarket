using System.Globalization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи». Чистый расчёт без сети и без окна (его
/// проверяет стенд salary-schemes). Все суммы — decimal, округление «от нуля» (MidpointRounding.AwayFromZero).
///
/// Формулы:
///   чистая маржа      = валовая прибыль сервера − возвраты (если включено «вычесть отдельно») − скидки/бонусы вне выручки;
///   чистая прибыль    = «Чистая прибыль» сервера (operating_profit, как в «Прибыли и деньгах») − возвраты − скидки
///                       + расходы тех статей, что владелец отключил для расчёта − (закупки из ящика, если включены);
///   по своим продажам = результат магазина × (продажи сотрудника / выручка магазина), доля не больше 100 %;
///   начисление        = процент от базы; база ≤ 0 → 0 с пометкой (зарплата не бывает отрицательной);
///   порог             = база меньше порога → 0; «только сверх порога» — процент с (база − порог);
///   прогрессивная     = «до предела — a %, свыше — b %» по частям базы;
///   пул               = процент по правилу пула от результата магазина, делится по продажам / часам / поровну; остаток от
///                       округления раздаётся методом наибольших остатков — сумма долей = пулу после округления.</summary>
public static class ProfitPayCalculator
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string M(decimal v) => v.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private static string P(decimal v) => v.ToString("0.##", Ru) + " %";

    public static decimal RoundMoney(decimal v, ProfitPayRounding mode) =>
        mode == ProfitPayRounding.Som ? Math.Round(v, 0, MidpointRounding.AwayFromZero) : Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Расчёт по всем сотрудникам с активной схемой.</summary>
    public static ProfitPayResult Compute(ProfitPayShopSettings shop, IReadOnlyList<ProfitPayScheme> schemes, ProfitPayFigures f, IReadOnlyList<ProfitPayEmployee> employees)
    {
        var res = new ProfitPayResult { Revenue = f.Revenue, Cogs = f.Cogs, Gross = f.Gross };

        // ── цифры магазина ──
        var returns = shop.SubtractReturns ? Math.Max(0, f.Returns) : 0m;
        var discounts = Math.Max(0, f.Discounts);
        res.Returns = returns;
        res.Discounts = discounts;
        res.Margin = f.Gross - returns - discounts;
        res.ShopSteps.Add(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum") + " " + M(f.Revenue) + " − "
            + Tr.T("закупочная цена проданного", "сатылгандын сатып алуу баасы", "purchase cost of goods sold", "satılanların alış fiyatı", "sotilganlarning xarid narxi") + " " + M(f.Cogs)
            + " = " + Tr.T("валовая прибыль", "дүң пайда", "gross profit", "brüt kâr", "yalpi foyda") + " " + M(f.Gross));
        if (returns > 0)
            res.ShopSteps.Add(Tr.T("Возвраты покупателям", "Сатып алуучуларга кайтаруу", "Customer refunds", "Müşteri iadeleri", "Xaridorlarga qaytarish") + " −" + M(returns));
        else if (f.Returns > 0)
            res.ShopSteps.Add(Tr.T("Возвраты " + M(f.Returns) + " уже учтены в выручке сервера (как в «Прибыли и деньгах») и второй раз не вычитаются.",
                "Кайтаруулар " + M(f.Returns) + " сервердин түшүмүндө эсепке алынган («Пайда жана акча» сыяктуу), экинчи жолу алынбайт.",
                "Refunds " + M(f.Returns) + " are already in the server revenue (as in “Profit & cash”) and are not subtracted twice.",
                "İadeler " + M(f.Returns) + " sunucu cirosunda zaten var («Kâr ve nakit» gibi), ikinci kez düşülmez.",
                "Qaytarishlar " + M(f.Returns) + " server tushumida hisobga olingan («Foyda va pul» kabi), ikkinchi marta ayirilmaydi."));
        if (discounts > 0)
            res.ShopSteps.Add(Tr.T("Скидки и бонусы вне выручки", "Түшүмгө кирбеген арзандатуу жана бонустар", "Discounts and bonuses outside revenue", "Ciro dışındaki indirim ve bonuslar", "Tushumga kirmagan chegirma va bonuslar") + " −" + M(discounts));
        res.ShopSteps.Add(Tr.T("Чистая маржа магазина", "Дүкөндүн таза маржасы", "Shop net margin", "Mağazanın net marjı", "Do'konning sof marjasi") + " = " + M(res.Margin));

        decimal allLines = f.Expenses.Values.Sum();
        var netAll = f.ServerNet ?? (f.Gross - allLines);
        var net = netAll - returns - discounts;
        foreach (var key in new[] { "salaries", "rent", "taxes", "utilities", "other" })
        {
            var amount = f.Expenses.TryGetValue(key, out var a) ? a : 0m;
            if (amount <= 0)
                continue;
            var title = ProfitReport.ExpenseTitle(key);
            if (shop.Subtracts(key))
                res.ExpensesSubtracted.Add((title, amount));
            else
            {
                res.ExpensesSkipped.Add((title, amount));
                net += amount; // отключённая статья не уменьшает прибыль
            }
        }
        if (shop.SubtractSupplierCash && f.SupplierCash > 0)
        {
            var title = Tr.T("Закупки из кассы", "Кассадан сатып алуулар", "Purchases from the till", "Kasadan alımlar", "Kassadan xaridlar");
            res.ExpensesSubtracted.Add((title, f.SupplierCash));
            net -= f.SupplierCash;
        }
        res.NetProfit = net;
        res.ShopSteps.Add(Tr.T("Чистая прибыль магазина", "Дүкөндүн таза пайдасы", "Shop net profit", "Mağazanın net kârı", "Do'konning sof foyda") + " = " + M(netAll)
            + (returns > 0 || discounts > 0 ? " − " + M(returns + discounts) : "")
            + (res.ExpensesSkipped.Count > 0 ? " + " + M(res.ExpensesSkipped.Sum(x => x.Amount)) + " (" + Tr.T("не вычитаются", "алынбайт", "not subtracted", "düşülmez", "ayirilmaydi") + ": " + string.Join(", ", res.ExpensesSkipped.Select(x => x.Title)) + ")" : "")
            + (shop.SubtractSupplierCash && f.SupplierCash > 0 ? " − " + M(f.SupplierCash) : "")
            + " = " + M(net));

        var active = schemes.Where(s => s.Active).ToList();
        var byId = employees.ToDictionary(e => e.UserId, e => e, StringComparer.OrdinalIgnoreCase);

        // ── обычные схемы (по своим продажам / по общему результату) ──
        foreach (var s in active.Where(s => s.Scope != ProfitPayScope.Pool))
        {
            byId.TryGetValue(s.UserId, out var emp);
            var name = emp?.Name is { Length: > 0 } n ? n : s.Name;
            var line = new ProfitPayLine { UserId = s.UserId, Name = name, Base = s.Rule.Base, Scope = s.Scope };
            var shopBase = s.Rule.Base == ProfitPayBase.Margin ? res.Margin : res.NetProfit;
            var baseName = BaseName(s.Rule.Base);
            decimal baseValue;
            if (s.Scope == ProfitPayScope.Own)
            {
                var sales = emp?.Sales ?? 0m;
                var ratio = f.Revenue > 0 ? Math.Min(1m, Math.Max(0m, sales / f.Revenue)) : 0m;
                baseValue = shopBase * ratio;
                line.Steps.Add(Tr.T("По своим продажам", "Өз сатуулары боюнча", "By own sales", "Kendi satışlarına göre", "O'z sotuvlari bo'yicha") + ": "
                    + Tr.T("продажи сотрудника", "кызматкердин сатуулары", "employee sales", "çalışanın satışları", "xodim sotuvlari") + " " + M(sales) + " / "
                    + Tr.T("выручка магазина", "дүкөндүн түшүмү", "shop revenue", "mağaza cirosu", "do'kon tushumi") + " " + M(f.Revenue) + " = " + (ratio * 100m).ToString("0.##", Ru) + " %");
                line.Steps.Add(baseName + " " + Tr.T("магазина", "дүкөндүн", "of the shop", "mağazanın", "do'konning") + " " + M(shopBase) + " × " + (ratio * 100m).ToString("0.##", Ru) + " % = " + M(baseValue));
                if (f.Revenue <= 0)
                    line.Steps.Add(Tr.T("Выручки за период нет — доля 0.", "Мезгилде түшүм жок — үлүш 0.", "No revenue in the period — share 0.", "Dönemde ciro yok — pay 0.", "Davrda tushum yo'q — ulush 0."));
            }
            else
            {
                baseValue = shopBase;
                line.Steps.Add(Tr.T("По общему результату магазина", "Дүкөндүн жалпы жыйынтыгы боюнча", "By the shop's overall result", "Mağazanın genel sonucuna göre", "Do'konning umumiy natijasi bo'yicha") + ": "
                    + baseName + " = " + M(baseValue));
            }
            line.BaseValue = baseValue;
            var (amount, note, steps) = ApplyRule(s.Rule, baseValue);
            line.Steps.AddRange(steps);
            line.Note = note;
            line.Zeroed = note.Length > 0 && amount == 0;
            line.Amount = RoundMoney(amount, shop.Rounding);
            if (amount != line.Amount)
                line.Steps.Add(Tr.T("Округление", "Тегеректөө", "Rounding", "Yuvarlama", "Yuvarlash") + ": " + amount.ToString("0.####", Ru) + " → " + M(line.Amount));
            line.Steps.Add(Tr.T("Начислено от прибыли", "Пайдадан эсептелди", "Accrued from profit", "Kârdan tahakkuk", "Foydadan hisoblangan") + " = " + M(line.Amount));
            res.Lines.Add(line);
        }

        // ── пул ──
        var pool = active.Where(s => s.Scope == ProfitPayScope.Pool).ToList();
        if (pool.Count > 0)
            ComputePool(shop, pool, res, byId);
        return res;
    }

    private static void ComputePool(ProfitPayShopSettings shop, List<ProfitPayScheme> pool, ProfitPayResult res, Dictionary<string, ProfitPayEmployee> byId)
    {
        var rule = shop.Pool;
        var baseName = BaseName(rule.Base);
        var shopBase = rule.Base == ProfitPayBase.Margin ? res.Margin : res.NetProfit;
        var (poolRaw, poolNote, poolSteps) = ApplyRule(rule, shopBase);
        var poolAmount = RoundMoney(poolRaw, shop.Rounding);
        res.PoolAmount = poolAmount;

        var weights = new List<decimal>();
        foreach (var s in pool)
        {
            byId.TryGetValue(s.UserId, out var emp);
            weights.Add(shop.Share switch
            {
                ProfitPayShare.Sales => Math.Max(0, emp?.Sales ?? 0m),
                ProfitPayShare.Hours => Math.Max(0, emp?.Hours ?? 0m),
                _ => 1m,
            });
        }
        var totalWeight = weights.Sum();
        var shares = new decimal[pool.Count];
        if (poolAmount > 0 && totalWeight > 0)
        {
            var unit = shop.Rounding == ProfitPayRounding.Som ? 1m : 0.01m;
            var totalUnits = (long)Math.Round(poolAmount / unit, 0, MidpointRounding.AwayFromZero);
            var raw = weights.Select(w => (decimal)totalUnits * w / totalWeight).ToArray();
            var floors = raw.Select(r => (long)Math.Floor(r)).ToArray();
            var left = totalUnits - floors.Sum();
            // метод наибольших остатков; при равных остатках — по порядку в списке (стабильно)
            foreach (var i in Enumerable.Range(0, raw.Length).OrderByDescending(i => raw[i] - floors[i]).ThenBy(i => i).Take((int)left))
                floors[i]++;
            for (var i = 0; i < shares.Length; i++)
                shares[i] = floors[i] * unit;
        }
        res.PoolUndistributed = poolAmount - shares.Sum();

        var shareName = shop.Share switch
        {
            ProfitPayShare.Sales => Tr.T("по доле продаж", "сатуу үлүшү боюнча", "by sales share", "satış payına göre", "sotuv ulushi bo'yicha"),
            ProfitPayShare.Hours => Tr.T("по отработанным часам", "иштелген сааттар боюнча", "by hours worked", "çalışılan saate göre", "ishlangan soat bo'yicha"),
            _ => Tr.T("поровну", "теңдей", "equally", "eşit", "teng"),
        };
        for (var i = 0; i < pool.Count; i++)
        {
            var s = pool[i];
            byId.TryGetValue(s.UserId, out var emp);
            var name = emp?.Name is { Length: > 0 } n ? n : s.Name;
            var line = new ProfitPayLine { UserId = s.UserId, Name = name, Base = rule.Base, Scope = ProfitPayScope.Pool, BaseValue = shopBase };
            line.Steps.Add(Tr.T("Общий пул", "Жалпы пул", "Shared pool", "Ortak havuz", "Umumiy pul") + ": " + baseName + " " + M(shopBase));
            line.Steps.AddRange(poolSteps);
            line.Steps.Add(Tr.T("Пул после округления", "Тегеректөөдөн кийинки пул", "Pool after rounding", "Yuvarlamadan sonraki havuz", "Yaxlitlashdan keyingi pul") + " = " + M(poolAmount)
                + "; " + Tr.T("делится", "бөлүнөт", "split", "bölünür", "bo'linadi") + " " + shareName);
            line.Note = poolNote;
            if (poolAmount <= 0)
            {
                line.Zeroed = true;
            }
            else if (totalWeight <= 0)
            {
                line.Zeroed = true;
                line.Note = Tr.T("Некого делить: у участников нет " + (shop.Share == ProfitPayShare.Hours ? "часов в табеле" : "продаж") + " — пул не распределён.",
                    "Бөлүүгө эч ким жок: катышуучуларда " + (shop.Share == ProfitPayShare.Hours ? "табелде саат" : "сатуу") + " жок — пул бөлүнгөн жок.",
                    "Nobody to split between: participants have no " + (shop.Share == ProfitPayShare.Hours ? "timesheet hours" : "sales") + " — the pool is not distributed.",
                    "Bölünecek kimse yok: katılımcıların " + (shop.Share == ProfitPayShare.Hours ? "puantaj saati" : "satışı") + " yok — havuz dağıtılmadı.",
                    "Bo'linadigan hech kim yo'q: ishtirokchilarda " + (shop.Share == ProfitPayShare.Hours ? "tabelda soat" : "sotuv") + " yo'q — pul taqsimlanmadi.");
                line.Steps.Add(line.Note);
            }
            else
            {
                var w = weights[i];
                var weightText = shop.Share switch
                {
                    ProfitPayShare.Hours => w.ToString("0.##", Ru) + " " + Tr.T("ч", "саат", "h", "sa", "soat") + (emp?.Hours is null ? " (" + Tr.T("в табеле не найден", "табелде табылган жок", "not found in the timesheet", "puantajda bulunamadı", "tabelda topilmadi") + ")" : ""),
                    ProfitPayShare.Sales => M(w),
                    _ => "1",
                };
                var totalText = shop.Share switch
                {
                    ProfitPayShare.Hours => totalWeight.ToString("0.##", Ru) + " " + Tr.T("ч", "саат", "h", "sa", "soat"),
                    ProfitPayShare.Sales => M(totalWeight),
                    _ => totalWeight.ToString("0", Ru),
                };
                line.Steps.Add(Tr.T("Доля", "Үлүш", "Share", "Pay", "Ulush") + ": " + weightText + " / " + totalText + " = " + (w / totalWeight * 100m).ToString("0.##", Ru) + " % → "
                    + M(poolAmount) + " × " + (w / totalWeight * 100m).ToString("0.##", Ru) + " %");
            }
            line.Amount = shares[i];
            line.Steps.Add(Tr.T("Начислено от прибыли", "Пайдадан эсептелди", "Accrued from profit", "Kârdan tahakkuk", "Foydadan hisoblangan") + " = " + M(line.Amount));
            res.Lines.Add(line);
        }
        if (res.PoolUndistributed != 0)
            res.ShopSteps.Add(Tr.T("Пул не распределён", "Пул бөлүнгөн жок", "Pool not distributed", "Havuz dağıtılmadı", "Pul taqsimlanmadi") + ": " + M(res.PoolUndistributed));
    }

    private static string BaseName(ProfitPayBase b) => b == ProfitPayBase.Margin
        ? Tr.T("Чистая маржа", "Таза маржа", "Net margin", "Net marj", "Sof marja")
        : Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda");

    /// <summary>Процент / порог / прогрессивная шкала от базы. Возвращает начисление до округления, пометку (если обнулено) и шаги.</summary>
    public static (decimal Amount, string Note, List<string> Steps) ApplyRule(ProfitPayRule rule, decimal baseValue)
    {
        var steps = new List<string>();
        if (baseValue <= 0)
        {
            var note = Tr.T("Прибыль за период нулевая или отрицательная (" + M(baseValue) + ") — начисление 0, зарплата не уходит в минус.",
                "Мезгилдеги пайда нөл же терс (" + M(baseValue) + ") — эсептөө 0, эмгек акы минуска кетпейт.",
                "Profit for the period is zero or negative (" + M(baseValue) + ") — accrual 0, pay never goes negative.",
                "Dönem kârı sıfır veya negatif (" + M(baseValue) + ") — tahakkuk 0, maaş eksiye düşmez.",
                "Davr foydasi nol yoki manfiy (" + M(baseValue) + ") — hisob 0, ish haqi manfiyga tushmaydi.");
            steps.Add(note);
            return (0m, note, steps);
        }
        var applicable = baseValue;
        if (rule.MinProfit > 0)
        {
            if (baseValue < rule.MinProfit)
            {
                var note = Tr.T("База " + M(baseValue) + " ниже порога " + M(rule.MinProfit) + " — начисление 0.",
                    "База " + M(baseValue) + " босогодон " + M(rule.MinProfit) + " төмөн — эсептөө 0.",
                    "Base " + M(baseValue) + " is below the threshold " + M(rule.MinProfit) + " — accrual 0.",
                    "Taban " + M(baseValue) + " eşiğin " + M(rule.MinProfit) + " altında — tahakkuk 0.",
                    "Baza " + M(baseValue) + " chegaradan " + M(rule.MinProfit) + " past — hisob 0.");
                steps.Add(note);
                return (0m, note, steps);
            }
            if (rule.OnlyExcess)
            {
                applicable = baseValue - rule.MinProfit;
                steps.Add(Tr.T("Считается только сверх порога", "Босогодон ашканы гана эсептелет", "Counted only above the threshold", "Yalnızca eşiğin üstü hesaplanır", "Faqat chegaradan oshgani hisoblanadi") + ": "
                    + M(baseValue) + " − " + M(rule.MinProfit) + " = " + M(applicable));
            }
            else
            {
                steps.Add(Tr.T("Порог " + M(rule.MinProfit) + " пройден — процент со всей базы.", "Босого " + M(rule.MinProfit) + " өттү — бүт базадан пайыз.",
                    "Threshold " + M(rule.MinProfit) + " reached — percentage of the whole base.", "Eşik " + M(rule.MinProfit) + " aşıldı — tüm tabandan yüzde.",
                    "Chegara " + M(rule.MinProfit) + " o'tildi — butun bazadan foiz."));
            }
        }
        decimal amount;
        if (rule.Progressive)
        {
            var limit = Math.Max(0, rule.Limit);
            var first = Math.Min(applicable, limit);
            var above = Math.Max(0, applicable - limit);
            var a1 = first * rule.Percent / 100m;
            var a2 = above * rule.PercentAbove / 100m;
            steps.Add(Tr.T("До ", "Чейин ", "Up to ", "Şuna kadar ", "Gacha ") + M(limit) + ": " + M(first) + " × " + P(rule.Percent) + " = " + M(a1));
            steps.Add(Tr.T("Свыше ", "Андан ашканы ", "Above ", "Üstü ", "Undan ortig'i ") + M(limit) + ": " + M(above) + " × " + P(rule.PercentAbove) + " = " + M(a2));
            amount = a1 + a2;
        }
        else
        {
            amount = applicable * rule.Percent / 100m;
            steps.Add(M(applicable) + " × " + P(rule.Percent) + " = " + M(amount));
        }
        return (amount, "", steps);
    }

    /// <summary>Часы по табелю внутри периода: берутся ЗАКРЫТЫЕ смены (как в «Табеле»), каждая обрезается по границам периода
    /// [from 00:00; to+1 день 00:00), поэтому смена через полночь на краю периода учитывается только своей частью внутри него.
    /// Ключ — имя кассира без регистра и лишних пробелов.</summary>
    public static Dictionary<string, decimal> HoursInPeriod(IEnumerable<ShiftHistoryEntry> shifts, DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date.AddDays(1);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shifts)
        {
            if (s.OpenedAt is not { } o || s.ClosedAt is not { } c || c <= o)
                continue;
            var a = o > start ? o : start;
            var b = c < end ? c : end;
            if (b <= a)
                continue;
            var key = NameKey(s.Cashier);
            var hours = (decimal)(b - a).TotalHours;
            result[key] = (result.TryGetValue(key, out var old) ? old : 0m) + hours;
        }
        return result.ToDictionary(k => k.Key, k => Math.Round(k.Value, 2, MidpointRounding.AwayFromZero), StringComparer.OrdinalIgnoreCase);
    }

    public static string NameKey(string? name) => string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
