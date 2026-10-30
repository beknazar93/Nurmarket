using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-18, решение владельца (аудит программы владельца, правило 5): в строке товара было 6 значков без
/// подписей (±, ✎, этикетка, ценник, фото, корзина), «Удалить» — рядом с частыми действиями. Теперь видны два главных
/// действия с подписью — «± Остаток» (приход/списание/точное количество, StockAdjust_Click) и «✎ Изменить»
/// (EditProduct_Click), остальное — в «⋯»: «Печать этикетки», «Ценник», «Загрузить фото», под чертой красное «Удалить».
/// Обработчики те же (товар — из Tag кнопки, как раньше; «Удалить» по-прежнему спрашивает PosConfirm с красной
/// кнопкой и код сотрудника). Таблица уже ~1040 px — подписи прячутся, остаются значки с подсказкой.</summary>
public partial class WarehouseWindow
{
    // Подписи и имена для экранного диктора (5 языков); x:Static в WarehouseWindow.axaml — окно создаётся уже на
    // выбранном языке.
    public static string RowStockText => Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq");
    public static string RowStockName => Tr.T("Изменить остаток", "Калдыкты өзгөртүү", "Change stock", "Stoğu değiştir", "Qoldiqni o'zgartirish");
    public static string RowEditName => Tr.T("Изменить товар", "Товарды түзөтүү", "Edit product", "Ürünü düzenle", "Mahsulotni tahrirlash");
    public static string RowMoreText => Tr.T("Ещё действия", "Дагы аракеттер", "More actions", "Diğer işlemler", "Boshqa amallar");

    /// <summary>2026-10-19, этап О9: «нет в наличии» — остаток 0 и меньше (красная плашка). Флаг IsLowStock у товара с нулём
    /// мог остаться ложным (товар создан сразу с остатком 0), и «0 шт» было зелёным.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter IsOutOfStock =
        new Avalonia.Data.Converters.FuncValueConverter<double, bool>(quantity => quantity <= 0);

    // Ширина колонки «Действия»: с подписями и только значками (3 × 36 + промежутки + отступ 12 от полосы прокрутки
    // + поля ячейки 12 + 12).
    private const double RowActionsWideWidth = 300;
    // 2026-10-29: «⋯» 48 dp и отступ справа 20 — 156 → 172.
    private const double RowActionsCompactWidth = 172;
    // Наименьшая ширина названия (MinWidth в WarehouseWindow.axaml) и в узкой таблице: в разделе владельца на экране
    // 1024 (таблица ~868) иначе появлялась прокрутка вбок, и полоса прокрутки ложилась на «⋯».
    private const double NameMinWidth = 200;
    private const double NameMinWidthCompact = 170;

    private const string IconLabel = "M5.5,7A1.5,1.5 0 0,1 4,5.5A1.5,1.5 0 0,1 5.5,4A1.5,1.5 0 0,1 7,5.5A1.5,1.5 0 0,1 5.5,7M21.41,11.58L12.41,2.58C12.05,2.22 11.55,2 11,2H4C2.89,2 2,2.89 2,4V11C2,11.55 2.22,12.05 2.59,12.41L11.58,21.41C11.95,21.77 12.45,22 13,22C13.55,22 14.05,21.77 14.41,21.41L21.41,14.41C21.78,14.05 22,13.55 22,13C22,12.44 21.77,11.94 21.41,11.58Z";
    private const string IconPriceTag = "M7,15H9C9,16.08 10.37,17 12,17C13.63,17 15,16.08 15,15C15,13.9 13.96,13.5 11.76,12.97C9.64,12.44 7,11.78 7,9C7,7.21 8.47,5.69 10.5,5.18V3H13.5V5.18C15.53,5.69 17,7.21 17,9H15C15,7.92 13.63,7 12,7C10.37,7 9,7.92 9,9C9,10.1 10.04,10.5 12.24,11.03C14.36,11.56 17,12.22 17,15C17,16.79 15.53,18.31 13.5,18.82V21H10.5V18.82C8.47,18.31 7,16.79 7,15Z";
    private const string IconPhoto = "M4,4H7L9,2H15L17,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9Z";
    private const string IconReceipts = "M19,3H5C3.9,3 3,3.9 3,5V19C3,20.1 3.9,21 5,21H19C20.1,21 21,20.1 21,19V5C21,3.9 20.1,3 19,3M19,19H5V5H19V19M7,7H17V9H7V7M7,11H17V13H7V11M7,15H13V17H7V15Z";
    private const string IconDelete = "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z";

