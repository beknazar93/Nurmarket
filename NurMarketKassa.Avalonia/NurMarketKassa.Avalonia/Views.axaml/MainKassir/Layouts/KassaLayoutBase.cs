using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>Общая основа раскладок кассы (2026-09-28): «Табличная», «Карточки», «Минимал», «Профи».
///
/// Раскладка — только вид. Каталог, чек, оплата, смена и сканер — те же ViewModel, что у обычной
/// кассы (MainWindowViewModel.Catalog / Basket / SideMenu), бизнес-логика не копируется. Здесь —
/// то, что у всех раскладок одинаково:
/// • клавиатура каталога (<see cref="ICatalogKeyboardSurface"/>): окно кассы через неё двигает
///   рамку по плиткам стрелками, добавляет товар по Num +, убавляет по Num −, открывает поиск;
/// • кнопки меню «Ещё», у которых нет команды во ViewModel (копия чека, списание);
/// • «Наличные» / «Безнал» — то же «Оплатить», только окно оплаты открывается сразу на нужном способе.
///
/// Сканер раскладке не нужен: код ловит само окно кассы (IBarcodeInputService), пока фокус не
/// в текстовом поле, — поэтому никакое поле раскладки не забирает фокус при показе.</summary>
public class KassaLayoutBase : UserControl, ICatalogKeyboardSurface
{
    private readonly List<ListBox> _productLists = new();

    protected MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>Поле поиска товара раскладки (Ctrl+F, Esc его очищает — имя "ProductSearchBox").</summary>
    protected TextBox? SearchBox { get; set; }

    /// <summary>Подключает список плиток к клавиатуре кассы. Вызывать после InitializeComponent.</summary>
    protected void RegisterProductList(ListBox listBox)
    {
        _productLists.Add(listBox);
        CatalogKeyboardNavigation.Attach(listBox, () => Vm?.Catalog);
    }

    private ListBox? VisibleProductList => _productLists.FirstOrDefault(l => l.IsEffectivelyVisible);

    /// <summary>2026-10-19, К1 (доработка по снимку «Карточек»: справа пустая полоса ~110 px): плитки списка — на всю
    /// ширину ряда, как в «Классике» (CatalogPanelView.FitTilesToRowDesktop). Ширина плитки — ресурс LxTileWidth этой
    /// раскладки (разметка: Width="{DynamicResource LxTileWidth}"); столбцов — с округлением вверх от желаемой
    /// ширины, но плитка не уже 85 % желаемой и не уже 140. Только вид: список, команды и клавиатура прежние.</summary>
    protected void FitTilesToRow(ListBox listBox, double preferredWidth, double slotMargin)
    {
        Resources["LxTileWidth"] = preferredWidth;
        void Fit()
        {
            var scroller = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var available = (scroller is { Viewport.Width: > 1 } ? scroller.Viewport.Width : listBox.Bounds.Width) - 2;
            if (available < preferredWidth + slotMargin)
                return;
            var minWidth = Math.Max(140, preferredWidth * 0.85);
            var columns = Math.Max(1, (int)Math.Ceiling(available / (preferredWidth + slotMargin)));
            while (columns > 1 && available / columns - slotMargin < minWidth)
                columns--;
            var width = Math.Floor(available / columns - slotMargin);
            if (Resources.TryGetValue("LxTileWidth", out var old) && old is double w && Math.Abs(w - width) < 0.5)
                return;
            Resources["LxTileWidth"] = width;
        }
        listBox.SizeChanged += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
    }

    public bool TryEnterCatalogNavigation()
    {
        if (!IsEffectivelyVisible || Vm?.Catalog is not { } catalog || VisibleProductList is not { } list)
            return false;
        return CatalogKeyboardNavigation.TryEnter(catalog, list);
    }

    public CatalogProductTileVm? HighlightedProduct =>
        CatalogKeyboardNavigation.GetHighlightedProduct(this, _productLists.ToArray());

    public bool TryAddHighlightedProduct()
    {
        if (Vm?.Catalog is not { } catalog || HighlightedProduct is not { } product)
            return false;
        if (catalog.SelectProductCommand.CanExecute(product))
            catalog.SelectProductCommand.Execute(product);
        return true;
    }

    public void FocusProductSearch()
    {
        if (SearchBox is null)
            return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public bool ContainsFocus(Control? focused) =>
        _productLists.Any(list => CatalogKeyboardNavigation.IsWithin(focused, list));

    // ------------------------------------------------------------------ общие кнопки

    protected void PrintLastReceipt_Click(object? sender, RoutedEventArgs e) =>
        _ = BasketExtraActions.PrintLastReceiptAsync(sender as Control, Vm?.Basket);

    protected void WriteOff_Click(object? sender, RoutedEventArgs e) =>
        _ = BasketExtraActions.WriteOffAsync(this, Vm?.Basket);

    protected void Rental_Click(object? sender, RoutedEventArgs e) =>
        BasketExtraActions.Rental(Vm?.Basket);

    /// <summary>Количество в строке чека применяется и при уходе из поля, не только по Enter —
    /// кассир часто сразу жмёт «Оплатить» (то же, что BasketPanelView в «Классике», 2026-09-28).</summary>
    protected void QuantityInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NurMarketKassa.ViewModels.Main.CartLineItemVm line }
            && line.SetQuantityCommand?.CanExecute(line) == true)
            line.SetQuantityCommand.Execute(line);
    }

    protected void PayCash_Click(object? sender, RoutedEventArgs e) => PayWith("cash");

    protected void PayCashless_Click(object? sender, RoutedEventArgs e) => PayWith("transfer");

    private void PayWith(string method)
    {
        if (Vm?.Basket is not { } basket || !basket.PayCommand.CanExecute(null))
            return;
        basket.PreferredPaymentMethod = method;
        basket.PayCommand.Execute(null);
    }

    /// <summary>2026-10-20, проверка фильтров: «Фильтр» есть и в раскладках (было только в «Классике») — то же окно; число на
    /// кнопке — сколько условий включено (Catalog.ActiveFilterCount), «✕» — сбросить.</summary>
    protected void OpenFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Catalog is { } catalog)
            CatalogPanelView.OpenFilterFor(catalog, this);
    }

    protected void ResetFilter_Click(object? sender, RoutedEventArgs e) => Vm?.Catalog?.ResetAdvancedFilter();

    /// <summary>Вкладка каталога (Все / Весовые / Штучные / …) по номеру из Tag кнопки.</summary>
    protected void CatalogTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Catalog is { } catalog && sender is Control { Tag: string tag } && int.TryParse(tag, out var index))
            catalog.SelectedTabIndex = index;
    }
}
