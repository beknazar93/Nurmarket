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

    // 2026-10-24, ТЗ «Склад» (Б1): «Товары поставщика» — панель с поиском вместо 60 кнопок, код перенесён в WarehouseWindow.ReceivingScan.cs (ShowSupplierProductsAsync).

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

    private async void ReceivingClear_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-20, аудит программы владельца: длинная накладная стиралась одним нажатием — при 2+ строках спрашиваем.
        var count = _viewModel.ReceivingLines.Count;
        if (count > 1)
        {
            var ok = await NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog.ShowModalAsync(this,
                Tr.T("Очистить список", "Тизмени тазалоо", "Clear the list", "Listeyi temizle", "Ro'yxatni tozalash"),
                Tr.T($"Убрать из приёмки все строки ({count})? Оприходованное ранее не изменится.",
                    $"Кабыл алуудан бардык саптарды ({count}) алып салабы? Мурда кирешеге алынгандар өзгөрбөйт.",
                    $"Remove all lines ({count}) from the receiving? Stock received earlier won't change.",
                    $"Kabulden tüm satırlar ({count}) kaldırılsın mı? Daha önce alınanlar değişmez.",
                    $"Qabuldan barcha qatorlar ({count}) olib tashlansinmi? Avval kirim qilinganlar o'zgarmaydi."),
                Tr.T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmAccent.Danger).ConfigureAwait(true);
            if (!ok)
                return;
        }
        ReceivingGrid.CancelEdit();
        _viewModel.ReceivingLines.Clear();
        CloseReceivingPopups();
        RefreshReceivingSummary();
        PosLogger.Log("Приёмка: список очищен кнопкой.", "STOCK");
    }
}
