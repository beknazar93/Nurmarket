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

public partial class CatalogPanelView : UserControl, ICatalogKeyboardSurface
{
    private CatalogPanelViewModel? _boundViewModel;

    public CatalogPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel: стрелки внутри списка разбираем сами раньше встроенной обработки ListBox —
        // она умеет только Влево/Вправо в пределах страницы. Сама логика с 2026-09-28 — в
        // CatalogKeyboardNavigation: её же используют остальные раскладки кассы.
        CatalogKeyboardNavigation.Attach(CardsListBox, () => DataContext as CatalogPanelViewModel);
        CatalogKeyboardNavigation.Attach(TableListBox, () => DataContext as CatalogPanelViewModel);
        // 2026-10-04, редизайн под любые Android-устройства: плитки — по ширине ряда (см. FitTilesToRow).
        if (OperatingSystem.IsAndroid())
        {
            CardsListBox.SizeChanged += (_, _) => Dispatcher.UIThread.Post(FitTilesToRow, DispatcherPriority.Background);
            // 2026-10-05, проверка 1.17.49 на телефоне: номера страниц налезали на «Вперёд ›» — на узком экране
            // у кнопок «Назад» / «Вперёд» остаются только стрелки.
            bool? narrowPager = null;
            SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < 520;
                if (narrow == narrowPager)
                    return;
                narrowPager = narrow;
                if (narrow)
                {
                    PrevPageButton.Content = "‹";
                    NextPageButton.Content = "›";
                }
                else
                {
                    PrevPageButton.Bind(ContentControl.ContentProperty, this.GetResourceObservable("csh.catalog.prevPage"));
                    NextPageButton.Bind(ContentControl.ContentProperty, this.GetResourceObservable("csh.catalog.nextPage"));
                }
            };
            // 2026-10-05, «при смене страницы каталога жёстко тормозит»: время показа страницы — в журнал.
            DataContextChanged += (_, _) =>
            {
                if (DataContext is CatalogPanelViewModel vm)
                    vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName != nameof(CatalogPanelViewModel.CurrentPage))
                            return;
                        var page = vm.CurrentPage;
                        // 2026-10-05, проверка 1.17.49 на телефоне: новая страница открывалась прокрученной туда же,
                        // где была прежняя (видна середина страницы) — показываем её с начала.
                        // Плитки лежат во внешнем ScrollViewer (вокруг ListBox) — сбрасываем и его, и внутренний.
                        foreach (var list in new[] { CardsListBox, TableListBox })
                        {
                            list.FindAncestorOfType<ScrollViewer>()?.ScrollToHome();
                            list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToHome();
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        // Этапы: подготовка (плитки созданы), раскладка, кадр нарисован.
                        long prepared = -1, laidOut = -1;
                        Dispatcher.UIThread.Post(() => prepared = sw.ElapsedMilliseconds, DispatcherPriority.Send);
                        void OnLayout(object? s, EventArgs a)
                        {
                            LayoutUpdated -= OnLayout;
                            laidOut = sw.ElapsedMilliseconds;
                        }
                        LayoutUpdated += OnLayout;
                        Dispatcher.UIThread.Post(() => PosLogger.Log(
                            $"Каталог: страница {page} показана за {sw.ElapsedMilliseconds} мс ({vm.Products.Count} товаров; " +
                            $"подготовка {prepared} мс, раскладка {laidOut} мс).", "UI"),
                            DispatcherPriority.Background);
                    };
            };
        }
    }

    /// <summary>2026-10-05, владелец (Android): «добавь сканер при поиске товара» — код с камеры уходит в поиск
    /// каталога (найдётся по штрихкоду, как при ручном вводе).</summary>
    private async void SearchCameraScan_Click(object? sender, RoutedEventArgs e)
    {
        var code = await CameraScan.ScanCodeAsync().ConfigureAwait(true);
        if (code is null || DataContext is not CatalogPanelViewModel vm)
            return;
        PosLogger.Log($"Камера-сканер: код в поиск каталога ({code.Length} симв.).", "CART");
        vm.SearchText = code;
    }

    /// <summary>Плитка с отступами: Margin="6" у плитки в шаблоне.</summary>
    private const double TileSlotMargin = 12;

    /// <summary>Уже этого плитка не делается — название и цена перестают помещаться.</summary>
    private const double MinFluidTileWidth = 140;

    /// <summary>2026-10-04, редизайн под любые Android-устройства. Плитки были одной ширины (тема × «Размер
    /// карточек»): на телефоне в колонку 430 точек помещалась одна плитка, на планшете по бокам сетки
    /// оставались пустые поля. Теперь число плиток в ряду — сколько помещается при выбранном размере
    /// (на телефоне не меньше двух), а ширина плитки растягивается на весь ряд. Высота — как была, на узких
    /// плитках немного ниже. Только Android: в Windows размер плиток — настройка кассира, как раньше.</summary>
    private void FitTilesToRow()
    {
        if (Application.Current is not { } app
            || !app.TryGetResource("CatalogTileWidth", app.ActualThemeVariant, out var wValue) || wValue is not double preferredWidth
            || !app.TryGetResource("CatalogTileHeight", app.ActualThemeVariant, out var hValue) || hValue is not double preferredHeight
            || preferredWidth <= 0)
            return;
        var scroller = CardsListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var available = scroller is { Viewport.Width: > 1 } ? scroller.Viewport.Width : CardsListBox.Bounds.Width;
        available -= 2; // запас на округление — иначе последняя плитка ряда переносится
        if (available < MinFluidTileWidth)
            return;
        var columns = Math.Max(1, (int)Math.Floor(available / (preferredWidth + TileSlotMargin)));
        if (columns < 2 && available / 2 - TileSlotMargin >= MinFluidTileWidth)
            columns = 2;
        var width = Math.Floor(available / columns - TileSlotMargin);
        var height = Math.Round(preferredHeight * Math.Clamp(width / preferredWidth, 0.85, 1.0));
        if (Resources.TryGetValue("CatalogTileWidth", out var oldW) && oldW is double ow && Math.Abs(ow - width) < 0.5
            && Resources.TryGetValue("CatalogTileHeight", out var oldH) && oldH is double oh && Math.Abs(oh - height) < 0.5)
            return;
        Resources["CatalogTileWidth"] = width;
        Resources["CatalogTileHeight"] = height;
    }

    /// <summary>Клавиатурный курсор стоит на плитке каталога (кассир ведёт его стрелками, рамка
    /// видна). Фокус, полученный мышью (клик по плитке или мимо кнопки в её край), курсором не
    /// считается. Товар под рамкой добавляет Num + (<see cref="TryAddHighlightedProduct"/>) —
    /// 2026-09-27, владелец: «добавление товара из каталога не через Enter, а через + на нумпаде»;
    /// Enter теперь всегда оплачивает.</summary>
    public bool HasKeyboardCursor => HighlightedProduct is not null;

    /// <summary>Товар под клавиатурной рамкой; null — рамки нет.</summary>
    public CatalogProductTileVm? HighlightedProduct =>
        CatalogKeyboardNavigation.GetHighlightedProduct(this, CardsListBox, TableListBox);

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

    /// <summary>Фокус где-то внутри каталога (после клика по плитке реальный фокус часто оседает
    /// на кнопке самой плитки, а не на ListBox) — стрелки тогда разбирает сам список.</summary>
    public bool ContainsFocus(Control? focused) => CatalogKeyboardNavigation.IsWithin(focused, this);

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
    /// дальше стрелками управляет CatalogKeyboardNavigation.
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

        return CatalogKeyboardNavigation.TryEnter(catalog, catalog.IsCardView ? CardsListBox : TableListBox);
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

    /// <summary>2026-10-01: «Подробнее» на плитке — карточка товара с описанием продавца
    /// (ProductDetailsWindow). Клик по кнопке не добавляет товар в чек: добавить можно из карточки.</summary>
    private async void ProductDetails_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not CatalogProductTileVm product)
            return;
        var vm = DataContext as CatalogPanelViewModel;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new ProductDetailsWindow(product, () => vm?.SelectProductCommand.Execute(product));
        if (owner != null)
            await window.ShowDialog(owner).ConfigureAwait(true);
        else
            window.Show();
    }
}
