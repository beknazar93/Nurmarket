using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Models;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Views.Dialogs; using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
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
    public partial class FinanceWindow : Window, INotifyPropertyChanged, IOwnerSection
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
        // 2026-11-06 (В11, Л4-06 хвост): «Сегодня» в отчётах — по серверным часам (ServerClock.Today), а не по часам ПК.
        private DateTime _displayFrom = ServerClock.Today;
        private DateTime _displayTo = ServerClock.Today;
        private int _summaryPeriod; // 0=день, 1=неделя, 2=месяц — см. UpdateSummaryPanel/RefreshSummaryPanelAsync
        private CancellationTokenSource? _summaryCts;
        private bool _shiftsShowOpen = true;
        private ObservableCollection<CashSessionEntry> _cashSessions;
        private readonly CollectionViewSource _salesViewSource = new();
        private readonly CollectionViewSource _historyViewSource = new();
        private DateTime _historyFrom = ServerClock.Today;
        private DateTime _historyTo = ServerClock.Today;
        private bool _isLoading;
        private string _errorMessage; 
        private CancellationTokenSource _loadCts;
        private DispatcherTimer _clockTimer;
        private string _currentUserId;
        private bool _isHamburgerOpen;

        private static readonly string CashHistoryFilePath = Path.Combine(
            NurMarketKassa.Services.AppPaths.Roaming,
            NurMarketKassa.Services.AppMode.DataFolderName,
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
            set
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
                // Любое сообщение по умолчанию — ошибка; «Отчёт сохранён» перекрашивает ShowSaved.
                AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: false);
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public FinanceWindow()
        {
            InitializeComponent();
            AddEmptyHints(); // 2026-10-18, доводка О4: пустые «Возвраты» и «История» — с подсказкой (FinanceWindow.Merged.cs)
            // 2026-10-19, этап О10: под плитками прибыли — как считается (те же слова, что на Сводке и в «Прибыли и деньгах»).
            // 2026-10-20: аудит владельца — расходы теперь из NurCRM (как в «Прибыли и деньгах»), а не изъятия из кассы.
            ProfitHowText.Text = ProfitTerms.GrossHow + ". " + ProfitTerms.NetHow + Tr.T(" (расходы — из NurCRM, как в «Прибыли и деньгах»).",
                " (чыгымдар — NurCRMден, «Пайда жана акча» бөлүмүндөгүдөй).", " (expenses come from NurCRM, as in “Profit & cash”).",
                " (giderler NurCRM'den, «Kâr ve para» bölümündeki gibi).", " (xarajatlar — NurCRMdan, «Foyda va pul» bo'limidagidek).");
            // Esc: всплывающий чек → меню → подраздел (Смены/Товары/заглушка) → выход из окна.
            EscapeKey.Attach(this, () =>
            {
                // 2026-10-18: «Продажи и финансы» — сначала открытый чек вкладки «Чеки» (FinanceWindow.Merged.cs).
                if (_mergedSales != null && _mergedSales.CloseReceiptPopup())
                    return true;
                if (ReceiptDetailsPopup.IsOpen)
                    ReceiptDetailsPopup.IsOpen = false;
                else if (_isHamburgerOpen)
                    CloseHamburgerMenu();
                else if (ShiftsPanel.IsVisible || ProductsAnalyticsPanel.IsVisible || UnderConstructionPanel.IsVisible)
                {
                    ShiftsPanel.IsVisible = false;
                    ProductsAnalyticsPanel.IsVisible = false;
                    UnderConstructionPanel.IsVisible = false;
                    MainContent.IsVisible = true;
                }
                else
                    return false;
                return true;
            }, () => Exit_Click(this, new RoutedEventArgs()));
            _currentUserId = App.CurrentUserId;

            _salesViewSource.Source = _sales;
            _salesViewSource.Filter += FilterSales;
            _historyViewSource.Source = _history;
            _historyViewSource.Filter += FilterHistory;

            DataContext = this;

            // 2026-10-24 (приёмка, ПР-5): плитки выручки и чеков — «(без долга)», под «Возвраты» — подпись; тексты те же, что в SalesWindow.
            // Суммы плиток не менялись.
            FinRevenueLabel.Text = Tr.T("Выручка (без долга)", "Түшүм (карызсыз)", "Revenue (excl. credit)", "Ciro (veresiyesiz)", "Tushum (qarzsiz)");
            FinReceiptsLabel.Text = Tr.T("Чеки (без долга)", "Чектер (карызсыз)", "Receipts (excl. credit)", "Fişler (veresiyesiz)", "Cheklar (qarzsiz)");
            FinRefundsNote.Text = Tr.T("возвращённые чеки уже не в выручке", "кайтарылган чектер киреше эсебинде жок", "returned receipts are already not in revenue",
                "iade fişleri artık gelirde değil", "qaytarilgan cheklar tushumda yo'q");

            FullscreenHelper.Apply(this);

            var cashHistory = LoadCashHistoryFromDisk();
            _cashSessions = new ObservableCollection<CashSessionEntry>(cashHistory);
            OnPropertyChanged(nameof(CashSessions));

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            // 2026-10-22 (оптимизация): раздел спрятан (программа владельца прячет, а не закрывает) — часы не обновляем: секундный тик
            // пересчитывал разметку невидимого окна всё время, пока оно стоит в памяти.
            _clockTimer.Tick += (_, _) => { if (IsVisible) CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss   dd.MM.yyyy"); };
            // 2026-10-18, доводка О4: время сразу, а не через секунду — пустой рамки часов не бывает.
            CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss   dd.MM.yyyy");
            // На телефоне шапка узкая, время и так видно в строке состояния — часы не показываем.
            if (OperatingSystem.IsAndroid())
                ClockBadge.IsVisible = false;
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

            // «Открыть смену» у закрытых карточек убрана (2026-09-24, «почему закрытые смены
            // можно открыть?»): кнопка открывала НОВУЮ смену той кассы, но стояла на каждой
            // старой смене и читалась как «открыть закрытую». Новую смену открывают в кассе.
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
            public string SummaryText => Tr.T($"{DayName} — лучше всего продаётся «{ProductName}» ({Quantity} шт., {Revenue:N0} сом)",
                $"{DayName} — эң көп сатылганы «{ProductName}» ({Quantity} даана, {Revenue:N0} сом)",
                $"{DayName} — best seller: “{ProductName}” ({Quantity} pcs, {Revenue:N0} som)",
                $"{DayName} — en çok satan: «{ProductName}» ({Quantity} adet, {Revenue:N0} som)",
                $"{DayName} — eng ko'p sotiladigani: «{ProductName}» ({Quantity} dona, {Revenue:N0} so'm)");
        }

        private sealed record SaleLineFact(DateTime SaleDate, string ProductName, decimal Revenue, int Quantity, decimal Cost);

        protected override void OnClosed(EventArgs e)
        {
            PosDataEvents.SalesChanged -= OnSalesChangedExternally;
            _liveCts?.Cancel();
            _liveCts?.Dispose();
            _liveCts = null;
            _clockTimer?.Stop();
            CancelLoad();

            // CancelLoad отменяет только _loadCts. Сводка («День/Неделя/Месяц») живёт на своём
            // токене и после закрытия окна продолжала запрашивать до 200 чеков, а потом писала
            // результат в контролы уничтоженного окна.
            _summaryCts?.Cancel();
            _summaryCts?.Dispose();
            _summaryCts = null;
            _allProductsPause?.Dispose();
            _abcCts?.Cancel();
            _seasonalityCts?.Cancel();
            _insightsCts?.Cancel();
            // 2026-10-18: «Продажи и финансы» — встроенная часть «Продаж» останавливает свои загрузки.
            _mergedSales?.ReleaseFromFinance();
            _mergedSales = null;
            base.OnClosed(e);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PosDataEvents.SalesChanged += OnSalesChangedExternally;
            // 2026-11-05 (аудит №4, Л4-12): спрятанный раздел продажи не перекачивал — раздел показали, а данные устарели → один пересчёт.
            Activated += (_, _) =>
            {
                if (IsVisible && _staleWhileHidden)
                {
                    _staleWhileHidden = false;
                    OnSalesChangedExternally();
                }
            };

            SyncDatePickers(_historyFrom, _historyTo);
            CustomDatePill.IsChecked = false;
            await LoadDataAsync(_historyFrom, _historyTo);
        }
        

        private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e)
        {
            e.Handled = true;
        }

        // ── раздел программы владельца ──

        private bool _isOwnerSection;
        private TabItem _ownerShiftsTab;
        private TabItem _ownerProductsTab;

        /// <summary>«Финансы» разделом программы владельца (см. <see cref="IOwnerSection"/>;
        /// 2026-09-27, владелец: «в финансах есть бургер меню, 2 бургер меню — это же не логично»).
        ///
        /// В кассе у окна своё меню ☰ — «Продажа / Склад / Смена / Товары / Выход»: это переходы
        /// кассира, окно открыто поверх продажи. В программе владельца меню уже есть слева, и там эти
        /// пункты либо повторяются («Продажи», «Склад»), либо не имеют смысла («Продажа» — экран
        /// кассира, «Выход» — разделу нечего закрывать). Своё содержание есть только у двух: «Смена» —
        /// смены всех касс (открытые и закрытые, отчёт смены, закрыть забытую смену) и «Товары» — что
        /// продаётся и что лежит без движения за выбранный период. Они становятся вкладками рядом с
        /// «Продажами» и «Аналитикой», второго меню нет.
        ///
        /// Шапка — одна строка: слева период (раньше он стоял под плитками, хотя плитки считаются
        /// именно по нему), справа Excel, Word и «Обновить». Карточки «сводка простым языком» со своим
        /// переключателем День/Неделя/Месяц здесь нет: она повторяет «Сводку» программы владельца и
        /// давала на одной странице два разных выбора периода (а ради неё ещё и качались строки до
        /// 200 чеков). Часов тоже нет — они для кассира за прилавком. В кассе окно прежнее.
        ///
        /// 2026-09-27, «все аналитики — в отдельную аналитику, в финансах ABC финансы»: вкладки
        /// «Аналитика» и «Товары» ушли в раздел «Аналитика» (это то же окно, см.
        /// <see cref="AsAnalyticsSection"/>), а «ABC-анализ» стал вкладкой «ABC» только с денежными
        /// срезами — по выручке и по прибыли. Остальные срезы — в «Продажах» и на «Складе».</summary>
        public void AsOwnerSection()
        {
            _isOwnerSection = true;

            // Выезжающая панель кассира стоит за левым краем окна; спрятанная целиком, она не
            // ловит и переход по Tab — её «Продажа» и «Выход» не нажмутся случайно с клавиатуры.
            HamburgerButton.IsVisible = false;
            HamburgerPanel.IsVisible = false;
            HeaderTitleText.IsVisible = false;
            ExitButton.IsVisible = false;
            RefreshButton.Margin = new Thickness(0);
            // 2026-11-06 (В11, Д4-30): абзац «как считается прибыль» под плитками — кнопкой «?» в шапке раздела (текст тот же, читается при открытии окошка).
            ProfitHowText.IsVisible = false;
            var hint = NurMarketKassa.AvaloniaHost.Views.Controls.HintButton.Create(() => ProfitHowText.Text);
            hint.Margin = new Thickness(0); // 2026-11-12 (Д-3): расстояние между кнопками шапки — Spacing у HeaderActions
            HeaderActions.Children.Insert(0, hint);
            DailySummaryCard.IsVisible = false;
            ClockBadge.IsVisible = false;
            _clockTimer?.Stop();

            // Период — в строку шапки, на место кнопки меню и названия; выгрузка и «Обновить» — справа.
            // Колонки шапки были тремя равными долями: период в средней трети не поместился бы.
            HeaderRow.ColumnDefinitions[0].Width = GridLength.Auto;
            HeaderRow.ColumnDefinitions[2].Width = GridLength.Auto;
            ContentGrid.Children.Remove(PeriodBar);
            PeriodBar.Margin = new Thickness(0, 0, 12, 0);
            PeriodBar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(PeriodBar, 0);
            Grid.SetColumn(PeriodBar, 1);
            HeaderRow.Children.Add(PeriodBar);
            HeaderActions.VerticalAlignment = VerticalAlignment.Center;
            // 2026-10-04, Android (телефон): период и «Excel / Word / Обновить» в одной строке налезали друг на
            // друга — период отдельной строкой под кнопками, на всю ширину, с переносом кнопок.
            if (OperatingSystem.IsAndroid())
            {
                HeaderRow.RowDefinitions = new RowDefinitions("Auto,Auto");
                Grid.SetRow(PeriodBar, 1);
                Grid.SetColumn(PeriodBar, 0);
                Grid.SetColumnSpan(PeriodBar, 3);
                PeriodBar.Margin = new Thickness(0, 8, 0, 0);
                HeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
            }
            HeaderBlock.Margin = new Thickness(0, 0, 0, 14);
            FinanceTabs.Margin = new Thickness(0);
            ContentGrid.Margin = OwnerSectionLayout.Margin;

            if (_isAnalyticsSection)
            {
                SetUpAnalyticsTabs();
            }
            else
            {
                FinanceTabs.Items.Remove(AnalyticsTab);
                AbcTab.Header = "ABC";
                AbcSection.SliceKeys = new[] { AnalyticsReportData.KeyRevenue, AnalyticsReportData.KeyProfit };
                AbcSection.ShowSeasonality = false;
                AbcSection.TableMaxHeight = 520;
                AbcSection.ProductAnalyticsRequested += ShowProductAnalytics;
                _ownerShiftsTab = MoveIntoOwnerTab(ShiftsPanel, ShiftsContent, ShiftsHeader,
                    Tr.T("Смены", "Сменалар", "Shifts", "Vardiyalar", "Smenalar"));
                // 2026-10-18: «Продажи и финансы» — срезы ABC обоих разделов, ABC последней вкладкой (FinanceWindow.Merged.cs).
                if (_mergedSales != null)
                    ApplySalesAndFinanceOwnerTabs();
            }
            FinanceTabs.SelectionChanged += OwnerTabs_SelectionChanged;
        }

        // ── раздел «Аналитика» программы владельца ──

        private bool _isAnalyticsSection;
        private TabItem _ownerSeasonalityTab;
        private TabItem _ownerStockTab;
        private SeasonalityView _seasonalityView;
        private bool _seasonalityLoaded;
        private CancellationTokenSource _seasonalityCts;
        private Border _stockTabBody;
        private WarehouseStockAnalyticsView _stockAnalyticsView;
        // 2026-10-22 (аналитика по категориям): вкладка «По категориям» — выбор категории, маржа, прибыль, расходы, движение товара.
        private TabItem _ownerCategoriesTab;
        private CategoryAnalyticsView _categoryView;

        /// <summary>Это окно — раздел «Аналитика» программы владельца (2026-09-27). Вызывается до
        /// показа, до <see cref="AsOwnerSection"/>.
        ///
        /// Почему то же окно, а не новое: «Аналитика» финансов — это выручка, прибыль, оплаты,
        /// графики по дням и часам, рекомендации по дням недели и топ товаров, и всё это считается
        /// из продаж, которые окно «Финансов» уже умеет грузить с сервера (с запасным путём без
        /// интернета, живым обновлением и выгрузкой). Переписать это второй раз — два расчёта одних
        /// и тех же цифр, которые рано или поздно разойдутся; а касса, где «Финансы» тоже работают,
        /// не заметит ничего: без этого вызова окно прежнее.
        ///
        /// В разделе остаются плитки и выбор периода, вкладки — «Выручка и оплаты» (бывшая
        /// «Аналитика»), «Товары», «Сезонность» (раньше пряталась во вкладках ABC) и «Склад»
        /// (аналитика склада — платная, без покупки вместо неё объяснение и ссылка в Маркетплейс).
        /// Показатели «Продаж» (чистая прибыль, маржа, топ-10) — те же, что во «Выручке и оплатах»,
        /// отдельной вкладкой их нет.</summary>
        public FinanceWindow AsAnalyticsSection()
        {
            _isAnalyticsSection = true;
            Title = Tr.T("Аналитика", "Талдоо", "Analytics", "Analiz", "Analitika");
            return this;
        }

        private void SetUpAnalyticsTabs()
        {
            foreach (var tab in new[] { SalesTab, AbcTab, ReturnsTab, HistoryTab })
                FinanceTabs.Items.Remove(tab);

            AnalyticsTab.Header = Tr.T("Выручка и оплаты", "Түшүм жана төлөмдөр", "Revenue & payments", "Ciro ve ödemeler", "Tushum va to'lovlar");
            _ownerProductsTab = MoveIntoOwnerTab(ProductsAnalyticsPanel, ProductsContent, ProductsHeader,
                Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"));

            // 2026-10-22 (аналитика по категориям): «в аналитике должен быть селект по категориям» — вкладка после «Товаров».
            _categoryView = new CategoryAnalyticsView();
            _ownerCategoriesTab = new TabItem
            {
                Header = Tr.T("По категориям", "Категориялар боюнча", "By category", "Kategoriye göre", "Kategoriyalar bo'yicha"),
                Content = new Border { Padding = new Thickness(8), Child = _categoryView },
            };
            FinanceTabs.Items.Add(_ownerCategoriesTab);

            _seasonalityView = new SeasonalityView();
            _ownerSeasonalityTab = new TabItem
            {
                Header = Tr.T("Сезонность", "Мезгилдүүлүк", "Seasonality", "Mevsimsellik", "Mavsumiylik"),
                Content = new Border { Padding = new Thickness(8), Child = _seasonalityView },
            };
            FinanceTabs.Items.Add(_ownerSeasonalityTab);

            _stockTabBody = new Border { Padding = new Thickness(8) };
            _ownerStockTab = new TabItem
            {
                Header = Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
                Content = _stockTabBody,
            };
            FinanceTabs.Items.Add(_ownerStockTab);

            // 2026-10-05, владелец: «где в сводке и аналитике долги??» — вкладка «Долги» сразу после «Выручки и оплат».
            // 2026-10-19, этап О10 (аудит: «вкладка «Долги» — полная копия раздела «Долги клиентов»»): вместо копии — кнопка
            // «Долги клиентов» под шапкой (открывает тот раздел); плитки «Продаж» и круговая оплат здесь не повторяются.
            ApplyAnalyticsLayoutO10();

            FinanceTabs.SelectedItem = AnalyticsTab;

            // Доп. функцию могли купить в Маркетплейсе и вернуться сюда, не переключая вкладку.
            Activated += (_, _) =>
            {
                if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerStockTab))
                    RefreshStockTab();
            };
        }

        /// <summary>Вкладка «Склад» раздела «Аналитика» — для кнопки «Открыть аналитику склада» в
        /// Маркетплейсе программы владельца.</summary>
        public void ShowStockTab()
        {
            if (_ownerStockTab == null)
                return;
            if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerStockTab))
                RefreshStockTab();
            else
                FinanceTabs.SelectedItem = _ownerStockTab;
        }

        /// <summary>Сезонность — по всей истории продаж, от периода страницы не зависит. Считается
        /// в фоне при первом открытии вкладки и заново — после «Обновить» и новых продаж.</summary>
        private async Task LoadSeasonalityAsync()
        {
            if (_seasonalityView == null || _seasonalityLoaded)
                return;

            _seasonalityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _seasonalityCts = cts;
            try
            {
                var report = await Task.Run(() => AnalyticsReportData.BuildSeasonality(), cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested)
                    return;
                _seasonalityView.Show(report);
                _seasonalityLoaded = true;
                // 2026-10-20, аудит владельца: столбики по месяцам — итоги сервера (как «Сводка» за месяц), не история кассы.
                var today = ServerClock.Today;
                var months = await FetchServerMonthTotalsAsync(today, cts.Token).ConfigureAwait(true);
                if (!cts.IsCancellationRequested && months != null)
                    _seasonalityView.ShowServerMonths(months, today, report.FirstSale);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Сезонность не построена: {ex.Message}", "WARNING");
            }
            finally
            {
                if (ReferenceEquals(_seasonalityCts, cts))
                    _seasonalityCts = null;
                cts.Dispose();
            }
        }

        /// <summary>Вкладка «Склад»: аналитика склада — платная доп. функция
        /// (UserPreferences.WarehouseAnalyticsUnlocked), как и во вкладке склада кассы. Пока она не
        /// куплена, вместо цифр — что это и где подключить.</summary>
        private void RefreshStockTab()
        {
            if (_stockTabBody == null)
                return;

            if (!UserPreferences.Instance.WarehouseAnalyticsUnlocked)
            {
                _stockAnalyticsView = null;
                if (_stockTabBody.Child is not StackPanel)
                    _stockTabBody.Child = BuildStockLockedPanel();
                return;
            }

            _stockAnalyticsView ??= new WarehouseStockAnalyticsView();
            if (!ReferenceEquals(_stockTabBody.Child, _stockAnalyticsView))
                _stockTabBody.Child = _stockAnalyticsView;
            _stockAnalyticsView.Refresh();
        }

        private Control BuildStockLockedPanel()
        {
            var panel = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 480,
                Margin = new Thickness(0, 48, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var title = new TextBlock
            {
                Text = "🔒 " + Tr.T("Аналитика склада — платная доп. функция", "Кампанын аналитикасы — акы төлөнүүчү кошумча функция", "Warehouse analytics is a paid add-on", "Depo analitiği ücretli bir ek özelliktir", "Ombor analitikasi — pullik qo'shimcha funksiya"),
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            };
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
            panel.Children.Add(title);

            var text = new TextBlock
            {
                Text = Tr.T(
                    "Показатели остатков, самые дорогие остатки и остатки по категориям. Подключить её можно в разделе «Маркетплейс → Доп. функции».",
                    "Калдыктардын көрсөткүчтөрү, эң кымбат калдыктар жана категориялар боюнча калдыктар. Аны «Маркетплейс → Кошумча функциялар» бөлүмүндө кошсо болот.",
                    "Stock indicators, the most valuable stock and stock by category. You can enable it under “Marketplace → Extras”.",
                    "Stok göstergeleri, en değerli stoklar ve kategorilere göre stok. Bu özelliği «Pazar yeri → Ek özellikler» bölümünden etkinleştirebilirsiniz.",
                    "Qoldiq ko'rsatkichlari, eng qimmat qoldiqlar va kategoriyalar bo'yicha qoldiqlar. Uni «Marketpleys → Qo'shimcha funksiyalar» bo'limida ulash mumkin."),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(text);

            var open = new Button
            {
                Content = Tr.T("Открыть Маркетплейс", "Маркетплейсти ачуу", "Open Marketplace", "Pazar yerini aç", "Marketpleysni ochish"),
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 200,
            };
            // 2026-10-19, этап О10: обводкой — главная (жёлтая) кнопка раздела одна, «Обновить» в шапке.
            open.Classes.Add("sec-outline");
            open.Click += (_, _) => OpenMarketplaceExtras();
            panel.Children.Add(open);
            return panel;
        }

        /// <summary>Маркетплейс → «Доп. функции». В программе владельца — её разделом (меню слева
        /// подсветит «Маркетплейс»), иначе — отдельным окном.</summary>
        private void OpenMarketplaceExtras()
        {
            try
            {
                if (Owner is OwnerShellWindow shell)
                    shell.OpenMarketplaceExtras();
                else
                    MarketplaceWindow.Open(this).ShowExtrasTab();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Маркетплейс не открылся: {ex.Message}", "WARNING");
            }
        }

        /// <summary>Разбор товара по нажатию на столбец или строку ABC — за период страницы.</summary>
        private void ShowProductAnalytics(string productName)
        {
            try
            {
                new ProductAnalyticsWindow(productName, _historyFrom, _historyTo).Show(this);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Разбор товара не открылся: {ex}", "WARNING");
            }
        }

        /// <summary>Содержимое подраздела (в кассе — панель поверх окна со стрелкой «←») переезжает во
        /// вкладку; стрелка и заголовок подраздела не нужны — название стоит на ярлыке вкладки.</summary>
        private TabItem MoveIntoOwnerTab(Panel overlay, Control content, Control header, string title)
        {
            overlay.Children.Remove(content);
            header.IsVisible = false;
            content.Margin = new Thickness(8);
            var tab = new TabItem { Header = title, Content = content };
            FinanceTabs.Items.Add(tab);
            return tab;
        }

        /// <summary>Смены грузятся при каждом открытии вкладки, как в кассе при каждом входе в
        /// «Смену»: за это время на другой кассе могли открыть или закрыть смену.</summary>
        private async void OwnerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Выбор строк во вложенных списках тоже всплывает сюда — нужен только выбор вкладки.
            if (!ReferenceEquals(e.Source, FinanceTabs))
                return;
            // Вкладки есть не в каждом режиме (в «Аналитике» нет «Смен», в «Финансах» — «Товаров»),
            // а пустой выбор при перестройке вкладок совпал бы с отсутствующей.
            var selected = FinanceTabs.SelectedItem;
            if (selected == null)
                return;
            // 2026-10-18, этап О4 (аудит: «ABC зажат в окошке ~270 px»): на вкладке ABC плитки скрыты — ей вся высота раздела;
            // период остаётся в шапке. В «Аналитике» плитки — как были.
            if (!_isAnalyticsSection && TileGridOf(TotalSalesText) is { } tiles)
                tiles.IsVisible = !ReferenceEquals(selected, AbcTab);
            if (ReferenceEquals(selected, _ownerShiftsTab))
                await LoadShiftsAsync().ConfigureAwait(true);
            else if (ReferenceEquals(selected, _ownerProductsTab))
                RefreshProductsAnalyticsPanel();
            else if (ReferenceEquals(selected, _ownerCategoriesTab))
                await _categoryView.LoadAsync(_historyFrom, _historyTo).ConfigureAwait(true);
            else if (ReferenceEquals(selected, _ownerSeasonalityTab))
                await LoadSeasonalityAsync().ConfigureAwait(true);
            else if (ReferenceEquals(selected, _ownerStockTab))
                RefreshStockTab();
        }

        /// <summary>Во вкладке «Товары» период можно сменить, не уходя с неё (в кассе подраздел
        /// закрывал страницу целиком), — после загрузки пересчитываем её.</summary>
        private void RefreshOwnerProductsTabIfShown()
        {
            if (!_isOwnerSection || FinanceTabs.SelectedItem == null)
                return;
            if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerProductsTab))
                RefreshProductsAnalyticsPanel();
            else if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerStockTab))
                RefreshStockTab();
            else if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerCategoriesTab))
                _ = _categoryView.LoadAsync(_historyFrom, _historyTo);
            else if (ReferenceEquals(FinanceTabs.SelectedItem, _ownerSeasonalityTab))
                _ = LoadSeasonalityAsync();
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
            // 2026-10-18, этап О3: сегменты sec-seg — выбранный классом active (был свой TabButton.Active).
            ShiftsOpenTabButton.Classes.Add("active");
            ShiftsClosedTabButton.Classes.Remove("active");
            ApplyShiftsFilter();
        }

        private void ShiftsClosedTab_Click(object sender, RoutedEventArgs e)
        {
            _shiftsShowOpen = false;
            ShiftsClosedTabButton.Classes.Add("active");
            ShiftsOpenTabButton.Classes.Remove("active");
            ApplyShiftsFilter();
        }

        private void ApplyShiftsFilter()
        {
            // 2026-10-20, аудит программы владельца: «Смены» не следовали периоду (закрытых 256 за всё время при «Неделе»).
            // У владельца закрытые смены — открытые в выбранном периоде; открытые — все (они идут сейчас). Касса — как было.
            var inPeriod = _isOwnerSection
                ? _allShifts.Where(s => s.IsOpen || s.OpenedAtRaw == DateTime.MinValue
                                        || (s.OpenedAtRaw.Date >= _displayFrom.Date && s.OpenedAtRaw.Date <= _displayTo.Date)).ToList()
                : _allShifts;
            if (_isOwnerSection && _shiftsLoaded)
            {
                ShiftsOpenCountText.Text = inPeriod.Count(s => s.IsOpen).ToString();
                ShiftsClosedCountText.Text = inPeriod.Count(s => !s.IsOpen).ToString();
            }
            var visible = inPeriod.Where(s => s.IsOpen == _shiftsShowOpen).ToList();
            ShiftsList.ItemsSource = visible;
            ShiftsEmptyText.IsVisible = visible.Count == 0;
            // 2026-10-20: пустой список был белым листом без надписи (пока грузится — тоже) — пишем, что именно пусто.
            if (_isOwnerSection)
                ShiftsEmptyText.Text = !_shiftsLoaded
                    ? Tr.T("Загружаю смены…", "Сменалар жүктөлүүдө…", "Loading shifts…", "Vardiyalar yükleniyor…", "Smenalar yuklanmoqda…")
                    : _shiftsShowOpen
                        ? Tr.T("Открытых смен нет.", "Ачык сменалар жок.", "No open shifts.", "Açık vardiya yok.", "Ochiq smenalar yo'q.")
                        : Tr.T("Закрытых смен за этот период нет.", "Бул мезгилде жабык сменалар жок.", "No closed shifts in this period.",
                            "Bu dönemde kapalı vardiya yok.", "Bu davrda yopiq smenalar yo'q.");
        }

        /// <summary>2026-10-20: список смен уже пришёл с сервера (до этого надпись «Загружаю смены…»).</summary>
        private bool _shiftsLoaded;

        /// <summary>GET /api/construction/shifts/ (CashShiftList) — тот же источник, что уже
        /// использует ShiftStateService для определения текущей открытой смены на кассе.</summary>
        private async Task LoadShiftsAsync()
        {
            if (!_shiftsLoaded)
                ApplyShiftsFilter(); // 2026-10-20: «Загружаю смены…» вместо белого листа
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

                _shiftsLoaded = true;
                ApplyShiftsFilter();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Shifts list load failed: {ex}", "WARNING");
                _allShifts = new List<ShiftCardVm>();
                _shiftsLoaded = true;
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
                Title = $"{Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena")} #{shortId}",
                IsOpen = isOpen,
                StatusText = isOpen
                    ? Tr.T("Открыта", "Ачык", "Open", "Açık", "Ochiq")
                    : Tr.T("Закрыта", "Жабык", "Closed", "Kapalı", "Yopiq"),
                OpenedDisplay = openedAt == DateTime.MinValue
                    ? "—"
                    : $"{Tr.T("Открыта", "Ачылды", "Opened", "Açıldı", "Ochildi")} {openedAt:d MMMM} {Tr.T("в", "саат", "at", "saat", "soat")} {openedAt:HH:mm}",
                RevenueText = $"{salesTotal:N2} {Som}",
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

        /// <summary>Нажатие на смену — её отчёт: то же окно «Детали смены», что в «Истории смен»
        /// и после закрытия смены (итоги по оплатам, возвраты, расходы, печать Z-отчёта), плюс
        /// товары, проданные за смену. Долг уточняется тем же расчётом, что при закрытии.</summary>
        private async void ShiftCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Кнопка «Закрыть смену» внутри карточки сама обрабатывает нажатие.
            if (e.Handled || e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left)
                return;
            if (sender is not Control { Tag: ShiftCardVm row } || string.IsNullOrEmpty(row.ShiftId))
                return;

            // 2026-09-28, регресс 1.17.19: отчёт смены открывался ~8 с — перед показом
            // скачивался весь список смен ради одной. Теперь — запрос этой смены (0,2–0,5 с);
            // полный список — только если одиночный запрос не удался.
            var entry = await ShiftHistoryService.LoadOneAsync(row.ShiftId).ConfigureAwait(true)
                ?? (await ShiftHistoryService.LoadAsync().ConfigureAwait(true))
                    .FirstOrDefault(s => string.Equals(s.ShiftNumber, row.ShiftId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                PosMessageBox.Show(this, Tr.T("Не удалось загрузить отчёт смены — нет связи с сервером.", "Сменанын отчётун жүктөө мүмкүн болгон жок — сервер менен байланыш жок.", "Could not load the shift report — no connection to the server.", "Vardiya raporu yüklenemedi — sunucuyla bağlantı yok.", "Smena hisobotini yuklab bo'lmadi — server bilan aloqa yo'q."), Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var shift = ShiftModel.FromEntry(entry);
            var dialog = new ShiftDetailsDialog(shift);
            var dialogTask = PosDialogHost.ShowAsync(dialog, this);
            try
            {
                var resolvedDebt = await App.GetRequiredService<NurMarketKassa.Core.Contracts.ICashShiftService>()
                    .ResolveShiftDebtTotalAsync(shift.Id, shift.SalesCount)
                    .ConfigureAwait(true);
                if (resolvedDebt is { } debt && dialog.IsVisible)
                {
                    shift.DebtSales = debt;
                    dialog.RefreshDebtDisplay(debt);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Finance: resolve shift debt failed for {shift.Id}: {ex.GetType().Name}", "SHIFT");
            }

            await dialogTask.ConfigureAwait(true);
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
                // 2026-11-01 (В2-05, контрольный аудит): при сбое сервера исход неизвестен (смена могла закрыться) — говорим об этом и перечитываем список.
                var unknown = ServerOutageMonitor.IsServerFailure(ex);
                PosMessageBox.Show(this, unknown
                    ? Tr.T("Не удалось подтвердить закрытие смены. ", "Сменанын жабылганы тастыкталган жок. ", "Could not confirm the shift was closed. ", "Vardiyanın kapandığı doğrulanamadı. ", "Smena yopilgani tasdiqlanmadi. ")
                      + ServerOutageMonitor.FriendlyError(ex) + " "
                      + Tr.T("Проверьте список смен — смена могла закрыться.", "Сменалардын тизмесин текшериңиз — смена жабылып калышы мүмкүн.", "Check the shift list — the shift may have been closed.", "Vardiya listesini kontrol edin — vardiya kapanmış olabilir.", "Smenalar ro'yxatini tekshiring — smena yopilgan bo'lishi mumkin.")
                    : Tr.T("Не удалось закрыть смену: ", "Сменаны жабуу мүмкүн болгон жок: ", "Could not close the shift: ", "Vardiya kapatılamadı: ", "Smenani yopib bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex), Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                if (unknown)
                    await LoadShiftsAsync().ConfigureAwait(true);
            }
        }
    }
}
