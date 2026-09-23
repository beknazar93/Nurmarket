using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public sealed class DebtSaleRow
{
    public string Id { get; init; } = "";
    /// <summary>ID сделки (deal) продажи — именно по нему проходит оплата долга
    /// (api/main/clientdeals/{dealId}/pay/), а не по id самой продажи. Уточняется/обновляется
    /// при загрузке списка через ClientDealGetAsync, поэтому не init-only.</summary>
    public string DealId { get; set; } = "";
    public string DateDisplay { get; init; } = "";
    public string FirstItemName { get; init; } = "";
    /// <summary>Реальный остаток долга по сделке (remaining_debt) — уточняется при загрузке
    /// списка, а не берётся из total/debt_amount самой продажи, которые не отражают уже
    /// внесённые через сделку платежи.</summary>
    public string AmountDisplay { get; set; } = "";
    public double Amount { get; set; }

    /// <summary>ID конкретного взноса (installment) из графика платежей сделки — оплата в
    /// NurCRM всегда идёт по конкретному взносу, а не по сделке в целом (см. PosPayDebtAsync).
    /// Первый непогашенный взнос из <see cref="UnpaidInstallmentIds"/>, для обратной совместимости.</summary>
    public string? InstallmentId { get; set; }

    /// <summary>2026-09-15, живой баг ("долг полностью не закрывается, а всего лишь
    /// уменьшается") — сделка может иметь НЕСКОЛЬКО непогашенных взносов одновременно (график
    /// платежей), а не один на всю сумму продажи; погашение только первого взноса уменьшает
    /// remaining_debt лишь на его часть. Полное закрытие строки требует погасить ВСЕ взносы
    /// по очереди — список заполняется при загрузке через ClientDealGetAsync.</summary>
    public List<string> UnpaidInstallmentIds { get; set; } = new();

    /// <summary>Исходная сумма долга (для истории — сколько было оплачено всего).</summary>
    public string OriginalAmountDisplay { get; set; } = "";

    /// <summary>Редактируемая сумма к оплате — по умолчанию весь долг, но кассир может
    /// уменьшить для частичного погашения.</summary>
    public string AmountToPayText { get; set; } = "";
}

/// <summary>Оплата ранее оформленных продаж «в долг» (полностью или частично): выбор клиента,
/// список его непогашенных продаж и оплата — /api/main/clientdeals/{dealId}/pay/ (по deal_id
/// продажи, не по id самой продажи — см. PosPayDebtAsync).</summary>
public partial class PayDebtDialog : Window, INotifyPropertyChanged
{
    private readonly IClientsApiService _clientsApi;
    private readonly ISalesApiService _salesApi;
    private List<ClientOption> _allClients = new();
    private bool _clientsLoaded;

    private string _clientSearchText = "";
    private ClientOption? _selectedClient;
    private string _errorMessage = "";
    private bool _showHistory;

    public ObservableCollection<ClientOption> ClientSearchResults { get; } = new();
    public ObservableCollection<DebtSaleRow> DebtSales { get; } = new();
    /// <summary>Уже полностью погашенные долги клиента — сервер не удаляет их и не
    /// перестаёт числить продажу как status=debt, поэтому храним их здесь отдельно вместо
    /// того чтобы просто молча выкидывать из списка непогашенных.</summary>
    public ObservableCollection<DebtSaleRow> DebtHistory { get; } = new();

