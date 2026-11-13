using NurMarketKassa.Core.Application;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using Avalonia.Threading;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    public partial class SalesWindow : Window, INotifyPropertyChanged, IOwnerSection
    {
        private readonly ObservableCollection<SaleItem> _sales = new();
        private readonly ObservableCollection<RefundItem> _refunds = new();
        private readonly CollectionViewSource _salesViewSource = new();
        // 2026-11-06 (В11, Л4-06 хвост): «Сегодня» в отчётах — по серверным часам (ServerClock.Today), а не по часам ПК.
        private DateTime _historyFrom = ServerClock.Today;
        private DateTime _historyTo = ServerClock.Today;
        private string _searchFilter = "";
        private bool _isLoading;
        private string _errorMessage;
        private CancellationTokenSource _searchCts;
        private CancellationTokenSource _loadCts;
        private JsonElement _currentReceiptJson;
        private string _currentReceiptNumber = "";
        private DispatcherTimer _clockTimer;

        public ObservableCollection<TopItem> TopItems { get; } = new();
        public ICollectionView SalesView => _salesViewSource.View;

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

        public SalesWindow()
        {
            InitializeComponent();
            // Esc: сначала всплывающий чек, потом само окно.
            EscapeKey.Attach(this, () =>
            {
                if (!ReceiptDetailsPopup.IsOpen)
                    return false;
                ReceiptDetailsPopup.IsOpen = false;
                return true;
            });
            _salesViewSource.Source = _sales;
            _salesViewSource.Filter += FilterSales;
            DataContext = this;

            // 2026-10-18, этап О4 (аудит «Продажи»): поле поиска чека — с подсказкой (был пустой прямоугольник); чеки — вкладкой.
            SearchReceiptBox.Watermark = Tr.T("Поиск чека: номер или товар", "Чек издөө: номери же товар", "Find a receipt: number or product",
                "Fiş ara: numara veya ürün", "Chek qidirish: raqam yoki mahsulot");
            Avalonia.Automation.AutomationProperties.SetName(SearchReceiptBox, SearchReceiptBox.Watermark);
            ChecksTab.Header = Tr.T("Чеки", "Чектер", "Receipts", "Fişler", "Cheklar");
            AddReturnColumn(); // 2026-11-10, владелец: «функцию возврата добавь в продажи» — колонка «Возврат» (при праве «Возврат»)
            // 2026-10-24 (аудит, круг 8, С-5): плитки выручки и чеков — без продаж в долг (как в «Аналитике»); возвраты — подпись,
            // что возвращённые чеки уже не в выручке. Суммы плиток не менялись.
            RevenueLabel.Text = Tr.T("Выручка (без долга)", "Түшүм (карызсыз)", "Revenue (excl. credit)", "Ciro (veresiyesiz)", "Tushum (qarzsiz)");
            ReceiptsLabel.Text = Tr.T("Чеки (без долга)", "Чектер (карызсыз)", "Receipts (excl. credit)", "Fişler (veresiyesiz)", "Cheklar (qarzsiz)");
            RefundsNoteText.Text = Tr.T("возвращённые чеки уже не в выручке", "кайтарылган чектер киреше эсебинде жок", "returned receipts are already not in revenue",
                "iade fişleri artık gelirde değil", "qaytarilgan cheklar tushumda yo'q");
            // 2026-10-18, доводка О4: пустая таблица — с подсказкой, что делать.
            UiKit.EmptyHint(SalesGrid, Tr.T("Чеков за этот период нет. Выберите другой период — или проверьте строку поиска.",
                "Бул мезгилде чек жок. Башка мезгилди тандаңыз — же издөө сабын текшериңиз.",
                "No receipts for this period. Pick another period — or check the search box.",
                "Bu dönemde fiş yok. Başka bir dönem seçin — ya da arama kutusunu kontrol edin.",
                "Bu davrda chek yo'q. Boshqa davrni tanlang — yoki qidiruv qatorini tekshiring."));

            FullscreenHelper.Apply(this);
            // 2026-11-12 (Д-3): низкое окно — плотнее плитки и шапка (стиль Window.short в разметке), как в «Настройках».
            SizeChanged += (_, e) => { var low = e.NewSize.Height < 680; if (Classes.Contains("short") != low) Classes.Set("short", low); };

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

        protected override void OnClosed(EventArgs e)
        {
            PosDataEvents.SalesChanged -= OnSalesChangedExternally;
            _liveCts?.Cancel();
            _liveCts?.Dispose();
            _liveCts = null;
            _clockTimer?.Stop();
            CancelLoad();
            _searchCts?.Cancel();
            _searchCts = null;
            base.OnClosed(e);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 2026-11-12 (Д-3): поиск — над таблицей, шапка без поля поиска. Не в конструкторе: «Финансы» создают окно без показа и сами перекладывают
            // поиск и таблицу (DetachForFinance) — таблица не должна уже лежать в другой раскладке.
            if (!OperatingSystem.IsAndroid())
                MoveSearchIntoChecksTab();
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

        private void Back_Click(object sender, RoutedEventArgs e) => Close();

        private bool _searchInChecksTab;

        /// <summary>2026-11-12 (оценка UX/UI, Д-3): поиск чека — над таблицей во вкладке «Чеки», а не в шапке: шапка «Продаж» теперь та же, что у «Финансов»
        /// (заголовок, период, действия). Раньше так было только в программе владельца; один раз на окно.</summary>
        private void MoveSearchIntoChecksTab()
        {
            if (_searchInChecksTab)
                return;
            _searchInChecksTab = true;
            var search = (Control)SearchReceiptBox.Parent ?? SearchReceiptBox;
            (search.Parent as Panel)?.Children.Remove(search);
            search.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            search.Margin = new Thickness(0, 4, 0, 8);
            ChecksTab.Content = null;
            var checks = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(SalesGrid, 1);
            checks.Children.Add(SearchWithFilters(search)); // 2026-11-10: строка поиска + «Фильтры (N)» и панель фильтров
            checks.Children.Add(SalesGrid);
            ChecksTab.Content = checks;
        }

        /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): название уже над
        /// разделом, «Назад» некуда. Поиск чека, выгрузка и «Обновить» остаются строкой справа.
        /// Часы — для кассира за прилавком (как и в «Финансах» владельца, их здесь нет).
        ///
        /// 2026-09-27, «в продажах ABC продаж; аналитику из других вкладок убери»: вкладка
        /// «Показатели» (чистая прибыль, маржа, топ-10) ушла в раздел «Аналитика» — там те же
        /// цифры во вкладке «Выручка и оплаты». Здесь остаётся «ABC» со срезами про сами продажи:
        /// по количеству, по категориям и по брендам (деньги — в «Финансах», склад — на «Складе»).</summary>
        public void AsOwnerSection()
        {
            TitleText.IsVisible = false;
            BackButton.IsVisible = false;
            ClockBadge.IsVisible = false;
            _clockTimer?.Stop();
            RootGrid.Margin = OwnerSectionLayout.Margin;

            // 2026-10-18, этап О4 (аудит «Продажи»): период — в строку шапки слева (сначала период, потом цифры, как в
            // «Финансах»); на телефоне — своей строкой над плитками, как в разметке.
            if (!OperatingSystem.IsAndroid())
            {
                RootGrid.Children.Remove(PeriodBar);
                PeriodBar.Margin = new Thickness(0, 0, 12, 0);
                PeriodBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                Grid.SetRow(PeriodBar, 0);
                Grid.SetColumn(PeriodBar, 0);
                HeaderRow.Children.Add(PeriodBar);
                HeaderRow.Margin = new Thickness(0, 0, 0, 14);

                // Поиск чека — над таблицей во вкладке «Чеки» (как в «Продажах и финансах»): с периодом в шапке на 1024 он не
                // помещался бы.
                MoveSearchIntoChecksTab();
            }

            LowerTabs.Items.Remove(MetricsTab);
            // 2026-10-18, этап О4: «Чеки» и «ABC-анализ» — две вкладки на всю высоту (раньше ABC — одной вкладкой «ABC» в
            // нижней половине, с прокруткой внутри окошка); открыты «Чеки».
            LowerTabs.SelectedItem = ChecksTab;
            // На вкладке ABC плитки скрыты — ей вся высота раздела (как в «Продажах и финансах»).
            LowerTabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Source, LowerTabs) && LowerTabs.SelectedItem != null)
                    KpiTiles.IsVisible = !ReferenceEquals(LowerTabs.SelectedItem, AbcTab);
            };
            AbcSection.SliceKeys = new[]
            {
                AnalyticsReportData.KeyQuantity, AnalyticsReportData.KeyCategory, AnalyticsReportData.KeyBrand,
            };
            AbcSection.ShowSeasonality = false;
            AbcSection.TableMaxHeight = 520; // 2026-10-18, этап О4: как в «Финансах» — вкладка теперь на всю высоту
            AbcSection.ProductAnalyticsRequested += ShowProductAnalytics;
        }

        /// <summary>Разбор товара по нажатию на столбец или строку ABC — за период, выбранный на
        /// странице. Окно немодальное: товары сравнивают подряд.</summary>
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

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // 2026-09-29: «Обновить» — всегда свежие цифры сервера, мимо короткого кэша отчётов.
            NurMarketKassa.Services.Api.SalesApiService.InvalidateReportCache();
            await LoadDataAsync(_historyFrom, _historyTo);
        }

        // ── Загрузка данных ──

        /// <summary>Пересчитывает ABC за выбранный период. Считается локально, поэтому работает
        /// и без интернета — как и остальная аналитика. Расчёт уводим в Task.Run: он читает всю
        /// историю продаж за период, и на большом магазине это заметно подвесило бы окно.</summary>
        private async Task RefreshAbcAsync(DateTime from, DateTime to, CancellationToken token)
        {
            // 2026-10-18: в «Продажах и финансах» своего ABC нет — срезы во вкладке «ABC» окна «Финансов» (SalesWindow.Embedded.cs).
            if (_embeddedInFinance)
                return;
            try
            {
                // Сезонность читает всю историю продаж — без её вкладки (программа владельца) не считаем.
                var withSeasonality = AbcSection.ShowSeasonality;
                // 2026-09-28: ABC — по вкладке «Товары» сайта (как на сайте), без связи — по истории кассы.
                var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, withSeasonality, full: false, token)
                    .ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                AbcSection.Update(data);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ABC-анализ не построен: {ex.Message}", "WARNING");

            }
        }

        /// <summary>Пересчёт по сигналу «продажи изменились».
        ///
        /// С задержкой: за один чек хранилища поднимают сигнал несколько раз (строки продажи,
        /// событие смены, движение по кассе), а при выгрузке офлайн-очереди — по разу на чек.
        /// Без задержки окно перезагружалось бы десятки раз подряд и мигало.
        ///
        /// Сигнал приходит из фонового потока, поэтому обязательно уходим на UI-поток.</summary>
        private System.Threading.CancellationTokenSource? _liveCts;

        // 2026-11-05 (аудит №4, Л4-12): пока окно спрятано (раздел программы владельца), по продаже только пометка «устарело».
        private bool _staleWhileHidden;

        private void OnSalesChangedExternally()
        {
            // 2026-10-31 (приёмка 1.17.68): SalesChanged поднимается и с фоновых потоков (оплата долга, пульс продаж) — поля и _liveCts трогаем только в UI-потоке.
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(OnSalesChangedExternally);
                return;
            }
            // 2026-11-05 (аудит №4, Л4-12): окно спрятано — не качаем период по каждой продаже, только пометка; пересчёт при показе.
            if (!IsVisible)
            {
                _staleWhileHidden = true;
                _liveCts?.Cancel();
                return;
            }
            _liveCts?.Cancel();
            _liveCts?.Dispose();
            var cts = new System.Threading.CancellationTokenSource();
            _liveCts = cts;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(1200, cts.Token).ConfigureAwait(false);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (!IsVisible) { _staleWhileHidden = true; return; } // 2026-11-05 (Л4-12)
                        await LoadDataAsync(_historyFrom, _historyTo);
                    });
                }
                catch (System.OperationCanceledException)
                {
                    // Пришёл следующий сигнал — этот пересчёт уже не нужен.
                }
                catch (System.Exception ex)
                {
                    PosLogger.Log($"Живое обновление не выполнено: {ex.Message}", "WARNING");
                }
            });
        }

        private async Task LoadDataAsync(DateTime from, DateTime to)
        {
            CancelLoad();
            var currentCts = new CancellationTokenSource();
            _loadCts = currentCts;
            var token = currentCts.Token;

            try
            {
                IsLoading = true;
                ErrorMessage = null;

                // 2026-09-29: сервер не отвечает (ServerOutageMonitor) — сразу данные кассы с пометкой
                // времени, без ожидания таймаутов каждого запроса и без ошибок.
                if (ServerOutageMonitor.IsOutage)
                {
                    _serverSummary = null;
                    LoadFromLocalHistory(from, to, token);
                    return;
                }

                // ABC строится с сервера (вкладка «Товары» сайта), без связи — по локальной истории.
                // 2026-09-28: идёт одновременно с остальными запросами, а не перед ними — раньше
                // плитки ждали ещё и его (до 20 с при плохой связи).
                var abcTask = RefreshAbcAsync(from, to, token);

                // 2026-09-10: в автономном/офлайн режиме нет сервера, который отдал бы список
                // продаж — единственная правда о продажах лежит в локальной очереди чеков
                // (OfflinePendingSalesStore), куда PosCheckoutService пишет КАЖДУЮ продажу этого
                // режима (и они никогда оттуда не уходят — см. OfflineSaleEntry.IsAutonomous).
                if (OfflineModeHelper.UseLocalOperations)
                {
                    await abcTask;
                    // Сводка сервера прошлой загрузки к локальным данным не относится (2026-09-28:
                    // плитки теперь берут из неё выручку).
                    _serverSummary = null;
                    LoadLocalData(from, to, token);
                    return;
                }

                // 2026-09-28, «аналитика не похожа на веб, загрузка очень долгая»: сводка сайта,
                // товары сайта, журнал удалений и список чеков запрашиваются одновременно; плитки
                // показываются, как только ответила сводка (доли секунды), — не после всего списка
                // и не после построчной загрузки каждого чека (за месяц это было ≈ 2 минуты).
                var summaryTask = ServerSalesSummary.FetchAsync(from, to, token);
                var productsTask = FetchServerTopItemsAsync(from, to, token);
                // "is_refund" — несуществующее поле в реальном API (сервер никогда его не отдаёт,
                // это всегда false), поэтому возвраты нельзя достать фильтром по списку продаж —
                // единственный реальный след возврата оплаченной позиции в API это журнал удалений
                // из корзины (см. FetchCartItemDeletionsAsync).
                var refundsTask = FetchCartItemDeletionsAsync(from, to, token);
                // 2026-09-29: первые страницы списка видны сразу, не дожидаясь всего периода.
                var listTask = FetchAllSalesAsync(from, to, token, firstRows =>
                {
                    if (!token.IsCancellationRequested)
                        UpdateCollections(firstRows, new List<RefundItem>());
                });

                // Выручка, чеки, оплаты, возвраты и прибыль — цифры сайта (см. ServerSalesSummary);
                // null — считаем сами по списку.
                _serverSummary = await summaryTask;
                token.ThrowIfCancellationRequested();
                if (_serverSummary?.Revenue != null)
                    UpdateStats(new List<SaleItem>(), new List<RefundItem>(), listLoaded: false);

                // 2026-09-29, «увеличь скорость загрузки аналитики»: топ товаров — из вкладки «Товары»,
                // от списка чеков не зависит; показываем его сразу, а не после списка (у большого
                // магазина месяц — десятки страниц, 30–60 с).
                var serverTop = await productsTask;
                token.ThrowIfCancellationRequested();
                var topFromServer = serverTop != null && _serverSummary?.GrossProfit != null;
                if (topFromServer)
                {
                    TopItems.Clear();
                    foreach (var item in serverTop)
                        TopItems.Add(item);
                }

                var sales = await listTask;
                // 2026-10-18, ревью: период сменили, пока грузился список, — старый результат не показываем.
                token.ThrowIfCancellationRequested();

                AssignFallbackReceiptNumbers(sales);

                var refunds = await refundsTask;
                token.ThrowIfCancellationRequested();

                UpdateCollections(sales, refunds);
                UpdateStats(sales, refunds);
                if (_salesListTruncated)
                    ErrorMessage = Tr.T(
                        "Период очень большой: в списке — последние 50 000 чеков, скидки за период не посчитаны. Выручка, чеки, оплаты, прибыль, топ и ABC — полные, с сервера.",
                        "Мезгил өтө чоң: тизмеде — акыркы 50 000 чек, мезгилдеги арзандатуулар эсептелген жок. Түшүм, чектер, төлөмдөр, пайда, топ жана ABC — толук, серверден.",
                        "The period is very large: the list shows the latest 50,000 receipts and the period's discounts are not calculated. Revenue, receipts, payments, profit, top and ABC are complete, from the server.",
                        "Dönem çok büyük: listede son 50.000 fiş var, dönemin indirimleri hesaplanmadı. Ciro, fişler, ödemeler, kâr, en çok satanlar ve ABC eksiksiz, sunucudan.",
                        "Davr juda katta: ro'yxatda — oxirgi 50 000 ta chek, davr chegirmalari hisoblanmadi. Tushum, cheklar, to'lovlar, foyda, top va ABC — to'liq, serverdan.");

                if (topFromServer)
                {
                    // Состав чека в строке списка — в фоне, окно уже готово (см. LoadReceiptContentsAsync).
                    _ = LoadReceiptContentsAsync(sales);
                }
                else if (sales.Count <= 2000)
                {
                    // Сервер не дал вкладку «Товары» или сводку (например, нет доступа к аналитике) —
                    // прежний путь: строки каждого чека.
                    await LoadTopItemsAsync(sales, token);
                }
                else
                {
                    // 2026-09-29, стресс-тест: чеков тысячи — запрос на КАЖДЫЙ чек (за год — десятки
                    // тысяч) шёл бы часами и сам вызывал бы 429. Топ и прибыль — «—» до «Обновить»,
                    // состав в строках — как обычно, у новых чеков (MaxReceiptContents).
                    PosLogger.Log($"Продажи: топ и прибыль не посчитаны — сводка/товары сервера не ответили, а чеков {sales.Count}.", "WARNING");
                    TopItems.Clear();
                    if (_serverSummary?.GrossProfit == null)
                    {
                        NetProfitText.Text = "—";
                        MarkLoss(NetProfitText, false);
                        MarginPercentText.Text = "—";
                    }
                    _ = LoadReceiptContentsAsync(sales);
                }

                await abcTask;
            }
            catch (OperationCanceledException)
            {
                PosLogger.Log("Sales refresh canceled.", "DEBUG");
            }
            catch (Exception ex)
            {
                // Сервер недоступен — показываем то, что касса знает сама. До 2026-09-22 экран
                // просто оставался пустым с красной строкой «Этот хост неизвестен»: ни выручки,
                // ни чеков, ни топа товаров, хотя все эти данные лежат в локальной истории
                // продаж. Без сети магазин продолжает работать, и смотреть свои же цифры
                // владелец должен уметь в любой момент.
                PosLogger.Log($"Продажи: сервер недоступен ({ex.Message}) — показываю локальные данные.", "SALES");
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

        /// <summary>Запасной путь для экрана «Продажи», когда сервера нет.
        ///
        /// Источник — SoldLineItems: туда касса пишет КАЖДУЮ проданную строку в момент оплаты,
        /// и офлайн, и онлайн. Чек собирается группировкой по отметке времени: все строки одной
        /// продажи пишутся одним моментом, поэтому одна отметка — один чек.
        ///
        /// Чего здесь честно нет: способа оплаты и возвратов — они живут только на сервере.
        /// Поэтому оплата показывается прочерком, а не выдумывается, и в шапке прямо сказано,
        /// что данные локальные.</summary>
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

            UpdateCollections(sales, new List<RefundItem>());
            _serverSummary = null;
            UpdateStats(sales, new List<RefundItem>());

            // Топ товаров тоже из локальных строк — на сервер за ним ходить незачем.
            var top = lines
                .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TopItem
                {
                    ProductName = g.First().ProductName,
                    // Quantity в TopItem — целое: дробные килограммы округляем, иначе тип не
                    // сойдётся, а для «топа по выручке» доли килограмма роли не играют.
                    Quantity = (int)Math.Round(g.Sum(x => x.Quantity), MidpointRounding.AwayFromZero),
                    Revenue = (decimal)g.Sum(x => x.Quantity * x.UnitPrice),
                })
                .OrderByDescending(x => x.Revenue)
                .Take(10)
                .ToList();
            TopItemsGrid.ItemsSource = top;

            ErrorMessage = ServerOutageMonitor.IsOutage
                // 2026-09-29: «Данные на 14:05, сервер не отвечает» — с какого времени нет данных сервера.
                ? ServerOutageMonitor.StaleDataNote(null) + ServerOutageMonitor.LocalDataSuffix
                : sales.Count > 0
                ? Tr.T("Сервер NurCRM недоступен — показаны данные этой кассы за выбранный период.", // 2026-11-01 (В-08): единая фраза о сбое сервера
                    "NurCRM сервери жеткиликсиз — тандалган мезгил үчүн ушул кассанын маалыматтары көрсөтүлдү.",
                    "The NurCRM server is unavailable — showing this till's data for the selected period.",
                    "NurCRM sunucusu kullanılamıyor — seçilen dönem için bu kasanın verileri gösteriliyor.",
                    "NurCRM serveri mavjud emas — tanlangan davr uchun shu kassaning ma'lumotlari ko'rsatildi.")
                : Tr.T("Сервер NurCRM недоступен, а локальных продаж за выбранный период нет.",
                    "NurCRM сервери жеткиликсиз, ал эми тандалган мезгилде жергиликтүү сатуулар жок.",
                    "The NurCRM server is unavailable, and there are no local sales for the selected period.",
                    "NurCRM sunucusu kullanılamıyor ve seçilen dönemde yerel satış yok.",
                    "NurCRM serveri mavjud emas, tanlangan davrda esa mahalliy sotuvlar yo'q.");
        }

        /// <summary>2026-09-10: офлайн-эквивалент LoadDataAsync — источник данных
        /// OfflinePendingSalesStore вместо App.SalesApi. Возвратов здесь нет (нет локального
        /// журнала удалений позиций из уже оплаченного чека — это отдельная, более редкая
        /// функция, вне охвата текущей задачи), поэтому список возвратов всегда пуст.</summary>
        private void LoadLocalData(DateTime from, DateTime to, CancellationToken token)
        {
            var entries = OfflinePendingSalesStore.LoadAll()
                .Where(e => e.IsAutonomous)
                .Where(e => e.CreatedAt.Date >= from.Date && e.CreatedAt.Date <= to.Date)
                .OrderBy(e => e.CreatedAt)
                .ToList();

            var sales = new List<SaleItem>(entries.Count);
            var position = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                position++;
                sales.Add(new SaleItem
                {
                    Id = entry.Id,
                    CreatedAt = entry.CreatedAt.LocalDateTime,
                    ReceiptNumber = $"№{position}",
                    TotalAmount = LocalCartTotal(entry.CartJson),
                    PaymentMethod = entry.PaymentMethod,
                });
            }

            var refunds = new List<RefundItem>();
            UpdateCollections(sales, refunds);
            UpdateStats(sales, refunds);
            LoadLocalTopItems(entries);
        }

        private static decimal LocalCartTotal(string cartJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
                return (decimal)CartTotalsCalculator.Calculate(doc.RootElement).TotalDue;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Local sale total parse skipped: {ex.GetType().Name}", "WARNING");
                return 0m;
            }
        }

        private void LoadLocalTopItems(List<OfflineSaleEntry> entries)
        {
            var dict = new Dictionary<string, (decimal revenue, int qty)>();
            foreach (var entry in entries)
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Local sale items parse skipped: {ex.GetType().Name}", "WARNING");
                    continue;
                }

                using (doc)
                {
                    foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(doc.RootElement))
                    {
                        var name = CartDisplayHelper.ItemName(line);
                        var qty = CartDisplayHelper.LineQuantity(line);
                        var revenue = (decimal)(CartDisplayHelper.UnitPrice(line) * qty);
                        dict[name] = dict.TryGetValue(name, out var existing)
                            ? (existing.revenue + revenue, existing.qty + (int)qty)
                            : (revenue, (int)qty);
                    }
                }
            }

            var top10 = dict.OrderByDescending(kv => kv.Value.revenue).Take(10);
            TopItems.Clear();
            foreach (var kv in top10)
                TopItems.Add(new TopItem { ProductName = kv.Key, Revenue = kv.Value.revenue, Quantity = kv.Value.qty });
        }

        /// <summary>Качает продажи за период постранично.
        ///
        /// 2026-09-22, живой баг: здесь был вызов FetchSalesPageAsync(1, 500, token) — одна
        /// страница и БЕЗ периода, а фильтр по датам применялся уже к полученному набору.
        /// Ломалось это в двух местах сразу: PosSalesListAsync молча урезает page_size до 80
        /// (см. её Math.Clamp), и запрос уходил без date_from/date_to. В итоге экран всегда
        /// работал с одними и теми же 80 чеками магазина, и любой период — день, неделя,
        /// месяц — фильтровался внутри этой выборки. Поэтому цифры расходились с веб-аналитикой
        /// NurCRM, которая считает на сервере по всему периоду: у магазина с 355 продажами за
        /// месяц касса видела максимум 80 и показывала заниженную выручку.
        ///
        /// «Финансы» починили так же ещё 2026-09-21 (FinanceWindow.FetchAllSalesAsync) — этот
        /// экран тогда пропустили.</summary>
        /// <param name="firstBatch">2026-09-29, «увеличь скорость загрузки аналитики»: вызывается с первыми
        /// загруженными страницами (новые чеки), если за ними есть ещё — окно показывает их, пока
        /// догружается остальное (у большого магазина месяц — десятки страниц).</param>
        private async Task<List<SaleItem>> FetchAllSalesAsync(DateTime from, DateTime to, CancellationToken token,
            Action<List<SaleItem>> firstBatch = null)
        {
            // 2026-09-28: страницы по 500 строк и по 3 одновременно, предел 100 страниц (50 000 чеков)
            // вместо 60 по 80 (4 800): у магазина с сотнями чеков в день «Месяц» обрезался, и выручка
            // была меньше, чем на сайте. Страница за последней повторяет последнюю — её строки уже видены.
            const int pageSize = 500;
            const int maxPages = 100;
            const int parallelPages = 3;
            var result = new List<SaleItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _salesListTruncated = false;
            lock (_canceledSaleIds)
                _canceledSaleIds.Clear();

            for (var firstPage = 1; firstPage <= maxPages; firstPage += parallelPages)
            {
                token.ThrowIfCancellationRequested();
                var batch = await Task.WhenAll(Enumerable
                    .Range(firstPage, Math.Min(parallelPages, maxPages - firstPage + 1))
                    .Select(page => FetchSalesPageAsync(page, pageSize, from, to, token)));

                var done = false;
                foreach (var (pageItems, rawIds) in batch)
                {
                    // RawIds — все строки страницы, включая отменённые (они отсеиваются внутри
                    // FetchSalesPageAsync; 2026-09-25: без этого 79 из 80 выглядело концом списка).
                    var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var blank = 0;
                    foreach (var id in rawIds)
                    {
                        if (string.IsNullOrEmpty(id))
                            blank++;
                        else if (seen.Add(id))
                            fresh.Add(id);
                    }

                    foreach (var item in pageItems)
                    {
                        if (string.IsNullOrEmpty(item.Id) || fresh.Contains(item.Id))
                            result.Add(item);
                    }

                    if (fresh.Count + blank == 0 || rawIds.Count < pageSize)
                    {
                        done = true;
                        break;
                    }
                }

                if (done)
                    break;
                // Все страницы до предела полные — дальше чеки есть, но не загружены.
                if (firstPage + parallelPages > maxPages)
                    _salesListTruncated = true;
                else if (firstPage == 1)
                    firstBatch?.Invoke(result.Where(s => s.CreatedAt.Date >= from.Date && s.CreatedAt.Date <= to.Date).ToList());
            }

            if (_salesListTruncated)
                PosLogger.Log($"Продажи: список чеков {from:dd.MM.yyyy}–{to:dd.MM.yyyy} обрезан на {result.Count} строках (предел {maxPages} страниц).", "SALES");

            // Сервер фильтрует по дате сам, но подстраховываемся: если он проигнорировал
            // date_from/date_to, период всё равно будет соблюдён.
            return result
                .Where(s => s.CreatedAt.Date >= from.Date && s.CreatedAt.Date <= to.Date)
                .ToList();
        }

        /// <summary>2026-09-29, стресс-тест: список упёрся в предел страниц (50 000 чеков). Выручка, чеки,
        /// оплаты, прибыль, топ и ABC — со сводки сервера и полные; «Скидки» считаются по списку, поэтому
        /// при обрезанном списке вместо заниженной суммы — «—» и плашка.</summary>
        private bool _salesListTruncated;

        /// <summary>Отменённые на сервере чеки последней загрузки (см. FetchSalesPageAsync).</summary>
        private readonly HashSet<string> _canceledSaleIds = new(StringComparer.OrdinalIgnoreCase);

        private async Task<(List<SaleItem> Items, List<string> RawIds)> FetchSalesPageAsync(
            int page, int pageSize, DateTime from, DateTime to, CancellationToken token)
        {
            // 2026-09-28: список отменённых очищает FetchAllSalesAsync — страницы теперь идут
            // одновременно, и очистка на первой из них стёрла бы найденное соседними.

            // date_to на сервере НЕ включает свой день, поэтому передаём следующий.
            var raw = await App.SalesApi.PosSalesListAsync(
                page, pageSize, null, token,
                dateFrom: from.Date,
                dateToExclusive: to.Date.AddDays(1));
            token.ThrowIfCancellationRequested();
            var result = new List<SaleItem>(raw.Count);
            var rawIds = new List<string>(raw.Count);
            foreach (JsonElement el in raw)
            {
                var item = new SaleItem();
                if (el.TryGetProperty("id", out var idProp)) item.Id = idProp.ToString() ?? "";
                rawIds.Add(item.Id);
                // 2026-09-25: полностью возвращённый чек сервер помечает «canceled», но оставляет в
                // списке с прежней суммой. В выручку, наличные и число чеков он не входит.
                if (el.TryGetProperty("status", out var statusProp)
                    && string.Equals(statusProp.GetString(), "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_canceledSaleIds)
                        _canceledSaleIds.Add(item.Id);
                    continue;
                }
                if (el.TryGetProperty("status", out var saleStatus) && saleStatus.ValueKind == JsonValueKind.String)
                    item.Status = saleStatus.GetString() ?? "";
                // 2026-09-28: время — по часам сервера, как у сайта (см. FinanceWindow.TryServerTime).
                if (el.TryGetProperty("created_at", out var dateProp) && FinanceWindow.TryServerTime(dateProp.GetString(), out var dt)) item.CreatedAt = dt;
                // 2026-09-28: первый товар чека есть прямо в списке — видно сразу; полный состав
                // подгружается в фоне (LoadReceiptContentsAsync).
                if (el.TryGetProperty("first_item_name", out var firstItem) && firstItem.ValueKind == JsonValueKind.String)
                    item.ItemsSummary = firstItem.GetString() ?? "";
                item.ReceiptNumber = TryReceiptNumber(el) ?? "";
                if (el.TryGetProperty("total", out var totalProp)) item.TotalAmount = ParseDecimal(totalProp);
                if (el.TryGetProperty("discount_total", out var discProp)) item.DiscountTotal = ParseDecimal(discProp);
                if (el.TryGetProperty("payment_method", out var pmProp)) item.PaymentMethod = pmProp.GetString() ?? "";
                if (el.TryGetProperty("is_refund", out var rfProp) && ParseBool(rfProp)) item.IsRefund = true;
                if (el.TryGetProperty("refund_reason", out var rrProp)) item.RefundReason = rrProp.GetString();
                // 2026-11-10 (возврат и фильтры в «Продажах»): статус возврата, кассир, клиент.
                if (el.TryGetProperty("return_status", out var retProp) && retProp.ValueKind == JsonValueKind.String) item.ReturnStatus = retProp.GetString();
                item.Cashier = CartDisplayHelper.TryCashierName(el);
                if (el.TryGetProperty("client_name", out var cnProp) && cnProp.ValueKind == JsonValueKind.String) item.ClientName = cnProp.GetString();
                // ReceiptNumber остаётся пустым, если сервер не прислал ни одного из известных полей —
                // AssignFallbackReceiptNumbers подставит читаемый порядковый номер вместо «сырого» GUID.
                result.Add(item);
            }
            return (result, rawIds);
        }

        /// <summary>
        /// Ищет читаемый номер чека среди возможных полей ответа API (сервер использует разные имена
        /// в разных версиях). Числовые значения дополняются нулями до 6 знаков, как на печатном чеке.
        /// </summary>
        internal static string TryReceiptNumber(JsonElement sale)
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
                if (string.IsNullOrWhiteSpace(item.ReceiptNumber))
                    item.ReceiptNumber = $"№{position}";
            }
        }

        private static decimal ParseDecimal(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d)) return d;
            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d2)) return d2;
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
            _loadCts?.Cancel();
            _loadCts = null;
            _contentsCts?.Cancel();
            _contentsCts = null;
        }
    }
}
