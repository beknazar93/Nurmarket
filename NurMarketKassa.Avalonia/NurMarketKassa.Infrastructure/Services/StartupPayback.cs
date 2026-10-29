using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Один месяц (или его часть) с чистой прибылью: период «с — по» и чистая прибыль за него по отчёту сервера.</summary>
public sealed record PaybackMonth(DateTime From, DateTime To, decimal Net, NetProfitCalculator.Pnl? Detail = null)
{
    /// <summary>Месяц прошёл целиком и вошёл в расчёт полностью (не первый неполный и не текущий).</summary>
    public bool IsFull => From.Day == 1 && To.Date == new DateTime(From.Year, From.Month, DateTime.DaysInMonth(From.Year, From.Month));
}

/// <summary>Состояние расчёта окупаемости.</summary>
public enum PaybackStatus
{
    /// <summary>Стартовых расходов нет.</summary>
    NoCosts,
    /// <summary>Расходы есть, а даты открытия нет.</summary>
    NoOpeningDate,
    /// <summary>Идёт окупаемость.</summary>
    InProgress,
    /// <summary>Накопленная прибыль уже не меньше вложений.</summary>
    PaidBack,
}

/// <summary>Строка помесячной таблицы окупаемости.</summary>
public sealed record PaybackRow(DateTime Month, decimal Net, decimal Cumulative, decimal Invested, decimal Amortization)
{
    /// <summary>Чистая прибыль с учётом амортизации за этот месяц.</summary>
    public decimal NetAfterAmortization => NetProfitCalculator.AfterAmortization(Net, Amortization);
}

/// <summary>Результат расчёта окупаемости вложений.</summary>
public sealed class PaybackResult
{
    public PaybackStatus Status { get; init; }
    /// <summary>Вложено: сумма стартовых расходов.</summary>
    public decimal Invested { get; init; }
    /// <summary>Вернулось: накопленная чистая прибыль с даты открытия (может быть отрицательной).</summary>
    public decimal Returned { get; init; }
    /// <summary>Осталось окупить: вложено − вернулось, не меньше нуля.</summary>
    public decimal Remaining => Math.Max(0m, Invested - Returned);
    /// <summary>Окупаемость, % = вернулось ÷ вложено × 100 (может быть отрицательной — прибыли ещё нет).</summary>
    public decimal PercentBack => Invested > 0m ? Returned / Invested * 100m : 0m;
    /// <summary>Среднемесячная чистая прибыль за последние полные месяцы (до 3); null — данных для прогноза мало.</summary>
    public decimal? AvgMonthlyNet { get; init; }
    /// <summary>Сколько месяцев вошло в среднее (0 — среднее по дням с открытия).</summary>
    public int AvgMonthsUsed { get; init; }
    /// <summary>Сколько месяцев ещё окупаться при нынешней средней прибыли; null — прибыль не положительная или данных мало.</summary>
    public decimal? MonthsToPayback { get; init; }
    /// <summary>Прогноз даты окупаемости; null — как <see cref="MonthsToPayback"/>.</summary>
    public DateTime? ForecastDate { get; init; }
    /// <summary>Месяц, в котором накопленная прибыль впервые догнала вложения; null — ещё нет.</summary>
    public DateTime? PaidBackMonth { get; init; }
    /// <summary>Сколько месяцев прошло с открытия (дробное).</summary>
    public decimal ElapsedMonths { get; init; }
    public IReadOnlyList<PaybackRow> Rows { get; init; } = Array.Empty<PaybackRow>();
    /// <summary>Сумма амортизации за всё время (вложения, списанные к сегодня).</summary>
    public decimal AmortizedToDate { get; init; }
}

/// <summary>2026-10-28, владелец: «для аналитики как расход на начало бизнеса». Расчёт окупаемости вложений на открытие.
///
/// Формулы (всё по чистой прибыли отчёта сервера, <see cref="NetProfitCalculator"/> — той же, что в «Прибыли и деньгах»):
///   • Вложено = сумма стартовых расходов (<see cref="StartupCostsStore"/>).
///   • Вернулось = накопленная чистая прибыль с даты открытия бизнеса до сегодня (сумма по месяцам; чистая = валовая − расходы NurCRM).
///     Стартовые расходы сюда НЕ вычитаются — они вне операционной прибыли (иначе вложения посчитались бы дважды).
///   • Осталось окупить = max(0, Вложено − Вернулось); Окупаемость, % = Вернулось ÷ Вложено × 100.
///   • Среднемесячная прибыль = среднее чистой прибыли за последние до 3 ПОЛНЫХ месяцев (первый неполный и текущий неполный месяцы
///     не берутся). Полных месяцев нет, а с открытия прошло не меньше 14 дней — берётся прибыль на день за всё время × 30,4375.
///   • Срок окупаемости, мес. = Осталось ÷ Среднемесячная (только если она больше нуля); дата = сегодня + срок × 30,4375 дней.
///   • Амортизация (по переключателю, по умолчанию выключена): вложения списываются равномерно на N месяцев, считая от даты открытия
///     (по дням: Вложено ÷ дней в окне × дней периода в окне); «чистая прибыль с учётом амортизации» = чистая − амортизация.</summary>
public static class StartupPayback
{
    private const double DaysInMonth = 30.4375;

