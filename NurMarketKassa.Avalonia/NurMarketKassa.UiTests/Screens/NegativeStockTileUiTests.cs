using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22, владелец: «в кассе минус товары — количество, ушедшее в минус, показывать по-особенному». Плитка и список каталога: остаток −3/−12 — подпись «Минус −N шт.»
/// жирным красным (класс stock-minus), текст не обрезается ни в плитке, ни в списке (узком и широком) на ru / ky / uz; обычный «0 шт.» остаётся прежним (stock-out без stock-minus).</summary>
[Collection("UiScenarios")]
public sealed class NegativeStockTileUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new[] { 500.0, 650, 1000 }.SelectMany(w => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { w, l }));

    private static void AddTiles(List<CatalogProductTileVm> items)
    {
        foreach (var (id, title, qty, weighed) in new[]
                 {
                     ("m1", "DECO DVC15L DECO ПЫЛЕСОС 1300W", -3d, false),
                     ("m2", "Сахар песок", -12.5, true),
                     ("z0", "Нулевой остаток", 0d, false),
                 })
        {
            var tile = new CatalogProductTileVm(id, title, "9100.00 сом", weighed) { Barcode = "47000000" + id, Unit = weighed ? "кг" : "шт", Quantity = qty };
            ProductUnitNormalizer.ApplyToTile(tile);
            items.Add(tile);
        }
    }

    private static bool FitsWithoutEllipsis(TextBlock t)
    {
        var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
        var need = new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null).Width;
        return t.Bounds.Width >= need - 1;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task List_MinusStock_IsSpecial_AndNotClipped(double width, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var built = CatalogListFixture.Create(width, 700, 0, null, AddTiles);
        try
        {
            using var _ = UiShot.Render(built.Window);
            var list = built.View.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TableListBox");
            list.IsEffectivelyVisible.Should().BeTrue();

            var stocks = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tbl-stock") && t.IsEffectivelyVisible).ToList();
            var minus = stocks.Where(t => (t.Text ?? "").Contains('−')).ToList();
            minus.Should().HaveCount(2, "два товара в минусе: штучный и весовой");
            foreach (var t in minus)
            {
                t.Classes.Contains("stock-minus").Should().BeTrue($"«{t.Text}» — особый стиль минуса");
                t.Text.Should().StartWith(StockLabel.MinusWord);
                FitsWithoutEllipsis(t).Should().BeTrue($"«{t.Text}» не обрезается ({width}, {lang}); ширина {t.Bounds.Width:0}");
            }
            var zero = stocks.Single(t => (t.Text ?? "").StartsWith("0 ") || (t.Text ?? "").StartsWith("0 "));
            zero.Classes.Contains("stock-minus").Should().BeFalse("обычный «0 шт.» — без особого стиля минуса");
            zero.Classes.Contains("stock-out").Should().BeTrue();
        }
        finally
        {
            built.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Tile_MinusStock_IsSpecial_AndNotClipped(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var built = CatalogListFixture.Create(1400, 800, 0, null, AddTiles);
        try
        {
            built.Vm.SetViewMode(CatalogViewMode.Cards);
            using var _ = UiShot.Render(built.Window);
            var cards = built.View.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "CardsListBox");
            cards.IsEffectivelyVisible.Should().BeTrue("широкое окно — плитки, не автосписок");

            var stocks = cards.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("stock-minus") && t.IsEffectivelyVisible).ToList();
            stocks.Should().HaveCount(2, "два товара в минусе");
            foreach (var t in stocks)
            {
                t.Text.Should().StartWith(StockLabel.MinusWord).And.Contain("−");
                FitsWithoutEllipsis(t).Should().BeTrue($"«{t.Text}» не обрезается в плитке ({lang}); ширина {t.Bounds.Width:0}");
                // Плитка ~165 px: подпись не шире плитки.
                var tile = t.GetVisualAncestors().OfType<Button>().FirstOrDefault();
                if (tile is not null)
                    t.Bounds.Width.Should().BeLessThanOrEqualTo(tile.Bounds.Width + 0.5);
            }
        }
        finally
        {
            built.Dispose();
        }
    }
}
