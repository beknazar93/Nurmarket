using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11, запрос владельца «массовая печать ценников/штрихкодов — выведи в Склад сверху! Как в вебе!»: в «Складе» видимая вкладка
/// сразу после «Приёмки». 2026-11-21 (владелец: «собери в складе все редакторы в одну вкладку — слишком во многих местах»): это ЕДИНСТВЕННОЕ
/// место для печати ценников и штрихкодов, их генерации, редактора этикетки и шаблонов — вкладка «Ценники и штрихкоды» (<see cref="LabelsHubView"/>:
/// режимы «Печать», «Редактор», «Шаблоны»). Кнопок печати/редактора в верхней панели склада, в меню «⋯» товара, в приёмке, в карточке товара и в меню
/// программы больше нет. Платность и право — внутри вкладки (подсказка с «Ввести серийный номер»). Ключи для Нур Советника и поиска:
/// "barcodes" (прежний), "labels" и "pricetags" — та же вкладка (<see cref="SelectNavTab"/>).</summary>
public partial class WarehouseWindow
{
    private TabItem? _barcodesTab;
    private LabelsHubView? _barcodesHub;
    private Border? _barcodesBody;

    private void InitBarcodesTab()
    {
        _barcodesHub = new LabelsHubView();
        _barcodesHub.Unlocked += () => RefreshPaidFeatureVisibility();
        _barcodesBody = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, // 2026-11-11: ширину задаёт вкладка, а не содержимое
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(0, 14, 14, 14),
            Padding = new Thickness(16),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = _barcodesHub,
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
        // «Товары · Приёмка · Ценники и штрихкоды · …» — сразу после приёмки (как «Штрихкоды» рядом с «Товарами» в вебе); «Массовое добавление» — после неё.
        var index = WarehouseTabs.Items.IndexOf(ReceivingTabItem);
        WarehouseTabs.Items.Insert(index < 0 ? WarehouseTabs.Items.Count : index + 1, _barcodesTab);
        WarehouseTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, WarehouseTabs) && ReferenceEquals(WarehouseTabs.SelectedItem, _barcodesTab))
                _barcodesHub?.Refresh(); // каждый показ вкладки — свежее состояние (куплено/нет, каталог)
        };

        // Низкое окно (1366×768, 1024×600): поля вкладки меньше — карточкам товаров остаётся место (как у вкладки «Товары»).
        SizeChanged += (_, _) => ApplyBarcodesPadding();
        ApplyBarcodesPadding();
        Closed += (_, _) => _barcodesHub?.Shutdown();
        InitLabelsRequests(); // входы из Маркетплейса и прихода (WarehouseWindow.LabelsEntry.cs)
    }

    /// <summary>Название единственной вкладки печати и редакторов.</summary>
    public static string BarcodesTabText => Tr.T("Штрихкоды", "Штрих-коддор", "Barcodes", "Barkodlar", "Shtrix-kodlar") /* 2026-11-21 (владелец): одна вкладка «Штрихкоды» — штрихкоды, ценники и редактор этикетки (режимы Печать / Редактор / Шаблоны) */;

    private void ApplyBarcodesPadding()
    {
        if (_barcodesBody is null)
            return;
        var compact = ClientSize.Height > 0 && ClientSize.Height < 800;
        _barcodesBody.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16);
    }

    /// <summary>Единственная вкладка «Ценники и штрихкоды» (для проверок и переходов).</summary>
    public TabItem? LabelsTab => _barcodesTab;

    /// <summary>Содержимое вкладки (режимы «Печать», «Редактор», «Шаблоны»).</summary>
    public LabelsHubView? LabelsHub => _barcodesHub;

    /// <summary>Открыть вкладку «Ценники и штрихкоды». <paramref name="productIds"/> — товары (отмечаются и показываются первыми, копии по умолчанию).
    /// false — нет права «Печать штрих-кодов» или печать не куплена (серийный номер не введён); тогда вкладка не меняется.</summary>
    public bool OpenBarcodesTab(IReadOnlyList<string>? productIds)
    {
        if (_barcodesTab is null || _barcodesHub is null)
            return false;
        if (productIds is { Count: > 0 })
        {
            // Товары, которых уже нет в каталоге (удалены/в архиве после отметки), не передаём — иначе печать напишет «товар не найден».
            var present = new HashSet<string>(CatalogCacheService.Products.Select(p => p.Id), StringComparer.Ordinal);
            var ids = productIds.Where(present.Contains).ToList();
            if (ids.Count > 0 && !_barcodesHub.ApplySelection(ids, this))
                return false;
        }
        WarehouseTabs.SelectedItem = _barcodesTab;
        return ReferenceEquals(WarehouseTabs.SelectedItem, _barcodesTab);
    }

    /// <summary>Вкладка «Ценники и штрихкоды» с товарами и копиями (после прихода: копии = принятое количество). Позиции без права/покупки — false.</summary>
    public bool OpenLabelsTabWith(IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items)
    {
        if (_barcodesTab is null || _barcodesHub is null)
            return false;
        if (items.Count > 0 && !_barcodesHub.ApplyItems(items, this))
            return false;
        WarehouseTabs.SelectedItem = _barcodesTab;
        return ReferenceEquals(WarehouseTabs.SelectedItem, _barcodesTab);
    }
}
