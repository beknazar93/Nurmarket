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

// 2026-11-06: В10 — часть окна возврата (механический разбор ReturnSaleDialog.axaml.cs, 3146 строк): отправка возврата, обмена и возврата всего чека на сервер. Логика не менялась.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private async Task ReturnSelectedCoreAsync(string saleId)
    {
        // 2026-11-03 (аудит №3, Д3-01/Д3-05): возврат по этому чеку уже отправлен и ждёт медленный сервер — второй (с другим количеством) не отправляем.
        if (SlowMoneyCall.IsActive("return|" + saleId))
        {
            ShowInputErr(SlowMoneyCall.StillRunningText);
            return;
        }
        // 2026-10-27 (приёмка 1.17.63, Д-5): дальше везде — строки с тем количеством, которое кассир выбрал (по умолчанию всё).
        var selected = EffectiveReturnLines(Lines.Where(x => x.CanReturn && x.IsSelected));
        if (selected.Count == 0 && Lines.Count > 0 && !Lines.Any(x => x.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05)
            return;
        }
        if (selected.Count == 0)
        {
            ShowInputErr(Tr.T("Отметьте галочками хотя бы одну позицию для возврата.", "Кайтаруу үчүн жок дегенде бир позицияны белгилеңиз.", "Check at least one item to return.", "İade için en az bir kalemi işaretleyin.", "Qaytarish uchun kamida bitta pozitsiyani belgilang."));
            return;
        }

        var total = ComputeSelectedTotal(selected, Lines.Count(x => x.CanReturn), _ledger, _currentSaleTotal, _currentSaleRefundable);
        var linesSum = selected.Sum(line => line.RefundSum);

        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        // 2026-10-20 (К1): сумма — с долей скидки на чек, без «~» и в виде «1 234,50 сом».
        var totalShown = MoneyText.Som(total);
        // 2026-10-27 (В-3): чек уже частично возвращали не на этой кассе — сумму подтверждает кассир с цифрами перед глазами.
        var elsewhere = _ledger is null && _noLedgerTouched
            ? "\n\n" + Tr.T($"Чек уже возвращали не на этой кассе (сайт или другая касса). Сумма рассчитана по данным сервера: по чеку оплачено {MoneyText.Som(_currentSaleTotal ?? 0m)}, ранее возвращено примерно {MoneyText.Som(_noLedgerPriorReturned)}. Сверьте сумму.",
                $"Чек бул кассада эмес (сайт же башка касса) мурун кайтарылган. Сумма сервер маалыматы боюнча эсептелди: чек боюнча төлөнгөн {MoneyText.Som(_currentSaleTotal ?? 0m)}, мурун болжол менен {MoneyText.Som(_noLedgerPriorReturned)} кайтарылган. Сумманы текшериңиз.",
                $"This receipt was already returned elsewhere (website or another till). The amount is calculated from server data: paid {MoneyText.Som(_currentSaleTotal ?? 0m)}, returned earlier about {MoneyText.Som(_noLedgerPriorReturned)}. Please check the amount.",
                $"Bu fiş daha önce bu kasada değil (web sitesi veya başka kasa) iade edilmiş. Tutar sunucu verisinden hesaplandı: ödenen {MoneyText.Som(_currentSaleTotal ?? 0m)}, daha önce yaklaşık {MoneyText.Som(_noLedgerPriorReturned)} iade edilmiş. Tutarı kontrol edin.",
                $"Bu chek avval bu kassada emas (sayt yoki boshqa kassa) qaytarilgan. Summa server ma'lumoti bo'yicha hisoblandi: to'langan {MoneyText.Som(_currentSaleTotal ?? 0m)}, avval taxminan {MoneyText.Som(_noLedgerPriorReturned)} qaytarilgan. Summani tekshiring.")
            : "";
        // 2026-11-01 (Ж-08): «К выдаче» — до отправки, локальным расчётом по данным чека (не дольше 300 мс на остаток долга).
        var payoutPreview = await ExpectedPayoutTextAsync(saleId, total,
            reversesLoyalty: selected.Count == Lines.Count(x => x.CanReturn) && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6)).ConfigureAwait(true);
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Выбрано позиций: {selected.Count}\nСумма возврата: {totalShown}",
                    $"Тандалган позиция: {selected.Count}\nКайтаруу суммасы: {totalShown}",
                    $"Items selected: {selected.Count}\nRefund amount: {totalShown}",
                    $"Seçilen kalem: {selected.Count}\nİade tutarı: {totalShown}",
                    $"Tanlangan pozitsiyalar: {selected.Count}\nQaytarish summasi: {totalShown}") + PartialReturnNote(selected) + elsewhere + payoutPreview,
                Tr.T("Подтверждение возврата", "Кайтарууну ырастоо", "Confirm return", "İadeyi onayla", "Qaytarishni tasdiqlash"),
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): кнопки — глагол с суммой («Вернуть 1 620,00 сом» / «Отмена») вместо «Продолжить?».
                confirmText: ConfirmButtonText(saleId, total, whole: false, totalShown)).ConfigureAwait(true) != MessageBoxResult.Yes)
            return;

        // 2026-10-06 (О-31, О-32): срок обмена и товары «без обмена» — в сфере «Одежда».
        var defectPreset = await CheckReturnRulesAsync(selected, exchange: false).ConfigureAwait(true);
        if (defectPreset is null)
            return;

        // 2026-11-05 (функции 2 и 12): возврат от порога — PIN старшего (по настройке) и запись в «Подозрительные операции».
        if (!await ActionGuard.ConfirmAsync(GuardAction.Return, (double)total, _currentReceiptNumber).ConfigureAwait(true))
            return;
        // 2026-11-05 (ревью волн 0–3, п. 14): событие «подозрительный возврат» пишем ПОСЛЕ успешного ответа сервера (отмена причины или отказ сервера его не порождают).
        var fraudAmount = (double)total;

        var reasonDialog = new ReturnLineReasonDialog(selected.Count) { IsDefect = defectPreset == true };
        if (await PosDialogHost.ShowModalAsync(reasonDialog, this).ConfigureAwait(true) != true)
            return;

        if (!string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            return;

        var reason = reasonDialog.ReasonText;
        var isDefect = reasonDialog.IsDefect;
        // 2026-10-16, ревью: сколько было к возврату по строкам до отправки — для сверки, если ответ не дойдёт.
        var refundableBefore = RefundableByLine(selected);
        var outcomeUnknown = false;
        ApiException? debtLineRefusal = null; // 2026-11-06 (Б-2): сервер ответил «Долг нельзя указывать в строках оплаты», а возврат мог провести — выясняем по чеку
        IsBusy = true;

        try
        {
            var requests = selected.Select(line => new PosRefundLineRequest
            {
                LineId = line.LineId,
                ProductId = line.ProductId,
                Title = line.Title,
                Quantity = line.Quantity > 0 ? line.Quantity : 1,
                OriginalQuantity = line.OriginalQuantity > 0 ? line.OriginalQuantity : line.Quantity,
            }).ToList();

            // 2026-10-15, ревью: тот же чек и те же строки/количества — тот же ключ (см. PendingReturn).
            var returnSignature = "lines|" + (isDefect ? "defect|" : "") + saleId + "|" + string.Join(";", requests
                .OrderBy(r => r.LineId, StringComparer.Ordinal)
                .Select(r => r.LineId + ":" + r.Quantity.ToString("0.###", CultureInfo.InvariantCulture)));
            if (BlockedByUnresolvedReturn(saleId, returnSignature)) // 2026-11-03 (Д3-05)
                return;
            var returnKey = PendingReturn.Get(returnSignature);

            var debtSaleForDrawer = IsDebtSaleForDrawer(saleId);
            var cashBefore = await ReadDrawerBeforeRefundAsync(debtSaleForDrawer).ConfigureAwait(true);
            _refundSentAt = DateTimeOffset.Now; // 2026-11-02 (К3-03): документ возврата старше этого момента — не наш
            // 2026-11-03 (аудит №3, Д3-01/С-08): окно ждёт сервер не дольше 8 с; запрос НЕ отменяется и не задваивается (тот же ключ), ответ дожидается и обрабатывается ниже тем же кодом.
            var refundResponse = await SlowMoneyCall.RunAsync("pos/return", "return|" + saleId, t => PosRefundService.RefundLinesAsync(
                App.SalesApi,
                saleId,
                requests,
                reason,
                App.PosCashboxId,
                isDefect,
                t,
                idempotencyKey: returnKey), () => IsBusy = false).ConfigureAwait(true);
            PendingReturn.OnSuccess();
            FraudMonitor.OnReturn(saleId, _currentReceiptNumber, fraudAmount);

            SaleDetailCache.Forget(saleId);

            // 2026-10-04, стресс-тест: сумма возврата — та, что записал сервер (документ возврата).
            // Оценка выше (сумма строк) не знает скидки на чек: сервер хранит её у продажи, а не в
            // строках, и чек возврата печатал больше, чем сервер вернул (полный возврат №1348: касса
            // 56,00, сервер 50,40). Сервер не ответил — остаётся оценка, как раньше.
            // 2026-10-20 (живой аудит, К1–К3): сумма — расчёт кассы с долей скидки на чек (сервер её не учитывает и
            // мог вернуть больше оплаченного — тогда его число кассиру не показываем, а пишем в журнал и в
            // «Некорректные чеки»); к выдаче — только внесённые деньги (наличными / на карту), долг уменьшается.
            var estimatedTotal = linesSum;
            var outcome = await ResolveRefundAsync(saleId, total, whole: false,
                selected.Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList(),
                ServerRefundFigures.TryRead(refundResponse)) // 2026-11-01 (ответ Умара): выдача — из refund.cash
                .ConfigureAwait(true);
            total = outcome.Amount;
            if (!debtSaleForDrawer || ExchangeCashCorrections.DebtRefundHandledByServer) // 2026-11-05 (Р3-06 / Д3-11): шапка «Касса:» уменьшается сразу, не ждёт ответа сервера об остатке; у долгового чека — если сервер снимает наличные сам
                ExchangeCashCorrections.NotePendingServerCash(PosApp.ActiveShiftId, Math.Min(outcome.Cash, outcome.ServerCashOut ?? outcome.Cash));
            CheckDrawerAfterRefund(cashBefore, outcome.Cash, "lines|" + returnKey, saleId, outcome.NonCash, outcome.ServerCashOut, debtSaleForDrawer);

            // В итогах смены на сервере возвратов нет вовсе — записываем сами, иначе кассир
            // при закрытии смены их не увидит (см. ShiftEventsStore).
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(saleId),
                (double)total,
                outcome.Note);
            // 2026-10-20 (К8): остаток вернувшегося товара — сразу (брак на склад не возвращается).
            if (!isDefect)
                ReturnToStock(selected);

            // Баллы лояльности отменяются, когда возвращён ВЕСЬ чек — в том числе если кассир
            // сделал это, отметив все позиции галочками (самый естественный способ), а не кнопкой
            // «Полный возврат». Раньше отмена стояла только на кнопке полного возврата, поэтому
            // при построчном возврате покупатель оставлял себе бонусы за возвращённый товар.
            // Частичный возврат баллы не трогает: сервер не делит начисление по позициям.
            var returnedEverything = selected.Count == Lines.Count(x => x.CanReturn)
                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
            if (debtSaleForDrawer)
                NoteDebtCheck(saleId, partial: !returnedEverything, outcome.DebtReduced); // 2026-11-06 (Б-1/Б-2): долг клиента сверяется со свежим чеком в RefreshCurrentSaleAsync
            if (returnedEverything)
            {
                try
                {
                    ClientLoyaltyStore.ReverseRemainingForSale(saleId);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Loyalty reversal on line return failed: {ex.Message}", "WARNING");
                }
            }

            // Чек возврата обязателен: это выдача денег из кассы, её подтверждают бумагой
            // так же, как продажу. Печатаем после того, как сервер принял возврат — чтобы на
            // руках не оказалось чека по непрошедшей операции.
            // Печать — в фоне (2026-09-25): медленный или отключённый принтер больше не
            // подвешивает окно на время записи в порт.
            var printLines = RefundSplit.ScaleLines(selected.Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList(), estimatedTotal, total);
            var printReceiptNumber = _currentReceiptNumber;
            var printCashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var printError = await Task.Run(() => OperationReceiptPrinter.PrintReturn(
                printReceiptNumber,
                printLines,
                total,
                reason,
                isWholeSale: false,
                printCashier)).ConfigureAwait(true);

            // 2026-10-20 (живой аудит, К7): ошибка печати — строкой в том же итоговом окне, а не отдельным окном
            // (было 4 окна на один возврат: подтверждение, причина, ошибка печати, итог).
            // 2026-10-23 (аудит К6): возврат прошёл — это не ошибка; предупреждение о принтере показываем как info (без «Отложить ошибку»).
            var returnDoneText = (selected.Count == 1
                    ? Tr.T("Возврат оформлен.", "Кайтаруу таризделди.", "Return completed.", "İade tamamlandı.", "Qaytarish rasmiylashtirildi.")
                    : Tr.T($"Возврат оформлен ({selected.Count} поз.).", $"Кайтаруу таризделди ({selected.Count} поз.).", $"Return completed ({selected.Count} items).", $"İade tamamlandı ({selected.Count} kalem).", $"Qaytarish rasmiylashtirildi ({selected.Count} poz.)."))
                    // 2026-10-20 (К2, К3): к выдаче — внесённые деньги с разбивкой; у долга — на сколько уменьшен долг.
                    + "\n" + RefundToGiveText(outcome, TakeExpectedPayout(saleId))
                    + DrawerUnverifiedNote(debtSaleForDrawer, cashBefore, outcome.Cash)
                    + ReturnPrintNote(printError);
            if (printError is null)
                PosMessageBox.Show(this, returnDoneText, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                PosAlertDialog.Show(this, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), returnDoneText, PosAlertKind.Warning, null, info: true);
        }
        catch (ApiException ex) when (ex.StatusCode == 502 && ex.Message.Contains("Возвращено позиций", StringComparison.Ordinal))
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ApiException ex) when (OfflineRejection.IsDebtPaymentLineRefusal(ex))
        {
            // 2026-11-06 (живой тест Этапа 1, Б-2): сервер ceba67c на частичный возврат долгового чека отвечает 400 «Долг нельзя указывать в строках оплаты», но возврат проводит.
            // Ошибку кассиру не показываем и ключ не трогаем: ниже касса перечитывает чек и говорит «возврат уже проведён» (повторять нельзя) либо «не проведён» (с текстом сервера).
            PosLogger.Log($"Возврат по продаже {saleId}: сервер ответил «{ex.Message}» — проверяем по чеку, проведён ли возврат.", "WARNING");
            debtLineRefusal = ex;
        }
        catch (ApiException ex)
        {
            // 2026-10-16, ревью: 4xx (кроме 408/429) — ключ забыт; иначе исход неизвестен, ключ оставлен для повтора.
            var ownRetry = PendingReturn.HasUnansweredAttempt; // 2026-11-01 (приёмка, К-01): до OnError — ключ ещё помнит, что прошлая отправка осталась без ответа
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-10-30: «возврат больше положенного» — понятный текст с суммой, которую сервер разрешает (дальше окно пересчитает остаток чека).
            PosMessageBox.Show(this, ExceedsOrServerText(ex, ownRetry), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ReturnNoConnectionText(), // 2026-11-02 (К2-24): без сырого ex.Message
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-11-01 (контрольный аудит, К2-12): честное состояние вместо «Превышено время ожидания» — возврат не потерян, касса сама проверит чек; кассиру — что делать.
            PosMessageBox.Show(this, Tr.T(
                    "Сервер медленно отвечает — возврат не потерян. Касса проверит чек сама; проверьте результат в «Истории чеков» перед повтором и перед выдачей денег.",
                    "Сервер жай жооп берүүдө — кайтаруу жоголгон жок. Касса чекти өзү текшерет; кайталоодон жана акча берүүдөн мурун «Чектердин тарыхынан» жыйынтыкты текшериңиз.",
                    "The server is slow to respond — the return is not lost. The till will check the receipt itself; check the result in “Receipt history” before retrying and before paying out.",
                    "Sunucu yavaş yanıt veriyor — iade kaybolmadı. Kasa fişi kendisi kontrol edecek; tekrar etmeden ve ödeme yapmadan önce sonucu «Fiş geçmişi»nde kontrol edin.",
                    "Server sekin javob bermoqda — qaytarish yo'qolgani yo'q. Kassa chekni o'zi tekshiradi; qayta urinishdan va pul berishdan oldin natijani «Cheklar tarixi»da tekshiring."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        // 2026-10-16, ревью: ответ не дошёл — сервер мог провести возврат; сверяем по чеку (см. ReportReturnIfAppliedAsync).
        if (outcomeUnknown || debtLineRefusal is not null)
            await ReportReturnIfAppliedAsync(saleId, refundableBefore, total,
                reverseLoyalty: selected.Count == Lines.Count(x => x.CanReturn)
                                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6),
                returnedLines: selected, isDefect: isDefect, serverRefusal: debtLineRefusal).ConfigureAwait(true);
        await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
    }


    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-30): обмен одним документом — отмеченные позиции
    /// возвращаются, покупатель берёт другой товар, деньгами проходит только разница (окно ExchangeWindow).</summary>
    private async void ExchangeSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy || _operationInProgress)
            return;

        // 2026-10-15, ревью: двойной клик открывал бы два окна обмена одного чека (см. _operationInProgress).
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ExchangeSelectedCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ExchangeSelectedCoreAsync()
    {
        if (string.IsNullOrEmpty(_currentSaleId))
            return;
        // 2026-10-27 (Д-5): обмен тоже берёт выбранное количество (часть строки), а не всю строку.
        var selected = EffectiveReturnLines(Lines.Where(x => x.CanReturn && x.IsSelected));
        var exchangeTitle = Tr.T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish");
        if (selected.Count == 0 && Lines.Count > 0 && !Lines.Any(x => x.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05)
            return;
        }
        if (selected.Count == 0)
        {
            ShowInputErr(Tr.T("Отметьте галочками, что покупатель возвращает.", "Сатып алуучу эмнени кайтарарын белгилеңиз.", "Check what the customer is returning.",
                "Müşterinin iade ettiklerini işaretleyin.", "Xaridor nimani qaytarayotganini belgilang."));
            return;
        }
        // 2026-11-01 (аудит «10 из 10», К-05): обмен проходит те же защиты, что возврат. Чек «в долг»: что внесено, а что в долге, сервер при обмене
        // не различает (разницу выдают наличными целиком) — обмен не оформляется, вернуть и продать заново (там долг считается верно).
        var saleMethodNow = (_ledger?.Method is { Length: > 0 } knownMethod ? knownMethod : _currentSaleMethod ?? "").Trim().ToLowerInvariant();
        if (saleMethodNow == "debt" || _ledger is { Debt: > 0.005m })
        {
            PosMessageBox.Show(this, Tr.T(
                    "Чек продан в долг — обмен по нему не оформляется: касса не может разделить внесённые деньги и долг клиента. Оформите возврат (долг уменьшится сам) и продайте новый товар отдельным чеком.",
                    "Чек карызга сатылган — ал боюнча алмаштыруу тариздетилбейт: касса киргизилген акчаны жана кардардын карызын бөлө албайт. Кайтарууну тариздеңиз (карыз өзү азаят) жана жаңы товарды өзүнчө чек менен сатыңыз.",
                    "This receipt was sold on credit — an exchange cannot be processed: the till cannot separate the money paid from the client's debt. Process a return (the debt is reduced automatically) and sell the new item on a separate receipt.",
                    "Bu fiş veresiye satıldı — değişim yapılamaz: kasa ödenen parayı müşterinin borcundan ayıramaz. İade yapın (borç kendiliğinden azalır) ve yeni ürünü ayrı fişle satın.",
                    "Chek qarzga sotilgan — u bo'yicha almashtirish rasmiylashtirilmaydi: kassa kiritilgan pulni va mijoz qarzini ajrata olmaydi. Qaytarishni rasmiylashtiring (qarz o'zi kamayadi) va yangi tovarni alohida chek bilan soting."),
                exchangeTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (await CheckReturnRulesAsync(selected, exchange: true).ConfigureAwait(true) is null)
            return;

        // 2026-11-06 (аудит №5, Л5-08): обмен — это возврат отмеченных позиций с выдачей разницы; PIN старшего «возврат от суммы» требуется и здесь,
        // иначе дорогой товар меняли на дешёвый и выдавали разницу наличными без PIN. Сумма — стоимость возвращаемых позиций.
        if (!await ActionGuard.ConfirmAsync(GuardAction.Return, (double)selected.Sum(l => l.RefundSum), _currentReceiptNumber).ConfigureAwait(true))
            return;

        var lines = selected.Select(l => new ExchangeReturnLine(l.LineId, l.ProductId, l.Title,
            l.Quantity > 0 ? l.Quantity : 1,
            l.Quantity > 0 ? Math.Round(l.RefundSum / (decimal)l.Quantity, 2) : l.RefundSum)).ToList();
        var saleId = _currentSaleId;
        var window = new ExchangeWindow(saleId, _currentReceiptNumber ?? DisplaySaleNumber(saleId), _currentSaleDate, lines);
        if (await PosDialogHost.ShowModalAsync(window, this).ConfigureAwait(true) != true || window.Outcome is not { } done)
        {
            // 2026-10-16, ревью: обмен остался без ответа сервера и мог пройти — строки и количества к возврату
            // перечитываем с сервера, а не оставляем прежние.
            if (window.SaleMayHaveChanged)
                await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
            return;
        }

        var payDiff = done.CorrectedDifference ?? done.Difference; // К2-03: при «сервер засчитал лишнее» — разница по расчёту кассы
        var money = payDiff > 0.004m
            ? (done.PaymentMethod == "transfer"
                ? Tr.T($"Доплата переводом: {MoneyText.Amount(payDiff)} сом — проверьте, что перевод пришёл.", $"Которуу менен кошумча төлөм: {MoneyText.Amount(payDiff)} сом — которуу келгенин текшериңиз.",
                    $"Paid by transfer: {MoneyText.Amount(payDiff)} som — check that the transfer arrived.", $"Havale ile ek ödeme: {MoneyText.Amount(payDiff)} som — havalenin geldiğini kontrol edin.",
                    $"O'tkazma orqali qo'shimcha to'lov: {MoneyText.Amount(payDiff)} so'm — o'tkazma kelganini tekshiring.")
                : Tr.T($"Получить с покупателя: {MoneyText.Amount(payDiff)} сом наличными.", $"Сатып алуучудан алуу: {MoneyText.Amount(payDiff)} сом накталай.",
                    $"Take from the customer: {MoneyText.Amount(payDiff)} som in cash.", $"Müşteriden alınacak: {MoneyText.Amount(payDiff)} som nakit.",
                    $"Xaridordan olish: {MoneyText.Amount(payDiff)} so'm naqd."))
            : payDiff < -0.004m
                ? RefundToGiveText(-payDiff)
                : Tr.T("Без доплаты.", "Кошумча төлөмсүз.", "No difference to pay.", "Fark yok.", "Qo'shimcha to'lovsiz.");
        PosMessageBox.Show(this,
            Tr.T("Обмен оформлен.", "Алмаштыруу таризделди.", "Exchange completed.", "Değişim tamamlandı.", "Almashtirish rasmiylashtirildi.")
            + (done.Warning is { Length: > 0 } exchangeWarning ? "\n" + exchangeWarning : "") // 2026-11-01 (К-05)
            + (string.IsNullOrWhiteSpace(done.NewSaleNumber) ? "" : Tr.T($"\nНовый чек №{done.NewSaleNumber}.", $"\nЖаңы чек №{done.NewSaleNumber}.", $"\nNew receipt No. {done.NewSaleNumber}.",
                $"\nYeni fiş No. {done.NewSaleNumber}.", $"\nYangi chek №{done.NewSaleNumber}."))
            + "\n" + money,
            exchangeTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshCurrentSaleAsync().ConfigureAwait(true);
    }


    private async void ReturnWholeReceipt_Click(object? sender, RoutedEventArgs e)
    {
        if (_operationInProgress)
            return;
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy)
        {
            // 2026-10-22 (модалки, пакет 1): строкой в окне (снимется, когда кассир откроет чек), а не всплывающим окном;
            // во время загрузки (IsBusy) — молча, как кнопка «Вернуть выбранные».
            if (string.IsNullOrEmpty(_currentSaleId))
                ShowInputErr(Tr.T("Сначала выберите чек в списке слева.", "Алгач сол жактагы тизмеден чекти тандаңыз.", "Select a receipt in the list on the left first.", "Önce soldaki listeden bir fiş seçin.", "Avval chapdagi ro'yxatdan chekni tanlang."));
            return;
        }

        // 2026-10-15, ревью: защита от двойного клика — до первого подтверждения, снимается на любом выходе.
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ReturnWholeReceiptCoreAsync(_currentSaleId).ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ReturnWholeReceiptCoreAsync(string saleId)
    {
        // 2026-11-03 (аудит №3, Д3-01/Д3-05): возврат по этому чеку уже отправлен и ждёт медленный сервер — второй не отправляем.
        if (SlowMoneyCall.IsActive("return|" + saleId))
        {
            ShowInputErr(SlowMoneyCall.StillRunningText);
            return;
        }
        // 2026-11-01 (аудит «10 из 10», Ж-05): всё уже возвращено — останавливаем сразу, до подтверждения «0,00 сом» и ввода причины.
        if (Lines.Count > 0 && !Lines.Any(l => l.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText());
            return;
        }
        // 2026-10-20 (К1): касса видела чек до возвратов — остаток оплаченного по её журналу (сервер после частичного
        // возврата убирает скидку на чек из продажи, его total больше остатка); иначе — как раньше.
        var estimatedWhole = _ledger is { Total: > 0m } wholeLedger
            ? Math.Min(wholeLedger.Remaining, _currentSaleTotal ?? wholeLedger.Remaining)
            // 2026-10-27 (В-3): чек уже частично возвращали не здесь — остаток по строкам (с долей скидки), не больше итога чека.
            : _noLedgerTouched ? ReturnShareEstimator.Cap(Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum), _currentSaleTotal)
            : _currentSaleTotal ?? Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum);
        estimatedWhole = ReturnShareEstimator.CapRefundable(estimatedWhole, _currentSaleRefundable); // 2026-10-27 (ревью, См-5)

        // 2026-10-21 (пакет 6, п. 35): порядок как у частичного возврата — сначала подтверждение с суммой, потом причина
        // (было наоборот: у частичного «подтверждение → причина», у полного «причина → подтверждение»).
        var wholeShown = MoneyText.Som(estimatedWhole);
        var payoutPreview = await ExpectedPayoutTextAsync(saleId, estimatedWhole, reversesLoyalty: true).ConfigureAwait(true); // 2026-11-01 (Ж-08)
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Оформить полный возврат всего чека одной операцией? Позиции по отдельности возвращать не потребуется.\nСумма возврата: {wholeShown}",
                    $"Бүт чекти бир операция менен толук кайтарасызбы? Позицияларды өз-өзүнчө кайтаруунун кереги жок болот.\nКайтаруу суммасы: {wholeShown}",
                    $"Return the whole receipt in one operation? You won't need to return items one by one.\nRefund amount: {wholeShown}",
                    $"Tüm fiş tek işlemde iade edilsin mi? Kalemleri tek tek iade etmeniz gerekmeyecek.\nİade tutarı: {wholeShown}",
                    $"Butun chek bitta amal bilan qaytarilsinmi? Pozitsiyalarni birma-bir qaytarish shart bo'lmaydi.\nQaytarish summasi: {wholeShown}") + payoutPreview,
                Tr.T("Полный возврат", "Толук кайтаруу", "Full return", "Tam iade", "To'liq qaytarish"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): глагол с суммой вместо «Продолжить».
                confirmText: ConfirmButtonText(saleId, estimatedWhole, whole: true, wholeShown)).ConfigureAwait(true) != MessageBoxResult.Yes)
            return;

        // 2026-10-06 (О-31, О-32): срок обмена и товары «без обмена» — в сфере «Одежда».
        var defectPreset = await CheckReturnRulesAsync(Lines.Where(l => l.CanReturn).ToList(), exchange: false).ConfigureAwait(true);
        if (defectPreset is null)
            return;

        // 2026-11-05 (функции 2 и 12): возврат от порога — PIN старшего (по настройке) и запись в «Подозрительные операции».
        if (!await ActionGuard.ConfirmAsync(GuardAction.Return, (double)estimatedWhole, _currentReceiptNumber).ConfigureAwait(true))
            return;
        // 2026-11-05 (ревью волн 0–3, п. 14): событие — после успешного ответа сервера, а не до окна причины.

        var reasonDialog = new ReturnLineReasonDialog(kind: ReturnReasonDialogKind.FullReceipt) { IsDefect = defectPreset == true };
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(reasonDialog, this).ConfigureAwait(true) != true)
            return;

        if (!string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            return;

        // 2026-10-16, ревью: остаток к возврату по строкам до отправки и оценка суммы — для сверки, если ответ не дойдёт.
        var refundableBefore = RefundableByLine(Lines.Where(l => l.CanReturn));
        var outcomeUnknown = false;
        var exceedsRefused = false; // 2026-11-01 (К-01): сервер отказал «больше положенного» — чек перечитываем, деньги не выдаём
        ApiException? debtLineRefusal = null; // 2026-11-06 (Б-2): см. возврат по позициям
        IsBusy = true;
        try
        {
            // 2026-10-15, ревью: повтор полного возврата того же чека — тот же ключ (см. PendingReturn).
            var wholeSignature = "whole|" + (reasonDialog.IsDefect ? "defect|" : "") + saleId;
            if (BlockedByUnresolvedReturn(saleId, wholeSignature)) // 2026-11-03 (Д3-05)
                return;
            var returnKey = PendingReturn.Get(wholeSignature); // 2026-11-02 (К2-24): «брак» — другая операция, другой ключ
            var debtSaleForDrawer = IsDebtSaleForDrawer(saleId);
            var cashBeforeWhole = await ReadDrawerBeforeRefundAsync(debtSaleForDrawer).ConfigureAwait(true);
            _refundSentAt = DateTimeOffset.Now;
            // 2026-11-02 (К2-19): по чеку есть неотправленный возврат без связи — «весь чек» без строк вернул бы и его (сервер вернёт всё, что осталось); шлём построчно остаток, который кассе известен.
            var wholeHasUnsent = Lines.Any(l => l.CanReturn && NurMarketKassa.Services.OfflineOperationsStore.UnsentRefundQuantity(saleId, l.ProductId, l.Title) > 0);
            // 2026-11-03 (аудит №3, Д3-01/С-08): окно ждёт сервер не дольше 8 с; запрос НЕ отменяется и не задваивается (тот же ключ), ответ дожидается и обрабатывается ниже тем же кодом.
            var wholeLinesToSend = wholeHasUnsent
                ? Lines.Where(l => l.CanReturn).Select(l => new PosRefundLineRequest
                {
                    LineId = l.LineId,
                    ProductId = l.ProductId,
                    Title = l.Title,
                    Quantity = l.Quantity > 0 ? l.Quantity : 1,
                    OriginalQuantity = l.OriginalQuantity > 0 ? l.OriginalQuantity : l.Quantity,
                }).ToList()
                : null;
            var wholeResponse = await SlowMoneyCall.RunAsync("pos/return-whole", "return|" + saleId, t => wholeLinesToSend is not null
                ? PosRefundService.RefundLinesAsync(
                    App.SalesApi,
                    saleId,
                    wholeLinesToSend,
                    reasonDialog.ReasonText,
                    App.PosCashboxId,
                    reasonDialog.IsDefect,
                    t,
                    idempotencyKey: returnKey)
                : PosRefundService.RefundWholeSaleAsync(
                    App.SalesApi,
                    saleId,
                    reasonDialog.ReasonText,
                    App.PosCashboxId,
                    reasonDialog.IsDefect,
                    t,
                    idempotencyKey: returnKey), () => IsBusy = false).ConfigureAwait(true);
            PendingReturn.OnSuccess();
            FraudMonitor.OnReturn(saleId, _currentReceiptNumber, (double)estimatedWhole);

            // 2026-09-23, живой баг: полный возврат чека не писал событие смены вообще — запись
            // стояла только в построчном возврате. Вернули покупателю весь чек наличными, а в
            // Z-отчёте, Telegram-сводке и выгрузке этой суммы нет: кассир сдаёт смену с
            // «недостачей» ровно на сумму возврата.
            // 2026-10-04, стресс-тест: сумма строк не учитывает скидку на чек — чек возврата печатал
            // больше, чем заплатил покупатель (№1348: 56,00 вместо 50,40). Берём сумму документа
            // возврата сервера, без него — итог продажи (total), и только потом — сумму строк.
            var linesTotal = Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum);
            // 2026-10-20 (живой аудит, К1–К3): «Вернуть весь чек» после частичного — только остаток оплаченного
            // (estimatedWhole: остаток по журналу кассы), к выдаче — внесённые деньги с разбивкой, у долга — уменьшение долга.
            var wholeOutcome = await ResolveRefundAsync(saleId, estimatedWhole, whole: true,
                Lines.Where(l => l.CanReturn).Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList(),
                ServerRefundFigures.TryRead(wholeResponse)) // 2026-11-01 (ответ Умара): выдача — из refund.cash
                .ConfigureAwait(true);
            var wholeTotal = wholeOutcome.Amount;
            if (!debtSaleForDrawer || ExchangeCashCorrections.DebtRefundHandledByServer) // 2026-11-05 (Р3-06 / Д3-11): шапка «Касса:» уменьшается сразу
                ExchangeCashCorrections.NotePendingServerCash(PosApp.ActiveShiftId, Math.Min(wholeOutcome.Cash, wholeOutcome.ServerCashOut ?? wholeOutcome.Cash));
            CheckDrawerAfterRefund(cashBeforeWhole, wholeOutcome.Cash, "whole|" + returnKey, saleId, wholeOutcome.NonCash, wholeOutcome.ServerCashOut, debtSaleForDrawer);
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(saleId),
                (double)wholeTotal,
                wholeOutcome.Note);
            // 2026-10-20 (К8): остаток вернувшегося товара — сразу (брак на склад не возвращается).
            if (!reasonDialog.IsDefect)
                ReturnToStock(Lines.Where(l => l.CanReturn).ToList());

            var wholeLines = RefundSplit.ScaleLines(Lines.Where(l => l.CanReturn).Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList(), linesTotal, wholeTotal);
            var wholeReceiptNumber = _currentReceiptNumber;
            var wholeReason = reasonDialog.ReasonText;
            var wholeCashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var wholePrintError = await Task.Run(() => OperationReceiptPrinter.PrintReturn(
                wholeReceiptNumber,
                wholeLines,
                wholeTotal,
                wholeReason,
                isWholeSale: true,
                wholeCashier)).ConfigureAwait(true);

            // 2026-09-13, живой баг: полный возврат чека не отменял баллы лояльности,
            // начисленные/списанные при этой продаже (см. BasketPanelViewModel.
            // CreditOrRedeemLoyaltyPoints) — покупатель сохранял бонусы за возвращённый товар
            // навсегда, а списанные на оплату баллы не восстанавливались. Безопасно вызывать
            // всегда: если для этой продажи транзакции не было (лояльность выключена, продажа
            // без клиента, продажа была офлайн), метод просто ничего не делает.
            try
            {
                ClientLoyaltyStore.ReverseRemainingForSale(saleId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Loyalty reversal on full return failed: {ex.Message}", "WARNING");
            }

            // 2026-10-20 (живой аудит, К7): ошибка печати — строкой в этом же окне, а не отдельным окном.
            // 2026-10-23 (аудит, круг 7): как в возврате позиций — предупреждение о принтере показываем как info (без «Отложить ошибку»).
            var wholeDoneText = Tr.T("Полный возврат чека оформлен.", "Чек толугу менен кайтарылды.", "Full receipt return completed.", "Fişin tam iadesi tamamlandı.", "Chek to'liq qaytarildi.")
                    + "\n" + RefundToGiveText(wholeOutcome, TakeExpectedPayout(saleId))
                    + DrawerUnverifiedNote(debtSaleForDrawer, cashBeforeWhole, wholeOutcome.Cash)
                    + ReturnPrintNote(wholePrintError);
            if (wholePrintError is null)
                PosMessageBox.Show(this, wholeDoneText, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                PosAlertDialog.Show(this, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), wholeDoneText, PosAlertKind.Warning, null, info: true);
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
        }
        catch (ApiException ex) when (OfflineRejection.IsDebtPaymentLineRefusal(ex))
        {
            PosLogger.Log($"Возврат по продаже {saleId}: сервер ответил «{ex.Message}» — проверяем по чеку, проведён ли возврат.", "WARNING");
            debtLineRefusal = ex; // 2026-11-06 (Б-2): см. возврат по позициям
        }
        catch (ApiException ex)
        {
            // 2026-10-16, ревью: 4xx (кроме 408/429) — ключ забыт; иначе исход неизвестен, ключ оставлен для повтора.
            var ownRetry = PendingReturn.HasUnansweredAttempt; // 2026-11-01 (приёмка, К-01): см. возврат по позициям
            outcomeUnknown = PendingReturn.OnError(ex);
            exceedsRefused = RefundAmountMath.TryParseExceeds(ex, out _);
            // 2026-10-30: «возврат больше положенного» — понятный текст с суммой, которую сервер разрешает (дальше окно пересчитает остаток чека).
            PosMessageBox.Show(this, ExceedsOrServerText(ex, ownRetry), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ReturnNoConnectionText(), // 2026-11-02 (К2-24): без сырого ex.Message
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-11-01 (контрольный аудит, К2-12): честное состояние вместо «Превышено время ожидания» — возврат не потерян, касса сама проверит чек; кассиру — что делать.
            PosMessageBox.Show(this, Tr.T(
                    "Сервер медленно отвечает — возврат не потерян. Касса проверит чек сама; проверьте результат в «Истории чеков» перед повтором и перед выдачей денег.",
                    "Сервер жай жооп берүүдө — кайтаруу жоголгон жок. Касса чекти өзү текшерет; кайталоодон жана акча берүүдөн мурун «Чектердин тарыхынан» жыйынтыкты текшериңиз.",
                    "The server is slow to respond — the return is not lost. The till will check the receipt itself; check the result in “Receipt history” before retrying and before paying out.",
                    "Sunucu yavaş yanıt veriyor — iade kaybolmadı. Kasa fişi kendisi kontrol edecek; tekrar etmeden ve ödeme yapmadan önce sonucu «Fiş geçmişi»nde kontrol edin.",
                    "Server sekin javob bermoqda — qaytarish yo'qolgani yo'q. Kassa chekni o'zi tekshiradi; qayta urinishdan va pul berishdan oldin natijani «Cheklar tarixi»da tekshiring."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        // 2026-10-16, ревью: ответ не дошёл — сервер мог провести возврат; сверяем по чеку и показываем чек как на сервере.
        if (outcomeUnknown || debtLineRefusal is not null)
        {
            await ReportReturnIfAppliedAsync(saleId, refundableBefore, estimatedWhole, reverseLoyalty: true,
                whole: true, returnedLines: Lines.Where(l => l.CanReturn).ToList(), isDefect: reasonDialog.IsDefect, serverRefusal: debtLineRefusal).ConfigureAwait(true);
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
        }
        else if (exceedsRefused)
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true); // 2026-11-01 (К-01): «можно вернуть» пересчитывается по чеку
    }
}
