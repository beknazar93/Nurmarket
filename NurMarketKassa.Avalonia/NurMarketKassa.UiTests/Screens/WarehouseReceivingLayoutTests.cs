using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

/// <summary>2026-11-07 (жалоба владельца «Где в приёмке штрихкод?», фото ноутбука): «Склад → Приёмка» в узком виде (таблица уже 1300 px) прятала колонку
/// «Штрихкод» и серую подстроку под названием, у нового товара без названия был виден один значок «новый», заголовки рвались посреди слова
/// («Принимае/м», «Наценк/а %»). Окно настоящее (Avalonia.Headless), две строки: найденный товар (штрихкод, длинное название) и новый без названия и штрихкода.
/// Размеры — логические: 1024×600 и 1280×720 как есть, 1093×614 = 1366×768 при масштабе Windows 125 %, 1366×768 при 100 %.
/// Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\warehouse-receiving-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseReceivingLayoutTests
{
    private const string Barcode = "4600000000011";
    private const string LongName = "Реле зарядки Audi красное длинное название для проверки обрезки";

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1093, 614), (1280, 720), (1300, 740), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_Rows_ShowBarcode_DoNotOverlap_AndHeadersKeepWords(int width, int height, AppLanguage lang)
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
            var tile = new CatalogProductTileVm("w1", LongName, "250 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 3, PurchasePrice = 185 };
            var found = PurchaseReceivingService.FromTile(tile, 1);
            found.EnableReceivingPricing();
            var created = new ReceivingLineVm { Barcode = "", ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(found);
            vm.ReceivingLines.Add(created);
            UiShot.Settle(10);

            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var compact = grid.Classes.Contains("rcv-compact");
            compact.Should().Be(grid.Bounds.Width < 1300, $"узкий вид — по ширине таблицы {grid.Bounds.Width}");
            var tag = $"{width}x{height} {lang}";

            // --- строки: видны, не наезжают друг на друга, содержимое внутри своей строки
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.Bounds.Height > 0 && r.DataContext is ReceivingLineVm)
                .OrderBy(r => r.TranslatePoint(default, grid)!.Value.Y).ToList();
            rows.Should().HaveCount(2, tag);
            for (var i = 0; i < rows.Count; i++)
            {
                var top = rows[i].TranslatePoint(default, grid)!.Value.Y;
                if (i > 0)
                    top.Should().BeGreaterThanOrEqualTo(rows[i - 1].TranslatePoint(default, grid)!.Value.Y + rows[i - 1].Bounds.Height - 0.5, $"строки не наезжают ({tag})");
                foreach (var text in rows[i].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var y = text.TranslatePoint(default, rows[i])!.Value.Y;
                    y.Should().BeGreaterThanOrEqualTo(-0.5, $"«{text.Text}» не выше своей строки ({tag})");
                    (y + text.Bounds.Height).Should().BeLessThanOrEqualTo(rows[i].Bounds.Height + 0.5, $"«{text.Text}» не ниже своей строки ({tag})");
                }
                // последняя колонка (✕) целиком в таблице: горизонтальной прокрутки нет
                var cross = rows[i].GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible);
                cross.TranslatePoint(new Point(cross.Bounds.Width, 0), grid)!.Value.X.Should().BeLessThanOrEqualTo(grid.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({tag})");
            }

            // --- штрихкод найденного товара виден (подстрока под названием в узком виде, колонка в широком)
            var foundRow = rows.First(r => ReferenceEquals(r.DataContext, found));
            var barcodeText = foundRow.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && (t.Text ?? "").StartsWith(Barcode, StringComparison.Ordinal));
            barcodeText.Should().NotBeNull($"штрихкод товара виден в строке ({tag})");
            Need(barcodeText!).Should().BeLessThanOrEqualTo(barcodeText!.Bounds.Width + 1, $"штрихкод не обрезан ({tag})");
            foundRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == LongName).Should().BeTrue($"название видно ({tag})");

            // --- новый товар: подсказка про название, пометка «новый», «без штрихкода» в узком виде
            var newRow = rows.First(r => ReferenceEquals(r.DataContext, created));
            var newTexts = newRow.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0).Select(t => t.Text ?? "").ToList();
            newTexts.Should().Contain(WarehouseWindow.NewNameHintText, $"у нового товара видно подсказку про название ({tag})");
            newTexts.Should().Contain(created.NewBadgeText, $"значок «новый» ({tag})");
            if (compact)
                newTexts.Should().Contain(WarehouseWindow.NoBarcodeSubText, $"у нового товара в узком виде видно «без штрихкода» ({tag})");

            // --- правка названия нового товара: в узком виде под ним поле штрихкода, обе строки помещаются в строку таблицы
            grid.SelectedItem = created;
            grid.CurrentColumn = grid.Columns[1];
            grid.BeginEdit().Should().BeTrue();
            UiShot.Settle(6);
            var editors = newRow.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.GetVisualAncestors().OfType<DataGridCell>().Any()).ToList();
            editors.Should().NotBeEmpty($"поле названия в правке ({tag})");
            editors.First().Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле названия не схлопнуто ({tag})");
            if (compact)
            {
                var barEditor = editors.Single(t => t.Classes.Contains("rcv-baredit"));
                barEditor.Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле штрихкода не схлопнуто ({tag})");
                barEditor.TranslatePoint(new Point(0, barEditor.Bounds.Height), newRow)!.Value.Y.Should().BeLessThanOrEqualTo(newRow.Bounds.Height + 0.5, $"поле штрихкода в своей строке ({tag})");
                barEditor.Bounds.Width.Should().BeGreaterThanOrEqualTo(100, $"поле штрихкода читаемо ({tag})");
            }
            else
                editors.Any(t => t.Classes.Contains("rcv-baredit")).Should().BeFalse($"в широком виде штрихкод правят в своей колонке ({tag})");
            grid.CancelEdit();
            UiShot.Settle(4);

            // --- заголовки таблицы: ни одно слово не рвётся посреди (длиннейшее слово влезает по ширине)
            foreach (var head in grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible))
                foreach (var t in head.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var longest = (t.Text ?? "").Replace("­", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).First();
                    var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
                    var need = new FormattedText(longest, CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null).Width;
                    t.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 0.5, $"слово «{longest}» заголовка «{t.Text}» не должно рваться ({tag})");
                }

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"warehouse-receiving-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static double Need(TextBlock t)
    {
        var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
        return new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null).Width;
    }
}
