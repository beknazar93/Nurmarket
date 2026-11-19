using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (пакет Л-13 «Отчёты владельца»): чистые расчёты без интерфейса и сети — сравнение периодов (неделя к неделе, месяц к месяцу,
/// год к году), стрелки ↑↓ с процентом, отчёт по кассирам, тепловая карта «часы × дни недели». Деньги — decimal.
/// Данные приходят из списка продаж сервера (<see cref="SaleFact"/>), своих полей не придумываем.</summary>
public static class OwnerReportsMath
{
    /// <summary>С чем сравнивать период: с таким же предыдущим или с тем же периодом год назад.</summary>
    public enum CompareBase
    {
        None,
        Previous,
        YearAgo,
    }

    /// <summary>Вид периода: день, 7 дней, месяц с 1-го числа, свои даты.</summary>
    public enum PeriodKind
    {
        Day,
        Week,
        Month,
        Custom,
    }

    /// <summary>Предыдущий период (включительно по дням) для сравнения. Previous: день — вчера; неделя — те же дни на 7 дней раньше; месяц — те же числа
    /// прошлого месяца (31-е не вылезает в следующий месяц); свои даты — такой же по длине отрезок прямо перед ними. YearAgo — те же даты год назад
    /// (29 февраля → 28 февраля; конец периода — той же длины).</summary>
    public static (DateTime From, DateTime To) ComparePeriod(DateTime from, DateTime to, PeriodKind kind, CompareBase basis)
    {
        from = from.Date;
        to = to.Date;
        if (to < from)
            (from, to) = (to, from);
        var days = (to - from).Days;
        if (basis == CompareBase.YearAgo)
        {
            var yFrom = ShiftYear(from, -1);
            return (yFrom, yFrom.AddDays(days));
        }
        switch (kind)
        {
            case PeriodKind.Week:
                return (from.AddDays(-7), from.AddDays(-7 + days));
            case PeriodKind.Month:
            {
                var prevFrom = from.AddMonths(-1);
                var lastDay = DateTime.DaysInMonth(prevFrom.Year, prevFrom.Month) - 1;
                return (prevFrom, prevFrom.AddDays(Math.Min(days, lastDay)));
            }
            case PeriodKind.Custom:
                return (from.AddDays(-(days + 1)), from.AddDays(-1));
            default:
                return (from.AddDays(-1), to.AddDays(-1));
        }
    }

    private static DateTime ShiftYear(DateTime d, int years) => d.AddYears(years); // AddYears сам переносит 29.02 на 28.02

    /// <summary>Изменение в процентах; null — в прошлом периоде нулю (или меньше), сравнивать не с чем.</summary>
    public static decimal? DeltaPercent(decimal current, decimal previous) =>
        previous > 0.005m ? Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Направление изменения: +1 рост, −1 падение, 0 без изменений (меньше 0,5 %), null — сравнить не с чем.</summary>
    public static int? Direction(decimal? percent) => percent is not { } p ? null : Math.Abs(p) < 0.5m ? 0 : p > 0 ? 1 : -1;

    /// <summary>«↑ 12,5 %», «↓ 3 %», «→ 0 %»; null — «—». Число с запятой, как в программе (русская запись), знак — стрелкой.</summary>
    public static string Arrow(decimal? percent)
    {
        if (percent is not { } p)
            return "—";
        var abs = Math.Abs(p);
        var number = abs >= 100m ? abs.ToString("0", Ru) : abs.ToString("0.#", Ru);
        return Direction(p) switch
        {
            1 => "↑ " + number + " %",
            -1 => "↓ " + number + " %",
            _ => "→ 0 %",
        };
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    // ── Продажи ────────────────────────────────────────────────────────────────────

    /// <summary>Чек из списка продаж сервера. Status — как отдаёт сервер (paid, partially_returned, debt, canceled…). Discount — discount_total чека.
    /// Cashier пустой — сервер имя не прислал.</summary>
    public sealed record SaleFact(DateTime At, string Cashier, decimal Total, decimal Discount, string Status);

    /// <summary>Чек возвращён целиком или отменён (как <c>ReceiptHistoryService.IsReturnedOrCanceled</c>).</summary>
    public static bool IsReturn(string? status) => (status ?? "").Trim().ToLowerInvariant() is "canceled" or "cancelled" or "returned" or "refunded";

    /// <summary>Имя кассира для отчёта: пустое — «Не указан».</summary>
    public static string CashierName(string? name) => string.IsNullOrWhiteSpace(name)
        ? Tr.T("Не указан", "Көрсөтүлгөн эмес", "Not specified", "Belirtilmemiş", "Ko'rsatilmagan")
        : name.Trim();

    /// <summary>Итоги по кассиру. Revenue — оплаченные и частично возвращённые чеки (как плитка «Выручка»), Checks — их число, AvgCheck = Revenue / Checks.
    /// Returns/ReturnsSum — чеки, возвращённые целиком или отменённые (сумма чека); PartialReturns — частичные возвраты (суммы возврата список не даёт).
    /// DiscountSum — скидки в чеках выручки. Errors — число «ошибок» кассира из журнала «Удалено из чека» этого ПК (задаётся отдельно, см. <see cref="WithErrors"/>).</summary>
    public sealed record CashierRow(string Cashier, decimal Revenue, int Checks, decimal AvgCheck, int Returns, decimal ReturnsSum, int PartialReturns,
        decimal DiscountSum, int Errors)
    {
        /// <summary>Доля возвратов от всех чеков кассира (0–100); чеков нет — 0.</summary>
        public decimal ReturnsPercent => Checks + Returns > 0 ? Math.Round(Returns * 100m / (Checks + Returns), 1) : 0m;
    }

    /// <summary>По кассирам: по убыванию выручки, при равенстве — по имени. Чеки «в долг» в выручку не входят и в «Чеки» не считаются
    /// (как <c>SalesExportService.CountsAsRevenue</c>).</summary>
    public static IReadOnlyList<CashierRow> ByCashier(IEnumerable<SaleFact> sales)
    {
        var rows = new List<CashierRow>();
        foreach (var g in sales.GroupBy(s => CashierName(s.Cashier), StringComparer.CurrentCultureIgnoreCase))
        {
            decimal revenue = 0, discount = 0, returnsSum = 0;
            int checks = 0, returns = 0, partial = 0;
            foreach (var s in g)
            {
                if (IsReturn(s.Status))
                {
                    returns++;
                    returnsSum += s.Total;
                }
                else if (SalesExportService.CountsAsRevenue(s.Status))
                {
                    checks++;
                    revenue += s.Total;
                    discount += Math.Max(0m, s.Discount);
                    if (string.Equals((s.Status ?? "").Trim(), "partially_returned", StringComparison.OrdinalIgnoreCase))
                        partial++;
                }
            }
            rows.Add(new CashierRow(g.Key, revenue, checks, checks > 0 ? Math.Round(revenue / checks, 2) : 0m, returns, returnsSum, partial, discount, 0));
        }
        return rows.OrderByDescending(r => r.Revenue).ThenBy(r => r.Cashier, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Дописать число ошибок по кассирам (имя сравнивается без учёта регистра). Кассир из журнала, которого нет в продажах, строкой не добавляется.</summary>
    public static IReadOnlyList<CashierRow> WithErrors(IReadOnlyList<CashierRow> rows, IReadOnlyDictionary<string, int> errorsByCashier)
    {
        if (errorsByCashier.Count == 0)
            return rows;
        var map = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var (name, count) in errorsByCashier)
            map[CashierName(name)] = map.GetValueOrDefault(CashierName(name)) + count;
        return rows.Select(r => map.TryGetValue(r.Cashier, out var n) ? r with { Errors = n } : r).ToList();
    }

    /// <summary>Итоги по всем кассирам одной строкой (для плиток и строки «Итого»).</summary>
    public static CashierRow Total(IReadOnlyList<CashierRow> rows)
    {
        var revenue = rows.Sum(r => r.Revenue);
        var checks = rows.Sum(r => r.Checks);
        return new CashierRow(ReportLabels.Total, revenue, checks, checks > 0 ? Math.Round(revenue / checks, 2) : 0m, rows.Sum(r => r.Returns),
            rows.Sum(r => r.ReturnsSum), rows.Sum(r => r.PartialReturns), rows.Sum(r => r.DiscountSum), rows.Sum(r => r.Errors));
    }

    // ── Тепловая карта ─────────────────────────────────────────────────────────────

    /// <summary>Тепловая карта: 7 дней недели (0 — понедельник … 6 — воскресенье) × 24 часа. Revenue и Checks — по чекам выручки (без возвратов и долга).</summary>
    public sealed class HeatMap
    {
        public decimal[,] Revenue { get; } = new decimal[7, 24];

        public int[,] Checks { get; } = new int[7, 24];

        public decimal TotalRevenue { get; internal set; }

        public int TotalChecks { get; internal set; }

        public decimal DayRevenue(int day) => Enumerable.Range(0, 24).Sum(h => Revenue[day, h]);

        public int DayChecks(int day) => Enumerable.Range(0, 24).Sum(h => Checks[day, h]);

        public decimal HourRevenue(int hour) => Enumerable.Range(0, 7).Sum(d => Revenue[d, hour]);

        public int HourChecks(int hour) => Enumerable.Range(0, 7).Sum(d => Checks[d, hour]);

        /// <summary>Самая «горячая» ячейка по выручке; продаж нет — null.</summary>
        public (int Day, int Hour)? Peak()
        {
            (int, int)? best = null;
            decimal max = 0;
            for (var d = 0; d < 7; d++)
                for (var h = 0; h < 24; h++)
                    if (Revenue[d, h] > max)
                    {
                        max = Revenue[d, h];
                        best = (d, h);
                    }
            return best;
        }

        /// <summary>Самый сильный день недели и час по выручке; продаж нет — null.</summary>
        public int? BestDay() => TotalRevenue > 0 ? Enumerable.Range(0, 7).OrderByDescending(DayRevenue).First() : null;

        public int? BestHour() => TotalRevenue > 0 ? Enumerable.Range(0, 24).OrderByDescending(HourRevenue).First() : null;

        /// <summary>Яркость ячейки 0…1 — доля от самой большой (для цвета). Корень смягчает перекос: одна рекордная ячейка не делает остальные «пустыми».</summary>
        public double Intensity(int day, int hour)
        {
            var max = 0m;
            foreach (var v in Revenue)
                if (v > max)
                    max = v;
            return max <= 0 || Revenue[day, hour] <= 0 ? 0 : Math.Sqrt((double)(Revenue[day, hour] / max));
        }
    }

    /// <summary>День недели с понедельника: 0 — пн … 6 — вс.</summary>
    public static int WeekdayIndex(DateTime d) => ((int)d.DayOfWeek + 6) % 7;

    public static string WeekdayShort(int index) => index switch
    {
        0 => Tr.T("Пн", "Дш", "Mon", "Pzt", "Du"),
        1 => Tr.T("Вт", "Шш", "Tue", "Sal", "Se"),
        2 => Tr.T("Ср", "Шр", "Wed", "Çar", "Chor"),
        3 => Tr.T("Чт", "Бш", "Thu", "Per", "Pay"),
        4 => Tr.T("Пт", "Жм", "Fri", "Cum", "Ju"),
        5 => Tr.T("Сб", "Иш", "Sat", "Cmt", "Sha"),
        _ => Tr.T("Вс", "Жк", "Sun", "Paz", "Yak"),
    };

    public static string WeekdayLong(int index) => index switch
    {
        0 => Tr.T("Понедельник", "Дүйшөмбү", "Monday", "Pazartesi", "Dushanba"),
        1 => Tr.T("Вторник", "Шейшемби", "Tuesday", "Salı", "Seshanba"),
        2 => Tr.T("Среда", "Шаршемби", "Wednesday", "Çarşamba", "Chorshanba"),
        3 => Tr.T("Четверг", "Бейшемби", "Thursday", "Perşembe", "Payshanba"),
        4 => Tr.T("Пятница", "Жума", "Friday", "Cuma", "Juma"),
        5 => Tr.T("Суббота", "Ишемби", "Saturday", "Cumartesi", "Shanba"),
        _ => Tr.T("Воскресенье", "Жекшемби", "Sunday", "Pazar", "Yakshanba"),
    };

    public static HeatMap BuildHeatMap(IEnumerable<SaleFact> sales)
    {
        var map = new HeatMap();
        foreach (var s in sales)
        {
            if (IsReturn(s.Status) || !SalesExportService.CountsAsRevenue(s.Status))
                continue;
            var d = WeekdayIndex(s.At);
            map.Revenue[d, s.At.Hour] += s.Total;
            map.Checks[d, s.At.Hour]++;
            map.TotalRevenue += s.Total;
            map.TotalChecks++;
        }
        return map;
    }

    // ── Сравнение итогов двух периодов ─────────────────────────────────────────────

    /// <summary>Итоги периода для сравнения: выручка, чеков, средний чек.</summary>
    public sealed record Totals(decimal Revenue, int Checks, decimal AvgCheck);

    public static Totals TotalsOf(IEnumerable<SaleFact> sales)
    {
        decimal revenue = 0;
        var checks = 0;
        foreach (var s in sales)
        {
            if (IsReturn(s.Status) || !SalesExportService.CountsAsRevenue(s.Status))
                continue;
            revenue += s.Total;
            checks++;
        }
        return new Totals(revenue, checks, checks > 0 ? Math.Round(revenue / checks, 2) : 0m);
    }

    /// <summary>Строка сравнения «показатель — сейчас — было — изменение».</summary>
    public sealed record CompareLine(string Label, decimal Current, decimal Previous, decimal? Percent, bool IsMoney);

    public static IReadOnlyList<CompareLine> CompareTotals(Totals current, Totals previous) =>
    [
        new(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), current.Revenue, previous.Revenue, DeltaPercent(current.Revenue, previous.Revenue), true),
        new(Tr.T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), current.Checks, previous.Checks, DeltaPercent(current.Checks, previous.Checks), false),
        new(Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), current.AvgCheck, previous.AvgCheck, DeltaPercent(current.AvgCheck, previous.AvgCheck), true),
    ];

    /// <summary>Подпись «к чему сравниваем» для строки состояния.</summary>
    public static string CompareName(CompareBase basis, PeriodKind kind) => basis switch
    {
        CompareBase.YearAgo => Tr.T("к тому же периоду год назад", "бир жыл мурунку ошол мезгилге карата", "vs the same period a year ago", "geçen yılın aynı dönemine göre", "bir yil oldingi shu davrga nisbatan"),
        CompareBase.Previous => kind switch
        {
            PeriodKind.Week => Tr.T("к прошлой неделе", "өткөн жумага карата", "vs last week", "geçen haftaya göre", "o'tgan haftaga nisbatan"),
            PeriodKind.Month => Tr.T("к прошлому месяцу", "өткөн айга карата", "vs last month", "geçen aya göre", "o'tgan oyga nisbatan"),
            PeriodKind.Custom => Tr.T("к предыдущему периоду", "мурунку мезгилге карата", "vs previous period", "önceki döneme göre", "oldingi davrga nisbatan"),
            _ => Tr.T("к вчера", "кечээге карата", "vs yesterday", "düne göre", "kechaga nisbatan"),
        },
        _ => "",
    };
}
