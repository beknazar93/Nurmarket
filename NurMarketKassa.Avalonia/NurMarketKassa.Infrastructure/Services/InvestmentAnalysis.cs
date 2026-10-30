using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29, владелец: модуль «Инвестиции и окупаемость» (макет design/Investments/Main.dc.html). Движок выплат инвестору
/// по этапам: один шаг = один месяц. Порт функции run() макета на decimal с двумя отличиями от макета, нужными деньгам:
/// (1) выплата за месяц никогда не больше потолка месяца (cap) — ей передают чистую прибыль месяца минус уже выплаченное другим
/// инвесторам, поэтому сумма выплат не превышает чистой прибыли; (2) выплата округляется вниз до копейки (сумма долей не больше базы).</summary>
public sealed class PayoutEngine
{
    private readonly IReadOnlyList<PayStage> _stages;
    private readonly decimal _invested;
    private readonly string _loss;
    private readonly decimal _minPay;
    private int _si;
    private int _stageMonths;

    public PayoutEngine(IReadOnlyList<PayStage> stages, decimal invested, string lossRule, decimal minMonthly)
    {
        _stages = stages;
        _invested = invested;
        _loss = lossRule;
        _minPay = minMonthly;
    }

    /// <summary>Накоплено выплат (начислено, до налога).</summary>
    public decimal Cum { get; private set; }

    /// <summary>Убыток, ещё не покрытый (правило «переносить»).</summary>
    public decimal Carried { get; private set; }

    /// <summary>Номер текущего этапа (0…); равен числу этапов — выплаты завершены.</summary>
    public int Stage => _si;

    public readonly record struct Step(decimal Base, decimal Pay, int StageBefore, decimal Carried, decimal CutByCap);

    /// <summary>Один месяц: <paramref name="rawBase"/> — база месяца (может быть отрицательной), <paramref name="cap"/> — сколько всего можно выплатить за месяц.</summary>
    public Step Next(decimal rawBase, decimal cap)
    {
        decimal b;
        switch (_loss)
        {
            case "carry":
                b = rawBase - Carried;
                if (b < 0m) { Carried = -b; b = 0m; } else Carried = 0m;
                break;
            case "none":
                b = Math.Max(rawBase, 0m);
                break;
            default:
                b = rawBase;
                break;
        }

        cap = Math.Max(0m, cap);
        decimal pay = 0m, wanted = 0m;
        var startSi = _si;
        if (b < 0m)
        {
            // «Инвестор участвует в убытке»: его доля убытка уменьшает накопленное.
            if (_si < _stages.Count)
            {
                pay = b * _stages[_si].Pct / 100m;
                Cum += pay;
            }
        }
        else
        {
            var rem = b;
            var guard = 0;
            while (rem > 0.005m && _si < _stages.Count && guard++ < 50)
            {
                var st = _stages[_si];
                var pct = Math.Max(0m, st.Pct) / 100m;
                var room = cap - pay;
                if (st.Cond == "return")
                {
                    var need = _invested * st.Val / 100m - Cum;
                    if (need <= 0.005m)
                    {
                        _si++;
                        _stageMonths = 0;
                        continue;
                    }
                    if (pct <= 0m)
                        break;
                    var p = rem * pct;
                    wanted += Math.Min(p, need);
                    if (p >= need)
                    {
                        var take = Math.Min(need, Math.Max(0m, room));
                        pay += take;
                        Cum += take;
                        if (take < need)
                            break; // потолок месяца исчерпан — этап не закрыт
                        rem -= need / pct;
                        _si++;
                        _stageMonths = 0;
                        continue;
                    }
                    var t = Math.Min(p, Math.Max(0m, room));
                    pay += t;
                    Cum += t;
                    rem = 0m;
                }
                else
                {
                    var p = rem * pct;
                    wanted += p;
                    var t = Math.Min(p, Math.Max(0m, room));
                    pay += t;
                    Cum += t;
                    rem = 0m;
                }
            }

            if (_minPay > 0m && b > 0m && startSi < _stages.Count && pay < _minPay)
            {
                var extra = Math.Min(Math.Min(_minPay, b), cap) - pay;
                if (extra > 0m)
                {
                    pay += extra;
                    Cum += extra;
                }
            }
        }

        // До копейки вниз: сумма долей не больше базы и потолка.
        if (pay > 0m)
        {
            var floored = Math.Floor(pay * 100m) / 100m;
            Cum -= pay - floored;
            pay = floored;
        }

        if (_si < _stages.Count && _stages[_si].Cond == "months")
        {
            _stageMonths++;
            if (_stageMonths >= Math.Max(1m, _stages[_si].Val))
            {
                _si++;
                _stageMonths = 0;
            }
        }
        return new Step(b, pay, startSi, Carried, Math.Max(0m, wanted - Math.Max(pay, 0m)));
    }
}

