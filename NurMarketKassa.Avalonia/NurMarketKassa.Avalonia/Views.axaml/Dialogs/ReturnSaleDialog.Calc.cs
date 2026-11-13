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

// 2026-11-06: В10 — часть окна возврата (механический разбор ReturnSaleDialog.axaml.cs, 3146 строк): выбор строк, расчёт суммы, проверки, вид окна, тексты ошибок. Логика не менялась.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private void SelectAllReturnable_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var line in Lines.Where(x => x.CanReturn))
            line.IsSelected = true;
    }

    // 2026-10-27 (приёмка 1.17.63, Д-5): сколько вернуть из строки — кнопки −/+ и ввод числа. Изменение количества отмечает строку.
    private void ReturnQtyLess_Click(object? sender, RoutedEventArgs e) => StepReturnQuantity(sender, -1);

    private void ReturnQtyMore_Click(object? sender, RoutedEventArgs e) => StepReturnQuantity(sender, +1);

    private void StepReturnQuantity(object? sender, int direction)
    {
        if (IsBusy || _operationInProgress || (sender as Control)?.DataContext is not ReturnSaleLineVm line || !line.CanEditQuantity)
            return;
        line.ChangeReturnQuantity(direction);
        line.IsSelected = true;
        HideQtyError();
    }

    private void HideQtyError()
    {
        if (_inputError is not null && ErrorText.Text == _inputError)
            ErrorText.IsVisible = false;
    }

    private void ReturnQtyBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter)
            return;
        e.Handled = true;
        CommitReturnQuantity(sender);
    }

    // 2026-11-05 (аудит №3, Д3-10): потеря фокуса из-за щелчка по галочке строки раньше сама отмечала строку, а щелчок тут же снимал отметку («первая отметка сбрасывается») — теперь при потере фокуса только принимаем количество.
    private void ReturnQtyBox_LostFocus(object? sender, RoutedEventArgs e) => CommitReturnQuantity(sender, selectRow: false);

    private void CommitReturnQuantity(object? sender, bool selectRow = true)
    {
        if (sender is not TextBox { DataContext: ReturnSaleLineVm line } box || IsBusy || _operationInProgress)
            return;
        var typed = box.Text;
        // Текст не менялся (то же число) — ничего не делаем, строку не отмечаем.
        if (string.Equals((typed ?? "").Trim(), line.ReturnQuantityText, StringComparison.Ordinal))
            return;
        if (line.TrySetReturnQuantity(typed))
        {
            if (selectRow)
                line.IsSelected = true;
            HideQtyError();
        }
        else
        {
            var max = line.Quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
            ShowInputErr(line.AllowFractionalQuantity
                ? Tr.T($"Количество к возврату — число больше 0 и не больше {max}.", $"Кайтаруу саны — 0дөн чоң жана {max}дан ашпаган сан.", $"The return quantity must be more than 0 and not more than {max}.", $"İade miktarı 0'dan büyük ve en fazla {max} olmalı.", $"Qaytarish miqdori 0 dan katta va {max} dan oshmasligi kerak.")
                : Tr.T($"У штучного товара количество целое: от 1 до {max}.", $"Даана товардын саны бүтүн: 1ден {max}ге чейин.", $"Whole pieces only: from 1 to {max}.", $"Adetli üründe miktar tam sayı: 1 ile {max} arası.", $"Donalab sotiladigan mahsulotda miqdor butun: 1 dan {max} gacha."));
        }
        // Всегда показываем принятое значение (при ошибке — прежнее), а не набранный мусор.
        box.Text = line.ReturnQuantityText;
    }

    private void ClearReturnSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var line in Lines)
            line.IsSelected = false;
    }

    private async void ReturnSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy || _operationInProgress)
            return;

        // 2026-10-15, ревью: защита от двойного клика — до первого подтверждения, снимается на любом выходе.
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ReturnSelectedCoreAsync(_currentSaleId).ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    /// <summary>2026-10-27 (приёмка 1.17.63, Д-5): копии отмеченных строк для оформления возврата: Quantity и RefundSum — то, что
    /// кассир выбрал вернуть (вся строка или часть по количеству). Дальше возврат, журнал, остаток, чек и обмен работают как раньше.</summary>
    private static List<ReturnSaleLineVm> EffectiveReturnLines(IEnumerable<ReturnSaleLineVm> picked) =>
        picked.Select(l => new ReturnSaleLineVm
        {
            LineId = l.LineId,
            Title = l.Title,
            SubLine = l.SubLine,
            LineSumText = l.ReturnSumText,
            RefundSum = l.ReturnRefundSum,
            ProductId = l.ProductId,
            Quantity = l.ReturnQuantity,
            OriginalQuantity = l.OriginalQuantity,
            RemainingQuantity = l.Quantity,
            AllowFractionalQuantity = l.AllowFractionalQuantity,
            Unit = l.Unit, // 2026-11-12 (С-17): единица строки остаётся и в копии для оформления
            RefundPerUnit = l.RefundPerUnit,
            CanReturn = l.CanReturn,
            RefundReason = l.RefundReason,
        }).ToList();

    /// <summary>2026-10-27 (Д-5): в подтверждении возврата — какие строки возвращаются частично: «клей: 1 / 2».</summary>
    private static string PartialReturnNote(IEnumerable<ReturnSaleLineVm> selected)
    {
        var parts = selected.Where(l => l.Quantity < l.MaxReturnQuantity - 1e-6)
            .Select(l => $"{l.Title}: {l.Quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',')} / {l.MaxReturnQuantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',')}")
            .ToList();
        return parts.Count == 0
            ? ""
            : "\n" + Tr.T("Частично: ", "Жарым-жартылай: ", "Partially: ", "Kısmen: ", "Qisman: ") + string.Join("; ", parts);
    }

    /// <summary>2026-10-20 (К1), 2026-10-27 (Д-5): сумма возврата отмеченных строк. Все возвраты чека вместе — не больше оплаченного;
    /// последний возврат (отмечены все строки целиком) — ровно остаток чека без копеек округления. Частичный по количеству — только
    /// доля строки, остаток чека не «дочищается». Чистая функция — проверяется на стенде.</summary>
    internal static decimal ComputeSelectedTotal(IReadOnlyList<ReturnSaleLineVm> selected, int returnableLines, SaleLedgerEntry? ledgerEntry,
        decimal? saleTotal, decimal? refundableTotal)
    {
        var total = selected.Sum(line => line.RefundSum);
        // 2026-10-20 (К1): все возвраты чека вместе — не больше оплаченного; последние строки чека — ровно остаток
        // (без копеек округления). Касса чек до возвратов не видела (_ledger null) — как раньше, по строкам.
        if (ledgerEntry is { Total: > 0m } ledger)
        {
            // Каждая отмеченная строка возвращается целиком (весь её остаток) — отмечены все: это весь остаток чека.
            // Копейки округления — в последний возврат; возвраты мимо этой кассы (сайт) остаток не увеличат.
            var returnsAllLeft = selected.Count == returnableLines
                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
            total = returnsAllLeft && ledger.Remaining <= total + 0.01m * selected.Count
                ? ledger.Remaining
                : Math.Min(total, ledger.Remaining);
        }
        else
        {
            // 2026-10-27 (В-3): записи кассы о чеке нет — сумма не больше итога чека, как его сейчас отдаёт сервер.
            total = ReturnShareEstimator.Cap(total, saleTotal);
        }
        // 2026-10-27 (ревью, См-5): сервер сам сказал, сколько ещё можно вернуть (refundable_total) — это точнее и записи кассы, и оценки по итогу чека.
        // 2026-10-30 («Возврат больше положенного: сервер записал 2,00, положено 1,80»): последний возврат по чеку (отмечены все оставшиеся строки
        // целиком) — ровно остаток сервера, как у него; иначе — не больше остатка (RefundAmountMath).
        if (refundableTotal is { } left && left > 0m)
        {
            var allLeft = selected.Count == returnableLines && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
            return RefundAmountMath.AmountForReturn(total, left, allLeft && left <= total + 0.01m * selected.Count);
        }
        return total;
    }

    /// <summary>2026-11-01 (аудит «10 из 10», Ж-05): по этому чеку все строки уже возвращены (returnable_qty = 0).</summary>
    private static string AlreadyReturnedText() => Tr.T(
        "По этому чеку всё уже возвращено — возвращать нечего.",
        "Бул чек боюнча баары мурунтан эле кайтарылган — кайтарууга эч нерсе жок.",
        "Everything on this receipt has already been returned — nothing left to return.",
        "Bu fişte her şey zaten iade edilmiş — iade edilecek bir şey yok.",
        "Bu chek bo'yicha hammasi allaqachon qaytarilgan — qaytariladigan narsa yo'q.");


    private void UpdateReceiptChrome()
    {
        var hasSale = !string.IsNullOrEmpty(_currentSaleId);
        SelectedChequeBar.IsVisible = hasSale;
        RefreshWholeReceiptButton(); // 2026-11-01 (Д-2): было IsEnabled = hasSale — стирало привязку CanReturnWhole

        if (!hasSale)
        {
            SelectedSaleText.Text = "";
            LinesPlaceholder.Text = Tr.T("Сначала выберите чек в списке выше — здесь появится его содержимое.",
                "Алгач жогорудагы тизмеден чекти тандаңыз — анын курамы ушул жерде көрүнөт.",
                "Select a receipt in the list above first — its contents will appear here.",
                "Önce yukarıdaki listeden bir fiş seçin — içeriği burada görünecek.",
                "Avval yuqoridagi ro'yxatdan chekni tanlang — uning tarkibi shu yerda ko'rinadi.");
            LinesPlaceholder.IsVisible = true;
        }
        else if (Lines.Count == 0)
        {
            SelectedSaleText.Text = Tr.T("Выбран чек · ", "Тандалган чек · ", "Selected receipt · ", "Seçilen fiş · ", "Tanlangan chek · ") + DisplaySaleNumber(_currentSaleId);
            LinesPlaceholder.Text = Tr.T("В этом чеке нет позиций с идентификатором строки для возврата через кассу.",
                "Бул чекте кассадан кайтаруу үчүн сап идентификатору бар позициялар жок.",
                "This receipt has no items with a line ID that can be returned through the till.",
                "Bu fişte kasadan iade edilebilecek satır kimliğine sahip kalem yok.",
                "Bu chekda kassa orqali qaytarish uchun qator identifikatoriga ega pozitsiyalar yo'q.");
            LinesPlaceholder.IsVisible = true;
        }
        else
        {
            SelectedSaleText.Text = Tr.T($"Выбран чек · {DisplaySaleNumber(_currentSaleId)}  ·  позиций в чеке: {Lines.Count}",
                $"Тандалган чек · {DisplaySaleNumber(_currentSaleId)}  ·  чектеги позиция: {Lines.Count}",
                $"Selected receipt · {DisplaySaleNumber(_currentSaleId)}  ·  items in receipt: {Lines.Count}",
                $"Seçilen fiş · {DisplaySaleNumber(_currentSaleId)}  ·  fişteki kalem: {Lines.Count}",
                $"Tanlangan chek · {DisplaySaleNumber(_currentSaleId)}  ·  chekdagi pozitsiyalar: {Lines.Count}");
            // 2026-11-10 (владелец: возврат долгового чека): способ возврата долгового чека — всегда «в счёт долга клиента» (сервер уменьшает долг сам;
            // деньги выдаются только за часть, внесённую при покупке). Выбирать способ кассиру не нужно — говорим об этом прямо.
            var methodNow = (_ledger?.Method is { Length: > 0 } knownMethod ? knownMethod : _currentSaleMethod ?? "").Trim().ToLowerInvariant();
            if (methodNow == "debt" || _ledger is { Debt: > 0.005m })
                SelectedSaleText.Text += "\n" + Tr.T("Чек в долг: возврат — в счёт долга клиента (долг уменьшится; деньги — только за внесённую часть).",
                    "Карыз чек: кайтаруу — кардардын карызынын эсебинен (карыз азаят; акча — төлөнгөн бөлүгү үчүн гана).",
                    "Credit receipt: the return goes against the customer's debt (the debt decreases; money only for the part paid).",
                    "Veresiye fiş: iade müşterinin borcundan düşülür (borç azalır; para yalnızca ödenen kısım için).",
                    "Qarz chek: qaytarish — mijoz qarzi hisobidan (qarz kamayadi; pul — faqat to'langan qism uchun).");
            LinesPlaceholder.IsVisible = false;
        }

        UpdateReturnTermText(hasSale);
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-31): дата чека и сколько дней прошло; после срока
    /// обмена (закон КР — 14 дней, не считая дня покупки; см. NonExchangeableRules) — красное предупреждение.</summary>
    private void UpdateReturnTermText(bool hasSale)
    {
        var limit = NonExchangeableRules.DaysLimit;
        if (!hasSale || limit <= 0 || _currentSaleDate is not { } date)
        {
            ReturnTermText.IsVisible = false;
            return;
        }
        var days = NonExchangeableRules.DaysPassed(date);
        var outOfTerm = days > limit;
        ReturnTermText.Text = Tr.T($"Чек от {date:dd.MM.yyyy}: прошло {days} дн. из {limit}.", $"Чек {date:dd.MM.yyyy}: {limit} күндүн {days} күнү өттү.",
                                  $"Receipt of {date:dd.MM.yyyy}: {days} of {limit} days have passed.", $"{date:dd.MM.yyyy} tarihli fiş: {limit} günün {days} günü geçti.",
                                  $"{date:dd.MM.yyyy} sanadagi chek: {limit} kundan {days} kun o'tdi.")
                              + (outOfTerm
                                  ? Tr.T(" Срок обмена и возврата прошёл — без брака по закону не обменивают.", " Алмаштыруу жана кайтаруу мөөнөтү өттү — мыйзам боюнча бузук эмес товар алмаштырылбайт.",
                                      " The exchange and return period is over — by law, items without defects are not exchanged.", " Değişim ve iade süresi doldu — kanuna göre kusursuz ürün değiştirilmez.",
                                      " Almashtirish va qaytarish muddati o'tdi — qonun bo'yicha nuqsonsiz mahsulot almashtirilmaydi.")
                                  : "");
        ReturnTermText.Bind(TextBlock.ForegroundProperty, ReturnTermText.GetResourceObservable(outOfTerm ? "BrushDanger" : "BrushTextSoft"));
        ReturnTermText.IsVisible = true;
    }

    /// <summary>2026-10-06 (О-31, О-32): проверки перед возвратом и обменом в сфере «Одежда». null — кассир отказался;
    /// true — товар из категории «без обмена», кассир подтвердил брак (у возврата сразу стоит отметка «Брак»); false — обычный случай.
    /// Не запрет: брак принимают и после срока, и из этих категорий.</summary>
    private async Task<bool?> CheckReturnRulesAsync(IReadOnlyList<ReturnSaleLineVm> lines, bool exchange)
    {
        var title = exchange ? Tr.T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish") : Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish");
        if (NonExchangeableRules.IsOutOfTerm(_currentSaleDate))
        {
            var date = _currentSaleDate!.Value;
            var days = NonExchangeableRules.DaysPassed(date);
            var limit = NonExchangeableRules.DaysLimit;
            var what = exchange ? Tr.T("обмен", "алмаштырууну", "the exchange", "değişimi", "almashtirishni") : Tr.T("возврат", "кайтарууну", "the return", "iadeyi", "qaytarishni");
            if (await PosMessageBox.ShowModalAsync(this,
                    Tr.T($"Чек от {date:dd.MM.yyyy}: прошло {days} дн., а срок обмена и возврата — {limit} дн. (не считая дня покупки).\n\nПо закону товар без брака после этого срока не обменивают и не возвращают; брак принимают дольше.\n\nОформить {what} всё равно?",
                        $"Чек {date:dd.MM.yyyy}: {days} күн өттү, ал эми алмаштыруу жана кайтаруу мөөнөтү — {limit} күн (сатып алган күн эсептелбейт).\n\nМыйзам боюнча бул мөөнөттөн кийин бузук эмес товар алмаштырылбайт жана кайтарылбайт; бузук товар кечирээк да кабыл алынат.\n\nБаары бир {what} таризделсинби?",
                        $"Receipt of {date:dd.MM.yyyy}: {days} days have passed, but the exchange and return period is {limit} days (not counting the day of purchase).\n\nBy law, items without defects are not exchanged or returned after this period; defective items are accepted longer.\n\nProcess {what} anyway?",
                        $"{date:dd.MM.yyyy} tarihli fiş: {days} gün geçti, değişim ve iade süresi ise {limit} gün (satın alma günü sayılmaz).\n\nKanuna göre bu süreden sonra kusursuz ürün değiştirilmez ve iade edilmez; kusurlu ürün daha uzun süre kabul edilir.\n\nYine de {what} yapılsın mı?",
                        $"{date:dd.MM.yyyy} sanadagi chek: {days} kun o'tdi, almashtirish va qaytarish muddati esa {limit} kun (xarid kuni hisoblanmaydi).\n\nQonun bo'yicha bu muddatdan keyin nuqsonsiz mahsulot almashtirilmaydi va qaytarilmaydi; nuqsonli mahsulot uzoqroq qabul qilinadi.\n\nBaribir {what} rasmiylashtirilsinmi?"),
                    Tr.T("Срок обмена прошёл", "Алмаштыруу мөөнөтү өттү", "Exchange period is over", "Değişim süresi doldu", "Almashtirish muddati o'tdi"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    // 2026-10-22 (модалки, пакет 1): глагол вместо «Продолжить»: «Оформить возврат» / «Оформить обмен».
                    confirmText: exchange
                        ? Tr.T("Оформить обмен", "Алмаштырууну тариздөө", "Process exchange", "Değişimi yap", "Almashtirishni rasmiylashtirish")
                        : Tr.T("Оформить возврат", "Кайтарууну тариздөө", "Process return", "İadeyi yap", "Qaytarishni rasmiylashtirish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return null;
            PosLogger.Log($"{title} по чеку {_currentSaleId} после срока: прошло {days} дн. из {limit}, кассир подтвердил.", "SALES");
        }

        var flagged = lines
            .Select(l => (Line: l, Category: NonExchangeableRules.NonExchangeableCategoryOf(l.ProductId)))
            .Where(x => x.Category != null)
            .ToList();
        if (flagged.Count == 0)
            return false;
        var names = string.Join("\n", flagged.Select(x => $"• {x.Line.Title} — «{x.Category}»"));
        var text = exchange
            ? Tr.T($"Эти товары без брака по закону не обменивают:\n{names}\n\nЕсли это брак — лучше оформите «Возврат» с отметкой «Брак» (товар не вернётся в продажу), а новый товар продайте обычным чеком: обмен возвращает товар на склад.\n\nВсё равно оформить обмен?",
                $"Бул товарлар бузук болбосо мыйзам боюнча алмаштырылбайт:\n{names}\n\nЭгер бузук болсо — «Бузук» белгиси менен «Кайтарууну» таризделиңиз (товар сатууга кайтпайт), жаңы товарды кадимки чек менен сатыңыз: алмаштыруу товарды кампага кайтарат.\n\nБаары бир алмаштырылсынбы?",
                $"By law, these items are not exchanged unless defective:\n{names}\n\nIf it is a defect, better make a “Return” marked “Defective” (the item won't go back on sale) and sell the new item with a regular receipt: an exchange puts the item back in stock.\n\nExchange anyway?",
                $"Kanuna göre bu ürünler kusurlu değilse değiştirilmez:\n{names}\n\nKusurluysa «Kusurlu» işaretli «İade» yapın (ürün satışa dönmez) ve yeni ürünü normal fişle satın: değişim ürünü stoğa geri koyar.\n\nYine de değiştirilsin mi?",
                $"Qonun bo'yicha bu mahsulotlar nuqsonsiz bo'lsa almashtirilmaydi:\n{names}\n\nAgar nuqson bo'lsa — «Nuqsonli» belgisi bilan «Qaytarish»ni rasmiylashtiring (mahsulot sotuvga qaytmaydi), yangi mahsulotni oddiy chek bilan soting: almashtirish mahsulotni omborga qaytaradi.\n\nBaribir almashtirilsinmi?")
            : Tr.T($"Эти товары без брака по закону не обменивают и не возвращают:\n{names}\n\nЕсли у товара брак — оформите возврат как брак: товар не вернётся в продажу.",
                $"Бул товарлар бузук болбосо мыйзам боюнча алмаштырылбайт жана кайтарылбайт:\n{names}\n\nТовар бузук болсо — кайтарууну бузук катары тариздеңиз: товар сатууга кайтпайт.",
                $"By law, these items are not exchanged or returned unless defective:\n{names}\n\nIf the item is defective, return it as defective: it won't go back on sale.",
                $"Kanuna göre bu ürünler kusurlu değilse değiştirilmez ve iade edilmez:\n{names}\n\nÜrün kusurluysa kusurlu olarak iade edin: satışa dönmez.",
                $"Qonun bo'yicha bu mahsulotlar nuqsonsiz bo'lsa almashtirilmaydi va qaytarilmaydi:\n{names}\n\nMahsulot nuqsonli bo'lsa — nuqsonli sifatida qaytaring: sotuvga qaytmaydi.");
        var answer = await PosMessageBox.ShowModalAsync(this, text,
            Tr.T("Товар не подлежит обмену", "Товар алмаштырылбайт", "Item cannot be exchanged", "Ürün değiştirilemez", "Mahsulot almashtirilmaydi"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            // 2026-10-22 (модалки, пакет 1): глаголы вместо «Да» / «Нет» (текст больше не называет кнопки): у возврата —
            // «Оформить как брак», у обмена — «Оформить обмен»; вторая — «Отмена» (умолчание окна). Смысл ответов прежний.
            confirmText: exchange
                ? Tr.T("Оформить обмен", "Алмаштырууну тариздөө", "Process exchange", "Değişimi yap", "Almashtirishni rasmiylashtirish")
                : Tr.T("Оформить как брак", "Бузук катары тариздөө", "Return as defective", "Kusurlu olarak iade et", "Nuqsonli sifatida rasmiylashtirish")).ConfigureAwait(true);
        if (answer != MessageBoxResult.Yes)
            return null;
        PosLogger.Log($"{title} по чеку {_currentSaleId}: товар из категории без обмена ({string.Join(", ", flagged.Select(x => x.Category))}), кассир подтвердил.", "SALES");
        return !exchange;
    }


    private string DisplaySaleNumber(string? saleId)
    {
        if (string.IsNullOrEmpty(saleId))
            return "—";
        var row = FilteredSales.FirstOrDefault(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            return row.DisplayNumber;
        // 2026-09-28 (BE-08): постоянный номер, прочитанный из самой продажи при открытии.
        if (string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_currentReceiptNumber))
            return _currentReceiptNumber!;
        // 2026-09-28: чек открыт из «Истории чеков», а в загруженные страницы списка он не попал
        // (старше первых 35 продаж) — номер тот же, что показала история (он считается по тому
        // же правилу, что и здесь, — как на сайте), а не обрезанный код продажи.
        return string.Equals(saleId, InitialSaleId?.Trim(), StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(InitialReceiptNumber)
            ? InitialReceiptNumber!
            : TruncateId(saleId);
    }

    private static string TruncateId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return "—";
        return id.Length <= 36 ? id : id[..32] + "…";
    }


    /// <summary>2026-11-01 (приёмка, К-01): текст ошибки возврата. Свой повтор с тем же ключом (прошлая отправка осталась без ответа) получил 409 «больше положенного» —
    /// первый запрос мог на самом деле пройти: отдельный текст «проверьте в „Возвратах“ перед выдачей денег». В смену, на склад и в баллы ничего не пишется.</summary>
    private static string ExceedsOrServerText(ApiException ex, bool ownRetry)
    {
        if (ownRetry && RefundAmountMath.TryParseExceeds(ex, out _))
        {
            PosLogger.Log("Возврат: повтор своего запроса получил 409 «больше положенного» — первый запрос мог пройти; деньги не выдаются до проверки в «Возвратах».", "WARNING");
            return Tr.T(
                "Возможно, возврат уже проведён (прошлая отправка осталась без ответа). Проверьте в «Возвратах» перед выдачей денег — не выдавайте, пока не убедитесь. В смену этот возврат не записан.",
                "Кайтаруу мурунтан эле жүргүзүлгөн болушу мүмкүн (мурунку жөнөтүүгө жооп келген жок). Акча берүүдөн мурун «Кайтаруулардан» текшериңиз — ишенмейинче бербеңиз. Бул кайтаруу сменага жазылган жок.",
                "The return may already have been processed (the previous attempt got no reply). Check “Returns” before paying out — do not pay until you are sure. This return is not recorded in the shift.",
                "İade zaten yapılmış olabilir (önceki gönderim yanıtsız kaldı). Ödemeden önce «İadeler»i kontrol edin — emin olmadan vermeyin. Bu iade vardiyaya yazılmadı.",
                "Qaytarish allaqachon o'tkazilgan bo'lishi mumkin (oldingi yuborishga javob kelmadi). Pul berishdan oldin «Qaytarishlar»ni tekshiring — ishonch hosil qilmay bermang. Bu qaytarish smenaga yozilmadi.");
        }
        return RefundExceedsText(ex) ?? ex.Message;
    }

    /// <summary>2026-10-30: ответ сервера «refund_exceeds_paid» (возврат больше положенного) — текст для кассира с цифрами сервера
    /// (оплачено / уже возвращено / можно вернуть). Сумма в запросе не передаётся (сервер считает по строкам), поэтому повторять с другой суммой нельзя:
    /// окно после ответа заново читает чек и пересчитывает «можно вернуть» (RefreshCurrentSaleAsync). null — другая ошибка.</summary>
    internal static string? RefundExceedsText(ApiException ex)
    {
        if (!RefundAmountMath.TryParseExceeds(ex, out var info))
            return null;
        // 2026-11-01 (аудит «10 из 10», К-01): это отказ, а не прошедший возврат — деньги не выдаются (ниже к тексту добавлено «Деньги не выдавайте»).
        var noPay = "\n" + Tr.T("Деньги покупателю по этой операции не выдавайте.", "Бул операция боюнча сатып алуучуга акча бербеңиз.", "Do not pay the customer anything for this operation.", "Bu işlem için müşteriye para vermeyin.", "Bu amal bo'yicha xaridorga pul bermang.");
        var left = info.Refundable is { } r && r > 0m ? MoneyText.Som(r) : null;
        var paid = info.Paid is { } p ? MoneyText.Som(p) : null;
        var back = info.Refunded is { } b ? MoneyText.Som(b) : null;
        var numbers = paid is null || back is null ? "" : "\n" + Tr.T($"Оплачено {paid}, уже возвращено {back}.", $"Төлөндү {paid}, мурун кайтарылды {back}.", $"Paid {paid}, already returned {back}.", $"Ödenen {paid}, daha önce iade edilen {back}.", $"To'langan {paid}, avval qaytarilgan {back}.");
        return left is null
            ? Tr.T("Сервер не принял возврат: по этому чеку больше нечего возвращать (возможно, часть уже возвращена на сайте или другой кассе).", "Сервер кайтарууну кабыл алган жок: бул чек боюнча кайтарууга эч нерсе калган жок (бир бөлүгү сайтта же башка кассада кайтарылган болушу мүмкүн).", "The server did not accept the return: nothing is left to return on this receipt (part may have been returned on the website or another till).", "Sunucu iadeyi kabul etmedi: bu fişte iade edilecek bir şey kalmadı (bir kısmı sitede veya başka kasada iade edilmiş olabilir).", "Server qaytarishni qabul qilmadi: bu chek bo'yicha qaytariladigan narsa qolmadi (bir qismi saytda yoki boshqa kassada qaytarilgan bo'lishi mumkin).") + numbers + noPay
            : Tr.T($"Сервер не принял возврат: по чеку можно вернуть не больше {left}. Выберите меньше строк или количество.", $"Сервер кайтарууну кабыл алган жок: чек боюнча {left} ашык кайтарууга болбойт. Азыраак позиция же сан тандаңыз.", $"The server did not accept the return: no more than {left} can be returned on this receipt. Select fewer items or a smaller quantity.", $"Sunucu iadeyi kabul etmedi: bu fişte en fazla {left} iade edilebilir. Daha az kalem veya miktar seçin.", $"Server qaytarishni qabul qilmadi: bu chek bo'yicha {left} dan ko'p qaytarib bo'lmaydi. Kamroq pozitsiya yoki miqdor tanlang.") + numbers + noPay;
    }
}
