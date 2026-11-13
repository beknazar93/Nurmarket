using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (аудит «Закупки — удобство и дизайн», Ф0): обе приёмки в Avalonia.Headless — F2/Ctrl+F ведут в поле скана, Del (Ctrl+Del в поле) убирает строку
/// с плашкой «Вернуть» и Ctrl+Z, F9 запускает «Оприходовать» только когда кнопка доступна, крупное «Итого приход» у кнопки и строка подсказок клавиш (ru/ky, 1024×600 и 1366×768,
/// без обрезки текста), «Подробнее ▸» в «Склад → Приёмка» (свёрнуто на таблице уже 1500, выбор запоминается).</summary>
[Collection("UiScenarios")]
public sealed class ReceivingHotkeysUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Press(Window w, Key key, PhysicalKey physical, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, physical, null);
        w.KeyRelease(key, mods, physical, null);
        UiShot.Settle(6);
    }

    private static Control? Focused(Window w) => TopLevel.GetTopLevel(w)!.FocusManager!.GetFocusedElement() as Control;

    private static List<ReceivingLineVm> PurchaseLines(PurchasesWindow window)
    {
        var rs = typeof(PurchasesWindow).GetField("_receive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        return ((IEnumerable<ReceivingLineVm>)rs.GetType().GetField("Lines")!.GetValue(rs)!).ToList();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task PurchasesReceive_HotKeys_DeleteWithUndo_TotalAndKeysHint(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", "Вода питьевая 0,5 л", "30 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk2", "Сахар-рафинад 1 кг", "60 сом", false) { Barcode = "4600000000028", Unit = "шт", Quantity = 4, PurchasePrice = 50 });
        var window = new PurchasesWindow();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Вода питьевая 0,5 л", "шт", 3.0, (double?)null), ("pk2", "Сахар-рафинад 1 кг", "шт", 2.0, (double?)null) }));
            UiShot.Settle(14);
            var scan = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark is { Length: > 0 } wm
                && (wm.Contains("Штрихкод", StringComparison.OrdinalIgnoreCase) || wm.Contains("штрихкод", StringComparison.OrdinalIgnoreCase)));
            var text = UiDrive.AllText(window);

            // БП-6: «Итого приход» 3 × 20 + 2 × 50 = 160 — крупно у кнопки
            text.Should().Contain(lang == AppLanguage.Kyrgyz ? "Жалпы кирүү" : "Итого приход", tag);
            var total = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.FontSize >= 19 && t.Classes.Contains("num"));
            total.Text.Should().Be(MoneyText.Som(160.0), tag);
            // БП-5: строка подсказок клавиш
            text.Should().Contain("F9 / Ctrl+Enter", tag);
            TextClipChecker.Find(window, "purchases-receive-keys", lang == AppLanguage.Kyrgyz ? "ky" : "ru")
                .Where(v => v.Detail.Contains("F2") || v.Detail.Contains("Итого приход") || v.Detail.Contains("Жалпы кирүү") || v.Key.Contains("F2")).Should().BeEmpty($"строка клавиш и итог не обрезаны ({tag})");

            // F2 — в поле скана
            var qty = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell" && t.Text == "3");
            qty.Focus();
            UiShot.Settle(4);
            Press(window, Key.F2, PhysicalKey.F2);
            Focused(window).Should().BeSameAs(scan, $"F2 — поле скана ({tag})");
            qty.Focus();
            UiShot.Settle(4);
            Press(window, Key.F, PhysicalKey.F, RawInputModifiers.Control);
            Focused(window).Should().BeSameAs(scan, $"Ctrl+F — поле скана ({tag})");

            // Ctrl+Del в поле строки — строка убрана, «Вернуть» возвращает на место
            qty.Focus();
            UiShot.Settle(4);
            Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.Control);
            PurchaseLines(window).Select(l => l.ProductId).Should().Equal(new[] { "pk2" }, tag);
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "сабы алынды" : "убрана", tag);
            total = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.FontSize >= 19 && t.Classes.Contains("num"));
            total.Text.Should().Be(MoneyText.Som(100.0), $"итог пересчитан ({tag})");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Вернуть", "Кайтаруу"));
            UiShot.Settle(8);
            PurchaseLines(window).Select(l => l.ProductId).Should().Equal(new[] { "pk1", "pk2" }, $"строка вернулась на прежнее место ({tag})");

            // ✕ — тоже с «Вернуть»; Ctrl+Z из пустого поля скана возвращает
            var remove = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "✕");
            remove.Focusable.Should().BeTrue($"БП-8: ✕ доступна с Tab ({tag})");
            UiDrive.Click(window, remove);
            UiShot.Settle(8);
            PurchaseLines(window).Should().HaveCount(1, tag);
            scan.Focus();
            UiShot.Settle(4);
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
            PurchaseLines(window).Should().HaveCount(2, $"Ctrl+Z вернул строку ({tag})");

            // Del на ✕ (фокус не в поле) — убирает строку без Ctrl
            remove = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "✕");
            remove.Focus();
            UiShot.Settle(4);
            Press(window, Key.Delete, PhysicalKey.Delete);
            PurchaseLines(window).Should().HaveCount(1, $"Del на строке ({tag})");

            // F9 — «Оприходовать»: кнопка доступна → спросили поставщика (окно), закрыли — ничего не проведено
            Press(window, Key.F9, PhysicalKey.F9);
            UiShot.Settle(10);
            var ask = window.OwnedWindows.OfType<PurchaseNoSupplierDialog>().ToList();
            ask.Should().ContainSingle($"F9 нажал «Оприходовать» ({tag})");
            ask[0].Close();
            UiShot.Settle(10);
            PurchaseLines(window).Should().HaveCount(1, "отмена — строки на месте");
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_HotKeys_DeleteWithUndo_Total_AndDetailsToggle(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var a = new ReceivingLineVm { Barcode = "4600000000011", ProductName = "Вода питьевая", Source = ReceivingSource.Unknown, Quantity = 3, PurchasePrice = 20, SalePrice = 30 };
            var b = new ReceivingLineVm { Barcode = "4600000000028", ProductName = "Сахар", Source = ReceivingSource.Unknown, Quantity = 2, PurchasePrice = 50, SalePrice = 60 };
            vm.ReceivingLines.Add(a);
            vm.ReceivingLines.Add(b);
            UiShot.Settle(12);
            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var scan = UiDrive.Find<TextBox>(window, "ReceivingScanBox");
            var text = UiDrive.AllText(window);

            text.Should().Contain(lang == AppLanguage.Kyrgyz ? "Жалпы кирүү" : "Итого приход", tag);
            text.Should().Contain(MoneyText.Som(160.0), $"итог 3 × 20 + 2 × 50 ({tag})");
            if (height >= WarehouseWindow.ReceivingKeysHintMinHeight)
                text.Should().Contain("F9 / Ctrl+Enter", tag);
            else
                ToolTip.GetTip(UiDrive.Find<Button>(window, "ReceivingCommitButton")).Should().Be(WarehouseWindow.ReceivingKeysHintText, $"на низком экране строка клавиш — в подсказке кнопки ({tag})");

            // «Подробнее ▸»: таблица уже 1500 — подробные колонки свёрнуты; кнопка раскрывает и запоминает
            grid.Columns[10].IsVisible.Should().BeFalse($"«Как посчитана цена» свёрнута по умолчанию ({tag})");
            var details = UiDrive.ButtonByText(window, "Подробнее ▸", "Толугураак ▸");
            UiDrive.Click(window, details);
            UiShot.Settle(10);
            grid.Columns[10].IsVisible.Should().BeTrue($"раскрыли ({tag})");
            ReceivingViewPrefs.Details.Should().BeTrue("выбор запомнен");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Скрыть подробности ◂", "Толугун жашыруу ◂"));
            UiShot.Settle(10);
            grid.Columns[10].IsVisible.Should().BeFalse(tag);

            // F2 — поле скана
            grid.SelectedItem = a;
            grid.Focus();
            UiShot.Settle(4);
            Press(window, Key.F2, PhysicalKey.F2);
            Focused(window).Should().BeSameAs(scan, $"F2 — поле скана ({tag})");

            // Del на выбранной строке (фокус в таблице, не в поле) — убрана; Ctrl+Z — вернулась на место
            grid.SelectedItem = a;
            grid.Focus();
            UiShot.Settle(4);
            Press(window, Key.Delete, PhysicalKey.Delete);
            vm.ReceivingLines.Should().Equal(new[] { b }, tag);
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "сабы алынды" : "убрана", tag);
            grid.Focus();
            UiShot.Settle(4);
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
            vm.ReceivingLines.Should().Equal(new[] { a, b }, $"Ctrl+Z вернул строку на прежнее место ({tag})");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }
}
