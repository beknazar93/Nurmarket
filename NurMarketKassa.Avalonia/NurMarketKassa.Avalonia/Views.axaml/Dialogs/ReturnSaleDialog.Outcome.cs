using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System;

// 2026-11-06: В10 — часть окна возврата (механический разбор ReturnSaleDialog.axaml.cs, 3146 строк): итог возврата: цифры сервера, касса, разбивка нал/безнал, сверка после ответа. Логика не менялась.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    /// <summary>2026-10-04, стресс-тест на тестовом аккаунте: документ только что оформленного возврата
    /// так, как его записал сервер (GET pos/returns/?sale=…, самый новый), и сколько денег он выдал (движения
    /// «Возврат по чеку»; у продажи «в долг» документ — на весь чек, а выдано только внесённое).
    /// 2026-10-20 (живой аудит, К1–К3): только цифры сервера для сверки — сумму к выдаче считает ResolveRefundAsync.
    /// Документ, уже записанный в журнал прошлым возвратом этой кассы, — не этот возврат (сервер мог не успеть
    /// показать новый). null — сервер не ответил.</summary>
    private static async Task<(decimal? Doc, string? DocId, decimal? CashOut)> ServerReturnFiguresAsync(string? saleId, SaleLedgerEntry? ledger, DateTimeOffset? notBefore = null)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return (null, null, null);
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var returns = await NurCrmReportsApi.ListReturnsAsync(null, null, saleId: saleId, ct: cts.Token).ConfigureAwait(true);
            var seen = ledger?.Refunds.Select(r => r.ServerDocId).Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string?>();
            var newest = returns?
                .Where(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase) && r.Amount > 0m && !seen.Contains(r.Id)
                            // 2026-11-02 (К3-03): документ, созданный задолго до отправки этого возврата, — прошлый возврат, а не этот (минута — запас на часы сервера)
                            && (notBefore is not { } sentAt || r.CreatedAt is not { } created || created >= sentAt - TimeSpan.FromMinutes(1)))
                .OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (newest is null)
                return (null, null, null);
            var cashOut = await NurCrmReportsApi.ReturnCashOutAsync(saleId, newest.CreatedAt, cts.Token).ConfigureAwait(true);
            return (newest.Amount, newest.Id, cashOut);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сумма возврата с сервера не получена, берём расчёт кассы: {ex.Message}", "WARNING");
            return (null, null, null);
        }
    }

    /// <summary>2026-10-20 (живой аудит, К1–К3): что вернули по этому возврату. Amount — стоимость возвращённого (с долей
    /// скидки на чек, не больше остатка оплаченного); Cash/NonCash — что выдать покупателю наличными и на карту/перевод;
    /// DebtReduced — на сколько уменьшился долг клиента (продажа «в долг»). SplitKnown=false — как заплатили, касса не знает
    /// (чек до возвратов не видела, смешанная оплата): показывается одна сумма.</summary>
    /// <param name="DebtUnknown">2026-10-21 (ревью денег, п. 2): продажа «в долг», а текущий остаток долга с сервера не получен —
    /// уменьшение долга посчитано по журналу кассы (оплаты долга он не знает): кассиру — предупреждение, а не «Выдать 0,00».</param>
    /// <param name="ServerOver">2026-10-21 (ревью денег, мелочь 5): на сколько сервер записал возврат больше выданного кассой
    /// (скидка на чек не учтена сервером, ТЗ 19.9) — окно закрытия смены объясняет «излишек» в ящике.</param>
    /// <summary>2026-10-22 (аудит 1.17.59, П-А, Б1): остаток смены на сервере до возврата — сервер не вычитает наличные при
    /// возврате долгового чека, и шапка «Касса» оставалась завышенной (см. ExchangeCashCorrections). null — не узнали.</summary>
    private static async Task<decimal?> ReadDrawerBeforeRefundAsync(bool debtSale = false)
    {
        var shiftId = PosApp.ActiveShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        // 2026-11-01 (финальная приёмка 1.17.73, Ф-2): у долгового чека сервер не вычитает выдачу из expected_cash — поправка ящика нужна, а она считается по остатку «до». Поэтому для долгового
        // чека ждём остаток прежние 6 с (внутренний предел запроса), для остальных — 2,5 с (там ошибки сервера нет, пропуск сверки безвреден).
        // 2026-11-05: при признаке debt_return_drawer_cash сервер сам снимает наличные с долгового чека — сверка не нужна, ждём как у обычного чека.
        var maxWait = debtSale && !ExchangeCashCorrections.DebtRefundHandledByServer ? TimeSpan.FromSeconds(6.5) : TimeSpan.FromMilliseconds(2500);
        // 2026-11-01 (контрольный аудит, К2-12): остаток «до» ждём не дольше 2,5 с (раньше до 6 с перед каждым возвратом). Не успели — null: сверка ящика после возврата пропускается,
        // сам возврат не задерживается (поправка не запишется, но шапку и X/Z можно обновить F5/следующей сверкой).
        var read = ExchangeCashCorrections.ReadExpectedCashAsync(App.GetRequiredService<NurMarketApiClient>(), shiftId);
        var first = await Task.WhenAny(read, Task.Delay(maxWait)).ConfigureAwait(true);
        if (first == read)
            return await read.ConfigureAwait(true);
        PosLogger.Log($"Возврат: остаток смены до возврата не получен за {maxWait.TotalSeconds:0.#} с — сверка ящика после возврата пропускается{(debtSale ? " (долговой чек: проверьте «Касса:» при закрытии смены)" : "")}.", "WARNING");
        return null;
    }

    /// <summary>2026-11-01 (Ф-2): чек «в долг» — выдача по ответу сервера (журнал кассы + движение денег): для поправки ящика нужен остаток смены «до» возврата.</summary>
    private bool IsDebtSaleForDrawer(string saleId)
    {
        var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            ? known
            : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
        var method = (ledger?.Method is { Length: > 0 } m ? m
            : _saleMethods.TryGetValue(saleId, out var sm) ? sm
            : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null) ?? "";
        return ledger is { Debt: > 0.005m } || method.Trim().ToLowerInvariant() == "debt";
    }

    /// <summary>2026-11-01 (Ф-2): остаток смены «до» возврата долгового чека не получен — поправка ящика не записана; кассиру — честное предупреждение (в журнале WARNING есть).</summary>
    private static string DrawerUnverifiedNote(bool debtSale, decimal? cashBefore, decimal cashOut) =>
        debtSale && cashBefore is null && cashOut > 0.005m && !ExchangeCashCorrections.DebtRefundHandledByServer // 2026-11-05: сервер считает сам — предупреждение не нужно
            ? "\n" + Tr.T(
                "Внимание: остаток смены с сервера не получен вовремя — «Касса:» после возврата долгового чека может быть завышена на выданную сумму. Проверьте «Касса:» и пересчёт ящика при закрытии смены.",
                "Көңүл буруңуз: смена калдыгы серверден өз убагында алынган жок — карыз чегин кайтаргандан кийин «Касса:» берилген сумма боюнча жогору көрүнүшү мүмкүн. Смена жабылганда «Касса:» жана кассаны кайра санаңыз.",
                "Warning: the shift balance was not received from the server in time — “Till” may be overstated by the amount paid out after a credit-receipt return. Check “Till” and the drawer count when closing the shift.",
                "Dikkat: vardiya bakiyesi sunucudan zamanında alınamadı — veresiye fişi iadesinden sonra «Kasa» verilen tutar kadar fazla görünebilir. Vardiyayı kapatırken «Kasa»yı ve çekmece sayımını kontrol edin.",
                "Diqqat: smena qoldig'i serverdan o'z vaqtida olinmadi — qarz cheki qaytarilgandan keyin «Kassa» berilgan summaga ortiq ko'rinishi mumkin. Smenani yopishda «Kassa»ni va qutini qayta sanashni tekshiring.")
            : "";

    /// <summary>Сверка после возврата: наличные ушли из ящика на <paramref name="cashOut"/>, а сервер остаток не уменьшил — поправка.</summary>
    /// <param name="serverCashOut">2026-11-01 (приёмка, Ж-01): сколько наличных выдачи записал сервер, если меньше выданного кассой (касса выдала по оплатам клиента, сервер
    /// — по движениям денег: чек 001967, касса 2, сервер 1). Остаток смены на сервере тогда завышен на разницу — поправка пишется на неё, ящик сходится.</param>
    private static void CheckDrawerAfterRefund(decimal? before, decimal cashOut, string key, string? saleId = null, decimal nonCash = 0m, decimal? serverCashOut = null, bool debtSale = false)
    {
        var shiftId = PosApp.ActiveShiftId;
        // 2026-10-23 (живой аудит, Б-1): долговой чек с безналичной предоплатой — наличные не уходят (cashOut 0), но сервер
        // вычитает предоплату из остатка смены и старая поправка предоплаты становится лишней: снимаем её, если остаток
        // на сервере действительно уменьшился на безнал.
        if (before is { } b0 && cashOut <= 0m && nonCash > 0m && !string.IsNullOrWhiteSpace(shiftId) && !string.IsNullOrWhiteSpace(saleId))
        {
            var client0 = App.GetRequiredService<NurMarketApiClient>();
            _ = Task.Run(async () =>
            {
                var after0 = await ExchangeCashCorrections.ReadExpectedCashAsync(client0, shiftId).ConfigureAwait(false);
                if (after0 is { } a0 && Math.Abs((a0 - b0) + nonCash) < 0.01m)
                    ExchangeCashCorrections.RemoveDebtPrepay(shiftId, saleId);
            });
            return;
        }
        if (before is null || cashOut <= 0m || string.IsNullOrWhiteSpace(shiftId))
            return;
        var client = App.GetRequiredService<NurMarketApiClient>();
        _ = Task.Run(async () =>
        {
            var after = await ExchangeCashCorrections.ReadExpectedCashAsync(client, shiftId).ConfigureAwait(false);
            if (after is { } a)
            {
                // 2026-11-05 (сервер ceba67c, debt_return_drawer_cash): долговой чек — наличные снял сервер, наша поправка не пишется и не применяется (иначе вычтется дважды).
                if (debtSale && ExchangeCashCorrections.DebtRefundHandledByServer)
                {
                    ExchangeCashCorrections.NoteServerFeatureDebtRefund(shiftId, cashOut);
                    // 2026-11-06 (аудит №5, Л5-05, Ж-01): сервер снимает свою запись выдачи; если касса выдала больше (чек 001967: 2 против 1), ящик расходится на разницу — поправка как раньше.
                    ExchangeCashCorrections.RecordServerUnderRecordedDebtRefund(shiftId, key, before.Value, a, cashOut, serverCashOut);
                    return;
                }
                var overstated = (a - before.Value) + cashOut;
                // 2026-11-05 (приёмка волн 0–3, С-1): долговой чек, а сервер сам вычел выданные наличные — ошибку сервера исправили; новые поправки не пишутся (проверка ниже)
                // и это попадает в журнал один раз, чтобы поправки текущей смены не пережили исправление незамеченными.
                if (debtSale && cashOut > 0.005m && Math.Abs(overstated) < 0.01m)
                    ExchangeCashCorrections.NoteServerSubtractsDebtRefund(shiftId, cashOut);
                // 2026-11-01 (приёмка, Ж-01): сервер вычел из ящика меньше, чем выдала касса, — ожидаемое завышение = разница; иначе прежняя проверка (сервер не вычел ничего).
                var suspect = cashOut;
                if (serverCashOut is { } serverGave && serverGave < cashOut - 0.009m)
                {
                    // 2026-11-01 (code-review, Ж-01): поправка — по факту выданных денег (завышение остатка сервера), а не по точному совпадению с записью сервера; допуск 0,01 — только округление.
                    // Правдоподобное завышение (0 < x ≤ выдано) пишется как есть; расхождение с ожидаемым (выдано − записано сервером) — в журнал.
                    var expectedGap = cashOut - serverGave;
                    if (overstated > 0.009m && overstated <= cashOut + 0.01m)
                    {
                        suspect = overstated;
                        if (Math.Abs(overstated - expectedGap) > 0.01m)
                            PosLogger.Log($"Возврат (наличные): остаток смены на сервере завышен на {overstated:0.00}, ожидалось {expectedGap:0.00} (выдано {cashOut:0.00}, сервер записал {serverGave:0.00}) — поправка по факту.", "WARNING");
                    }
                    else
                        PosLogger.Log($"Возврат (наличные): остаток смены разошёлся на {overstated:0.00} при выдаче {cashOut:0.00} (сервер записал {serverGave:0.00}) — поправка не записана, сверить смену вручную.", "WARNING");
                }
                ExchangeCashCorrections.RecordIfKnownServerError(shiftId, "refund:" + key, overstated, suspect, "Возврат (наличные)", debtRefund: debtSale);
            }
        });
    }

    /// <param name="ServerCashOut">2026-11-01 (аудит «10 из 10», Ж-01): сколько выдачи записал сервер, если оно не совпало с положенным по оплатам клиента
    /// (предоплата + оплаты долга, текущий долг) — кассиру показывается сумма и причина расхождения; null — совпало или сервер не сказал.</param>
    /// <param name="CalcOverServer">2026-11-01 (code-review, Ж-01): расчёт по журналу кассы был выше цифры сервера и не объяснён оплатами на этой кассе — выдана меньшая (сервера), это расчёт кассы.</param>
    private sealed record RefundOutcome(decimal Amount, decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown = false,
        decimal ServerOver = 0m, decimal? ServerCashOut = null, decimal? CalcOverServer = null, decimal? DebtRemaining = null, decimal Balance = 0m, decimal? ServerRejected = null, decimal? ServerDiffersCalc = null)
    {
        public decimal Money => Cash + NonCash;

        /// <summary>Примечание события смены (ShiftEventsStore): разбивка для окна закрытия смены.</summary>
        public string Note => $"cash={SaleReturnLedger.Inv(Cash)};noncash={SaleReturnLedger.Inv(NonCash)};debt={SaleReturnLedger.Inv(DebtReduced)}"
                              + (ServerOver > 0.005m ? $";server_over={SaleReturnLedger.Inv(ServerOver)}" : "")
                              + (ServerCashOut is { } under && under < Cash - 0.005m ? $";server_under={SaleReturnLedger.Inv(Cash - under)}" : ""); // 2026-11-01 (Ж-01): сервер выдал меньше — окно закрытия смены объяснит разницу
    }

    /// <summary>2026-10-20 (живой аудит, К1–К3). <paramref name="expected"/> — расчёт кассы (доля строк со скидкой на чек,
    /// не больше остатка оплаченного). Сервер вернул меньше — берём его (денег ушло меньше); больше (скидка на чек не
    /// учтена: 001837 — 80 из 75) — его число кассиру не показываем: журнал (WARNING) и «Некорректные чеки». Деньги:
    /// у продажи «в долг» сначала уменьшается долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его
    /// цифра); у смешанной — пропорционально тому, что ещё не возвращено наличными и безналом.</summary>
    /// <param name="figures">2026-11-01 (ответ Умара, п. 3): цифры из ответа сервера на возврат — выдача наличными в <c>refund.cash</c> (не <c>debt_adjustment.reduced_by</c>, это уменьшение долга).
    /// Есть <c>refund.cash</c> — сервер называет выдачу, касса ему доверяет (без «Расчёт кассы X, сервер Y» и поправок ящика); нет (старый сервер, ответ не дошёл) — прежний расчёт.</param>
    private async Task<RefundOutcome> ResolveRefundAsync(string saleId, decimal expected, bool whole, List<SaleLedgerLine> lines, ServerRefundFigures? figures = null)
    {
        // 2026-10-21 (ревью денег, мелочь 4): журнал и способ оплаты — именно этого чека, даже если кассир уже открыл другой.
        var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            ? known
            : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
        var saleMethod = _saleMethods.TryGetValue(saleId, out var m) ? m
            : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null;
        // 2026-11-01 (К2-12): сервер назвал выдачу (refund.cash) — документ возврата и движения нужны только для сверки и журнала: ждём их не дольше 3 с.
        // Старый сервер (цифры выдачи только в документе) — как раньше, без ограничения.
        (decimal? Doc, string? DocId, decimal? CashOut) serverFigures = (null, null, null);
        if (figures is { HasCash: true, RefundId: { Length: > 0 } responseRefundId })
        {
            // 2026-11-02 (аудит №2, К3-03): сервер прислал id и сумму документа возврата в ответе — берём их, список возвратов не запрашиваем (быстрее, и устаревший документ не подставится).
            serverFigures = (figures.RefundAmount, responseRefundId, null);
        }
        else
        {
            var figuresTask = ServerReturnFiguresAsync(saleId, ledger, _refundSentAt == default ? null : _refundSentAt);
            if (figures is { HasCash: true })
            {
                var first = await Task.WhenAny(figuresTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
                if (first == figuresTask)
                    serverFigures = await figuresTask.ConfigureAwait(true);
                else
                    PosLogger.Log($"Возврат по чеку {saleId}: документ возврата не получен за 3 с — итог по ответу сервера (refund.cash); проверить возврат в «Истории чеков».", "WARNING");
            }
            else
                serverFigures = await figuresTask.ConfigureAwait(true);
        }
        var (doc, docId, cashOut) = serverFigures;
        // 2026-10-21 (ревью денег, п. 2): остаток долга по чеку с сервера (запрошен при открытии чека, до возврата).
        decimal? serverDebtLeft = null;
        if (_serverDebtLeft.Remove(saleId, out var debtLookup))
            serverDebtLeft = await debtLookup.ConfigureAwait(true);
        var amount = Math.Max(0m, expected);
        var serverOver = 0m;
        var number = DisplaySaleNumber(saleId);
        if (doc is { } server)
        {
            if (server < amount - 0.009m)
            {
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер вернул {server:0.00}, касса насчитала {amount:0.00} — берём сумму сервера.", "WARNING");
                amount = server;
            }
            else if (server > amount + 0.009m)
            {
                serverOver = server - amount;
                var paid = ledger is null ? "" : $", оплачено {MoneyText.Amount(ledger.Total)}, возвращено раньше {MoneyText.Amount(ledger.Refunded)}";
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер вернул {server:0.00} — больше, чем положено ({amount:0.00}{paid}; скидка на чек не учтена сервером). Кассиру показана сумма кассы.", "WARNING");
                try
                {
                    IrregularReceiptsStore.Append(new IrregularReceiptEntry
                    {
                        Tag = IrregularReceiptEntry.ReturnOverpaid,
                        CashierName = PosApp.CurrentUserDisplayName,
                        Note = Tr.T(
                            $"Возврат по чеку {number}: сервер записал {MoneyText.Som(server)}, положено {MoneyText.Som(amount)}{(ledger is null ? "" : $" (оплачено {MoneyText.Som(ledger.Total)})")}. Покупателю выдано по кассе.",
                            $"{number} чеги боюнча кайтаруу: сервер {MoneyText.Som(server)} жазды, туурасы {MoneyText.Som(amount)}{(ledger is null ? "" : $" (төлөнгөн {MoneyText.Som(ledger.Total)})")}. Сатып алуучуга касса боюнча берилди.",
                            $"Return for receipt {number}: the server recorded {MoneyText.Som(server)}, due {MoneyText.Som(amount)}{(ledger is null ? "" : $" (paid {MoneyText.Som(ledger.Total)})")}. The customer got the till's amount.",
                            $"{number} fişi iadesi: sunucu {MoneyText.Som(server)} kaydetti, olması gereken {MoneyText.Som(amount)}{(ledger is null ? "" : $" (ödenen {MoneyText.Som(ledger.Total)})")}. Müşteriye kasanın tutarı verildi.",
                            $"{number} chek bo'yicha qaytarish: server {MoneyText.Som(server)} yozdi, to'g'risi {MoneyText.Som(amount)}{(ledger is null ? "" : $" (to'langan {MoneyText.Som(ledger.Total)})")}. Xaridorga kassa summasi berildi."),
                        Total = (double)(server - amount),
                        CartJson = "{}",
                    });
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Некорректный возврат не записан в журнал: {ex.Message}", "WARNING");
                }
            }
        }

        // 2026-11-01 (code-review, Ж-01): сколько оплат долга по этому клиенту записано на этой кассе — объясняет ли оно расхождение расчёта и цифры сервера.
        var localDebtPaid = LocalDebtPaidHere(saleId, ledger, serverDebtLeft, cashOnly: false);
        var localDebtCash = LocalDebtPaidHere(saleId, ledger, serverDebtLeft, cashOnly: true);
        var (cash, nonCash, debtReduced, splitKnown, debtUnknown, serverCashOut, calcOverServer) = RefundSplit.Compute(ledger, saleMethod, amount, cashOut, serverDebtLeft, localDebtPaid, localDebtCash);
        decimal? debtRemaining = null;
        var balancePart = 0m;
        decimal? rejectedServerMoney = null;
        decimal? serverDiffersCalc = null; // расчёт кассы, если выдача по серверу допустима, но отличается от суммы возврата
        if (figures is { HasCash: true } trusted)
        {
            // 2026-11-01 (ответ Умара, п. 3): сервер назвал выдачу — доверяем ему, НО только если цифры правдоподобны (приёмка 1.17.72, В-1). Расчёт кассы остаётся для блока
            // «К выдаче (ожидаемо)» ДО проведения, для старых серверов и как запасной вариант, когда ответ сервера не сходится с суммой возврата.
            var local = RefundSplit.Compute(ledger, saleMethod, amount, null, serverDebtLeft, null, localDebtCash);
            var methodNow = (ledger?.Method is { Length: > 0 } knownMethodNow ? knownMethodNow : saleMethod ?? "").Trim().ToLowerInvariant();
            var debtSaleNow = ledger is { Debt: > 0.005m } || methodNow == "debt";
            var rawCash = trusted.Cash!.Value;
            var srvCash = Math.Max(0m, rawCash);
            var srvBalance = Math.Max(0m, trusted.Balance ?? 0m);
            decimal srvNon, srvDebt;
            bool plausible;
            if (!debtSaleNow)
            {
                // Продажа не в долг. Цифру сервера отбрасываем ТОЛЬКО при явной нелепости (контрольное ревью): наличные < 0, наличные больше суммы возврата, либо чек оплачен
                // только наличными, а сервер говорит «наличных 0» при положительной сумме. Расхождение суммы частей с суммой возврата (чек оплачен бонусами, часть возвращена
                // на другой кассе/сайте) — не повод отбрасывать: выдаём по серверу и предупреждаем. Безнал без refund.noncash — доля расчёта кассы по способу оплаты чека, не «сумма − наличные».
                srvNon = Math.Max(0m, trusted.NonCash ?? local.NonCash);
                // 2026-11-01 (контрольный аудит, К2-04): наличные + безнал + «на баланс» не больше суммы возврата (допуск 0,01). Безнал из расчёта кассы (сервер его не назвал)
                // урезается до остатка суммы; названный сервером безнал сверх суммы — цифра нелепа, берём расчёт кассы.
                if (trusted.NonCash is null && srvCash + srvNon + srvBalance > amount + 0.01m)
                    srvNon = Math.Max(0m, amount - srvCash - srvBalance);
                srvDebt = 0m;
                plausible = rawCash >= -0.005m && rawCash <= amount + 0.01m
                            && srvCash + srvNon + srvBalance <= amount + 0.01m
                            && !(methodNow == "cash" && srvCash <= 0.005m && amount > 0.005m);
            }
            else
            {
                // Чек в долг: 0 ≤ выдача ≤ сумма возврата − уменьшение долга (если сервер его назвал).
                srvNon = Math.Max(0m, trusted.NonCash ?? (local.DebtUnknown ? 0m : local.NonCash));
                srvDebt = trusted.DebtReducedBy is { } reducedBy ? Math.Max(0m, reducedBy) : Math.Max(0m, amount - srvCash - srvNon - srvBalance);
                plausible = rawCash >= -0.005m && srvCash + srvNon + srvBalance <= amount - (trusted.DebtReducedBy is not null ? srvDebt : 0m) + 0.01m
                            // 2026-11-02 (аудит №2, К3-04): сервер не прислал debt_adjustment — выдача не больше того, что покупатель реально внёс по расчёту кассы (при известном остатке долга)
                            && (trusted.DebtReducedBy is not null || local.DebtUnknown || srvCash + srvNon <= local.Cash + local.NonCash + 0.01m);
            }
            if (plausible)
            {
                cash = srvCash;
                nonCash = srvNon;
                balancePart = srvBalance;
                debtReduced = srvDebt;
                // «Остаток долга» — только из ответа на ЭТОТ возврат и если чек не возвращён целиком (у full сервер оставляет старый остаток; фактически долг по чеку 0).
                debtRemaining = CartDisplayHelper.IsFullyReturnedStatus(trusted.ReturnStatus) ? null : trusted.RemainingDebt;
                splitKnown = true;
                debtUnknown = false;
                serverCashOut = null;
                calcOverServer = null;
                if (!local.DebtUnknown && Math.Abs(local.Cash + local.NonCash - (cash + nonCash)) > 0.009m)
                {
                    PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер (refund.cash) выдаёт {cash + nonCash:0.00}, расчёт кассы был {local.Cash + local.NonCash:0.00} — берём сервер.", "SALES");
                    // Не-долговой чек: сумма выдачи сервера не равна сумме возврата — это допустимо, но кассиру «Сервер выдаёт X, расчёт кассы Y — проверьте».
                    if (!debtSaleNow && Math.Abs(cash + nonCash + balancePart - amount) > 0.01m)
                        serverDiffersCalc = local.Cash + local.NonCash;
                }
            }
            else
            {
                rejectedServerMoney = srvCash + srvNon + srvBalance;
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): ответ сервера не сходится с суммой возврата {amount:0.00} (наличными {srvCash:0.00}, безнал {srvNon:0.00}, на баланс {srvBalance:0.00}, долг −{srvDebt:0.00}) — "
                              + $"используется расчёт кассы {cash + nonCash:0.00}, проверить возврат.", "WARNING");
            }
        }
        if (calcOverServer is { } calcShown)
            PosLogger.Log($"Возврат по чеку {number} ({saleId}): расчёт по журналу кассы {calcShown:0.00}, сервер {cash + nonCash:0.00}; оплаты долга на этой кассе ({localDebtPaid:0.00}) разницу не объясняют — выдаётся меньшая, проверить долг клиента.", "WARNING");
        if (serverCashOut is { } serverGave)
            PosLogger.Log($"Возврат по чеку {number} ({saleId}): по оплатам клиента положено выдать {cash + nonCash:0.00} (долг сейчас {serverDebtLeft ?? 0m:0.00}, долг уменьшен на {debtReduced:0.00}), сервер записал выдачу {serverGave:0.00} — кассиру показана сумма по оплатам.", "WARNING");
        if (debtUnknown && debtReduced > 0.005m)
            PosLogger.Log($"Возврат по чеку {number}: остаток долга с сервера неизвестен — кассиру показано предупреждение проверить долг клиента (по кассе долг −{debtReduced:0.00}).", "WARNING");

        if (cashOut is { } flows && Math.Abs(flows - (cash + nonCash)) > 0.009m)
            PosLogger.Log($"Возврат по чеку {number}: по движениям денег сервера выдано {flows:0.00}, по кассе к выдаче {cash + nonCash:0.00} (наличными {cash:0.00}, безнал {nonCash:0.00}, долг −{debtReduced:0.00}).", "WARNING");

        if (ledger is not null)
        {
            SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund
            {
                At = DateTimeOffset.Now,
                Amount = amount,
                Cash = cash,
                NonCash = nonCash,
                DebtReduced = debtReduced,
                ServerAmount = doc,
                ServerDocId = docId,
                Source = SaleLedgerRefund.SourceReturn, // 2026-11-13: обычный возврат — сверка с сервером (CustomerReturnReconciler) может поправить его сумму
                Whole = whole,
                Lines = lines,
            });
            if (string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase))
                _ledger = SaleReturnLedger.Get(saleId);
        }

        PosLogger.Log($"Возврат по чеку {number}: {amount:0.00} (наличными {cash:0.00}, безнал {nonCash:0.00}, долг −{debtReduced:0.00}); документ сервера {(doc is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "—")}.", "SALES");
        return new RefundOutcome(amount, cash, nonCash, debtReduced, splitKnown, debtUnknown, serverOver, serverCashOut, calcOverServer, debtRemaining, balancePart, rejectedServerMoney, serverDiffersCalc);
    }

    /// <summary>2026-11-01 (приёмка 1.17.72, С-2): сколько оплат долга по ЭТОМУ чеку записано на этой кассе. Журнал смены хранит имя клиента, а не id сделки/чека, поэтому
    /// берутся только оплаты этого клиента не раньше даты продажи чека и не больше, чем сервер считает погашенным по сделке (журнальный долг чека − текущий остаток).
    /// Нет даты продажи, журнала кассы, имени или остатка долга сервера — 0 (не подтверждено: нейтральная пометка «возвращайте тем же способом»).</summary>
    private decimal LocalDebtPaidHere(string saleId, SaleLedgerEntry? ledger, decimal? serverDebtLeft, bool cashOnly)
    {
        try
        {
            var since = string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleDate : null;
            since ??= ledger?.SoldAt?.LocalDateTime;
            if (since is null || ledger is null || serverDebtLeft is not { } debtNow
                || !_saleClientNames.TryGetValue(saleId, out var name) || string.IsNullOrWhiteSpace(name))
                return 0m;
            var paidOnDeal = Math.Max(0m, ledger.DebtLeft - Math.Max(0m, debtNow));
            if (paidOnDeal <= 0.005m)
                return 0m;
            var sinceUtc = since.Value.Kind == DateTimeKind.Utc ? since.Value : since.Value.ToUniversalTime();
            var recorded = (decimal)ShiftEventsStore.DebtPaidByClientName(name, cashOnly, sinceUtc);
            return Math.Min(recorded, paidOnDeal);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: оплаты долга на этой кассе не подсчитаны: {ex.Message}", "WARNING");
            return 0m;
        }
    }

    /// <summary>2026-11-01 (живая регрессия, Р-5): подпись кнопки подтверждения. Сумма чека/возврата и сумма К ВЫДАЧЕ — разные вещи: если к выдаче иная сумма — «Вернуть весь чек (к выдаче 2,00)»,
    /// если сумму определит сервер — без суммы; иначе прежняя подпись с суммой возврата.</summary>
    private string ConfirmButtonText(string saleId, decimal amount, bool whole, string amountShown)
    {
        if (_expectedPayouts.TryGetValue(saleId, out var payout))
        {
            if (payout is null)
                return whole
                    ? Tr.T("Вернуть весь чек", "Бүт чекти кайтаруу", "Refund whole receipt", "Tüm fişi iade et", "Butun chekni qaytarish")
                    : Tr.T("Вернуть выбранное", "Тандалганды кайтаруу", "Refund selected", "Seçilenleri iade et", "Tanlanganlarni qaytarish");
            if (Math.Abs(payout.Value - amount) > 0.009m)
            {
                var give = MoneyText.Som(payout.Value);
                return whole
                    ? Tr.T($"Вернуть весь чек (к выдаче {give})", $"Бүт чекти кайтаруу (берүүгө {give})", $"Refund whole receipt (pay out {give})", $"Tüm fişi iade et (verilecek {give})", $"Butun chekni qaytarish (berish {give})")
                    : Tr.T($"Вернуть (к выдаче {give})", $"Кайтаруу (берүүгө {give})", $"Refund (pay out {give})", $"İade et (verilecek {give})", $"Qaytarish (berish {give})");
            }
        }
        return whole
            ? Tr.T($"Вернуть весь чек — {amountShown}", $"Бүт чекти кайтаруу — {amountShown}", $"Refund whole receipt — {amountShown}", $"Tüm fişi iade et — {amountShown}", $"Butun chekni qaytarish — {amountShown}")
            : Tr.T($"Вернуть {amountShown}", $"{amountShown} кайтаруу", $"Refund {amountShown}", $"{amountShown} iade et", $"{amountShown} qaytarish");
    }

    /// <summary>2026-11-01 (Ж-08): забирает «к выдаче», показанное в подтверждении (один раз).</summary>
    private decimal? TakeExpectedPayout(string saleId) => _expectedPayouts.Remove(saleId, out var v) ? v : null;

    /// <summary>2026-11-01 (Ж-08, владелец): до отправки возврата — «К выдаче (ожидаемо): наличными A, на карту B; долг клиента уменьшится на C; бонусы −D». Тот же расчёт, что после
    /// ответа сервера (<see cref="RefundSplit.Compute"/>), но без цифры сервера: журнал кассы, способ оплаты чека, остаток долга сделки (запрошен при открытии чека; ждём не дольше
    /// 300 мс — иначе считаем «неизвестен»). Данных не хватает (нет журнала кассы или остатка долга у долгового чека) — «сумму определит сервер» и предупреждение проверить долг клиента.
    /// Кнопка подтверждения расчётом не блокируется. Возвращает текст с начальным переводом строки или "".</summary>
    private async Task<string> ExpectedPayoutTextAsync(string saleId, decimal amount, bool reversesLoyalty)
    {
        _expectedPayouts.Remove(saleId);
        try
        {
            var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
                ? known
                : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
            var method = _saleMethods.TryGetValue(saleId, out var m) ? m
                : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null;
            decimal? debtLeft = null;
            if (_serverDebtLeft.TryGetValue(saleId, out var debtTask))
            {
                if (!debtTask.IsCompleted)
                    await Task.WhenAny(debtTask, Task.Delay(300)).ConfigureAwait(true);
                if (debtTask.IsCompletedSuccessfully)
                    debtLeft = debtTask.Result;
            }

            var previewCashPaid = LocalDebtPaidHere(saleId, ledger, debtLeft, cashOnly: true);
            var (cash, nonCash, debtReduced, splitKnown, debtUnknown, _, _) = RefundSplit.Compute(ledger, method, Math.Max(0m, amount), null, debtLeft, null, previewCashPaid);
            var lead = "\n\n";
            if (debtUnknown)
            {
                _expectedPayouts[saleId] = null;
                return lead + Tr.T(
                    "К выдаче: сумму определит сервер — остаток долга клиента или оплаты чека кассе неизвестны. Проверьте долг клиента перед выдачей.",
                    "Берүүгө: сумманы сервер аныктайт — кардардын карызынын калдыгы же чектин төлөмдөрү кассага белгисиз. Берүүдөн мурун кардардын карызын текшериңиз.",
                    "To pay out: the server will determine the amount — the client's remaining debt or the receipt's payments are unknown to the till. Check the client's debt before paying out.",
                    "Verilecek: tutarı sunucu belirler — müşterinin kalan borcu veya fişin ödemeleri kasaya bilinmiyor. Vermeden önce müşterinin borcunu kontrol edin.",
                    "Berish: summani server aniqlaydi — mijoz qarzining qoldig'i yoki chek to'lovlari kassaga noma'lum. Berishdan oldin mijoz qarzini tekshiring.");
            }

            _expectedPayouts[saleId] = cash + nonCash;
            var parts = new List<string>();
            if (cash > 0.005m)
                parts.Add(Tr.T("наличными ", "накталай ", "in cash ", "nakit ", "naqd ") + MoneyText.Som(cash));
            if (nonCash > 0.005m)
                parts.Add(Tr.T("на карту/перевод ", "картага/которуу менен ", "to the card/transfer ", "karta/havale ile ", "kartaga/o'tkazma bilan ") + MoneyText.Som(nonCash));
            var give = !splitKnown ? MoneyText.Som(cash + nonCash) : parts.Count == 0 ? MoneyText.Som(0m) : string.Join(" · ", parts);
            var text = lead + Tr.T($"К выдаче (ожидаемо): {give}.", $"Берүүгө (күтүлүүдө): {give}.", $"To pay out (expected): {give}.", $"Verilecek (beklenen): {give}.", $"Berish (kutilmoqda): {give}.");
            if (!splitKnown)
                text += " " + Tr.T("Как заплатил покупатель, кассе неизвестно — возвращайте тем же способом.", "Сатып алуучу кандай төлөгөнү кассага белгисиз — ошол эле жол менен кайтарыңыз.",
                    "The till does not know how the customer paid — refund the same way.", "Müşterinin nasıl ödediği kasaya bilinmiyor — aynı yolla iade edin.", "Xaridor qanday to'lagani kassaga noma'lum — shu usulda qaytaring.");
            if (debtReduced > 0.005m)
            {
                var debt = MoneyText.Som(debtReduced);
                text += "\n" + Tr.T($"Долг клиента уменьшится на {debt} — эти деньги не выдаются.", $"Кардардын карызы {debt} азаят — бул акча берилбейт.", $"The client's debt will be reduced by {debt} — this money is not paid out.",
                    $"Müşterinin borcu {debt} azalacak — bu para verilmez.", $"Mijozning qarzi {debt} ga kamayadi — bu pul berilmaydi.");
            }
            if (reversesLoyalty && ClientLoyaltyStore.RemainingForSale(saleId) is var points && Math.Abs(points) >= 0.005)
            {
                var p = Math.Abs(points).ToString("0.##", CultureInfo.InvariantCulture);
                text += "\n" + (points > 0
                    ? Tr.T($"Бонусы клиента: −{p} (начисленные за этот чек будут сняты).", $"Кардардын бонустары: −{p} (бул чек үчүн эсептелгендер алынат).", $"Client bonuses: −{p} (those earned for this receipt will be removed).",
                        $"Müşteri bonusları: −{p} (bu fiş için kazanılanlar silinecek).", $"Mijoz bonuslari: −{p} (bu chek uchun hisoblanganlar olinadi).")
                    : Tr.T($"Бонусы клиента: +{p} (списанные на оплату будут возвращены).", $"Кардардын бонустары: +{p} (төлөмгө жумшалгандар кайтарылат).", $"Client bonuses: +{p} (those spent on this receipt will be returned).",
                        $"Müşteri bonusları: +{p} (bu fişte harcananlar iade edilecek).", $"Mijoz bonuslari: +{p} (to'lovga sarflanganlar qaytariladi)."));
            }
            return text;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: «к выдаче» до отправки не посчитано: {ex.Message}", "WARNING");
            return "";
        }
    }

    /// <summary>2026-10-04: строка «Выдать покупателю: N сом» в сообщении об оформленном возврате.
    /// 2026-10-20 (живой аудит, К2, К3): только внесённые деньги — с разбивкой «наличными · на карту/перевод»; у продажи
    /// «в долг» — «Выдать: 0,00 · долг клиента уменьшен на 15,00».</summary>
    private static string RefundToGiveText(decimal amount) => Tr.T(
        $"Выдать покупателю: {MoneyText.Som(amount)}.",
        $"Сатып алуучуга берүү: {MoneyText.Som(amount)}.",
        $"Give the customer: {MoneyText.Som(amount)}.",
        $"Müşteriye verilecek: {MoneyText.Som(amount)}.",
        $"Xaridorga berish: {MoneyText.Som(amount)}.");

    private static string RefundToGiveText(RefundOutcome outcome, decimal? expectedBefore = null)
    {
        var parts = new List<string>();
        if (outcome.Cash > 0.005m)
            parts.Add(Tr.T("наличными ", "накталай ", "in cash ", "nakit ", "naqd ") + MoneyText.Som(outcome.Cash));
        if (outcome.NonCash > 0.005m)
            parts.Add(Tr.T("на карту/перевод ", "картага/которуу менен ", "to the card/transfer ", "karta/havale ile ", "kartaga/o'tkazma bilan ") + MoneyText.Som(outcome.NonCash));
        var give = !outcome.SplitKnown
            ? MoneyText.Som(outcome.Money)
            : parts.Count == 0 ? MoneyText.Som(0m) : string.Join(" · ", parts);
        var text = Tr.T($"Выдать покупателю: {give}.", $"Сатып алуучуга берүү: {give}.", $"Give the customer: {give}.",
            $"Müşteriye verilecek: {give}.", $"Xaridorga berish: {give}.");
        if (outcome.DebtReduced > 0.005m && outcome.DebtUnknown)
        {
            // 2026-10-21 (ревью денег, п. 2): остаток долга с сервера не получен — не уверенное «долг уменьшен», а проверка.
            var debt = MoneyText.Som(outcome.DebtReduced);
            text += "\n" + Tr.T(
                $"Внимание: остаток долга клиента с сервера не получен. По кассе долг уменьшен на {debt}, но если клиент уже погасил долг по этому чеку — ему нужно вернуть до {debt}. Проверьте долг клиента («Клиенты» → долги) перед выдачей.",
                $"Көңүл буруңуз: кардардын карызынын калдыгы серверден алынган жок. Касса боюнча карыз {debt} азайды, бирок кардар бул чек боюнча карызын төлөп койгон болсо — ага {debt} чейин кайтаруу керек. Берүүдөн мурун кардардын карызын текшериңиз («Кардарлар» → карыздар).",
                $"Warning: the client's remaining debt was not received from the server. By the till the debt is reduced by {debt}, but if the client has already paid off this receipt's debt, up to {debt} must be refunded. Check the client's debt (“Clients” → debts) before paying out.",
                $"Dikkat: müşterinin kalan borcu sunucudan alınamadı. Kasaya göre borç {debt} azaldı, ancak müşteri bu fişin borcunu zaten ödediyse kendisine {debt} tutarına kadar iade edilmeli. Ödemeden önce müşterinin borcunu kontrol edin («Müşteriler» → borçlar).",
                $"Diqqat: mijoz qarzining qoldig'i serverdan olinmadi. Kassa bo'yicha qarz {debt} ga kamaydi, lekin mijoz bu chek bo'yicha qarzini to'lab bo'lgan bo'lsa — unga {debt} gacha qaytarish kerak. Berishdan oldin mijoz qarzini tekshiring («Mijozlar» → qarzlar).");
        }
        else if (outcome.DebtReduced > 0.005m)
        {
            var debt = MoneyText.Som(outcome.DebtReduced);
            text += "\n" + Tr.T($"Долг клиента уменьшен на {debt} — эти деньги не выдаются.",
                $"Кардардын карызы {debt} азайды — бул акча берилбейт.",
                $"The client's debt is reduced by {debt} — this money is not paid out.",
                $"Müşterinin borcu {debt} azaldı — bu para verilmez.",
                $"Mijozning qarzi {debt} ga kamaydi — bu pul berilmaydi.");
        }
        if (outcome.DebtReduced > 0.005m && outcome.DebtRemaining is { } debtLeftNow && !outcome.DebtUnknown)
        {
            // 2026-11-01 (ответ Умара, п. 3): сервер называет остаток долга после возврата (debt_adjustment.remaining_debt).
            var left = MoneyText.Som(debtLeftNow);
            text += " " + Tr.T($"Остаток долга: {left}.", $"Карыздын калдыгы: {left}.", $"Remaining debt: {left}.", $"Kalan borç: {left}.", $"Qarz qoldig'i: {left}.");
        }
        if (outcome.Balance > 0.005m)
        {
            var onBalance = MoneyText.Som(outcome.Balance);
            text += "\n" + Tr.T($"На баланс клиента: {onBalance}.", $"Кардардын балансына: {onBalance}.", $"To the client's balance: {onBalance}.", $"Müşteri bakiyesine: {onBalance}.", $"Mijoz balansiga: {onBalance}.");
        }
        if (outcome.ServerDiffersCalc is { } calcDiffers)
        {
            // Контрольное ревью: выдача по серверу допустима, но не равна сумме возврата / расчёту кассы (чек частично оплачен бонусами, часть возвращена на другой кассе или сайте).
            var srvGives = MoneyText.Som(outcome.Money);
            var calcGives = MoneyText.Som(calcDiffers);
            text += "\n" + Tr.T(
                $"Внимание: сервер выдаёт {srvGives}, расчёт кассы {calcGives} — проверьте. Выдайте {srvGives} (по серверу).",
                $"Көңүл буруңуз: сервер {srvGives} берет, касса эсеби {calcGives} — текшериңиз. {srvGives} бериңиз (сервер боюнча).",
                $"Warning: the server pays out {srvGives}, the till's calculation is {calcGives} — please check. Pay out {srvGives} (per the server).",
                $"Dikkat: sunucu {srvGives} veriyor, kasa hesabı {calcGives} — kontrol edin. {srvGives} verin (sunucuya göre).",
                $"Diqqat: server {srvGives} beradi, kassa hisobi {calcGives} — tekshiring. {srvGives} bering (server bo'yicha).");
        }
        if (outcome.ServerRejected is { } rejectedShown)
        {
            // 2026-11-01 (приёмка 1.17.72, В-1): цифры сервера не сходятся с суммой возврата — выдаётся расчёт кассы, кассиру — предупреждение.
            var calcMoney = MoneyText.Som(outcome.Money);
            var srvMoney = MoneyText.Som(rejectedShown);
            text += "\n" + Tr.T(
                $"Внимание: расчёт кассы {calcMoney}, сервер {srvMoney} — проверьте. Выдаётся расчёт кассы; сверьте возврат в «Возвратах» и долг клиента.",
                $"Көңүл буруңуз: касса эсеби {calcMoney}, сервер {srvMoney} — текшериңиз. Касса эсеби берилет; кайтарууну «Кайтаруулардан» жана кардардын карызын салыштырыңыз.",
                $"Warning: the till's calculation is {calcMoney}, the server's is {srvMoney} — please check. The till's amount is paid out; compare the return in “Returns” and the client's debt.",
                $"Dikkat: kasa hesabı {calcMoney}, sunucu {srvMoney} — kontrol edin. Kasa hesabı verilir; iadeyi «İadeler»de ve müşterinin borcunu karşılaştırın.",
                $"Diqqat: kassa hisobi {calcMoney}, server {srvMoney} — tekshiring. Kassa hisobi beriladi; qaytarishni «Qaytarishlar»da va mijoz qarzini solishtiring.");
        }
        if (outcome.CalcOverServer is { } calcShown)
        {
            var calc = MoneyText.Som(calcShown);
            var serverSum = MoneyText.Som(outcome.Money);
            text += "\n" + Tr.T(
                $"Внимание: расчёт кассы {calc}, сервер {serverSum} — выдаётся меньшая сумма {serverSum}. Проверьте долг клиента: возможно, он платил на другой кассе или на сайте.",
                $"Көңүл буруңуз: касса эсеби {calc}, сервер {serverSum} — аз сумма {serverSum} берилет. Кардардын карызын текшериңиз: ал башка кассада же сайтта төлөгөн болушу мүмкүн.",
                $"Warning: the till's calculation is {calc}, the server's is {serverSum} — the smaller amount {serverSum} is paid out. Check the client's debt: they may have paid at another till or on the website.",
                $"Dikkat: kasa hesabı {calc}, sunucu {serverSum} — küçük tutar {serverSum} verilir. Müşterinin borcunu kontrol edin: başka kasada veya sitede ödemiş olabilir.",
                $"Diqqat: kassa hisobi {calc}, server {serverSum} — kichik summa {serverSum} beriladi. Mijoz qarzini tekshiring: u boshqa kassa yoki saytda to'lagan bo'lishi mumkin.");
        }
        if (expectedBefore is { } expectedShown && Math.Abs(expectedShown - outcome.Money) > 0.009m)
        {
            // 2026-11-01 (Ж-08): кассиру до отправки показали «к выдаче» X, а итог другой — видно «ожидали X, итог Y» (причина — в строке сервера ниже или в журнале).
            var was = MoneyText.Som(expectedShown);
            var now = MoneyText.Som(outcome.Money);
            text += "\n" + Tr.T(
                $"Внимание: до отправки ожидалось к выдаче {was}, итог — {now}. Выдайте {now}.",
                $"Көңүл буруңуз: жөнөтүүдөн мурун {was} берүү күтүлгөн, жыйынтык — {now}. {now} бериңиз.",
                $"Warning: {was} was expected to be paid out before sending, the result is {now}. Pay out {now}.",
                $"Dikkat: göndermeden önce {was} ödeme bekleniyordu, sonuç {now}. {now} verin.",
                $"Diqqat: yuborishdan oldin {was} berish kutilgan edi, natija — {now}. {now} bering.");
        }
        if (outcome.ServerCashOut is { } serverGave)
        {
            // 2026-11-01 (аудит «10 из 10», Ж-01): сервер записал другую выдачу, чем положено по оплатам клиента — сумма и причина.
            var should = MoneyText.Som(outcome.Money);
            var recorded = MoneyText.Som(serverGave);
            if (serverGave > outcome.Money)
                text += "\n" + Tr.T(
                    $"Внимание: сервер записал выдачу {recorded} — больше, чем положено по оплатам клиента ({should}). Выдайте {should}; расхождение с сервером записано в журнал кассы.",
                    $"Көңүл буруңуз: сервер {recorded} берүүнү жазды — кардардын төлөмдөрү боюнча туура болгондон ({should}) көп. {should} бериңиз; сервер менен айырма касса журналына жазылды.",
                    $"Warning: the server recorded a payout of {recorded} — more than is due by the client's payments ({should}). Pay out {should}; the difference with the server is written to the till log.",
                    $"Dikkat: sunucu {recorded} ödeme kaydetti — müşterinin ödemelerine göre olması gerekenden ({should}) fazla. {should} verin; sunucuyla fark kasa günlüğüne yazıldı.",
                    $"Diqqat: server {recorded} berishni yozdi — mijoz to'lovlariga ko'ra to'g'ri summadan ({should}) ko'p. {should} bering; server bilan farq kassa jurnaliga yozildi.");
            else
            text += "\n" + Tr.T(
                $"Внимание: сервер записал выдачу {recorded}, а клиент внёс {should} (предоплата и оплаты долга по этому чеку, долг сейчас меньше). Выдайте {should}; расхождение с сервером записано в журнал кассы.",
                $"Көңүл буруңуз: сервер {recorded} берүүнү жазды, ал эми кардар {should} төлөгөн (бул чек боюнча алдын ала төлөм жана карыздын төлөмдөрү, азыр карыз аз). {should} бериңиз; сервер менен айырма касса журналына жазылды.",
                $"Warning: the server recorded a payout of {recorded}, but the client paid in {should} (prepayment and debt payments on this receipt; the debt is now smaller). Pay out {should}; the difference with the server is written to the till log.",
                $"Dikkat: sunucu {recorded} ödeme kaydetti, oysa müşteri {should} ödemiş (bu fişin ön ödemesi ve borç ödemeleri; borç artık daha az). {should} verin; sunucuyla fark kasa günlüğüne yazıldı.",
                $"Diqqat: server {recorded} berishni yozdi, mijoz esa {should} to'lagan (bu chek bo'yicha oldindan to'lov va qarz to'lovlari, qarz hozir kamroq). {should} bering; server bilan farq kassa jurnaliga yozildi.");
        }
        if (outcome.NonCash > 0.005m && outcome.SplitKnown)
            text += "\n" + Tr.T("На карту/перевод — вернуть через банк, не из кассы.",
                "Картага/которуу — банк аркылуу кайтарыңыз, кассадан эмес.",
                "Card/transfer part — refund through the bank, not from the till.",
                "Kart/havale kısmı — kasadan değil, banka üzerinden iade edin.",
                "Karta/o'tkazma qismi — kassadan emas, bank orqali qaytaring.");
        return text;
    }

    /// <summary>2026-10-20 (живой аудит, К7): чек возврата не напечатался — одной строкой в итоговом окне (подробности — в журнале).</summary>
    private static string ReturnPrintNote(string? printError) => printError is null
        ? ""
        : "\n\n" + Tr.T("Чек возврата не напечатан — принтер не подключён или не отвечает.",
            "Кайтаруу чеги басылган жок — принтер туташкан эмес же жооп бербейт.",
            "The return receipt was not printed — the printer is not connected or not responding.",
            "İade fişi yazdırılmadı — yazıcı bağlı değil veya yanıt vermiyor.",
            "Qaytarish cheki chop etilmadi — printer ulanmagan yoki javob bermayapti.");

    /// <summary>2026-10-20 (живой аудит, К8): возвращённый товар — сразу в остаток каталога (как «Остаток после продажи
    /// уменьшен сразу»), потом сверка с сервером (запоздавший ответ со старым числом не откатит остаток).</summary>
    private void ReturnToStock(IEnumerable<ReturnSaleLineVm> lines)
    {
        try
        {
            var returned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0)
                    continue;
                var perQty = _stockUnitsPerQty.TryGetValue(line.LineId, out var k) && k > 0 ? k : 1.0;
                returned[line.ProductId] = (returned.TryGetValue(line.ProductId, out var before) ? before : 0) + line.Quantity * perQty;
            }
            if (returned.Count == 0)
                return;
            var expected = StockSyncService.ApplyReturnedItemsIncrement(returned);
            _ = StockSyncService.RefreshReturnedItemsStockAsync(expected);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Остаток после возврата не обновлён сразу: {ex.Message}", "STOCK");
        }
    }

    /// <summary>2026-10-16, ревью: сколько было к возврату по строкам чека (id строки → количество) перед отправкой.</summary>
    private static Dictionary<string, double> RefundableByLine(IEnumerable<ReturnSaleLineVm> lines) =>
        lines.DistinctBy(l => l.LineId, StringComparer.OrdinalIgnoreCase)
            // 2026-11-06 (Б-2): было l.Quantity — у копии строки возврата это количество К ВОЗВРАТУ (1 из 2), и «стало меньше, чем было» никогда не срабатывало; нужно, сколько было к возврату (2).
            .ToDictionary(l => l.LineId, l => l.MaxReturnQuantity, StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-16, ревью: возврат (или обмен — ExchangeWindow) уже есть на сервере — чек отменён целиком или по
    /// какой-то из отправленных строк к возврату осталось меньше, чем было перед отправкой.</summary>
    internal static bool LinesReturnedSince(JsonElement sale, IReadOnlyDictionary<string, double> refundableBefore) =>
        ReturnProbe.LinesReturnedSince(sale, refundableBefore); // 2026-11-06: тело перенесено в Infrastructure (ReturnProbe) — там оно проверяется тестами

    /// <summary>2026-10-16, ревью: возврат кончился без ответа сервера (таймаут, обрыв, 5xx) — сервер мог его провести.
    /// Перечитываем чек (LinesReturnedSince). Прошёл — ключ забываем (следующий возврат того же состава — новая
    /// операция, а не повтор прошлой), пишем возврат в журнал смены (касса его не записала) и говорим кассиру, что
    /// повторять не нужно и сколько выдать — один раз. Не прошёл или чек не прочитался — ключ остаётся: повтор уйдёт с
    /// тем же ключом, и сервер вернёт прежний итог. Чек возврата здесь не печатается.</summary>
    /// <param name="returnedLines">2026-10-20 (К8): строки этого возврата — для остатка каталога (брак — нет).</param>
    private async Task ReportReturnIfAppliedAsync(string saleId, IReadOnlyDictionary<string, double> refundableBefore,
        decimal estimate, bool reverseLoyalty, bool whole = false, IReadOnlyList<ReturnSaleLineVm>? returnedLines = null, bool isDefect = false, ApiException? serverRefusal = null)
    {
        NurMarketKassa.Services.OfflineOperationsService.NoteUnknownOnlineReturn(saleId); // 2026-10-29 (ревью, В-5): онлайн-возврат без ответа — офлайн-возврат по чеку закрыт, пока исход не выяснен
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            // 2026-11-02 (аудит №2, К2-09): сервер сам знает исход по ключу идемпотентности (GET idempotency/{key}/): найден — наш возврат прошёл; не найден — не прошёл (даже если по чеку
            // «к возврату стало меньше» из-за возврата на другой кассе или на сайте). Сервер ключей не знает (старый) — прежняя сверка по чеку.
            IdempotencyRecord? record = null;
            // 2026-11-06 (Б-2): сервер ответил ошибкой «Долг нельзя указывать в строках оплаты» — запись по ключу может хранить эту ошибку, хотя возврат проведён: верим только чеку.
            if (serverRefusal is null && PendingReturn.PeekKey is { Length: > 0 } pendingKey && App.SalesApi is IPosQuickCheckoutApi lookupApi)
            {
                try { record = await lookupApi.PosIdempotencyRecordAsync(pendingKey, cts.Token).ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OperationCanceledException) { record = null; }
            }
            // 2026-11-03 (аудит №3, Д3-05): «ключ не найден» сразу не принимаем — на медленном сервере запрос мог ещё не дойти до записи ключа. Выдержка: до трёх повторных проверок
            // с паузой 5 с (как у оплаты, InFlightReconcile). Всё ещё не найден — исход «неясен»: другой возврат по чеку не даём (_unresolvedReturn), кассиру — «уточните на сайте».
            if (record is { Found: false } && PendingReturn.PeekKey is { Length: > 0 } recheckKey && App.SalesApi is IPosQuickCheckoutApi recheckApi)
            {
                IsBusy = true;
                try
                {
                    for (var pass = 1; pass <= 3 && record is { Found: false }; pass++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                        using var recheckCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try { record = await recheckApi.PosIdempotencyRecordAsync(recheckKey, recheckCts.Token).ConfigureAwait(true); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { record = null; }
                        PosLogger.Log($"Возврат по продаже {saleId}: повторная проверка ключа {pass}/3 — {(record is null ? "сервер не ответил" : record.Found ? "найден" : "не найден")}.", "WARNING");
                    }
                }
                finally
                {
                    IsBusy = false;
                }
            }
            if (record is { Found: false })
            {
                PosLogger.Log($"Возврат по продаже {saleId}: сервер не знает ключ возврата и после повторных проверок — исход неясен, ключ оставлен для повтора, другой возврат по чеку заблокирован.", "WARNING");
                if (PendingReturn.PeekKey is { Length: > 0 } unresolvedKey)
                    _unresolvedReturn = (saleId, unresolvedKey);
                PosMessageBox.Show(this, Tr.T(
                        "Сервер пока не подтвердил этот возврат: неясно, прошёл он или нет. Деньги не выдавайте, пока не проверите на сайте NurCRM («Возвраты» этого чека). Другой возврат по этому чеку сейчас не оформляется; повторить можно только тот же самый.",
                        "Сервер бул кайтарууну азырынча ырастаган жок: өткөнү же жокпу — белгисиз. NurCRM сайтынан («Кайтаруулар») текшергенге чейин акча бербеңиз. Бул чек боюнча башка кайтаруу азыр тариздетилбейт; ошол эле кайтарууну гана кайталоого болот.",
                        "The server has not confirmed this return yet: it is unclear whether it went through. Do not pay out until you check on the NurCRM website (the receipt's returns). A different return for this receipt cannot be processed now; only the same one can be repeated.",
                        "Sunucu bu iadeyi henüz doğrulamadı: geçip geçmediği belirsiz. NurCRM sitesinde (fişin iadeleri) kontrol edene kadar ödeme yapmayın. Bu fiş için şimdi başka bir iade yapılamaz; yalnızca aynısı tekrarlanabilir.",
                        "Server bu qaytarishni hali tasdiqlamadi: o'tgan-o'tmagani noaniq. NurCRM saytida (chekning qaytarishlari) tekshirmaguncha pul bermang. Bu chek bo'yicha hozir boshqa qaytarish rasmiylashtirilmaydi; faqat o'shaning o'zini takrorlash mumkin."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (record is { IsFailure: true } failed)
            {
                // «Найден» не значит «проведён»: сервер хранит и отказы. Сохранённый ответ — ошибка: возврат НЕ проведён, деньги не выдаются, ключ забываем (отказ по существу).
                PosLogger.Log($"Возврат по продаже {saleId}: по ключу на сервере сохранён отказ (код {failed.StatusCode}, {failed.Code ?? "—"}) — возврат не проведён.", "WARNING");
                PendingReturn.OnSuccess();
                PosMessageBox.Show(this, Tr.T(
                        $"Сервер отказал в возврате (код {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — возврат НЕ проведён. Деньги покупателю не выдавайте; чек будет перечитан.",
                        $"Сервер кайтарууга уруксат берген жок (код {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — кайтаруу ЖҮРГҮЗҮЛГӨН ЖОК. Сатып алуучуга акча бербеңиз; чек кайра окулат.",
                        $"The server refused the return (code {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — the return was NOT processed. Do not pay the customer; the receipt will be reloaded.",
                        $"Sunucu iadeyi reddetti (kod {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — iade YAPILMADI. Müşteriye ödeme yapmayın; fiş yeniden okunacak.",
                        $"Server qaytarishni rad etdi (kod {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — qaytarish O'TKAZILMADI. Xaridorga pul bermang; chek qayta o'qiladi."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var snapshot = string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase) ? _saleSnapshot : null; // 2026-11-06 (Б-2): чек «до возврата»
            bool linesReduced;
            if (serverRefusal is not null)
            {
                // 2026-11-06 (Б-2): сервер мог записать возврат с задержкой — чек перечитывается с нарастающей паузой (1+2+3+4+5 = 15 с), сравнивается со снимком ДО запроса.
                // Окно занято (IsBusy), повторно не нажать. Перечитать не удалось — исход неизвестен (ниже catch: «не выдавайте деньги, не повторяйте» + запрет повтора).
                IsBusy = true;
                ReturnProbeResult probe;
                try
                {
                    probe = await ReturnProbe.RunAsync(t => App.SalesApi.PosSaleGetAsync(saleId, t), snapshot, refundableBefore).ConfigureAwait(true);
                }
                finally
                {
                    IsBusy = false;
                }
                if (probe == ReturnProbeResult.Unknown)
                    throw new HttpRequestException("чек после ошибки сервера не перечитан");
                linesReduced = probe == ReturnProbeResult.Applied;
            }
            else
            {
                var sale = await App.SalesApi.PosSaleGetAsync(saleId, cts.Token).ConfigureAwait(true);
                linesReduced = ReturnProbe.Applied(sale, snapshot, refundableBefore);
            }
            if (record is { Found: true } && !(record.IsSuccess && linesReduced))
            {
                // Запись найдена, но это не успех (операция ещё идёт, неизвестное состояние/код) либо успех, который чек не подтверждает — деньги не выдаём, как при таймауте.
                PosLogger.Log($"Возврат по продаже {saleId}: запись по ключу неоднозначна (state={record.State}, code={record.StatusCode}, чек {(linesReduced ? "изменился" : "не изменился")}) — деньги не выдаются, проверить в «Истории чеков».", "WARNING");
                PosMessageBox.Show(this, Tr.T(
                        "Не удалось точно определить, проведён ли возврат (сервер ещё обрабатывает его или ответ неоднозначен). Деньги пока не выдавайте — проверьте результат в «Истории чеков» и повторите сверку.",
                        "Кайтаруу жүргүзүлгөнү так аныкталган жок (сервер аны дагы иштетип жатат же жооп так эмес). Азырынча акча бербеңиз — жыйынтыкты «Чектердин тарыхынан» текшериңиз.",
                        "It could not be determined exactly whether the return went through (the server is still processing it or the answer is ambiguous). Do not pay out yet — check the result in “Receipt history”.",
                        "İadenin gerçekleşip gerçekleşmediği kesin olarak belirlenemedi (sunucu hâlâ işliyor veya yanıt belirsiz). Henüz ödeme yapmayın — sonucu «Fiş geçmişi»nde kontrol edin.",
                        "Qaytarish o'tgan-o'tmagani aniq aniqlanmadi (server hali qayta ishlamoqda yoki javob noaniq). Hozircha pul bermang — natijani «Cheklar tarixi»da tekshiring."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!linesReduced)
            {
                if (serverRefusal is not null)
                {
                    // 2026-11-06 (Б-2): чек не изменился — возврат не проведён, это настоящий отказ: ключ забываем, кассиру — текст сервера.
                    PendingReturn.OnError(serverRefusal);
                    NurMarketKassa.Services.OfflineOperationsService.ClearUnknownOnlineReturn(saleId);
                    PosLogger.Log($"Возврат по продаже {saleId}: после ответа «{serverRefusal.Message}» чек не изменился — возврат не проведён.", "WARNING");
                    PosMessageBox.Show(this, ExceedsOrServerText(serverRefusal, false), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: ответ не дошёл, чек перечитать не удалось — ключ оставлен для повтора: {ex.Message}", "WARNING");
            if (serverRefusal is not null)
            {
                _returnUnknownSaleId = saleId; // 2026-11-06 (Б-2): повтор запрещён, пока окно не прочитает чек заново (BlockedByUnresolvedReturn)
                PosMessageBox.Show(this, Tr.T(
                        $"Сервер ответил ошибкой («{serverRefusal.Message}»), но мог провести возврат. Деньги не выдавайте и не повторяйте возврат, пока не проверите чек в «Истории чеков».",
                        $"Сервер ката менен жооп берди («{serverRefusal.Message}»), бирок кайтарууну жүргүзгөн болушу мүмкүн. «Чектердин тарыхында» чекти текшермейинче акча бербеңиз жана кайтарууну кайталабаңыз.",
                        $"The server answered with an error (“{serverRefusal.Message}”) but may have processed the return. Do not pay out or repeat the return until you check the receipt in “Receipt history”.",
                        $"Sunucu hata ile yanıt verdi («{serverRefusal.Message}») ancak iadeyi işlemiş olabilir. Fişi «Fiş geçmişi»nde kontrol edene kadar ödeme yapmayın ve iadeyi tekrarlamayın.",
                        $"Server xato bilan javob berdi («{serverRefusal.Message}»), lekin qaytarishni o'tkazgan bo'lishi mumkin. Chekni «Cheklar tarixi»da tekshirmaguncha pul bermang va qaytarishni takrorlamang."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        PendingReturn.OnSuccess();
        NurMarketKassa.Services.OfflineOperationsService.ClearUnknownOnlineReturn(saleId); // 2026-10-29: исход выяснен — возврат без связи по чеку снова доступен
        SaleDetailCache.Forget(saleId);
        // 2026-10-20 (живой аудит, К1–К3): та же сумма и разбивка, что у дошедшего ответа (ResolveRefundAsync).
        var outcome = await ResolveRefundAsync(saleId, estimate, whole,
            (returnedLines ?? Array.Empty<ReturnSaleLineVm>()).Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList())
            .ConfigureAwait(true);
        var amount = outcome.Amount;
        // 2026-11-06 (Б-2, п. 3): возврат, проведённый сервером после ошибки, попадает в журнал смены («Возвраты в счёт долга» при закрытии смены) — как и обычный.
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, PosApp.ActiveShiftId, ShiftEventsStore.OperationKey(saleId), (double)amount, outcome.Note);
        NoteDebtCheck(saleId, partial: !whole && !reverseLoyalty, outcome.DebtReduced);
        if (!isDefect && returnedLines is { Count: > 0 })
            ReturnToStock(returnedLines);
        if (reverseLoyalty)
        {
            try
            {
                ClientLoyaltyStore.ReverseRemainingForSale(saleId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Loyalty reversal after unanswered return failed: {ex.Message}", "WARNING");
            }
        }
        PosLogger.Log($"Возврат по продаже {saleId}: ответ {(serverRefusal is null ? "не дошёл" : $"сервера — ошибка «{serverRefusal.Message}»")}, но по чеку сервер его провёл ({amount:0.00}) — записан в смену, ключ забыт.", "WARNING");
        if (serverRefusal is not null)
            PosDataEvents.RaiseClientDebtChanged(null); // 2026-11-06 (Б-2): долг клиента изменился — окна долгов перечитывают его с сервера (не по локальному вычитанию)
        // 2026-10-20 (К2, К3): к выдаче — та же строка, что у обычного возврата (разбивка, долг).
        // 2026-11-06 (живой прогон 3, P3-53): возврат уже проведён — это не ошибка кассы; окно info (кнопка «Закрыть», без «Отложить ошибку»).
        PosAlertDialog.Show(this, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), (serverRefusal is not null
            ? Tr.T(
                $"Возврат уже проведён на сервере (сервер ответил ошибкой «{serverRefusal.Message}», но чек изменился). Повторно не оформляйте. Деньги — только если ещё не выдали (не выдавайте дважды).",
                $"Кайтаруу серверде мурунтан эле жүргүзүлгөн (сервер «{serverRefusal.Message}» деген ката менен жооп берди, бирок чек өзгөрдү). Кайра тариздебеңиз. Акчаны — эгер али бере элек болсоңуз гана (эки жолу бербеңиз).",
                $"The return is already processed on the server (the server answered with an error “{serverRefusal.Message}”, but the receipt changed). Do not process it again. Pay out only if you have not done so yet (do not pay twice).",
                $"İade sunucuda zaten işlendi (sunucu «{serverRefusal.Message}» hatasıyla yanıt verdi ama fiş değişti). Tekrar yapmayın. Parayı yalnızca henüz vermediyseniz verin (iki kez vermeyin).",
                $"Qaytarish serverda allaqachon o'tkazilgan (server «{serverRefusal.Message}» xatosi bilan javob berdi, lekin chek o'zgardi). Qayta rasmiylashtirmang. Pulni faqat hali bermagan bo'lsangiz bering (ikki marta bermang).")
            : Tr.T(
                "Сервер уже провёл этот возврат — ответ не дошёл до кассы из-за связи. Повторно не оформляйте. Деньги — только если ещё не выдали (не выдавайте дважды).",
                "Сервер бул кайтарууну мурунтан эле жүргүзгөн — байланыштан улам жооп кассага жеткен жок. Кайра тариздебеңиз. Акчаны — эгер али бере элек болсоңуз гана (эки жолу бербеңиз).",
                "The server has already processed this return — its reply did not reach the till because of the connection. Do not process it again. Pay out only if you have not done so yet (do not pay twice).",
                "Sunucu bu iadeyi zaten işledi — bağlantı nedeniyle yanıt kasaya ulaşmadı. Tekrar yapmayın. Parayı yalnızca henüz vermediyseniz verin (iki kez vermeyin).",
                "Server bu qaytarishni allaqachon o'tkazgan — aloqa tufayli javob kassaga yetib kelmadi. Qayta rasmiylashtirmang. Pulni faqat hali bermagan bo'lsangiz bering (ikki marta bermang)."))
            + "\n" + RefundToGiveText(outcome),
            PosAlertKind.Warning, null, info: true);
    }

    /// <summary>Запомнить, что после возврата долгового чека надо сверить долг со свежим чеком (снимок «до» — из загруженного чека).</summary>
    private void NoteDebtCheck(string saleId, bool partial, decimal debtReduced)
    {
        if (string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase) && _saleSnapshot is { } before)
            _pendingDebtCheck = (saleId, before, partial, debtReduced);
    }

    /// <summary>true — долг после возврата не сходится с ожидаемым: окна долгов перечитывают долг клиента с сервера, кассиру показывается предупреждение (вызывающий).</summary>
    private bool CheckDebtRecalc(string saleId, JsonElement freshSale)
    {
        if (_pendingDebtCheck is not { } pending || !string.Equals(pending.SaleId, saleId, StringComparison.OrdinalIgnoreCase))
            return false;
        _pendingDebtCheck = null;
        var after = ReturnSnapshot.From(freshSale);
        if (!ReturnSnapshot.DebtRecalcSuspicious(pending.Before, after, pending.Partial, pending.DebtReduced))
            return false;
        PosLogger.Log($"Возврат по продаже {saleId}: сервер пересчитал долг иначе (чек {pending.Before.Status}/{pending.Before.ReturnStatus} → {after.Status}/{after.ReturnStatus}, "
                      + $"долг по чеку {pending.Before.DebtRemaining:0.00} → {after.DebtRemaining:0.00}, касса уменьшила на {pending.DebtReduced:0.00}) — кассиру показано предупреждение, долги перечитываются.", "WARNING");
        PosDataEvents.RaiseClientDebtChanged(null); // долг клиента перечитывается с сервера во всех открытых окнах долгов
        return true;
    }

    private async Task RefreshCurrentSaleAsync(string? expectedSaleId = null)
    {
        var saleId = expectedSaleId ?? _currentSaleId;
        if (string.IsNullOrEmpty(saleId))
            return;

        try
        {
            // 2026-11-03 (С-19, аудит «зависание при медленном сервере»): перечитывание чека после возврата — не дольше 8 с (раньше до 55 с без токена).
            var sale = await ServerAnswerWait.ForegroundAsync("pos/sales/get", t => App.SalesApi.PosSaleGetAsync(saleId, t)).ConfigureAwait(true);
            if (!string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase))
                return;
            // 2026-11-06 (Б-1/Б-2): сервер пересчитал долг клиента иначе, чем ждала касса (частичный возврат стал «отменён», долг не уменьшился) — до перезаписи снимка.
            var debtWarning = CheckDebtRecalc(saleId, sale);
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
            if (debtWarning)
                PosMessageBox.Show(this, ReturnSnapshot.DebtRecalcWarningText, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _saleSnapshot = null; // 2026-11-06 (Б-2): чек не перечитан — старый снимок «до возврата» устарел
            PosLogger.Log($"Return receipt refresh failed after operation: {ex}", "WARNING");
        }

        await LoadSalesAsync(true).ConfigureAwait(true);
        // 2026-10-21 (пакет 6, п. 35): после возврата строка чека снова выделена (и с пометкой «возвращён»).
        SelectRowInGrid(saleId);
    }
}
