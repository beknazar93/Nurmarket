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
    /// <summary>2026-09-28: момент продажи — запасной путь «Погасить одной суммой» гасит от
    /// старых долгов к новым, как и сервер (DateDisplay как строка по дате не сортируется).</summary>
    public DateTimeOffset CreatedAt { get; init; }
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

    public string UnpaidTabLabel => Tr.T("Непогашенные", "Төлөнбөгөндөр", "Unpaid", "Ödenmemiş", "To'lanmagan");
    public string HistoryTabLabel => Tr.T("История", "Тарых", "History", "Geçmiş", "Tarix");

    public ICommand SelectClientCommand { get; }

    private static bool IsKyrgyz => UserPreferences.Instance.Language == AppLanguage.Kyrgyz;

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    /// <summary>2026-10-05: телефон — подпись «Погасить одной суммой» строкой выше поля и кнопки; в таблице
    /// долгов вместо колонки «Товар» — дата, сумма и «Оплатить»; без кнопки «свернуть».</summary>
    private void ApplyNarrowLayout(bool narrow)
    {
        RootGrid.Margin = narrow ? new Avalonia.Thickness(8, 6, 8, 8) : new Avalonia.Thickness(16, 12, 16, 16);
        MinimizeButton.IsVisible = !narrow;
        if (narrow)
        {
            PayAllGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            PayAllGrid.RowDefinitions = new RowDefinitions("Auto,6,Auto");
            Grid.SetColumnSpan(PayAllLabelText, 2);
            PayAllLabelText.Margin = default;
            Grid.SetRow(PayAllAmountBox, 2);
            Grid.SetColumn(PayAllAmountBox, 0);
            Grid.SetRow(PayAllButton, 2);
            Grid.SetColumn(PayAllButton, 1);
        }
        else
        {
            PayAllGrid.ColumnDefinitions = new ColumnDefinitions("Auto,160,Auto");
            PayAllGrid.RowDefinitions = new RowDefinitions();
            Grid.SetColumnSpan(PayAllLabelText, 1);
            PayAllLabelText.Margin = new Avalonia.Thickness(0, 0, 10, 0);
            Grid.SetRow(PayAllAmountBox, 0);
            Grid.SetColumn(PayAllAmountBox, 1);
            Grid.SetRow(PayAllButton, 0);
            Grid.SetColumn(PayAllButton, 2);
        }

        // Непогашенные: Дата, Товар, К оплате, «Оплатить». История: Дата, Товар, Оплачено.
        if (UnpaidGrid.Columns.Count >= 4)
        {
            UnpaidGrid.Columns[0].Width = new DataGridLength(narrow ? 138 : 140);
            UnpaidGrid.Columns[1].IsVisible = !narrow;
            UnpaidGrid.Columns[2].Width = narrow ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(150);
            UnpaidGrid.Columns[3].Width = new DataGridLength(narrow ? 96 : 120);
        }
        if (HistoryGrid.Columns.Count >= 3)
        {
            HistoryGrid.Columns[0].Width = new DataGridLength(narrow ? 138 : 140);
            HistoryGrid.Columns[2].Width = new DataGridLength(narrow ? 112 : 150);
        }
    }

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
        // 2026-10-05, снимок владельца с телефона: кнопка «Погасить» уходила за край, в таблице видна одна
        // строка и обрезанная дата. На телефоне окно во весь экран (WindowLayerHost) и перестраивается здесь.
        AvaloniaHost.Services.NarrowLayout.Attach(this, 700, ApplyNarrowLayout);

        Title = Tr.T("Оплата долга", "Карыз төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash");
        HeaderTitleText.Text = Tr.T("Оплата долга", "Карыз төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash");
        ClientLabelText.Text = Tr.T("Клиент", "Клиент", "Client", "Müşteri", "Mijoz");
        ClientSearchBox.Watermark = Tr.T("Поиск по имени или телефону…", "Аты же телефону боюнча издөө…", "Search by name or phone…", "İsim veya telefonla ara…", "Ism yoki telefon bo'yicha qidirish…");
        DebtSalesTitleText.Text = Tr.T("Непогашенные продажи", "Төлөнбөгөн сатуулар", "Unpaid sales", "Ödenmemiş satışlar", "To'lanmagan sotuvlar");
        MinimizeButton.SetValue(ToolTip.TipProperty, Tr.T("Свернуть", "Кичирейтүү", "Minimize", "Küçült", "Yig'ish"));
        CloseWindowButton.SetValue(ToolTip.TipProperty, Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));

        SelectClientCommand = new RelayCommand<ClientOption>(SelectClient);
        // 2026-09-28: блок «Погасить одной суммой» (PayDebtDialog.OneSum.cs).
        InitOneSumPayment();
        Opened += async (_, _) => await EnsureClientsLoadedAsync().ConfigureAwait(true);
        // 2026-10-04: фоновая догрузка клиентов останавливается вместе с окном.
        Closed += (_, _) => _closedCts.Cancel();
    }

    /// <summary>2026-10-04: подпись сбоя для журнала аварии (ServerOutageMonitor).</summary>
    private static string DebtContext => Tr.T("оплата долга", "карыз төлөө", "debt payment", "borç ödemesi", "qarzni to'lash");

    /// <summary>2026-10-04: отмена фоновой догрузки списка клиентов при закрытии окна.</summary>
    private readonly CancellationTokenSource _closedCts = new();

    /// <summary>2026-10-04: сервер не ответил на первую порцию данных (или касса уже в аварии) —
    /// текст для кассира: что делать, а не «Превышено время ожидания» через 55 с.</summary>
    private static string ServerDownMessage(ServerNotAnsweringException? ex) =>
        ex is { Throttled: true } ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.DebtPaymentUnavailableInOutage;

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
        $"{Tr.T("Итого к оплате", "Жалпы төлөнө турган сумма", "Total due", "Ödenecek toplam", "Jami to'lanadigan summa")}: " +
        $"{DebtSales.Sum(s => s.Amount).ToString("0.00", CultureInfo.InvariantCulture)}" + Tr.T(" сом", " сом", " som", " som", " so'm");

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    private string _successMessage = "";

    /// <summary>«Оплачено N сом» после успешной оплаты; сбрасывается при следующем действии.</summary>
    public string SuccessMessage
    {
        get => _successMessage;
        private set { _successMessage = value; OnPropertyChanged(); }
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

    /// <summary>2026-10-04, отчёт «офлайн и сбои сервера»: раньше окно ждало ВСЕ страницы клиентов
    /// (до 40) с таймаутом 55 с на запрос — при молчащем сервере кассир почти минуту смотрел на пустое
    /// окно. Теперь первая страница — не дольше 5 с без ответа сервера (ServerAnswerWait; сбой — авария
    /// и текст «оплата долга недоступна»), остальные догружаются фоном, окно ими не блокируется.</summary>
    private async Task EnsureClientsLoadedAsync()
    {
        if (_clientsLoaded)
            return;

        if (OfflineModeHelper.IsServerOutage)
        {
            ErrorMessage = ServerDownMessage(null);
            return;
        }

        try
        {
            var (firstPage, hasNext) = await ServerAnswerWait.FirstPortionAsync(DebtContext,
                ct => _clientsApi.GetClientsPageAsync(1, null, ct), _closedCts.Token).ConfigureAwait(true);
            _allClients = ToClientOptions(firstPage);
            _clientsLoaded = true;
            ApplyClientFilter();
            if (hasNext)
                _ = LoadRemainingClientsAsync(_closedCts.Token);
        }
        catch (ServerNotAnsweringException ex)
        {
            ErrorMessage = ServerDownMessage(ex);
        }
        catch (OperationCanceledException) when (_closedCts.IsCancellationRequested)
        {
            // окно закрыли во время загрузки
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось загрузить клиентов: ", "Клиенттерди жүктөө мүмкүн болгон жок: ", "Could not load clients: ", "Müşteriler yüklenemedi: ", "Mijozlarni yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"PayDebt clients load failed: {ex}", "WARNING");
        }
    }

    /// <summary>2026-10-04: клиенты в списке окна — те же правила, что были в EnsureClientsLoadedAsync.</summary>
    private static List<ClientOption> ToClientOptions(IEnumerable<JsonElement> raw) =>
        raw.Where(el => TryGetString(el, "type") is null or "client")
            .Select(ToClientOption)
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>2026-10-04: страницы клиентов со второй — фоном, по одной: каждая сразу попадает в
    /// поиск. Обычные таймауты: никто не ждёт. Сбой — в журнал и строкой в окне, загруженное остаётся.</summary>
    private async Task LoadRemainingClientsAsync(CancellationToken ct)
    {
        var loaded = 0;
        try
        {
            for (var page = 2; !ct.IsCancellationRequested; page++)
            {
                var (items, hasNext) = await _clientsApi.GetClientsPageAsync(page, null, ct).ConfigureAwait(true);
                if (items.Count == 0)
                    break;

                var ids = _allClients.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = ToClientOptions(items).Where(c => ids.Add(c.Id)).ToList();
                if (added.Count > 0)
                {
                    _allClients = _allClients.Concat(added)
                        .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    loaded += added.Count;
                    ApplyClientFilter();
                }

                if (!hasNext)
                    break;
            }

            if (loaded > 0)
                PosLogger.Log($"Оплата долга: фоном догружено клиентов {loaded}, всего {_allClients.Count}.", "SALES");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: список клиентов загружен не полностью ({_allClients.Count}): {ex.Message}", "WARNING");
            if (string.IsNullOrEmpty(ErrorMessage))
                ErrorMessage = Tr.T(
                    "Список клиентов загружен не полностью — сервер не ответил. Если клиента нет в поиске, откройте окно ещё раз.",
                    "Клиенттердин тизмеси толук жүктөлгөн жок — сервер жооп берген жок. Клиент издөөдө жок болсо, терезени кайра ачыңыз.",
                    "The client list was not fully loaded — the server did not respond. If the client is not in the search, reopen the window.",
                    "Müşteri listesi tam yüklenmedi — sunucu yanıt vermedi. Müşteri aramada yoksa pencereyi yeniden açın.",
                    "Mijozlar ro'yxati to'liq yuklanmadi — server javob bermadi. Mijoz qidiruvda bo'lmasa, oynani qayta oching.");
        }
    }

    private async Task LoadDebtSalesAsync(string clientId)
    {
        ErrorMessage = "";
        SuccessMessage = "";
        DebtSales.Clear();
        DebtHistory.Clear();
        OnPropertyChanged(nameof(TotalOwedText));
        // 2026-10-04: сервер уже признан лежащим — не ждём его ещё раз.
        if (OfflineModeHelper.IsServerOutage)
        {
            ErrorMessage = ServerDownMessage(null);
            return;
        }

        try
        {
            // 2026-10-04: долги клиента и их остатки по сделкам — одна «первая порция»: не дольше 5 с
            // без ответа сервера (окно отсчитывается заново после каждого ответа), сбой — авария и
            // текст. Раньше — до 55 с на каждый запрос. Не дождались остатков — список не показываем:
            // суммы продаж без уточнения по сделке завышены (уже внесённые платежи в них не видны).
            var rows = await ServerAnswerWait.FirstPortionAsync(DebtContext, async ct =>
            {
                var raw = await _salesApi.PosDebtSalesAsync(clientId, ct).ConfigureAwait(true);
                var list = raw.Select(ToDebtSaleRow).OrderByDescending(r => r.DateDisplay).ToList();

                // Сервер не обновляет status самой продажи после оплаты через сделку (deal) —
                // продажа может годами висеть как "debt", хотя реально уже полностью погашена.
                // Уточняем остаток напрямую по сделке: непогашенные остаются в основном списке,
                // а полностью оплаченные переезжают в историю вместо того чтобы просто исчезать.
                await Task.WhenAll(list.Select(r => ResolveRealRemainingDebtAsync(clientId, r, ct))).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                return list;
            }, _closedCts.Token).ConfigureAwait(true);

            foreach (var row in rows)
            {
                if (row.Amount > 0.005)
                    DebtSales.Add(row);
                else
                    DebtHistory.Add(row);
            }
            OnPropertyChanged(nameof(TotalOwedText));
            // 2026-09-28: сумма «Погасить одной суммой» — весь долг по умолчанию.
            OnDebtSalesReloaded();
        }
        catch (ServerNotAnsweringException ex)
        {
            ErrorMessage = ServerDownMessage(ex);
        }
        catch (OperationCanceledException) when (_closedCts.IsCancellationRequested)
        {
            // окно закрыли во время загрузки
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
    private async Task ResolveRealRemainingDebtAsync(string clientId, DebtSaleRow row, CancellationToken ct = default)
    {
        try
        {
            var dealId = row.DealId;
            if (string.IsNullOrWhiteSpace(dealId))
            {
                var saleDetail = await _salesApi.PosSaleGetAsync(row.Id, ct).ConfigureAwait(true);
                dealId = saleDetail.ValueKind == JsonValueKind.Object
                    && saleDetail.TryGetProperty("deal_id", out var dealIdEl)
                    && dealIdEl.ValueKind == JsonValueKind.String
                        ? dealIdEl.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(dealId))
                    return;
                row.DealId = dealId;
            }

            var deal = await _salesApi.ClientDealGetAsync(clientId, dealId, ct).ConfigureAwait(true);
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
            row.AmountDisplay = remaining.ToString("0.00", CultureInfo.InvariantCulture) + Tr.T(" сом", " сом", " som", " som", " so'm");
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
        SuccessMessage = "";

        var raw = (row.AmountToPayText ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            ErrorMessage = Tr.T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Enter the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
            return;
        }

        if (amount > row.Amount + 0.005)
        {
            ErrorMessage = Tr.T(
                $"Сумма не может превышать остаток долга ({row.AmountDisplay}).",
                $"Сумма карыздын калдыгынан ({row.AmountDisplay}) ашпашы керек.",
                $"The amount cannot exceed the remaining debt ({row.AmountDisplay}).",
                $"Tutar, kalan borcu ({row.AmountDisplay}) aşamaz.",
                $"Summa qarz qoldig'idan ({row.AmountDisplay}) oshib ketmasligi kerak.");
            return;
        }

        button.IsEnabled = false;
        try
        {
            // 2026-09-28: у клиента один долг — гасим одной суммой через сервер (BE-12, один
            // запрос вместо взносов по одному). При нескольких долгах сервер гасит от старых к
            // новым, а кассир нажал «Оплатить» у конкретной строки, — поэтому там старый путь,
            // а общая сумма — кнопкой «Погасить» над таблицей. Сервер без этого адреса — тоже
            // старый путь (TryPayViaServerAsync вернёт false).
            if (DebtSales.Count == 1 && _selectedClient is { } onlyDebtClient
                && await TryPayViaServerAsync(onlyDebtClient, amount, button).ConfigureAwait(true))
                return;

            // 2026-10-05: долг одной продажи — одним запросом pos/sales/{id}/pay-debt/ (сервер починил, ТЗ ч.12, п. 2.3);
            // сервер без этого адреса — старый путь по взносам.
            if (await TryPaySaleViaServerAsync(row, amount, button).ConfigureAwait(true))
                return;

            if (!await PayRowLegacyAsync(row, amount, button).ConfigureAwait(true))
                return;

            // Перегружаем с сервера, а не правим локально — сумма частичной оплаты
            // и статус (может стать "paid") должны отражать серверную истину.
            if (_selectedClient != null)
                await LoadDebtSalesAsync(_selectedClient.Id).ConfigureAwait(true);

            var paidText = amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
            SuccessMessage = Tr.T($"Оплачено {paidText} сом.", $"{paidText} сом төлөндү.", $"Paid {paidText} som.",
                $"{paidText} som ödendi.", $"{paidText} so'm to'landi.");
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

    /// <summary>Старый путь оплаты одной строки долга — по взносам сделки (до 2026-09-28 был
    /// телом PayDebt_Click; вынесен без изменений, чтобы им же пользовался запасной путь кнопки
    /// «Погасить» одной суммой). false — оплата не состоялась, причина уже в ErrorMessage.</summary>
    private async Task<bool> PayRowLegacyAsync(DebtSaleRow row, double amount, Button button)
    {
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
                return false;
            }

            if (_selectedClient is null)
                return false;

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
                        $"{unpaidInstallmentIds.Count} төлөмдүн {paidCount} төлөндү — бир басуу үчүн өтө көп. Калгандары үчүн «Төлөө» баскычын дагы бир жолу басыңыз.",
                        $"Paid off {paidCount} of {unpaidInstallmentIds.Count} installments — too many for one click. Click “Pay” again for the rest.",
                        $"{unpaidInstallmentIds.Count} taksitten {paidCount} tanesi ödendi — tek seferde ödenemeyecek kadar çok. Kalanlar için «Öde»ye tekrar basın.",
                        $"{unpaidInstallmentIds.Count} ta to'lovdan {paidCount} tasi to'landi — bir bosishga bu juda ko'p. Qolganlari uchun «To'lash»ni yana bosing.");
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
            return true;
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
            CreatedAt = created,
            FirstItemName = TryGetString(element, "first_item_name") ?? "",
            Amount = amount,
            AmountDisplay = amountText + Tr.T(" сом", " сом", " som", " som", " so'm"),
            AmountToPayText = amountText,
            OriginalAmountDisplay = amountText + Tr.T(" сом", " сом", " som", " som", " so'm"),
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
