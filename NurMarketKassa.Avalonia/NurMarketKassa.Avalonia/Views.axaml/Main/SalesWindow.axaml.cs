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
    public partial class SalesWindow : Window, INotifyPropertyChanged
    {
        private readonly ObservableCollection<SaleItem> _sales = new();
        private readonly ObservableCollection<RefundItem> _refunds = new();
        private readonly CollectionViewSource _salesViewSource = new();
        private DateTime _historyFrom = DateTime.Today;
        private DateTime _historyTo = DateTime.Today;
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
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
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

            FullscreenHelper.Apply(this);

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss   dd.MM.yyyy");
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
            PosDataEvents.SalesChanged += OnSalesChangedExternally;

            FromPicker.SelectedDate = new DateTimeOffset(_historyFrom);
            ToPicker.SelectedDate = new DateTimeOffset(_historyTo);
            CustomDatePill.IsChecked = false;
            CustomDatePanel.IsVisible = false;
            await LoadDataAsync(_historyFrom, _historyTo);
        }

        private void Back_Click(object sender, RoutedEventArgs e) => Close();

        private async void Refresh_Click(object sender, RoutedEventArgs e) =>
            await LoadDataAsync(_historyFrom, _historyTo);

        // ── Загрузка данных ──

        /// <summary>Пересчитывает ABC за выбранный период. Считается локально, поэтому работает
        /// и без интернета — как и остальная аналитика. Расчёт уводим в Task.Run: он читает всю
        /// историю продаж за период, и на большом магазине это заметно подвесило бы окно.</summary>
        private async Task RefreshAbcAsync(DateTime from, DateTime to, CancellationToken token)
        {
            try
            {
                var data = await Task.Run(() => AnalyticsReportData.Build(from, to), token)
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

        private void OnSalesChangedExternally()
        {
            _liveCts?.Cancel();
            _liveCts?.Dispose();
            var cts = new System.Threading.CancellationTokenSource();
            _liveCts = cts;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(1200, cts.Token).ConfigureAwait(false);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await LoadDataAsync(_historyFrom, _historyTo));
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

                // ABC считается по локальной истории продаж и от сервера не зависит, поэтому
                // запускаем его до сетевых запросов: даже если сервер не ответит, таблица
                // будет заполнена.
                await RefreshAbcAsync(from, to, token);

                // 2026-09-10: в автономном/офлайн режиме нет сервера, который отдал бы список
                // продаж — единственная правда о продажах лежит в локальной очереди чеков
                // (OfflinePendingSalesStore), куда PosCheckoutService пишет КАЖДУЮ продажу этого
                // режима (и они никогда оттуда не уходят — см. OfflineSaleEntry.IsAutonomous).
                if (OfflineModeHelper.UseLocalOperations)
                {
                    LoadLocalData(from, to, token);
                    return;
                }

                var sales = await FetchAllSalesAsync(from, to, token);

                AssignFallbackReceiptNumbers(sales);

                // "is_refund" — несуществующее поле в реальном API (сервер никогда его не отдаёт,
                // это всегда false), поэтому возвраты нельзя достать фильтром по списку продаж —
                // единственный реальный след возврата оплаченной позиции в API это журнал удалений
                // из корзины (см. FetchCartItemDeletionsAsync).
                var refunds = await FetchCartItemDeletionsAsync(from, to, token);

                UpdateCollections(sales, refunds);
                UpdateStats(sales, refunds);
                await LoadTopItemsAsync(sales, token);
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

            ErrorMessage = sales.Count > 0
                ? "Нет связи с сервером — показаны данные этой кассы за выбранный период."
                : "Нет связи с сервером, а локальных продаж за выбранный период нет.";
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
                // все 60 итераций, набив список копиями одной и той же страницы.
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

            // Сервер фильтрует по дате сам, но подстраховываемся: если он проигнорировал
            // date_from/date_to, период всё равно будет соблюдён.
            return result
                .Where(s => s.CreatedAt.Date >= from.Date && s.CreatedAt.Date <= to.Date)
                .ToList();
        }

        /// <summary>Отменённые на сервере чеки последней загрузки (см. FetchSalesPageAsync).</summary>
        private readonly HashSet<string> _canceledSaleIds = new(StringComparer.OrdinalIgnoreCase);

        private async Task<List<SaleItem>> FetchSalesPageAsync(
            int page, int pageSize, DateTime from, DateTime to, CancellationToken token)
        {
            if (page == 1)
            {
                lock (_canceledSaleIds)
                    _canceledSaleIds.Clear();
            }

            // date_to на сервере НЕ включает свой день, поэтому передаём следующий.
            var raw = await App.SalesApi.PosSalesListAsync(
                page, pageSize, null, token,
                dateFrom: from.Date,
                dateToExclusive: to.Date.AddDays(1));
            token.ThrowIfCancellationRequested();
            var result = new List<SaleItem>(raw.Count);
            foreach (JsonElement el in raw)
            {
                var item = new SaleItem();
                if (el.TryGetProperty("id", out var idProp)) item.Id = idProp.ToString() ?? "";
                // 2026-09-25: полностью возвращённый чек сервер помечает «canceled», но оставляет в
                // списке с прежней суммой. В выручку, наличные и число чеков он не входит.
                if (el.TryGetProperty("status", out var statusProp)
                    && string.Equals(statusProp.GetString(), "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_canceledSaleIds)
                        _canceledSaleIds.Add(item.Id);
                    continue;
                }
                if (el.TryGetProperty("created_at", out var dateProp) && DateTime.TryParse(dateProp.GetString(), out var dt)) item.CreatedAt = dt;
                item.ReceiptNumber = TryReceiptNumber(el) ?? "";
                if (el.TryGetProperty("total", out var totalProp)) item.TotalAmount = ParseDecimal(totalProp);
                if (el.TryGetProperty("discount_total", out var discProp)) item.DiscountTotal = ParseDecimal(discProp);
                if (el.TryGetProperty("payment_method", out var pmProp)) item.PaymentMethod = pmProp.GetString() ?? "";
                if (el.TryGetProperty("is_refund", out var rfProp) && ParseBool(rfProp)) item.IsRefund = true;
                if (el.TryGetProperty("refund_reason", out var rrProp)) item.RefundReason = rrProp.GetString();
                // ReceiptNumber остаётся пустым, если сервер не прислал ни одного из известных полей —
                // AssignFallbackReceiptNumbers подставит читаемый порядковый номер вместо «сырого» GUID.
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
        }

        private void UpdateCollections(List<SaleItem> sales, List<RefundItem> refunds)
        {
            _sales.Clear();
            foreach (var s in sales) _sales.Add(s);
            _refunds.Clear();
            foreach (var r in refunds) _refunds.Add(r);
        }

        /// <summary>Журнал удалений позиций из корзины (product/quantity/who/when) — единственный
        /// след возвратов по оплаченным чекам в реальном API (сервер не отдаёт "is_refund" вовсе).
        /// Сумма возврата — оценка: текущая цена товара из каталога × количество (не историческая
        /// цена на момент продажи, но ближе к правде, чем всегда показывать 0 в статистике).</summary>
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
                    if (el.TryGetProperty("created_at", out var dateProp) &&
                        DateTime.TryParse(dateProp.GetString(), out var dt))
                        item.CreatedAt = dt;
                    if (item.CreatedAt.Date < from.Date || item.CreatedAt.Date > to.Date)
                        continue;

                    string productName = "";
                    if (el.TryGetProperty("product_name", out var nameProp))
                        productName = nameProp.GetString() ?? "";

                    decimal quantity = 0;
                    if (el.TryGetProperty("quantity", out var qtyProp))
                        quantity = ParseDecimal(qtyProp);

                    if (quantity > 0 && el.TryGetProperty("product", out var productIdProp) &&
                        productIdProp.ValueKind == JsonValueKind.String)
                    {
                        var productId = productIdProp.GetString();
                        var product = CatalogCacheService.Products.FirstOrDefault(p =>
                            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                        if (product != null)
                            item.TotalAmount = (decimal)LocalCartService.ParsePrice(product.PriceLine) * quantity;
                    }

                    item.ReceiptNumber = productName;
                    item.Reason = quantity > 0
                        ? $"{productName} ×{quantity.ToString("0.###", CultureInfo.InvariantCulture)}"
                        : productName;
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

        /// <summary>Выгрузка аналитики за выбранный период в Excel и в Word.
        ///
        /// Период берётся ровно тот, что выбран на экране, — чтобы цифры в файле совпадали с
        /// теми, что кассир видит перед собой. Отчёт строится из локальных данных кассы и не
        /// требует интернета: выгрузку часто просят тогда, когда связи уже нет.</summary>
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

        private void UpdateStats(List<SaleItem> sales, List<RefundItem> refunds)
        {
            decimal totalSales = sales.Sum(s => s.TotalAmount);
            decimal totalRefunds = refunds.Sum(r => Math.Abs(r.TotalAmount));
            decimal cashSales = sales.Where(s => (s.PaymentMethod?.ToUpper() == "CASH") || (s.PaymentMethod?.ToLower().Contains("нал") ?? false)).Sum(s => s.TotalAmount);
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
            var returnsInRevenue = 0m;
            try
            {
                var events = ShiftEventsStore.TotalsBetween(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime());
                if (events.TryGetValue(ShiftEventsStore.KindReturn, out var returned))
                    realReturns = (decimal)returned;
                // Из выручки вычитаются только частичные возвраты: полностью возвращённый чек
                // сервер уже отменил, и он в выручку не вошёл (2026-09-25).
                HashSet<string> canceled;
                lock (_canceledSaleIds)
                    canceled = new HashSet<string>(_canceledSaleIds, StringComparer.OrdinalIgnoreCase);
                returnsInRevenue = (decimal)ShiftEventsStore.ReturnsBetweenExcluding(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime(),
                    canceled);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Возвраты для показателей не прочитаны: {ex.Message}", "WARNING");
            }

            // 2026-09-25: продажи в долг — не безналичные. Раньше «Безнал» считался как «всё, что не
            // наличные», и долг клиента попадал туда же.
            decimal debtSales = sales
                .Where(s => string.Equals(s.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.TotalAmount);
            decimal nonCash = totalSales - cashSales - debtSales;
            int totalCount = sales.Count;
            decimal net = totalSales - returnsInRevenue;
            decimal avg = totalCount > 0 ? net / totalCount : 0m;

            TotalSalesText.Text = $"{net:N2} сом";
            TotalRefundsText.Text = $"{realReturns:N2} сом";
            CashText.Text = $"{cashSales:N2} сом";
            NonCashText.Text = $"{nonCash:N2} сом";
            AvgReceiptText.Text = $"{avg:N2} сом";
            ReceiptCountText.Text = totalCount.ToString();

            // Скидки и оплата бонусами (2026-09-22). Берутся из локальной таблицы кассы, а не с
            // сервера: сервер отдаёт скидку чека одной суммой, в которой бонусы неотличимы от
            // обычной скидки, а про баллы он не знает вовсе.
            try
            {
                var adjustments = ClientLoyaltyStore.AdjustmentsBetween(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime());
                // 2026-09-25: скидки — из discount_total самих чеков (там и скидки на строку, и на
                // весь чек, и чеки других касс), за вычетом оплаты бонусами: сервер пишет её в ту же
                // сумму. Локальная таблица знает только скидки на весь чек этой кассы, поэтому плитка
                // показывала 0,00, когда скидка была на строку.
                var serverDiscounts = sales.Sum(s => s.DiscountTotal);
                var discounts = Math.Max(0d, (double)serverDiscounts - adjustments.PointsRedeemed);
                DiscountsText.Text = $"{discounts:N2} сом";
                PointsRedeemedText.Text = $"{adjustments.PointsRedeemed:N2} сом";
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Shift adjustments read failed: {ex.Message}", "WARNING");
                DiscountsText.Text = "—";
                PointsRedeemedText.Text = "—";
            }

            // 2026-09-17: реальные себестоимость/маржа считаются в LoadTopItemsAsync (нужны
            // закупочные цены по каждой проданной позиции — раньше здесь было costOfGoods =
            // totalSales * 0.6m, поэтому маржа ВСЕГДА показывала ровно 40% независимо от
            // реальных продаж). Пока построчные данные не загрузились — плейсхолдер загрузки.
            NetProfitText.Text = totalSales > 0 ? "…" : "—";
            MarginPercentText.Text = totalSales > 0 ? "…" : "—";
        }

        /// <summary>2026-09-17: раньше здесь были ДВЕ проблемы разом:
        /// 1) N+1 — по одному последовательному запросу PosSaleGetAsync НА КАЖДЫЙ чек периода,
        ///    один за другим (при сотнях чеков — сотни round-trip подряд, отсюда "аналитика
        ///    очень медленно грузится"). Теперь запросы идут параллельно с ограничением
        ///    одновременных (SemaphoreSlim), а не строго по одному.
        /// 2) Маржа в UpdateStats считалась как totalSales * 0.6m (фиктивная себестоимость),
        ///    поэтому ВСЕГДА показывала ровно 40% вне зависимости от реальных продаж. Раз уж
        ///    здесь и так построчно загружаются проданные позиции — заодно считаем реальную
        ///    себестоимость по закупочной цене каждого товара (PurchasePrice из локального
        ///    каталога) и настоящую маржу.</summary>
        private async Task LoadTopItemsAsync(List<SaleItem> sales, CancellationToken token)
        {
            using var gate = new SemaphoreSlim(8);

            // Один снимок каталога на весь проход: раньше на КАЖДУЮ строку КАЖДОГО чека товар
            // искался перебором всего каталога (то же исправление, что уже сделано в «Финансах»).
            var catalogById = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in CatalogCacheService.Products)
            {
                if (!string.IsNullOrEmpty(p.Id))
                    catalogById[p.Id] = p;
            }

            async Task<(decimal revenue, decimal cost, List<(string name, decimal total, int qty)> lines)> FetchOneAsync(SaleItem sale)
            {
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var json = await SaleDetailCache.GetAsync(sale.Id, token);
                    var lines = new List<(string, decimal, int)>();
                    decimal revenue = 0m, cost = 0m;
                    if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in items.EnumerateArray())
                        {
                            string name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                            decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                            // "price"/"total" aren't real fields on a sale line — CartDisplayHelper
                            // (used successfully during checkout) reads the price from "unit_price".
                            // Mirror that instead of guessing field names again.
                            decimal total = line.TryGetProperty("line_total", out var lt) ? ParseDecimal(lt)
                                : line.TryGetProperty("amount", out var am) ? ParseDecimal(am)
                                : line.TryGetProperty("total", out var t) ? ParseDecimal(t)
                                : (decimal)CartDisplayHelper.UnitPrice(line) * qty;

                            revenue += total;
                            var productId = CartDisplayHelper.TryProductId(line);
                            NurMarketKassa.Models.Pos.CatalogProductTileVm? product = null;
                            if (!string.IsNullOrEmpty(productId))
                                catalogById.TryGetValue(productId, out product);
                            if (product != null && product.PurchasePrice > 0)
                                cost += (decimal)product.PurchasePrice * qty;

                            lines.Add((name, total, (int)qty));
                        }
                    }

                    return (revenue, cost, lines);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Sales receipt aggregation skipped: {ex.GetType().Name}", "WARNING");
                    return (0m, 0m, new List<(string, decimal, int)>());
                }
                finally
                {
                    gate.Release();
                }
            }

            var results = await Task.WhenAll(sales.Select(FetchOneAsync));
            token.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, (decimal revenue, int qty)>();
            decimal totalRevenueFromItems = 0m;
            decimal totalCostFromItems = 0m;
            foreach (var r in results)
            {
                totalRevenueFromItems += r.revenue;
                totalCostFromItems += r.cost;
                foreach (var (name, total, qty) in r.lines)
                {
                    dict[name] = dict.TryGetValue(name, out var existing)
                        ? (existing.revenue + total, existing.qty + qty)
                        : (total, qty);
                }
            }

            var top10 = dict.OrderByDescending(kv => kv.Value.revenue).Take(10);
            TopItems.Clear();
            foreach (var kv in top10)
                TopItems.Add(new TopItem { ProductName = kv.Key, Revenue = kv.Value.revenue, Quantity = kv.Value.qty });

            if (totalRevenueFromItems > 0)
            {
                var netProfit = totalRevenueFromItems - totalCostFromItems;
                NetProfitText.Text = $"{netProfit:N2} сом";
                MarginPercentText.Text = $"{netProfit / totalRevenueFromItems * 100:F1}%";
            }
            else
            {
                NetProfitText.Text = "—";
                MarginPercentText.Text = "—";
            }
        }

        // ── Фильтрация и поиск ──
        private void FilterSales(object sender, FilterEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_searchFilter)) { e.Accepted = true; return; }
            if (e.Item is SaleItem s) e.Accepted = s.ReceiptNumber?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) == true;
            else e.Accepted = false;
        }

        private async void SearchReceiptBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchFilter = SearchReceiptBox.Text;
            _searchCts?.Cancel();
            var currentCts = new CancellationTokenSource();
            _searchCts = currentCts;
            try
            {
                await Task.Delay(300, currentCts.Token);
                SalesView.Refresh();
            }
            catch (TaskCanceledException)
            {
                PosLogger.Log("Sales search debounce superseded.", "DEBUG");
            }
            finally
            {
                if (ReferenceEquals(_searchCts, currentCts))
                    _searchCts = null;
                currentCts.Dispose();
            }
        }

        private async void SalesGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (SalesGrid.SelectedItem is SaleItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        private async Task ShowReceiptDetailsByIdAsync(string receiptId, string receiptNumber)
        {
            if (string.IsNullOrEmpty(receiptId)) return;
            try
            {
                JsonElement json;
                if (OfflineModeHelper.UseLocalOperations)
                {
                    var entry = OfflinePendingSalesStore.TryGetById(receiptId);
                    if (entry is null)
                    {
                        ErrorMessage = "Локальный чек не найден.";
                        return;
                    }
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
                    json = doc.RootElement.Clone();
                }
                else
                {
                    json = await App.SalesApi.PosSaleGetAsync(receiptId, CancellationToken.None);
                }

                // 2026-09-19, временная диагностика по просьбе владельца ("печать чека с
                // мобильного через LAN-принтер") — нужно увидеть сырой ответ сервера и найти
                // поле, отличающее продажу с мобильного приложения от продажи в этой кассе
                // (source/channel/device и т.п. — сейчас в коде такое поле нигде не читается).
                // Снять после того, как найдём нужное поле и сузим до постоянной проверки.
                PosLogger.Log($"[DEBUG] Receipt {receiptNumber} raw JSON: {json.GetRawText()}", "SALE_DEBUG");

                var items = new List<string>();
                // Сервер называет поле цены "unit_price" (не "price") и использует разные имена
                // для суммы строки в разных ответах — прямое чтение "price"/"total" молча давало
                // 0.00 для обоих (кассир видел "1,000 × 0,00 = 0,00"). CartDisplayHelper уже умеет
                // это надёжно разбирать (тот же разбор, что и в диалоге возврата).
                // Предпросмотр строится тем же построителем, что и печать: раньше это были
                // два разных формата, и кассир видел на экране одно, а на бумаге другое.
                var receiptDiscount = ReadSaleDiscount(json);
                var receiptTotal = ReadSaleTotal(json);
                PopupReceiptText.Text = SaleReceiptTextBuilder.Build(
                    json, receiptNumber, receiptDiscount, receiptTotal);

                PopupTitle.Text = "Чек " + receiptNumber;
                _currentReceiptJson = json;
                _currentReceiptNumber = receiptNumber;
                ReceiptDetailsPopup.IsOpen = true;
            }
            catch (Exception ex) { ErrorMessage = "Не удалось загрузить детали чека: " + ex.Message; }
        }

        /// <summary>Скидка чека, как её записал сервер. Обычная скидка и списанные бонусы
        /// приходят ОДНОЙ суммой — разделить их здесь нельзя, для этого есть локальная таблица
        /// (ClientLoyaltyStore.AdjustmentsBetween).</summary>
        /// <summary>Скидка по проведённой продаже.
        ///
        /// Раньше читались ТОЛЬКО поля шапки чека (discount_total / order_discount_total), и
        /// это давало живой баг: на экране кассы скидка есть, а в чеке её нет. Причина в том,
        /// что скидку фиксированной суммой сервер у кассира отклоняет пятисоткой, и касса
        /// раскладывает её по строкам чека (StagingCartService.TrySpreadDiscountOverLinesAsync).
        /// После этого в шапке ноль, а вся скидка лежит на позициях — предпросмотр и повторная
        /// печать показывали чек так, будто скидки не было вовсе.
        ///
        /// Теперь складываем обе части: скидку на чек и сумму построчных скидок.</summary>
        private static decimal ReadSaleDiscount(System.Text.Json.JsonElement json)
        {
            var header = 0m;
            foreach (var name in new[] { "discount_total", "order_discount_total" })
            {
                if (ReadDecimalProperty(json, name) is { } value && value > 0m)
                {
                    header = value;
                    break;
                }
            }

            var lines = 0m;
            foreach (var item in CartDisplayHelper.EnumerateSaleLineItems(json))
            {
                foreach (var name in new[] { "discount_total", "line_discount", "discount" })
                {
                    if (ReadDecimalProperty(item, name) is { } value && value > 0m)
                    {
                        lines += value;
                        break;
                    }
                }
            }

            return header + lines;
        }

        /// <summary>Итог чека со стороны сервера — то, что покупатель реально заплатил.</summary>
        private static decimal? ReadSaleTotal(System.Text.Json.JsonElement json) =>
            ReadDecimalProperty(json, "total");

        private static decimal? ReadDecimalProperty(System.Text.Json.JsonElement json, string name)
        {
            if (json.ValueKind != System.Text.Json.JsonValueKind.Object
                || !json.TryGetProperty(name, out var element))
            {
                return null;
            }

            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
                System.Text.Json.JsonValueKind.String when decimal.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => null,
            };
        }

        private async void PrintReceiptAgain_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Тот же построитель, что и в предпросмотре: раньше повторная печать
                // собирала собственный, третий по счёту формат чека.
                var reprintText = SaleReceiptTextBuilder.Build(
                    _currentReceiptJson,
                    _currentReceiptNumber,
                    ReadSaleDiscount(_currentReceiptJson),
                    ReadSaleTotal(_currentReceiptJson),
                    isReprint: true);

                // В фоне: медленный принтер не подвешивает окно.
                await Task.Run(() => ReceiptPrintService.PrintReceipt("{}", receiptText: reprintText)).ConfigureAwait(true);
                ErrorMessage = "";
            }
            catch (Exception ex)
            {
                ErrorMessage = "Не удалось напечатать чек: " + ex.Message;
            }
        }

        private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true;

        private void CloseReceiptDetails_Click(object sender, RoutedEventArgs e) =>
            ReceiptDetailsPopup.IsOpen = false;

        // Быстрые даты
        private async void QuickDate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            // 2026-09-17, по репорту пользователя ("в продаже не работает спец дата") — тут
            // была незаконченная заглушка ("диалог") без реализации, кнопка ничего не делала.
            // Переиспользуем тот же диалог выбора периода, что и в «Финансы».
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
            if (sender == FromPicker) _historyFrom = FromPicker.SelectedDate?.DateTime ?? DateTime.Today;
            else if (sender == ToPicker) _historyTo = ToPicker.SelectedDate?.DateTime ?? DateTime.Today;
            await LoadDataAsync(_historyFrom, _historyTo);
        }

        // INPC
        public new event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Вспомогательные классы
        public class TopItem
        {
            public string ProductName { get; set; }
            public decimal Revenue { get; set; }
            public int Quantity { get; set; }
        }

        public class SaleItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            // 2026-09-25: дата и время строкой, а не StringFormat=HH:mm в разметке — в «Истории»
            // все чеки показывали 00:00, хотя время с сервера приходило верное.
            public string DateText => CreatedAt.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture);
            public string TimeText => CreatedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public decimal DiscountTotal { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
            public bool IsRefund { get; set; }
            public string RefundReason { get; set; }
        }

        /// <summary>Читаемая русская подпись способа оплаты — сервер отдаёт технические ключи (cash/transfer/…).</summary>
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

        public class RefundItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string Reason { get; set; } = "";
        }
    }
}
