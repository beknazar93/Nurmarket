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
        TableGrid.SizeChanged += (_, _) => UpdateTableDensity();
        // 2026-10-04, редизайн под любые Android-устройства: плитки — по ширине ряда (см. FitTilesToRow).
        // 2026-10-19, К1 (аудит 19.10, проблема 3: на 1366 три столбца и пустая полоса ~150 px справа): и в Windows/Linux
        // плитки растягиваются на всю ширину ряда (FitTilesToRowDesktop) — 4 столбца на 1366, на 1024 — сколько влезает
        // без обрезки. Размер темы и «Размер карточек» по-прежнему задают желаемую ширину.
        if (!OperatingSystem.IsAndroid())
        {
            CardsListBox.SizeChanged += (_, _) => Dispatcher.UIThread.Post(FitTilesToRowDesktop, DispatcherPriority.Background);
            ResourcesChanged += (_, _) => Dispatcher.UIThread.Post(FitTilesToRowDesktop, DispatcherPriority.Background);
            // 2026-10-20, живая проверка («верхний ряд плиток срезан»): новая страница каталога открывалась
            // прокрученной туда же, где была прежняя, — как на Android (ниже), показываем её с начала.
            CatalogPanelViewModel? pagedVm = null;
            void OnPageChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                // 2026-10-21: и после «Фильтр → Применить» / «✕ Сбросить» (страница остаётся первой, а список был
                // прокручен на середину) — с начала (пакет 6, п. 15).
                if (e.PropertyName is not (nameof(CatalogPanelViewModel.CurrentPage) or nameof(CatalogPanelViewModel.ActiveFilterTip)))
                    return;
                foreach (var list in new[] { CardsListBox, TableListBox })
                {
                    list.FindAncestorOfType<ScrollViewer>()?.ScrollToHome();
                    list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToHome();
                }
            }
            DataContextChanged += (_, _) =>
            {
                if (pagedVm is not null)
                    pagedVm.PropertyChanged -= OnPageChanged;
                pagedVm = DataContext as CatalogPanelViewModel;
                if (pagedVm is not null)
                    pagedVm.PropertyChanged += OnPageChanged;
            };
        }
        // 2026-11-06 (В7, Д4-03): признак «одна строка Скан или поиск» (файл single-input-row.on) — поле поиска принимает и скан, и ручной ввод кода/названия:
        // Enter, не разобранный окном как скан сканера (оно разбирает быстрый набор раньше и помечает Enter обработанным), добавляет введённое в чек, как «Добавить».
        if (KassaMainViewRules.SingleInputRowEnabled())
        {
            ProductSearchBox.Watermark = KassaMainViewRules.SearchWatermark(true);
            ProductSearchBox.KeyDown += ProductSearchBox_KeyDown;
        }
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
                    PrevPageButton.Bind(ContentControl.ContentProperty, PrevPageButton.GetResourceObservable("csh.catalog.prevPage"));
                    NextPageButton.Bind(ContentControl.ContentProperty, NextPageButton.GetResourceObservable("csh.catalog.nextPage"));
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

    private void ProductSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || e.Handled)
            return;
        var text = ProductSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || DataContext is not CatalogPanelViewModel catalog
            || (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)?.Basket is not { } basket)
            return;
        e.Handled = true;
        catalog.SearchText = "";
        basket.EnqueueBarcode(text);
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
    /// <summary>2026-10-19, К1: плитки на всю ширину ряда на компьютере. Столбцов — с округлением вверх от желаемой
    /// ширины темы (190 → на 1366 четыре по ~175), но плитка не уже 85 % желаемой и не уже 150 (иначе цена «12 450,00»
    /// не помещается) — тогда на столбец меньше, а плитки шире. Высота — как у темы (у Android она ужимается, здесь
    /// нет: 204 посчитаны по содержимому карточки). Своё значение кладётся в ресурсы самой панели — другие окна
    /// (группы быстрых клавиш) берут прежний размер из приложения.</summary>
    private void FitTilesToRowDesktop()
    {
        if (Application.Current is not { } app
            || !app.TryGetResource("CatalogTileWidth", app.ActualThemeVariant, out var wValue) || wValue is not double preferredWidth
            || !app.TryGetResource("CatalogTileHeight", app.ActualThemeVariant, out var hValue) || hValue is not double preferredHeight
            || preferredWidth <= 0)
            return;
        var scroller = CardsListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var available = scroller is { Viewport.Width: > 1 } ? scroller.Viewport.Width : CardsListBox.Bounds.Width;
        available -= 2; // запас на округление — иначе последняя плитка ряда переносится
        if (available < preferredWidth + TileSlotMargin)
            return;
        // 2026-11-05 (аудит №4, Д4-36): в тесном окне (класс окна "compact" — моноблок 1024/1366, масштаб 125 %; "shortscreen" — ниже 640)
        // плитки ниже и уже: на 1024×700 было 4 товара, на 125 % — 6. Название берёт до трёх строк, цена и остаток — как были.
        var dense = _hostWindow is { } hw && (hw.Classes.Contains("compact") || hw.Classes.Contains("shortscreen"));
        var slotWidth = dense ? Math.Max(150, preferredWidth * 0.8) : preferredWidth;
        var tileHeight = dense ? Math.Round(Math.Max(140, preferredHeight * 0.72)) : preferredHeight;
        var minWidth = dense ? Math.Max(140, preferredWidth * 0.7) : Math.Max(150, preferredWidth * 0.85);
        var columns = Math.Max(1, (int)Math.Ceiling(available / (slotWidth + TileSlotMargin)));
        while (columns > 1 && available / columns - TileSlotMargin < minWidth)
            columns--;
        var width = Math.Floor(available / columns - TileSlotMargin);
        if (Resources.TryGetValue("CatalogTileWidth", out var oldW) && oldW is double ow && Math.Abs(ow - width) < 0.5
            && Resources.TryGetValue("CatalogTileHeight", out var oldH) && oldH is double oh && Math.Abs(oh - tileHeight) < 0.5)
            return;
        Resources["CatalogTileWidth"] = width;
        Resources["CatalogTileHeight"] = tileHeight;
    }

    // 2026-11-05 (Д4-36): окно кассы — по его классам "compact"/"shortscreen" плитки каталога становятся плотнее (см. FitTilesToRowDesktop).
    private Window? _hostWindow;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (OperatingSystem.IsAndroid())
            return;
        if (_hostWindow is not null)
            _hostWindow.Classes.CollectionChanged -= OnHostClassesChanged;
        _hostWindow = e.Root as Window;
        if (_hostWindow is not null)
            _hostWindow.Classes.CollectionChanged += OnHostClassesChanged;
        HookWidthSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_hostWindow is not null)
            _hostWindow.Classes.CollectionChanged -= OnHostClassesChanged;
        _hostWindow = null;
        if (_widthSource is not null)
            _widthSource.SizeChanged -= OnWidthSourceChanged;
        _widthSource = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ── 2026-11-06 (В7, Д4-36): автосписок. Окно уже 1280 (логических точек, после масштаба интерфейса) — каталог сам показывается списком (строка ~48 px:
    // название · код · остаток · цена), а не плитками: на 1366 при 125 % плиток было 6, строк списка — 12–14. Выбор кассира «Плитки» уважаем до конца сеанса.
    // На Android не включается: там плитки по две в ряд — задуманный вид. Решает KassaMainViewRules.UseAutoList (с запасом на границе, чтобы вид не мигал).
    private bool _autoList;
    private bool _userChoseCards;
    private Control? _widthSource;

    /// <summary>Источник «логической» ширины окна: содержимое LayoutTransformControl (масштаб интерфейса) — его размер без масштаба; иначе само окно.</summary>
    private void HookWidthSource()
    {
        if (OperatingSystem.IsAndroid())
            return;
        var source = (this.FindAncestorOfType<LayoutTransformControl>()?.Child as Control) ?? _hostWindow;
        if (!ReferenceEquals(source, _widthSource))
        {
            if (_widthSource is not null)
                _widthSource.SizeChanged -= OnWidthSourceChanged;
            _widthSource = source;
            if (_widthSource is not null)
                _widthSource.SizeChanged += OnWidthSourceChanged;
        }
        RecomputeAutoList();
    }

    private void OnWidthSourceChanged(object? sender, SizeChangedEventArgs e) => RecomputeAutoList();

    private void RecomputeAutoList()
    {
        var want = !OperatingSystem.IsAndroid()
            && DataContext is CatalogPanelViewModel { IsCardView: true }
            && KassaMainViewRules.UseAutoList(_widthSource?.Bounds.Width ?? 0, _autoList, _userChoseCards);
        _autoList = want;
        UpdateViewVisibility();
    }

    /// <summary>Плитки или список: список — выбран кассиром («Таблица») или включён автоматически на узком окне.</summary>
    private void UpdateViewVisibility()
    {
        var table = _autoList || DataContext is CatalogPanelViewModel { IsTableView: true };
        CardsScroll.IsVisible = !table;
        TableGrid.IsVisible = table;
        if (_autoList)
        {
            if (!CatalogRoot.Classes.Contains("autolist"))
                CatalogRoot.Classes.Add("autolist");
        }
        else
        {
            CatalogRoot.Classes.Remove("autolist");
        }
    }

    private bool _tblNarrow, _tblTight;

    /// <summary>2026-11-07 (живая проверка 1.17.81, Б-A1): плотность списка по его ширине — узкий (&lt; 760): штрихкод скрыт, остаток под ценой; тесный (&lt; 400): ещё и «сом» скрыт.
    /// Так цена (колонка с постоянной шириной) не обрезается, сжимается только название. Гистерезис 24 px — граница не «мигает».</summary>
    internal void UpdateTableDensity()
    {
        var w = TableGrid.Bounds.Width;
        if (w <= 1)
            return;
        var (narrow, tight) = TableDensity(w, _tblNarrow, _tblTight);
        _tblNarrow = narrow;
        _tblTight = tight;
        SetClass(CatalogRoot, "tbl-narrow", narrow);
        SetClass(CatalogRoot, "tbl-tight", tight);
        static void SetClass(StyledElement e, string c, bool on)
        {
            if (on && !e.Classes.Contains(c))
                e.Classes.Add(c);
            else if (!on)
                e.Classes.Remove(c);
        }
    }

    /// <summary>Чистая функция плотности: (узкий, тесный) по ширине списка; текущие значения дают гистерезис.</summary>
    public static (bool Narrow, bool Tight) TableDensity(double width, bool wasNarrow, bool wasTight) =>
        (width < (wasNarrow ? 784 : 760), width < (wasTight ? 424 : 400));

    /// <summary>2026-11-06 (В7, Д4-08): значок типа на плитках («Весовой», «Комплект») — только во вкладке «Все товары» и при поиске (он идёт по всему каталогу).</summary>
    private void UpdateTypeBadgeVisibility()
    {
        var hide = DataContext is CatalogPanelViewModel { SelectedTabIndex: not 0, IsSearchActive: false };
        if (hide)
        {
            if (!CatalogRoot.Classes.Contains("hide-type-badge"))
                CatalogRoot.Classes.Add("hide-type-badge");
        }
        else
        {
            CatalogRoot.Classes.Remove("hide-type-badge");
        }
    }

    private void OnCatalogPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CatalogPanelViewModel.IsCardView):
            case nameof(CatalogPanelViewModel.IsTableView):
                RecomputeAutoList();
                break;
            case nameof(CatalogPanelViewModel.SelectedTabIndex):
            case nameof(CatalogPanelViewModel.IsSearchActive):
                UpdateTypeBadgeVisibility();
                break;
        }
    }

    private void OnHostClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(FitTilesToRowDesktop, DispatcherPriority.Background);

    /// <summary>2026-10-19, К1: правая кнопка мыши или долгое нажатие по плитке — карточка товара (как значок «i»),
    /// только там, где значок показывается (одежда и услуги или у товара есть описание). Товар в чек не добавляется.</summary>
    private void Tile_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CatalogProductTileVm product
            || DataContext is not CatalogPanelViewModel vm
            || !(vm.ShowProductDetails || !string.IsNullOrEmpty(product.Description)))
            return;
        e.Handled = true;
        ProductDetails_Click(sender, new RoutedEventArgs());
    }

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

        // 2026-11-06 (В7, Д4-36): в автосписке (узкое окно) на экране список, а не плитки.
        return CatalogKeyboardNavigation.TryEnter(catalog, catalog.IsCardView && !_autoList ? CardsListBox : TableListBox);
    }

    private void OpenFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
            OpenFilterFor(catalog, this);
    }

    /// <summary>2026-10-20, проверка фильтров: окно «Фильтр» — одно для «Классики» и раскладок (KassaLayoutBase).</summary>
    internal static void OpenFilterFor(CatalogPanelViewModel catalog, Visual owner)
    {
        var filter = catalog.CreateFilterViewModel();
        var dialog = new FilterWindow(filter);
        if (PosDialogHost.Show(dialog, TopLevel.GetTopLevel(owner) as Window) == true)
            catalog.ApplyAdvancedFilter(filter.BuildCriteria());
    }

    /// <summary>2026-10-20: «✕» рядом с «Фильтр» — сбросить условия окна «Фильтр» (поиск остаётся).</summary>
    private void ResetFilter_Click(object? sender, RoutedEventArgs e) => (DataContext as CatalogPanelViewModel)?.ResetAdvancedFilter();

    private void CardView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
        {
            _userChoseCards = true; // 2026-11-06 (Д4-36): кассир сам выбрал плитки — автосписок до конца сеанса не включаем
            catalog.SetViewMode(CatalogViewMode.Cards);
            RecomputeAutoList();
        }
    }

    private void TableView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
        {
            _userChoseCards = false;
            catalog.SetViewMode(CatalogViewMode.Table);
            RecomputeAutoList();
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.Products.CollectionChanged -= OnProductsCollectionChanged;
            _boundViewModel.PropertyChanged -= OnCatalogPropertyChanged;
        }

        _boundViewModel = DataContext as CatalogPanelViewModel;
        // 2026-11-06 (В7): плитки/список и значки типа зависят от модели — пересчитываем при смене модели (в том числе когда её нет).
        RecomputeAutoList();
        UpdateTypeBadgeVisibility();
        if (_boundViewModel is null)
            return;

        _boundViewModel.Products.CollectionChanged += OnProductsCollectionChanged;
        _boundViewModel.PropertyChanged += OnCatalogPropertyChanged;
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
    private void LoadThumbnails(IEnumerable<CatalogProductTileVm> products)
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

            _ = LoadThumbGatedAsync(thumbs, authApi, apiBaseUrl, product);
        }
    }

    // 2026-11-06 (В7, фото плиток): страница из 50 плиток раньше запускала 50 загрузок/расшифровок сразу — диск и процессор забивались, список «дёргался».
    // Теперь не больше четырёх одновременно; товар, который уже ушёл со страницы (другая страница, поиск), не грузим. Слот под фото уже занят (Border.tile-photo).
    private static readonly SemaphoreSlim ThumbGate = new(4);

    private async Task LoadThumbGatedAsync(ProductThumbService thumbs, IAuthApiService authApi, string apiBaseUrl, CatalogProductTileVm product)
    {
        await ThumbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stillShown = await Dispatcher.UIThread.InvokeAsync(() => _boundViewModel?.Products.Contains(product) == true);
            if (!stillShown)
                return;
            await thumbs.SetThumbAsync(Dispatcher.UIThread, authApi, apiBaseUrl, product.ImageUrl!, product, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миниатюра товара не загрузилась: {ex.GetType().Name}: {ex.Message}", "UI");
        }
        finally
        {
            ThumbGate.Release();
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
