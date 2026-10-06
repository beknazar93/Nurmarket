using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
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
    private string? _pendingPayKey;
    private string? _pendingPaySignature;
    /// <summary>Сервер не знает pay-debt — в этом окне больше не пробуем, сразу старый путь.</summary>
    private bool _serverPayDebtMissing;

    // 2026-10-05: долг одной продажи — pos/sales/{id}/pay-debt/ (ТЗ ч.12, п. 2.3). Ключ живёт до ответа сервера: повторное
    // нажатие после обрыва связи посылает ТОТ ЖЕ ключ — сервер вернёт прежний итог, вторая оплата не пройдёт.
    private string? _pendingSalePayKey;
    private string? _pendingSalePaySignature;
    private bool _serverSalePayMissing;

    /// <summary>Оплата долга одной продажи через сервер одним запросом. true — оплата прошла (список перезагружен);
    /// false — сервер адрес не знает, вызывающий идёт старым путём по взносам.</summary>
    private async Task<bool> TryPaySaleViaServerAsync(DebtSaleRow row, double amount, Button button)
    {
        if (_serverSalePayMissing || string.IsNullOrWhiteSpace(row.Id))
            return false;
        var api = App.AppHost?.Services.GetService<ClientDebtsApiService>();
        if (api == null)
            return false;

        var signature = row.Id + "|" + amount.ToString("0.00", CultureInfo.InvariantCulture);
        if (_pendingSalePayKey == null || _pendingSalePaySignature != signature)
        {
            _pendingSalePayKey = Guid.NewGuid().ToString();
            _pendingSalePaySignature = signature;
        }
        var key = _pendingSalePayKey;

        var contentBefore = button.Content;
        button.Content = Tr.T("Оплата…", "Төлөм…", "Paying…", "Ödeniyor…", "To'lanmoqda…");
        var watch = Stopwatch.StartNew();
        PayDebtResult result;
        try
        {
            result = await api.PaySaleDebtAsync(row.Id, amount, "cash", PosApp.ActiveShiftId, key).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ClientDebtsApiService.IsEndpointMissing(ex))
        {
            _serverSalePayMissing = true;
            _pendingSalePayKey = null;
            PosLogger.Log($"Pay-debt продажи: сервер не знает pos/sales/{{id}}/pay-debt/ ({ex.StatusCode}) — старый путь по взносам.", "PAYMENT");
            return false;
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 and not 408 and not 429)
        {
            // Отказ по существу (например, 400 no_debt — продажа уже оплачена): следующее нажатие — новая операция.
            _pendingSalePayKey = null;
            throw;
        }
        finally
        {
            button.Content = contentBefore;
        }

        watch.Stop();
        _pendingSalePayKey = null;
        ShiftEventsStore.Record(
            ShiftEventsStore.KindDebtPayment,
            PosApp.ActiveShiftId,
            "pay-debt-sale:" + key,
            result.Paid,
            _selectedClient?.DisplayName);
        PosLogger.Log(
            $"Pay-debt продажи {row.Id}: оплачено {result.Paid:0.00}, остаток {result.Left:0.00}, {watch.ElapsedMilliseconds} мс{(result.Replayed ? ", повтор по ключу" : "")}.",
            "PAYMENT");

        if (_selectedClient != null)
            await LoadDebtSalesAsync(_selectedClient.Id).ConfigureAwait(true);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var paidText = result.Paid.ToString("N2", ru);
        var leftText = result.Left.ToString("N2", ru);
        SuccessMessage = result.Left <= 0.005
            ? Tr.T($"Оплачено {paidText} сом. Долг по чеку погашен.", $"{paidText} сом төлөндү. Чек боюнча карыз жабылды.",
                $"Paid {paidText} som. The receipt's debt is paid off.", $"{paidText} som ödendi. Fişin borcu kapandı.",
                $"{paidText} so'm to'landi. Chek bo'yicha qarz yopildi.")
            : Tr.T($"Оплачено {paidText} сом. Остаток по чеку: {leftText} сом.", $"{paidText} сом төлөндү. Чек боюнча калдык: {leftText} сом.",
                $"Paid {paidText} som. Left on the receipt: {leftText} som.", $"{paidText} som ödendi. Fişte kalan: {leftText} som.",
                $"{paidText} so'm to'landi. Chek bo'yicha qoldiq: {leftText} so'm.");
        return true;
    }

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

    public string PayAllButtonLabel => Tr.T("Погасить", "Төлөө", "Pay off", "Öde", "To'lash");

    public string PayAllHint => Tr.T(
        "Сумма гасит долги клиента от старых к новым. Можно внести и часть долга.",
        "Сумма кардардын карыздарын эскилеринен жаңыларына карай жабат. Карыздын бир бөлүгүн да төлөсө болот.",
        "The amount pays off the client's debts from oldest to newest. You can also pay part of the debt.",
        "Tutar, müşterinin borçlarını eskiden yeniye doğru kapatır. Borcun bir kısmı da ödenebilir.",
        "Summa mijozning qarzlarini eskisidan yangisiga qarab yopadi. Qarzning bir qismini ham to'lash mumkin.");

    private void InitOneSumPayment()
    {
        DebtSales.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowPayAllBlock));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HasSelectedClient))
                OnPropertyChanged(nameof(ShowPayAllBlock));
        };
    }

    private void OnDebtSalesReloaded()
    {
        var total = DebtSales.Sum(s => s.Amount);
        PayAllAmountText = total > 0.005 ? total.ToString("0.00", CultureInfo.InvariantCulture) : "";
        OnPropertyChanged(nameof(ShowPayAllBlock));
    }

    private async void PayAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedClient is not { } client)
            return;

        ErrorMessage = "";
        SuccessMessage = "";

        var raw = (PayAllAmountText ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            ErrorMessage = Tr.T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Enter the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
            return;
        }

        var totalOwed = DebtSales.Sum(s => s.Amount);
        if (amount > totalOwed + 0.005)
        {
            var owedText = totalOwed.ToString("0.00", CultureInfo.InvariantCulture);
            ErrorMessage = Tr.T(
                $"Сумма не может превышать долг клиента ({owedText} сом).",
                $"Сумма кардардын карызынан ({owedText} сом) ашпашы керек.",
                $"The amount cannot exceed the client's debt ({owedText} som).",
                $"Tutar, müşterinin borcunu ({owedText} som) aşamaz.",
                $"Summa mijozning qarzidan ({owedText} so'm) oshib ketmasligi kerak.");
            return;
        }

        PayAllButton.IsEnabled = false;
        try
        {
            if (await TryPayViaServerAsync(client, amount, PayAllButton).ConfigureAwait(true))
                return;

            await PayAllLegacyAsync(client, amount).ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            PosLogger.Log($"Pay-debt (one sum) failed for client {client.Id}: {ex}", "PAYMENT");
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось оплатить долг: ", "Карызды төлөө мүмкүн болгон жок: ", "Could not pay the debt: ", "Borç ödenemedi: ", "Qarzni to'lab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Pay-debt (one sum) failed for client {client.Id}: {ex}", "PAYMENT");
        }
        finally
        {
            PayAllButton.Content = PayAllButtonLabel;
            PayAllButton.IsEnabled = true;
        }
    }

    /// <summary>Оплата одной суммой через сервер. true — запрос состоялся (оплата прошла, список
    /// перезагружен); false — сервер этот адрес не знает, вызывающий берёт старый путь. Отказ
    /// сервера (400 и т.п.) и обрыв связи — исключением, как в старом пути.</summary>
    private async Task<bool> TryPayViaServerAsync(ClientOption client, double amount, Button button)
    {
        if (_serverPayDebtMissing)
            return false;

        var api = App.AppHost?.Services.GetService<ClientDebtsApiService>();
        if (api == null)
            return false;

        var signature = client.Id + "|" + amount.ToString("0.00", CultureInfo.InvariantCulture);
        if (_pendingPayKey == null || _pendingPaySignature != signature)
        {
            _pendingPayKey = Guid.NewGuid().ToString();
            _pendingPaySignature = signature;
        }
        var key = _pendingPayKey;

        var contentBefore = button.Content;
        button.Content = Tr.T("Оплата…", "Төлөм…", "Paying…", "Ödeniyor…", "To'lanmoqda…");
        var watch = Stopwatch.StartNew();
        PayDebtResult result;
        try
        {
            result = await api.PayDebtAsync(client.Id, amount, "cash", PosApp.ActiveShiftId, key).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ClientDebtsApiService.IsEndpointMissing(ex))
        {
            _serverPayDebtMissing = true;
            _pendingPayKey = null;
            PosLogger.Log($"Pay-debt: сервер не знает clients/{{id}}/pay-debt/ ({ex.StatusCode}) — старый путь по взносам.", "PAYMENT");
            return false;
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 and not 408 and not 429)
        {
            // Сервер оплату отклонил — следующее нажатие будет новой операцией, с новым ключом.
            _pendingPayKey = null;
            throw;
        }
        finally
        {
            button.Content = contentBefore;
        }

        watch.Stop();
        _pendingPayKey = null;

        // Итоги смены этой кассы — как у старого пути, одной записью на платёж. Ключ операции —
        // Idempotency-Key: повтор того же платежа второй записью не ляжет.
        ShiftEventsStore.Record(
            ShiftEventsStore.KindDebtPayment,
            PosApp.ActiveShiftId,
            "pay-debt:" + key,
            result.Paid,
            client.DisplayName);
        PosLogger.Log(
            $"Pay-debt одной суммой: клиент {client.Id}, оплачено {result.Paid:0.00}, остаток {result.Left:0.00}, " +
            $"взносов {result.AppliedCount}, {watch.ElapsedMilliseconds} мс{(result.Replayed ? ", повтор по ключу" : "")}.",
            "PAYMENT");

        await LoadDebtSalesAsync(client.Id).ConfigureAwait(true);

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var paidText = result.Paid.ToString("N2", ru);
        var leftText = result.Left.ToString("N2", ru);
        SuccessMessage = result.Left <= 0.005
            ? Tr.T($"Оплачено {paidText} сом. Долг погашен полностью.",
                $"{paidText} сом төлөндү. Карыз толугу менен жабылды.",
                $"Paid {paidText} som. The debt is fully paid off.",
                $"{paidText} som ödendi. Borç tamamen kapandı.",
                $"{paidText} so'm to'landi. Qarz to'liq yopildi.")
            : Tr.T($"Оплачено {paidText} сом. Остаток долга: {leftText} сом.",
                $"{paidText} сом төлөндү. Карыздын калдыгы: {leftText} сом.",
                $"Paid {paidText} som. Remaining debt: {leftText} som.",
                $"{paidText} som ödendi. Kalan borç: {leftText} som.",
                $"{paidText} so'm to'landi. Qarz qoldig'i: {leftText} so'm.");
        return true;
    }

    /// <summary>Запасной путь «одной суммой» для сервера без pay-debt: по строкам, от старых долгов
    /// к новым, тем же старым способом (по взносам), что и кнопка «Оплатить» у строки.</summary>
    private async Task PayAllLegacyAsync(ClientOption client, double amount)
    {
        var remaining = amount;
        var paid = 0.0;
        string? stopReason = null;

        foreach (var row in DebtSales.OrderBy(r => r.CreatedAt).ToList())
        {
            if (remaining <= 0.005)
                break;
            var part = Math.Min(remaining, row.Amount);
            if (part <= 0.005)
                continue;

            if (!await PayRowLegacyAsync(row, part, PayAllButton).ConfigureAwait(true))
            {
                stopReason = ErrorMessage;
                break;
            }

            paid += part;
            remaining -= part;

            // «Погашено k из N взносов — нажмите ещё раз»: дальше не идём, чтобы сумма не ушла
            // в следующие долги, пока этот не закрыт.
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                stopReason = ErrorMessage;
                break;
            }
        }

        await LoadDebtSalesAsync(client.Id).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(stopReason))
            ErrorMessage = stopReason;

        if (paid > 0.005)
        {
            var paidText = paid.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
            SuccessMessage = Tr.T($"Оплачено {paidText} сом.", $"{paidText} сом төлөндү.", $"Paid {paidText} som.",
                $"{paidText} som ödendi.", $"{paidText} so'm to'landi.");
        }
    }
}
