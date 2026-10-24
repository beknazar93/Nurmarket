using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: погашение долга одной суммой — POST /api/main/clients/{id}/pay-debt/ (BE-12).
/// Раньше долг гасился по взносам сделки, по запросу на каждый: у старых долгов их десятки
/// (продажа в долг на 39 сом — 30 взносов по 1,30), и оплата шла ~15 с. Теперь — один запрос,
/// сервер сам раскладывает сумму от старых долгов к новым.
///
/// Заголовок Idempotency-Key: ключ живёт, пока не пришёл ответ сервера, и при повторном нажатии
/// на ту же сумму того же клиента (например, после обрыва связи) уходит тот же — сервер вернёт
/// прежний итог и второй раз деньги не проведёт.
///
/// Сервер без этого адреса (404/405) — старый путь: по строкам, от старых к новым, по взносам.
/// </summary>
public partial class PayDebtDialog
{
    private string _payAllAmountText = "";
    /// <summary>2026-10-18, второй круг ревью: сумма «одной суммой», подставленная окном (не правленная кассиром).</summary>
    private string _payAllDefault = "";
    /// <summary>Сервер не знает pay-debt — в этом окне больше не пробуем, сразу старый путь.</summary>
    private bool _serverPayDebtMissing;
    private bool _serverSalePayMissing;

    /// <summary>Оплата долга одной продажи через сервер одним запросом. true — оплата прошла (список перезагружен);
    /// false — сервер адрес не знает, вызывающий идёт старым путём по взносам.</summary>
    private Task<bool> TryPaySaleViaServerAsync(DebtSaleRow row, double amount, string method) =>
        _serverSalePayMissing || string.IsNullOrWhiteSpace(row.Id) ? Task.FromResult(false) : PayViaServerAsync(row, amount, method);

    /// <summary>Сумма «Погасить одной суммой»; после загрузки долгов — весь долг клиента.</summary>
    public string PayAllAmountText
    {
        get => _payAllAmountText;
        set
        {
            if (_payAllAmountText == value)
                return;
            _payAllAmountText = value;
            OnPropertyChanged();
        }
    }

    public bool ShowPayAllBlock => HasSelectedClient && DebtSales.Count > 0;

    public string PayAllLabel => Tr.T("Погасить одной суммой:", "Бир суммада төлөө:", "Pay off in one amount:", "Tek tutarla öde:", "Bitta summada to'lash:");

    /// <summary>2026-10-18, третий круг ревью: во время оплаты — «Оплата…» (привязка, без записи в Content).</summary>
    public string PayAllButtonLabel => _paying ? Tr.T("Оплата…", "Төлөм…", "Paying…", "Ödeniyor…", "To'lanmoqda…") : Tr.T("Погасить", "Төлөө", "Pay off", "Öde", "To'lash");

    public string PayAllHint => Tr.T(
        "Сумма гасит долги клиента от старых к новым. Можно внести и часть долга.",
        "Сумма кардардын карыздарын эскилеринен жаңыларына карай жабат. Карыздын бир бөлүгүн да төлөсө болот.",
        "The amount pays off the client's debts from oldest to newest. You can also pay part of the debt.",
        "Tutar, müşterinin borçlarını eskiden yeniye doğru kapatır. Borcun bir kısmı da ödenebilir.",
        "Summa mijozning qarzlarini eskisidan yangisiga qarab yopadi. Qarzning bir qismini ham to'lash mumkin.");

    /// <summary>2026-10-18, девятый круг: по части чеков точный остаток не получен — пояснение под «одной суммой».</summary>
    /// <remarks>2026-10-18, живая проверка: правило «одной суммой» при неуточнённых чеках — DebtPaymentFlow.OneSumRule.</remarks>
    /// <remarks>2026-10-18, ревью после живой проверки (п.9): считается один раз при изменении списка (поле), не на каждое чтение.</remarks>
    private DebtPaymentFlow.OneSumState OneSum { get; set; } = new(true, 0, null);

    public bool HasRemainingUnknown => HasSelectedClient && OneSum.Text is not null;

