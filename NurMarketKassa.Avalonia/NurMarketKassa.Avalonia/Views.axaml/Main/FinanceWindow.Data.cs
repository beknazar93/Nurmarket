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
    // 2026-11-06: В15 — часть класса FinanceWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — FinanceWindow.axaml.cs
    public partial class FinanceWindow
    {
        /// <summary>Пересчитывает популярные/непопулярные товары из уже загруженных данных
        /// текущего периода (TopItems/facts) — без отдельных запросов к серверу.</summary>
        private void RefreshProductsAnalyticsPanel()
        {
            // Пустой TopItems может означать либо реальное отсутствие продаж за период, либо
            // неудачную загрузку (например, кратковременный обрыв связи) — раньше оба случая
            // выглядели одинаково ("Нет данных"), что сбивало с толку. HasError уже выставляется
            // в LoadDataAsync при сбое запроса, но баннер с ошибкой показывается только на главной
            // вкладке продаж, а не здесь — поэтому дублируем понятное сообщение прямо в диаграммах.
            // 2026-09-29: плашка «список обрезан» — не ошибка загрузки, товары при ней полные.
            if (HasError && ErrorMessage != ListTruncatedNotice)
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

            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — полосы товаров снова разноцветные (было одним цветом с 2026-10-19, О10):
            // цвет товара — по хэшу названия (ChartPalette), не по порядку, тот же на всех экранах и в мобилке.
            BarChartRenderer.Render(PopularProductsChart, TopItems
                .Select(t => (t.ProductName, (double)t.Revenue, $"{t.Revenue:N0} {Som}"))
                .ToList());
            BarChartRenderer.RenderPie(PopularProductsPie, TopItems
                .Take(10)
                .Select(t => (t.ProductName, (double)t.Revenue, $"{t.Revenue:N0} {Som}"))
                .ToList());
            BarChartRenderer.Render(UnpopularProductsChart, unpopular
                .Take(10)
                .Select(p => (p.Title, p.Quantity, $"{p.Quantity:N0} {Pcs}"))
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
                    StockText = $"{p.Quantity:0.###} {(p.MustWeigh ? Kg : Pcs)}",
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
                .Select(p => (p.Title, p.Quantity, $"{p.Quantity:N0} {(p.MustWeigh ? Kg : Pcs)}"))
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
                $"Мезгилдин маалыматтарын жүктөө мүмкүн болгон жок: {ErrorMessage}", $"Could not load data for the period: {ErrorMessage}", $"Dönem verileri yüklenemedi: {ErrorMessage}", $"Davr uchun ma'lumotlarni yuklab bo'lmadi: {ErrorMessage}");
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

        // 2026-11-06 (В11, шаг 7, п. 26.3): «Все товары» (до 15 000 строк) фильтруются после паузы 200 мс с последней буквы, а не на каждую.
        private NurMarketKassa.AvaloniaHost.Services.SearchPause _allProductsPause;

        private void AllProductsSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            (_allProductsPause ??= new NurMarketKassa.AvaloniaHost.Services.SearchPause(ApplyAllProductsFilter, 200)).Trigger();

        private void ApplyAllProductsFilter()
        {
            var query = (AllProductsSearchBox.Text ?? "").Trim();
            AllProductsGrid.ItemsSource = query.Length < 2
                ? _allProductRows
                : _allProductRows
                    .Where(r => SearchMatch.Matches(query, r.ProductName, r.Article)) // 2026-10-29: единый поиск
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
        /// и без интернета — как и остальная аналитика.
        ///
        /// 2026-09-27: расчёт — в фоне, как в «Продажах». Раньше он шёл прямо в UI-потоке вместе с
        /// сезонностью (полный проход по всей истории продаж), и на каждом переключении периода
        /// окно замирало. Новый пересчёт отменяет недосчитанный прошлый.</summary>
        private CancellationTokenSource _abcCts;

        private void RefreshAbc() => _ = RefreshAbcAsync();

        private async Task RefreshAbcAsync()
        {
            // В разделе «Аналитика» программы владельца вкладки ABC нет — считать нечего.
            if (_isAnalyticsSection)
                return;

            _abcCts?.Cancel();
            var cts = new CancellationTokenSource();
            _abcCts = cts;
            var from = _historyFrom;
            var to = _historyTo;
            var withSeasonality = AbcSection.ShowSeasonality;
            try
            {
                // 2026-09-28: ABC — по вкладке «Товары» сайта (как на сайте), без связи — по истории кассы.
                var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, withSeasonality, full: false, cts.Token)
                    .ConfigureAwait(true);
                if (cts.IsCancellationRequested)
                    return;

                AbcSection.Update(data);
            }
            catch (OperationCanceledException)
            {
                // Период сменили, пока считался прошлый, — результат уже не нужен.
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ABC-анализ не построен: {ex.Message}", "WARNING");
            }
            finally
            {
                if (ReferenceEquals(_abcCts, cts))
                    _abcCts = null;
                cts.Dispose();
            }
        }

        /// <summary>Выгрузка аналитики за выбранный период — те же файлы, что и на экране
        /// «Продажи». 2026-09-28: цифры — те же, что на этом экране и на сайте (выручка, чеки,
        /// скидки, возвраты, товары — с сервера, см. AnalyticsReportData.ServerFigures); без связи
        /// отчёт строится из локальных данных кассы, как раньше.</summary>
        // 2026-10-19, владелец: «при нажатии на Excel просто выведи все продажи, а не аналитику» — Excel: все чеки периода
        // (лист «Чеки» и «Товары», SalesExcelExport); Word — аналитика, как была.
        private async void ExportExcel_Click(object? sender, RoutedEventArgs e) =>
            await SalesExcelExport.RunAsync(this, _historyFrom, _historyTo, message => ErrorMessage = message,
                () => AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: true)).ConfigureAwait(true);

        private async void ExportWord_Click(object? sender, RoutedEventArgs e) =>
            await ExportAnalyticsAsync(toWord: true).ConfigureAwait(true);

        private async Task ExportAnalyticsAsync(bool toWord)
        {
            if (!TariffGate.CanUseAnalyticsExport)
            {
                ErrorMessage = TariffGate.AnalyticsExportLockedMessage;
                return;
            }

            // 2026-10-22 (аналитика по категориям): на вкладке «По категориям» выгружается отчёт выбранных категорий.
            var categoryExport = _categoryView != null && ReferenceEquals(FinanceTabs.SelectedItem, _ownerCategoriesTab) ? _categoryView.Data : null;
            var extension = toWord ? "docx" : "xlsx";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = toWord
                    ? Tr.T("Сохранить отчёт в Word", "Отчётту Word'го сактоо", "Save report to Word", "Raporu Word olarak kaydet", "Hisobotni Word'ga saqlash")
                    : Tr.T("Сохранить отчёт в Excel", "Отчётту Excel'ге сактоо", "Save report to Excel", "Raporu Excel olarak kaydet", "Hisobotni Excel'ga saqlash"),
                SuggestedFileName = AnalyticsExportService.SuggestFileName(categoryExport != null ? CategoryAnalyticsExport.FileTitle : AnalyticsExportService.FileTitle,
                    _historyFrom, _historyTo, ReportShopName.Current, extension),
                FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
            });
            if (file is null)
                return;

            var path = ExportFileBridge.PathFor(file); // 2026-11-05 (А3-05): на Android без локального пути — через временный файл

            try
            {
                ErrorMessage = Tr.T("Готовлю отчёт…", "Отчёт даярдалууда…", "Preparing the report…", "Rapor hazırlanıyor…", "Hisobot tayyorlanmoqda…");
                if (categoryExport != null)
                {
                    var shopName = ReportShopName.Current;
                    await Task.Run(() =>
                    {
                        if (toWord)
                            CategoryAnalyticsExport.ExportToWord(path!, categoryExport, shopName);
                        else
                            CategoryAnalyticsExport.ExportToExcel(path!, categoryExport, shopName);
                    }).ConfigureAwait(true);
                    path = await ExportFileBridge.CompleteAsync(file, path); // 2026-11-05 (А3-05)
                    ErrorMessage = Tr.T($"Отчёт сохранён: {path}", $"Отчёт сакталды: {path}", $"Report saved: {path}", $"Rapor kaydedildi: {path}", $"Hisobot saqlandi: {path}");
                    AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: true);
                    return;
                }
                var data = await AnalyticsReportData.BuildAsync(App.SalesApi, _historyFrom, _historyTo, full: true).ConfigureAwait(true);
                var shop = ReportShopName.Current;

                await Task.Run(() =>
                {
                    if (toWord)
                        AnalyticsExportService.ExportToWord(path!, data, shop);
                    else
                        AnalyticsExportService.ExportToExcel(path!, data, shop);
                }).ConfigureAwait(true);
                path = await ExportFileBridge.CompleteAsync(file, path); // 2026-11-05 (А3-05)

                ErrorMessage = Tr.T($"Отчёт сохранён: {path}", $"Отчёт сакталды: {path}", $"Report saved: {path}", $"Rapor kaydedildi: {path}", $"Hisobot saqlandi: {path}");
                AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выгрузка аналитики не удалась: {ex}", "WARNING");
                ErrorMessage = Tr.T("Не удалось сохранить отчёт: ", "Отчётту сактоо мүмкүн болгон жок: ", "Could not save the report: ", "Rapor kaydedilemedi: ", "Hisobotni saqlab bo'lmadi: ") + ex.Message;
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
            _serverSummary = null;
            _serverExpenses = null; // 2026-10-20: без сервера — расходы по изъятиям кассы, как раньше
            _expensesPending = false;
            SalesTileRules.Show(FinReceiptsDebtNote, sales.Select(s => (s.Status, s.TotalAmount))); // 2026-11-07 (Б-C1)
            UpdateStats(sales, noRefunds);
            UpdateCharts(sales);
            UpdateHistory(sales, noRefunds);

            RefreshAbc();
            // 2026-10-18: «Продажи и финансы» — «Чеки» тоже по данным кассы (FinanceWindow.Merged.cs).
            ShowMergedSalesLocal(from, to);

            ErrorMessage = ServerOutageMonitor.IsOutage
                // 2026-09-29: «Данные на 14:05, сервер не отвечает» — с какого времени нет данных сервера.
                ? ServerOutageMonitor.StaleDataNote(null) + ServerOutageMonitor.LocalDataSuffix
                : sales.Count > 0
                ? Tr.T("Сервер недоступен — показаны данные этой кассы за выбранный период.",
                    "Сервер жеткиликсиз — тандалган мезгил үчүн ушул кассанын маалыматтары көрсөтүлдү.",
                    "The server is unavailable — showing this till's data for the selected period.",
                    "Sunucu kullanılamıyor — seçilen dönem için bu kasanın verileri gösteriliyor.",
                    "Server mavjud emas — tanlangan davr uchun shu kassaning ma'lumotlari ko'rsatildi.")
                : Tr.T("Сервер недоступен, а локальных продаж за выбранный период нет.",
                    "Сервер жеткиликсиз, ал эми тандалган мезгилде жергиликтүү сатуулар жок.",
                    "The server is unavailable, and there are no local sales for the selected period.",
                    "Sunucu kullanılamıyor ve seçilen dönemde yerel satış yok.",
                    "Server mavjud emas, tanlangan davrda esa mahalliy sotuvlar yo'q.");
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
            // Сезонность раздела «Аналитика» пересчитается после перезагрузки (если вкладка открыта).
            _seasonalityLoaded = false;
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
                    // 2026-09-28: список чеков периода не перекачивался (окно то же) — история и
                    // графики не видели новую продажу. Для коротких периодов по сегодня (до недели —
                    // одна-две страницы) перекачиваем; плитки и так берутся со сводки сервера заново.
                    // 2026-10-18, ревью объединения: в «Продажах и финансах» список по сегодня перекачивается при любом периоде —
                    // иначе «Чеки» и «Скидки» на «Месяце» не видели новую продажу.
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (!IsVisible) { _staleWhileHidden = true; return; } // 2026-11-05 (Л4-12): спрятали, пока ждали
                        await LoadDataAsync(_historyFrom, _historyTo,
                        forceRefresh: _historyTo.Date >= ServerClock.Today && (_mergedSales != null || (_historyTo.Date - _historyFrom.Date).Days < 7));
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

        private async Task LoadDataAsync(DateTime from, DateTime to, bool forceRefresh = false)
        {
            CancelLoad();
            // 2026-10-18, ревью объединения: «Чеки» — признак загрузки, «Скидки» и «Оплачено бонусами» — «…» до нового списка.
            BeginMergedSalesLoad();
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
                    LoadFromLocalHistory(from, to, token);
                    return;
                }

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
                // 2026-10-20, аудит программы владельца: смены уже загружены — пересчитать под новый период.
                if (_isOwnerSection && _shiftsLoaded)
                    ApplyShiftsFilter();

                // 2026-09-28, жалоба клиентов «аналитика не похожа на веб, загрузка очень долгая»:
                // окно «период + последние 60 дней» (ради карточки сводки) даже для «Сегодня»
                // качало два месяца продаж по 80 строк — 15 запросов и ~12 с на тестовом аккаунте,
                // а у магазина с сотнями чеков в день упиралось в предел 60 страниц (4 800 чеков):
                // «Месяц» и прошлые периоды обрезались, выручка выходила меньше, чем на сайте.
                // Теперь качается ровно выбранный период (сводка берёт свои цифры с сервера, см.
                // RefreshSummaryPanelAsync), а плитки — с сервера (tab=sales), как у сайта.
                var fetchFrom = from.Date;
                var fetchTo = to.Date;

                // Сводка сайта, товары сайта и журнал удалений — одновременно со списком чеков.
                var summaryTask = ServerSalesSummary.FetchAsync(from, to, token);
                // 2026-10-20: аудит владельца — расходы и чистая прибыль из отчёта сервера, как «Прибыль и деньги».
                var expensesTask = FetchServerExpensesAsync(from, to, token);
                var wantProducts = !_isOwnerSection || _isAnalyticsSection;
                var productsTask = wantProducts ? FetchServerProductFactsAsync(from, to, from.Date, token) : null;
                var refundsTask = FetchRefundsAsync(from, to, token);
                // Переключение на уже загруженный период не ходит за списком в сеть.
                var listTask = forceRefresh || _lastAllSales.Count == 0
                               || _lastFetchFrom != fetchFrom || _lastFetchTo != fetchTo
                    ? FetchAllSalesAsync(fetchFrom, fetchTo, token, firstRows =>
                    {
                        // 2026-09-29: первые страницы списка видны сразу, не дожидаясь всего периода.
                        if (!token.IsCancellationRequested)
                        {
                            UpdateCollections(firstRows, new List<RefundItem>());
                            // 2026-10-18: и во вкладку «Чеки» — без прошлого периода на 30–60 с.
                            ShowMergedSalesFirstRows(firstRows);
                        }
                    })
                    : null;

                // Плитки — сразу, как только ответила сводка сайта (доли секунды), не дожидаясь списка.
                _serverSummary = await summaryTask;
                token.ThrowIfCancellationRequested();
                // 2026-10-20: расходы приходят сами по себе — плитки выручки их не ждут (до ответа — «…»).
                _serverExpenses = null;
                _expensesPending = true;
                _ = ApplyServerExpensesAsync(expensesTask, token);
                if (_serverSummary?.Revenue != null)
                {
                    SalesTileRules.Show(FinReceiptsDebtNote, null); // 2026-11-07 (Б-C1): список ещё грузится — строку «в долг» не показываем
                    UpdateStats(new List<SaleItem>(), new List<RefundItem>());
                }
                // 2026-09-29, стресс-тест: ABC берётся из вкладки «Товары» сервера и от списка чеков не
                // зависит — считаем его сразу, а не после списка (за год большого магазина список —
                // сотня страниц и больше минуты, всё это время вкладка ABC стояла пустой).
                RefreshAbc();

                // 2026-09-29, «увеличь скорость загрузки аналитики»: оплаты и выручка по дням — из той же
                // сводки, топ товаров и рекомендации — из вкладки «Товары»; всё это показывается сразу, а
                // не после списка чеков (у большого магазина месяц — десятки страниц, 40–60 с). Список
                // догружается следом и обновляет только то, что без него не посчитать: сами строки,
                // историю и график по часам.
                _listPending = true;
                if (_serverSummary?.Revenue != null)
                    UpdateCharts(new List<SaleItem>());
                List<SaleLineFact> serverFacts = null;
                if (wantProducts)
                {
                    // 2026-09-28: топ — из вкладки «Товары» сайта одним запросом (выручка строк после
                    // скидки, как на сайте). Раньше здесь скачивался КАЖДЫЙ чек периода отдельно:
                    // за месяц (767 чеков) ≈ 2 минуты с ограничением частоты, а у больших магазинов —
                    // десятки минут. Старый путь остался на случай, когда сервер эту вкладку не дал
                    // (например, у учётной записи кассира нет доступа к аналитике).
                    serverFacts = productsTask == null ? null : await productsTask;
                    token.ThrowIfCancellationRequested();
                    if (serverFacts != null)
                    {
                        _lastSaleLineFacts = serverFacts;
                        UpdateTopItems(serverFacts);
                        _ = LoadDayInsightsAsync(from, to, serverFacts);
                        if (_serverSummary?.Revenue != null)
                            UpdateStats(new List<SaleItem>(), new List<RefundItem>());
                    }
                }

                if (listTask != null)
                {
                    _lastAllSales = await listTask;
                    _lastFetchFrom = fetchFrom;
                    _lastFetchTo = fetchTo;
                }

                // Фильтр оставлен как страховка: если сервер вдруг проигнорирует date_from/date_to,
                // период всё равно будет верным — просто загрузка окажется дольше.
                var sales = _lastAllSales
                    .Where(s => s.CreatedAt.Date >= from.Date && s.CreatedAt.Date <= to.Date)
                    .ToList();

                AssignFallbackReceiptNumbers(sales);

                // 2026-11-01 (В-27, аудит «10 из 10»): сервер не дал ни сводки, ни чеков (ответил 500 без исключения) — раньше экран
                // показывал пустые плитки без пояснения. Теперь — «Сервер недоступен».
                if (_serverSummary is null && sales.Count == 0)
                    ErrorMessage = ServerOfflineNotice.Text;

                // Возвраты — с сервера (GET api/main/pos/returns/, полные и частичные); журнал
                // удалений строк корзины — только если сервер список не дал (см. FetchRefundsAsync).
                var refunds = await refundsTask;

                UpdateCollections(sales, refunds);
                // 2026-10-18, ревью объединения: «Чеки» и «Скидки» — из этого же списка, без второго списка с сервера.
                token.ThrowIfCancellationRequested();
                ShowMergedSales(sales, from, to);
                // 2026-09-17: базовые показатели (выручка/чеки/наличные/безнал) считаются сразу из
                // уже полученных sales/refunds и не зависят от построчных данных чеков — раньше
                // UpdateStats ждала полной загрузки LoadTopItemsAsync (нужна для маржи), из-за чего
                // ВСЕ показатели, включая самые простые, оставались пустыми на весь долгий период
                // построчной загрузки. Теперь базовые показатели видны сразу (маржа/чистая прибыль
                // тут ещё по данным ПРЕДЫДУЩЕГО периода/пустые при первой загрузке), а после
                // LoadTopItemsAsync UpdateStats вызывается повторно и обновляет уже настоящую маржу.
                // 2026-09-25, сверка с сайтом: выручка, способы оплаты и прибыль — только по
                // оплаченным чекам (SaleItem.CountsAsRevenue); список и история — по всем.
                DailyRevenueNote.Text = Tr.T("без продаж в долг", "карызга сатуусуз", "excluding sales on credit", "veresiye satışlar hariç", "qarzga sotuvlarsiz");
            var revenueSales = sales.Where(s => s.CountsAsRevenue).ToList();
                // Выручка, чеки, оплаты, возвраты и прибыль — цифры сайта (см. ServerSalesSummary,
                // получена выше); null — считаем сами по списку.
                SalesTileRules.Show(FinReceiptsDebtNote, sales.Select(s => (s.Status, s.TotalAmount))); // 2026-11-07 (Б-C1): «+ в долг: N на S сом»
                UpdateStats(revenueSales, refunds);
                _listPending = false;
                UpdateCharts(revenueSales);
                UpdateHistory(sales, refunds);
                // Топ товаров и рекомендации нужны только вкладкам «Аналитика» и «Товары». В
                // «Финансах» программы владельца их нет (они в разделе «Аналитика», 2026-09-27).
                if (wantProducts)
                {
                    // Топ с сервера уже показан выше; здесь — только запасной путь без вкладки «Товары».
                    if (serverFacts == null && revenueSales.Count <= MaxPerReceiptFallback)
                    {
                        await LoadTopItemsAsync(revenueSales, token);
                    }
                    else if (serverFacts == null)
                    {
                        // 2026-09-29, стресс-тест: вкладка «Товары» не ответила, а чеков тысячи — запрос
                        // на КАЖДЫЙ чек (за год — десятки тысяч) шёл бы часами и сам вызывал бы 429.
                        // Топ остаётся пустым до следующего «Обновить».
                        PosLogger.Log($"Финансы: топ товаров не построен — вкладка «Товары» не ответила, а чеков {revenueSales.Count}.", "WARNING");
                        _lastSaleLineFacts = new List<SaleLineFact>();
                        UpdateTopItems(_lastSaleLineFacts);
                        DayInsights.Clear();
                    }

                    UpdateStats(revenueSales, refunds);
                }

                if (_salesListTruncated)
                    ErrorMessage = ListTruncatedNotice;
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
                    _mergedSales?.EndFinanceLoad(); // 2026-10-18: признак загрузки во вкладке «Чеки»
                    RefreshOwnerProductsTabIfShown();
                }
                currentCts.Dispose();
            }
        }

        /// <summary>2026-09-29: не больше стольких чеков грузим по одному, когда сервер не дал вкладку «Товары».</summary>
        private const int MaxPerReceiptFallback = 2000;

        private async Task LoadTopItemsAsync(List<SaleItem> sales, CancellationToken token)
        {
            var facts = await FetchSaleLineFactsAsync(sales, token);
            _lastSaleLineFacts = facts;
            UpdateTopItems(facts);
            UpdateDayOfWeekInsights(facts);
        }

        /// <summary>2026-09-28: проданные за дни [from; to] товары — вкладка «Товары» сайта
        /// (analytics/market/?tab=products, один запрос вместо запроса на каждый чек). Каждый товар —
        /// один «факт» с датой <paramref name="factDate"/>; себестоимость — закупочная цена сервера.
        /// null — сервер вкладку не дал (нет связи или нет доступа).</summary>
        private static async Task<List<SaleLineFact>> FetchServerProductFactsAsync(
            DateTime from, DateTime to, DateTime factDate, CancellationToken token)
        {
            try
            {
                var figures = await AnalyticsReportData.ServerFigures.FetchAsync(App.SalesApi, from, to, full: false, token)
                    .ConfigureAwait(false);
                return figures?.Products
                    .Select(p => new SaleLineFact(factDate, p.Name, (decimal)p.Revenue,
                        (int)Math.Round(p.Quantity, MidpointRounding.AwayFromZero), (decimal)(p.PurchasePrice * p.Quantity)))
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Товары периода с сервера не получены: {ex.Message}", "WARNING");
                return null;
            }
        }

        /// <summary>Товары сайта по дням — для рекомендаций по дням недели. Прошедшие дни не
        /// меняются и запоминаются до закрытия окна.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<DateTime, List<SaleLineFact>> _dayProductFacts = new();
        private CancellationTokenSource _insightsCts;

        /// <summary>Рекомендации по дням недели (2026-09-28). Раньше они строились из строк каждого
        /// чека периода — тех самых сотен отдельных запросов. Теперь — из вкладки «Товары» сайта за
        /// каждый день (не больше 31 последнего дня периода, по 3 запроса сразу), в фоне: плитки,
        /// графики и топ уже показаны и не ждут этого.</summary>
        private async Task LoadDayInsightsAsync(DateTime from, DateTime to, List<SaleLineFact> wholePeriod)
        {
            _insightsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _insightsCts = cts;
            var token = cts.Token;
            try
            {
                if (from.Date == to.Date)
                {
                    UpdateDayOfWeekInsights(wholePeriod);
                    return;
                }

                var first = to.Date.AddDays(-30) > from.Date ? to.Date.AddDays(-30) : from.Date;
                var days = Enumerable.Range(0, (to.Date - first).Days + 1).Select(i => first.AddDays(i)).ToList();
                using var gate = new SemaphoreSlim(3);

                async Task<List<SaleLineFact>> OneDayAsync(DateTime day)
                {
                    if (day < ServerClock.Today && _dayProductFacts.TryGetValue(day, out var cached))
                        return cached;
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var facts = await FetchServerProductFactsAsync(day, day, day, token).ConfigureAwait(false);
                        if (facts == null)
                            return new List<SaleLineFact>();
                        if (day < ServerClock.Today)
                            _dayProductFacts[day] = facts;
                        return facts;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }

                var perDay = await Task.WhenAll(days.Select(OneDayAsync)).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                    UpdateDayOfWeekInsights(perDay.SelectMany(f => f).ToList());
            }
            catch (OperationCanceledException)
            {
                // Пришла новая загрузка или окно закрыто.
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Рекомендации по дням недели не построены: {ex.Message}", "WARNING");
            }
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
                    var json = await SaleDetailCache.GetWithRetryAsync(sale.Id, token);
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
                    PosLogger.Log($"Finance receipt aggregation skipped ({sale.Id}): {SaleDetailCache.Describe(ex)}", "WARNING");
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

        // Названия дней — на языке интерфейса, поэтому массив собирается при каждом обращении.
        private static (DayOfWeek Day, string Name)[] WeekDayOrder => new (DayOfWeek Day, string Name)[]
        {
            (DayOfWeek.Monday, Tr.T("Понедельник", "Дүйшөмбү", "Monday", "Pazartesi", "Dushanba")),
            (DayOfWeek.Tuesday, Tr.T("Вторник", "Шейшемби", "Tuesday", "Salı", "Seshanba")),
            (DayOfWeek.Wednesday, Tr.T("Среда", "Шаршемби", "Wednesday", "Çarşamba", "Chorshanba")),
            (DayOfWeek.Thursday, Tr.T("Четверг", "Бейшемби", "Thursday", "Perşembe", "Payshanba")),
            (DayOfWeek.Friday, Tr.T("Пятница", "Жума", "Friday", "Cuma", "Juma")),
            (DayOfWeek.Saturday, Tr.T("Суббота", "Ишемби", "Saturday", "Cumartesi", "Shanba")),
            (DayOfWeek.Sunday, Tr.T("Воскресенье", "Жекшемби", "Sunday", "Pazar", "Yakshanba")),
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
                lines.Add(Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + $": {product.Title}");
                if (!string.IsNullOrWhiteSpace(product.Barcode))
                    lines.Add(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod") + $": {product.Barcode}");
                if (!string.IsNullOrWhiteSpace(product.Category))
                    lines.Add(Tr.T("Категория", "Категория", "Category", "Kategori", "Toifa") + $": {product.Category}");
                if (!string.IsNullOrWhiteSpace(product.Brand))
                    lines.Add(Tr.T("Бренд", "Бренд", "Brand", "Marka", "Brend") + $": {product.Brand}");
                if (!string.IsNullOrWhiteSpace(product.Unit))
                    lines.Add(Tr.T("Ед. изм.", "Өлч. бирд.", "Unit", "Birim", "O'lchov birligi") + $": {product.Unit}");
                lines.Add(Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx") + $": {product.PriceLine}");
                lines.Add(Tr.T("Остаток на складе", "Кампадагы калдык", "In stock", "Stok", "Ombordagi qoldiq") + $": {product.Quantity:0.###} {product.Unit}");
                lines.Add(Tr.T("Тип", "Түрү", "Type", "Tür", "Turi") + ": " + (product.IsWeighted
                    ? Tr.T("Весовой", "Салмактуу", "By weight", "Tartılı", "Vaznli")
                    : Tr.T("Штучный", "Даана", "Piece", "Adet", "Dona")));
            }
            else
            {
                lines.Add(Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + $": {insight.ProductName}");
                lines.Add(Tr.T("Не найден в текущем каталоге кассы (возможно, переименован или удалён).",
                    "Кассанын учурдагы каталогунан табылган жок (аты өзгөртүлгөн же өчүрүлгөн болушу мүмкүн).",
                    "Not found in the till's current catalog (it may have been renamed or deleted).",
                    "Kasanın mevcut kataloğunda bulunamadı (yeniden adlandırılmış veya silinmiş olabilir).",
                    "Kassaning joriy katalogida topilmadi (nomi o'zgartirilgan yoki o'chirilgan bo'lishi mumkin)."));
            }

            lines.Add("");
            var dayLower = insight.DayName.ToLowerInvariant();
            lines.Add(Tr.T($"Продажи ({dayLower}): {insight.Quantity} шт. на {insight.Revenue:N2} сом",
                $"Сатуулар ({dayLower}): {insight.Quantity} даана, {insight.Revenue:N2} сом",
                $"Sales ({insight.DayName}): {insight.Quantity} pcs for {insight.Revenue:N2} som",
                $"Satışlar ({dayLower}): {insight.Quantity} adet, {insight.Revenue:N2} som",
                $"Sotuvlar ({dayLower}): {insight.Quantity} dona, {insight.Revenue:N2} so'm"));

            PosDialogs.Info(this, string.Join("\n", lines), Tr.T("Информация о товаре", "Товар жөнүндө маалымат", "Product information", "Ürün bilgisi", "Mahsulot haqida ma'lumot"));
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
        /// <summary>Отменённые на сервере чеки последней загрузки (см. FetchSalesPageAsync).</summary>
        private readonly HashSet<string> _canceledSaleIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>2026-09-29, стресс-тест: список последней загрузки упёрся в предел страниц (50 000
        /// чеков) — в нём не все чеки периода. Плитки всё равно полные (сводка сервера), а графики
        /// оплат и выручки по дням строятся по сводке сервера (см. UpdateCharts); окно говорит об
        /// этом плашкой, а не показывает молча числа по части чеков.</summary>
        private bool _salesListTruncated;

        /// <summary>2026-09-29: список чеков текущей загрузки ещё качается — графики оплат и выручки
        /// по дням пока по сводке сервера (см. LoadDataAsync, UpdateCharts).</summary>
        private bool _listPending;

        private static string ListTruncatedNotice => Tr.T(
            "Период очень большой: список чеков, график по часам и графики динамики — по последним 50 000 чекам. Плитки (выручка, чеки, оплаты, прибыль), разбивка по оплатам, выручка по дням и ABC — полные, с сервера.",
            "Мезгил өтө чоң: чектердин тизмеси, саат боюнча график жана динамика графиктери — акыркы 50 000 чек боюнча. Плиткалар (түшүм, чектер, төлөмдөр, пайда), төлөм түрлөрү, күн боюнча түшүм жана ABC — толук, серверден.",
            "The period is very large: the receipt list, the hourly chart and the trend charts are based on the latest 50,000 receipts. The tiles (revenue, receipts, payments, profit), the payment split, daily revenue and ABC are complete, from the server.",
            "Dönem çok büyük: fiş listesi, saatlik grafik ve eğilim grafikleri son 50.000 fişe göre. Kutucuklar (ciro, fişler, ödemeler, kâr), ödeme dağılımı, günlük ciro ve ABC eksiksiz, sunucudan.",
            "Davr juda katta: cheklar ro'yxati, soatlik grafik va dinamika grafiklari — oxirgi 50 000 ta chek bo'yicha. Plitkalar (tushum, cheklar, to'lovlar, foyda), to'lov turlari, kunlik tushum va ABC — to'liq, serverdan.");

        /// <param name="firstBatch">2026-09-29, «увеличь скорость загрузки аналитики»: вызывается с первыми
        /// загруженными страницами (новые чеки), если за ними есть ещё, — вкладка «Продажи» показывает
        /// их, пока догружается остальное.</param>
        private async Task<List<SaleItem>> FetchAllSalesAsync(DateTime from, DateTime to, CancellationToken token,
            Action<List<SaleItem>> firstBatch = null)
        {
            _salesListTruncated = false;
            lock (_canceledSaleIds)
                _canceledSaleIds.Clear();
            // 2026-09-28: страницы по 500 строк (сервер отдаёт до 500) и по 3 одновременно; предел —
            // 100 страниц (50 000 чеков) вместо 60 по 80 (4 800), на котором у больших магазинов
            // обрезался «Месяц». Страница за последней повторяет последнюю — её строки уже видены.
            const int pageSize = 500;
            const int maxPages = 100;
            const int parallelPages = 3;
            var result = new List<SaleItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var firstPage = 1; firstPage <= maxPages; firstPage += parallelPages)
            {
                token.ThrowIfCancellationRequested();
                var batch = await Task.WhenAll(Enumerable
                    .Range(firstPage, Math.Min(parallelPages, maxPages - firstPage + 1))
                    .Select(page => FetchSalesPageAsync(page, pageSize, from, to, token)));

                var done = false;
                foreach (var (pageItems, rawIds) in batch)
                {
                    // Новых строк (включая отменённые — они отсеиваются внутри FetchSalesPageAsync,
                    // 2026-09-25) нет — это повтор последней страницы или пустой ответ.
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
                PosLogger.Log($"Финансы: список чеков {from:dd.MM.yyyy}–{to:dd.MM.yyyy} обрезан на {result.Count} строках (предел {maxPages} страниц).", "FINANCE");
            return result;
        }

        /// <summary>Одна страница продаж периода. RawIds — id ВСЕХ строк страницы, включая отменённые
        /// (2026-09-28: по ним FetchAllSalesAsync узнаёт повтор последней страницы).</summary>
        private async Task<(List<SaleItem> Items, List<string> RawIds)> FetchSalesPageAsync(
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
            var rawIds = new List<string>(raw.Count);
            foreach (JsonElement el in raw)
            {
                var item = new SaleItem();
                if (el.TryGetProperty("id", out var idProp))
                    item.Id = idProp.ToString() ?? "";
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
                if (el.TryGetProperty("created_at", out var dateProp) &&
                    TryServerTime(dateProp.GetString(), out var dt))
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
                // 2026-10-18: «Продажи и финансы» — скидка и первый товар чека, как их читает список «Продаж»: вкладка «Чеки» и плитка
                // «Скидки» берут этот же список, а не качают его второй раз (FinanceWindow.Merged.cs).
                if (el.TryGetProperty("discount_total", out var discProp))
                    item.DiscountTotal = ParseDecimal(discProp);
                if (el.TryGetProperty("first_item_name", out var firstItem) && firstItem.ValueKind == JsonValueKind.String)
                    item.FirstItemName = firstItem.GetString() ?? "";
                // 2026-11-12 (возврат чеков в программе владельца): те же поля, что читает список «Продаж» (SalesWindow) — пометки возврата и фильтры во «Чеках».
                if (el.TryGetProperty("return_status", out var retProp) && retProp.ValueKind == JsonValueKind.String)
                    item.ReturnStatus = retProp.GetString();
                item.Cashier = CartDisplayHelper.TryCashierName(el);
                if (el.TryGetProperty("client_name", out var cnProp) && cnProp.ValueKind == JsonValueKind.String)
                    item.ClientName = cnProp.GetString();
                // ReceiptNumber остаётся пустым, если сервер не прислал ни одного из известных полей —
                // UpdateCollections подставит читаемый порядковый номер вместо «сырого» GUID.
                result.Add(item);
            }
            return (result, rawIds);
        }

        /// <summary>2026-09-28: время чека — по часам сервера (Бишкек, +06:00), а не по часовому поясу
        /// компьютера. Сайт и сервер делят период на дни по времени Бишкека; на кассе с другим поясом
        /// (например, +05:00 или UTC) чеки около полуночи попадали в соседний день — «Сегодня» и график
        /// по дням расходились с сайтом. На компьютере с поясом +06:00 ничего не меняется.</summary>
        internal static bool TryServerTime(string text, out DateTime value)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                value = stamp.DateTime;
                return true;
            }

            value = default;
            return false;
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
    }
}
