using NurMarketKassa.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Views.Dialogs; using NurMarketKassa.AvaloniaHost.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;



using System.Windows.Data;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    public partial class FinanceWindow : Window, INotifyPropertyChanged
    {
        // ── внутренние коллекции ──
        private readonly ObservableCollection<SaleItem> _sales = new();
        private readonly ObservableCollection<RefundItem> _refunds = new();
        private readonly ObservableCollection<HistoryItem> _history = new();
        private List<ShiftCardVm> _allShifts = new();
        private List<AllProductRow> _allProductRows = new();
        private List<SaleLineFact> _lastSaleLineFacts = new();
        private List<SaleItem> _lastAllSales = new();

        /// <summary>Границы уже загруженного с сервера окна продаж — по ним решается, нужен ли
        /// новый запрос при смене периода (см. LoadDataAsync).</summary>
        private DateTime _lastFetchFrom = DateTime.MinValue;
        private DateTime _lastFetchTo = DateTime.MinValue;

        /// <summary>Период, который сейчас ПОКАЗАН на странице (может быть уже загруженного окна —
        /// см. _lastFetchFrom). Нужен показателям, которые считаются не из списка продаж:
        /// например, расходы из денежного ящика.</summary>
        private DateTime _displayFrom = DateTime.Today;
        private DateTime _displayTo = DateTime.Today;
        private int _summaryPeriod; // 0=день, 1=неделя, 2=месяц — см. UpdateSummaryPanel/RefreshSummaryPanelAsync
        private CancellationTokenSource? _summaryCts;
        private bool _shiftsShowOpen = true;
        private ObservableCollection<CashSessionEntry> _cashSessions;
        private readonly CollectionViewSource _salesViewSource = new();
        private readonly CollectionViewSource _historyViewSource = new();
        private DateTime _historyFrom = DateTime.Today;
        private DateTime _historyTo = DateTime.Today;
        private bool _isLoading;
        private string _errorMessage; 
        private CancellationTokenSource _loadCts;
        private DispatcherTimer _clockTimer;
        private string _currentUserId;
        private bool _isHamburgerOpen;

        private static readonly string CashHistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "cash_history.json");

        // ── публичные свойства ──
        public ObservableCollection<TopItem> TopItems { get; } = new();
        public ObservableCollection<DayInsightItem> DayInsights { get; } = new();
        public ObservableCollection<CashSessionEntry> CashSessions => _cashSessions;
        public ICollectionView SalesView => _salesViewSource.View;
        public ICollectionView RefundsView => CollectionViewSource.GetDefaultView(_refunds);
        public ICollectionView HistoryView => _historyViewSource.View;

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotLoading)); }
        }

        public bool IsNotLoading => !IsLoading;

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public FinanceWindow()
        {
            InitializeComponent();
            _currentUserId = App.CurrentUserId;

            _salesViewSource.Source = _sales;
            _salesViewSource.Filter += FilterSales;
            _historyViewSource.Source = _history;
            _historyViewSource.Filter += FilterHistory;

            DataContext = this;

            FullscreenHelper.Apply(this);

            var cashHistory = LoadCashHistoryFromDisk();
            _cashSessions = new ObservableCollection<CashSessionEntry>(cashHistory);
            OnPropertyChanged(nameof(CashSessions));

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss   dd.MM.yyyy");
            _clockTimer.Start();
        }

        private void FilterSales(object sender, FilterEventArgs e)
        {
            e.Accepted = true;
        }

        private void FilterHistory(object sender, FilterEventArgs e)
        {
            e.Accepted = true;
        }

        public class TopItem
        {
            public string ProductName { get; set; }
            public decimal Revenue { get; set; }
            public int Quantity { get; set; }
        }

        public class ShiftCardVm
        {
            public string Title { get; set; } = "";
            public bool IsOpen { get; set; }
            public string StatusText { get; set; } = "";
            public string OpenedDisplay { get; set; } = "";
            public string RevenueText { get; set; } = "";
            public string SalesCountText { get; set; } = "";
            public string CashboxName { get; set; } = "";
            public string CashierLine { get; set; } = "";
            public DateTime OpenedAtRaw { get; set; }
            public string ShiftId { get; set; } = "";
            public string CashboxId { get; set; } = "";
            public decimal SalesTotalRaw { get; set; }
            public int SalesCountRaw { get; set; }

            /// <summary>Касса этого терминала — её сменой управляют кнопками в самой кассе
            /// (MainWindow.OpenShiftAsync/CloseShiftAsync), чтобы не расходиться с локальным
            /// PosApp.ActiveShiftId/ShiftService.IsShiftOpen, от которых зависит чек/корзина.
            /// Отсюда можно управлять только сменами ДРУГИХ касс компании.</summary>
            public bool IsCurrentCashbox { get; set; }
            public bool CanOpen => !IsCurrentCashbox && !IsOpen;
            public bool CanClose => !IsCurrentCashbox && IsOpen;
        }

        public class UnpopularProductRow
        {
            public string ProductName { get; set; } = "";
            public string StockText { get; set; } = "";
            public string CategoryName { get; set; } = "";
        }

        public class AllProductRow
        {
            public string ProductName { get; set; } = "";
            public string Article { get; set; } = "";
            public string StockText { get; set; } = "";
            public string CategoryName { get; set; } = "";
        }

        public class DayInsightItem
        {
            public string DayName { get; set; }
            public string ProductName { get; set; }
            public int Quantity { get; set; }
            public decimal Revenue { get; set; }
            public string SummaryText => $"{DayName} — лучше всего продаётся «{ProductName}» ({Quantity} шт., {Revenue:N0} сом)";
        }

        private sealed record SaleLineFact(DateTime SaleDate, string ProductName, decimal Revenue, int Quantity, decimal Cost);

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer?.Stop();
            CancelLoad();

            // CancelLoad отменяет только _loadCts. Сводка («День/Неделя/Месяц») живёт на своём
            // токене и после закрытия окна продолжала запрашивать до 200 чеков, а потом писала
            // результат в контролы уничтоженного окна.
            _summaryCts?.Cancel();
            _summaryCts?.Dispose();
            _summaryCts = null;
            base.OnClosed(e);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            FromPicker.SelectedDate = new DateTimeOffset(_historyFrom);
            ToPicker.SelectedDate = new DateTimeOffset(_historyTo);
            CustomDatePill.IsChecked = false;
            CustomDatePanel.IsVisible = false;
            await LoadDataAsync(_historyFrom, _historyTo);
        }
        

        private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e)
        {
            e.Handled = true;
        }

        private void HamburgerMenu_Click(object sender, RoutedEventArgs e)
        {
            _isHamburgerOpen = !_isHamburgerOpen;
            AnimateHamburgerMenu(_isHamburgerOpen);
        }

        private void HamburgerOverlay_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (_isHamburgerOpen)
            {
                _isHamburgerOpen = false;
                AnimateHamburgerMenu(false);
            }
        }

        private void HamburgerMenuClose_Click(object sender, RoutedEventArgs e)
        {
            _isHamburgerOpen = false;
            AnimateHamburgerMenu(false);
        }

        private void ShowUnderConstruction()
        {
            MainContent.IsVisible = false;
            UnderConstructionPanel.IsVisible = true;
        }

        private void HideUnderConstruction()
        {
            UnderConstructionPanel.IsVisible = false;
            MainContent.IsVisible = true;
        }

        private void UnderConstructionBack_Click(object sender, RoutedEventArgs e)
        {
            HideUnderConstruction();
        }

        private void AnimateHamburgerMenu(bool open)
        {
            HamburgerOverlay.IsVisible = open;
            HamburgerPanel.Margin = open ? new Thickness(0, 0, 0, 0) : new Thickness(-320, 0, 0, 0);
        }

        // Закрываем меню при выборе пункта
        private void CloseHamburgerMenu()
        {
            if (_isHamburgerOpen)
            {
                _isHamburgerOpen = false;
                AnimateHamburgerMenu(false);
            }
        }

        private async void NavigateToSale_Click(object sender, RoutedEventArgs e)
        {
            CloseHamburgerMenu();
            var salesWindow = new SalesWindow();
            await salesWindow.ShowDialog<bool>(this);
            Close();
        }

        private void NavigateToStock_Click(object sender, RoutedEventArgs e)
        {
            CloseHamburgerMenu();
            var warehouseWindow = App.GetRequiredService<WarehouseWindow>();
            warehouseWindow.AnalyticsOnly = true;
            warehouseWindow.Show(this);
        }

        private async void ManageShift_Click(object sender, RoutedEventArgs e)
        {
            CloseHamburgerMenu();
            MainContent.IsVisible = false;
            ShiftsPanel.IsVisible = true;
            await LoadShiftsAsync().ConfigureAwait(true);
        }

        private void ShiftsBack_Click(object sender, RoutedEventArgs e)
        {
            ShiftsPanel.IsVisible = false;
            MainContent.IsVisible = true;
        }

        private void NavigateToProducts_Click(object sender, RoutedEventArgs e)
        {
            CloseHamburgerMenu();
            MainContent.IsVisible = false;
            ProductsAnalyticsPanel.IsVisible = true;
            RefreshProductsAnalyticsPanel();
        }

        private void ProductsBack_Click(object sender, RoutedEventArgs e)
        {
            ProductsAnalyticsPanel.IsVisible = false;
            MainContent.IsVisible = true;
        }

        private void ShiftsOpenTab_Click(object sender, RoutedEventArgs e)
        {
            _shiftsShowOpen = true;
            ShiftsOpenTabButton.Classes.Add("Active");
            ShiftsClosedTabButton.Classes.Remove("Active");
            ApplyShiftsFilter();
        }

        private void ShiftsClosedTab_Click(object sender, RoutedEventArgs e)
        {
            _shiftsShowOpen = false;
            ShiftsClosedTabButton.Classes.Add("Active");
            ShiftsOpenTabButton.Classes.Remove("Active");
            ApplyShiftsFilter();
        }

        private void ApplyShiftsFilter()
        {
            var visible = _allShifts.Where(s => s.IsOpen == _shiftsShowOpen).ToList();
            ShiftsList.ItemsSource = visible;
            ShiftsEmptyText.IsVisible = visible.Count == 0;
        }

        /// <summary>GET /api/construction/shifts/ (CashShiftList) — тот же источник, что уже
        /// использует ShiftStateService для определения текущей открытой смены на кассе.</summary>
        private async Task LoadShiftsAsync()
        {
            try
            {
                var raw = await App.ShiftApi.ConstructionShiftsListAsync(ct: CancellationToken.None).ConfigureAwait(true);
                var rows = raw.ValueKind == JsonValueKind.Array
                    ? raw.EnumerateArray().ToList()
                    : raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("results", out var results) &&
                      results.ValueKind == JsonValueKind.Array
                        ? results.EnumerateArray().ToList()
                        : new List<JsonElement>();

                _allShifts = rows.Select(ParseShiftRow).OrderByDescending(s => s.OpenedAtRaw).ToList();

                var openCount = _allShifts.Count(s => s.IsOpen);
                var closedCount = _allShifts.Count - openCount;
                ShiftsOpenCountText.Text = openCount.ToString();
                ShiftsClosedCountText.Text = closedCount.ToString();

                ApplyShiftsFilter();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Shifts list load failed: {ex}", "WARNING");
                _allShifts = new List<ShiftCardVm>();
                ApplyShiftsFilter();
            }
        }

        private static ShiftCardVm ParseShiftRow(JsonElement row)
        {
            var isOpen = row.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String &&
                string.Equals(statusProp.GetString(), "open", StringComparison.OrdinalIgnoreCase);

            var openedAt = row.TryGetProperty("opened_at", out var openedProp) &&
                DateTime.TryParse(openedProp.GetString(), out var openedDt) ? openedDt : DateTime.MinValue;

            var idText = row.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "") : "";
            var shortId = idText.Length >= 8 ? idText[..8].ToUpperInvariant() : idText.ToUpperInvariant();

            var salesCount = row.TryGetProperty("sales_count", out var scProp) && scProp.ValueKind == JsonValueKind.Number
                ? scProp.GetInt32() : 0;
            var salesTotal = row.TryGetProperty("sales_total", out var stProp) ? ParseDecimal(stProp) : 0m;
            var cashboxName = row.TryGetProperty("cashbox_name", out var cbProp) ? (cbProp.GetString() ?? "—") : "—";
            var cashierName = row.TryGetProperty("cashier_display", out var chProp) ? (chProp.GetString() ?? "—") : "—";
            var cashboxId = TryCashboxId(row) ?? "";

            return new ShiftCardVm
            {
                Title = $"{Tr.T("Смена", "Кезек", "Shift", "Vardiya", "Smena")} #{shortId}",
                IsOpen = isOpen,
                StatusText = isOpen
                    ? Tr.T("Открыта", "Ачык", "Open", "Açık", "Ochiq")
                    : Tr.T("Закрыта", "Жабык", "Closed", "Kapalı", "Yopiq"),
                OpenedDisplay = openedAt == DateTime.MinValue
                    ? "—"
                    : $"{Tr.T("Открыта", "Ачылды", "Opened", "Açıldı", "Ochildi")} {openedAt:d MMMM в HH:mm}",
                RevenueText = $"{salesTotal:N2} сом",
                SalesCountText = salesCount.ToString(),
                CashboxName = cashboxName,
                CashierLine = $"{Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")}: {cashierName}",
                OpenedAtRaw = openedAt,
                ShiftId = idText,
                CashboxId = cashboxId,
                SalesTotalRaw = salesTotal,
                SalesCountRaw = salesCount,
                IsCurrentCashbox = !string.IsNullOrEmpty(cashboxId) &&
                    string.Equals(cashboxId, PosApp.PosCashboxId, StringComparison.Ordinal),
            };
        }

        /// <summary>Тот же разбор, что ShiftHelper.RowMatchesCashbox (id кассы либо в
        /// "cashbox_id", либо во вложенном объекте "cashbox").</summary>
        private static string? TryCashboxId(JsonElement row)
        {
            if (row.TryGetProperty("cashbox", out var cb))
            {
                if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("id", out var cid))
                    return cid.ValueKind == JsonValueKind.String ? cid.GetString() : cid.GetRawText();
                if (cb.ValueKind == JsonValueKind.String)
                    return cb.GetString();
            }
            if (row.TryGetProperty("cashbox_id", out var cbi))
                return cbi.ValueKind == JsonValueKind.String ? cbi.GetString() : cbi.GetRawText();
            return null;
        }

        /// <summary>Открытие смены ДРУГОЙ (не текущего терминала) кассы компании прямо из
        /// списка "Финансы → Смены" — по просьбе пользователя ("несколько касс на одну смену",
        /// "как в вебе"). В отличие от MainWindow.OpenShiftAsync здесь напрямую вызывается
        /// IShiftApiService (обходя CashShiftService), потому что CashShiftService жёстко
        /// привязан к ОДНОЙ кассе этого терминала (PosApp.PosCashboxId/ActiveShiftId) — вызов
        /// его отсюда для чужой кассы испортил бы локальное состояние текущей смены/чека.
        /// Своя касса терминала в списке не получает эту кнопку (см. ShiftCardVm.CanOpen).</summary>
        private async void OpenShiftForCashbox_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ShiftCardVm row })
                return;

            var dlg = App.GetRequiredService<OpenShiftDialog>();
            if (await dlg.ShowDialog<bool?>(this).ConfigureAwait(true) != true)
                return;

            try
            {
                await App.ShiftApi.ConstructionShiftOpenAsync(
                    row.CashboxId, dlg.OpeningCash.ToString("0.00", CultureInfo.InvariantCulture), CancellationToken.None)
                    .ConfigureAwait(true);
                await LoadShiftsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Open shift for other cashbox failed: {ex}", "SHIFT");
                PosMessageBox.Show(this, "Не удалось открыть смену: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void CloseShiftForCashbox_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ShiftCardVm row } || string.IsNullOrEmpty(row.ShiftId))
                return;

            var dlg = App.GetRequiredService<CloseShiftDialog>();
            dlg.Totals = new ShiftBalanceHelper.ShiftTotals
            {
                TotalSales = row.SalesTotalRaw,
                SalesCount = row.SalesCountRaw,
            };
            if (await dlg.ShowDialog<bool?>(this).ConfigureAwait(true) != true)
                return;

            try
            {
                var closing = dlg.ClosingCash?.ToString("0.00", CultureInfo.InvariantCulture);
                await App.ShiftApi.ConstructionShiftCloseAsync(row.ShiftId, closing, null, CancellationToken.None)
                    .ConfigureAwait(true);
                await LoadShiftsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Close shift for other cashbox failed: {ex}", "SHIFT");
                PosMessageBox.Show(this, "Не удалось закрыть смену: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Пересчитывает популярные/непопулярные товары из уже загруженных данных
        /// текущего периода (TopItems/facts) — без отдельных запросов к серверу.</summary>
        private void RefreshProductsAnalyticsPanel()
        {
            // Пустой TopItems может означать либо реальное отсутствие продаж за период, либо
            // неудачную загрузку (например, кратковременный обрыв связи) — раньше оба случая
            // выглядели одинаково ("Нет данных"), что сбивало с толку. HasError уже выставляется
            // в LoadDataAsync при сбое запроса, но баннер с ошибкой показывается только на главной
            // вкладке продаж, а не здесь — поэтому дублируем понятное сообщение прямо в диаграммах.
            if (HasError)
            {
                ShowProductsLoadError();
                return;
            }

            var soldNames = new HashSet<string>(TopItems.Select(t => t.ProductName), StringComparer.OrdinalIgnoreCase);
            // Facts могут включать товары за пределами топ-10 — сверяем со всеми проданными
            // именами, а не только с усечённым списком TopItems.
            foreach (var fact in _lastSaleLineFacts)
                soldNames.Add(fact.ProductName);

            var unpopular = CatalogCacheService.Products
                .Where(p => !soldNames.Contains(p.Title))
                .OrderByDescending(p => p.Quantity)
                .Take(30)
                .ToList();

            UnpopularProductsGrid.ItemsSource = unpopular
                .Select(p => new UnpopularProductRow
                {
                    ProductName = p.Title,
                    StockText = p.Quantity.ToString("0.###"),
                    CategoryName = string.IsNullOrWhiteSpace(p.Category) ? "—" : p.Category!,
                })
                .ToList();

            BarChartRenderer.Render(PopularProductsChart, TopItems
                .Select(t => (t.ProductName, (double)t.Revenue, $"{t.Revenue:N0} сом"))
                .ToList());
            BarChartRenderer.RenderPie(PopularProductsPie, TopItems
                .Take(10)
                .Select(t => (t.ProductName, (double)t.Revenue, $"{t.Revenue:N0} сом"))
                .ToList());
            BarChartRenderer.Render(UnpopularProductsChart, unpopular
                .Take(10)
                .Select(p => (p.Title, p.Quantity, $"{p.Quantity:N0} шт."))
                .ToList());

            RefreshAllProductsSection();
        }

        /// <summary>
        /// "Все товары": весь каталог (не только проданное/непроданное за период) — доля каждого
        /// товара от общего количества/веса на складе показана круговой диаграммой. Товары с
        /// весовым и штучным учётом складываются напрямую (см. примечание под диаграммой,
        /// finance.allProductsPieNote) — это тот же упрощённый подход, что уже используется для
        /// "Непопулярные товары" (Quantity без учёта единиц измерения).
        /// </summary>
        private void RefreshAllProductsSection()
        {
            _allProductRows = CatalogCacheService.Products
                .OrderByDescending(p => p.Quantity)
                .Select(p => new AllProductRow
                {
                    ProductName = p.Title,
                    Article = string.IsNullOrWhiteSpace(p.Article) ? "—" : p.Article!,
                    StockText = $"{p.Quantity:0.###} {(p.MustWeigh ? "кг" : "шт.")}",
                    CategoryName = string.IsNullOrWhiteSpace(p.Category) ? "—" : p.Category!,
                })
                .ToList();

            ApplyAllProductsFilter();

            const int topSlices = 9;
            var byStock = CatalogCacheService.Products
                .Where(p => p.Quantity > 0)
                .OrderByDescending(p => p.Quantity)
                .ToList();

            var pieItems = byStock
                .Take(topSlices)
                .Select(p => (p.Title, p.Quantity, $"{p.Quantity:N0} {(p.MustWeigh ? "кг" : "шт.")}"))
                .ToList();

            var othersQty = byStock.Skip(topSlices).Sum(p => p.Quantity);
            if (othersQty > 0)
                pieItems.Add((Tr.T("Остальные товары", "Башка товарлар", "Other products", "Diğer ürünler", "Boshqa mahsulotlar"), othersQty, $"{othersQty:N0}"));

            BarChartRenderer.RenderPie(AllProductsStockPie, pieItems, diameter: 200);
        }

        private void ShowProductsLoadError()
        {
            var message = Tr.T(
                $"Не удалось загрузить данные за период: {ErrorMessage}",
                $"Мезгил үчүн маалыматтарды жүктөө мүмкүн болгон жок: {ErrorMessage}");
            foreach (var panel in new[] { PopularProductsChart, PopularProductsPie, UnpopularProductsChart })
            {
                panel.Children.Clear();
                panel.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        private void AllProductsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyAllProductsFilter();

        private void ApplyAllProductsFilter()
        {
            var query = (AllProductsSearchBox.Text ?? "").Trim();
            AllProductsGrid.ItemsSource = query.Length < 2
                ? _allProductRows
                : _allProductRows
                    .Where(r => r.ProductName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || r.Article.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            CloseHamburgerMenu();
            Close();
        }

        /// <summary>Запасной путь для «Финансов», когда сервера нет. Источник тот же, что и у
        /// экрана «Продажи»: SoldLineItems, куда касса пишет каждую проданную строку в момент
        /// оплаты. Чек — это группа строк с одной отметкой времени.
        ///
        /// Возвратов и способа оплаты здесь нет: они живут только на сервере. Поэтому оплата
        /// остаётся прочерком, а не выдумывается, и в шапке прямо сказано, что данные локальные.</summary>

        /// <summary>Пересчитывает ABC за выбранный период. Считается локально, поэтому работает
        /// и без интернета — как и остальная аналитика.</summary>
        private void RefreshAbc()
        {
            try
            {
                var data = AnalyticsReportData.Build(_historyFrom, _historyTo);

                AbcSection.Update(data);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ABC-анализ не построен: {ex.Message}", "WARNING");

            }
        }

        /// <summary>Выгрузка аналитики за выбранный период — те же файлы, что и на экране
        /// «Продажи». Отчёт строится из локальных данных кассы, интернет не нужен.</summary>
        private async void ExportExcel_Click(object? sender, RoutedEventArgs e) =>
            await ExportAnalyticsAsync(toWord: false).ConfigureAwait(true);

        private async void ExportWord_Click(object? sender, RoutedEventArgs e) =>
            await ExportAnalyticsAsync(toWord: true).ConfigureAwait(true);

        private async Task ExportAnalyticsAsync(bool toWord)
        {
            if (!TariffGate.CanUseAnalyticsExport)
            {
                ErrorMessage = TariffGate.AnalyticsExportLockedMessage;
                return;
            }

            var extension = toWord ? "docx" : "xlsx";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = toWord ? "Сохранить отчёт в Word" : "Сохранить отчёт в Excel",
                SuggestedFileName = $"analitika-{_historyFrom:yyyy-MM-dd}_{_historyTo:yyyy-MM-dd}.{extension}",
                FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
            });
            if (file is null)
                return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                ErrorMessage = "Не удалось определить путь файла — выберите папку на этом компьютере.";
                return;
            }

            try
            {
                ErrorMessage = "Готовлю отчёт…";
                var data = await Task.Run(() => AnalyticsReportData.Build(_historyFrom, _historyTo)).ConfigureAwait(true);
                var shop = UserPreferences.Instance.StoreName;

                await Task.Run(() =>
                {
                    if (toWord)
                        AnalyticsExportService.ExportToWord(path!, data, shop);
                    else
                        AnalyticsExportService.ExportToExcel(path!, data, shop);
                }).ConfigureAwait(true);

                ErrorMessage = $"Отчёт сохранён: {path}";
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выгрузка аналитики не удалась: {ex}", "WARNING");
                ErrorMessage = "Не удалось сохранить отчёт: " + ex.Message;
            }
        }

        private void LoadFromLocalHistory(DateTime from, DateTime to, CancellationToken token)
        {
            var fromUtc = from.Date.ToUniversalTime();
            var toUtc = to.Date.AddDays(1).ToUniversalTime();
            var lines = SoldLineItemsStore.LoadWithPriceSince(fromUtc, toUtc);

            var sales = lines
                .GroupBy(l => l.SoldAt)
                .OrderBy(g => g.Key)
                .Select((g, index) => new SaleItem
                {
                    Id = "local-" + g.Key.Ticks.ToString(CultureInfo.InvariantCulture),
                    CreatedAt = g.Key.ToLocalTime(),
                    ReceiptNumber = "№" + (index + 1).ToString(CultureInfo.InvariantCulture),
                    TotalAmount = (decimal)g.Sum(x => x.Quantity * x.UnitPrice),
                    PaymentMethod = "",
                })
                .ToList();

            token.ThrowIfCancellationRequested();

            var noRefunds = new List<RefundItem>();
            UpdateCollections(sales, noRefunds);
            UpdateStats(sales, noRefunds);
            UpdateCharts(sales);
            UpdateHistory(sales, noRefunds);

            RefreshAbc();

            ErrorMessage = sales.Count > 0
                ? "Нет связи с сервером — показаны данные этой кассы за выбранный период."
                : "Нет связи с сервером, а локальных продаж за выбранный период нет.";
        }

        private async Task LoadDataAsync(DateTime from, DateTime to, bool forceRefresh = false)
        {
            CancelLoad();
            var currentCts = new CancellationTokenSource();
            _loadCts = currentCts;
            var token = currentCts.Token;

            try
            {
                IsLoading = true;
                ErrorMessage = null;

                // 2026-09-17, по репорту пользователя ("нажимаешь неделю и очень долго ждёшь",
                // "Финансы очень медленно загружаются") — раньше КАЖДЫЙ клик по периоду
                // (Сегодня/Вчера/Неделя/Месяц/своя дата) заново выкачивал постранично ВСЮ историю
                // продаж магазина с сервера (FetchAllSalesAsync — до 60 страниц), хотя сервер не
                // умеет фильтровать список продаж по дате и результат для того же периода не
                // менялся. Теперь полная история скачивается один раз за сеанс окна (или по кнопке
                // "Обновить"), а переключение периода просто перефильтровывает уже загруженный
                // список на клиенте — мгновенно, без сети.
                // 2026-09-21: с сервера берётся не вся история магазина, а ограниченное окно
                // (см. FetchAllSalesAsync — сервер умеет date_from/date_to).
                // Окно шире выбранного периода намеренно: карточка сводки
                // (RefreshSummaryPanelAsync) считает по этим же данным и в режиме «Месяц»
                // смотрит 30 дней плюс ещё 30 предыдущих для сравнения. Поэтому качаем
                // объединение периода страницы и последних 60 дней — иначе сводка молча
                // показала бы нули за «прошлый месяц».
                _displayFrom = from.Date;
                _displayTo = to.Date;

                var summaryFrom = DateTime.Today.AddDays(-59);
                var fetchFrom = from.Date < summaryFrom ? from.Date : summaryFrom;
                var fetchTo = to.Date > DateTime.Today ? to.Date : DateTime.Today;

                // Переключение периодов внутри уже загруженного окна не ходит в сеть вовсе —
                // как и раньше, но теперь окно ограничено датами, а не возрастом магазина.
                if (forceRefresh || _lastAllSales.Count == 0
                    || _lastFetchFrom != fetchFrom || _lastFetchTo != fetchTo)
                {
                    _lastAllSales = await FetchAllSalesAsync(fetchFrom, fetchTo, token);
                    _lastFetchFrom = fetchFrom;
                    _lastFetchTo = fetchTo;
                }

                // Фильтр оставлен как страховка: если сервер вдруг проигнорирует date_from/date_to,
                // период всё равно будет верным — просто загрузка окажется дольше.
                var sales = _lastAllSales
                    .Where(s => s.CreatedAt.Date >= from.Date && s.CreatedAt.Date <= to.Date)
                    .ToList();

                AssignFallbackReceiptNumbers(sales);

                // Реальный API продаж не содержит поля "это возврат" — статус чека это только
                // new/paid/debt/canceled. Возврат по оплаченному чеку регистрируется отдельно,
                // через журнал удалений позиций из корзины (см. FetchCartItemDeletionsAsync).
                var refunds = await FetchCartItemDeletionsAsync(from, to, token);

                UpdateCollections(sales, refunds);
                // 2026-09-17: базовые показатели (выручка/чеки/наличные/безнал) считаются сразу из
                // уже полученных sales/refunds и не зависят от построчных данных чеков — раньше
                // UpdateStats ждала полной загрузки LoadTopItemsAsync (нужна для маржи), из-за чего
                // ВСЕ показатели, включая самые простые, оставались пустыми на весь долгий период
                // построчной загрузки. Теперь базовые показатели видны сразу (маржа/чистая прибыль
                // тут ещё по данным ПРЕДЫДУЩЕГО периода/пустые при первой загрузке), а после
                // LoadTopItemsAsync UpdateStats вызывается повторно и обновляет уже настоящую маржу.
                UpdateStats(sales, refunds);
                UpdateCharts(sales);
                UpdateHistory(sales, refunds);
                await LoadTopItemsAsync(sales, token);
                UpdateStats(sales, refunds);

                RefreshAbc();
                _ = RefreshSummaryPanelAsync();
            }
            catch (OperationCanceledException)
            {
                PosLogger.Log("Finance refresh canceled.", "DEBUG");
            }
            catch (Exception ex)
            {
                // Сервера нет — показываем то, что касса знает сама (см. LoadFromLocalHistory).
                // Магазин без интернета работать не перестаёт, и свои цифры владелец должен
                // видеть в любой момент, а не упираться в пустой экран с красной строкой.
                PosLogger.Log($"Финансы: сервер недоступен ({ex.Message}) — показываю локальные данные.", "FINANCE");
                LoadFromLocalHistory(from, to, token);
            }
            finally
            {
                if (ReferenceEquals(_loadCts, currentCts))
                {
                    _loadCts = null;
                    IsLoading = false;
                }
                currentCts.Dispose();
            }
        }

        private async Task LoadTopItemsAsync(List<SaleItem> sales, CancellationToken token)
        {
            var facts = await FetchSaleLineFactsAsync(sales, token);
            _lastSaleLineFacts = facts;
            UpdateTopItems(facts);
            UpdateDayOfWeekInsights(facts);
        }

        /// <summary>2026-09-17, по репорту пользователя ("Финансы очень медленно загружаются") —
        /// раньше это был один PosSaleGetAsync НА КАЖДЫЙ чек периода, строго последовательно один
        /// за другим (при сотнях чеков — сотни round-trip подряд). Теперь запросы идут
        /// параллельно с ограничением одновременных (SemaphoreSlim), порядок фактов роли не
        /// играет — дальше они всё равно только группируются.</summary>
        /// <summary>Строки уже оплаченного чека неизменны, поэтому результат кэшируется на время
        /// жизни окна: раньше одни и те же чеки скачивались дважды за один проход загрузки
        /// (LoadTopItemsAsync и следом RefreshSummaryPanelAsync), и заново — при каждом
        /// переключении периода.</summary>
        /// Concurrent, а не обычный Dictionary: FetchOneAsync выполняется до 8 задач одновременно.
        /// Сейчас их продолжения возвращаются на UI-поток и доступ фактически сериализован, но
        /// полагаться на это нельзя — достаточно одного ConfigureAwait(false) в этом файле, чтобы
        /// получить порчу словаря (ровно такая же гонка на общем списке каталога ломала «Склад»).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<SaleLineFact>> _saleLineFactsCache =
            new(StringComparer.OrdinalIgnoreCase);

        private async Task<List<SaleLineFact>> FetchSaleLineFactsAsync(List<SaleItem> sales, CancellationToken token)
        {
            using var gate = new SemaphoreSlim(8);

            // Один снимок каталога на весь проход вместо линейного поиска по списку товаров для
            // КАЖДОЙ строки КАЖДОГО чека: на кассе с 15 000 товаров и 550 чеками это было около
            // 25 млн сравнений строк, причём в продолжениях, выполняющихся на UI-потоке.
            var catalogById = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in CatalogCacheService.Products)
            {
                if (!string.IsNullOrEmpty(product.Id))
                    catalogById[product.Id] = product;
            }

            async Task<List<SaleLineFact>> FetchOneAsync(SaleItem sale)
            {
                if (!string.IsNullOrEmpty(sale.Id)
                    && _saleLineFactsCache.TryGetValue(sale.Id, out var cached))
                    return cached;

                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var json = await App.SalesApi.PosSaleGetAsync(sale.Id, token);
                    var lineFacts = new List<SaleLineFact>();
                    // парсим элементы чека
                    if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in items.EnumerateArray())
                        {
                            string name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                            // The API has no "price" field on a sale line — it uses "unit_price" and
                            // returns the already-computed "line_total". Mirror CartDisplayHelper
                            // (proven against real API data) instead of a field name that doesn't exist,
                            // which silently made every line's revenue compute as 0.
                            decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                            decimal revenue = decimal.TryParse(
                                CartDisplayHelper.LineTotal(line), NumberStyles.Number, CultureInfo.InvariantCulture, out var rt)
                                ? rt
                                : 0;
                            // Настоящая закупочная цена товара уже есть в локальном кэше каталога
                            // (purchase_price, синхронизируется с сервера) — сопоставляем строку чека
                            // с товаром по product_id и берём реальную себестоимость. Товар без
                            // известной закупочной цены даёт себестоимость 0 для этой строки (лучше,
                            // чем произвольное предположение).
                            var productId = CartDisplayHelper.TryProductId(line);
                            CatalogProductTileVm? tile = null;
                            if (!string.IsNullOrEmpty(productId))
                                catalogById.TryGetValue(productId, out tile);
                            decimal cost = tile is { PurchasePrice: > 0 } ? (decimal)tile.PurchasePrice * qty : 0m;
                            lineFacts.Add(new SaleLineFact(sale.CreatedAt, name, revenue, (int)qty, cost));
                        }
                    }

                    if (!string.IsNullOrEmpty(sale.Id))
                        _saleLineFactsCache[sale.Id] = lineFacts;

                    return lineFacts;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Finance receipt aggregation skipped: {ex.GetType().Name}", "WARNING");
                    return new List<SaleLineFact>();
                }
                finally
                {
                    gate.Release();
                }
            }

            var perSale = await Task.WhenAll(sales.Select(FetchOneAsync));
            token.ThrowIfCancellationRequested();
            return perSale.SelectMany(f => f).ToList();
        }

        private void UpdateTopItems(List<SaleLineFact> facts)
        {
            var top10 = facts
                .GroupBy(f => f.ProductName)
                .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.Revenue), Qty = g.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Revenue)
                .Take(10);

            TopItems.Clear();
            foreach (var item in top10)
                TopItems.Add(new TopItem { ProductName = item.Name, Revenue = item.Revenue, Quantity = item.Qty });
        }

        private static readonly (DayOfWeek Day, string Name)[] WeekDayOrder =
        {
            (DayOfWeek.Monday, "Понедельник"),
            (DayOfWeek.Tuesday, "Вторник"),
            (DayOfWeek.Wednesday, "Среда"),
            (DayOfWeek.Thursday, "Четверг"),
            (DayOfWeek.Friday, "Пятница"),
            (DayOfWeek.Saturday, "Суббота"),
            (DayOfWeek.Sunday, "Воскресенье"),
        };

        private void UpdateDayOfWeekInsights(List<SaleLineFact> facts)
        {
            DayInsights.Clear();

            var byDay = facts
                .GroupBy(f => f.SaleDate.DayOfWeek)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.ProductName)
                        .Select(pg => new { Name = pg.Key, Qty = pg.Sum(x => x.Quantity), Revenue = pg.Sum(x => x.Revenue) })
                        .OrderByDescending(x => x.Qty)
                        .FirstOrDefault());

            foreach (var (day, name) in WeekDayOrder)
            {
                if (!byDay.TryGetValue(day, out var best) || best is null || best.Qty <= 0)
                    continue;

                DayInsights.Add(new DayInsightItem
                {
                    DayName = name,
                    ProductName = best.Name,
                    Quantity = best.Qty,
                    Revenue = best.Revenue,
                });
            }
        }

        private void DayInsightIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DayInsightItem insight })
                return;

            var product = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Title, insight.ProductName, StringComparison.OrdinalIgnoreCase));

            var lines = new List<string>();
            if (product != null)
            {
                lines.Add($"Товар: {product.Title}");
                if (!string.IsNullOrWhiteSpace(product.Barcode))
                    lines.Add($"Штрихкод: {product.Barcode}");
                if (!string.IsNullOrWhiteSpace(product.Category))
                    lines.Add($"Категория: {product.Category}");
                if (!string.IsNullOrWhiteSpace(product.Brand))
                    lines.Add($"Бренд: {product.Brand}");
                if (!string.IsNullOrWhiteSpace(product.Unit))
                    lines.Add($"Ед. изм.: {product.Unit}");
                lines.Add($"Цена: {product.PriceLine}");
                lines.Add($"Остаток на складе: {product.Quantity:0.###} {product.Unit}");
                lines.Add($"Тип: {(product.IsWeighted ? "Весовой" : "Штучный")}");
            }
            else
            {
                lines.Add($"Товар: {insight.ProductName}");
                lines.Add("Не найден в текущем каталоге кассы (возможно, переименован или удалён).");
            }

            lines.Add("");
            lines.Add($"Продажи ({insight.DayName.ToLowerInvariant()}): {insight.Quantity} шт. на {insight.Revenue:N2} сом");

            PosDialogs.Info(this, string.Join("\n", lines), "Информация о товаре");
        }

        /// <summary>2026-09-15, живой баг ("время в аналитике не работают" — переключение
        /// периода не меняло данные). Раньше LoadDataAsync запрашивал ОДНУ страницу
        /// (FetchSalesPageAsync(1, 500, ...)), но SalesApiService.PosSalesListAsync молча
        /// урезает page_size до максимума 80 (см. её Math.Clamp) — для магазина с историей
        /// продаж больше ~80 чеков выбор "Неделя"/"Месяц"/произвольного периода фильтровал
        /// один и тот же фиксированный набор из 80 записей, а не реальные продажи за выбранный
        /// диапазон. Порядок сортировки, который в итоге вернёт сервер, не гарантирован (первый
        /// вариант запроса в PosSalesListAsync без "ordering" может сработать раньше, чем
        /// пробуется "-created_at") — поэтому вместо допущения "сначала свежие" (и остановки по
        /// первой "старой" продаже) здесь честно проходим все страницы до конца или до разумного
        /// предела, а сам фильтр по датам, как и раньше, применяется в LoadDataAsync поверх уже
        /// ПОЛНОГО набора.</summary>
        /// <summary>2026-09-21: качает продажи ТОЛЬКО за нужный период. Раньше здесь выкачивалась
        /// вся история магазина (до 60 страниц по 80), потому что считалось, будто сервер не умеет
        /// фильтровать список продаж по дате. Умеет: параметры date_from/date_to проверены живыми
        /// запросами — на этом аккаунте 681 продажа всего и 17 за день, то есть месяц стал одной
        /// страницей вместо девяти, а окно перестало зависеть от возраста магазина.
        /// date_to у сервера НЕ включает свой день, поэтому передаётся следующий (см.
        /// SalesApiService.PosSalesListAsync).</summary>
        private async Task<List<SaleItem>> FetchAllSalesAsync(DateTime from, DateTime to, CancellationToken token)
        {
            const int pageSize = 80;
            const int maxPages = 60;
            var result = new List<SaleItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var page = 1; page <= maxPages; page++)
            {
                token.ThrowIfCancellationRequested();
                var pageItems = await FetchSalesPageAsync(page, pageSize, from, to, token);
                if (pageItems.Count == 0)
                    break;

                // Защита от сервера, который проигнорировал бы "page": без неё цикл отработал бы
                // все 60 итераций, набив список копиями одной и той же страницы — а дальше по
                // каждой «продаже» идёт отдельный запрос за строками чека.
                var added = 0;
                foreach (var item in pageItems)
                {
                    if (string.IsNullOrEmpty(item.Id) || seen.Add(item.Id))
                    {
                        result.Add(item);
                        added++;
                    }
                }

                if (added == 0 || pageItems.Count < pageSize)
                    break;
            }
            return result;
        }

        private async Task<List<SaleItem>> FetchSalesPageAsync(
            int page,
            int pageSize,
            DateTime from,
            DateTime to,
            CancellationToken token)
        {
            var raw = await App.SalesApi.PosSalesListAsync(
                page,
                pageSize,
                null,
                token,
                dateFrom: from.Date,
                dateToExclusive: to.Date.AddDays(1));
            token.ThrowIfCancellationRequested();

            var result = new List<SaleItem>(raw.Count);
            foreach (JsonElement el in raw)
            {
                var item = new SaleItem();
                if (el.TryGetProperty("id", out var idProp))
                    item.Id = idProp.ToString() ?? "";
                if (el.TryGetProperty("created_at", out var dateProp) &&
                    DateTime.TryParse(dateProp.GetString(), out var dt))
                    item.CreatedAt = dt;
                item.ReceiptNumber = TryReceiptNumber(el) ?? "";
                if (el.TryGetProperty("total", out var totalProp))
                    item.TotalAmount = ParseDecimal(totalProp);
                if (el.TryGetProperty("payment_method", out var pmProp))
                    item.PaymentMethod = pmProp.GetString() ?? "";
                if (el.TryGetProperty("is_refund", out var rfProp) && ParseBool(rfProp))
                    item.IsRefund = true;
                if (el.TryGetProperty("refund_reason", out var rrProp))
                    item.RefundReason = rrProp.GetString();
                if (el.TryGetProperty("customer_id", out var cidProp))
                    item.CustomerId = cidProp.GetString();
                // ReceiptNumber остаётся пустым, если сервер не прислал ни одного из известных полей —
                // UpdateCollections подставит читаемый порядковый номер вместо «сырого» GUID.
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// Ищет читаемый номер чека среди возможных полей ответа API (сервер использует разные имена
        /// в разных версиях). Числовые значения дополняются нулями до 6 знаков, как на печатном чеке.
        /// </summary>
        private static string TryReceiptNumber(JsonElement sale)
        {
            if (sale.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var key in new[]
                     {
                         "receipt_number", "receipt_no", "check_number", "check_no", "sale_number", "number", "seq",
                     })
            {
                if (!sale.TryGetProperty(key, out var v))
                    continue;

                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
                s = (s ?? "").Trim();
                if (s.Length == 0)
                    continue;

                return s.All(char.IsAsciiDigit) && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
                    ? num.ToString("D6", CultureInfo.InvariantCulture)
                    : s;
            }

            return null;
        }

        private static decimal ParseDecimal(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d))
                return d;
            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d2))
                return d2;
            return 0m;
        }

        private static bool ParseBool(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
            JsonValueKind.Number => element.TryGetInt32(out var i) && i != 0,
            _ => false,
        };

        private void CancelLoad()
        {
            if (_loadCts != null)
            {
                _loadCts.Cancel();
                _loadCts = null;
            }
        }

        /// <summary>
        /// Сервер не всегда присылает читаемый номер чека — в этом случае вместо «сырого» GUID
        /// показываем стабильный порядковый номер («№1», «№2», …) в хронологическом порядке
        /// за выбранный период, как и в диалоге возврата.
        /// </summary>
        private static void AssignFallbackReceiptNumbers(List<SaleItem> items)
        {
            var position = 0;
            foreach (var item in items.OrderBy(i => i.CreatedAt))
            {
                position++;
                // 2026-09-17: с кэшированием _lastAllSales (см. LoadDataAsync) одни и те же
                // объекты SaleItem теперь переживают несколько вызовов подряд для РАЗНЫХ периодов
                // (Неделя/Месяц/...) — старая проверка "только если пусто" однажды присваивала
                // "№N" и больше никогда не пересчитывала его, так что чек мог показывать номер от
                // совсем другого (более узкого) периода. Настоящие номера с сервера никогда не
                // начинаются с "№", поэтому такие safely пересчитываются каждый раз заново.
                if (string.IsNullOrWhiteSpace(item.ReceiptNumber) || item.ReceiptNumber.StartsWith("№", StringComparison.Ordinal))
                    item.ReceiptNumber = $"№{position}";
            }
        }

        /// <summary>2026-09-17, по репорту пользователя ("нет времени в финансах возвраты") —
        /// поле "created_at" в ответе журнала удалений не всегда несёт время суток (парсится как
        /// 00:00, и колонка "Время" показывала одинаковый фиктивный "00:00" для каждой строки).
        /// Пробуем ещё несколько вероятных имён поля с реальным временем — тот же приём, что и
        /// TryReceiptNumber для номера чека; если ни одно поле не содержит времени, возвращаем
        /// дату как есть (RefundItem.TimeDisplay покажет "—" вместо обманчивого "00:00").</summary>
        private static DateTime? TryDeletionTimestamp(JsonElement el)
        {
            DateTime? dateOnly = null;
            foreach (var key in new[] { "created_at", "deleted_at", "timestamp", "updated_at", "date" })
            {
                if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                    continue;
                if (!DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    continue;
                if (dt.TimeOfDay != TimeSpan.Zero)
                    return dt;
                dateOnly ??= dt;
            }

            return dateOnly;
        }

        /// <summary>Журнал удалений позиций из корзины (product/quantity/who/when) — единственный
        /// след возвратов по оплаченным чекам в реальном API (см. PosCartItemDeletionsListAsync).
        /// Сервер не отдаёт сумму возврата напрямую, поэтому TotalAmount — оценка: текущая цена
        /// товара из каталога × количество (не историческая цена на момент продажи, но ближе
        /// к правде, чем всегда показывать 0 в статистике/аналитике).</summary>
        private async Task<List<RefundItem>> FetchCartItemDeletionsAsync(DateTime from, DateTime to, CancellationToken token)
        {
            try
            {
                var raw = await App.SalesApi.PosCartItemDeletionsListAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                var result = new List<RefundItem>(raw.Count);
                foreach (var el in raw)
                {
                    var item = new RefundItem();
                    if (el.TryGetProperty("id", out var idProp))
                        item.Id = idProp.ToString() ?? "";
                    if (TryDeletionTimestamp(el) is { } dt)
                        item.CreatedAt = dt;
                    if (item.CreatedAt.Date < from.Date || item.CreatedAt.Date > to.Date)
                        continue;

                    if (el.TryGetProperty("product_name", out var nameProp))
                        item.ProductName = nameProp.GetString() ?? "";
                    decimal quantity = 0;
                    if (el.TryGetProperty("quantity", out var qtyProp))
                    {
                        quantity = ParseDecimal(qtyProp);
                        item.QuantityDisplay = quantity.ToString("0.###", CultureInfo.InvariantCulture);
                    }
                    if (el.TryGetProperty("deleted_by_display", out var byProp))
                        item.DeletedBy = byProp.GetString() ?? "";

                    if (quantity > 0 && el.TryGetProperty("product", out var productIdProp) &&
                        productIdProp.ValueKind == JsonValueKind.String)
                    {
                        var productId = productIdProp.GetString();
                        var product = CatalogCacheService.Products.FirstOrDefault(p =>
                            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                        if (product != null)
                            item.TotalAmount = (decimal)LocalCartService.ParsePrice(product.PriceLine) * quantity;
                    }

                    item.Reason = item.ProductName;
                    result.Add(item);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cart item deletions (возвраты) load failed: {ex}", "WARNING");
                return new List<RefundItem>();
            }
        }

        private void UpdateCollections(List<SaleItem> sales, List<RefundItem> refunds)
        {
            _sales.Clear();
            foreach (var s in sales) _sales.Add(s);

            _refunds.Clear();
            foreach (var r in refunds) _refunds.Add(r);
        }

        private void UpdateStats(List<SaleItem> sales, List<RefundItem> refunds)
        {
            decimal totalSales = sales.Sum(s => s.TotalAmount);
            decimal totalRefunds = refunds.Sum(r => Math.Abs(r.TotalAmount));

            decimal cashSales = sales
                .Where(s =>
                    (s.PaymentMethod?.ToUpper() == "CASH") ||
                    (s.PaymentMethod?.ToLower().Contains("нал") ?? false))
                .Sum(s => s.TotalAmount);

            // 2026-09-23. Три расхождения, из-за которых цифры кассы не сходились ни с вебом,
            // ни между собственными экранами:
            //
            // 1. «Возвраты» показывали журнал удалений позиций ИЗ КОРЗИНЫ — то есть строки,
            //    убранные ДО оплаты. Они и так не входят в сумму чека, поэтому это были не
            //    возвраты вовсе. Реальные возвраты оплаченных чеков сервер в итогах не отдаёт,
            //    касса пишет их сама (ShiftEventsStore.KindReturn) — берём их.
            // 2. «Выручка» показывала сумму продаж без вычета возвратов, а «Средний чек» рядом
            //    считался уже с вычетом: две плитки в одной строке противоречили друг другу.
            // 3. «Чеков» считалось как продажи ПЛЮС записи журнала удалений, из-за чего средний
            //    чек делился на завышенное число.
            var realReturns = 0m;
            try
            {
                var events = ShiftEventsStore.TotalsBetween(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime());
                if (events.TryGetValue(ShiftEventsStore.KindReturn, out var returned))
                    realReturns = (decimal)returned;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Возвраты для показателей не прочитаны: {ex.Message}", "WARNING");
            }

            decimal nonCash = totalSales - cashSales;
            int totalCount = sales.Count;
            decimal net = totalSales - realReturns;
            decimal avg = totalCount > 0 ? net / totalCount : 0m;

            // ── Базовые показатели ──
            TotalSalesText.Text = $"{net:N2} сом";
            TotalRefundsText.Text = $"{realReturns:N2} сом";
            CashText.Text = $"{cashSales:N2} сом";
            NonCashText.Text = $"{nonCash:N2} сом";
            AvgReceiptText.Text = $"{avg:N2} сом";
            ReceiptCountText.Text = totalCount.ToString();

            // ── Новые показатели ──
            // Чистая прибыль и маржа — за вычетом возвратов (иначе прибыль выглядела бы
            // завышенной на сумму возвратов). 2026-09-16, по репорту пользователя ("маржа всегда
            // показывает 40%"): себестоимость раньше считалась условно как 60% выручки для ЛЮБОГО
            // товара — теперь берётся реальная закупочная цена каждого проданного товара
            // (см. FetchSaleLineFactsAsync/_lastSaleLineFacts, покрывает тот же период sales, что
            // и эта сводка, так как обе выборки строятся из одного и того же списка sales).
            decimal costOfGoods = _lastSaleLineFacts.Sum(f => f.Cost);
            decimal netProfit = totalSales - costOfGoods - totalRefunds;
            if (NetProfitText != null)
                NetProfitText.Text = $"{netProfit:N2} сом";

            if (MarginPercentText != null)
            {
                decimal margin = net > 0 ? (netProfit / net * 100) : 0;
                MarginPercentText.Text = $"{margin:F1}%";
            }

            // Расходы за период — изъятия из денежного ящика (в т.ч. «Доп. услуга → Расход» при
            // пустой корзине, которая оформляется именно как изъятие). Это деньги, реально
            // вынутые из кассы, и до сих пор в аналитике они не отражались вообще: прибыль
            // выглядела больше, чем осталось в ящике.
            var (deposits, withdrawals) = CashOperationsForPeriod(_displayFrom, _displayTo);
            if (ExpensesText != null)
                ExpensesText.Text = $"{withdrawals:N2} сом";
            if (ExpensesBreakdownText != null)
            {
                ExpensesBreakdownText.Text = deposits > 0m
                    ? $"внесено: {deposits:N2} сом"
                    : "внесений не было";
            }

            if (ProfitAfterExpensesText != null)
                ProfitAfterExpensesText.Text = $"{netProfit - withdrawals:N2} сом";
        }

        /// <summary>Внесения и изъятия из денежного ящика за период. Операции хранятся локально
        /// (сервер их не знает, см. ShiftCashOperationsStore), поэтому фильтруем по дате самой
        /// операции, а не по смене — период на странице может охватывать несколько смен.</summary>
        private static (decimal Deposits, decimal Withdrawals) CashOperationsForPeriod(DateTime from, DateTime to)
        {
            var deposits = 0m;
            var withdrawals = 0m;
            try
            {
                foreach (var op in ShiftCashOperationsStore.LoadAll())
                {
                    var day = op.CreatedAt.Date;
                    if (day < from.Date || day > to.Date)
                        continue;

                    switch (CashOperationModel.ResolveKind(op.Type))
                    {
                        case CashOperationKind.Deposit:
                            deposits += op.Amount;
                            break;
                        case CashOperationKind.Withdrawal:
                            withdrawals += op.Amount;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Finance expenses aggregation failed: {ex.Message}", "WARNING");
            }

            return (deposits, withdrawals);

            // Максимальный, минимальный чек и долг временно скрыты —
            // их можно вернуть, добавив соответствующие TextBlock в XAML
        }

        private void SummaryPeriod_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out var period))
                return;

            _summaryPeriod = period;
            SummaryPeriodDayButton.Classes.Set("Active", period == 0);
            SummaryPeriodWeekButton.Classes.Set("Active", period == 1);
            SummaryPeriodMonthButton.Classes.Set("Active", period == 2);
            _ = RefreshSummaryPanelAsync();
        }

        /// <summary>Сводка простым языком за день/неделю/месяц (см. AI-фичи 2026-09-03, п.2 и
        /// п.11 из мозгового штурма — расширено с "только сегодня" до переключателя периодов).
        /// Никакого внешнего ИИ — просто оборачивает уже загруженные данные в текст. Работает
        /// НЕЗАВИСИМО от диапазона дат основной страницы (Sales/History/чарты) — использует
        /// <see cref="_lastAllSales"/> (см. FetchAllSalesAsync — вся история продаж, постранично,
        /// до ~4800 чеков) и сам вырезает нужное окно, чтобы клик по "Неделя"/"Месяц" в сводке
        /// не переключал фильтр всей остальной страницы.</summary>
        private async Task RefreshSummaryPanelAsync()
        {
            var cts = new CancellationTokenSource();
            _summaryCts?.Cancel();
            _summaryCts = cts;
            var token = cts.Token;

            DailySummaryCard.IsVisible = true;

            var to = DateTime.Today;
            var from = _summaryPeriod switch
            {
                1 => to.AddDays(-6),
                2 => to.AddDays(-29),
                _ => to,
            };
            var periodLengthDays = (to - from).Days + 1;
            var prevTo = from.AddDays(-1);
            var prevFrom = prevTo.AddDays(-(periodLengthDays - 1));

            var periodSales = _lastAllSales.Where(s => s.CreatedAt.Date >= from && s.CreatedAt.Date <= to).ToList();
            var prevPeriodRevenue = _lastAllSales
                .Where(s => s.CreatedAt.Date >= prevFrom && s.CreatedAt.Date <= prevTo)
                .Sum(s => (decimal?)s.TotalAmount);

            var periodLabel = _summaryPeriod switch { 1 => "За неделю", 2 => "За месяц", _ => "Сегодня" };
            var compareLabel = _summaryPeriod switch { 1 => "прошлую неделю", 2 => "прошлый месяц", _ => "вчера" };

            if (periodSales.Count == 0)
            {
                DailySummaryText.Text = _summaryPeriod == 0
                    ? "Сегодня пока не было продаж."
                    : "За этот период пока не было продаж.";
                return;
            }

            List<RefundItem> periodRefunds;
            List<SaleLineFact> periodFacts;
            try
            {
                // Только для выбранного периода — избегаем дорогого N+1 запроса по каждому чеку
                // за месяц, если в сводке сейчас день/неделя (у них периодSales уже маленький).
                periodRefunds = await FetchCartItemDeletionsAsync(from, to, token).ConfigureAwait(true);
                periodFacts = await FetchSaleLineFactsAsync(periodSales.Take(200).ToList(), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            decimal periodRevenue = periodSales.Sum(s => s.TotalAmount);
            decimal periodNet = periodRevenue - periodRefunds.Sum(r => Math.Abs(r.TotalAmount));
            int receiptCount = periodSales.Count;
            decimal avgCheck = receiptCount > 0 ? periodRevenue / receiptCount : 0m;

            var topProduct = periodFacts
                .GroupBy(f => f.ProductName)
                .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.Revenue), Qty = g.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Revenue)
                .FirstOrDefault();

            var busiestHour = periodSales
                .GroupBy(s => s.CreatedAt.Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            var sb = new System.Text.StringBuilder();
            sb.Append($"{periodLabel}: выручка {periodNet:N0} сом");

            if (prevPeriodRevenue is > 0)
            {
                decimal changePercent = (periodRevenue - prevPeriodRevenue.Value) / prevPeriodRevenue.Value * 100;
                string direction = changePercent >= 0 ? "больше" : "меньше";
                sb.Append($", это на {Math.Abs(changePercent):F0}% {direction}, чем за {compareLabel} ({prevPeriodRevenue.Value:N0} сом)");
            }
            sb.Append('.');

            if (topProduct is not null)
                sb.Append($" Лучше всего продавался «{topProduct.Name}» ({topProduct.Qty} шт).");

            sb.Append($" Чеков — {receiptCount}, средний чек — {avgCheck:N0} сом.");

            if (busiestHour is not null)
                sb.Append($" Самое активное время — {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00.");

            if (periodSales.Count > 200)
                sb.Append(" (Товар-лидер посчитан по первым 200 чекам периода.)");

            DailySummaryText.Text = sb.ToString();
        }

        private static readonly string[] PaymentPalette =
            { "#F59E0B", "#22C55E", "#3B82F6", "#A855F7", "#EF4444", "#14B8A6", "#EC4899", "#64748B" };

        private void UpdateCharts(List<SaleItem> sales)
        {
            UpdatePaymentSplitChart(sales);
            UpdateDailyRevenueChart(sales);
            UpdateHourlyRevenueChart(sales);
            UpdatePaymentSplitPie(sales);
            UpdateRevenueTrendCharts(sales);
        }

        private void UpdatePaymentSplitPie(List<SaleItem> sales)
        {
            var byMethod = sales
                .GroupBy(s => NormalizePaymentMethodLabel(s.PaymentMethod))
                .Select(g => (Label: g.Key, Value: (double)g.Sum(s => s.TotalAmount), ValueText: $"{g.Sum(s => s.TotalAmount):N0} сом"))
                .Where(x => x.Value > 0)
                .OrderByDescending(x => x.Value)
                .ToList();
            BarChartRenderer.RenderPie(PaymentSplitPie, byMethod);
        }

        /// <summary>Линейная (тренд), с областями (накопительно) и комбинированная (выручка +
        /// кол-во продаж) диаграммы по дням — на тех же дневных данных, что уже строит
        /// UpdateDailyRevenueChart, просто ещё двумя способами показа.</summary>
        private void UpdateRevenueTrendCharts(List<SaleItem> sales)
        {
            var byDay = sales
                .GroupBy(s => s.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(s => s.TotalAmount), Count = g.Count() })
                .OrderBy(x => x.Date)
                .ToList();

            var linePoints = byDay
                .Select(d => (d.Date.ToString("dd.MM"), (double)d.Total))
                .ToList();
            BarChartRenderer.RenderLine(RevenueTrendChart, linePoints);

            var runningTotal = 0m;
            var cumulativePoints = byDay
                .Select(d =>
                {
                    runningTotal += d.Total;
                    return (d.Date.ToString("dd.MM"), (double)runningTotal);
                })
                .ToList();
            BarChartRenderer.RenderArea(RevenueCumulativeChart, cumulativePoints);

            var combined = byDay
                .Select(d => (d.Date.ToString("dd.MM"), (double)d.Total, (double)d.Count, $"{d.Total:N0} сом"))
                .ToList();
            BarChartRenderer.RenderCombined(RevenueVsSalesChart, combined,
                Tr.T("Выручка", "Киреше", "Revenue", "Ciro", "Tushum"), Tr.T("Продажи, шт.", "Сатуулар, даана", "Sales, pcs.", "Satış, adet", "Sotuv, dona"));
        }

        private static string NormalizePaymentMethodLabel(string method)
        {
            var key = (method ?? "").Trim().ToLowerInvariant();
            return key switch
            {
                "" => "Не указано",
                "cash" => "Наличные",
                "transfer" or "card" => "Перевод",
                "mbank" => "MBank",
                "mixed" => "Смешанный",
                "debt" => "Долг",
                _ when key.Contains("нал") => "Наличные",
                _ => char.ToUpperInvariant(method![0]) + method[1..],
            };
        }

        private void UpdatePaymentSplitChart(List<SaleItem> sales)
        {
            PaymentSplitBar.ColumnDefinitions.Clear();
            PaymentSplitBar.Children.Clear();
            PaymentLegend.Children.Clear();

            decimal total = sales.Sum(s => s.TotalAmount);
            var byMethod = sales
                .GroupBy(s => NormalizePaymentMethodLabel(s.PaymentMethod))
                .Select(g => new { Label = g.Key, Total = g.Sum(s => s.TotalAmount) })
                .Where(x => x.Total > 0)
                .OrderByDescending(x => x.Total)
                .ToList();

            if (total <= 0 || byMethod.Count == 0)
            {
                PaymentSplitBar.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                PaymentSplitBar.Children.Add(new Border { Background = Brushes.LightGray, CornerRadius = new CornerRadius(6) });
                PaymentLegend.Children.Add(new TextBlock
                {
                    Text = "Нет данных за выбранный период",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                });
                return;
            }

            for (var i = 0; i < byMethod.Count; i++)
            {
                var entry = byMethod[i];
                var color = PaymentPalette[i % PaymentPalette.Length];
                var isFirst = i == 0;
                var isLast = i == byMethod.Count - 1;

                PaymentSplitBar.ColumnDefinitions.Add(new ColumnDefinition((double)entry.Total, GridUnitType.Star));
                var segment = new Border
                {
                    Background = Brush.Parse(color),
                    CornerRadius = new CornerRadius(isFirst ? 6 : 0, isLast ? 6 : 0, isLast ? 6 : 0, isFirst ? 6 : 0),
                    [ToolTip.TipProperty] = $"{entry.Label}: {entry.Total:N2} сом ({entry.Total / total * 100:N0}%)",
                };
                Grid.SetColumn(segment, i);
                PaymentSplitBar.Children.Add(segment);

                var pct = entry.Total / total * 100;
                var legendItem = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                legendItem.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brush.Parse(color) });
                legendItem.Children.Add(new TextBlock
                {
                    Text = $"{entry.Label}: {entry.Total:N0} сом ({pct:N0}%)",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                });
                PaymentLegend.Children.Add(legendItem);
            }
        }

        private void UpdateHourlyRevenueChart(List<SaleItem> sales)
        {
            const double maxBarHeight = 80;
            const double barWidth = 22;

            HourlyRevenueBars.Children.Clear();

            if (sales.Count == 0)
            {
                HourlyRevenueBars.Children.Add(new TextBlock
                {
                    Text = "Нет данных за выбранный период",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 0, 8),
                });
                return;
            }

            var byHour = sales
                .GroupBy(s => s.CreatedAt.Hour)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.TotalAmount));
            var maxVal = byHour.Count > 0 ? byHour.Values.Max() : 0m;
            if (maxVal <= 0) maxVal = 1;

            for (var hour = 0; hour < 24; hour++)
            {
                var value = byHour.TryGetValue(hour, out var v) ? v : 0m;
                var barHeight = value > 0 ? Math.Max(3, (double)(value / maxVal) * maxBarHeight) : 2;

                var column = new StackPanel
                {
                    Width = barWidth,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Spacing = 4,
                };
                column.Children.Add(new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = value > 0 ? Brush.Parse("#3B82F6") : Brushes.LightGray,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    [ToolTip.TipProperty] = $"{hour:00}:00 — {value:N2} сом",
                });
                column.Children.Add(new TextBlock
                {
                    Text = $"{hour:00}",
                    FontSize = 8,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                HourlyRevenueBars.Children.Add(column);
            }
        }

        private void UpdateDailyRevenueChart(List<SaleItem> sales)
        {
            const double maxBarHeight = 100;
            const double barWidth = 28;

            DailyRevenueBars.Children.Clear();

            var byDay = sales
                .GroupBy(s => s.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(s => s.TotalAmount) })
                .OrderBy(x => x.Date)
                .ToList();

            if (byDay.Count == 0)
            {
                DailyRevenueBars.Children.Add(new TextBlock
                {
                    Text = "Нет данных за выбранный период",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 0, 8),
                });
                return;
            }

            var maxVal = byDay.Max(x => x.Total);
            if (maxVal <= 0) maxVal = 1;

            foreach (var day in byDay)
            {
                var barHeight = Math.Max(3, (double)(day.Total / maxVal) * maxBarHeight);

                var column = new StackPanel
                {
                    Width = barWidth + 8,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Spacing = 4,
                };
                column.Children.Add(new TextBlock
                {
                    Text = day.Total > 0 ? $"{day.Total:N0}" : "",
                    FontSize = 9,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                column.Children.Add(new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = Brush.Parse("#F59E0B"),
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    [ToolTip.TipProperty] = $"{day.Date:dd.MM.yyyy}: {day.Total:N2} сом",
                });
                column.Children.Add(new TextBlock
                {
                    Text = day.Date.ToString("dd.MM"),
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                DailyRevenueBars.Children.Add(column);
            }
        }

        private void UpdateHistory(List<SaleItem> sales, List<RefundItem> refunds)
        {
            _history.Clear();
            foreach (var s in sales)
            {
                _history.Add(new HistoryItem
                {
                    Id = s.Id,
                    CreatedAt = s.CreatedAt,
                    Type = "Продажа",
                    ReceiptNumber = s.ReceiptNumber,
                    TotalAmount = s.TotalAmount,
                    PaymentMethod = s.PaymentMethod ?? "—"
                });
            }
            foreach (var r in refunds)
            {
                _history.Add(new HistoryItem
                {
                    Id = r.Id,
                    CreatedAt = r.CreatedAt,
                    Type = "Возврат",
                    ReceiptNumber = r.ProductName,
                    TotalAmount = -Math.Abs(r.TotalAmount),
                    PaymentMethod = string.IsNullOrWhiteSpace(r.DeletedBy) ? "—" : r.DeletedBy
                });
            }
            // Обновляем представление, чтобы фильтр применился заново
            _historyViewSource.View.Refresh();
        }

        // Детали чека (popup)
        private async Task ShowReceiptDetailsByIdAsync(string receiptId, string receiptNumber)
        {
            if (string.IsNullOrEmpty(receiptId)) return;
            try
            {
                var json = await App.SalesApi.PosSaleGetAsync(receiptId, CancellationToken.None);
                var items = new List<string>();
                try
                {
                    foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(json))
                    {
                        items.Add($"• {CartDisplayHelper.ItemName(line)} — " +
                                  $"{CartDisplayHelper.LineQuantity(line)} × " +
                                  $"{CartDisplayHelper.UnitPrice(line):N2} = " +
                                  $"{CartDisplayHelper.LineTotal(line):N2}");
                    }
                }
                catch
                {
                    if (json.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in arr.EnumerateArray())
                        {
                            string name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                            decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                            decimal price = (decimal)CartDisplayHelper.UnitPrice(line);
                            decimal total = decimal.TryParse(
                                CartDisplayHelper.LineTotal(line), NumberStyles.Number, CultureInfo.InvariantCulture, out var lt)
                                ? lt
                                : 0;
                            items.Add($"• {name} — {qty} × {price:N2} = {total:N2}");
                        }
                    }
                }
                ShowReceiptDetailsPopup(receiptNumber ?? "—", items);
            }
            catch (Exception ex)
            {
                ErrorMessage = "Не удалось загрузить детали чека: " + ex.Message;
            }
        }

        private void ShowReceiptDetailsPopup(string receiptNumber, List<string> items)
        {
            if (ReceiptDetailsPopup == null || PopupTitle == null || PopupItemsControl == null)
            {
                // Элементы ещё не загружены — ничего не делаем
                return;
            }

            PopupTitle.Text = "Чек " + receiptNumber;
            PopupItemsControl.ItemsSource = items;
            ReceiptDetailsPopup.IsOpen = true;
        }

        private void CloseReceiptDetails_Click(object sender, RoutedEventArgs e) =>
            ReceiptDetailsPopup.IsOpen = false;

        private async void SalesGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (SalesGrid.SelectedItem is SaleItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        private void RefundsGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            // Строки этого списка — записи журнала удалений позиций (см. FetchCartItemDeletionsAsync),
            // а не сама продажа: у них нет id чека, который можно было бы открыть через PosSaleGetAsync.
        }

        private async void HistoryGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is HistoryItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        // Быстрые даты
        private async void QuickDate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            if (tag == "Custom")
            {
                var dlg = new FinanceDateRangeDialog();
                if (await dlg.ShowDialog<bool>(this) == true)
                {
                    _historyFrom = dlg.FromDate;
                    _historyTo = dlg.ToDate;
                    FromPicker.SelectedDate = new DateTimeOffset(dlg.FromDate);
                    ToPicker.SelectedDate = new DateTimeOffset(dlg.ToDate);
                    await LoadDataAsync(_historyFrom, _historyTo);
                }
                CustomDatePill.IsChecked = false;
                return;
            }

            DateTime from, to;
            DateTime today = DateTime.Today;
            switch (tag)
            {
                case "Yesterday": from = to = today.AddDays(-1); break;
                // Неделя начинается с понедельника (СНГ), а не с воскресенья, как в (int)DayOfWeek по умолчанию —
                // иначе по воскресеньям диапазон схлопывался в один сегодняшний день без вчерашних продаж.
                case "Week": from = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); to = today; break;
                case "Month": from = new DateTime(today.Year, today.Month, 1); to = today; break;
                default: from = to = today; break;
            }

            _historyFrom = from;
            _historyTo = to;
            FromPicker.SelectedDate = new DateTimeOffset(from);
            ToPicker.SelectedDate = new DateTimeOffset(to);
            await LoadDataAsync(from, to);
            CustomDatePill.IsChecked = false;
        }

        private async void DatePicker_DateChanged(object sender, EventArgs e)
        {
            if (sender == FromPicker)
                _historyFrom = FromPicker.SelectedDate?.DateTime ?? DateTime.Today;
            else if (sender == ToPicker)
                _historyTo = ToPicker.SelectedDate?.DateTime ?? DateTime.Today;
            await LoadDataAsync(_historyFrom, _historyTo);
        }

        // Работа с кассовой историей
        private static List<CashSessionEntry> LoadCashHistoryFromDisk()
        {
            try
            {
                if (File.Exists(CashHistoryFilePath))
                    return JsonSerializer.Deserialize<List<CashSessionEntry>>(File.ReadAllText(CashHistoryFilePath))
                           ?? new List<CashSessionEntry>();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cash history load failed: {ex.GetType().Name}", "WARNING");
            }
            return new List<CashSessionEntry>();
        }

        private static void SaveCashHistoryToDisk(IEnumerable<CashSessionEntry> entries)
        {
            try
            {
                var dir = Path.GetDirectoryName(CashHistoryFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(CashHistoryFilePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cash history save failed: {ex.GetType().Name}", "WARNING");
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) =>
            await LoadDataAsync(_historyFrom, _historyTo, forceRefresh: true);

        // INotifyPropertyChanged
        public new event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Вложенные классы
        public class SaleItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
            public bool IsRefund { get; set; }
            public string RefundReason { get; set; }
            public string CustomerId { get; set; }
        }

        public class RefundItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            /// <summary>2026-09-17: "—" вместо обманчивого "00:00", когда сервер не прислал
            /// реальное время суток (см. TryDeletionTimestamp) — ровно полночь для события
            /// удаления позиции из корзины на практике не бывает, так что это безопасный признак
            /// "время неизвестно", а не настоящая полночь.</summary>
            public string TimeDisplay => CreatedAt.TimeOfDay == TimeSpan.Zero ? "—" : CreatedAt.ToString("HH:mm");
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string Reason { get; set; } = "";
            public string ProductName { get; set; } = "";
            public string QuantityDisplay { get; set; } = "";
            public string DeletedBy { get; set; } = "";
        }

        public class HistoryItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string Type { get; set; } = "";
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
        }

        public class CashSessionEntry
        {
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public decimal Amount { get; set; }
            public string UserId { get; set; }
            public string Type { get; set; }
            public string Comment { get; set; }
        }
    }
}
