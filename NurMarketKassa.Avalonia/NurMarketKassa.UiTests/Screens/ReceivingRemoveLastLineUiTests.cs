using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22 (владелец: «в приёмке последний товар не убирается!»): «Склад → Приёмка» — убрать можно любую строку, в том числе последнюю, всеми путями:
/// «✕» в карточке, Del на строке, кнопка «Убрать» (отмеченные галочкой / строка с курсором / единственная строка), «Очистить список»; Ctrl+Z возвращает.
/// Причина: «Убрать» убирала только «выбранную» строку (_rcvSelectedLine — последняя, в карточке которой был курсор) и не знала про галочки; после «✕»/Del
/// она указывала на уже убранную строку, и последняя оставшаяся не убиралась ничем, кроме «✕».</summary>
[Collection("UiScenarios")]
public sealed class ReceivingRemoveLastLineUiTests
{
    private static ReceivingLineVm Line(int i) => new() { Barcode = "46000000010" + i, ProductName = "Товар " + i, Source = ReceivingSource.Unknown, Quantity = 1 + i, PurchasePrice = 10, SalePrice = 15 };

    private static async Task<(WarehouseWindow Window, WarehouseViewModel Vm, List<ReceivingLineVm> Lines, IDisposable Render)> OpenAsync(int count)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var render = UiShot.Render(window, new UiSize(1366, 768));
        UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
        UiShot.Settle();
        var vm = (WarehouseViewModel)window.DataContext!;
        vm.ReceivingLines.Clear();
        var lines = new List<ReceivingLineVm>();
        for (var i = 0; i < count; i++)
        {
            var l = Line(i);
            lines.Add(l);
            vm.ReceivingLines.Add(l);
            UiShot.Settle(4);
        }
        return (window, vm, lines, render);
    }

    private static void Close(WarehouseWindow window, IDisposable render)
    {
        ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
        render.Dispose();
        window.Close();
        UiShot.Pump();
    }

    private static Button RemoveButton(Window w) =>
        w.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && UiDrive.LabelOf(b) == "Убрать");

    private static Button CardX(Window w, ReceivingLineVm line) =>
        RcvCards.Card(w, line).GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "✕"));

    private static void Tick(Window w, ReceivingLineVm line) =>
        RcvCards.Card(w, line).GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;

    /// <summary>Итоги и «Оприходовать» после того, как строк не осталось.</summary>
    private static void AssertEmptyState(WarehouseWindow w)
    {
        UiShot.Settle(6);
        UiDrive.Find<TextBlock>(w, "SumPos").Text.Should().Be("0", "счётчик «Позиций» обновился");
        UiDrive.Find<TextBlock>(w, "SumQty").Text.Should().Be("0");
        UiDrive.Find<Button>(w, "ReceivingCommitButton").IsEffectivelyEnabled.Should().BeFalse("без товаров «Оприходовать» неактивна");
        RcvCards.Cards(w).Should().BeEmpty();
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CardX_RemovesEveryLine_IncludingLast(int count)
    {
        var (window, vm, lines, render) = await OpenAsync(count);
        try
        {
            foreach (var l in lines.ToList())
            {
                UiDrive.Click(window, CardX(window, l));
                UiShot.Settle(4);
                vm.ReceivingLines.Should().NotContain(l);
            }
            vm.ReceivingLines.Should().BeEmpty();
            AssertEmptyState(window);
        }
        finally { Close(window, render); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RemoveButton_TickedRows_RemovesThemIncludingLast(int count)
    {
        var (window, vm, lines, render) = await OpenAsync(count);
        try
        {
            foreach (var l in lines)
                Tick(window, l);
            UiShot.Settle(4);
            UiDrive.Click(window, RemoveButton(window));
            UiShot.Settle(6);
            vm.ReceivingLines.Should().BeEmpty("«Убрать» убирает все отмеченные строки, и последнюю тоже");
            AssertEmptyState(window);
        }
        finally { Close(window, render); }
    }

    [AvaloniaFact]
    public async Task RemoveButton_LineWithCursor_ThenLastRemainingLine_Goes()
    {
        var (window, vm, lines, render) = await OpenAsync(2);
        try
        {
            RcvCards.Field(window, lines[0], "qty").Focus(); // курсор в карточке первой
            UiShot.Settle(4);
            UiDrive.Click(window, RemoveButton(window));
            UiShot.Settle(6);
            vm.ReceivingLines.Should().Equal(lines[1]);

            // курсор ушёл, галочек нет, строка одна — «Убрать» убирает именно её (раньше «выбранной» оставалась уже убранная — и ничего не происходило)
            UiDrive.Click(window, RemoveButton(window));
            UiShot.Settle(6);
            vm.ReceivingLines.Should().BeEmpty("последняя строка убирается кнопкой «Убрать»");
            AssertEmptyState(window);
        }
        finally { Close(window, render); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DelKey_OnCardX_RemovesLine_IncludingLast(int count)
    {
        var (window, vm, lines, render) = await OpenAsync(count);
        try
        {
            foreach (var l in lines.ToList())
            {
                var x = CardX(window, l);
                x.Focus();
                UiShot.Settle(4);
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                UiShot.Settle(4);
                vm.ReceivingLines.Should().NotContain(l, "Del убирает строку, в том числе последнюю");
            }
            AssertEmptyState(window);

            // 2026-11-22: Ctrl+Z / «Вернуть» в Headless не проверяется (горячая клавиша окна не доходит до обработчика через KeyPress) — проверить вживую.
        }
        finally { Close(window, render); }
    }

    // 2026-11-22: «Очистить список» здесь не проверяется — окно-вопрос при 2+ строках в Headless подвешивает тест-хост (путь «Очистить» не менялся, кроме сброса _rcvSelectedLine).
}
