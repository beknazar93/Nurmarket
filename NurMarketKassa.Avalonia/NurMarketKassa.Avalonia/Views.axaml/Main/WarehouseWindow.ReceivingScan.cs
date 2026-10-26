using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-24, ТЗ «Склад — ревизия, приёмка, поставщик, Telegram-группа», пакеты Б1 и В. Склад → Приёмка: «скан → строка».
/// • Скан (или выбор товара) ставит строку СВЕРХУ списка, курсор — в «Принято» с выделенным числом; Enter (и Tab) ведёт по ячейкам:
///   «Принято» → «Цена закупки» → «Цена продажи» → снова поле скана. Флажок «Сразу к следующему скану» оставляет курсор в поле скана.
/// • «Товары поставщика (N)» — панель с поиском и виртуализированным списком вместо 60 кнопок; подсказки поля скана показывают товары
///   выбранного поставщика первыми.
/// • «＋» рядом с выбором поставщика — создать поставщика (SupplierQuickCreate), он сразу выбран; в списке поставщиков есть поиск.
/// Деньги и остатки здесь не считаются — всё проводит PurchaseReceivingService (через WarehouseViewModel.CommitReceivingAsync).</summary>
public partial class WarehouseWindow
{
    private static string TR(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ------------------------------------------------------------------ значок «поставщик» в подсказках скана

    /// <summary>Подпись значка у подсказки, если товар приходил от выбранного поставщика (x:Static в разметке).</summary>
    public static string SupplierBadgeText => TR("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi");

    // Идентификаторы товаров выбранного поставщика. Статическая ссылка нужна конвертеру разметки; окно склада одно, при двух —
    // значок покажет товары того окна, где поставщика выбрали последним (на логику приёмки не влияет).
    private static HashSet<string> s_supplierProductIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _supplierProductIds = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IValueConverter IsSupplierProductConverter =
        new FuncValueConverter<string?, bool>(id => !string.IsNullOrEmpty(id) && s_supplierProductIds.Contains(id));

    private bool IsSupplierProduct(string? productId) => !string.IsNullOrEmpty(productId) && _supplierProductIds.Contains(productId);

    // ------------------------------------------------------------------ колонки таблицы приёмки

    // 2026-10-24: x:Name у колонок DataGrid поля не создаёт, поэтому колонки — свойствами в одном месте. Порядок — как в разметке ReceivingGrid;
    // «Наценка %» и «Изменение» сдвинули прежние номера (Columns[1], [2], [9] в коде приёмки заменены на эти имена).
    private DataGridColumn RcvColSource => ReceivingGrid.Columns[0];
    private DataGridColumn RcvColName => ReceivingGrid.Columns[1];
    private DataGridColumn RcvColBarcode => ReceivingGrid.Columns[2];
    private DataGridColumn RcvColUnit => ReceivingGrid.Columns[3];
    private DataGridColumn RcvColWas => ReceivingGrid.Columns[4];
    private DataGridColumn RcvColQty => ReceivingGrid.Columns[5];
    private DataGridColumn RcvColPurchase => ReceivingGrid.Columns[6];
    private DataGridColumn RcvColMarkup => ReceivingGrid.Columns[7];
    private DataGridColumn RcvColSale => ReceivingGrid.Columns[8];
    private DataGridColumn RcvColChange => ReceivingGrid.Columns[9];
    private DataGridColumn RcvColTotal => ReceivingGrid.Columns[10];
    private DataGridColumn RcvColSizes => ReceivingGrid.Columns[11];
    private DataGridColumn RcvColRemove => ReceivingGrid.Columns[12];

    // ------------------------------------------------------------------ запуск

    private void InitReceivingScan()
    {
        ReceivingHintText.Text = TR(
            "Сканируйте товары по накладной. Поставщика можно выбрать в любой момент до «Оприходовать». После скана курсор встаёт в «Принято»: число → Enter → цена закупки → Enter → цена продажи → Enter — и снова скан. Незнакомый штрихкод — новая строка: впишите название.",
            "Накладная боюнча товарларды сканерлеңиз. Жеткирүүчүнү «Кирешеге алуу» басылганга чейин каалаган убакта тандаса болот. Сканерден кийин курсор «Кабыл алынды» уячасына тургандыктан: сан → Enter → сатып алуу баасы → Enter → сатуу баасы → Enter — жана кайра скан. Белгисиз штрихкод — жаңы сап: атын жазыңыз.",
            "Scan the items of the delivery note. You can pick the supplier at any moment before “Receive”. After a scan the cursor lands in “Received”: number → Enter → purchase price → Enter → sale price → Enter — and scan again. An unknown barcode is a new line: type its name.",
            "İrsaliyedeki ürünleri okutun. Tedarikçiyi «Stoğa al»dan önce istediğiniz an seçebilirsiniz. Okutmadan sonra imleç «Kabul edilen» hücresinde olur: sayı → Enter → alış fiyatı → Enter → satış fiyatı → Enter — ve yine okutma. Bilinmeyen barkod — yeni satır: adını yazın.",
            "Nakladnoy bo'yicha mahsulotlarni skanerlang. Yetkazib beruvchini «Kirim qilish»dan oldin istalgan paytda tanlash mumkin. Skanerdan keyin kursor «Qabul qilindi» katakchasida turadi: son → Enter → xarid narxi → Enter → sotuv narxi → Enter — va yana skaner. Notanish shtrix-kod — yangi qator: nomini yozing.");
        ReceivingFastNextCheck.Content = TR("Сразу к скану", "Дароо сканерге", "Straight to scan", "Doğrudan okutmaya", "Darhol skanerga");
        // Подсказка вкладки (на низком окне она скрыта) — всплывающей подсказкой поля скана.
        ToolTip.SetTip(ReceivingScanBox, ReceivingHintText.Text);
        ToolTip.SetTip(ReceivingFastNextCheck, TR("Курсор остаётся в поле скана, каждый скан прибавляет количество из поля рядом.",
            "Курсор сканер талаасында калат, ар бир скан жанындагы талаадагы санды кошот.",
            "The cursor stays in the scan field; every scan adds the quantity from the field next to it.",
            "İmleç okutma alanında kalır, her okutma yanındaki alandaki miktarı ekler.",
            "Kursor skaner maydonida qoladi, har bir skan yonidagi maydondagi sonni qo'shadi."));
        RcvMarkupHeader.Text = TR("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %");
        RcvChangeHeader.Text = TR("Динамика", "Динамика", "Trend", "Trend", "Dinamika");
        ToolTip.SetTip(ReceivingNewSupplierButton, TR("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
        if (OfflineModeHelper.UseLocalOperations)
        {
            ReceivingNewSupplierButton.IsEnabled = false;
            ToolTip.SetTip(ReceivingNewSupplierButton, TR("Поставщика можно создать только при связи с сервером.", "Жеткирүүчүнү сервер менен байланышта гана түзсө болот.",
                "A supplier can only be created while connected to the server.", "Tedarikçi yalnızca sunucuya bağlıyken oluşturulabilir.",
                "Yetkazib beruvchini faqat server bilan aloqada yaratish mumkin."));
        }
        ReceivingSupplierSearch.Watermark = TR("Поиск по названию или телефону", "Аталышы же телефону боюнча издөө", "Search by name or phone",
            "Ad veya telefona göre ara", "Nomi yoki telefon bo'yicha qidirish");
        ReceivingSupplierProductsSearch.Watermark = TR("Найти товар поставщика: название или штрихкод — Enter добавит первый",
            "Жеткирүүчүнүн товарын табуу: аталышы же штрихкод — Enter биринчисин кошот", "Find a supplier product: name or barcode — Enter adds the first",
            "Tedarikçi ürününü bul: ad veya barkod — Enter ilkini ekler", "Yetkazib beruvchi mahsulotini topish: nomi yoki shtrix-kod — Enter birinchisini qo'shadi");

        // Enter / Tab в правке ячейки приёмки — по цепочке «Принято» → «Закупка» → «Продажа» → поле скана (туннельно: раньше, чем DataGrid уведёт курсор вниз).
        ReceivingGrid.AddHandler(KeyDownEvent, ReceivingGrid_PreviewKeyDown, RoutingStrategies.Tunnel);

        // 2026-10-26, приёмка (Д-2): в программе владельца в пустой приёмке видели «строку-призрак» («Новый · шт · 0 · 1 …») поверх «Накладная пока
        // пустая», хотя в списке 0 позиций. Причину в стенде воспроизвести не удалось, поэтому — защита от симптома: после каждого изменения
        // списка, если таблица показывает строк больше, чем в списке, она получает источник заново (лишние строки пропадают).
        _viewModel.ReceivingLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(SweepStaleReceivingRows, DispatcherPriority.Background);

        // Открыли вкладку — курсор в поле скана. e.Source: SelectionChanged всплывает от вложенных списков (замечание к Р2).
        WarehouseTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, WarehouseTabs) || !ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                    ReceivingScanBox.Focus();
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>2026-10-26 (Д-2): таблица приёмки показывает больше строк, чем в списке, — переустановить источник (см. InitReceivingScan).
    /// В норме не делает ничего: число видимых строк не больше числа позиций (прокрутка показывает меньше, лишних не бывает).</summary>
    private void SweepStaleReceivingRows()
    {
        try
        {
            if (!ReceivingGrid.IsEffectivelyVisible)
                return;
            var shown = ReceivingGrid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible && r.Bounds.Height > 0);
            if (shown <= _viewModel.ReceivingLines.Count)
                return;
            PosLogger.Log($"Приёмка: таблица показывала лишние строки ({shown} при {_viewModel.ReceivingLines.Count} в списке) — таблица обновлена.", "WARNING");
            var source = ReceivingGrid.ItemsSource;
            ReceivingGrid.ItemsSource = null;
            ReceivingGrid.ItemsSource = source;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: проверка лишних строк таблицы не удалась: {ex.Message}", "DEBUG");
        }
    }

    // ------------------------------------------------------------------ строка добавлена: наверх, курсор в «Принято»

    /// <summary>Скан / выбор товара добавил (или нашёл) строку: она уже первая в списке. Курсор — в «Принято» (у нового товара без названия — в «Название»);
    /// с флажком «Сразу к следующему скану» остаётся в поле скана.</summary>
    private void OnReceivingLineAdded(ReceivingLineVm line)
    {
        RefreshReceivingSummary();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_viewModel.ReceivingLines.Contains(line))
                return;
            ReceivingGrid.SelectedItem = line;
            ReceivingGrid.ScrollIntoView(line, null);
            if (ReceivingFastNextCheck.IsChecked == true)
            {
                ReceivingScanBox.Focus();
                return;
            }
            BeginReceivingEdit(line, string.IsNullOrWhiteSpace(line.ProductName) ? RcvColName : RcvColQty);
        }, DispatcherPriority.Background);
    }

    /// <summary>Правка ячейки строки с выделенным текстом (число сразу заменяется набором). Предыдущая правка дописывается.</summary>
    private void BeginReceivingEdit(ReceivingLineVm line, DataGridColumn column)
    {
        if (!_viewModel.ReceivingLines.Contains(line) || !column.IsVisible)
            return;
        ReceivingGrid.CommitEdit();
        ReceivingGrid.SelectedItem = line;
        ReceivingGrid.ScrollIntoView(line, column);
        ReceivingGrid.CurrentColumn = column;
        if (!ReceivingGrid.BeginEdit())
            return;
        // Редактор появляется после разметки ячейки; фокус и выделение ставим сами — не зависим от версии DataGrid.
        Dispatcher.UIThread.Post(() =>
        {
            var editor = ReceivingGrid.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.IsVisible && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            if (editor is null)
                return;
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Enter и Tab в правке ячейки: «Название» → «Принято» → «Закупка» → «Продажа» → поле скана. Остальные ячейки — как в DataGrid.</summary>
    private void ReceivingGrid_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Tab) || e.KeyModifiers != KeyModifiers.None)
            return;
        if (e.Source is not TextBox editor || !editor.GetVisualAncestors().OfType<DataGridCell>().Any())
            return;
        if (ReceivingGrid.SelectedItem is not ReceivingLineVm line)
            return;

        var current = ReceivingGrid.CurrentColumn;
        DataGridColumn? next;
        if (ReferenceEquals(current, RcvColName))
            next = RcvColQty;
        else if (ReferenceEquals(current, RcvColQty))
            next = RcvColPurchase;
        else if (ReferenceEquals(current, RcvColPurchase))
            next = RcvColSale;
        else if (ReferenceEquals(current, RcvColSale))
            next = null;
        else
            return;

        e.Handled = true;
        if (!ReceivingGrid.CommitEdit())
            return;
        RefreshReceivingSummary();
        Dispatcher.UIThread.Post(() =>
        {
            if (next is null)
            {
                ReceivingGrid.SelectedItem = line;
                ReceivingScanBox.Focus();
                ReceivingScanBox.SelectAll();
            }
            else
                BeginReceivingEdit(line, next);
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ поставщик: список с поиском, «＋», панель товаров

    private bool _supplierListSync;
    private List<SupplierProductRow> _supplierProductRows = new();
    private CancellationTokenSource? _supplierProductsCts;
    private bool _supplierProductsOpen;

    /// <summary>Строка панели «Товары поставщика».</summary>
    private sealed class SupplierProductRow
    {
        public string Title { get; init; } = "";
        public string Info { get; init; } = "";
        public string SearchKey { get; init; } = "";
        public CatalogProductTileVm Tile { get; init; } = null!;
        public PurchaseReceivingService.SupplierProduct Item { get; init; } = null!;
    }

    private static string DigitsOnly(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Поиск в списке поставщиков — по названию и по цифрам телефона. «Без поставщика» остаётся всегда.</summary>
    private void ReceivingSupplierSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!ReceivingSupplierPanel.IsVisible)
            return;
        var query = (ReceivingSupplierSearch.Text ?? "").Trim();
        var digits = DigitsOnly(query);
        object source = query.Length == 0
            ? _viewModel.ReceivingSuppliers
            : _viewModel.ReceivingSuppliers.Where(s => string.IsNullOrEmpty(s.Id)
                || s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (digits.Length >= 3 && DigitsOnly(s.Phone).Contains(digits, StringComparison.Ordinal))).ToList();
        _supplierListSync = true;
        try
        {
            ReceivingSupplierList.ItemsSource = (System.Collections.IEnumerable)source;
        }
        finally
        {
            _supplierListSync = false;
        }
    }

    /// <summary>«＋»: окно «Новый поставщик» (название и телефон). Такое же название — выбирается существующий, второй не создаётся.</summary>
    private async void ReceivingNewSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (OfflineModeHelper.UseLocalOperations || !ReceivingNewSupplierButton.IsEnabled)
            return;
        ReceivingNewSupplierButton.IsEnabled = false; // от двойного нажатия
        try
        {
            await _viewModel.LoadReceivingSuppliersAsync().ConfigureAwait(true);
            CloseReceivingPopups();
            var known = _viewModel.ReceivingSuppliers.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
            var result = await SupplierQuickCreate.ShowAsync(this, known).ConfigureAwait(true);
            if (result is null)
                return;
            _viewModel.SelectOrAddReceivingSupplier(result.Supplier);
            App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(result.Existed
                ? TR("Такой поставщик уже есть — выбран.", "Мындай жеткирүүчү бар — тандалды.", "This supplier already exists — selected.",
                    "Bu tedarikçi zaten var — seçildi.", "Bunday yetkazib beruvchi allaqachon bor — tanlandi.")
                : TR("Поставщик добавлен и выбран.", "Жеткирүүчү кошулду жана тандалды.", "Supplier added and selected.",
                    "Tedarikçi eklendi ve seçildi.", "Yetkazib beruvchi qo'shildi va tanlandi."));
            ReceivingScanBox.Focus();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: окно «Новый поставщик» не открылось: {ex}", "WARNING");
        }
        finally
        {
            ReceivingNewSupplierButton.IsEnabled = !OfflineModeHelper.UseLocalOperations;
        }
    }

    /// <summary>Товары выбранного поставщика (приходили раньше): кнопка «Товары поставщика (N)» открывает панель с поиском. Список — все товары
    /// (до 600 и больше), виртуализированный; свежие приходы сверху.</summary>
    private async Task ShowSupplierProductsAsync()
    {
        _supplierProductsCts?.Cancel();
        _supplierProductsOpen = false;
        _supplierProductRows = new List<SupplierProductRow>();
        _supplierProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s_supplierProductIds = _supplierProductIds;
        ReceivingSupplierProductsList.ItemsSource = null;
        ReceivingSupplierProductsSearch.Text = "";
        if (_viewModel?.ReceivingSupplier is not { Id.Length: > 0 } supplier)
        {
            ReceivingSupplierProductsBox.IsVisible = false;
            ReceivingSupplierProductsButton.IsVisible = false;
            return;
        }
        var cts = _supplierProductsCts = new CancellationTokenSource();
        ReceivingSupplierProductsBox.IsVisible = false;
        ReceivingSupplierProductsButton.IsVisible = true;
        ReceivingSupplierProductsButton.IsEnabled = false;
        ReceivingSupplierProductsButton.Content = TR("Товары поставщика…", "Жеткирүүчүнүн товарлары…", "Supplier products…", "Tedarikçi ürünleri…", "Yetkazib beruvchi mahsulotlari…");
        List<PurchaseReceivingService.SupplierProduct> products;
        try
        {
            products = await PurchaseReceivingService.Instance.LoadSupplierProductsAsync(supplier, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
                return;
            ReceivingSupplierProductsButton.IsVisible = false;
            ReceivingSupplierProductsBox.IsVisible = true;
            ReceivingSupplierProductsSearch.IsVisible = false;
            ReceivingSupplierProductsList.IsVisible = false;
            ReceivingSupplierProductsTitle.Text = TR("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "Supplier products didn't load: ",
                "Tedarikçi ürünleri yüklenemedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ") + ex.Message;
            return;
        }
        if (cts.IsCancellationRequested)
            return;

        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in CatalogCacheService.Products)
            catalog.TryAdd(p.Id, p);
        var rows = new List<SupplierProductRow>();
        foreach (var item in products)
        {
            if (!catalog.TryGetValue(item.ProductId, out var tile))
                continue;
            var at = item.LastAt.ToLocalTime().ToString("dd.MM.yyyy");
            var price = item.LastPurchasePrice > 0 ? $" × {item.LastPurchasePrice:0.##}" : "";
            rows.Add(new SupplierProductRow
            {
                Title = tile.Title,
                Info = $"{at} · {item.LastQuantity:0.###}{price}",
                SearchKey = $"{tile.Title} {tile.Barcode} {tile.Article}",
                Tile = tile,
                Item = item,
            });
        }
        _supplierProductRows = rows;
        foreach (var row in rows)
            _supplierProductIds.Add(row.Tile.Id);

        ReceivingSupplierProductsSearch.IsVisible = rows.Count > 0;
        ReceivingSupplierProductsList.IsVisible = rows.Count > 0;
        if (rows.Count == 0)
        {
            ReceivingSupplierProductsButton.IsVisible = false;
            ReceivingSupplierProductsBox.IsVisible = true;
            ReceivingSupplierProductsTitle.Text = TR($"От «{supplier.Name}» раньше ничего не приходовали.", $"«{supplier.Name}» жеткирүүчүдөн мурда эч нерсе кириштелген эмес.",
                $"Nothing has been received from “{supplier.Name}” before.", $"«{supplier.Name}» tedarikçisinden daha önce bir şey alınmadı.",
                $"«{supplier.Name}» dan avval hech narsa kirim qilinmagan.");
            return;
        }
        ReceivingSupplierProductsButton.IsEnabled = true;
        ReceivingSupplierProductsButton.Content = TR($"Товары поставщика ({rows.Count})", $"Жеткирүүчүнүн товарлары ({rows.Count})",
            $"Supplier products ({rows.Count})", $"Tedarikçi ürünleri ({rows.Count})", $"Yetkazib beruvchi mahsulotlari ({rows.Count})");
        ReceivingSupplierProductsTitle.Text = TR($"Товары поставщика «{supplier.Name}» ({rows.Count}) — дата последнего прихода · количество × цена закупки. Найдите нужный и нажмите:",
            $"«{supplier.Name}» жеткирүүчүнүн товарлары ({rows.Count}) — акыркы кирешенин күнү · саны × сатып алуу баасы. Керектүүсүн тапып басыңыз:",
            $"Products of “{supplier.Name}” ({rows.Count}) — date of the last receipt · quantity × purchase price. Find the one you need and press it:",
            $"«{supplier.Name}» ürünleri ({rows.Count}) — son giriş tarihi · miktar × alış fiyatı. İhtiyacınız olanı bulup basın:",
            $"«{supplier.Name}» mahsulotlari ({rows.Count}) — oxirgi kirim sanasi · soni × xarid narxi. Keraklisini topib bosing:");
        ApplySupplierProductsFilter();
    }

    private void ApplySupplierProductsFilter()
    {
        var query = (ReceivingSupplierProductsSearch.Text ?? "").Trim();
        IEnumerable<SupplierProductRow> rows = _supplierProductRows;
        if (query.Length > 0)
            rows = rows.Where(r => r.SearchKey.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        ReceivingSupplierProductsList.ItemsSource = rows.ToList();
    }

    private void ReceivingSupplierProducts_Click(object? sender, RoutedEventArgs e)
    {
        _supplierProductsOpen = !_supplierProductsOpen;
        ReceivingSupplierProductsBox.IsVisible = _supplierProductsOpen;
        if (_supplierProductsOpen)
            ReceivingSupplierProductsSearch.Focus();
    }

    private void ReceivingSupplierProductsClose_Click(object? sender, RoutedEventArgs e)
    {
        _supplierProductsOpen = false;
        ReceivingSupplierProductsBox.IsVisible = false;
    }

    private void ReceivingSupplierProductsSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplySupplierProductsFilter();

    /// <summary>Enter в поиске панели — добавить первый найденный товар.</summary>
    private void ReceivingSupplierProductsSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (ReceivingSupplierProductsList.ItemsSource is IEnumerable<SupplierProductRow> found && found.FirstOrDefault() is { } first)
            AddSupplierProductRow(first);
    }

    private void ReceivingSupplierProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SupplierProductRow row })
            AddSupplierProductRow(row);
    }

    /// <summary>Нажатие «＋» у товара поставщика: строка сверху с количеством из поля рядом и последней ценой закупки у этого поставщика
    /// (если в строке цены закупки ещё нет); курсор — в «Принято».</summary>
    private void AddSupplierProductRow(SupplierProductRow row)
    {
        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;
        ReceivingGrid.CommitEdit();
        var line = _viewModel.AddReceivingProduct(row.Tile, quantity);
        if (row.Item.LastPurchasePrice > 0 && line.PurchasePrice <= 0)
            line.PurchasePrice = row.Item.LastPurchasePrice;
        ReceivingQuantityBox.Text = "1";
        ReceivingSupplierProductsSearch.Text = "";
        OnReceivingLineAdded(line);
    }

    // ------------------------------------------------------------------ подорожание: «Обновить цены продажи (N)» и подтверждение перед «Оприходовать»

    /// <summary>Кнопка «Обновить цены продажи (N)» видна, пока есть строки с подорожанием и непоправленной продажей (ReceivingPricing.CountRaisable).</summary>
    private void RefreshReceivingRaiseButton()
    {
        var n = ReceivingPricing.CountRaisable(_viewModel.ReceivingLines);
        ReceivingRaiseButton.IsVisible = n > 0;
        if (n > 0)
            ReceivingRaiseButton.Content = TR($"Обновить цены продажи ({n})", $"Сатуу баасын жаңыртуу ({n})", $"Update sale prices ({n})",
                $"Satış fiyatlarını güncelle ({n})", $"Sotuv narxlarini yangilash ({n})");
    }

    private async void ReceivingRaise_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingGrid.CommitEdit();
        var dialog = new ReceivingRaiseDialog(_viewModel.ReceivingLines.ToList());
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.AppliedCount > 0)
            PosLogger.Log($"Приёмка: «Обновить цены продажи» — {dialog.AppliedCount} строк.", "STOCK");
        RefreshReceivingSummary();
    }

    /// <summary>Перед «Оприходовать»: «Цена продажи изменится у K товаров» со списком (прежняя → новая). false — кассир передумал.</summary>
    private Task<bool> ConfirmSalePriceChangesAsync(IReadOnlyList<ReceivingLineVm> changed)
    {
        var shown = changed.Take(12).Select(l =>
            $"• {(string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName)}: {l.OriginalSalePrice:0.##} → {l.SalePrice:0.##}").ToList();
        if (changed.Count > shown.Count)
            shown.Add(TR($"…и ещё {changed.Count - shown.Count}", $"…жана дагы {changed.Count - shown.Count}", $"…and {changed.Count - shown.Count} more",
                $"…ve {changed.Count - shown.Count} tane daha", $"…va yana {changed.Count - shown.Count}"));
        var message = TR($"Цена продажи изменится у {changed.Count} товаров:", $"{changed.Count} товардын сатуу баасы өзгөрөт:",
                $"The sale price will change for {changed.Count} products:", $"{changed.Count} ürünün satış fiyatı değişecek:",
                $"{changed.Count} ta mahsulotning sotuv narxi o'zgaradi:")
            + "\n\n" + string.Join("\n", shown);
        return PosConfirmDialog.ShowModalAsync(this,
            TR("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"), message,
            TR("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"),
            TR("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary);
    }
}
