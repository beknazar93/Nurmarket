using NurMarketKassa.Core.Application;
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
using NurMarketKassa.AvaloniaHost.Services;
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
    /// <summary>2026-10-18: номер чека («number» продажи) — для окна «Принять оплату» программы владельца.</summary>
    public string Number { get; init; } = "";
    /// <summary>2026-09-28: момент продажи — запасной путь «Погасить одной суммой» гасит от
    /// старых долгов к новым, как и сервер (DateDisplay как строка по дате не сортируется).</summary>
    public DateTimeOffset CreatedAt { get; init; }
    public string FirstItemName { get; init; } = "";
    /// <summary>Реальный остаток долга по сделке (remaining_debt) — уточняется при загрузке
    /// списка, а не берётся из total/debt_amount самой продажи, которые не отражают уже
    /// внесённые через сделку платежи.</summary>
    public string AmountDisplay { get; set; } = "";
    public double Amount { get; set; }

    // 2026-10-18, шестой круг ревью: InstallmentId / UnpaidInstallmentIds убраны — старый путь по взносам берёт график сделки
    // свежим запросом перед оплатой (FindUnpaidInstallments), список их больше не хранит.

    /// <summary>Исходная сумма долга (для истории — сколько было оплачено всего).</summary>
    public string OriginalAmountDisplay { get; set; } = "";

    /// <summary>Редактируемая сумма к оплате — по умолчанию весь долг, но кассир может
    /// уменьшить для частичного погашения.</summary>
    public string AmountToPayText { get; set; } = "";

    /// <summary>2026-10-18, девятый круг ревью (деньги): точный остаток по сделке (remaining_debt) НЕ получен — Amount лишь
    /// сумма продажи. Принцип «точный остаток или отказ»: оплату этой строки и «одной суммой» окна не дают.</summary>
    public bool RemainingUnknown { get; set; }

    /// <summary>2026-10-18, живая проверка: остаток самой продажи (debt_remaining, иначе debt_amount, иначе total). Точный, если
    /// у продажи НЕТ сделки (оплат через сделку тогда нет) — как в 1.17.58.</summary>
    public double SaleRemaining { get; set; }

    /// <summary>2026-10-21 (ревью денег, мелочь): SaleRemaining пришёл полем debt_remaining (а не взят из debt_amount/total).</summary>
    public bool SaleRemainingFromServer { get; set; }

    public bool RemainingKnown => !RemainingUnknown;

    /// <summary>2026-10-22 (аудит 1.17.59, П-Б): сумма в поле ввода — «121,00» (запятая), а не «121.00»; разбор CashAmountInput принимает оба.</summary>
    public static string InputText(double v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>2026-10-21 (ревью денег, A2): «Долг без продажи» (записан в «Клиентах») — сделка клиента без чека. Id — «deal:…»,
    /// оплатить «по чеку» нельзя (адрес оплаты — по продаже), только «одной суммой»: сервер гасит его вместе с чеками.</summary>
    public bool IsNoSale { get; init; }

    /// <summary>2026-10-23 (аудит, круг 8, В-3): исходная сумма долга числом (для чека — debt_amount, иначе total; для долга без
    /// продажи — debt_amount/amount) — по ней «Долг без продажи» сверяется с чеками клиента.</summary>
    public double OriginalAmount { get; set; }

    /// <summary>2026-10-23 (аудит, круг 8, В-3): «Долг без продажи» похож на двойную запись чека (та же сумма, та же минута): в
    /// долг клиента не входит (Amount = 0), оплатить нельзя — проверить на сайте NurCRM.</summary>
    /// <remarks>2026-10-24 (приёмка, ПР-1): как на сервере — Amount = остаток сделки (в итог долга входит, сервер его считает), строка только
    /// помечена: «Оплатить» нет, в «Погасить одной суммой» не включается (OneSumRule получает список без неё).</remarks>
    public bool IsSuspectedDuplicate { get; init; }

    /// <summary>2026-10-21 (ревью денег, A2): у строки есть поле суммы и «Оплатить» (точный остаток и это чек, а не долг без продажи).</summary>
    public bool CanPayByReceipt => RemainingKnown && !IsNoSale;

    public string RemainingUnknownLabel => Tr.T("остаток уточняется", "калдык такталууда", "balance being checked", "bakiye kontrol ediliyor", "qoldiq aniqlanmoqda");

    /// <summary>Как назвать чек в тексте: «№123» или дата.</summary>
    public string Label => Number.Length > 0 ? "№" + Number : DateDisplay;
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
        // 2026-10-19, К1: поля задаёт оболочка ModalShell — у сетки внутри своих полей нет.
        RootGrid.Margin = default;
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

        // 2026-10-18: способ оплаты — на телефоне подпись строкой выше кнопок «Наличные / Безнал».
        MethodGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "Auto,*");
        MethodGrid.RowDefinitions = narrow ? new RowDefinitions("Auto,6,Auto") : new RowDefinitions();
        if (MethodGrid.Children.Count > 1)
        {
            Grid.SetRow(MethodGrid.Children[1], narrow ? 2 : 0);
            Grid.SetColumn(MethodGrid.Children[1], narrow ? 0 : 1);
            if (MethodGrid.Children[1] is Grid methodButtons)
                methodButtons.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
        }
        MethodCashRadio.MinWidth = narrow ? 0 : 220;
        MethodTransferRadio.MinWidth = narrow ? 0 : 220;

        // Непогашенные: Дата, Товар, К оплате, «Оплатить». История: Дата, Товар, Оплачено.
        if (UnpaidGrid.Columns.Count >= 4)
        {
            // 2026-10-19, К1: дата 160 (на 140 «18:0(» обрезалось), кнопка 130.
            UnpaidGrid.Columns[0].Width = new DataGridLength(narrow ? 138 : 160);
            UnpaidGrid.Columns[1].IsVisible = !narrow;
            UnpaidGrid.Columns[2].Width = narrow ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(150);
            UnpaidGrid.Columns[3].Width = new DataGridLength(narrow ? 96 : 130);
        }
        if (HistoryGrid.Columns.Count >= 3)
        {
            HistoryGrid.Columns[0].Width = new DataGridLength(narrow ? 138 : 160);
            HistoryGrid.Columns[2].Width = new DataGridLength(narrow ? 112 : 170);
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
        // 2026-10-22 (модалки, пакет 2): верхний блок не выше ~45 % окна — остальное всегда у таблицы долгов (на 910×512
        // плашки «неизвестной оплаты»/предупреждения сжимали таблицу до нуля); сам блок при этом прокручивается.
        // Клиент ещё не выбран (таблицы нет) — блок поиска без предела.
        void LimitTop() => TopScroll.MaxHeight = HasSelectedClient && RootGrid.Bounds.Height > 0
            ? Math.Max(140, Math.Floor(RootGrid.Bounds.Height * 0.45))
            : double.PositiveInfinity;
        RootGrid.SizeChanged += (_, _) => LimitTop();
        ((INotifyPropertyChanged)this).PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(HasSelectedClient))
                LimitTop();
        };
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
        // 2026-10-18: способ оплаты по общим настройкам «Оплата долга» и перечитывание при оплате в другой программе.
        InitMethodChoice();
        Opened += async (_, _) => await EnsureClientsLoadedAsync().ConfigureAwait(true);
        // 2026-10-21 (пакет 6, п. 47): Enter больше не «ничего не делает» — в поиске выбирает первого клиента списка,
        // в сумме «Погасить одной суммой» — нажимает «Погасить» (с подтверждением, см. PayAll_Click).
        ClientSearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || ClientSearchResults.Count == 0)
                return;
            e.Handled = true;
            SelectClient(ClientSearchResults[0]);
        }, RoutingStrategies.Tunnel);
        PayAllAmountBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || _paying || !CanPayAll)
                return;
            e.Handled = true;
            PayAll_Click(PayAllButton, new RoutedEventArgs());
        }, RoutingStrategies.Tunnel);
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

    /// <remarks>2026-10-18, десятый круг (п.6): итог — только по точным остаткам; «уточняется» — отдельной припиской.</remarks>
    public string TotalOwedText =>
        $"{Tr.T("Итого к оплате", "Жалпы төлөнө турган сумма", "Total due", "Ödenecek toplam", "Jami to'lanadigan summa")}: " +
        // 2026-10-19, К1: сумма — «64 500,00 сом» (MoneyText), как у владельца; сама сумма та же.
        MoneyText.Som(DebtSales.Where(s => !s.RemainingUnknown).Sum(s => s.Amount))
        // 2026-10-23 (аудит, круг 8, В-3): есть долг без продажи — сколько его в итоге (в кассе и в списке клиентов суммы расходились).
        // 2026-10-24 (приёмка, ПР-1): «(в т. ч. без продажи: Y; на проверку: Z)» — Z (похоже на двойную запись) тоже входит в итог, как на сервере.
        + NoSaleTotalNote(
            DebtSales.Where(s => s.IsNoSale && !s.RemainingUnknown && !s.IsSuspectedDuplicate).Sum(s => s.Amount),
            DebtSales.Where(s => s.IsSuspectedDuplicate && !s.RemainingUnknown).Sum(s => s.Amount))
        + DebtPaymentFlow.CheckingSuffix(DebtSales.Count(s => s.RemainingUnknown));

    /// <summary>2026-10-24 (приёмка, ПР-1): приписка к итогу — «без продажи: Y» (Y &gt; 0) и «на проверку: Z» (Z &gt; 0); обе нули — пусто.</summary>
    internal static string NoSaleTotalNote(double noSale, double toCheck)
    {
        var parts = new List<string>();
        if (noSale > 0.005)
            parts.Add(Tr.T("без продажи", "сатуусуз", "without a sale", "satışsız", "sotuvsiz") + ": " + MoneyText.Som(noSale));
        if (toCheck > 0.005)
            parts.Add(Tr.T("на проверку", "текшерүүгө", "to check", "kontrol için", "tekshirish uchun") + ": " + MoneyText.Som(toCheck));
        return parts.Count == 0 ? "" : " (" + Tr.T("в т. ч.", "анын ичинде", "incl.", "dahil", "shundan") + " " + string.Join("; ", parts) + ")";
    }

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
        // 2026-10-18, восьмой круг (п.4): замок клиента смене клиента не мешает; во время своей операции — нельзя.
        if (_paying)
            return;
        _debtLoadGen++; // 2026-10-18: недогруженный список прежнего клиента не появится у нового
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
    /// <summary>2026-10-29: сервер недоступен — это окно закрывается, открывается «Оплата долга без связи» (клиенты из памяти кассы). Раньше здесь был тупик.</summary>
    private void SwitchToOfflineDebtPayment()
    {
        var owner = Owner as Window;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Close();
            _ = OfflineOperationsWindow.OpenAsync(owner, OfflineOperationsWindow.Mode.DebtPayment);
        });
    }

    private async Task EnsureClientsLoadedAsync()
    {
        if (_clientsLoaded)
            return;

        if (OfflineModeHelper.IsServerOutage)
        {
            SwitchToOfflineDebtPayment();
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
            // 2026-10-21 (пакет 6, п. 13 аудита кассы): суммы долга в списке — фоном, окно ими не блокируется.
            _ = LoadDebtorsAsync(_closedCts.Token);
        }
        catch (ServerNotAnsweringException ex)
        {
            if (ex.Throttled)
                ErrorMessage = ServerDownMessage(ex);
            else
                SwitchToOfflineDebtPayment();
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

    /// <summary>2026-10-18: номер загрузки долгов — ответ прежней загрузки (две подряд: оплата и оповещение из другой
    /// кассы) не добавляет строки второй раз.</summary>
    private int _debtLoadGen;

    /// <param name="keepInput">2026-10-18, ревью: перечитывание «со стороны» (оплата в другой кассе, у владельца, проверка
    /// остатка) — сообщения не стираются, прежний список виден до прихода нового, введённые суммы сохраняются (больше нового
    /// остатка — уменьшаются до него, с пояснением). true — список получен.</param>
    private async Task<bool> LoadDebtSalesAsync(string clientId, bool keepInput = false)
    {
        var gen = ++_debtLoadGen;
        // 2026-10-18, шестой/восьмой круг: версия долга клиента (общая, PosDataEvents) на момент НАЧАЛА загрузки.
        var startSeq = PosDataEvents.DebtVersionOf(clientId);
        // 2026-10-18, второй круг ревью: «введённым» считается только то, что кассир правил руками; сумма по умолчанию
        // (весь долг / остаток чека) просто пересчитывается в новый остаток, без красной ошибки.
        var enteredAll = PayAllAmountText != _payAllDefault ? PayAllAmountText : null;
        var enteredRows = DebtSales
            .Where(r => r.AmountToPayText != DebtSaleRow.InputText(r.Amount))
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().AmountToPayText);
        if (!keepInput)
        {
            ErrorMessage = "";
            SuccessMessage = "";
            DebtSales.Clear();
            DebtHistory.Clear();
            OnPropertyChanged(nameof(TotalOwedText));
        }
        // 2026-10-04: сервер уже признан лежащим — не ждём его ещё раз.
        if (OfflineModeHelper.IsServerOutage)
        {
            ErrorMessage = ServerDownMessage(null);
            return false;
        }

        try
        {
            // 2026-10-04: долги клиента и их остатки по сделкам — одна «первая порция»: не дольше 5 с
            // без ответа сервера (окно отсчитывается заново после каждого ответа), сбой — авария и
            // текст. Раньше — до 55 с на каждый запрос. Не дождались остатков — список не показываем:
            // суммы продаж без уточнения по сделке завышены (уже внесённые платежи в них не видны).
            var rows = await ServerAnswerWait.FirstPortionAsync(DebtContext,
                ct => LoadDebtRowsAsync(_salesApi, clientId, ct), _closedCts.Token).ConfigureAwait(true);

            // 2026-10-18: пока ждали сервер, началась другая загрузка (или выбран другой клиент) — её строки главнее.
            if (gen != _debtLoadGen)
                return false;
            _loadedSeq = startSeq; // это последняя загрузка (номер проверен выше) — её версия
            var lowered = false;
            DebtSales.Clear();
            DebtHistory.Clear();
            foreach (var row in rows)
            {
                // 2026-10-18, ревью: при перечитывании «со стороны» введённая у строки сумма остаётся (не больше остатка).
                if (keepInput && row.Amount > 0.005 && enteredRows.TryGetValue(row.Id, out var typed)
                    && CashAmountInput.TryParse(typed, out var typedDec) && (double)typedDec is var typedAmount && typedAmount > 0)
                {
                    if (typedAmount <= row.Amount + 0.005)
                        row.AmountToPayText = typed;
                    else
                        lowered = true;
                }
                // 2026-10-18, ревью после живой проверки (п.3, деньги): неуточнённая строка — среди долгов (не в «Истории»), даже с
                // нулевой суммой продажи: она участвует в OneSumRule (старый неуточнённый чек закрывает «одной суммой»).
                // 2026-10-23 (аудит, круг 8, В-3): «похоже на двойную запись» (Amount 0) — на виду среди долгов, а не в «Истории».
                if (row.Amount > 0.005 || row.RemainingUnknown || row.IsSuspectedDuplicate)
                    DebtSales.Add(row);
                else
                    DebtHistory.Add(row);
            }
            OnPropertyChanged(nameof(TotalOwedText));
            // 2026-09-28: сумма «Погасить одной суммой» — весь долг по умолчанию.
            OnDebtSalesReloaded();
            // 2026-10-18, ревью: введённая «одной суммой» сумма не затирается полным долгом; больше нового остатка — до него.
            if (keepInput && CashAmountInput.TryParse(enteredAll, out var typedAllDec) && (double)typedAllDec is var typedAll && typedAll > 0)
            {
                // 2026-10-18, ревью после живой проверки (п.2): «одной суммой» — не больше суммы ТОЧНЫХ остатков (OneSum.Limit).
                var total = OneSum.Limit;
                if (typedAll <= total + 0.005)
                    PayAllAmountText = enteredAll;
                else if (total > 0.005)
                    lowered = true;
            }
            if (lowered)
            {
                var owedText = DebtSaleRow.InputText(OneSum.Limit);
                ErrorMessage = Tr.T(
                    $"Остаток долга изменился — введённая сумма больше него и заменена остатком ({owedText} сом). Проверьте сумму.",
                    $"Карыздын калдыгы өзгөрдү — киргизилген сумма андан чоң, калдык менен алмаштырылды ({owedText} сом). Сумманы текшериңиз.",
                    $"The remaining debt changed — the entered amount was larger and was replaced with the balance ({owedText} som). Check the amount.",
                    $"Kalan borç değişti — girilen tutar daha büyüktü ve bakiye ile değiştirildi ({owedText} som). Tutarı kontrol edin.",
                    $"Qarz qoldig'i o'zgardi — kiritilgan summa undan katta edi va qoldiq bilan almashtirildi ({owedText} so'm). Summani tekshiring.");
            }
            return true;
        }
        // 2026-10-18, ревью: ошибка устаревшей загрузки (началась новая) сообщение новой не перезаписывает.
        catch (ServerNotAnsweringException ex)
        {
            if (gen == _debtLoadGen)
                ErrorMessage = ServerDownMessage(ex);
        }
        catch (OperationCanceledException) when (_closedCts.IsCancellationRequested)
        {
            // окно закрыли во время загрузки
        }
        catch (ApiException ex)
        {
            if (gen == _debtLoadGen)
                ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            if (gen == _debtLoadGen)
                ErrorMessage = Tr.T("Не удалось загрузить долги клиента: ", "Клиенттин карызын жүктөө мүмкүн болгон жок: ", "Could not load the client's debts: ", "Müşteri borçları yüklenemedi: ", "Mijozning qarzlarini yuklab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"PayDebt sales load failed: {ex}", "WARNING");
        }
        return false;
    }

    /// <summary>2026-10-18: долги клиента с точным остатком по сделкам (remaining_debt) — общий загрузчик окна кассы и
    /// окна «Принять оплату» программы владельца (раньше был телом LoadDebtSalesAsync). Строки — от новых к старым;
    /// полностью погашенные — с Amount 0 (окно кассы кладёт их в «Историю»).</summary>
    internal static async Task<List<DebtSaleRow>> LoadDebtRowsAsync(ISalesApiService salesApi, string clientId, CancellationToken ct)
    {
        var rows = await LoadDebtRowsAsync(salesApi, clientId, await salesApi.PosDebtSalesAsync(clientId, ct).ConfigureAwait(true), ct).ConfigureAwait(true);
        // 2026-10-21 (ревью денег, A2): и «Долг без продажи» — окна оплаты видели только pos/sales/?status=debt и писали «Долгов
        // нет», а «одной суммой» сервер мог погасить сначала этот долг (план в подтверждении был неверным).
        var noSale = await LoadNoSaleDebtRowsAsync(clientId, rows, ct).ConfigureAwait(true);
        if (noSale.Count == 0)
            return rows;
        rows.AddRange(noSale);
        return rows.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>2026-10-21 (ревью денег, A2): сделки клиента без продажи с остатком долга (GET clients/{id}/deals/ — у сделки
    /// продажи есть поле sale; сделки уже найденных чеков исключаются по deal_id). Не получилось — пусто, как раньше (WARNING).</summary>
    /// <param name="onIncomplete">2026-10-31 (приёмка 1.17.68, В-1б): вызывается, если долги без продажи не получены (сбой запроса или чек без известной сделки) — ответ неполный, кэшировать его нельзя.</param>
    internal static async Task<List<DebtSaleRow>> LoadNoSaleDebtRowsAsync(string clientId, IReadOnlyList<DebtSaleRow> saleRows, CancellationToken ct, Action? onIncomplete = null)
    {
        var result = new List<DebtSaleRow>();
        // Сделка неуточнённого чека неизвестна — её нельзя отличить от долга без продажи (иначе долг посчитался бы дважды).
        if (saleRows.Any(r => r.RemainingUnknown && string.IsNullOrWhiteSpace(r.DealId)))
        {
            PosLogger.Log("Оплата долга: долги без продажи не проверены — есть чек без известной сделки.", "WARNING");
            onIncomplete?.Invoke();
            return result;
        }
        var knownDeals = new HashSet<string>(saleRows.Select(r => r.DealId).Where(d => !string.IsNullOrWhiteSpace(d)), StringComparer.OrdinalIgnoreCase);
        static bool Linked(JsonElement deal, string key) => deal.TryGetProperty(key, out var v)
            && (v.ValueKind is JsonValueKind.Number or JsonValueKind.Object
                || (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())));
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            var data = await api.RequestAsync(System.Net.Http.HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(clientId.Trim())}/deals/", null,
                new Dictionary<string, string> { ["page_size"] = "500" }, ct).ConfigureAwait(true);
            foreach (var deal in NurMarketApiClient.UnwrapList(data))
            {
                if (deal.ValueKind != JsonValueKind.Object)
                    continue;
                var id = TryGetString(deal, "id") ?? (TryGetDouble(deal, "id") is { } n ? n.ToString("0", CultureInfo.InvariantCulture) : null);
                if (string.IsNullOrWhiteSpace(id) || knownDeals.Contains(id) || Linked(deal, "sale") || Linked(deal, "sale_id"))
                    continue;
                if (string.Equals(TryGetString(deal, "kind"), "prepayment", StringComparison.OrdinalIgnoreCase))
                    continue;
                // 2026-10-24 (приёмка, ПР-1): сделки типа «sale» сервер в долг без продажи (clients/debtors) не считает — это запись
                // самой продажи («Предоплата тест» на 24 933 — двойник чека «Проверка фото»).
                if (string.Equals(TryGetString(deal, "kind"), "sale", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryGetDouble(deal, "remaining_debt") is not { } remaining || remaining <= 0.005)
                    continue;
                DateTimeOffset.TryParse(TryGetString(deal, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);
                var original = TryGetDouble(deal, "debt_amount") is { } da && da > 0 ? da : TryGetDouble(deal, "amount") ?? remaining;
                var note = (TryGetString(deal, "note") ?? TryGetString(deal, "comment") ?? "").Trim();
                // 2026-10-23 (аудит, круг 8, В-3): чек клиента на ту же исходную сумму в те же ±5 минут — «Долг без продажи» похож на
                // двойную запись этой продажи: оплатить нельзя.
                // 2026-10-24 (приёмка, ПР-1): правило только ПОМЕЧАЕТ строку — из долга не вычитается (сервер её считает, итог = clients/debtors).
                var twin = created == default ? null : saleRows.FirstOrDefault(r => !r.IsNoSale && r.CreatedAt != default
                    && Math.Abs((r.OriginalAmount > 0.005 ? r.OriginalAmount : r.Amount) - original) <= 0.01
                    && Math.Abs((r.CreatedAt - created).TotalMinutes) <= 5);
                if (twin is not null)
                {
                    PosLogger.Log($"Оплата долга: долг без продажи (сделка {id}) похож на двойную запись чека {twin.Id} — помечен «на проверку», в итог входит, не оплачивается.", "INFO");
                    result.Add(new DebtSaleRow
                    {
                        Id = "deal:" + id,
                        DealId = id,
                        IsNoSale = true,
                        IsSuspectedDuplicate = true,
                        CreatedAt = created,
                        DateDisplay = created.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                        FirstItemName = Tr.T(
                            $"Долг без продажи — похоже на двойную запись продажи от {twin.DateDisplay} ({MoneyText.Som(original)}). Проверьте на сайте NurCRM.",
                            $"Сатуусуз карыз — {twin.DateDisplay} күнүндөгү сатуунун эки жолку жазылышына окшош ({MoneyText.Som(original)}). NurCRM сайтынан текшериңиз.",
                            $"Debt without a sale — looks like a double entry of the sale from {twin.DateDisplay} ({MoneyText.Som(original)}). Check it on the NurCRM site.",
                            $"Satışsız borç — {twin.DateDisplay} tarihli satışın çift kaydına benziyor ({MoneyText.Som(original)}). NurCRM sitesinde kontrol edin.",
                            $"Sotuvsiz qarz — {twin.DateDisplay} dagi sotuvning ikki marta yozilganiga o'xshaydi ({MoneyText.Som(original)}). NurCRM saytida tekshiring."),
                        Amount = remaining,
                        AmountDisplay = MoneyText.Som(remaining),
                        AmountToPayText = "",
                        SaleRemaining = remaining,
                        OriginalAmount = original,
                        OriginalAmountDisplay = MoneyText.Som(original),
                    });
                    continue;
                }
                result.Add(new DebtSaleRow
                {
                    Id = "deal:" + id,
                    DealId = id,
                    IsNoSale = true,
                    CreatedAt = created,
                    DateDisplay = created == default ? "" : created.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                    FirstItemName = Tr.T("Долг без продажи", "Сатуусуз карыз", "Debt without a sale", "Satışsız borç", "Sotuvsiz qarz") + (note.Length > 0 ? " — " + note : ""),
                    Amount = remaining,
                    AmountDisplay = MoneyText.Som(remaining),
                    AmountToPayText = DebtSaleRow.InputText(remaining),
                    SaleRemaining = remaining,
                    OriginalAmount = original,
                    OriginalAmountDisplay = MoneyText.Som(original),
                });
            }
            if (result.Count > 0)
                PosLogger.Log($"Оплата долга: у клиента {clientId} долгов без продажи: {result.Count(r => !r.IsSuspectedDuplicate)} на {result.Where(r => !r.IsSuspectedDuplicate).Sum(r => r.Amount):0.00} сом, похожих на двойную запись: {result.Count(r => r.IsSuspectedDuplicate)} на {result.Where(r => r.IsSuspectedDuplicate).Sum(r => r.Amount):0.00} сом.", "INFO");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: долги без продажи не получены ({ErrorCode(ex)}) — показаны только чеки.", "WARNING");
            onIncomplete?.Invoke();
        }
        return result;
    }

    /// <summary>2026-10-18, девятый круг (п.3): то же по уже полученному списку продаж в долг (раздел «Долги клиентов» строит
    /// карточку клиента по точному остатку сделок, не запрашивая список второй раз).</summary>
    /// <param name="throttle">2026-10-18, десятый круг: ограничение одновременных запросов уточнения (раздел «Долги клиентов»).</param>
    internal static async Task<List<DebtSaleRow>> LoadDebtRowsAsync(ISalesApiService salesApi, string clientId, IReadOnlyList<JsonElement> raw, CancellationToken ct,
        SemaphoreSlim? throttle = null)
    {
        // 2026-10-18, восьмой круг (п.6): по дате, а не по строке «dd.MM.yyyy HH:mm» (она сортировала по дню месяца).
        var list = raw.Select(ToDebtSaleRow).OrderByDescending(r => r.CreatedAt).ToList();

        // Сервер не обновляет status самой продажи после оплаты через сделку (deal) —
        // продажа может годами висеть как "debt", хотя реально уже полностью погашена.
        // Уточняем остаток напрямую по сделке: непогашенные остаются в основном списке,
        // а полностью оплаченные переезжают в историю вместо того чтобы просто исчезать.
        await Task.WhenAll(list.Select(async r =>
        {
            if (throttle is null)
            {
                await ResolveRealRemainingDebtAsync(salesApi, clientId, r, ct).ConfigureAwait(true);
                return;
            }
            await throttle.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await ResolveRealRemainingDebtAsync(salesApi, clientId, r, ct).ConfigureAwait(true);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        return list;
    }

    /// <summary>Догружает deal_id (если не пришёл со списком) и подставляет реальный
    /// remaining_debt по сделке вместо исходной суммы продажи. При любой ошибке (сеть,
    /// сделка не найдена и т.п.) оставляет строку как есть — она просто ведёт себя
    /// по-старому и покажет ошибку сервера при попытке оплаты, а не исчезнет молча.</summary>
    /// <remarks>2026-10-18, девятый круг (п.2): точный остаток не получен — RemainingUnknown = true. 2026-10-18, живая проверка
    /// NBS: в WARNING — причина (без данных клиента); у продажи БЕЗ сделки остаток продажи точный (оплат через сделку нет).</remarks>
    private static async Task ResolveRealRemainingDebtAsync(ISalesApiService salesApi, string clientId, DebtSaleRow row, CancellationToken ct = default)
    {
        row.RemainingUnknown = true;
        string? reason = null;
        try
        {
            var dealId = string.IsNullOrWhiteSpace(row.DealId) ? null : row.DealId;
            if (dealId is null)
            {
                JsonElement sale;
                try
                {
                    sale = await salesApi.PosSaleGetAsync(row.Id, ct).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    reason = "ошибка запроса продажи (" + ErrorCode(ex) + ")";
                    return;
                }
                if (sale.ValueKind != JsonValueKind.Object)
                {
                    reason = "ответ продажи не объект";
                    return;
                }
                dealId = DealIdOf(sale);
                if (dealId is null)
                {
                    // Сделки нет (deal_id пуст / null / нет поля) — гасить долг через сделку нельзя, остаток продажи и есть точный.
                    // 2026-10-21 (ревью денег, мелочь): остаток — из свежей карточки продажи (debt_remaining), если в списке его не
                    // было: debt_amount/total не знают частичных оплат и завышали «Весь долг».
                    if (!row.SaleRemainingFromServer)
                    {
                        if (TryGetDouble(sale, "debt_remaining") is { } fresh)
                            row.SaleRemaining = Math.Max(0, fresh);
                        else
                            PosLogger.Log($"Оплата долга: у чека {row.Id} нет сделки и нет debt_remaining — остаток по сумме продажи ({row.SaleRemaining:0.00}); проверить на сервере.", "WARNING");
                    }
                    SetAmount(row, row.SaleRemaining);
                    row.RemainingUnknown = false;
                    return;
                }
                row.DealId = dealId;
            }

            JsonElement deal;
            try
            {
                deal = await salesApi.ClientDealGetAsync(clientId, dealId, ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                reason = "ошибка запроса сделки (" + ErrorCode(ex) + ")";
                return;
            }
            if (deal.ValueKind != JsonValueKind.Object)
            {
                reason = "ответ сделки не объект";
                return;
            }
            if (!deal.TryGetProperty("remaining_debt", out var remainingEl))
            {
                reason = "в сделке нет remaining_debt";
                return;
            }

            var remainingText = remainingEl.ValueKind switch
            {
                JsonValueKind.String => remainingEl.GetString(),
                JsonValueKind.Number => remainingEl.GetRawText(),
                _ => null,
            };
            if (remainingText is null
                || !double.TryParse(remainingText, NumberStyles.Any, CultureInfo.InvariantCulture, out var remaining))
            {
                reason = "remaining_debt не число (" + remainingEl.ValueKind + ")";
                return;
            }

            SetAmount(row, remaining);
            row.RemainingUnknown = false;
        }
        catch (Exception ex)
        {
            reason = "ошибка " + ex.GetType().Name;
        }
        finally
        {
            if (row.RemainingUnknown)
                PosLogger.Log($"Оплата долга: точный остаток по чеку {row.Id} не получен ({reason ?? "?"}) — оплата этого чека закрыта; «Обновить» запросит снова.", "WARNING");
        }
    }

    private static void SetAmount(DebtSaleRow row, double remaining)
    {
        row.Amount = remaining;
        row.AmountDisplay = MoneyText.Som(remaining);
        row.AmountToPayText = DebtSaleRow.InputText(remaining);
    }

    /// <summary>Код ошибки для журнала — без текста ответа (в нём могут быть данные клиента).</summary>
    private static string ErrorCode(Exception ex) => ex switch
    {
        ApiException api => "HTTP " + (api.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "?"),
        OperationCanceledException => "таймаут/отмена",
        System.Net.Http.HttpRequestException http => "сеть" + (http.StatusCode is { } code ? " " + (int)code : ""),
        _ => ex.GetType().Name,
    };

    /// <summary>deal_id из карточки продажи (строка или число); пусто / null / нет поля — null (сделки нет).</summary>
    private static string? DealIdOf(JsonElement sale)
    {
        if (!sale.TryGetProperty("deal_id", out var dealIdEl))
            return null;
        var dealId = dealIdEl.ValueKind switch
        {
            JsonValueKind.String => dealIdEl.GetString(),
            JsonValueKind.Number => dealIdEl.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(dealId) ? null : dealId;
    }

    /// <summary>2026-10-18, девятый круг (п.10): deal_id продажи — из строки списка или из карточки продажи (значение строкой
    /// или числом). Одна реализация для уточнения остатка и для старого пути по взносам. null — сделки нет.</summary>
    internal static async Task<string?> ResolveDealIdAsync(ISalesApiService salesApi, DebtSaleRow row, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(row.DealId))
            return row.DealId;
        var saleDetail = await salesApi.PosSaleGetAsync(row.Id, ct).ConfigureAwait(true);
        if (saleDetail.ValueKind != JsonValueKind.Object || DealIdOf(saleDetail) is not { } dealId)
            return null;
        row.DealId = dealId;
        return dealId;
    }

    private async void PayDebt_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DebtSaleRow row } button || _paying)
            return;

        ErrorMessage = "";
        SuccessMessage = "";
        // 2026-10-18, девятый круг (деньги): «точный остаток или отказ» — по этой строке остаток не получен.
        if (row.RemainingUnknown)
        {
            ErrorMessage = DebtPaymentFlow.RemainingUnknownText(new[] { row }, owner: false);
            return;
        }
        // 2026-10-21 (ревью денег, A2): долг без продажи — только «одной суммой» (оплата «по чеку» идёт по продаже).
        if (row.IsNoSale)
        {
            ErrorMessage = DebtPaymentFlow.NoSaleByReceiptText;
            return;
        }

        // 2026-10-18, третий круг ревью: разбор суммы — как у владельца (CashAmountInput: запятая/точка, пробелы).
        if (!CashAmountInput.TryParse(row.AmountToPayText, out var amountDec) || amountDec <= 0m)
        {
            ErrorMessage = Tr.T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Enter the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
            return;
        }
        var amount = (double)Math.Round(amountDec, 2);

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

        // 2026-10-22 (аудит 1.17.59, П-Б): «Оплатить» у строки — только после подтверждения суммы, способа и остатка
        // (раньше проводило деньги одним нажатием, в отличие от «Погасить»). Общий флаг с PayAll_Click — один вопрос за раз.
        if (_confirmingPayAll || _selectedClient is not { } rowClient)
            return;
        var rowLeftText = DebtPaymentFlow.Money(Math.Max(0, row.Amount - amount));
        var rowAmountText = DebtPaymentFlow.Money(amount);
        var rowMethod = DebtPaymentFlow.MethodTitle(SelectedMethod);
        var rowConfirm = Tr.T($"Принять от «{rowClient.Name}» {rowAmountText} по чеку {row.Label} ({rowMethod})?\nОстанется по чеку: {rowLeftText}",
            $"«{rowClient.Name}» кардардан {row.Label} чек боюнча {rowAmountText} кабыл алынсынбы ({rowMethod})?\nЧек боюнча калды: {rowLeftText}",
            $"Accept {rowAmountText} from “{rowClient.Name}” for receipt {row.Label} ({rowMethod})?\nLeft on the receipt: {rowLeftText}",
            $"«{rowClient.Name}» müşterisinden {row.Label} fişi için {rowAmountText} alınsın mı ({rowMethod})?\nFişte kalan: {rowLeftText}",
            $"«{rowClient.Name}» mijozdan {row.Label} chek uchun {rowAmountText} qabul qilinsinmi ({rowMethod})?\nChekda qolgan: {rowLeftText}");
        bool rowConfirmed;
        _confirmingPayAll = true;
        try
        {
            rowConfirmed = await PosDialogs.ConfirmYesNoModalAsync(this, rowConfirm,
                Tr.T("Оплата долга", "Карызды төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash"),
                confirmText: Tr.T($"Принять {rowAmountText}", $"{rowAmountText} кабыл алуу", $"Accept {rowAmountText}",
                    $"{rowAmountText} al", $"{rowAmountText} qabul qilish")).ConfigureAwait(true);
        }
        finally
        {
            _confirmingPayAll = false;
        }
        if (!rowConfirmed || _paying || !ReferenceEquals(_selectedClient, rowClient))
            return;

        // 2026-10-18, пятый круг (п.10): защита, замок, код, способ, ошибки — общая обёртка RunPaymentAsync.
        await RunPaymentAsync(async method =>
        {
            // 2026-10-18, десятый круг (п.3): «Оплатить» у строки — ВСЕГДА оплата этого чека (pos/sales/{id}/pay-debt/, запись с
            // SaleId), даже если строка одна. Раньше (2026-09-28) при одном долге шли «одной суммой» — тогда ещё не было адреса
            // по чеку (BE-12). «Одной суммой» осталось только запасным: сервер без адреса по чеку и строка одна (как 1.17.58);
            // дальше — старый путь по взносам.
            if (await TryPaySaleViaServerAsync(row, amount, method).ConfigureAwait(true))
                return;
            if (DebtSales.Count(r => !r.IsSuspectedDuplicate) == 1 && _serverSalePayMissing && await TryPayViaServerAsync(amount, method).ConfigureAwait(true))
                return;

            if (RefuseLegacyForNonCash(method))
                return;
            // Старый путь: события смены и LAN — по каждому оплаченному взносу сразу (п.6); список перечитывается всегда.
            double? legacyPaid = null;
            var reloaded = false;
            try
            {
                legacyPaid = await PayRowLegacyAsync(row, amount, button).ConfigureAwait(true);
            }
            finally
            {
                var legacyNote = ErrorMessage;
                if (_selectedClient != null)
                    reloaded = await LoadDebtSalesAsync(_selectedClient.Id).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(legacyNote))
                    ErrorMessage = legacyNote;
            }
            if (legacyPaid is { } paid && paid > 0.005)
            {
                // 2026-10-18, десятый круг (п.10): общий текст успеха; остаток — по перечитанному списку (неточный — не пишем).
                var fresh = DebtSales.FirstOrDefault(r => r.Id == row.Id);
                double? left = !reloaded ? null : fresh is null ? 0 : fresh.RemainingUnknown ? null : fresh.Amount;
                SuccessMessage = DebtPaymentRunner.SuccessText(paid, left, bySale: true, owner: false);
            }
        }, $"sale {row.Id}").ConfigureAwait(true);
    }

    /// <summary>Старый путь оплаты одной строки долга — по взносам сделки (сервер без pay-debt). null — оплата не начиналась,
    /// причина уже в ErrorMessage; иначе — оплаченная сумма для показа. 2026-10-18, шестой круг ревью: все взносы прошли — в
    /// смену вся введённая сумма одним событием (как 1.17.58); прервано (ошибка/лимит 20 с / 30 взносов) — по каждому оплаченному
    /// взносу его сумма, только если она есть в графике; неизвестна — в смену не пишется, кассиру — проверить сайт и Z-отчёт.</summary>
    private async Task<double?> PayRowLegacyAsync(DebtSaleRow row, double amount, Button button)
    {
        // Список долгов (GET .../sales/?status=debt) не отдаёт deal_id в каждой строке — только одиночная карточка продажи.
        var dealId = await ResolveDealIdAsync(_salesApi, row).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(dealId))
        {
            ErrorMessage = Tr.T(
                "У этой продажи нет привязанной сделки — оплата долга недоступна.",
                "Бул сатууга байланган келишим жок — карызды төлөө мүмкүн эмес.",
                "This sale has no linked deal — debt payment is unavailable.",
                "Bu satışa bağlı bir anlaşma yok — borç ödemesi yapılamaz.",
                "Bu sotuvga bog'langan bitim yo'q — qarzni to'lash mumkin emas.");
            return null;
        }

        if (_selectedClient is not { } client)
            return null;

        // Взносы с суммами — из графика сделки (одним запросом перед оплатой).
        var deal = await _salesApi.ClientDealGetAsync(client.Id, dealId).ConfigureAwait(true);
        var installments = FindUnpaidInstallments(deal);

        // Записать один оплаченный взнос: в смену (наличные — старый путь гасит в кассу) и соседям по LAN.
        void RecordPaid(string part, double sum)
        {
            var operationKey = ShiftEventsStore.OperationKey(dealId + ":" + part);
            ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, PosApp.ActiveShiftId, operationKey, sum, client.DisplayName);
            AnnouncePaid("legacy:" + operationKey, client, sum, -1, DebtPaymentSettings.MethodCash); // 2026-10-18: остаток неизвестен (< 0)
        }

        var isFullPayment = amount >= row.Amount - 0.005;
        if (!isFullPayment || installments.Count == 0)
        {
            // Частичная оплата — первый взнос с явной суммой; нет графика — одним вызовом без суммы (весь остаток).
            var firstInstallmentId = installments.Count > 0 ? installments[0].Id : null;
            await _salesApi.PosPayDebtAsync(client.Id, dealId, firstInstallmentId, isFullPayment ? null : amount).ConfigureAwait(true);
            RecordPaid(firstInstallmentId ?? "all", amount);
            return amount;
        }

        // Полное закрытие — все непогашенные взносы по очереди (без суммы, как в подтверждённом рабочем вызове), не больше
        // 30 за нажатие и 20 с.
        const int maxInstallmentsPerClick = 30;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var paidCount = 0;
        var paidInstallments = new List<(string Id, double? Sum)>();
        Exception? failure = null;
        try
        {
            foreach (var (id, sum) in installments.Take(maxInstallmentsPerClick))
            {
                if (DateTime.UtcNow > deadline)
                    break;
                button.Content = Tr.T(
                    $"Оплата {paidCount + 1}/{installments.Count}…",
                    $"Төлөм {paidCount + 1}/{installments.Count}…",
                    $"Paying {paidCount + 1}/{installments.Count}…",
                    $"Ödeniyor {paidCount + 1}/{installments.Count}…",
                    $"To'lanmoqda {paidCount + 1}/{installments.Count}…");
                await _salesApi.PosPayDebtAsync(client.Id, dealId, id, null).ConfigureAwait(true);
                paidCount++;
                paidInstallments.Add((id, sum));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            PosLogger.Log($"Pay-debt (старый путь): остановка на взносе {paidCount + 1} из {installments.Count} сделки {dealId}: {ex.Message}", "PAYMENT");
        }
        finally
        {
            // 2026-10-18, седьмой круг (п.9): у каждой кнопки своя привязка надписи — «Погасить» (PayAllButtonLabel, DataContext
            // окна) и «Оплатить» у строки таблицы (PayButtonLabel через окно-предка).
            if (ReferenceEquals(button, PayAllButton))
                button[!ContentControl.ContentProperty] = new Avalonia.Data.Binding(nameof(PayAllButtonLabel));
            else
                button[!ContentControl.ContentProperty] = new Avalonia.Data.Binding("DataContext.PayButtonLabel")
                {
                    RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Window) },
                };
        }

        // 2026-10-18, шестой круг ревью (деньги): суммы взносов в графике (amount/sum) живым захватом НЕ подтверждены —
        // поведение не хуже 1.17.58. Все взносы оплачены без ошибки — в смену вся введённая сумма одним событием, как было.
        if (failure is null && paidCount == installments.Count)
        {
            RecordPaid("all", amount);
            return amount;
        }
        // Первый же взнос не прошёл — ничего не оплачено: ошибка как обычно.
        if (paidCount == 0 && failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        // Прервано (ошибка или лимит): в смену — только взносы с известной суммой.
        var paid = 0.0;
        var unknownSums = 0;
        foreach (var (id, sum) in paidInstallments)
        {
            if (sum is { } s && s > 0.005)
            {
                paid += s;
                RecordPaid(id, s);
            }
            else
                unknownSums++;
        }
        var wantText = DebtPaymentFlow.Money(amount);
        if (unknownSums > 0)
        {
            PosLogger.Log($"Pay-debt (старый путь): оплачено {paidCount} из {installments.Count} взносов сделки {dealId}, у {unknownSums} сумма в графике неизвестна — " +
                          $"в смену записано {paid:0.00} из {amount:0.00}; проверить долг на сайте и Z-отчёт.", "WARNING");
            ErrorMessage = Tr.T(
                    $"Оплачено {paidCount} из {installments.Count} взносов, сумма части неизвестна — проверьте долг клиента на сайте NurCRM и Z-отчёт.",
                    $"{installments.Count} төлөмдүн {paidCount} төлөндү, бир бөлүгүнүн суммасы белгисиз — кардардын карызын NurCRM сайтынан жана Z-отчётту текшериңиз.",
                    $"Paid {paidCount} of {installments.Count} installments, part of the amount is unknown — check the client's debt on the NurCRM site and the Z-report.",
                    $"{installments.Count} taksitten {paidCount} tanesi ödendi, bir kısmının tutarı bilinmiyor — müşterinin borcunu NurCRM sitesinde ve Z raporunda kontrol edin.",
                    $"{installments.Count} ta to'lovdan {paidCount} tasi to'landi, bir qismining summasi noma'lum — mijoz qarzini NurCRM saytida va Z-hisobotda tekshiring.")
                + (failure is not null ? " " + failure.Message : "");
        }
        else
        {
            var paidText = DebtPaymentFlow.Money(paid);
            ErrorMessage = Tr.T(
                    $"Оплачено {paidText} из {wantText} ({paidCount} из {installments.Count} взносов) — нажмите «Оплатить» ещё раз для остатка.",
                    $"{wantText} ичинен {paidText} төлөндү ({installments.Count} төлөмдүн {paidCount}) — калдыгы үчүн «Төлөө» баскычын дагы басыңыз.",
                    $"Paid {paidText} of {wantText} ({paidCount} of {installments.Count} installments) — click “Pay” again for the rest.",
                    $"{wantText} tutarın {paidText} kadarı ödendi ({installments.Count} taksitten {paidCount}) — kalan için «Öde»ye tekrar basın.",
                    $"{wantText} dan {paidText} to'landi ({installments.Count} ta to'lovdan {paidCount}) — qolgani uchun «To'lash»ni yana bosing.")
                + (failure is not null ? " " + failure.Message : "");
        }
        return paid;
    }

    private void ApplyClientFilter()
    {
        ClientSearchResults.Clear();
        var query = _clientSearchText?.Trim() ?? "";
        IEnumerable<ClientOption> source = _allClients;
        if (query.Length > 0)
            source = source.Where(c => SearchMatch.Matches(query, c.DisplayName)); // 2026-10-29: единый поиск
        // 2026-10-21 (пакет 6, п. 13 аудита кассы): должники — первыми (больший долг выше), остальные — по алфавиту.
        if (_debtByClient.Count > 0)
            source = source.OrderByDescending(c => _debtByClient.TryGetValue(c.Id, out var d) ? d : 0);

        foreach (var client in source.Take(30))
            ClientSearchResults.Add(new DebtClientOption
            {
                Id = client.Id,
                DisplayName = client.DisplayName,
                // 2026-10-23 (аудит, круг 8, В-3): сводка сервера (clients/debtors/) — это долг по чекам, без «долга без продажи»; так и подписано.
                DebtText = _debtByClient.TryGetValue(client.Id, out var debt)
                    ? Tr.T("по чекам: ", "чектер боюнча: ", "by receipts: ", "fişlere göre: ", "cheklar bo'yicha: ") + MoneyText.Som(debt) : "",
            });
        OnPropertyChanged(nameof(HasClientSearchResults));
    }

    /// <summary>2026-10-21 (пакет 6, п. 13 аудита кассы): строка списка клиентов — с суммой долга справа.</summary>
    public sealed class DebtClientOption : ClientOption
    {
        public string DebtText { get; init; } = "";
    }

    /// <summary>id клиента → долг по сводке сервера (GET clients/debtors/); пусто — сводки нет, список как раньше.</summary>
    private Dictionary<string, double> _debtByClient = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadDebtorsAsync(CancellationToken ct)
    {
        try
        {
            if (App.AppHost?.Services.GetService<ClientDebtsApiService>() is not { } api)
                return;
            var debtors = await api.GetDebtorsAsync(0.01, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            _debtByClient = debtors.Where(d => d.DebtTotal > 0.005 && !string.IsNullOrWhiteSpace(d.ClientId))
                .GroupBy(d => d.ClientId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(d => d.DebtTotal), StringComparer.OrdinalIgnoreCase);
            ApplyClientFilter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: суммы долгов для списка клиентов не получены: {ex.Message}", "WARNING");
        }
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
        var amountText = DebtSaleRow.InputText(amount);

        return new DebtSaleRow
        {
            Id = TryGetString(element, "id") ?? "",
            // 2026-10-18: номер чека — сервер отдаёт строкой или числом.
            Number = TryGetString(element, "number") ?? (TryGetDouble(element, "number") is { } n ? n.ToString("0", CultureInfo.InvariantCulture) : ""),
            DealId = TryGetString(element, "deal_id") ?? "",
            DateDisplay = created == default ? "" : created.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            CreatedAt = created,
            FirstItemName = TryGetString(element, "first_item_name") ?? "",
            Amount = amount,
            // 2026-10-19, К1: подписи сумм — «14 000,00 сом» (MoneyText); AmountToPayText (поле ввода) — прежнее «0.00».
            AmountDisplay = MoneyText.Som(amount),
            AmountToPayText = amountText,
            SaleRemaining = TryGetDouble(element, "debt_remaining") ?? amount,
            SaleRemainingFromServer = TryGetDouble(element, "debt_remaining") is not null,
            OriginalAmountDisplay = MoneyText.Som(amount),
            OriginalAmount = amount, // 2026-10-23 (аудит, круг 8, В-3): для сверки «долга без продажи» с чеком
        };
    }

    /// <summary>Непогашенные взносы сделки (paid_on пуст) в порядке графика — 2026-09-15: подтверждено живым захватом DevTools,
    /// что долг в NurCRM — график взносов внутри сделки и оплата идёт по installment_id (id и paid_on подтверждены). 2026-10-18,
    /// пятый/шестой круг ревью: с суммой к оплате (amount/sum минус paid_amount; эти поля живым захватом НЕ подтверждены — нет
    /// суммы → null), чтобы при прерванной оплате записать в смену только взносы с известной суммой.</summary>
    private static List<(string Id, double? Sum)> FindUnpaidInstallments(JsonElement deal)
    {
        var result = new List<(string, double?)>();
        if (deal.ValueKind != JsonValueKind.Object || !deal.TryGetProperty("installments", out var installments)
            || installments.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var installment in installments.EnumerateArray())
        {
            if (installment.ValueKind != JsonValueKind.Object)
                continue;
            if (installment.TryGetProperty("paid_on", out var paidOn) && paidOn.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(paidOn.GetString()))
                continue;
            if (!installment.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idEl.GetString()))
                continue;
            var full = TryGetDouble(installment, "amount") ?? TryGetDouble(installment, "sum");
            double? sum = full is { } f ? Math.Max(0, Math.Round(f - (TryGetDouble(installment, "paid_amount") ?? 0), 2)) : null;
            result.Add((idEl.GetString()!, sum));
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