    /// <summary>«Погасить» одной суммой — только при точном остатке всех чеков.</summary>
    public bool CanPayAll => OneSum.Allowed;

    public string RemainingUnknownHint => OneSum.Text ?? "";

    private void InitOneSumPayment()
    {
        DebtSales.CollectionChanged += (_, _) =>
        {
            // 2026-10-24 (приёмка, ПР-1): «похоже на двойную запись» в «одной суммой» не входит (как у поставщика, П-4).
            OneSum = DebtPaymentFlow.OneSumRule(DebtSales.Where(r => !r.IsSuspectedDuplicate), owner: false);
            OnPropertyChanged(nameof(ShowPayAllBlock));
            OnPropertyChanged(nameof(HasRemainingUnknown));
            OnPropertyChanged(nameof(CanPayAll));
            OnPropertyChanged(nameof(RemainingUnknownHint));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HasSelectedClient))
                OnPropertyChanged(nameof(ShowPayAllBlock));
        };
    }

    private void OnDebtSalesReloaded()
    {
        var total = DebtSales.Where(s => !s.RemainingUnknown && !s.IsSuspectedDuplicate).Sum(s => s.Amount); // 2026-10-24 (ПР-1): без «двойной записи»
        PayAllAmountText = total > 0.005 ? DebtSaleRow.InputText(total) : "";
        _payAllDefault = PayAllAmountText;
        OnPropertyChanged(nameof(ShowPayAllBlock));
    }

    /// <summary>2026-10-22 (модалки, пакет 2): открыт вопрос «Принять …» — второе нажатие/Enter не открывает второй вопрос.</summary>
    private bool _confirmingPayAll;

    private async void PayAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedClient is not { } client || _paying || _confirmingPayAll)
            return;

        ErrorMessage = "";
        SuccessMessage = "";

        // 2026-10-18, девятый круг (деньги): «одной суммой» — только если точный остаток известен по ВСЕМ чекам.
        var oneSum = OneSum;
        if (!oneSum.Allowed)
        {
            ErrorMessage = oneSum.Text ?? "";
            return;
        }

        // 2026-10-18, третий круг ревью: разбор суммы — как у владельца (CashAmountInput: запятая/точка, пробелы).
        if (!CashAmountInput.TryParse(PayAllAmountText, out var amountDec) || amountDec <= 0m)
        {
            ErrorMessage = Tr.T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Enter the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
            return;
        }
        var amount = (double)Math.Round(amountDec, 2);

        // Не больше суммы ТОЧНЫХ остатков (неуточнённые — новее, их «одной суммой» не трогаем).
        var totalOwed = oneSum.Limit;
        if (amount > totalOwed + 0.005)
        {
            var owedText = DebtSaleRow.InputText(totalOwed);
            ErrorMessage = Tr.T(
                $"Сумма не может превышать долг клиента ({owedText} сом).",
                $"Сумма кардардын карызынан ({owedText} сом) ашпашы керек.",
                $"The amount cannot exceed the client's debt ({owedText} som).",
                $"Tutar, müşterinin borcunu ({owedText} som) aşamaz.",
                $"Summa mijozning qarzidan ({owedText} so'm) oshib ketmasligi kerak.");
            return;
        }

        // 2026-10-21 (пакет 6, п. 13 аудита кассы): «Погасить» — только после подтверждения суммы, способа и остатка
        // (было одним нажатием, а после оплаты поле снова = весь остаток: второе нажатие гасило весь долг).
        // 2026-10-24 (приёмка, ПР-1): «похоже на двойную запись» не оплачивается, но остаётся в долге клиента (сервер его считает) — «Останется долг» с ним.
        var suspects = DebtSales.Where(r => r.IsSuspectedDuplicate && !r.RemainingUnknown).ToList();
        var leftAfter = Math.Max(0, totalOwed - amount) + suspects.Sum(r => r.Amount);
        var methodTitle = DebtPaymentFlow.MethodTitle(SelectedMethod);
        var amountText = DebtPaymentFlow.Money(amount);
        var leftText = DebtPaymentFlow.Money(leftAfter);
        var confirm = Tr.T($"Принять от «{client.Name}» {amountText} в счёт долга ({methodTitle})?\nОстанется долг: {leftText}",
            $"«{client.Name}» кардардан карыз үчүн {amountText} кабыл алынсынбы ({methodTitle})?\nКалган карыз: {leftText}",
            $"Accept {amountText} from “{client.Name}” towards the debt ({methodTitle})?\nDebt left: {leftText}",
            $"«{client.Name}» müşterisinden borç için {amountText} alınsın mı ({methodTitle})?\nKalan borç: {leftText}",
            $"«{client.Name}» mijozdan qarz uchun {amountText} qabul qilinsinmi ({methodTitle})?\nQolgan qarz: {leftText}");
        // 2026-10-21 (ревью денег, A2): долг без продажи в общем долге — честно, что раскладку по чекам делает сервер.
        var noSaleTotal = DebtSales.Where(r => r.IsNoSale && !r.IsSuspectedDuplicate).Sum(r => r.Amount);
        if (noSaleTotal > 0.005)
            confirm += "\n\n" + DebtPaymentFlow.NoSalePlanNote(noSaleTotal);
        // 2026-10-24 (приёмка, ПР-1): по строке на каждую «двойную запись» — в сумму оплаты не входит.
        foreach (var suspect in suspects)
        {
            var sd = suspect.CreatedAt == default ? suspect.DateDisplay : suspect.CreatedAt.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            var sm = DebtPaymentFlow.Money(suspect.Amount);
            confirm += "\n" + Tr.T($"Долг от {sd} на {sm} похож на двойную запись — в сумму не входит, но сервер гасит долги от старых к новым и может зачесть оплату на него: проверьте на сайте NurCRM.",
                $"{sd} күнүндөгү {sm} карыз эки жолку жазууга окшош — сумманын ичине кирбейт, бирок сервер карыздарды эскиден жаңыга карай жабат жана төлөмдү ага эсептеши мүмкүн: NurCRM сайтынан текшериңиз.",
                $"The debt from {sd} for {sm} looks like a double entry — it is not in the amount, but the server pays debts from oldest to newest and may apply the payment to it: check on the NurCRM website.",
                $"{sd} tarihli {sm} borç çift kayda benziyor — tutara dahil değil, ancak sunucu borçları eskiden yeniye kapatır ve ödemeyi buna sayabilir: NurCRM sitesinde kontrol edin.",
                $"{sd} dagi {sm} qarz ikki marta yozilganga o'xshaydi — summaga kirmaydi, lekin server qarzlarni eskidan yangiga yopadi va to'lovni unga hisoblashi mumkin: NurCRM saytida tekshiring.");
        }
        // 2026-10-22 (модалки, пакет 2): кнопка-глагол «Принять N сом» вместо «Да/Нет».
        bool confirmed;
        _confirmingPayAll = true;
        try
        {
            confirmed = await PosDialogs.ConfirmYesNoModalAsync(this, confirm,
                Tr.T("Погасить долг", "Карызды төлөө", "Pay off debt", "Borcu öde", "Qarzni to'lash"),
                confirmText: Tr.T($"Принять {amountText}", $"{amountText} кабыл алуу", $"Accept {amountText}",
                    $"{amountText} al", $"{amountText} qabul qilish")).ConfigureAwait(true);
        }
        finally
        {
            _confirmingPayAll = false;
        }
        if (!confirmed)
            return;
        if (_paying || !ReferenceEquals(_selectedClient, client))
            return;

        // 2026-10-18, пятый круг (п.10): защита, замок, код, способ, ошибки — общая обёртка RunPaymentAsync.
        await RunPaymentAsync(async method =>
        {
            if (await TryPayViaServerAsync(amount, method).ConfigureAwait(true))
                return;
            if (RefuseLegacyForNonCash(method))
                return;
            await PayAllLegacyAsync(client, amount).ConfigureAwait(true);
        }, $"one sum, client {client.Id}").ConfigureAwait(true);
    }

    /// <summary>Оплата одной суммой через сервер. true — запрос состоялся (оплата прошла, список
    /// перезагружен); false — сервер этот адрес не знает, вызывающий берёт старый путь. Отказ
    /// сервера (400 и т.п.) и обрыв связи — исключением, как в старом пути.</summary>
    private Task<bool> TryPayViaServerAsync(double amount, string method) =>
        _serverPayDebtMissing ? Task.FromResult(false) : PayViaServerAsync(null, amount, method);

    /// <summary>Запасной путь «одной суммой» для сервера без pay-debt: по строкам, от старых долгов
    /// к новым, тем же старым способом (по взносам), что и кнопка «Оплатить» у строки.</summary>
    private async Task PayAllLegacyAsync(ClientOption client, double amount)
    {
        var remaining = amount;
        var paid = 0.0;
        // 2026-10-18, седьмой круг (п.7, деньги): сколько чеков реально оплачено — для «Оплачено X (чеков: k) — остальное не прошло».
        var paidRows = 0;
        string? stopReason = null;

        // 2026-10-18, пятый круг (п.6): после цикла (успех, ошибка, лимит) список перечитывается всегда.
        try
        {
        foreach (var row in DebtSales.OrderBy(r => r.CreatedAt).ToList())
        {
            if (remaining <= 0.005)
                break;
            // 2026-10-21 (ревью денег, A2): долг без продажи старым путём (по взносам чека) не гасится.
            if (row.IsNoSale)
                continue;
            var part = Math.Min(remaining, row.Amount);
            if (part <= 0.005)
                continue;

            // 2026-10-18, четвёртый круг: учитываем РЕАЛЬНО оплаченное (при остановке по лимиту — меньше части).
            double? rowPaid;
            try
            {
                rowPaid = await PayRowLegacyAsync(row, part, PayAllButton).ConfigureAwait(true);
            }
            catch (Exception ex) when (paid > 0.005)
            {
                // 2026-10-18, седьмой круг (п.7): прошлые чеки УЖЕ оплачены и записаны в смену — исключение на следующем не должно
                // превратиться в «Не удалось оплатить» (кассир взял бы деньги снова). Первый же чек — ошибка как раньше (throw).
                PosLogger.Log($"Pay-debt (старый путь, одной суммой): оплачено {paid:0.00} сом по {paidRows} чек., остановка на чеке {row.Id}: {ex.Message}", "PAYMENT");
                stopReason = ex.Message;
                break;
            }
            if (rowPaid is not { } rowSum)
            {
                stopReason = ErrorMessage;
                break;
            }

            paid += rowSum;
            if (rowSum > 0.005)
                paidRows++;
            remaining -= part;

            // «Погашено k из N взносов — нажмите ещё раз»: дальше не идём, чтобы сумма не ушла
            // в следующие долги, пока этот не закрыт.
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                stopReason = ErrorMessage;
                break;
            }
        }

        }
        finally
        {
            stopReason ??= string.IsNullOrEmpty(ErrorMessage) ? null : ErrorMessage;
            await LoadDebtSalesAsync(client.Id).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(stopReason))
                ErrorMessage = stopReason;
        }

        if (paid > 0.005)
        {
            var paidText = DebtPaymentFlow.Money(paid);
            // 2026-10-18, десятый круг (п.10): общий текст успеха; остаток — по перечитанному списку (неточный — не пишем).
            if (string.IsNullOrEmpty(stopReason))
                SuccessMessage = DebtPaymentRunner.SuccessText(paid,
                    _selectedClient is not null && DebtSales.All(r => !r.RemainingUnknown) ? DebtSales.Sum(r => r.Amount) : null, bySale: false, owner: false);
            else
                ErrorMessage = Tr.T($"Оплачено {paidText} (чеков: {paidRows}) — остальное не прошло: {stopReason}",
                    $"{paidText} төлөндү (чектер: {paidRows}) — калганы өткөн жок: {stopReason}",
                    $"Paid {paidText} (receipts: {paidRows}) — the rest did not go through: {stopReason}",
                    $"{paidText} ödendi (fiş: {paidRows}) — kalanı geçmedi: {stopReason}",
                    $"{paidText} to'landi (cheklar: {paidRows}) — qolgani o'tmadi: {stopReason}");
        }
    }
}