    /// <summary>Узкая таблица — только значки (подписи прячет стиль DataGrid.row-compact), колонка уже.</summary>
    private void ProductsGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var columns = ProductsGrid.Columns;
        if (columns.Count < 2 || e.NewSize.Width <= 0)
            return;
        double others = 0;
        for (var i = 1; i < columns.Count - 1; i++)
            others += columns[i].Width.IsAbsolute ? columns[i].Width.Value : columns[i].ActualWidth;
        // 32 — рамка таблицы, полоса прокрутки и поля строк.
        var compact = e.NewSize.Width < NameMinWidth + others + RowActionsWideWidth + 32;
        ProductsGrid.Classes.Set("row-compact", compact);
        columns[0].MinWidth = compact ? NameMinWidthCompact : NameMinWidth;
        // 2026-10-19, этап О9 (аудит: на 1024 «Кока-Кола 1,…»): в узкой таблице колонка «Код / штрихкод» прячется — место
        // названию. Поиск по коду и штрихкоду работает как раньше; ширина колонки по-прежнему входит в расчёт «узкой».
        columns[1].IsVisible = !compact;
        var width = compact ? RowActionsCompactWidth : RowActionsWideWidth;
        if (!columns[^1].Width.IsAbsolute || columns[^1].Width.Value != width)
            columns[^1].Width = new DataGridLength(width);
    }

    /// <summary>«⋯» в строке или плитке товара: остальные действия меню с подписями и значками, строки 44 px.</summary>
    private void RowMore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product } anchor)
            return;

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.FlyoutPresenterClasses.Add("row-menu");
        // 2026-10-30, просьба владельца: «Поступления и партии» — от кого, когда и по какой накладной пришёл товар (вкладка карточки товара).
        menu.Items.Add(RowMenuItem(Tr.T("Поступления и партии", "Кирүүлөр жана партиялар", "Receipts and batches", "Girişler ve partiler", "Kirimlar va partiyalar"),
            IconReceipts, ProductReceipts_Click, product));
        menu.Items.Add(RowMenuItem(Tr.T("Печать этикетки", "Этикетка басып чыгаруу", "Print label", "Etiket yazdır", "Yorliq chop etish"),
            IconLabel, PrintLabel_Click, product));
        menu.Items.Add(RowMenuItem(Tr.T("Ценник", "Баа белгиси", "Price tag", "Fiyat etiketi", "Narx yorlig'i"),
            IconPriceTag, PrintPriceTag_Click, product));
        menu.Items.Add(RowMenuItem(Tr.T("Загрузить фото", "Сүрөт жүктөө", "Upload photo", "Fotoğraf yükle", "Rasm yuklash"),
            IconPhoto, UploadPhoto_Click, product));
        // «Удалить» — последним, под чертой и красным: не рядом с частыми действиями (подтверждение — в самом обработчике).
        menu.Items.Add(new Separator());
        menu.Items.Add(RowMenuItem(Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            IconDelete, DeleteProduct_Click, product, danger: true));
        menu.ShowAt(anchor);
    }

    private MenuItem RowMenuItem(string text, string icon, Action<object?, RoutedEventArgs> handler, CatalogProductTileVm product, bool danger = false)
    {
        // Красный, читаемый и на мягком красном фоне наведения (BrushDangerOnSoft, AccentThemeService).
        var brush = danger ? "BrushDangerOnSoft" : "BrushText";
        var header = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        header.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        var pathIcon = new PathIcon { Data = Geometry.Parse(icon), Width = 16, Height = 16 };
        pathIcon.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable(brush));
        var item = new MenuItem { Header = header, Icon = pathIcon };
        if (danger)
            item.Classes.Add("danger");
        AutomationProperties.SetName(item, text);
        // Тот же обработчик, что у прежнего значка строки: товар он берёт из Tag кнопки (как WarehouseWindow.Redesign.cs).
        item.Click += (_, args) => handler(new Button { Tag = product }, args);
        return item;
    }
}
