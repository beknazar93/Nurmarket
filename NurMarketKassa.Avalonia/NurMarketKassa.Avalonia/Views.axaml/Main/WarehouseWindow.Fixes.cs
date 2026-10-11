using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-11, замечания тестировщика (склад.md): приёмка и ревизия — крестик «убрать» в каждой строке, «Очистить список»,
/// подсказки и список поставщиков больше не «зависают» (закрываются по Esc, крестиком, кликом мимо и повторным выбором того же
/// поставщика — раньше событие выбора на уже выбранном не срабатывало, и список оставался поверх окна).</summary>
public partial class WarehouseWindow
{
    private void InitWarehouseFixes()
    {
        ReceivingClearButton.Content = Tr.T("Очистить список", "Тизмени тазалоо", "Clear the list", "Listeyi temizle", "Ro'yxatni tozalash");
        RevisionClearButton.Content = Tr.T("Очистить список", "Тизмени тазалоо", "Clear the list", "Listeyi temizle", "Ro'yxatni tozalash");

        // Esc — закрыть подсказки и список поставщиков.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || (!ReceivingSuggestionsBox.IsVisible && !ReceivingSupplierPanel.IsVisible))
                return;
            CloseReceivingPopups();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // Ушли из поля скана (клик по таблице, другой кнопке) — подсказки закрываются. С задержкой: клик по самой подсказке
        // сначала забирает фокус, и она должна успеть сработать.
        ReceivingScanBox.LostFocus += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            if (!ReceivingScanBox.IsFocused && !ReceivingSuggestionsBox.IsPointerOver)
                ReceivingSuggestionsBox.IsVisible = false;
        }, TimeSpan.FromMilliseconds(300));

        // Нажали уже выбранного поставщика — событие выбора не приходит; закрываем список по отпусканию кнопки мыши на нём.
        // Сменили поставщика — его прошлые товары под строкой поставщика.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.ReceivingSupplier))
                _ = ShowSupplierProductsAsync();
        };

        ReceivingSupplierList.AddHandler(PointerReleasedEvent, (_, _) => DispatcherTimer.RunOnce(() =>
        {
            if (ReceivingSupplierList.SelectedItem is PurchaseReceivingService.Supplier supplier && _viewModel is not null)
                _viewModel.ReceivingSupplier = supplier;
            ReceivingSupplierPanel.IsVisible = false;
        }, TimeSpan.FromMilliseconds(50)), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private CancellationTokenSource? _supplierProductsCts;

    /// <summary>2026-10-11 (склад.md, 2.2.1): товары выбранного поставщика — кнопками; нажатие добавляет товар в приёмку
    /// с количеством из поля рядом и последней ценой закупки у этого поставщика.</summary>
    private async Task ShowSupplierProductsAsync()
    {
        _supplierProductsCts?.Cancel();
        ReceivingSupplierProductsPanel.Children.Clear();
        if (_viewModel?.ReceivingSupplier is not { Id.Length: > 0 } supplier)
        {
            ReceivingSupplierProductsBox.IsVisible = false;
            return;
        }
        var cts = _supplierProductsCts = new CancellationTokenSource();
        ReceivingSupplierProductsBox.IsVisible = true;
        ReceivingSupplierProductsTitle.Text = Tr.T($"Загружаю товары поставщика «{supplier.Name}»…", $"«{supplier.Name}» жеткирүүчүнүн товарларын жүктөп жатам…",
            $"Loading products of “{supplier.Name}”…", $"«{supplier.Name}» tedarikçisinin ürünleri yükleniyor…", $"«{supplier.Name}» mahsulotlari yuklanmoqda…");
        List<PurchaseReceivingService.SupplierProduct> products;
        try
        {
            products = await PurchaseReceivingService.Instance.LoadSupplierProductsAsync(supplier, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ReceivingSupplierProductsTitle.Text = Tr.T("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "Supplier products didn't load: ",
                "Tedarikçi ürünleri yüklenemedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ") + ex.Message;
            return;
        }
        if (cts.IsCancellationRequested)
            return;
        var catalog = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in CatalogCacheService.Products)
            catalog.TryAdd(p.Id, p);
        var shown = products.Where(p => catalog.ContainsKey(p.ProductId)).Take(60).ToList();
        ReceivingSupplierProductsTitle.Text = shown.Count == 0
            ? Tr.T($"От «{supplier.Name}» раньше ничего не приходовали.", $"«{supplier.Name}» жеткирүүчүдөн мурда эч нерсе кириштелген эмес.",
                $"Nothing has been received from “{supplier.Name}” before.", $"«{supplier.Name}» tedarikçisinden daha önce bir şey alınmadı.",
                $"«{supplier.Name}» dan avval hech narsa kirim qilinmagan.")
            : Tr.T($"Товары поставщика «{supplier.Name}» ({shown.Count}) — нажмите, чтобы добавить:", $"«{supplier.Name}» жеткирүүчүнүн товарлары ({shown.Count}) — кошуу үчүн басыңыз:",
                $"Products of “{supplier.Name}” ({shown.Count}) — press to add:", $"«{supplier.Name}» ürünleri ({shown.Count}) — eklemek için basın:",
                $"«{supplier.Name}» mahsulotlari ({shown.Count}) — qo'shish uchun bosing:");
        foreach (var item in shown)
        {
            var tile = catalog[item.ProductId];
            var price = item.LastPurchasePrice > 0 ? $" · {item.LastPurchasePrice:0.##}" : "";
            var button = new Button
            {
                Classes = { "SecondaryButton" }, Padding = new Avalonia.Thickness(10, 4), Margin = new Avalonia.Thickness(0, 0, 6, 6),
                Content = new TextBlock { Text = tile.Title + price, FontSize = 12.5, MaxWidth = 260, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            };
            var at = item.LastAt.ToLocalTime().ToString("dd.MM.yyyy");
            ToolTip.SetTip(button, Tr.T($"Последний приход: {at}, {item.LastQuantity:0.###} по {item.LastPurchasePrice:0.##}",
                $"Акыркы кириш: {at}, {item.LastQuantity:0.###} × {item.LastPurchasePrice:0.##}",
                $"Last receipt: {at}, {item.LastQuantity:0.###} at {item.LastPurchasePrice:0.##}",
                $"Son giriş: {at}, {item.LastQuantity:0.###} × {item.LastPurchasePrice:0.##}",
                $"Oxirgi kirim: {at}, {item.LastQuantity:0.###} × {item.LastPurchasePrice:0.##}"));
            button.Click += (_, _) =>
            {
                if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                    quantity = 1;
                _viewModel.AddReceivingProduct(tile, quantity);
                // Цена закупки — последняя у этого поставщика, если в строке её ещё нет.
                if (item.LastPurchasePrice > 0
                    && _viewModel.ReceivingLines.LastOrDefault(l => string.Equals(l.ProductId, tile.Id, StringComparison.OrdinalIgnoreCase)) is { PurchasePrice: <= 0 } line)
                    line.PurchasePrice = item.LastPurchasePrice;
                ReceivingQuantityBox.Text = "1";
                RefreshReceivingSummary();
            };
            ReceivingSupplierProductsPanel.Children.Add(button);
        }
    }

    private void CloseReceivingPopups()
    {
        ReceivingSuggestionsBox.IsVisible = false;
        ReceivingSuggestionsPanel.ItemsSource = null;
        ReceivingSupplierPanel.IsVisible = false;
    }

    private void ReceivingSuggestionsClose_Click(object? sender, RoutedEventArgs e) => CloseReceivingPopups();

    private void ReceivingRowRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReceivingLineVm line })
            return;
        // Как «Убрать»: сначала отменить начатую правку ячейки — иначе строка вернётся при уходе с вкладки (2026-10-03).
        ReceivingGrid.CancelEdit();
        _viewModel.ReceivingLines.Remove(line);
        RefreshReceivingSummary();
    }

    private void ReceivingClear_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingGrid.CancelEdit();
        _viewModel.ReceivingLines.Clear();
        CloseReceivingPopups();
        RefreshReceivingSummary();
        PosLogger.Log("Приёмка: список очищен кнопкой.", "STOCK");
    }

    private void RevisionRowRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RevisionLineVm line })
            return;
        RevisionGrid.CancelEdit();
        _viewModel.RevisionLines.Remove(line);
    }

    private void RevisionClear_Click(object? sender, RoutedEventArgs e)
    {
        RevisionGrid.CancelEdit();
        _viewModel.RevisionLines.Clear();
        PosLogger.Log("Ревизия: список очищен кнопкой.", "STOCK");
    }
}