    /// <summary>Сколько последних полных месяцев берётся для прогноза.</summary>
    public const int ForecastMonths = 3;

    public static PaybackResult Compute(IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, IReadOnlyList<PaybackMonth> months, DateTime today,
        decimal? investedOverride = null, int forecastMonths = ForecastMonths, decimal extraReturned = 0m)
    {
        var invested = investedOverride ?? StartupCostsStore.Sum(items);
        if (invested <= 0m)
            return new PaybackResult { Status = PaybackStatus.NoCosts };
        if (meta.OpenedOn is not { } opened)
            return new PaybackResult { Status = PaybackStatus.NoOpeningDate, Invested = invested };

        var ordered = months.OrderBy(m => m.From).ToList();
        var returned = ordered.Sum(m => m.Net) + extraReturned;
        var rows = new List<PaybackRow>();
        decimal cumulative = 0m;
        DateTime? paidBack = null;
        foreach (var m in ordered)
        {
            cumulative += m.Net;
            rows.Add(new PaybackRow(new DateTime(m.From.Year, m.From.Month, 1), m.Net, cumulative, invested, AmortizationFor(invested, opened, meta.AmortMonths, m.From, m.To)));
            if (paidBack is null && cumulative >= invested)
                paidBack = new DateTime(m.From.Year, m.From.Month, 1);
        }

        // Прогноз: последние до 3 полных месяцев.
        var full = ordered.Where(m => m.IsFull && m.To.Date < today.Date).TakeLast(Math.Max(1, forecastMonths)).ToList();
        decimal? avg = null;
        var used = 0;
        if (full.Count > 0)
        {
            avg = full.Average(m => m.Net);
            used = full.Count;
        }
        else
        {
            var days = (today.Date - opened.Date).TotalDays + 1;
            if (days >= 14)
                avg = returned / (decimal)days * (decimal)DaysInMonth;
        }

        var remaining = Math.Max(0m, invested - returned);
        decimal? monthsLeft = null;
        DateTime? forecast = null;
        if (remaining > 0m && avg is > 0m)
        {
            monthsLeft = remaining / avg.Value;
            // Больше 100 лет — это не прогноз, а «никогда»; DateTime.AddDays на таких числах и вовсе упадёт.
            if (monthsLeft < 1200m)
                forecast = today.Date.AddDays((double)monthsLeft.Value * DaysInMonth);
            else
                monthsLeft = null;
        }

        var elapsed = (decimal)((today.Date - opened.Date).TotalDays / DaysInMonth);
        return new PaybackResult
        {
            Status = returned >= invested ? PaybackStatus.PaidBack : PaybackStatus.InProgress,
            Invested = invested,
            Returned = returned,
            AvgMonthlyNet = avg,
            AvgMonthsUsed = used,
            MonthsToPayback = monthsLeft,
            ForecastDate = forecast,
            PaidBackMonth = paidBack,
            ElapsedMonths = Math.Max(0m, elapsed),
            Rows = rows,
            AmortizedToDate = AmortizationFor(invested, opened, meta.AmortMonths, opened, today),
        };
    }

    /// <summary>Амортизация за период [from; to] (включительно): вложения равномерно на <paramref name="amortMonths"/> месяцев от даты открытия.</summary>
    public static decimal AmortizationFor(decimal invested, DateTime openedOn, int amortMonths, DateTime from, DateTime to)
    {
        if (invested <= 0m || amortMonths <= 0 || to.Date < from.Date)
            return 0m;
        var start = openedOn.Date;
        var end = start.AddMonths(amortMonths); // правая граница не включается
        var windowDays = (end - start).TotalDays;
        if (windowDays <= 0)
            return 0m;
        var a = from.Date > start ? from.Date : start;
        var b = to.Date.AddDays(1) < end ? to.Date.AddDays(1) : end;
        var overlap = (b - a).TotalDays;
        return overlap <= 0 ? 0m : invested * (decimal)(overlap / windowDays);
    }