/// <summary>Строка ведомости: месяц инвестора.</summary>
public sealed record MonthPayout(DateTime From, DateTime To, string MonthKey, decimal Net, decimal Base, decimal Carried, int StageBefore,
    decimal Pay, decimal Tax, decimal Cum, decimal CutByCap)
{
    /// <summary>К выплате на руки: начислено минус налог.</summary>
    public decimal ToHand => Pay - Tax;
}

/// <summary>Результат по инвестору.</summary>
public sealed class InvestorResult
{
    public required InvestorItem Investor { get; init; }
    public decimal Invested => Investor.Amount;
    public List<MonthPayout> Rows { get; init; } = new();
    /// <summary>Накоплено выплат (до налога).</summary>
    public decimal Got => Rows.Count > 0 ? Rows[^1].Cum : 0m;
    public decimal GotToHand => Rows.Sum(r => r.ToHand);
    public decimal LastPay => Rows.Count > 0 ? Rows[^1].Pay : 0m;
    public decimal Percent => Invested > 0m ? Math.Clamp(Got / Invested * 100m, 0m, 100m) : 0m;
    /// <summary>Через сколько месяцев от даты вложения инвестор вернёт вложения (по прогнозу); null — не вернёт.</summary>
    public int? PaybackMonths { get; set; }
    public DateTime? PaybackDate { get; set; }
    /// <summary>Этап сейчас (индекс); равен числу этапов — выплаты завершены.</summary>
    public int StageNow { get; set; }
}

/// <summary>Результат по владельцу (своя часть вложений).</summary>
public sealed class OwnerResult
{
    public decimal Invested { get; init; }
    public decimal Got { get; init; }
    public decimal LastMonthly { get; init; }
    public decimal Percent => Invested > 0m ? Math.Clamp(Got / Invested * 100m, 0m, 100m) : 0m;
    public int? PaybackMonths { get; set; }
    public DateTime? PaybackDate { get; set; }
}

public sealed class InvestmentResult
{
    public PaybackResult Business { get; init; } = new();
    public List<InvestorResult> Investors { get; init; } = new();
    public OwnerResult Owner { get; init; } = new();
    /// <summary>Вложено всего (стартовые расходы, но не меньше суммы инвесторов).</summary>
    public decimal TotalInvested { get; init; }
    /// <summary>Засчитанный товарный остаток (по закупке × ликвидность); 0 — не засчитывается.</summary>
    public decimal StockCredit { get; init; }
    public IReadOnlyList<PaybackMonth> Months { get; init; } = Array.Empty<PaybackMonth>();
    /// <summary>Чистая прибыль месяца за последний полный месяц (для показателей «за месяц»).</summary>
    public PaybackMonth? LastMonth { get; init; }
    /// <summary>Среднемесячная чистая прибыль, по которой строятся прогнозы.</summary>
    public decimal? AvgNet { get; init; }
    public int ForecastMonths { get; init; }
    /// <summary>Месяцев плана окупаемости - фактический срок бизнеса (положительное — раньше плана); null — план не задан или не окупится.</summary>
    public int? AheadOfPlan { get; init; }
    public DateTime? BusinessPaybackMonth { get; init; }
}

