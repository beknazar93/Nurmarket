using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    /// штрихкодов работал). "Входит" в сетку товаров: если курсор ещё не установлен,
    /// ставит его на первую плитку, затем передаёт клавиатурный фокус списку — дальше
    /// стрелками управляет уже сам ListBox/WrapPanel.</summary>
    public bool TryEnterCatalogNavigation()
    {
        if (DataContext is not CatalogPanelViewModel catalog || catalog.Products.Count == 0)
            return false;

        catalog.SelectedProduct ??= catalog.Products[0];
        var listBox = catalog.IsCardView ? CardsListBox : TableListBox;
        listBox.Focus();
        return true;
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

    /// <summary>Enter/Space добавляют товар под клавиатурным курсором в чек — то же самое
    /// действие, что и клик по плитке мышью. Влево/Вправо в карточном виде обрабатывает сам
    /// ListBox/WrapPanel. Вверх/Вниз — нет: встроенная у WrapPanel директивная навигация
    /// умеет уверенно вычислять только Влево/Вправо (по линейному индексу), а "соседний ряд"
    /// для Вверх/Вниз — нет, поэтому считаем его вручную по фактическим координатам плиток.</summary>
    private void ProductsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CatalogPanelViewModel catalog || sender is not ListBox listBox)
            return;

        if (listBox == CardsListBox && (e.Key == Key.Up || e.Key == Key.Down))
        {
            if (TryMoveVertically(listBox, e.Key == Key.Down))
                e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Space)
            return;
        if (listBox.SelectedItem is not CatalogProductTileVm product)
            return;

        e.Handled = true;
        if (catalog.SelectProductCommand.CanExecute(product))
            catalog.SelectProductCommand.Execute(product);
    }

    /// <summary>Ищет среди уже отрисованных плиток ближайшую по X в ближайшем ряду выше/ниже
    /// текущей (по фактическим Bounds, а не по линейному индексу элемента).</summary>
    private static bool TryMoveVertically(ListBox listBox, bool down)
    {
        if (listBox.ItemCount == 0)
            return false;

        var currentIndex = listBox.SelectedIndex;
        if (currentIndex < 0)
            currentIndex = 0;

        if (listBox.ContainerFromIndex(currentIndex) is not Control currentContainer)
            return false;

        var currentBounds = currentContainer.Bounds;
        var currentCenterX = currentBounds.X + currentBounds.Width / 2;
        var currentY = currentBounds.Y;

        var bestIndex = -1;
        Control? bestContainer = null;
        var bestAbsDeltaY = double.MaxValue;
        var bestDeltaX = double.MaxValue;

        for (var i = 0; i < listBox.ItemCount; i++)
        {
            if (i == currentIndex)
                continue;
            if (listBox.ContainerFromIndex(i) is not Control container)
                continue;

            var bounds = container.Bounds;
            var deltaY = bounds.Y - currentY;
            if (down ? deltaY <= 0.5 : deltaY >= -0.5)
                continue;

            var absDeltaY = System.Math.Abs(deltaY);
            var centerX = bounds.X + bounds.Width / 2;
            var deltaX = System.Math.Abs(centerX - currentCenterX);

            // Ближайший по Y ряд, а среди равных — ближайший по X (та же "колонка").
            if (absDeltaY < bestAbsDeltaY - 0.5 ||
                (System.Math.Abs(absDeltaY - bestAbsDeltaY) < 0.5 && deltaX < bestDeltaX))
            {
                bestAbsDeltaY = absDeltaY;
                bestDeltaX = deltaX;
                bestIndex = i;
                bestContainer = container;
            }
        }

        if (bestIndex < 0 || bestContainer is null)
            return false;

        listBox.SelectedIndex = bestIndex;
        bestContainer.Focus();
        return true;
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