    /// <summary>Амортизация за период для окон «Прибыль и деньги» и «Сводка»: 0, если переключатель выключен, даты открытия или вложений нет.</summary>
    public static decimal AmortizationForPeriod(IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, DateTime from, DateTime to)
    {
        if (!meta.Amortize || meta.OpenedOn is not { } opened)
            return 0m;
        return AmortizationFor(StartupCostsStore.Sum(items), opened, meta.AmortMonths, from, to);
    }

    /// <summary>Месяцы от даты открытия до сегодня: первый — с даты открытия, последний — по сегодня.</summary>
    public static List<(DateTime From, DateTime To)> MonthRanges(DateTime openedOn, DateTime today)
    {
        var list = new List<(DateTime, DateTime)>();
        var first = new DateTime(openedOn.Year, openedOn.Month, 1);
        for (var m = first; m <= today.Date; m = m.AddMonths(1))
        {
            var from = m < openedOn.Date ? openedOn.Date : m;
            var endOfMonth = m.AddMonths(1).AddDays(-1);
            list.Add((from, endOfMonth > today.Date ? today.Date : endOfMonth));
        }
        return list;
    }
}

/// <summary>Загрузка чистой прибыли по месяцам с даты открытия (отчёт сервера GET api/main/analytics/pnl/ на каждый месяц, по 3 одновременно;
/// прошедшие месяцы запоминаются на 6 часов, текущий — на 2 минуты).</summary>
public static class StartupPaybackLoader
{
    /// <summary>Больше этого числа месяцев не запрашивается (10 лет).</summary>
    public const int MaxMonths = 120;

    private static readonly Dictionary<string, (NetProfitCalculator.Pnl Pnl, DateTime At)> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>Стенд проверки: свой источник отчёта вместо сервера (период → ответ pnl).</summary>
    public static Func<DateTime, DateTime, Task<JsonElement>>? PnlOverride { get; set; }

    public static void ClearCache()
    {
        lock (CacheLock)
            Cache.Clear();
    }

    /// <summary>Чистая прибыль по месяцам; сервер не ответил за любой месяц — исключение (частичная сумма вводила бы в заблуждение).</summary>
    public static async Task<List<PaybackMonth>> LoadAsync(NurMarketApiClient? api, DateTime openedOn, DateTime today, CancellationToken ct)
    {
        var ranges = StartupPayback.MonthRanges(openedOn, today);
        if (ranges.Count > MaxMonths)
            throw new InvalidOperationException(Tr.T($"Дата открытия слишком давняя (больше {MaxMonths / 12} лет).", $"Ачылган күн өтө эрте ({MaxMonths / 12} жылдан ашык).",
                $"The opening date is too far back (over {MaxMonths / 12} years).", $"Açılış tarihi çok eski ({MaxMonths / 12} yıldan fazla).", $"Ochilgan sana juda eski ({MaxMonths / 12} yildan ortiq)."));
        var company = CompanyInfoService.LastCompany?.Id ?? "";
        var result = new PaybackMonth[ranges.Count];
        using var gate = new SemaphoreSlim(3);
        var tasks = ranges.Select(async (r, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var pnl = await PnlFor(api, company, r.From, r.To, today, ct).ConfigureAwait(false);
                result[i] = new PaybackMonth(r.From, r.To, pnl.Net, pnl);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result.ToList();
    }

    private static async Task<NetProfitCalculator.Pnl> PnlFor(NurMarketApiClient? api, string company, DateTime from, DateTime to, DateTime today, CancellationToken ct)
    {
        var key = $"{company}|{from:yyyy-MM-dd}|{to:yyyy-MM-dd}";
        var current = to.Date >= today.Date;
        var ttl = current ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(6);
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ttl)
                return hit.Pnl;

        JsonElement data;
        if (PnlOverride is { } over)
            data = await over(from, to).ConfigureAwait(false);
        else
        {
            var query = new Dictionary<string, string>
            {
                ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            data = await api!.RequestAsync(HttpMethod.Get, "api/main/analytics/pnl/", null, query, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        var pnl = NetProfitCalculator.FromPnl(data) ?? throw new InvalidDataException("отчёт прибыли без нужных полей");
        lock (CacheLock)
            Cache[key] = (pnl, DateTime.UtcNow);
        return pnl;
    }
}
