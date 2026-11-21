using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (Д-6, живая проверка «Склада» на NBS): после проведения прихода журнал владельца — WARN «таблица показывала лишние строки (4 при 1 в списке)».
/// Причина (воспроизведено здесь до исправления): строку, ячейка которой в правке, DataGrid при удалении из списка не перерабатывает — она остаётся спрятанной
/// и снова видна после смены вкладки (1 строка при 0 в списке). Проведение (F9 из ячейки) убирает ровно такие строки. Теперь таблица пересобирается сразу.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingGhostRowUiTests
{
    /// <summary>2026-11-21: строки приёмки — карточки (внешняя рамка с BulkScanRow в DataContext).</summary>
    private static int Shown(Window window) => window.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is NurMarketKassa.Services.BulkScanRow && b.GetVisualParent()?.DataContext is not NurMarketKassa.Services.BulkScanRow && b.IsEffectivelyVisible && b.Bounds.Height > 0);

    [AvaloniaFact]
    public async Task EditingRow_RemovedLikePosting_ThenTabSwitch_NoGhostRows()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var tabs = UiDrive.Find<TabControl>(window, "WarehouseTabs");
            var receivingTab = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            tabs.SelectedItem = receivingTab;
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var lines = new List<ReceivingLineVm>();
            for (var i = 0; i < 2; i++) // 2026-11-21: карточки высокие — в списке видно две (список виртуализирован)
            {
                var l = new ReceivingLineVm { Barcode = "460000000001" + i, ProductName = "Товар " + i, Source = ReceivingSource.Unknown, Quantity = 1 + i, PurchasePrice = 10, SalePrice = 15 };
                lines.Add(l);
                vm.ReceivingLines.Insert(0, l);
                UiShot.Settle(4);
            }
            Shown(window).Should().Be(2);

            // ячейка «Принимаем» последней добавленной строки в правке (как перед F9 «Провести»)
            var qtyBox = window.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "qty") && t.DataContext is NurMarketKassa.Services.BulkScanRow r && ReferenceEquals(r.Line, lines[1]));
            qtyBox.Focus();
            UiShot.Settle(6);

            // проведение убирает отправленные строки по одной, в порядке добавления (WarehouseViewModel.CommitReceivingCoreAsync)
            foreach (var l in lines)
                vm.ReceivingLines.Remove(l);
            UiShot.Settle(10);
            Shown(window).Should().Be(0, "после проведения список пуст");

            tabs.SelectedIndex = 0; // «Товары» — как в живой проверке (проверка остатка)
            UiShot.Settle(10);
            tabs.SelectedItem = receivingTab;
            UiShot.Settle(10);
            Shown(window).Should().Be(0, "Д-6: после смены вкладки строка, бывшая в правке, не возвращается призраком");

            vm.ReceivingLines.Insert(0, new ReceivingLineVm { Barcode = "4600000000099", ProductName = "Новый", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 10, SalePrice = 15 });
            UiShot.Settle(10);
            Shown(window).Should().Be(1, "следующий скан — ровно одна строка");
        }
        finally
        {
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }
}
