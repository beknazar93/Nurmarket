using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-19 («Неудобства от владельца», п. 10): выбор товаров галочками и панель массовых действий на вкладке «Товары».
/// Галочка в шапке таблицы отмечает/снимает все товары страницы; пока отмечен хоть один товар, на месте строки фильтров стоит панель
/// «Выбрано: N · Ценники и штрихкоды · Снять выбор». «Ценники и штрихкоды» открывает вкладку «Штрихкоды» с этими товарами.
/// 2026-11-22 (владелец: «в товарах чек бокс выделение тоже верни как было раньше»): галочки и панель возвращены (в 1.17.92 их убрали); набор отмеченных — <see cref="_printPicked"/>.
/// Здесь же — блок пустого состояния списка товаров с кнопками действий.</summary>
public partial class WarehouseWindow
{
    private Button? _bulkBarPrintButton;
    private CheckBox? _selectPageCheck;

    private void InitBulkBar()
    {
        BulkBarClearButton.Content = Tr.T("Снять выбор", "Тандоону алып салуу", "Clear selection", "Seçimi kaldır", "Tanlovni bekor qilish");
        _bulkBarPrintButton = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), Focusable = false };
        _bulkBarPrintButton.Classes.Add("PrimaryButton");
        _bulkBarPrintButton.Click += BulkBarPrint_Click;
        BulkBarActions.Children.Add(_bulkBarPrintButton);
        // Страница сменилась (поиск, фильтр, листание) — галочка шапки показывает состояние новой страницы.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.PagedProducts))
                SyncSelectPageCheck();
        };
        UpdateBulkBar();
    }

    /// <summary>2026-11-22: «Ценники и штрихкоды» в панели — вкладка «Штрихкоды» с отмеченными товарами (право и покупку проверяет вкладка).</summary>
    private void BulkBarPrint_Click(object? sender, RoutedEventArgs e) => OpenBarcodesTab(_printPicked.ToList());

    /// <summary>Заголовок колонки «Название»: галочка «выбрать все на странице» + прежняя надпись со стрелкой сортировки (вызывает InitProductSort).</summary>
    private Control SelectAllHeader(Control title)
    {
        var box = new CheckBox { Width = 44, MinWidth = 0, MinHeight = 44, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false }; // 2026-11-22: зона касания 44 px (сенсор)
        var text = Tr.T("Выбрать все товары на странице", "Барактагы бардык товарларды тандоо", "Select all products on the page", "Sayfadaki tüm ürünleri seç", "Sahifadagi barcha mahsulotlarni tanlash");
        AutomationProperties.SetName(box, text);
        ToolTip.SetTip(box, text);
        box.Click += SelectPage_Click;
        // Нажатие на галочку не должно доходить до заголовка (иначе он ещё и сортирует колонку).
        box.PointerPressed += (_, e) => e.Handled = true;
        box.PointerReleased += (_, e) => e.Handled = true;
        _selectPageCheck = box;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(title, 1);
        grid.Children.Add(box);
        grid.Children.Add(title);
        return grid;
    }

    private void SelectPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box)
            return;
        var check = box.IsChecked == true;
        foreach (var p in _viewModel.PagedProducts)
        {
            if (check)
                _printPicked.Add(p.Id);
            else
                _printPicked.Remove(p.Id);
        }
        SyncRowChecks();
        UpdatePrintPickButton();
        e.Handled = true; // нажатие на галочку не сортирует колонку
    }

    private void BulkBarClear_Click(object? sender, RoutedEventArgs e)
    {
        _printPicked.Clear();
        SyncRowChecks();
        UpdatePrintPickButton();
    }

    /// <summary>Галочки видимых строк — по набору отмеченных (после «выбрать все» / «снять выбор»).</summary>
    private void SyncRowChecks()
    {
        // 2026-11-22: и в списке, и в плитках (у плиток тоже есть галочка «В печать»).
        foreach (var box in ProductsGrid.GetVisualDescendants().Concat(ProductsTilesScroll.GetVisualDescendants()).OfType<CheckBox>())
            if (box.DataContext is CatalogProductTileVm product && !ReferenceEquals(box, _selectPageCheck))
                box.IsChecked = _printPicked.Contains(product.Id);
    }

    private void SyncSelectPageCheck()
    {
        if (_selectPageCheck is null)
            return;
        var page = _viewModel.PagedProducts;
        var picked = page.Count(p => _printPicked.Contains(p.Id));
        // Все отмечены — галочка, часть — «чёрточка» (третье состояние только для показа), никто — пусто.
        _selectPageCheck.IsChecked = page.Count > 0 && picked == page.Count ? true : picked > 0 ? null : false;
    }

    /// <summary>Панель массовых действий: видна при отмеченных товарах; строка фильтров под ней на это время прозрачна и не нажимается (высота ряда не меняется).</summary>
    private void UpdateBulkBar()
    {
        var n = _printPicked.Count;
        BulkBar.IsVisible = n > 0;
        ProductsFilterRow.Opacity = n > 0 ? 0 : 1;
        ProductsFilterRow.IsHitTestVisible = n == 0;
        if (n > 0)
        {
            BulkBarCount.Text = Tr.T($"Выбрано: {n}", $"Тандалды: {n}", $"Selected: {n}", $"Seçildi: {n}", $"Tanlandi: {n}");
            if (_bulkBarPrintButton is not null)
                _bulkBarPrintButton.Content = Tr.T("Ценники и штрихкоды", "Баа белгилери жана штрих-коддор", "Price tags and barcodes", "Fiyat etiketleri ve barkodlar", "Narx yorliqlari va shtrix-kodlar");
        }
        SyncSelectPageCheck();
    }

    /// <summary>Пустой список товаров: значок, заголовок, подсказка и кнопки действий (общий вид — <see cref="StateBlock.Empty"/>).</summary>
    private Control BuildProductsEmpty(bool filtered, bool compact)
    {
        Control Head(string title, string hint)
        {
            if (!compact)
                return StateBlock.Empty(title, hint);
            var text = new TextBlock { Text = title + ". " + hint, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushText"));
            return text;
        }
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (filtered)
        {
            panel.Children.Add(Head(
                Tr.T("Ничего не найдено", "Эчтеке табылган жок", "Nothing found", "Hiçbir şey bulunamadı", "Hech narsa topilmadi"),
                Tr.T("Измените поиск или фильтры.", "Издөөнү же чыпкаларды өзгөртүңүз.", "Change the search or the filters.", "Aramayı veya filtreleri değiştirin.", "Qidiruv yoki filtrlarni o'zgartiring.")));
            buttons.Children.Add(EmptyAction(Tr.T("Сбросить поиск и фильтры", "Издөөнү жана чыпкаларды тазалоо", "Reset search and filters", "Aramayı ve filtreleri sıfırla", "Qidiruv va filtrlarni tozalash"), primary: false, (_, _) =>
            {
                _viewModel.ProductSearchText = "";
                ResetSaleUnitFilter();
                SetStockFilter("all");
            }));
        }
        else
        {
            // 2026-11-21: импорт списка из файла — только в программе владельца; в кассе вместо кнопки — пояснение ImportAccess.KassaNote.
            if (ImportAccess.Allowed)
            {
                panel.Children.Add(Head(
                    Tr.T("Загрузите товары", "Товарларды жүктөңүз", "Add your products", "Ürünlerinizi yükleyin", "Mahsulotlarni yuklang"),
                    Tr.T("Создайте первый товар или загрузите весь список из Excel, 1С и других программ.", "Биринчи товарды түзүңүз же бүт тизмени Excel, 1С жана башка программалардан жүктөңүз.",
                        "Create the first product or import the whole list from Excel, 1C and other programs.", "İlk ürünü oluşturun veya tüm listeyi Excel, 1C ve diğer programlardan yükleyin.",
                        "Birinchi mahsulotni yarating yoki butun ro'yxatni Excel, 1C va boshqa dasturlardan yuklang.")));
            }
            else
            {
                panel.Children.Add(Head(
                    Tr.T("Добавьте товары", "Товарларды кошуңуз", "Add your products", "Ürünlerinizi ekleyin", "Mahsulotlarni qo'shing"),
                    Tr.T("Создайте первый товар. ", "Биринчи товарды түзүңүз. ", "Create the first product. ", "İlk ürünü oluşturun. ", "Birinchi mahsulotni yarating. ") + ImportAccess.KassaNote + "."));
            }
            buttons.Children.Add(EmptyAction(Tr.T("Создать товар", "Товар түзүү", "Create product", "Ürün oluştur", "Mahsulot yaratish"), primary: true, AddProduct_Click));
            if (ImportAccess.Allowed)
                buttons.Children.Add(EmptyAction(Tr.T("Массовая загрузка", "Массалык жүктөө", "Bulk import", "Toplu yükleme", "Ommaviy yuklash"), primary: false, BulkImport_Click));
        }
        panel.Children.Add(buttons);
        return panel;
    }

    private static Button EmptyAction(string text, bool primary, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 4, 4, 0), Focusable = false };
        button.Classes.Add(primary ? "PrimaryButton" : "SecondaryButton");
        button.Click += click;
        return button;
    }
}
