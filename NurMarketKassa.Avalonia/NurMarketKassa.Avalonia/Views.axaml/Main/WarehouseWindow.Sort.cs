using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, владелец: «на складе сортировка: при нажатии на заголовок столбца «Остаток» как в Windows — сначала показывать
/// с 0 остатком». Клик по заголовку любого столбца списка: ▲ по возрастанию → ▼ по убыванию → без сортировки (как в Проводнике).
/// Сортируется весь отфильтрованный список (WarehouseViewModel.SortProducts), а не видимая страница из 50 строк — встроенная сортировка
/// таблицы упорядочивала только страницу. В «Плитках» — выпадающий список «Сортировка» с теми же вариантами. Выбор запоминается.
/// Здесь же — «Загружено N из M» фоновой сверки каталога после «Обновить» (15 тыс. товаров).</summary>
public partial class WarehouseWindow
{
    /// <summary>Столбец таблицы (SortMemberPath) → ключ сортировки, ключ надписи заголовка, выравнивание заголовка.</summary>
    private static readonly (string Path, string Key, string Resource, HorizontalAlignment Align)[] SortColumns =
    [
        ("Title", "name", "common.name", HorizontalAlignment.Left),
        ("Article", "code", "warehouse.colCodeBarcode", HorizontalAlignment.Left),
        ("PriceValue", "price", "csh.catalog.colPrice", HorizontalAlignment.Right),
        ("PurchasePrice", "cost", "warehouse.colCost", HorizontalAlignment.Right),
        ("Quantity", "stock", "csh.catalog.colStock", HorizontalAlignment.Center),
    ];

    private readonly Dictionary<string, Avalonia.Controls.Documents.Run> _sortArrows = new();
    private ComboBox? _sortPicker;
    private StackPanel? _sortPickerPanel;
    private bool _sortPickerUpdating;
    private bool _sortReady;

    private sealed record SortOption(string Key, bool Descending, string Label)
    {
        public override string ToString() => Label;
    }

    // 2026-10-30 (приёмка 1.17.67): пункты «Закупка» видны только ролям с правом видеть закупку (как колонка «Себестоимость»).
    private static List<SortOption> BuildSortOptions() => BuildAllSortOptions().Where(o => o.Key != "cost" || RevisionShowPurchase).ToList();

    private static List<SortOption> BuildAllSortOptions() =>
    [
        new("", false, T("Без сортировки", "Иреттөөсүз", "No sorting", "Sıralama yok", "Saralashsiz")),
        new("name", false, T("Название: А → Я", "Аталышы: А → Я", "Name: A → Z", "Ad: A → Z", "Nomi: A → Z")),
        new("name", true, T("Название: Я → А", "Аталышы: Я → А", "Name: Z → A", "Ad: Z → A", "Nomi: Z → A")),
        new("stock", false, T("Остаток: сначала 0 ▲", "Калдык: адегенде 0 ▲", "Stock: zero first ▲", "Stok: önce 0 ▲", "Qoldiq: avval 0 ▲")),
        new("stock", true, T("Остаток: больше сверху ▼", "Калдык: көбү жогоруда ▼", "Stock: largest first ▼", "Stok: en çok üstte ▼", "Qoldiq: ko'pi yuqorida ▼")),
        new("price", false, T("Цена: дешевле сверху ▲", "Баасы: арзаны жогоруда ▲", "Price: lowest first ▲", "Fiyat: en düşük üstte ▲", "Narx: arzoni yuqorida ▲")),
        new("price", true, T("Цена: дороже сверху ▼", "Баасы: кымбаты жогоруда ▼", "Price: highest first ▼", "Fiyat: en yüksek üstte ▼", "Narx: qimmati yuqorida ▼")),
        new("cost", false, T("Закупка: дешевле сверху ▲", "Сатып алуу: арзаны жогоруда ▲", "Cost: lowest first ▲", "Alış: en düşük üstte ▲", "Xarid: arzoni yuqorida ▲")),
        new("cost", true, T("Закупка: дороже сверху ▼", "Сатып алуу: кымбаты жогоруда ▼", "Cost: highest first ▼", "Alış: en yüksek üstte ▼", "Xarid: qimmati yuqorida ▼")),
        new("code", false, T("Код: по возрастанию ▲", "Коду: өсүү боюнча ▲", "Code: ascending ▲", "Kod: artan ▲", "Kod: o'sish bo'yicha ▲")),
        new("code", true, T("Код: по убыванию ▼", "Коду: кемүү боюнча ▼", "Code: descending ▼", "Kod: azalan ▼", "Kod: kamayish bo'yicha ▼")),
        new("category", false, T("Категория: А → Я", "Категориясы: А → Я", "Category: A → Z", "Kategori: A → Z", "Turkum: A → Z")),
        new("category", true, T("Категория: Я → А", "Категориясы: Я → А", "Category: Z → A", "Kategori: Z → A", "Turkum: Z → A")),
    ];

