using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class CatalogPanelView : UserControl
{
    private CatalogPanelViewModel? _boundViewModel;

    public CatalogPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel: стрелки внутри списка разбираем сами раньше встроенной обработки ListBox —
        // она умеет только Влево/Вправо в пределах страницы (см. ProductsList_ArrowKeyTunnel).
        CardsListBox.AddHandler(KeyDownEvent, ProductsList_ArrowKeyTunnel, RoutingStrategies.Tunnel);
        TableListBox.AddHandler(KeyDownEvent, ProductsList_ArrowKeyTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Клавиатурный курсор стоит на плитке каталога (кассир ведёт его стрелками, рамка
    /// видна). Фокус, полученный мышью (клик по плитке или мимо кнопки в её край), курсором не
    /// считается. Товар под рамкой добавляет Num + (<see cref="TryAddHighlightedProduct"/>) —
    /// 2026-09-27, владелец: «добавление товара из каталога не через Enter, а через + на нумпаде»;
    /// Enter теперь всегда оплачивает.</summary>
    public bool HasKeyboardCursor => HighlightedProduct is not null;

    /// <summary>Товар под клавиатурной рамкой; null — рамки нет.</summary>
    public CatalogProductTileVm? HighlightedProduct =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ListBoxItem item
        && item.Classes.Contains(":focus-visible")
        && (CardsListBox.IndexFromContainer(item) >= 0 || TableListBox.IndexFromContainer(item) >= 0)
            ? item.DataContext as CatalogProductTileVm
            : null;

    /// <summary>Добавляет в чек товар под рамкой — то же, что клик по плитке мышью. false — рамки нет.</summary>
    public bool TryAddHighlightedProduct()
    {
        if (DataContext is not CatalogPanelViewModel catalog || HighlightedProduct is not { } product)
            return false;
        if (catalog.SelectProductCommand.CanExecute(product))
            catalog.SelectProductCommand.Execute(product);
        return true;
    }

    public void FocusProductSearch()
    {
        ProductSearchBox.Focus();
        ProductSearchBox.SelectAll();
    }

    /// <summary>2026-09-10: пустой каталог (обычно — свежий автономный режим без единого
    /// добавленного товара, или локальная база без последнего sync) — вместо только «Обновить»
    /// (бесполезно без сервера) сразу ведём в Склад, где «Добавить товар» уже умеет писать
    /// локально при офлайне (см. LocalProductEditor).</summary>
    private void AddProductEmptyState_Click(object? sender, RoutedEventArgs e) =>
        App.AppHost?.Services.GetService<MainWindowHostBridge>()?.Window?.NavigateWarehouse();

    /// <summary>Вызывается окном, когда кассир нажимает стрелку, а фокус ещё нигде в
    /// каталоге не стоял (обычное состояние покоя — фокус на самом окне, чтобы сканер
    /// штрихкодов работал). "Входит" в сетку товаров: ставит курсор на товар, где он стоял
    /// в прошлый раз, или на первую плитку и передаёт клавиатурный фокус самой плитке —
    /// дальше стрелками управляет ProductsList_ArrowKeyTunnel.
    ///
    /// 2026-09-27, живой баг («нажимаешь стрелку — товар выделяется и не двигается»): раньше
    /// здесь фокус передавался самому ListBox, но ListBox в Avalonia 11 не фокусируемый
    /// (Focusable=false, фокус принимают только его ListBoxItem) — Focus() молча ничего не
    /// делал, фокус оставался на окне, и каждая следующая стрелка снова попадала сюда же:
    /// курсор уже стоял на первой плитке, а событие помечалось обработанным.</summary>
    public bool TryEnterCatalogNavigation()
    {
        // Раскладка «1С» прячет каталог — тогда стрелки не наши.
        if (!IsEffectivelyVisible || DataContext is not CatalogPanelViewModel catalog || catalog.Products.Count == 0)
            return false;

        var listBox = catalog.IsCardView ? CardsListBox : TableListBox;
        var index = catalog.SelectedProduct is { } selected ? catalog.Products.IndexOf(selected) : -1;
        return FocusTile(listBox, index >= 0 ? index : 0);
    }

    private void OpenFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CatalogPanelViewModel catalog)
            return;

        var filter = catalog.CreateFilterViewModel();
        var dialog = new FilterWindow(filter);
        if (PosDialogHost.Show(dialog, TopLevel.GetTopLevel(this) as Window) == true)
            catalog.ApplyAdvancedFilter(filter.BuildCriteria());
    }

    private void CardView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
            catalog.SetViewMode(CatalogViewMode.Cards);
    }

    private void TableView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
            catalog.SetViewMode(CatalogViewMode.Table);
    }

    /// <summary>Стрелки по плиткам каталога. Встроенная навигация ListBox + WrapPanel умеет только
    /// Влево/Вправо и только в пределах страницы, поэтому считаем сами по индексу: Влево/Вправо —
    /// соседняя плитка (с переносом через конец ряда), Вверх/Вниз — плитка того же столбца в
    /// соседнем ряду; число столбцов берётся из фактической раскладки. Шаг за первую/последнюю
    /// плитку страницы листает на соседнюю страницу. Стрелки с Ctrl/Shift/Alt не трогаем.</summary>
    private void ProductsList_ArrowKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        if (sender is not ListBox listBox || DataContext is not CatalogPanelViewModel catalog || listBox.ItemCount == 0)
            return;

        // Даже когда двигаться некуда (первая плитка первой страницы), стрелку забираем: иначе её
        // подхватит встроенная навигация и курсор уйдёт туда, где его не ждут.
        e.Handled = true;
        MoveKeyboardCursor(listBox, catalog, e.Key, e.Source as Visual);
    }

    private void MoveKeyboardCursor(ListBox listBox, CatalogPanelViewModel catalog, Key key, Visual? source)
    {
        var count = listBox.ItemCount;
        var current = IndexOfTileContaining(listBox, source);
        if (current < 0)
            current = listBox.SelectedIndex;
        if (current < 0 || current >= count)
        {
            FocusTile(listBox, 0);
            return;
        }

        var columns = listBox == CardsListBox ? CountColumns(listBox) : 1;
        var column = current % columns;
        var target = key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Up => current - columns,
            _ => current + columns,
        };

        if (target >= 0 && target < count)
        {
            FocusTile(listBox, target);
            return;
        }

        // Вниз, а под плиткой пусто, потому что последний ряд неполный, — на последнюю плитку,
        // листать страницу ещё рано.
        if (key == Key.Down && current / columns < (count - 1) / columns)
        {
            FocusTile(listBox, count - 1);
            return;
        }

        var forward = target >= count;
        var pageCommand = forward ? catalog.NextPageCommand : catalog.PreviousPageCommand;
        if (!pageCommand.CanExecute(null))
            return;

        pageCommand.Execute(null);
        // Плитки новой страницы должны получить размеры до того, как на них ставить фокус и
        // прокручивать к ним.
        listBox.UpdateLayout();
        var newCount = listBox.ItemCount;
        if (newCount == 0)
            return;

        var lastRowStart = (newCount - 1) / columns * columns;
        FocusTile(listBox, key switch
        {
            Key.Right => 0,
            Key.Left => newCount - 1,
            Key.Down => Math.Min(column, newCount - 1),
            _ => Math.Min(lastRowStart + column, newCount - 1),
        });
    }

    /// <summary>Ставит курсор на плитку: выделение (оно же SelectedProduct) и клавиатурный фокус
    /// на саму плитку, а не на кнопку внутри неё; прокручивает сетку так, чтобы плитку было видно.</summary>
    private static bool FocusTile(ListBox listBox, int index)
    {
        if (index < 0 || index >= listBox.ItemCount)
            return false;

        listBox.SelectedIndex = index;
        listBox.ScrollIntoView(index);
        if (listBox.ContainerFromIndex(index) is not { } tile || !tile.Focus(NavigationMethod.Directional))
            return false;

        tile.BringIntoView();
        return true;
    }

    private static int IndexOfTileContaining(ListBox listBox, Visual? source)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, listBox); visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem tile)
                return listBox.IndexFromContainer(tile);
        }
        return -1;
    }

    /// <summary>Сколько плиток в ряду сейчас: считаем плитки первого ряда по их координатам —
    /// ширина плитки зависит от темы, а ширина каталога от окна и разделителя.</summary>
    private static int CountColumns(ListBox listBox)
    {
        if (listBox.ContainerFromIndex(0) is not { } first)
            return 1;

        var columns = 0;
        for (var i = 0; i < listBox.ItemCount; i++)
        {
            if (listBox.ContainerFromIndex(i) is not { } tile || Math.Abs(tile.Bounds.Y - first.Bounds.Y) > 1)
                break;
            columns++;
        }
        return Math.Max(1, columns);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_boundViewModel is not null)
            _boundViewModel.Products.CollectionChanged -= OnProductsCollectionChanged;

        _boundViewModel = DataContext as CatalogPanelViewModel;
        if (_boundViewModel is null)
            return;

        _boundViewModel.Products.CollectionChanged += OnProductsCollectionChanged;
        LoadThumbnails(_boundViewModel.Products);
    }

    private void OnProductsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
            return;

        LoadThumbnails(e.NewItems.Cast<CatalogProductTileVm>());
    }

    /// <summary>
    /// Fetches + caches each product's photo the first time its tile appears in the
    /// catalog. No-op for tiles that already have a local ProductImagePath (download-once
    /// guard lives inside ProductThumbService), so re-filtering/searching is cheap.
    /// </summary>
    private static void LoadThumbnails(IEnumerable<CatalogProductTileVm> products)
    {
        var services = App.AppHost?.Services;
        if (services is null)
            return;

        var thumbs = services.GetRequiredService<ProductThumbService>();
        var authApi = services.GetRequiredService<IAuthApiService>();
        var apiBaseUrl = services.GetRequiredService<AppSettings>().ApiBaseUrl;

        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.ImageUrl) || !string.IsNullOrEmpty(product.ProductImagePath))
                continue;

            _ = thumbs.SetThumbAsync(
                Dispatcher.UIThread, authApi, apiBaseUrl, product.ImageUrl!, product, CancellationToken.None);
        }
    }
}
