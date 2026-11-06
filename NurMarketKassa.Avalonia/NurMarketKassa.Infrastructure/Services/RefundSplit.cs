namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В2 Л4-16 / АР-1 — ЕДИНАЯ формула доли «наличные / безнал» возврата. Раньше была написана в двух местах (окно возврата
/// ReturnSaleDialog.ComputeRefundSplit и офлайн-план OfflineRefundPlanner) — правка одной незаметно разводила возврат со связью и без неё.
/// Правило: возвращается пропорционально тому, что по чеку ещё не возвращено наличными и безналом; наличная доля округляется до копеек «от нуля»
/// и не больше самой суммы, безнал — остаток (сумма долей всегда равна сумме возврата). Нечего делить (оба остатка 0) — всё наличными
/// (вызывающий сам решает, помечать ли это «способ неизвестен»). Чистая арифметика: без файлов, окон и сети.</summary>
public static class RefundSplit
{
    /// <summary>Разделить <paramref name="amount"/> по остаткам <paramref name="cashLeft"/> / <paramref name="nonCashLeft"/>. Отрицательные остатки считаются нулём.</summary>
    public static (decimal Cash, decimal NonCash) Split(decimal amount, decimal cashLeft, decimal nonCashLeft)
    {
        var cashPart = Math.Max(0m, cashLeft);
        var nonCashPart = Math.Max(0m, nonCashLeft);
        var total = cashPart + nonCashPart;
        if (total <= 0m)
            return (amount, 0m);
        var cash = Math.Min(amount, Math.Round(amount * cashPart / total, 2, MidpointRounding.AwayFromZero));
        return (cash, amount - cash);
    }

    // 2026-11-06: В10 Л4-16 / АР-1 — функции перенесены из ReturnSaleDialog (Outcome.cs) БЕЗ изменения логики: окно только вызывает их, тесты Wave10 сверяют с копией прежнего кода.
    /// <summary>2026-10-20 (живой аудит, К2, К3): чем вернуть <paramref name="amount"/>. Продажа «в долг» — сначала уменьшается
    /// долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его цифра; без журнала кассы и без движения денег
    /// сервера — 0); смешанная — пропорционально тому, что ещё не возвращено наличными и безналом; без журнала — по способу
    /// оплаты продажи с сервера (<paramref name="fallbackMethod"/>). Чистая функция — проверяется на стенде.</summary>
    /// <param name="serverDebtLeft">2026-10-21 (ревью денег, п. 2): текущий остаток долга по чеку с сервера (null — не получен).
    /// Долг уменьшается не больше него: клиент мог уже погасить долг, тогда возврат выдаётся деньгами.</param>
    public static (decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown, decimal? ServerCashOut, decimal? CalcOverServer) Compute(
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
            cash = RefundSplit.Split(prepaidMoney, cashShareLeft, nonCashShareLeft).Cash;
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
                    (cash, nonCash) = RefundSplit.Split(amount, ledger.CashLeft, ledger.NonCashLeft); // 2026-11-06: В2 Л4-16 — единая формула
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

    /// <summary>Строки чека возврата пропорционально приводятся к сумме сервера (см.
    /// ServerRefundAmountAsync), чтобы сумма строк на бумаге совпадала с итогом.</summary>
    public static List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> ScaleLines(
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
}