/// <summary>Результат симулятора («что будет, если прибыль будет X в месяц»).</summary>
public sealed record SimResult(int? InvestorMonths, int? OwnerMonths, int? BusinessMonths, List<(int Stage, int From, int To, decimal PayPerMonth)> Stages, decimal Monthly);

public static class InvestmentAnalysis
{
    private const int FutureMonths = 240;

    /// <summary>База доли за месяц по условиям инвестора: база (выручка/валовая/чистая с исключёнными статьями) минус резерв.</summary>
    public static decimal BaseOf(NetProfitCalculator.Pnl pnl, InvestorTerms t)
    {
        var b = NetProfitCalculator.BaseFor(pnl, t.Base, t.ExcludedExpenses);
        return b > 0m && t.ReservePct > 0m ? b * (1m - t.ReservePct / 100m) : b;
    }

    /// <summary>С какой даты считать: дата открытия бизнеса, а если её нет — самая ранняя дата вложения или инвестора; null — данных нет.</summary>
    public static DateTime? AnalysisStart(IReadOnlyList<StartupCostItem> costs, StartupCostsMeta meta, IReadOnlyList<InvestorItem> investors)
    {
        if (meta.OpenedOn is { } o)
            return o.Date;
        var dates = costs.Where(c => !c.Deleted).Select(c => c.Date).Concat(investors.Where(i => !i.Deleted).Select(i => i.Date)).ToList();
        return dates.Count > 0 ? dates.Min().Date : null;
    }

    public static string MonthKey(DateTime from) => from.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Индекс первого месяца, в котором инвестор уже вложился: месяц, содержащий дату, или первый после неё.</summary>
    public static int StartIndex(IReadOnlyList<PaybackMonth> months, DateTime date)
    {
        for (var i = 0; i < months.Count; i++)
            if (months[i].To.Date >= date.Date)
                return i;
        return months.Count;
    }

    private static decimal AvgTail(IReadOnlyList<decimal> values, IReadOnlyList<PaybackMonth> months, DateTime today, int fc)
    {
        var idx = Enumerable.Range(0, months.Count).Where(i => months[i].IsFull && months[i].To.Date < today.Date).ToList();
        if (idx.Count == 0)
            idx = Enumerable.Range(0, months.Count).ToList();
        if (idx.Count == 0)
            return 0m;
        var take = idx.TakeLast(Math.Max(1, fc)).ToList();
        return take.Average(i => values[i]);
    }

    private static int? FirstReach(IReadOnlyList<decimal> cumulative, decimal target)
    {
        if (target <= 0m)
            return null;
        for (var i = 0; i < cumulative.Count; i++)
            if (cumulative[i] >= target - 0.005m)
                return i;
        return null;
    }