    private void InitProductSort()
    {
        if (_sortReady || ProductsGrid is null)
            return;
        _sortReady = true;

        // Заголовки: прежняя надпись (та же строка ресурса, меняется с языком) + стрелка ▲/▼ справа.
        foreach (var column in ProductsGrid.Columns)
        {
            var def = SortColumns.FirstOrDefault(c => c.Path == column.SortMemberPath);
            if (def.Key is null)
                continue;
            // Одна строка из двух частей (надпись + стрелка): отдельные TextBlock в StackPanel заголовок таблицы накладывал друг на друга.
            var header = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = def.Align switch { HorizontalAlignment.Right => TextAlignment.Right, HorizontalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Left },
            };
            var text = new Avalonia.Controls.Documents.Run();
            text.Bind(Avalonia.Controls.Documents.Run.TextProperty, header.GetResourceObservable(def.Resource));
            var arrow = new Avalonia.Controls.Documents.Run { Text = "", FontSize = 13 };
            arrow.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, header.GetResourceObservable("BrushAccentStrong"));
            header.Inlines = [text, arrow];
            column.Header = header;
            _sortArrows[def.Key] = arrow;
        }

        ProductsGrid.Sorting += (_, e) =>
        {
            // Встроенная сортировка таблицы упорядочила бы только видимую страницу — сортирует модель весь список.
            e.Handled = true;
            var def = SortColumns.FirstOrDefault(c => c.Path == e.Column?.SortMemberPath);
            if (def.Key is not null)
                _viewModel.CycleProductSort(def.Key);
        };

        // Плитки: выпадающий список «Сортировка» (в «Списке» сортируют заголовки).
        _sortPicker = new ComboBox { MinWidth = 220, ItemsSource = BuildSortOptions(), VerticalAlignment = VerticalAlignment.Center };
        _sortPicker.SelectionChanged += (_, _) =>
        {
            if (_sortPickerUpdating || _sortPicker.SelectedItem is not SortOption opt)
                return;
            _viewModel.SetProductSort(opt.Key, opt.Descending);
        };
        var label = new TextBlock { Text = T("Сортировка", "Иреттөө", "Sort", "Sıralama", "Saralash"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        label.Classes.Add("seg-label");
        _sortPickerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 6) };
        _sortPickerPanel.Children.Add(label);
        _sortPickerPanel.Children.Add(_sortPicker);
        WarehouseFiltersRow.Children.Add(_sortPickerPanel);

        // 2026-10-30 (приёмка 1.17.67): запомненная сортировка по закупке не должна действовать у роли без права видеть закупку (порядок выдал бы цены).
        if (!RevisionShowPurchase && _viewModel.ProductSortKey == "cost")
            _viewModel.SetProductSort("", false);
        _viewModel.ProductSortChanged += OnProductSortChanged;
        Closed += (_, _) => _viewModel.ProductSortChanged -= OnProductSortChanged;
        UpdateSortIndicators();
        UpdateSortPickerVisibility();
    }

    private void OnProductSortChanged()
    {
        UpdateSortIndicators();
        // Новая сортировка — к началу списка (страница 1 уже выбрана моделью).
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (ProductsGrid.IsVisible && _viewModel.PagedProducts.Count > 0)
                    ProductsGrid.ScrollIntoView(_viewModel.PagedProducts[0], null);
                else if (ProductsTilesScroll.IsVisible)
                    ProductsTilesScroll.Offset = default;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Склад: прокрутка после сортировки: {ex.Message}", "WARNING");
            }
        });
    }

    private void UpdateSortIndicators()
    {
        var key = _viewModel.ProductSortKey;
        var desc = _viewModel.ProductSortDescending;
        foreach (var (k, arrow) in _sortArrows)
        {
            arrow.Text = k != key ? "" : desc ? " ▼" : " ▲";
        }

        if (_sortPicker?.ItemsSource is List<SortOption> options)
        {
            _sortPickerUpdating = true;
            try
            {
                _sortPicker.SelectedItem = options.FirstOrDefault(o => o.Key == key && (key.Length == 0 || o.Descending == desc)) ?? options[0];
            }
            finally
            {
                _sortPickerUpdating = false;
            }
        }
    }

    private void UpdateSortPickerVisibility()
    {
        if (_sortPickerPanel is not null)
            _sortPickerPanel.IsVisible = ViewTilesRadio.IsChecked == true;
    }

    /// <summary>2026-10-30 (15 тыс. товаров): после «Обновить» таблица уже показана по ленте изменений, а полная сверка каталога идёт в фоне —
    /// рядом с поиском «Сверка с сервером: загружено N из M» (раньше — «Загружаю товары с сервера… 178 с» с неработающим окном).</summary>
    private async Task TrackBackgroundFullReloadAsync(Task<CatalogSyncResult> fullReload)
    {
        void OnProgress(int loaded, int? total) => Dispatcher.UIThread.Post(() =>
        {
            if (fullReload.IsCompleted)
                return;
            ProductCountLabel.IsVisible = false;
            ProductCountLoading.IsVisible = true;
            ProductCountLoading.Text = total is > 0
                ? T($"Сверка с сервером: загружено {loaded:N0} из {total:N0}", $"Сервер менен салыштыруу: {total:N0} ичинен {loaded:N0} жүктөлдү",
                    $"Checking with the server: {loaded:N0} of {total:N0} loaded", $"Sunucuyla karşılaştırma: {total:N0} içinden {loaded:N0} yüklendi",
                    $"Server bilan solishtirish: {total:N0} tadan {loaded:N0} yuklandi")
                : T($"Сверка с сервером: загружено {loaded:N0}", $"Сервер менен салыштыруу: {loaded:N0} жүктөлдү", $"Checking with the server: {loaded:N0} loaded",
                    $"Sunucuyla karşılaştırma: {loaded:N0} yüklendi", $"Server bilan solishtirish: {loaded:N0} yuklandi");
        });

        CatalogApiService.FullLoadProgress += OnProgress;
        try
        {
            var result = await fullReload.ConfigureAwait(true);
            if (result.Success)
            {
                // Полная загрузка заменила объекты товаров — таблица и итоги по новым.
                _viewModel.ReapplyProductFilter();
                RefreshWarehouseTotals();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: фоновая сверка каталога не удалась: {ex.Message}", "CATALOG");
        }
        finally
        {
            CatalogApiService.FullLoadProgress -= OnProgress;
            ProductCountLoading.IsVisible = false;
            ProductCountLabel.IsVisible = true;
        }
    }
}
