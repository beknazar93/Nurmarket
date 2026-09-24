using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Товары одной горячей клавиши — отдельным окном поверх кассы.
///
/// Раньше нажатие F1…F12 просто фильтровало каталог слева: список под рукой менялся, и чтобы
/// вернуться ко всем товарам, фильтр надо было снять тем же нажатием. Продавец при этом терял
/// из виду остальной каталог. Теперь группа открывается окном: выбрал товар — он ушёл в чек,
/// окно закрылось, каталог остался как был.</summary>
public partial class HotkeyGroupDialog : Window
{
    private CatalogProductTileVm? _selected;

    public HotkeyGroupDialog() : this("F1", []) { }

    public HotkeyGroupDialog(string groupKey, IReadOnlyList<CatalogProductTileVm> products)
    {
        InitializeComponent();

        GroupKeyText.Text = groupKey;
        TitleText.Text = Tr.T("Товары горячей клавиши", "Ыкчам баскычтын товарлары", "Hotkey products",
            "Kısayol ürünleri", "Tezkor tugma mahsulotlari");

        ProductsPanel.ItemsSource = products;

        HintText.Text = products.Count == 0
            ? Tr.T($"К клавише {groupKey} пока не привязан ни один товар. Привязать можно в карточке товара на складе — поле «Горячая клавиша».",
                   $"{groupKey} баскычына азырынча бир да товар байланган эмес. Кампадагы товардын карточкасынан байлоого болот.",
                   $"No products are assigned to {groupKey} yet. Assign them in the product card in the warehouse.",
                   $"{groupKey} tuşuna henüz ürün atanmadı. Depodaki ürün kartından atayabilirsiniz.",
                   $"{groupKey} tugmasiga hali mahsulot bog'lanmagan. Ombordagi mahsulot kartasidan bog'lash mumkin.")
            : Tr.T("Нажмите на товар — он добавится в чек.", "Товарды басыңыз — ал чекке кошулат.",
                   "Tap a product to add it to the receipt.", "Ürüne dokunun — fişe eklenir.",
                   "Mahsulotni bosing — chekka qo'shiladi.");
    }

    /// <summary>Выбранный товар либо null, если окно закрыли без выбора.</summary>
    public static CatalogProductTileVm? Show(Window? owner, string groupKey, IReadOnlyList<CatalogProductTileVm> products)
    {
        var dialog = new HotkeyGroupDialog(groupKey, products);
        PosDialogHost.Show(dialog, owner);
        return dialog._selected;
    }

    private void Product_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogProductTileVm product })
        {
            _selected = product;
            Close(true);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
}