    /// <summary>Полный расчёт: окупаемость бизнеса, ведомости и окупаемость инвесторов, часть владельца. <paramref name="stockCost"/> — товарный
    /// остаток по закупочной цене (null — не известен). Месяцы — как их вернул <see cref="StartupPaybackLoader"/> (у каждого есть Pnl).</summary>
    public static InvestmentResult Build(IReadOnlyList<StartupCostItem> costs, StartupCostsMeta meta, IReadOnlyList<InvestorItem> investors,
        IReadOnlyList<PaybackMonth> monthsIn, decimal? stockCost, DateTime today, int forecastMonths = StartupPayback.ForecastMonths)
    {
        var months = monthsIn.OrderBy(m => m.From).ToList();
        var costTotal = StartupCostsStore.Sum(costs);
        var invSum = investors.Sum(i => i.Amount);
        var total = Math.Max(costTotal, invSum);
        var ownInvested = Math.Max(0m, total - invSum);

        var credit = 0m;
        if (stockCost is > 0m && investors.FirstOrDefault(i => i.Terms.StockCredit) is { } sc)
            credit = Math.Round(stockCost.Value * sc.Terms.StockLiquidityPct / 100m, 2);

        var effMeta = meta.Clone();
        effMeta.OpenedOn ??= AnalysisStart(costs, meta, investors);
        var business = StartupPayback.Compute(costs, effMeta, months, today, total > 0m ? total : null, forecastMonths, credit);

        var nets = months.Select(m => m.Net).ToList();
        var ordered = investors.OrderBy(i => i.Date).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        var bases = ordered.Select(inv => months.Select(m => m.Detail is { } p ? BaseOf(p, inv.Terms) : m.Net).ToList()).ToList();
        var starts = ordered.Select(inv => StartIndex(months, inv.Date)).ToList();

        // История.
        var rowsBy = RunAll(ordered, bases, starts, nets, months, out var engines);
        var results = new List<InvestorResult>();
        for (var k = 0; k < ordered.Count; k++)
            results.Add(new InvestorResult { Investor = ordered[k], Rows = rowsBy[k], StageNow = engines[k].Stage });

        // Прогноз по средней за forecastMonths полных месяцев.
        var futureNet = AvgTail(nets, months, today, forecastMonths);
        var extNets = nets.Concat(Enumerable.Repeat(futureNet, FutureMonths)).ToList();
        var extBases = bases.Select((b, k) => b.Concat(Enumerable.Repeat(AvgTail(b, months, today, forecastMonths), FutureMonths)).ToList()).ToList();
        var extMonths = months.Concat(Enumerable.Range(0, FutureMonths).Select(i =>
        {
            var from = (months.Count > 0 ? new DateTime(months[^1].From.Year, months[^1].From.Month, 1) : new DateTime(today.Year, today.Month, 1)).AddMonths(i + 1);
            return new PaybackMonth(from, from.AddMonths(1).AddDays(-1), futureNet);
        })).ToList();
        var extRows = RunAll(ordered, extBases, starts, extNets, extMonths, out _);
        var first = months.Count > 0 ? new DateTime(months[0].From.Year, months[0].From.Month, 1) : new DateTime(today.Year, today.Month, 1);
        for (var k = 0; k < ordered.Count; k++)
        {
            var idx = FirstReach(extRows[k].Select(r => r.Cum).ToList(), ordered[k].Amount);
            if (idx is { } i)
            {
                // Месяцев от вложения: индекс окупаемости минус индекс старта плюс 1.
                results[k].PaybackMonths = Math.Max(1, i - Math.Min(starts[k], i) + 1);
                results[k].PaybackDate = first.AddMonths(i);
            }
        }

        // Владелец.
        decimal c = 0m;
        var ownCum = new List<decimal>();
        for (var i = 0; i < extNets.Count; i++)
        {
            var paid = extRows.Sum(r => r[i].Pay);
            c += extNets[i] - paid;
            ownCum.Add(c);
        }
        var histPaid = months.Select((_, i) => results.Sum(r => i < r.Rows.Count ? r.Rows[i].Pay : 0m)).ToList();
        var ownGot = months.Select((m, i) => m.Net - histPaid[i]).Sum();
        var owner = new OwnerResult
        {
            Invested = ownInvested,
            Got = ownGot,
            LastMonthly = months.Count > 0 ? months[^1].Net - histPaid[^1] : 0m,
        };
        if (FirstReach(ownCum, ownInvested) is { } oi)
        {
            owner.PaybackMonths = oi + 1;
            owner.PaybackDate = first.AddMonths(oi);
        }

        // Бизнес целиком: накопленная прибыль + засчитанный остаток против total.
        decimal bc = credit;
        var bizCum = extNets.Select(n => bc += n).ToList();
        var bizIdx = FirstReach(bizCum, total);
        int? ahead = null;
        if (meta.PlanMonths > 0 && bizIdx is { } bi)
            ahead = meta.PlanMonths - (bi + 1);

        return new InvestmentResult
        {
            Business = business,
            Investors = results,
            Owner = owner,
            TotalInvested = total,
            StockCredit = credit,
            Months = months,
            LastMonth = months.LastOrDefault(),
            AvgNet = business.AvgMonthlyNet ?? (months.Count > 0 ? futureNet : null),
            ForecastMonths = forecastMonths,
            AheadOfPlan = ahead,
            BusinessPaybackMonth = bizIdx is { } bj ? first.AddMonths(bj) : null,
        };
    }

