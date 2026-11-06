using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave10;

/// <summary>2026-11-06: В10 Л4-16 / АР-1 — разбивка возврата «наличные / безнал / долг» и пропорциональная подгонка строк чека возврата вынесены из окна
/// возврата (ReturnSaleDialog) в RefundSplit.Compute / RefundSplit.ScaleLines. Тесты сверяют перенос с копией ПРЕЖНЕГО кода окна (Reference*; формула смешанной
/// оплаты — до-В2, OldMixed): на тысячах случайных чеков результаты должны совпасть до копейки.</summary>
public sealed class ReturnSplitEquivalenceTests
{
    private static (decimal Cash, decimal NonCash) OldMixed(decimal amount, decimal cashLeft, decimal nonCashLeft) =>
        cashLeft + nonCashLeft > 0m
            ? (Math.Min(amount, Math.Round(amount * cashLeft / (cashLeft + nonCashLeft), 2, MidpointRounding.AwayFromZero)),
               amount - Math.Min(amount, Math.Round(amount * cashLeft / (cashLeft + nonCashLeft), 2, MidpointRounding.AwayFromZero)))
            : (amount, 0m);

    private static (decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown, decimal? ServerCashOut, decimal? CalcOverServer) Reference(
        SaleLedgerEntry? ledger, string? fallbackMethod, decimal amount, decimal? cashOut, decimal? serverDebtLeft = null, decimal? localDebtPaid = null, decimal? localDebtCash = null)
    {
        decimal cash, nonCash, debtReduced;
        var splitKnown = true;
        var debtUnknown = false;
        decimal? serverCashOut = null;
        decimal? calcOverServer = null;
        var method = (ledger?.Method is { Length: > 0 } known ? known : fallbackMethod ?? "").Trim().ToLowerInvariant();
        var isDebtSale = ledger is { Debt: > 0.005m } || method == "debt";
        if (isDebtSale)
        {
            // Долг — сначала; выдаётся только то, что покупатель реально внёс.
            decimal money;
            if (serverDebtLeft is { } debtNow)
            {
                // 2026-10-21 (ревью денег, п. 2): долг уменьшается не больше текущего остатка сервера (и не больше остатка
                // по журналу кассы — сервер мог не уменьшить долг на прошлые возвраты); погашенный долг — выдаётся деньгами.
                // 2026-11-01 (аудит «10 из 10», Ж-01): остаток долга известен — это главное: клиент внёс предоплату и оплаты долга, всё внесённое
                // и есть «положено выдать» (чек 3: предоплата 1 + оплата долга 1, долг сейчас 1 → выдать 2). Раньше цифра сервера (выдача по
                // движениям денег) брала верх и не знала оплат долга — выдавалось 1. Цифра сервера теперь только сверяется (ServerCashOut).
                var debtLeft = ledger is not null ? Math.Min(Math.Max(0m, debtNow), ledger.DebtLeft) : Math.Max(0m, debtNow);
                var byDebt = Math.Max(0m, amount - Math.Min(amount, debtLeft));
                decimal? byServer = cashOut is { } serverGave ? Math.Min(amount, Math.Max(0m, serverGave)) : null;
                if (ledger is null)
                {
                    // 2026-11-01 (приёмка, Ж-01): журнала кассы нет (чек с другой кассы/сайта) — остаток долга сделки может быть общим или неточным, оплаты
                    // не подтверждены: выдачу не уменьшаем ниже цифры сервера, а если цифры расходятся или сервер выдачу не назвал — кассиру предупреждение
                    // «проверьте долг клиента перед выдачей» (как при неизвестном остатке).
                    money = byServer is { } bs ? Math.Max(byDebt, bs) : byDebt;
                    if (byServer is null || Math.Abs(byDebt - byServer.Value) > 0.009m)
                        debtUnknown = true;
                }
                else if (byServer is { } lesser && byDebt > lesser + 0.009m && localDebtPaid is { } paidHere && paidHere < byDebt - lesser - 0.009m)
                {
                    // 2026-11-01 (code-review, Ж-01): расчёт по журналу выше цифры сервера, а разницу не объясняют оплаты долга, записанные на ЭТОЙ кассе (клиент мог
                    // платить на другой кассе или на сайте — журнал устарел): выдаём меньшую сумму, из ящика не уходит то, чего сервер не отразил, кассиру — предупреждение.
                    // Чек 001967-типа (оплаты долга сделаны здесь и записаны в смену) сюда не попадает: выдача по долгу, расхождение — ServerCashOut.
                    money = lesser;
                    calcOverServer = byDebt;
                }
                else
                    money = byDebt;
                if (calcOverServer is null && byServer is { } given && Math.Abs(given - money) > 0.009m)
                    serverCashOut = given;
            }
            else if (cashOut is { } givenByServer)
                money = Math.Min(amount, Math.Max(0m, givenByServer));
            else
            {
                // 2026-10-21 (ревью денег, п. 2): остаток сервера неизвестен — журнал не знает оплат долга, уверенное
                // «Выдать 0,00» было бы неправдой: расчёт по журналу, но кассиру — предупреждение проверить долг.
                debtUnknown = true;
                money = ledger is not null ? Math.Max(0m, amount - Math.Min(amount, ledger.DebtLeft)) : 0m;
            }
            debtReduced = amount - money;
            var cashShareLeft = ledger?.CashLeft ?? money;
            var nonCashShareLeft = ledger?.NonCashLeft ?? 0m;
            // 2026-10-21 (ревью денег, п. 2): к выдаче больше внесённого при продаже — разница пришла оплатами долга, способ
            // которых касса не знает: внесённое делим по способам, остальное — одной суммой (наличными, без «на карту»).
            var prepaidMoney = ledger is not null ? Math.Min(money, cashShareLeft + nonCashShareLeft) : money;
            // 2026-11-01 (живая регрессия, Р-5): оплаты долга наличными записаны на этой кассе (журнал событий смены) — их способ известен, «неизвестно» не пишем.
            if (money - prepaidMoney > 0.005m && !(localDebtCash is { } paidHereCash && paidHereCash >= money - prepaidMoney - 0.009m))
                splitKnown = false;
            // 2026-11-06: В2 Л4-16 — доля «наличные/безнал» — единая формула RefundSplit (общая с офлайн-планом возврата).
            cash = OldMixed(prepaidMoney, cashShareLeft, nonCashShareLeft).Cash;
            cash += money - prepaidMoney;
            nonCash = money - cash;
        }
        else
        {
            debtReduced = 0m;
            switch (method)
            {
                case "cash":
                    cash = amount;
                    nonCash = 0m;
                    break;
                case "mixed" or "split" when ledger is not null && ledger.CashLeft + ledger.NonCashLeft > 0m:
                    (cash, nonCash) = OldMixed(amount, ledger.CashLeft, ledger.NonCashLeft); // 2026-11-06: В2 Л4-16 — единая формула
                    break;
                case "mixed" or "split" or "":
                    // Касса не знает, как заплатили (чек до возвратов не видела) — одна сумма, как раньше.
                    cash = amount;
                    nonCash = 0m;
                    splitKnown = false;
                    break;
                default:
                    cash = 0m;
                    nonCash = amount;
                    break;
            }
        }
        return (cash, nonCash, debtReduced, splitKnown, debtUnknown, serverCashOut, calcOverServer);
    }

