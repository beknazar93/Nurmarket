using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly string _groupKey;

    /// <summary>Другая F-клавиша, нажатая при открытом окне (2026-09-28). Окно перехватывает
    /// клавиатуру, и раньше F2 поверх окна F1 ничего не делала — приходилось закрывать и
    /// нажимать заново. Теперь окно закрывается, а касса сразу открывает новую группу.</summary>
    private string? _nextGroup;

    public HotkeyGroupDialog() : this("F1", []) { }

    public HotkeyGroupDialog(string groupKey, IReadOnlyList<CatalogProductTileVm> products)
    {
        InitializeComponent();
        _groupKey = groupKey;
        // Туннелем: F-клавиши не должны доставаться кнопкам товаров внутри окна.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);

        GroupKeyText.Text = groupKey;
        TitleText.Text = Tr.T("Товары горячей клавиши", "Ыкчам баскычтын товарлары", "Hotkey products",
            "Kısayol ürünleri", "Tezkor tugmadagi mahsulotlar");

        ProductsPanel.ItemsSource = products;

        HintText.Text = products.Count == 0
            ? Tr.T($"К клавише {groupKey} пока не привязан ни один товар. Привязать можно в карточке товара на складе — поле «Горячая клавиша».",
                   $"{groupKey} баскычына азырынча бир да товар байланган эмес. Аны кампадагы товардын карточкасынан — «Ыкчам баскыч» талаасынан байласа болот.",
                   $"No products are assigned to {groupKey} yet. You can assign them in the product card in the warehouse — the “Hotkey” field.",
                   $"{groupKey} tuşuna henüz ürün atanmadı. Depodaki ürün kartından, «Kısayol tuşu» alanından atayabilirsiniz.",
                   $"{groupKey} tugmasiga hali birorta mahsulot bog'lanmagan. Uni ombordagi mahsulot kartasida — «Tezkor tugma» maydonida bog'lash mumkin.")
            : Tr.T("Нажмите на товар — он добавится в чек.", "Товарды басыңыз — ал чекке кошулат.",
                   "Tap a product to add it to the receipt.", "Ürüne dokunun — fişe eklenir.",
                   "Mahsulotni bosing — chekka qo'shiladi.");
    }

    /// <summary>Выбранный товар либо null, если окно закрыли без выбора.</summary>
    public static CatalogProductTileVm? Show(Window? owner, string groupKey, IReadOnlyList<CatalogProductTileVm> products) =>
        Show(owner, groupKey, products, out _);

    /// <summary>То же, плюс <paramref name="nextGroup"/> — F-клавиша, на которую переключились
    /// при открытом окне (null — окно просто закрыли или выбрали товар).</summary>
    public static CatalogProductTileVm? Show(Window? owner, string groupKey, IReadOnlyList<CatalogProductTileVm> products, out string? nextGroup)
    {
        var dialog = new HotkeyGroupDialog(groupKey, products);
        PosDialogHost.Show(dialog, owner);
        nextGroup = dialog._nextGroup;
        return dialog._selected;
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
            return;
        }

        if (e.Key is < Key.F1 or > Key.F12)
            return;

        e.Handled = true;
        var group = "F" + (e.Key - Key.F1 + 1);
        // Та же клавиша ещё раз — просто закрыть окно; другая — переключиться на её товары.
        if (!string.Equals(group, _groupKey, System.StringComparison.OrdinalIgnoreCase))
            _nextGroup = group;
        Close(false);
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
