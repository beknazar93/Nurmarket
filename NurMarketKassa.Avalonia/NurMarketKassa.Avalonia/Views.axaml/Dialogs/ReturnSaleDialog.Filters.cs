using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

// 2026-11-10 (владелец: «и фильтр в возврате!», «поиск по товару!», «возврат добавь в клиентов которые брали в долг»): фильтры списка чеков (общий SaleListFilter
// и панель SaleFilterBar — те же, что в «Продажах»), поиск по товару в составе чеков (название / штрихкод / артикул) с подсветкой и предвыбором найденной позиции,
// режим «чеки одного клиента» (карточка клиента, «Долги клиентов»: InitialClientId + статус «Долговой»).
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    /// <summary>Открыть сразу с поиском по товару (из «Продаж»: в поиске был товар).</summary>
    public string? InitialProductQuery { get; set; }

    /// <summary>Режим «чеки одного клиента»: список — продажи этого клиента (pos/sales/?client=), без «Ещё».</summary>
    public string? InitialClientId { get; set; }
    public string? InitialClientName { get; set; }

    /// <summary>Статус в фильтре при открытии (карточка должника — «Долговой»).</summary>
    public SaleListFilter.StatusKind? InitialStatus { get; set; }

    /// <summary>Сколько карточек чеков догружать для поиска по товару за один запрос поиска (общий темп запросов, кэш чеков на сеанс).</summary>
    internal const int DeepSearchLimit = 150;

    private SaleFilterBar? _filterBar;
    private TextBox? _productBox;
    private Button? _filtersToggle;
    private Border? _filterHost;
    private TextBlock? _countText;
    private SaleProductQuery? _productQuery;
    private string _deepNote = "";
    private bool _preselectOnFill;
    private CancellationTokenSource? _productDebounce;
    private CancellationTokenSource? _deepSearchCts;

    /// <summary>Строки чеков для поиска по товару: название и id товара (список, локальная история кассы, карточки чеков).</summary>
    private readonly Dictionary<string, List<(string? Name, string? ProductId)>> _saleLines = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Чеки, чей состав известен полностью (карточка чека или локальная история) — догружать не нужно.</summary>
    private readonly HashSet<string> _saleLinesComplete = new(StringComparer.OrdinalIgnoreCase);

    internal SaleListFilter ListFilter => _filterBar?.Filter ?? _fallbackFilter;
    private readonly SaleListFilter _fallbackFilter = new();

    private static string FT(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Поле «Товар», кнопка «Фильтры (N)», панель фильтров и счётчик — над таблицей чеков (вставляются в блок «1. Найдите чек»).</summary>
    private void BuildFilterUi()
    {
        if (MainAreaGrid.Children.Count == 0 || MainAreaGrid.Children[0] is not DockPanel salesBlock)
            return;
        _productBox = new TextBox
        {
            Classes = { "ModernInput" },
            Watermark = FT("Товар: название, штрихкод, артикул", "Товар: аты, штрихкод, артикул", "Product: name, barcode, SKU", "Ürün: ad, barkod, stok kodu", "Mahsulot: nomi, shtrixkod, artikul"),
        };
        _productBox.Name = "ReturnProductSearchBox";
        Avalonia.Automation.AutomationProperties.SetName(_productBox, FT("Поиск чека по товару", "Товар боюнча чек издөө", "Find a receipt by product", "Ürüne göre fiş ara", "Mahsulot bo'yicha chek qidirish"));
        _productBox.TextChanged += (_, _) => OnProductTextChanged();
        _productBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OnProductTextChanged(immediate: true);
            }
        };
        _filtersToggle = new Button { Classes = { "btn-secondary" }, Height = 44, Padding = new Thickness(12, 0), Margin = new Thickness(8, 0, 0, 0) };
        _filtersToggle.Name = "ReturnFiltersToggle";
        _filtersToggle.Click += (_, _) =>
        {
            if (_filterHost is not null)
                _filterHost.IsVisible = !_filterHost.IsVisible;
            UpdateFiltersToggle();
            FitFilterHeight();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(_productBox);
        Grid.SetColumn(_filtersToggle, 1);
        row.Children.Add(_filtersToggle);
        DockPanel.SetDock(row, Dock.Top);

        _filterBar = new SaleFilterBar(showPeriod: true) { ShowCount = false };
        _filterBar.Changed += () =>
        {
            UpdateFiltersToggle();
            ApplySalesFilter();
        };
        _filterBar.PeriodChanged += () =>
        {
            // Период — запросом к серверу (date_from / date_to): за месяц чеков больше, чем первая страница списка.
            if (string.IsNullOrWhiteSpace(InitialClientId))
                _ = LoadSalesAsync(true);
        };
        _filterBar.ResetRequested += () =>
        {
            if (_productBox is not null && !string.IsNullOrEmpty(_productBox.Text))
                _productBox.Text = "";
        };
        // 2026-11-12 (аудит Fable, В-6): панель фильтров — в прокрутке с подгоняемой высотой (FitFilterHeight): на 1024×600 она занимала 4 ряда, и от списка чеков оставалась шапка.
        _filterScroll = new ScrollViewer
        {
            Content = _filterBar,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 4 * FilterRowHeight,
        };
        _filterHost = new Border { Child = _filterScroll, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_filterHost, Dock.Top);

        _countText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6), IsVisible = false };
        _countText.Name = "ReturnFilterCount";
        _countText.Bind(TextBlock.ForegroundProperty, _countText.GetResourceObservable("BrushTextSoft"));
        DockPanel.SetDock(_countText, Dock.Top);

        // После шапки «1. Найдите чек» и строки поиска — до таблицы.
        var at = Math.Min(2, salesBlock.Children.Count);
        salesBlock.Children.Insert(at, _countText);
        salesBlock.Children.Insert(at, _filterHost);
        salesBlock.Children.Insert(at, row);
        UpdateFiltersToggle();
        // 2026-11-12 (В-6, С-16): высота панели фильтров подгоняется под окно; значок «ⓘ» получает имя для диктора
        NameLayoutControls();
        salesBlock.SizeChanged += (_, _) => FitFilterHeight();
        _countText.SizeChanged += (_, _) => FitFilterHeight();
    }

    /// <summary>Начальные значения (карточка клиента, «Продажи») — до первой загрузки списка.</summary>
    private void ApplyInitialFilters()
    {
        if (InitialStatus is { } status && _filterBar is not null)
            _filterBar.SetStatus(status);
        if (!string.IsNullOrWhiteSpace(InitialProductQuery) && _productBox is not null)
        {
            _productBox.Text = InitialProductQuery.Trim();
            RebuildProductQuery();
        }
        if (!string.IsNullOrWhiteSpace(InitialClientId))
        {
            MoreSalesButton.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(InitialClientName))
                Title = FT("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish") + " · " + InitialClientName;
        }
        UpdateFiltersToggle();
    }

    private void UpdateFiltersToggle()
    {
        if (_filtersToggle is null)
            return;
        var n = ListFilter.ActiveCount;
        var open = _filterHost?.IsVisible == true;
        _filtersToggle.Content = (open ? "▴ " : "▾ ") + FT("Фильтры", "Чыпкалар", "Filters", "Filtreler", "Filtrlar") + (n > 0 ? $" ({n})" : "");
        Avalonia.Automation.AutomationProperties.SetName(_filtersToggle, FT("Фильтры чеков", "Чектердин чыпкалары", "Receipt filters", "Fiş filtreleri", "Chek filtrlari") + (n > 0 ? $": {n}" : ""));
    }

    private void OnProductTextChanged(bool immediate = false)
    {
        _productDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _productDebounce = cts;
        _ = DebounceProductAsync(cts.Token, immediate);
    }

    private async Task DebounceProductAsync(CancellationToken ct, bool immediate)
    {
        try
        {
            if (!immediate)
                await Task.Delay(300, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            RebuildProductQuery();
            ApplySalesFilter();
            StartDeepProductSearch();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Запрос «Товар» → SaleProductQuery: название — по словам, штрихкод/артикул/код — по каталогу кассы (точно).</summary>
    private void RebuildProductQuery()
    {
        var text = (_productBox?.Text ?? "").Trim();
        ListFilter.Product = text.Length == 0 ? null : text;
        if (text.Length == 0)
        {
            _productQuery = null;
            _deepNote = "";
            _deepSearchCts?.Cancel();
            return;
        }
        List<CatalogProductTileVm> catalog;
        try
        {
            catalog = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            catalog = new List<CatalogProductTileVm>(); // каталог как раз перезагружается — ищем только по названиям
        }
        _productQuery = new SaleProductQuery(text, catalog.Select(p => ((string?)p.Id, (string?)p.Title,
            (IEnumerable<string?>)new[] { p.Barcode, p.Article, p.ProductCode, p.AlternateBarcodesRaw })));
    }

    /// <summary>Строка списка проходит фильтры и поиск по товару. <paramref name="hit"/> — найденный товар (подпись в строке).</summary>
    private bool PassesListFilters(ReturnSaleListItemVm item, DateTime now, out string? hit)
    {
        hit = null;
        var row = new SaleListFilter.Row(item.SaleDate, item.TotalAmount, item.PaymentMethod, item.Status, item.ReturnStatus, item.Cashier, item.ClientName);
        // Период уже применён сервером (date_from/date_to); здесь он нужен в режиме клиента (его чеки приходят без периода).
        var filter = ListFilter;
        var periodWas = filter.Period;
        if (string.IsNullOrWhiteSpace(InitialClientId))
            filter.Period = SaleListFilter.PeriodKind.All;
        try
        {
            if (!filter.Matches(row, now))
                return false;
        }
        finally
        {
            filter.Period = periodWas;
        }
        if (_productQuery is not { IsEmpty: false } pq)
            return true;
        hit = pq.FirstMatch(LinesFor(item));
        return hit is not null;
    }

    private IEnumerable<(string? Name, string? ProductId)> LinesFor(ReturnSaleListItemVm item)
    {
        if (_saleLines.TryGetValue(item.SaleId, out var lines))
            foreach (var l in lines)
                yield return l;
        if (!string.IsNullOrWhiteSpace(item.FirstItemName))
            yield return (item.FirstItemName, null);
    }

    /// <summary>Счётчик под полем «Товар»: «Найдено N из M» и что ещё ищется.</summary>
    private void UpdateFilterCount()
    {
        if (_countText is null)
            return;
        var active = ListFilter.IsActive;
        var text = active
            ? FT($"Найдено: {FilteredSales.Count} из {Sales.Count}", $"Табылды: {FilteredSales.Count} / {Sales.Count}", $"Found: {FilteredSales.Count} of {Sales.Count}",
                $"Bulunan: {FilteredSales.Count} / {Sales.Count}", $"Topildi: {FilteredSales.Count} / {Sales.Count}")
            : "";
        if (_productQuery is { IsEmpty: false })
            text += (text.Length > 0 ? " · " : "") + (_deepNote.Length > 0 ? _deepNote
                : FT("ищет в загруженных чеках", "жүктөлгөн чектерден издейт", "searching the loaded receipts", "yüklenen fişlerde arıyor", "yuklangan cheklardan qidiradi"));
        _countText.Text = text;
        _countText.IsVisible = text.Length > 0;
        _filterBar?.SetCount(FilteredSales.Count, Sales.Count);
    }

    /// <summary>Состав чеков этой кассы из локальной истории продаж (без запросов) — для только что загруженных чеков.</summary>
    private void AddLocalLines(IReadOnlyCollection<string> saleIds)
    {
        if (saleIds.Count == 0)
            return;
        try
        {
            var local = SoldLineItemsStore.LinesBySale(saleIds);
            foreach (var (saleId, lines) in local)
            {
                if (_saleLinesComplete.Contains(saleId))
                    continue;
                _saleLines[saleId] = lines.Select(l => ((string?)l.ProductName, (string?)null)).ToList();
                _saleLinesComplete.Add(saleId);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: локальный состав чеков не прочитан ({ex.Message}).", "DEBUG");
        }
    }

    /// <summary>Состав чека из его карточки (открытый чек, догрузка для поиска).</summary>
    private void RememberSaleLines(string saleId, JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return;
        var lines = CartDisplayHelper.EnumerateSaleLineItems(sale)
            .Select(l => ((string?)CartDisplayHelper.ItemName(l), CartDisplayHelper.TryProductId(l))).ToList();
        if (lines.Count == 0)
            return;
        _saleLines[saleId] = lines;
        _saleLinesComplete.Add(saleId);
    }

    /// <summary>Поиск по товару в чеках, чей состав ещё не известен: карточки догружаются (не больше <see cref="DeepSearchLimit"/> за запрос, по 4 сразу,
    /// общий темп запросов и кэш чеков на сеанс), список обновляется по ходу, в счётчике — «ищет в составе: 40 из 150…».</summary>
    private void StartDeepProductSearch()
    {
        _deepSearchCts?.Cancel();
        if (_productQuery is not { IsEmpty: false } || OfflineModeHelper.IsServerOutage)
        {
            UpdateFilterCount();
            return;
        }
        var pending = Sales.Where(s => !_saleLinesComplete.Contains(s.SaleId)).Select(s => s.SaleId).Take(DeepSearchLimit).ToList();
        var skipped = Sales.Count(s => !_saleLinesComplete.Contains(s.SaleId)) - pending.Count;
        if (pending.Count == 0)
        {
            _deepNote = "";
            UpdateFilterCount();
            return;
        }
        var cts = new CancellationTokenSource();
        _deepSearchCts = cts;
        _ = DeepSearchAsync(pending, skipped, cts.Token);
    }

    private async Task DeepSearchAsync(List<string> saleIds, int skipped, CancellationToken ct)
    {
        var done = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = saleIds.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return (Id: id, Sale: await SaleDetailCache.GetAsync(id, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Id: id, Sale: default(JsonElement));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        try
        {
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(true);
                tasks.Remove(finished);
                var (id, sale) = await finished.ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                RememberSaleLines(id, sale);
                done++;
                if (done % 15 == 0 || tasks.Count == 0)
                {
                    _deepNote = tasks.Count == 0
                        ? (skipped > 0
                            ? FT($"проверены последние {saleIds.Count} чеков — уточните период, чтобы искать дальше", $"акыркы {saleIds.Count} чек текшерилди — андан ары издөө үчүн мезгилди тактаңыз",
                                $"checked the latest {saleIds.Count} receipts — narrow the period to search further", $"son {saleIds.Count} fiş kontrol edildi — daha fazlası için dönemi daraltın",
                                $"oxirgi {saleIds.Count} chek tekshirildi — davom ettirish uchun davrni aniqlang")
                            : "")
                        : FT($"ищет в составе чеков: {done} из {saleIds.Count}…", $"чектердин курамынан издейт: {done} / {saleIds.Count}…", $"searching receipt contents: {done} of {saleIds.Count}…",
                            $"fiş içeriğinde arıyor: {done} / {saleIds.Count}…", $"cheklar tarkibidan qidiradi: {done} / {saleIds.Count}…");
                    var selected = (SalesDataGrid.SelectedItem as ReturnSaleGridRow)?.SaleId;
                    ApplySalesFilter();
                    SelectRowInGrid(selected);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Открыт чек при поиске по товару — найденные позиции отмечены, количество 1 (кассир может поменять). Только при открытии, не после перечитывания.</summary>
    private void ApplyProductPreselect()
    {
        if (!_preselectOnFill)
            return;
        _preselectOnFill = false;
        if (_productQuery is not { IsEmpty: false })
            return;
        var hits = Lines.Where(l => l.IsSearchHit && l.CanReturn).ToList();
        if (hits.Count == 0)
            return;
        foreach (var line in Lines)
            line.IsSelected = false;
        foreach (var line in hits)
        {
            line.IsSelected = true;
            if (line.Quantity > 1 + 1e-6)
                line.ReturnQuantity = 1;
        }
    }

    /// <summary>Строка списка из ответа сервера (общий разбор для страницы и чеков клиента).</summary>
    private static ReturnSaleListItemVm ListItemFromRow(JsonElement row, string saleId)
    {
        DateTime saleDate = DateTime.MinValue;
        _ = TryGetDateTime(row, "date", out saleDate)
            || TryGetDateTime(row, "created_at", out saleDate)
            || TryGetDateTime(row, "sale_date", out saleDate)
            || TryGetDateTime(row, "order_date", out saleDate);

        decimal totalAmount = 0;
        _ = TryGetDecimal(row, "total", out totalAmount)
            || TryGetDecimal(row, "grand_total", out totalAmount)
            || TryGetDecimal(row, "total_amount", out totalAmount)
            || TryGetDecimal(row, "amount", out totalAmount)
            || TryGetDecimal(row, "total_sum", out totalAmount)
            || TryGetDecimal(row, "sum", out totalAmount)
            || TryGetDecimal(row, "price", out totalAmount)
            || TryGetDecimal(row, "total_price", out totalAmount)
            || TryGetDecimal(row, "final_total", out totalAmount)
            || TryGetDecimal(row, "order_total", out totalAmount);

        string? receiptNumber = row.TryGetProperty("receipt_number", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString() : null;
        if (string.IsNullOrWhiteSpace(receiptNumber))
            receiptNumber = SalesWindow.TryReceiptNumber(row);

        return new ReturnSaleListItemVm
        {
            SaleId = saleId,
            SaleDate = saleDate,
            TotalAmount = totalAmount,
            Summary = PosSaleRowFormatter.SummaryLine(row),
            ReceiptNumber = string.IsNullOrWhiteSpace(receiptNumber) ? null : receiptNumber,
            Status = SaleStatus(row),
            ReturnStatus = StrOf(row, "return_status"),
            PaymentMethod = StrOf(row, "payment_method"),
            Cashier = CartDisplayHelper.TryCashierName(row),
            ClientName = StrOf(row, "client_name") ?? StrOf(row, "client_display") ?? StrOf(row, "debt_client_name"),
            ClientId = StrOf(row, "client"),
            FirstItemName = StrOf(row, "first_item_name"),
        };
    }

    private static string? StrOf(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) is { Length: > 0 } s ? s.Trim() : null
            : null;
}
