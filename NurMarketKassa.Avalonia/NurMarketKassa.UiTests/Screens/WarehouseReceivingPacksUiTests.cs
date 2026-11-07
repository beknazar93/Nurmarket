using System.Globalization;
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
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-07 («Склад → Приёмка»: упаковки, решение владельца «Да» + «типа поштучной продажи»): настоящее окно в Avalonia.Headless. Три строки — «упаковками → на склад штуки»,
/// «упаковка с поштучной продажей» (новый товар) и обычная. Проверяется: поля «Принимаем» / «шт/упак.» / «Закупка» помещаются в строку, строки не наезжают друг на друга,
/// штрихкод по-прежнему виден (узкий вид — подстрока под названием), высота строки не меньше содержимого, порядок Enter «Принимаем → шт/упак. → Закупка», итоги — от пересчитанных штук.
/// Размеры — логические: 1024×600, 1093×614 (1366×768 при масштабе 125 %), 1366×768. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\warehouse-packs-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseReceivingPacksUiTests
{
    private const string Barcode = "4600000000011";
    private const string LongName = "Реле зарядки Audi красное длинное название для проверки обрезки";

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1093, 614), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_PackModes_FitInRows_KeepBarcode_AndEnterOrderWorks(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = CatalogCacheService.Products.ToList();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;

            var tile = new CatalogProductTileVm("w1", LongName, "250 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 3, PurchasePrice = 185 };
            var byPacks = PurchaseReceivingService.FromTile(tile, 1);
            byPacks.EnableReceivingPricing();
            byPacks.SetPackMode(true, 24);
            byPacks.SetPackCount(5);
            byPacks.SetPackPriceText("600");
            byPacks.SalePrice = 30;

            var packSale = new ReceivingLineVm { Barcode = "4602000000017", ProductName = "Сигареты Космос синие", Source = ReceivingSource.Unknown, Quantity = 1 };
            packSale.SetPackSale(true, 10).Should().BeTrue();
            packSale.SetPackCount(3);
            packSale.SetPackPriceText("480");
            packSale.SalePrice = 600;

            var plain = new ReceivingLineVm { Barcode = "", ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(byPacks);
            vm.ReceivingLines.Add(packSale);
            vm.ReceivingLines.Add(plain);
            UiShot.Settle(10);

            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var tag = $"{width}x{height} {lang}";
            var compact = grid.Classes.Contains("rcv-compact");
            var rowHeight = compact ? 48 : 52;

            List<DataGridRow> Rows() => grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.Bounds.Height > 0 && r.DataContext is ReceivingLineVm)
                .OrderBy(r => r.TranslatePoint(default, grid)!.Value.Y).ToList();

            void CheckRows(string stage)
            {
                var rows = Rows();
                rows.Count.Should().BeGreaterThanOrEqualTo(2, $"{stage} {tag}: в таблице видно минимум две строки (строка упаковок — 84 px, на 1024×600 третья уходит под прокрутку)");
                for (var i = 0; i < rows.Count; i++)
                {
                    var top = rows[i].TranslatePoint(default, grid)!.Value.Y;
                    if (i > 0)
                        top.Should().BeGreaterThanOrEqualTo(rows[i - 1].TranslatePoint(default, grid)!.Value.Y + rows[i - 1].Bounds.Height - 0.5, $"строки не наезжают ({stage} {tag})");
                    foreach (var text in rows[i].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && !string.IsNullOrWhiteSpace(t.Text)))
                    {
                        var y = text.TranslatePoint(default, rows[i])!.Value.Y;
                        y.Should().BeGreaterThanOrEqualTo(-0.5, $"«{text.Text}» не выше своей строки ({stage} {tag})");
                        (y + text.Bounds.Height).Should().BeLessThanOrEqualTo(rows[i].Bounds.Height + 0.5, $"«{text.Text}» не ниже своей строки ({stage} {tag})");
                    }
                    var cross = rows[i].GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible);
                    cross.TranslatePoint(new Point(cross.Bounds.Width, 0), grid)!.Value.X.Should().BeLessThanOrEqualTo(grid.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({stage} {tag})");
                }
                // высота: строки упаковок выше обычной и не меньше содержимого; обычная — прежние 48/52
                rows.Where(r => ReferenceEquals(r.DataContext, byPacks)).ToList().ForEach(r => r.Bounds.Height.Should().BeGreaterThanOrEqualTo(84 - 0.5, $"строка «упаковками → штуки» выше ({stage} {tag})"));
                rows.Where(r => ReferenceEquals(r.DataContext, packSale)).ToList().ForEach(r => r.Bounds.Height.Should().BeGreaterThanOrEqualTo(84 - 0.5, $"строка поштучной продажи выше ({stage} {tag})"));
                rows.Where(r => ReferenceEquals(r.DataContext, plain)).ToList().ForEach(r => r.Bounds.Height.Should().BeLessThanOrEqualTo(rowHeight + 1.5, $"обычная строка не раздулась ({stage} {tag})"));
            }

            CheckRows("показ");

            var foundRow = Rows().Single(r => ReferenceEquals(r.DataContext, byPacks));
            // штрихкод товара виден (подстрока под названием в узком виде, колонка в широком), название тоже
            foundRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && (t.Text ?? "").StartsWith(Barcode, StringComparison.Ordinal)).Should().BeTrue($"штрихкод виден ({tag})");
            foundRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == LongName).Should().BeTrue($"название видно ({tag})");
            var shown = foundRow.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            shown.Should().Contain(byPacks.PackShortNote, $"под числом упаковок — «× 24 = 120 шт.» ({tag})");
            shown.Should().Contain(byPacks.PackLinkShort, $"ссылка способа приёмки ({tag})");
            shown.Should().Contain(byPacks.PackPriceNote, $"под закупкой — за упак. и за шт ({tag})");
            var saleRow = Rows().Single(r => ReferenceEquals(r.DataContext, packSale));
            saleRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == packSale.PackLinkShort).Should().BeTrue($"ссылка поштучной продажи ({tag})");
            saleRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == packSale.PackShortNote).Should().BeTrue($"«по 10 шт» ({tag})");

            // подписи-ссылки не обрезаны многоточием (короткий текст влезает в колонку 96 px)
            foreach (var row in new[] { foundRow, saleRow })
            {
                var link = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && (t.Text ?? "").EndsWith("▾", StringComparison.Ordinal));
                var typeface = new Avalonia.Media.Typeface(link.FontFamily, link.FontStyle, link.FontWeight, link.FontStretch);
                var need = new Avalonia.Media.FormattedText(link.Text ?? "", CultureInfo.CurrentUICulture, link.FlowDirection, typeface, link.FontSize, null).Width;
                link.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 1, $"ссылка «{link.Text}» не обрезана ({tag})");
            }

            // итоги внизу — от пересчитанных штук: 120 (5 упак. × 24) + 3 (упаковки поштучной продажи) + 1
            UiDrive.Find<TextBlock>(window, "SumQty").Text.Should().Be((120 + 3 + 1).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")), $"итог «Штук» ({tag})");
            UiDrive.Find<TextBlock>(window, "SumPos").Text.Should().Be("3");

            // --- правка «Принимаем» в режиме упаковок: два поля (упаковки и «шт/упак.»), оба в строке; Enter ведёт «Принимаем → шт/упак. → Закупка»
            grid.SelectedItem = byPacks;
            grid.CurrentColumn = grid.Columns[5];
            grid.BeginEdit().Should().BeTrue();
            UiShot.Settle(8);
            List<TextBox> Editors(object line) => Rows().Single(r => ReferenceEquals(r.DataContext, line)).GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.IsEffectivelyVisible && t.GetVisualAncestors().OfType<DataGridCell>().Any()).ToList();
            var editors = Editors(byPacks);
            var qtyBox = editors.First(t => !t.Classes.Contains("rcv-pieces"));
            var piecesBox = editors.Single(t => t.Classes.Contains("rcv-pieces"));
            qtyBox.Text.Should().Be("5", "в режиме упаковок «Принимаем» — упаковки");
            piecesBox.Text.Should().Be("24");
            var editedRow = Rows().Single(r => ReferenceEquals(r.DataContext, byPacks));
            foreach (var box in new[] { qtyBox, piecesBox })
            {
                box.Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле не схлопнуто ({tag})");
                box.Bounds.Width.Should().BeGreaterThanOrEqualTo(50, $"поле читаемо ({tag})");
                box.TranslatePoint(new Point(0, box.Bounds.Height), editedRow)!.Value.Y.Should().BeLessThanOrEqualTo(editedRow.Bounds.Height + 0.5, $"поле в своей строке ({tag})");
            }
            piecesBox.TranslatePoint(new Point(0, 0), editedRow)!.Value.Y.Should().BeGreaterThanOrEqualTo(qtyBox.TranslatePoint(new Point(0, qtyBox.Bounds.Height), editedRow)!.Value.Y - 0.5, $"поля друг на друга не наезжают ({tag})");

            qtyBox.Focus();
            UiShot.Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            UiShot.Settle(8);
            (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox)!.Classes.Should().Contain("rcv-pieces", $"Enter из «Принимаем» ведёт в «шт/упак.» ({tag})");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            UiShot.Settle(8);
            grid.CurrentColumn.Should().BeSameAs(grid.Columns[6], $"Enter из «шт/упак.» ведёт в «Закупка» ({tag})");
            var priceBox = Editors(byPacks).Single();
            priceBox.Text.Should().Be("600", "в режиме упаковок «Закупка» — цена за упаковку");
            priceBox.Bounds.Height.Should().BeGreaterThanOrEqualTo(20);
            grid.CommitEdit();
            UiShot.Settle(6);
            CheckRows("после правки");

            // --- правка у строки поштучной продажи и у обычной: поля в строке
            foreach (var line in new[] { packSale, plain })
            {
                grid.SelectedItem = line;
                grid.ScrollIntoView(line, null);
                UiShot.Settle(4);
                grid.CurrentColumn = grid.Columns[5];
                grid.BeginEdit().Should().BeTrue();
                UiShot.Settle(6);
                var row = Rows().Single(r => ReferenceEquals(r.DataContext, line));
                foreach (var box in Editors(line))
                {
                    box.Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле не схлопнуто ({tag})");
                    box.TranslatePoint(new Point(0, box.Bounds.Height), row)!.Value.Y.Should().BeLessThanOrEqualTo(row.Bounds.Height + 0.5, $"поле в своей строке ({tag})");
                }
                grid.CommitEdit();
                UiShot.Settle(4);
            }
            CheckRows("после правок");

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"warehouse-packs-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    /// <summary>«Провести» со строкой «упаковка с поштучной продажей» спрашивает подтверждение («Товар будет вестись упаковками по N шт…»); отказ — ничего не проводится, строка остаётся в таблице.</summary>
    [AvaloniaFact]
    public async Task WarehouseReceiving_PackSale_AsksConfirmationBeforePosting_AndDeclineKeepsTheLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1280, 720));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var line = new ReceivingLineVm { Barcode = "4602000000017", ProductName = "Сигареты Космос синие", Source = ReceivingSource.Unknown, Quantity = 1 };
            line.SetPackSale(true, 10).Should().BeTrue();
            line.SetPackCount(3);
            line.SetPackPriceText("480");
            line.SalePrice = 600;
            vm.ReceivingLines.Add(line);
            UiShot.Settle(6);

            IReadOnlyList<ReceivingLineVm>? asked = null;
            vm.ConfirmPackSale = lines =>
            {
                asked = lines;
                return Task.FromResult(false); // «Вернуться»
            };
            vm.CommitReceivingCommand.Execute(null);
            UiShot.Settle(12);

            asked.Should().NotBeNull("перед проведением спросили подтверждение");
            asked!.Should().ContainSingle().Which.Should().BeSameAs(line);
            vm.ReceivingLines.Should().Contain(line, "отказ — строка осталась, ничего не проведено");
            ReceivingPacks.PackSaleConfirmMessage(asked).Should().Contain("по 10 шт").And.Contain("остаток — в упаковках");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Меню способа приёмки в таблице Склада: у существующего товара «поштучная продажа» недоступна (только новый товар), «упаковками → штуки» включается, у нового — все три пункта доступны.</summary>
    [AvaloniaFact]
    public async Task WarehouseReceiving_PackMenu_BlocksPackSaleForExistingProduct_AndSwitchesModes()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1280, 720));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var tile = new CatalogProductTileVm("w2", "Вода", "30 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 7, PurchasePrice = 25 };
            var existing = PurchaseReceivingService.FromTile(tile, 1);
            var created = new ReceivingLineVm { Barcode = "4602", ProductName = "Новая", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(existing);
            vm.ReceivingLines.Add(created);
            UiShot.Settle(8);

            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            TextBlock Link(ReceivingLineVm line) => grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => ReferenceEquals(r.DataContext, line))
                .GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == line.PackLinkShort);
            MenuItem Item(string start) => window.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.IsEffectivelyVisible && m.Header is string h && h.TrimStart().TrimStart((char)0x2713).TrimStart().StartsWith(start, StringComparison.Ordinal)) ?? throw new InvalidOperationException("нет пункта «" + start + "»; пункты: " + string.Join(" | ", window.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header + "/" + m.IsEffectivelyVisible)) + "; hosts=" + window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.OverlayPopupHost>().Count() + " popups=" + window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Count() + "; ссылки: " + string.Join(" | ", window.GetVisualDescendants().OfType<TextBlock>().Where(t => (t.Text ?? "").EndsWith("▾")).Select(t => t.Text + "@" + t.Bounds + "/" + t.IsEffectivelyVisible)));

            // существующий товар с остатком: поштучная продажа недоступна, а «упаковками → штуки» включается

            UiDrive.Click(window, Link(existing));
            UiShot.Settle(6);
            Item("Упаковка с поштучной продажей").IsEnabled.Should().BeFalse("у товара с остатком смена единицы остатка исказит склад");
            Item("Упаковками → на склад штуки").IsEnabled.Should().BeTrue();
            UiDrive.Click(window, Item("Упаковками → на склад штуки"));
            UiShot.Settle(8);
            existing.PackMode.Should().BeTrue();
            existing.PackSale.Should().BeFalse();
            existing.PackNeedsSize.Should().BeTrue("размер упаковки ещё не введён — провести нельзя");
            PurchaseReceivingService.Validate(new[] { existing }).Should().Contain(p => p.Contains("упаков"), "окно Склада использует Validate: блокировка без «шт в упак.»");

            // новый товар: поштучная продажа доступна
            UiDrive.Click(window, Link(created));
            UiShot.Settle(6);
            Item("Упаковка с поштучной продажей").IsEnabled.Should().BeTrue();
            UiDrive.Click(window, Item("Упаковка с поштучной продажей"));
            UiShot.Settle(8);
            created.PackSale.Should().BeTrue();
            created.PackLinkText.Should().Contain("поштучно");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
