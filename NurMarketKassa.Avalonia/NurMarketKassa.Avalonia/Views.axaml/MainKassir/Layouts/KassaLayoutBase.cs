using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    protected void PayCash_Click(object? sender, RoutedEventArgs e) => PayWith("cash");

    protected void PayCashless_Click(object? sender, RoutedEventArgs e) => PayWith("transfer");

    private void PayWith(string method)
    {
        if (Vm?.Basket is not { } basket || !basket.PayCommand.CanExecute(null))
            return;
        basket.PreferredPaymentMethod = method;
        basket.PayCommand.Execute(null);
    }

    /// <summary>Вкладка каталога (Все / Весовые / Штучные / …) по номеру из Tag кнопки.</summary>
    protected void CatalogTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Catalog is { } catalog && sender is Control { Tag: string tag } && int.TryParse(tag, out var index))
            catalog.SelectedTabIndex = index;
    }
}
