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

/// <summary>2026-11-10 (жалоба владельца: «Удобства не удобно! Количество 1 не удаляется!! Генерация штрихкода как в вебе!», «Наименование должно быть длинным!»): настоящее окно в Avalonia.Headless.
/// Одно нажатие на ячейку — сразу ввод с выделенным числом; Backspace оставляет пустое поле, набор заменяет; Enter — к «Закупке»; Esc откатывает; выход с пустым полем — прежнее значение.
/// Кнопка «Сгенерировать штрихкод» у нового товара (и в узком виде). Колонка «Название» — не уже 300 px на 1366×768 и 220 px на 1024×600.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingCellInputUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Key(Window w, Key key, PhysicalKey physical)
    {
        w.KeyPress(key, RawInputModifiers.None, physical, null);
        UiShot.Settle(6);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_SingleClickEditsWithSelection_EmptyStaysWhileTyping_EscRollsBack_GenerateBarcode(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var line = new ReceivingLineVm { Barcode = "", ProductName = "Новый товар для проверки ввода", Source = ReceivingSource.Unknown, Quantity = 3, PurchasePrice = 10, SalePrice = 15 }; // 3, а не 1: «1» стоит и в колонке «№»
            vm.ReceivingLines.Add(line);
            UiShot.Settle(10);
            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var tag = $"{width}x{height} {lang}";
            DataGridRow Row() => grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => ReferenceEquals(r.DataContext, line));
            TextBox? Focused() => TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox;

            // «Название» — главная колонка
            grid.Columns[1].ActualWidth.Should().BeGreaterThanOrEqualTo(width >= 1366 ? 300 : 220, $"название не ужато ({tag})");

            // одно нажатие на «Принимаем» — поле в фокусе, число выделено
            var qtyCell = Row().GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "3" && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            UiDrive.Click(window, qtyCell);
            UiShot.Settle(10);
            var box = Focused();
            box.Should().NotBeNull($"после одного нажатия — сразу ввод ({tag})");
            box!.Text.Should().Be("3");
            box.SelectedText.Should().Be("3", $"число выделено — набор заменит его ({tag})");

            // Backspace — пустое поле, «1» не возвращается; набор — своё число
            Key(window, Avalonia.Input.Key.Back, PhysicalKey.Backspace);
            Focused()!.Text.Should().Be("", $"«Количество не удаляется» — исправлено ({tag})");
            line.Quantity.Should().Be(0);
            UiShot.Settle(6);
            Focused()!.Text.Should().Be("", "пока набирают, значение не навязывается обратно");
            window.KeyTextInput("7");
            UiShot.Settle(4);
            line.Quantity.Should().Be(7);
            UiDrive.Find<TextBlock>(window, "SumQty").Text.Should().Be("7", $"Б-6: итог «Штук» обновился на лету, до Enter ({tag})");

            // Enter — к «Закупке», там тоже всё выделено; Esc откатывает правку ячейки
            Key(window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
            UiShot.Settle(6);
            grid.CurrentColumn.Should().BeSameAs(grid.Columns[6], $"Enter из «Принимаем» — в «Закупку» ({tag})");
            Focused()!.SelectedText.Should().Be("10");
            window.KeyTextInput("99");
            UiShot.Settle(4);
            line.PurchasePrice.Should().Be(99);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            UiShot.Settle(6);
            line.PurchasePrice.Should().Be(10, $"Esc вернул прежнюю закупку ({tag})");
            line.Quantity.Should().Be(7, "правка «Принимаем» уже закончена Enter — Esc её не трогает");

            // выход из ячейки с пустым полем — прежнее значение
            var qtyCell2 = Row().GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "7" && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            UiDrive.Click(window, qtyCell2);
            UiShot.Settle(10);
            Key(window, Avalonia.Input.Key.Back, PhysicalKey.Backspace);
            Focused()!.Text.Should().Be("");
            Key(window, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
            UiShot.Settle(6);
            line.Quantity.Should().Be(7, $"ушли с пустым полем — вернулось прежнее, а не «молча 1» ({tag})");
            grid.CommitEdit();
            UiShot.Settle(6);

            // Б-5 (видео 11.34.58): поле названия нового товара в правке видно целиком и с подсказкой (в узком виде под ним ещё поле штрихкода)
            grid.SelectedItem = line;
            grid.CurrentColumn = grid.Columns[1];
            grid.BeginEdit().Should().BeTrue();
            UiShot.Settle(8);
            var nameBox = Row().GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            nameBox.Bounds.Height.Should().BeGreaterThanOrEqualTo(30, $"поле названия не схлопнуто ({tag})");
            nameBox.Watermark.Should().Be(WarehouseWindow.NewNameWatermarkText);
            nameBox.TranslatePoint(new Point(0, nameBox.Bounds.Height), Row())!.Value.Y.Should().BeLessThanOrEqualTo(Row().Bounds.Height + 0.5, $"поле в своей строке ({tag})");
            grid.CommitEdit();
            UiShot.Settle(6);

            // «Сгенерировать штрихкод» у нового товара без штрихкода (и в узком виде)
            var gen = Row().GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && Equals(b.Tag, "rcv-genbar"));
            gen.Should().NotBeNull($"кнопка видна ({tag})");
            UiDrive.Click(window, gen!);
            UiShot.Settle(8);
            line.Barcode.Should().HaveLength(13);
            BarcodeGenerator.HasValidCheckDigit(line.Barcode).Should().BeTrue();
            line.Barcode[0].Should().NotBe('2');
            Row().GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Tag, "rcv-genbar")).Should().BeFalse("штрихкод есть — кнопка скрылась");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(1024, 640)]
    [InlineData(1366, 768)]
    public async Task PurchasesReceive_ClickSelectsAll_EmptyLeaveRestores_EscRollsBack(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        const string title = "Вода питьевая негазированная 0,5 л — длинное название для проверки ширины";
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", title, "30 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 10, PurchasePrice = 25 });
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", title, "шт", 3.0, (double?)null) }));
            UiShot.Settle(12);
            TextBox Cell(string startsWith) => window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell"
                && (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith(startsWith, StringComparison.Ordinal));
            TextBox? Focused() => TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox;

            var qty = Cell("Количество");
            qty.Text.Should().Be("3");
            UiDrive.Click(window, qty);
            UiShot.Settle(8);
            Focused().Should().BeSameAs(qty);
            qty.SelectedText.Should().Be("3", "клик — всё число выделено");
            Key(window, Avalonia.Input.Key.Back, PhysicalKey.Backspace);
            qty.Text.Should().Be("", "пустое поле остаётся пустым, пока набирают");
            Key(window, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
            UiShot.Settle(6);
            qty.Text.Should().Be("3", "ушли с пустым полем — прежнее количество");

            var price = Cell("Цена закупки");
            UiDrive.Click(window, price);
            UiShot.Settle(8);
            window.KeyTextInput("777");
            UiShot.Settle(4);
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            UiShot.Settle(6);
            price.Text.Should().Be("25", "Esc — откат правки поля");

            var name = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Text == title);
            name.Bounds.Width.Should().BeGreaterThanOrEqualTo(width >= 1366 ? 300 : 220, "название — главная колонка");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    /// <summary>2026-11-11 (регрессия из аудита «Закупки — удобство и дизайн»: «Принимаем» 0 у всех строк, «Позиций 0», итог «Сканируйте товары»): отложенный TextChanged скрытого
    /// поля «шт в упак.» (пустой текст) обнулял количество каждой строки после отрисовки; «Наценка %» и «Цена продажи» так же пересчитывали цену. Строка скана (+1) и строка заказа (5) —
    /// в ячейках 1 и 5, «Позиций: 2 · штук: 6», цена продажи и её режим не меняются сами.</summary>
    [AvaloniaTheory]
    [InlineData(1024, 640)]
    [InlineData(1366, 768)]
    public async Task PurchasesReceive_ScanLineAndOrderLine_KeepQuantities_TotalsAndSalePrice(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", "Вода питьевая 0,5 л", "30 сом", false) { Barcode = "4600000000011", Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk2", "Сахар-рафинад 1 кг", "10 сом", false) { Barcode = "4600000000028", Unit = "шт", Quantity = 4, PurchasePrice = 7 }); // наценка 42,86 % — показ округлён
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk2", "Сахар-рафинад 1 кг", "шт", 5.0, (double?)null) }));
            UiShot.Settle(12);
            var scan = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && (t.Watermark ?? "").Contains("штрихкод", StringComparison.OrdinalIgnoreCase));
            scan.Text = "4600000000011";
            scan.Focus();
            UiShot.Pump();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(16, 20);
            var tag = $"{width}x{height}";

            var rs = typeof(PurchasesWindow).GetField("_receive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var lines = ((IEnumerable<ReceivingLineVm>)rs.GetType().GetField("Lines")!.GetValue(rs)!).ToList();
            lines.Should().HaveCount(2, tag);
            lines.Single(l => l.ProductId == "pk1").Quantity.Should().Be(1, $"строка скана — 1 ({tag})");
            var sugar = lines.Single(l => l.ProductId == "pk2");
            sugar.Quantity.Should().Be(5, $"строка заказа — 5 ({tag})");
            sugar.SalePrice.Should().Be(10, $"цена продажи не пересчитана из округлённой наценки ({tag})");
            sugar.SaleMode.Should().NotBe(SalePriceMode.Manual, $"строку никто не правил — не «вручную» ({tag})");
            sugar.PurchasePrice.Should().Be(7);

            var qtyTexts = window.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell" && (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith("Количество", StringComparison.Ordinal))
                .Select(t => t.Text).ToList();
            qtyTexts.Should().BeEquivalentTo(new[] { "1", "5" }, $"ячейки «Принимаем» ({tag})");
            var text = UiDrive.AllText(window);
            text.Should().Contain("Позиций: 2 · штук: 6", $"итог внизу ({tag})");
            text.Should().NotContain("Сканируйте товары или выберите поставщика", tag);

            // Б-4 на стыке с упаковками: скан кода к строке в режиме упаковок — +1 упаковка (как «Склад → Приёмка»), а не +1 штука
            sugar.SetPackMode(true, 5); // 5 шт = 1 упаковка по 5
            UiShot.Settle(6);
            scan.Text = "4600000000028";
            scan.Focus();
            UiShot.Pump();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(16, 20);
            sugar.PackCount.Should().Be(2, $"+1 упаковка ({tag})");
            sugar.Quantity.Should().Be(10, $"2 × 5 шт ({tag})");
            sugar.SalePrice.Should().Be(10, tag);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