    public bool ShowHistory
    {
        get => _showHistory;
        set
        {
            if (_showHistory == value)
                return;
            _showHistory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowUnpaid));
        }
    }

    public bool ShowUnpaid => !ShowHistory;

    public string UnpaidTabLabel => Tr.T("Непогашенные", "Төлөнбөгөндөр", "Unsettled", "Kapatılmamış", "Yopilmagan");
    public string HistoryTabLabel => Tr.T("История", "Тарых", "History", "Geçmiş", "Tarix");

    public ICommand SelectClientCommand { get; }

    private static bool IsKyrgyz => UserPreferences.Instance.Language == AppLanguage.Kyrgyz;

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public PayDebtDialog() : this(ResolveService<IClientsApiService>(), ResolveService<ISalesApiService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public PayDebtDialog(IClientsApiService clientsApi, ISalesApiService salesApi)
    {
        _clientsApi = clientsApi;
        _salesApi = salesApi;
        InitializeComponent();
        this.ClampToScreenHeight();
        DataContext = this;

        Title = Tr.T("Оплата долга", "Карыз төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash");
        HeaderTitleText.Text = Tr.T("Оплата долга", "Карыз төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash");
        ClientLabelText.Text = Tr.T("Клиент", "Клиент", "Client", "Müşteri", "Mijoz");
        ClientSearchBox.Watermark = Tr.T("Поиск по имени или телефону…", "Аты же телефону боюнча издөө…", "Search by name or phone…", "İsim veya telefonla ara…", "Ism yoki telefon bo'yicha qidirish…");
        DebtSalesTitleText.Text = Tr.T("Непогашенные продажи", "Төлөнбөгөн сатуулар", "Unsettled sales", "Kapatılmamış satışlar", "Yopilmagan sotuvlar");
        MinimizeButton.SetValue(ToolTip.TipProperty, Tr.T("Свернуть", "Кичирейтүү", "Minimize", "Küçült", "Yig'ish"));
        CloseWindowButton.SetValue(ToolTip.TipProperty, Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));

        SelectClientCommand = new RelayCommand<ClientOption>(SelectClient);
        Opened += async (_, _) => await EnsureClientsLoadedAsync().ConfigureAwait(true);
    }

    public string ClientSearchText
    {
        get => _clientSearchText;
        set
        {
            if (_clientSearchText == value)
                return;
            _clientSearchText = value;
            OnPropertyChanged();
            ApplyClientFilter();
        }
    }

    public ClientOption? SelectedClient
    {
        get => _selectedClient;
        private set
        {
            _selectedClient = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedClient));
            OnPropertyChanged(nameof(SelectedClientName));
        }
    }

    public bool HasSelectedClient => _selectedClient != null;
    public string SelectedClientName => _selectedClient?.DisplayName ?? "";
    public bool HasClientSearchResults => ClientSearchResults.Count > 0;

    public string TotalOwedText =>
        $"{Tr.T("Итого к оплате", "Жалпы төлөнө турган сумма", "Total due", "Ödenecek toplam", "To'lov uchun jami")}: " +
        $"{DebtSales.Sum(s => s.Amount).ToString("0.00", CultureInfo.InvariantCulture)} сом";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public string PayButtonLabel => Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash");

    private void SelectClient(ClientOption? client)
    {
        if (client == null)
            return;
        SelectedClient = client;
        ClientSearchText = "";
        _ = LoadDebtSalesAsync(client.Id);
    }

    private void ChangeClientButton_Click(object? sender, RoutedEventArgs e)
    {
        SelectedClient = null;
        DebtSales.Clear();
        DebtHistory.Clear();
        ClientSearchText = "";
        ClientSearchBox.Focus();
    }

    private async Task EnsureClientsLoadedAsync()
    {
        if (_clientsLoaded)
            return;

        try
        {
            var raw = await _clientsApi.GetClientsAsync(null).ConfigureAwait(true);
            _allClients = raw
                .Where(el => TryGetString(el, "type") is null or "client")
                .Select(ToClientOption)
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _clientsLoaded = true;
            ApplyClientFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось загрузить клиентов: ", "Клиенттерди жүктөө мүмкүн болгон жок: ", "Could not load clients: ", "Müşteriler yüklenemedi: ", "Mijozlarni yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"PayDebt clients load failed: {ex}", "WARNING");
        }
    }

    private async Task LoadDebtSalesAsync(string clientId)
    {
        ErrorMessage = "";
        DebtSales.Clear();
        DebtHistory.Clear();
        OnPropertyChanged(nameof(TotalOwedText));
        try
        {
            var raw = await _salesApi.PosDebtSalesAsync(clientId).ConfigureAwait(true);
            var rows = raw.Select(ToDebtSaleRow).OrderByDescending(r => r.DateDisplay).ToList();

            // Сервер не обновляет status самой продажи после оплаты через сделку (deal) —
            // продажа может годами висеть как "debt", хотя реально уже полностью погашена.
            // Уточняем остаток напрямую по сделке: непогашенные остаются в основном списке,
            // а полностью оплаченные переезжают в историю вместо того чтобы просто исчезать.
            await Task.WhenAll(rows.Select(r => ResolveRealRemainingDebtAsync(clientId, r))).ConfigureAwait(true);

            foreach (var row in rows)
            {
                if (row.Amount > 0.005)
                    DebtSales.Add(row);
                else
                    DebtHistory.Add(row);
            }
            OnPropertyChanged(nameof(TotalOwedText));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось загрузить долги клиента: ", "Клиенттин карызын жүктөө мүмкүн болгон жок: ", "Could not load the client's debts: ", "Müşteri borçları yüklenemedi: ", "Mijozning qarzlarini yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"PayDebt sales load failed: {ex}", "WARNING");
        }
    }

    /// <summary>Догружает deal_id (если не пришёл со списком) и подставляет реальный
    /// remaining_debt по сделке вместо исходной суммы продажи. При любой ошибке (сеть,
    /// сделка не найдена и т.п.) оставляет строку как есть — она просто ведёт себя
    /// по-старому и покажет ошибку сервера при попытке оплаты, а не исчезнет молча.</summary>
    private async Task ResolveRealRemainingDebtAsync(string clientId, DebtSaleRow row)
    {
        try
        {
            var dealId = row.DealId;
            if (string.IsNullOrWhiteSpace(dealId))
            {
                var saleDetail = await _salesApi.PosSaleGetAsync(row.Id).ConfigureAwait(true);
                dealId = saleDetail.ValueKind == JsonValueKind.Object
                    && saleDetail.TryGetProperty("deal_id", out var dealIdEl)
                    && dealIdEl.ValueKind == JsonValueKind.String
                        ? dealIdEl.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(dealId))
                    return;
                row.DealId = dealId;
            }

            var deal = await _salesApi.ClientDealGetAsync(clientId, dealId).ConfigureAwait(true);
            if (deal.ValueKind != JsonValueKind.Object)
                return;

            row.UnpaidInstallmentIds = FindUnpaidInstallmentIds(deal);
            row.InstallmentId = row.UnpaidInstallmentIds.Count > 0 ? row.UnpaidInstallmentIds[0] : null;

            if (!deal.TryGetProperty("remaining_debt", out var remainingEl))
                return;

            var remainingText = remainingEl.ValueKind switch
            {
                JsonValueKind.String => remainingEl.GetString(),
                JsonValueKind.Number => remainingEl.GetRawText(),
                _ => null,
            };
            if (remainingText is null
                || !double.TryParse(remainingText, NumberStyles.Any, CultureInfo.InvariantCulture, out var remaining))
                return;

            row.Amount = remaining;
            row.AmountDisplay = remaining.ToString("0.00", CultureInfo.InvariantCulture) + " сом";
            row.AmountToPayText = remaining.ToString("0.00", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PayDebt remaining-debt resolve skipped for sale {row.Id}: {ex.GetType().Name}", "DEBUG");
        }
    }

    private async void PayDebt_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DebtSaleRow row } button)
            return;

        ErrorMessage = "";

        var raw = (row.AmountToPayText ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            ErrorMessage = Tr.T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Specify the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
            return;
        }

        if (amount > row.Amount + 0.005)
        {
            ErrorMessage = Tr.T(
                $"Сумма не может превышать остаток долга ({row.AmountDisplay}).",
                $"Сумма карыздын калдыгынан ({row.AmountDisplay}) ашык болбошу керек.",
                $"The amount cannot exceed the remaining debt ({row.AmountDisplay}).",
                $"Tutar, kalan borcu ({row.AmountDisplay}) aşamaz.",
                $"Summa qarz qoldig'idan ({row.AmountDisplay}) oshib ketmasligi kerak.");
            return;
        }

        button.IsEnabled = false;
        try
        {
            // Список долгов (GET .../sales/?status=debt) не отдаёт deal_id в каждой
            // строке — только одиночная карточка продажи его содержит. Догружаем лениво,
            // только когда реально нужно платить, а не для каждой строки списка.
            var dealId = row.DealId;
            if (string.IsNullOrWhiteSpace(dealId))
            {
                var saleDetail = await _salesApi.PosSaleGetAsync(row.Id).ConfigureAwait(true);
                dealId = saleDetail.ValueKind == JsonValueKind.Object
                    && saleDetail.TryGetProperty("deal_id", out var dealIdEl)
                    && dealIdEl.ValueKind == JsonValueKind.String
                        ? dealIdEl.GetString()
                        : null;
            }

            if (string.IsNullOrWhiteSpace(dealId))
            {
                ErrorMessage = Tr.T(
                    "У этой продажи нет привязанной сделки — оплата долга недоступна.",
                    "Бул сатууга байланган келишим жок — карызды төлөө мүмкүн эмес.",
                    "This sale has no linked deal — debt payment is unavailable.",
                    "Bu satışa bağlı bir anlaşma yok — borç ödemesi yapılamaz.",
                    "Bu sotuvga bog'langan bitim yo'q — qarzni to'lash mumkin emas.");
                return;
            }

            if (_selectedClient is null)
                return;

            // Обычно UnpaidInstallmentIds уже подтянут заранее в ResolveRealRemainingDebtAsync
            // (при загрузке списка) — но если dealId только что догрузился лениво выше (row.DealId
            // изначально был пуст), сделку по нему мы ещё не запрашивали.
            var unpaidInstallmentIds = row.UnpaidInstallmentIds;
            if (unpaidInstallmentIds.Count == 0)
            {
                var deal = await _salesApi.ClientDealGetAsync(_selectedClient.Id, dealId).ConfigureAwait(true);
                unpaidInstallmentIds = FindUnpaidInstallmentIds(deal);
            }

            // 2026-09-15, живой баг ("долг полностью не закрывается, а всего лишь уменьшается") —
            // у сделки может быть НЕСКОЛЬКО непогашенных взносов (график платежей), а не один на
            // всю сумму продажи; погашение только первого взноса уменьшало remaining_debt лишь на
            // его часть. "К оплате" по умолчанию предзаполнено полным остатком (row.Amount) — клик
            // на "Оплатить" без ручного уменьшения суммы означает "закрыть полностью", и тогда
            // гасим ВСЕ непогашенные взносы по очереди (каждый — отдельным вызовом без amount,
            // как в подтверждённом живым захватом рабочем примере). При настоящей частичной оплате
            // (сумма уменьшена вручную) по-прежнему гасим только первый взнос с явной суммой —
            // этот случай живым захватом не подтверждён.
            //
            // 2026-09-15, живой баг ("зависает при оплате") — у старых тестовых сделок (со многими
            // прошлыми частичными оплатами) взносов оказалось МНОГО; последовательный await по
            // каждому без ограничения по времени/количеству выглядел как настоящее зависание
            // диалога на давних продажах. Теперь — жёсткий предел по количеству и общему времени:
            // если не успели за отведённое время, честно сообщаем, сколько погашено, а не висим
            // молча дальше.
            // Погашение долга в итогах смены на сервере не отражается — записываем сами,
            // одной записью на весь платёж, а не по каждому взносу (см. ShiftEventsStore).

            var isFullPayment = amount >= row.Amount - 0.005;
            if (isFullPayment && unpaidInstallmentIds.Count > 0)
            {
                const int maxInstallmentsPerClick = 30;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var paidCount = 0;
                foreach (var id in unpaidInstallmentIds.Take(maxInstallmentsPerClick))
                {
                    if (DateTime.UtcNow > deadline)
                        break;
                    button.Content = Tr.T(
                        $"Оплата {paidCount + 1}/{unpaidInstallmentIds.Count}…",
                        $"Төлөм {paidCount + 1}/{unpaidInstallmentIds.Count}…",
                        $"Paying {paidCount + 1}/{unpaidInstallmentIds.Count}…",
                        $"Ödeniyor {paidCount + 1}/{unpaidInstallmentIds.Count}…",
                        $"To'lanmoqda {paidCount + 1}/{unpaidInstallmentIds.Count}…");
                    await _salesApi.PosPayDebtAsync(_selectedClient.Id, dealId, id, null).ConfigureAwait(true);
                    paidCount++;
                }
                button.Content = PayButtonLabel;

                if (paidCount < unpaidInstallmentIds.Count)
                {
                    ErrorMessage = Tr.T(
                        $"Погашено {paidCount} из {unpaidInstallmentIds.Count} взносов — слишком много для одного клика. Нажмите «Оплатить» ещё раз для остальных.",
                        $"{unpaidInstallmentIds.Count} төлөмдүн {paidCount} өтөлдү — бир басууга өтө көп. Калгандары үчүн «Төлөө» дагы басыңыз.",
                        $"Paid off {paidCount} of {unpaidInstallmentIds.Count} installments — too many for one click. Click «Pay» again for the rest.",
                        $"{unpaidInstallmentIds.Count} taksitten {paidCount} tanesi ödendi — tek tıklama için çok fazla. Kalanlar için «Öde»ye tekrar tıklayın.",
                        $"{unpaidInstallmentIds.Count} ta bo'lib to'lashdan {paidCount} tasi to'landi — bitta bosish uchun juda ko'p. Qolganlari uchun «To'lash»ni yana bosing.");
                }
            }
            else
            {
                var firstInstallmentId = unpaidInstallmentIds.Count > 0 ? unpaidInstallmentIds[0] : null;
                await _salesApi.PosPayDebtAsync(
                    _selectedClient.Id, dealId, firstInstallmentId, isFullPayment ? null : amount).ConfigureAwait(true);
            }
            // Погашение долга в итогах смены на сервере не отражается — записываем сами, одной
            // записью на весь платёж, а не по каждому взносу.
            //
            // 2026-09-23: запись перенесена СЮДА, после фактической оплаты. Раньше она стояла до
            // вызовов PosPayDebtAsync: если сеть отваливалась, в Z-отчёте всё равно значилось
            // «Оплата долгов: N», а денег в кассе не было. И передавался row.DealId вместо
            // реально использованного dealId, который выше мог быть догружен лениво.
            ShiftEventsStore.Record(
                ShiftEventsStore.KindDebtPayment,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(dealId),
                amount,
                _selectedClient?.DisplayName);

            // Перегружаем с сервера, а не правим локально — сумма частичной оплаты
            // и статус (может стать "paid") должны отражать серверную истину.
            if (_selectedClient != null)
                await LoadDebtSalesAsync(_selectedClient.Id).ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            PosLogger.Log($"Pay-debt failed for sale {row.Id}: {ex}", "PAYMENT");
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось оплатить долг: ", "Карызды төлөө мүмкүн болгон жок: ", "Could not pay the debt: ", "Borç ödenemedi: ", "Qarzni to'lab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Pay-debt failed for sale {row.Id}: {ex}", "PAYMENT");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ApplyClientFilter()
    {
        ClientSearchResults.Clear();
        var query = _clientSearchText?.Trim() ?? "";
        IEnumerable<ClientOption> source = _allClients;
        if (query.Length > 0)
            source = source.Where(c => c.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        foreach (var client in source.Take(30))
            ClientSearchResults.Add(client);
        OnPropertyChanged(nameof(HasClientSearchResults));
    }

    private static ClientOption ToClientOption(JsonElement element)
    {
        var id = TryGetString(element, "id") ?? "";
        var name = TryGetString(element, "full_name") ?? "";
        var phone = TryGetString(element, "phone") ?? "";
        var display = string.IsNullOrWhiteSpace(phone) ? name : $"{name} · {phone}";
        return new ClientOption { Id = id, DisplayName = display };
    }

    private static DebtSaleRow ToDebtSaleRow(JsonElement element)
    {
        var createdAt = TryGetString(element, "created_at");
        DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);

        var amount = TryGetDouble(element, "debt_amount") ?? TryGetDouble(element, "total") ?? 0;
        var amountText = amount.ToString("0.00", CultureInfo.InvariantCulture);

        return new DebtSaleRow
        {
            Id = TryGetString(element, "id") ?? "",
            DealId = TryGetString(element, "deal_id") ?? "",
            DateDisplay = created == default ? "" : created.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            FirstItemName = TryGetString(element, "first_item_name") ?? "",
            Amount = amount,
            AmountDisplay = amountText + " сом",
            AmountToPayText = amountText,
            OriginalAmountDisplay = amountText + " сом",
        };
    }

    /// <summary>2026-09-15: подтверждено живым захватом DevTools реального успешного платежа
    /// (200 OK) в веб-CRM — долг в NurCRM хранится как график ПЛАТЕЖЕЙ-ВЗНОСОВ (installments)
    /// внутри сделки, и оплата всегда идёт по конкретному installment_id, а не "по сделке в
    /// целом". Возвращает id ВСЕХ ещё не погашенных взносов (paid_on пуст), в порядке графика —
    /// живой баг ("долг уменьшается, но не закрывается") показал, что у одной сделки таких
    /// взносов может быть несколько, и для полного закрытия нужно погасить их все по очереди.</summary>
    private static List<string> FindUnpaidInstallmentIds(JsonElement deal)
    {
        var result = new List<string>();
        if (deal.ValueKind != JsonValueKind.Object
            || !deal.TryGetProperty("installments", out var installments)
            || installments.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var installment in installments.EnumerateArray())
        {
            if (installment.ValueKind != JsonValueKind.Object)
                continue;

            if (installment.TryGetProperty("paid_on", out var paidOn)
                && paidOn.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(paidOn.GetString()))
                continue;

            if (installment.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(idEl.GetString()))
                result.Add(idEl.GetString()!);
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? TryGetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private void ShowUnpaidTab_Click(object? sender, RoutedEventArgs e) => ShowHistory = false;
    private void ShowHistoryTab_Click(object? sender, RoutedEventArgs e) => ShowHistory = true;

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
