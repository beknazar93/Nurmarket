using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 («Аналитика товаров» в программе владельца, вкладка «Склад» раздела «Аналитика»): настоящий элемент <see cref="WarehouseStockAnalyticsView"/> на 1024×600, 800×600 и 1366×768,
/// ru / ky / uz. Проверяется: плитки показателей (не меньше 9, без закупки — 11 с ней) не налезают друг на друга и не выходят за ширину; фильтры (поиск, категория, Excel) не налезают друг на друга;
/// текст не обрезан (TextClipChecker); заголовки колонок каждой таблицы не пересекаются; таблицы идут друг под другом без перекрытия; клик по строке категории раскрывает список её товаров ниже таблицы;
/// фильтр «ничего не найдено» оставляет плитки с нулями без падения. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\stock-analytics-*.png.</summary>
[Collection("UiScenarios")]
public sealed class StockAnalyticsUiTests : IDisposable
{
    private List<CatalogProductTileVm>? _savedProducts;

    public StockAnalyticsUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        if (_savedProducts != null)
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(_savedProducts);
        }
    }

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private void Seed()
    {
        _savedProducts ??= CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        var rnd = new Random(27);
        var categories = new[] { "Бакалея", "Напитки", "Сухофрукты", "Электроника", "Үй тиричилик химиясы, тазалоочу жана жуучу каражаттар (оптом жана чекене)", "Молочные продукты", "Хозтовары", "Канцелярия" };
        for (var i = 0; i < 90; i++)
        {
            var qty = i % 9 == 0 ? 0 : i % 11 == 0 ? -2 : i % 5 == 0 ? 3 : 10 + rnd.Next(500);
            var t = new CatalogProductTileVm("sa" + i, i == 3 ? "Очень длинное название товара для проверки обрезания текста в таблице аналитики склада " + i : "Товар " + i, (20 + rnd.Next(2000)) + ".50 сом", false)
            {
                Category = i % 13 == 0 ? null : categories[i % categories.Length],
                Barcode = i % 7 == 0 ? null : (4600000000000L + i).ToString(),
                PurchasePrice = 10 + rnd.Next(500),
                Unit = "шт",
            };
            t.Quantity = qty;
            t.IsLowStock = CatalogProductTileVm.IsLow(qty, null);
            CatalogCacheService.Products.Add(t);
        }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (800, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static Rect Box(Window w, Control c) => new(c.TranslatePoint(default, w) ?? default, c.Bounds.Size);

    private static void NoOverlap(IReadOnlyList<(string Name, Rect Box)> items, string tag, double tolerance = 1.5)
    {
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                items[i].Box.Deflate(tolerance).Intersects(items[j].Box.Deflate(tolerance)).Should()
                    .BeFalse($"«{items[i].Name}» {items[i].Box} и «{items[j].Name}» {items[j].Box} не налезают {tag}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task View_NoOverlap_NotClipped_CategoryExpands(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        Seed();
        var tag = $"({width}x{height} {Code(lang)})";
        var view = new WarehouseStockAnalyticsView();
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16, 8), Child = view } };
        try
        {
            window.Show();
            view.Refresh();
            UiShot.Settle(8);

            // плитки
            var tiles = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("sec-kpi") && b.IsEffectivelyVisible).ToList();
            tiles.Count.Should().BeGreaterThanOrEqualTo(9, $"плитки показателей {tag}");
            NoOverlap(tiles.Select((t, i) => ($"плитка {i + 1}", Box(window, t))).ToList(), tag);
            foreach (var t in tiles)
                Box(window, t).Right.Should().BeLessThanOrEqualTo(width + 0.5, $"плитка не шире окна {tag}");

            // фильтры: поле поиска, список категорий, кнопка Excel — в одной строке, не налезают
            var search = view.GetVisualDescendants().OfType<TextBox>().First(x => x.IsEffectivelyVisible);
            var combo = view.GetVisualDescendants().OfType<ComboBox>().First(x => x.IsEffectivelyVisible);
            var export = view.GetVisualDescendants().OfType<Button>().First(x => x.IsEffectivelyVisible && x.Classes.Contains("sec-outline"));
            NoOverlap([("поиск", Box(window, search)), ("категория", Box(window, combo)), ("Excel", Box(window, export))], tag);
            search.Bounds.Width.Should().BeGreaterThanOrEqualTo(120, $"поиск не сжат до щели {tag}");
            Box(window, export).Right.Should().BeLessThanOrEqualTo(width + 0.5, tag);

            // текст не обрезан
            TextClipChecker.Find(window, "stock-analytics", Code(lang)).Should().BeEmpty($"текст не обрезан {tag}");

            // заголовки колонок каждой таблицы не пересекаются; таблицы одна под другой
            var grids = view.GetVisualDescendants().OfType<DataGrid>().Where(g => g.IsEffectivelyVisible).ToList();
            grids.Count.Should().BeGreaterThanOrEqualTo(3, tag);
            foreach (var g in grids)
            {
                var heads = g.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0).ToList();
                NoOverlap(heads.Select((h, i) => ($"колонка {i + 1}", Box(window, h))).ToList(), tag);
            }
            var gridBoxes = grids.Select((g, i) => ($"таблица {i + 1}", Box(window, g))).ToList();
            NoOverlap(gridBoxes, tag, 0.5);

            // раскрытие категории: выбор строки показывает список её товаров под таблицей категорий
            var categoryGrid = grids[0];
            categoryGrid.SelectedIndex = 0;
            UiShot.Settle(6);
            var after = view.GetVisualDescendants().OfType<DataGrid>().Where(g => g.IsEffectivelyVisible).ToList();
            after.Count.Should().Be(grids.Count + 1, $"список товаров категории раскрылся {tag}");
            NoOverlap(after.Select((g, i) => ($"таблица {i + 1}", Box(window, g))).ToList(), tag, 0.5);
            TextClipChecker.Find(window, "stock-analytics-open", Code(lang)).Should().BeEmpty($"текст не обрезан после раскрытия {tag}");
            SavePng(window, $"stock-analytics-{Code(lang)}-{width}x{height}");

            // ничего не найдено: плитки на месте, без падения
            search.Text = "zzzнетттакого";
            UiShot.Settle(12, 60);
            view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("sec-kpi") && b.IsEffectivelyVisible).Should().BeGreaterThanOrEqualTo(9, tag);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void SavePng(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }
}