    private static List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> ReferenceScale(
        List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> lines, decimal estimated, decimal actual)
    {
        if (lines.Count == 0 || estimated <= 0m || Math.Abs(estimated - actual) < 0.005m)
            return lines;

        var factor = actual / estimated;
        var result = new List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)>(lines.Count);
        var left = actual;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var sum = i == lines.Count - 1 ? left : Math.Round(line.Sum * factor, 2, MidpointRounding.AwayFromZero);
            left -= sum;
            var qty = line.Quantity > 0 ? (decimal)line.Quantity : 1m;
            result.Add((line.Name, line.Quantity, Math.Round(sum / qty, 2, MidpointRounding.AwayFromZero), sum));
        }

        return result;
    }

    private static readonly string[] Methods = { "cash", "mixed", "split", "card", "debt", "", "transfer" };

    private static SaleLedgerEntry Ledger(string method, int total, int paidCash, int paidNon, int refCash, int refNon, int refDebt)
    {
        var e = new SaleLedgerEntry
        {
            SaleId = "s", Method = method, Total = total / 100m, LinesTotal = total / 100m,
            PaidCash = paidCash / 100m, PaidNonCash = paidNon / 100m,
        };
        if (refCash + refNon + refDebt > 0)
            e.Refunds.Add(new SaleLedgerRefund { Amount = (refCash + refNon + refDebt) / 100m, Cash = refCash / 100m, NonCash = refNon / 100m, DebtReduced = refDebt / 100m });
        return e;
    }

    [Fact]
    public void Compute_MatchesOldWindowCode_OnRandomReceipts() =>
        Gen.Select(
                Gen.Select(Gen.Int[0, 6], Gen.Int[100, 300_000], Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 100]),
                Gen.Select(Gen.Int[0, 100], Gen.Int[0, 100], Gen.Int[0, 3], Gen.Int[0, 100], Gen.Int[0, 100]))
            .Sample((a, b) =>
            {
                var (mi, total, pcPct, pnPct, rcPct) = a;
                var (rnPct, rdPct, hasLedger, opt1, opt2) = b;
                var method = Methods[mi];
                var paidCash = total * pcPct / 100;
                var paidNon = Math.Min(total - paidCash, total * pnPct / 100);
                var refCash = paidCash * rcPct / 100;
                var refNon = paidNon * rnPct / 100;
                var refDebt = Math.Max(0, total - paidCash - paidNon) * rdPct / 100;
                var ledger = hasLedger == 0 ? null : Ledger(method, total, paidCash, paidNon, refCash, refNon, refDebt);
                var amount = Math.Max(1, (total - refCash - refNon - refDebt) * (opt1 + 1) / 101) / 100m;
                decimal? cashOut = opt1 % 3 == 0 ? null : amount * opt1 / 100m;
                decimal? debtLeft = opt2 % 4 == 0 ? null : (ledger?.DebtLeft ?? total / 100m) * opt2 / 100m;
                decimal? localPaid = opt2 % 5 == 0 ? null : opt1 / 100m;
                decimal? localCash = opt1 % 7 == 0 ? null : opt2 / 100m;
                var fallback = hasLedger == 0 ? method : (opt1 % 2 == 0 ? method : null);

                var expected = Reference(ledger, fallback, amount, cashOut, debtLeft, localPaid, localCash);
                var actual = RefundSplit.Compute(ledger, fallback, amount, cashOut, debtLeft, localPaid, localCash);
                actual.Should().Be(expected);
            }, iter: 20000);

    [Fact]
    public void Compute_MixedSale_SplitsByLeftovers()
    {
        // чек 100: 70 наличными + 30 безналом, возврат 40 → 28 нал / 12 безнал
        var ledger = new SaleLedgerEntry { SaleId = "s", Method = "mixed", Total = 100m, LinesTotal = 100m, PaidCash = 70m, PaidNonCash = 30m };
        var r = RefundSplit.Compute(ledger, null, 40m, null);
        r.Cash.Should().Be(28m);
        r.NonCash.Should().Be(12m);
        r.DebtReduced.Should().Be(0m);
        r.SplitKnown.Should().BeTrue();
    }

    [Fact]
    public void Compute_DebtSale_WholeReceipt_ReducesDebtFirst()
    {
        // чек 100: предоплата 40 наличными, долг 60 (остаток 60); возврат всего чека → деньгами 40, долг −60
        var ledger = new SaleLedgerEntry { SaleId = "s", Method = "debt", Total = 100m, LinesTotal = 100m, PaidCash = 40m, PaidNonCash = 0m };
        var r = RefundSplit.Compute(ledger, null, 100m, null, 60m);
        r.Cash.Should().Be(40m);
        r.NonCash.Should().Be(0m);
        r.DebtReduced.Should().Be(60m);
    }

    [Fact]
    public void Compute_NoLedger_UnknownMethod_IsOneAmountAndFlaggedUnknown()
    {
        var r = RefundSplit.Compute(null, "", 25m, null);
        r.Cash.Should().Be(25m);
        r.SplitKnown.Should().BeFalse();
    }

    [Fact]
    public void ScaleLines_MatchesOldWindowCode_OnRandomLines() =>
        Gen.Select(Gen.Int[1, 6], Gen.Int[1, 500_000], Gen.Int[1, 500_000], Gen.Int[1, 9])
            .Sample((n, est, act, seed) =>
            {
                var lines = Enumerable.Range(0, n)
                    .Select(i => ($"p{i}", (double)((i + seed) % 4 + 1), ((i + seed) * 137 % 9000 + 100) / 100m, ((i + seed) * 977 % 90000 + 100) / 100m))
                    .ToList();
                var expected = ReferenceScale(lines, est / 100m, act / 100m);
                var actual = RefundSplit.ScaleLines(lines, est / 100m, act / 100m);
                actual.Should().Equal(expected);
            }, iter: 5000);

    [Fact]
    public void ScaleLines_SumEqualsActual_AndSameTotalIsUntouched()
    {
        var lines = new List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> { ("a", 1, 33.33m, 33.33m), ("b", 2, 33.33m, 66.66m), ("c", 3, 10m, 30m) };
        var scaled = RefundSplit.ScaleLines(lines, 130m, 100m);
        scaled.Sum(x => x.Sum).Should().Be(100m);
        RefundSplit.ScaleLines(lines, 130m, 130m).Should().BeSameAs(lines);
    }
}