    /// <summary>Прогон всех инвесторов по месяцам. За месяц все вместе не получают больше чистой прибыли месяца: инвесторы идут по дате вложения.</summary>
    private static List<List<MonthPayout>> RunAll(List<InvestorItem> investors, List<List<decimal>> bases, List<int> starts, IReadOnlyList<decimal> nets,
        IReadOnlyList<PaybackMonth> months, out List<PayoutEngine> engines)
    {
        engines = investors.Select(i => new PayoutEngine(i.Terms.Stages, i.Amount, i.Terms.LossRule, i.Terms.MinMonthly)).ToList();
        var rows = investors.Select(_ => new List<MonthPayout>()).ToList();
        for (var m = 0; m < months.Count; m++)
        {
            var remaining = Math.Max(0m, nets[m]);
            for (var k = 0; k < investors.Count; k++)
            {
                var eng = engines[k];
                if (m < starts[k])
                {
                    rows[k].Add(new MonthPayout(months[m].From, months[m].To, MonthKey(months[m].From), nets[m], 0m, 0m, 0, 0m, 0m, 0m, 0m));
                    continue;
                }
                var step = eng.Next(bases[k][m], remaining);
                if (step.Pay > 0m)
                    remaining -= step.Pay;
                var tax = step.Pay > 0m ? Math.Round(step.Pay * investors[k].Terms.TaxPct / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
                rows[k].Add(new MonthPayout(months[m].From, months[m].To, MonthKey(months[m].From), nets[m], step.Base, step.Carried, step.StageBefore, step.Pay, tax, eng.Cum, step.CutByCap));
            }
        }
        return rows;
    }

    /// <summary>Симулятор: постоянная база <paramref name="monthly"/> в месяц на 10 лет. Для базы «net» считается и окупаемость владельца и бизнеса.</summary>
    public static SimResult Simulate(InvestorTerms terms, decimal invested, decimal ownInvested, decimal monthly)
    {
        monthly = Math.Max(0m, monthly);
        var eng = new PayoutEngine(terms.Stages, invested, terms.LossRule, terms.MinMonthly);
        var groups = new List<(int Stage, int From, int To, decimal Pay)>();
        decimal ownCum = 0m;
        int? invIdx = null, ownIdx = null;
        for (var i = 0; i < 120; i++)
        {
            var s = eng.Next(monthly, monthly);
            if (groups.Count > 0 && groups[^1].Stage == s.StageBefore)
                groups[^1] = (groups[^1].Stage, groups[^1].From, i, groups[^1].Pay);
            else
                groups.Add((s.StageBefore, i, i, s.Pay));
            if (invIdx is null && eng.Cum >= invested - 0.005m && invested > 0m)
                invIdx = i + 1;
            ownCum += monthly - s.Pay;
            if (ownIdx is null && ownInvested > 0m && ownCum >= ownInvested - 0.005m)
                ownIdx = i + 1;
        }
        // 2026-10-30 (стресс-тест «Отчёты»): при ничтожной прибыли (0,01 сом/мес) и крупных вложениях частное не влезало в int — OverflowException
        // в симуляторе. Больше 100 лет (1200 мес) — не окупится в обозримом: «нет срока», как при нулевой прибыли.
        int? biz = null;
        if (monthly > 0m && invested + ownInvested > 0m)
        {
            var bizMonths = Math.Ceiling((invested + ownInvested) / monthly);
            biz = bizMonths <= 1200m ? (int)bizMonths : null;
        }
        var isNet = terms.Base == "net";
        return new SimResult(invIdx, isNet ? ownIdx : null, isNet ? biz : null, groups.Select(g => (g.Stage, g.From, g.To, g.Pay)).ToList(), monthly);
    }
}
