using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (живой показ 1.17.85, Д-3, и снимки владельца): «Склад → Приёмка» — содержимое склада уезжало вниз (вкладки на 270–390 px, «Обновить»
/// посередине справа, таблица сжата, итоги сразу под таблицей), после перехода между окнами становилось правильным. Проверяем ПОСЛЕ КАЖДОГО шага (открытие, вкладки
/// Товары → Приёмка → Ревизия → Приёмка, смена размера окна и возврат): вкладки не дальше 100 px от верха окна раздела, «Оприходовать» у нижнего края,
/// таблица приёмки занимает остаток. Два режима: раздел программы владельца (OwnerShellWindow.OpenSection — вся обвязка оболочки) и отдельное окно склада кассы.
/// Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\warehouse-layout-*.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseOwnerLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1440, 750)]
    [InlineData(1900, 1060)]
    [InlineData(1366, 768)]
    public async Task OwnerWarehouse_TabsAtTop_TotalsAtBottom_AfterEveryStep(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var shell = new OwnerShellWindow { Width = w, Height = h };
        shell.Show();
        UiShot.Settle(3, 30);
        WarehouseWindow? wh = null;
        try
        {
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(8, 30);
            RunSteps(wh!, $"владелец {w}x{h}", resize: dh => { shell.Height = h + dh; });
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    /// <summary>Тот же раздел при «Масштабе интерфейса» не 100 % (у раздела обёртка LayoutTransformControl) и при смене масштаба уже после открытия.</summary>
    [AvaloniaTheory]
    [InlineData(1440, 750, 0.8)]
    [InlineData(1440, 750, 1.25)]
    [InlineData(1900, 1060, 0.9)]
    [InlineData(1440, 750, -0.9)] // минус — масштаб 90 % задан ДО открытия раздела (как у владельца: «preference 90% -> applied 90%» с запуска)
    [InlineData(1900, 1060, -0.9)]
    [InlineData(1100, 750, -0.9)]  // узкий раздел: желаемая ширина содержимого больше места — раньше это сжимало и высоту (центр по высоте)
    [InlineData(1280, 720, -0.9)]
    public async Task OwnerWarehouse_WithUiScale_TabsAtTop_AfterEveryStep(int w, int h, double scale)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var shell = new OwnerShellWindow { Width = w, Height = h };
        shell.Show();
        UiShot.Settle(3, 30);
        WarehouseWindow? wh = null;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var before = scale < 0;
        scale = Math.Abs(scale);
        if (before)
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, scale);
        try
        {
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(6, 30);
            // масштаб меняется уже после открытия раздела — как OnShellScaleApplied
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, scale);
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", flags)!.Invoke(shell, [wh!]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", flags)!.Invoke(shell, null);
            UiShot.Settle(6, 30);
            RunSteps(wh!, $"владелец {w}x{h} масштаб {scale}", resize: dh =>
            {
                shell.Height = h + dh;
                UiShot.Settle(2, 20);
                typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", flags)!.Invoke(shell, null);
            });
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    /// <summary>Как у владельца 11.11 (журнал 20:50): масштаб 90 %, раздел «Склад» открывается сразу на «Приёмке» (вкладка запомнена), и тут же — вопрос
    /// «Найдена неоконченная приёмка» (модалка над разделом); после «Продолжить» в таблице строка черновика.</summary>
    [AvaloniaTheory]
    [InlineData(1440, 750)]
    [InlineData(1900, 1060)]
    public async Task OwnerWarehouse_OpensOnReceiving_WithDraftQuestion_AtScale90_TabsAtTop(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var dir = Path.Combine(Path.GetTempPath(), "nm-draft-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ReceivingDraftStore.DataDirOverride = dir;
        ReceivingDraftStore.Disabled = false;
        ReceivingDraftStore.ResetForTests();
        ReceivingDraftUi.ShowModal = (dialog, owner) => dialog.ShowDialog<bool?>(owner!);
        var line = new ReceivingLineVm { Barcode = "4600000000011", ProductName = "Тряпка для пола", Source = ReceivingSource.Warehouse, ProductId = "t1", Quantity = 1, PurchasePrice = 80, SalePrice = 120 };
        ReceivingDraftStore.WriteForTests(new ReceivingDraftFile
        {
            Version = 1, Slot = ReceivingDraftSlots.Warehouse, StartedAt = DateTime.Now.AddHours(-1), SavedAt = DateTime.Now.AddMinutes(-5),
            Lines = new List<ReceivingDraftLine> { line.ToDraft() },
        }).Should().BeTrue();
        var shell = new OwnerShellWindow { Width = w, Height = h };
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        WarehouseWindow? wh = null;
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, 0.9);
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() =>
            {
                wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
                wh.Opened += (_, _) => UiDrive.Find<TabControl>(wh, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(wh, "ReceivingTabItem");
                return wh;
            })]);
            PosConfirmDialog? q = null;
            for (var i = 0; i < 100 && q is null; i++)
            {
                UiShot.Settle(1, 30);
                q = wh!.OwnedWindows.OfType<PosConfirmDialog>().FirstOrDefault(d => d.IsVisible);
            }
            q.Should().NotBeNull("вопрос про черновик показан");
            UiShot.Settle(4, 30);
            UiDrive.Click(q!, UiDrive.ButtonByText(q!, "Продолжить"));
            var vm = (WarehouseViewModel)wh!.DataContext!;
            for (var i = 0; i < 100 && vm.ReceivingLines.Count == 0; i++)
                UiShot.Settle(1, 30);
            vm.ReceivingLines.Should().ContainSingle();
            UiShot.Settle(6, 30);
            var tabs = UiDrive.Find<TabControl>(wh, "WarehouseTabs");
            Shot(wh, $"владелец {w}x{h} черновик", "receiving-draft");
            CheckReceiving(wh, tabs, $"{w}x{h}: после вопроса о черновике");
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
            ReceivingDraftUi.ShowModal = (dialog, owner) => PosDialogHost.ShowModalAsync(dialog, owner);
            ReceivingDraftStore.Disabled = true;
            ReceivingDraftStore.DataDirOverride = null;
            ReceivingDraftStore.ResetForTests();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [AvaloniaTheory]
    [InlineData(1024, 600)]
    [InlineData(1366, 768)]
    public async Task KassaWarehouse_TabsAtTop_TotalsAtBottom_AfterEveryStep(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(wh, new UiSize(w, h));
            RunSteps(wh, $"касса {w}x{h}", resize: dh => { wh.Height = h + dh; });
        }
        finally
        {
            wh.Close();
            UiShot.Settle(2, 20);
        }
    }

    private static void RunSteps(WarehouseWindow wh, string tag, Action<int> resize)
    {
        var tabs = UiDrive.Find<TabControl>(wh, "WarehouseTabs");
        var vm = (WarehouseViewModel)wh.DataContext!;
        var tile = new CatalogProductTileVm("w-layout", "Тряпка для пола", "120 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 3, PurchasePrice = 80 };
        var line = PurchaseReceivingService.FromTile(tile, 1);
        line.EnableReceivingPricing();

        CheckTop(wh, tabs, $"{tag}: первый показ");
        tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ProductsTabItem");
        UiShot.Settle(3, 30);
        CheckTop(wh, tabs, $"{tag}: «Товары»");
        tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ReceivingTabItem");
        UiShot.Settle(3, 30);
        Shot(wh, tag, "receiving-empty");
        CheckReceiving(wh, tabs, $"{tag}: «Приёмка» пустая");
        vm.ReceivingLines.Add(line);
        UiShot.Settle(4, 30);
        CheckReceiving(wh, tabs, $"{tag}: «Приёмка» с одной строкой");
        Shot(wh, tag, "receiving");
        if (wh.FindControl<TabItem>("RevisionTabItem") is { IsVisible: true } revision)
        {
            tabs.SelectedItem = revision;
            UiShot.Settle(3, 30);
            CheckTop(wh, tabs, $"{tag}: «Ревизия»");
        }
        tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ReceivingTabItem");
        UiShot.Settle(3, 30);
        CheckReceiving(wh, tabs, $"{tag}: снова «Приёмка»");
        resize(-120);
        UiShot.Settle(4, 30);
        CheckReceiving(wh, tabs, $"{tag}: окно ниже на 120");
        resize(0);
        UiShot.Settle(4, 30);
        CheckReceiving(wh, tabs, $"{tag}: размер вернули");
        vm.ReceivingLines.Clear();
        UiShot.Settle(2, 20);
    }

    private static double Top(Control c, Window w) => c.TranslatePoint(default, w)!.Value.Y;

    /// <summary>Низ элемента в точках окна (с учётом масштаба обёртки раздела).</summary>
    private static double Bottom(Control c, Window w) => c.TranslatePoint(new Point(0, c.Bounds.Height), w)!.Value.Y;

    private static void CheckTop(Window wh, TabControl tabs, string step)
    {
        Top(tabs, wh).Should().BeLessThanOrEqualTo(100, $"вкладки склада сразу под заголовком ({step}); окно {wh.ClientSize}");
        Bottom(tabs, wh).Should().BeGreaterThan(wh.ClientSize.Height - 60, $"вкладки занимают окно до низа ({step}); окно {wh.ClientSize}, вкладки {tabs.Bounds}");
    }

    private static void CheckReceiving(Window wh, TabControl tabs, string step)
    {
        CheckTop(wh, tabs, step);
        var commit = UiDrive.Find<Button>(wh, "ReceivingCommitButton");
        var bottom = Bottom(commit, wh);
        bottom.Should().BeGreaterThan(wh.ClientSize.Height - 140, $"«Оприходовать» у нижнего края ({step}); окно {wh.ClientSize}, низ кнопки {bottom:0}");
        bottom.Should().BeLessThanOrEqualTo(wh.ClientSize.Height + 0.5, $"«Оприходовать» видна целиком ({step})");
        var grid = UiDrive.Find<DataGrid>(wh, "ReceivingGrid");
        if (wh.ClientSize.Height >= 600) // на совсем низком окне (масштаб 90 % и −120 px) таблица законно ужимается
            grid.Bounds.Height.Should().BeGreaterThan(120, $"таблица приёмки занимает остаток высоты ({step}); таблица {grid.Bounds}");
    }

    private static void Shot(Window wh, string tag, string name)
    {
        using var shot = UiShot.Render(wh);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"warehouse-layout-{name}-{tag.Replace(' ', '-').Replace(':', '-')}.png"), data.ToArray());
    }
}
