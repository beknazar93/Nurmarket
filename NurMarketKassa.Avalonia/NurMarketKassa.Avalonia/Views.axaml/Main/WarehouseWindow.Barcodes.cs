using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11, запрос владельца «массовая печать ценников/штрихкодов — выведи в Склад сверху! Как в вебе!»: в вебе («Товары и склад») вверху стоит вкладка «Штрихкоды» —
/// массовая печать этикеток. Здесь — видимая вкладка «Штрихкоды» сразу после «Приёмки» (код вкладки — как у «Массового добавления»): внутри печать штрихкодов и ценников
/// (<see cref="BarcodePrintTabHost"/> → <see cref="BarcodePrintView"/>, то же содержимое, что в окне «Печать штрихкодов и ценников»). Платность и права прежние: вкладка видна всегда,
/// а если печать не куплена или нет права — внутри подсказка с «Ввести серийный номер». На вкладке «Товары» у каждой строки есть галочка «В печать»; кнопка «Печать ценников (N)»
/// открывает вкладку «Штрихкоды» с этими товарами (копии — по умолчанию из настроек печати).</summary>
public partial class WarehouseWindow
{
    private TabItem? _barcodesTab;
    private BarcodePrintTabHost? _barcodesHost;
    private Border? _barcodesBody;
    private readonly HashSet<string> _printPicked = new(StringComparer.Ordinal);
    private bool _printPickTextChanged;

    private void InitBarcodesTab()
    {
        _barcodesHost = new BarcodePrintTabHost();
        _barcodesHost.Unlocked += () => RefreshPaidFeatureVisibility();
        _barcodesBody = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, // 2026-11-11: ширину задаёт вкладка, а не содержимое
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(0, 14, 14, 14),
            Padding = new Thickness(16),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = _barcodesHost,
        };
        _barcodesBody.Bind(Border.BackgroundProperty, _barcodesBody.GetResourceObservable("BrushPanel"));

        var icon = MarketplaceIcons.Icon("barcode", 16);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { icon, new TextBlock { Text = BarcodesTabText, VerticalAlignment = VerticalAlignment.Center } },
        };
        _barcodesTab = new TabItem { Header = header, Content = _barcodesBody };
        AutomationProperties.SetName(_barcodesTab, BarcodesTabText);
        // «Товары · Приёмка · Штрихкоды · …» — сразу после приёмки (как «Штрихкоды» рядом с «Товарами» в вебе); «Массовое добавление» встаёт после неё.
        var index = WarehouseTabs.Items.IndexOf(ReceivingTabItem);
        WarehouseTabs.Items.Insert(index < 0 ? WarehouseTabs.Items.Count : index + 1, _barcodesTab);

        // Низкое окно (1366×768, 1024×600): поля вкладки меньше — карточкам товаров остаётся место (как у вкладки «Товары»).
        SizeChanged += (_, _) => ApplyBarcodesPadding();
        ApplyBarcodesPadding();
        Closed += (_, _) => _barcodesHost?.Shutdown();
    }

    private static string BarcodesTabText => Tr.T("Штрихкоды", "Штрих-коддор", "Barcodes", "Barkodlar", "Shtrix-kodlar");

    private void ApplyBarcodesPadding()
    {
        if (_barcodesBody is null)
            return;
        var compact = ClientSize.Height > 0 && ClientSize.Height < 800;
        _barcodesBody.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16);
    }

    /// <summary>Открыть вкладку «Штрихкоды». <paramref name="productIds"/> — отмеченные товары (отмечаются и показываются первыми); null или пусто — вкладка как есть.
    /// false — нет права «Печать штрих-кодов» или печать не куплена (серийный номер не введён); тогда вкладка не меняется.</summary>
    internal bool OpenBarcodesTab(IReadOnlyList<string>? productIds)
    {
        if (_barcodesTab is null || _barcodesHost is null)
            return false;
        if (productIds is { Count: > 0 })
        {
            // Товары, которых уже нет в каталоге (удалены/в архиве после отметки), не передаём — иначе печать напишет «нет в каталоге кассы».
            var present = new HashSet<string>(CatalogCacheService.Products.Select(p => p.Id), StringComparer.Ordinal);
            var ids = productIds.Where(present.Contains).ToList();
            if (ids.Count > 0 && !_barcodesHost.ApplySelection(ids, this))
                return false;
        }
        WarehouseTabs.SelectedItem = _barcodesTab;
        return ReferenceEquals(WarehouseTabs.SelectedItem, _barcodesTab);
    }

    /// <summary>Товары, отмеченные галочкой «В печать» на вкладке «Товары».</summary>
    public IReadOnlyCollection<string> PrintPickedIds => _printPicked;

    // ------------------------------------------------------------------ галочка «В печать» в строке товара

    private void PrintPick_DataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not CheckBox box)
            return;
        var product = box.DataContext as CatalogProductTileVm;
        box.IsVisible = product is not null;
        box.IsChecked = product is not null && _printPicked.Contains(product.Id);
        var text = Tr.T("В печать ценников и штрихкодов", "Баа белгилерин жана штрих-коддорду басууга", "Add to price tag and barcode printing", "Fiyat etiketi ve barkod yazdırmaya ekle", "Narx yorliqlari va shtrix-kodlarni chop etishga qo'shish");
        AutomationProperties.SetName(box, text);
        ToolTip.SetTip(box, text);
    }

    private void PrintPick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: CatalogProductTileVm product } box)
            return;
        if (box.IsChecked == true)
            _printPicked.Add(product.Id);
        else
            _printPicked.Remove(product.Id);
        UpdatePrintPickButton();
    }

    /// <summary>«Печать ценников (N)» — кнопка на вкладке «Товары»: с отмеченными товарами видна всегда (нажатие спросит серийный номер, если печать не куплена), без них — только когда печать куплена.</summary>
    private void UpdatePrintPickButton()
    {
        var n = _printPicked.Count;
        BulkPriceTagButton.IsVisible = n > 0 || UserPreferences.Instance.BulkPriceTagUnlocked;
        if (n > 0)
        {
            BulkPriceTagButton.Content = Tr.T($"Печать ценников ({n})", $"Баа белгилерин басуу ({n})", $"Print price tags ({n})", $"Fiyat etiketi yazdır ({n})", $"Narx yorliqlarini chop etish ({n})");
            _printPickTextChanged = true;
        }
        else if (_printPickTextChanged)
        {
            BulkPriceTagButton.Content = Tr.T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags", "Barkod ve fiyat etiketi yazdır", "Shtrix-kod va narx yorliqlarini chop etish");
            _printPickTextChanged = false;
        }
    }
}
