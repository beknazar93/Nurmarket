using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (просьба владельца «сначала товары без штрихкода — хлеб и т. д.»): настоящая панель каталога на 1024×600 и 1366×768, ru / ky / uz, плитки и таблица.
/// «Хлеб» без штрихкода стоит первым, переключатель «Сначала товары без штрихкода» выключает правило. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\catalog-nobarcode-*.png.</summary>
[Collection("UiScenarios")]
public sealed class CatalogNoBarcodeFirstUiTests : IDisposable
{
    private readonly bool _before = UserPreferences.Instance.CatalogNoBarcodeFirst;
    private readonly CatalogViewMode _mode = UserPreferences.Instance.CatalogViewMode;

    public void Dispose()
    {
        UserPreferences.Instance.CatalogNoBarcodeFirst = _before;
        UserPreferences.Instance.CatalogViewMode = _mode;
    }

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private static void SavePng(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    public static IEnumerable<object[]> Cases() =>
        new (double W, double H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }
                .SelectMany(l => new[] { CatalogViewMode.Cards, CatalogViewMode.Table }.Select(m => new object[] { s.W, s.H, l, m })));

    /// <summary>Видимые строки списка (плитки / таблица) сверху вниз, слева направо.</summary>
    private static List<CatalogProductTileVm> VisibleOrder(Window window)
    {
        // Окно уже 1280 — панель сама переходит с плиток на список (автосписок), поэтому берём тот список, что сейчас виден.
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name is "CardsListBox" or "TableListBox" && l.IsEffectivelyVisible);
        return list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.DataContext is CatalogProductTileVm)
            .Select(i => (Item: (CatalogProductTileVm)i.DataContext!, P: i.TranslatePoint(default, window) ?? default))
            .OrderBy(x => Math.Round(x.P.Y / 8)).ThenBy(x => x.P.X)
            .Select(x => x.Item)
            .ToList();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Bread_WithoutBarcode_StandsFirst_AndToggleTurnsRuleOff(double width, double height, AppLanguage lang, CatalogViewMode mode)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.CatalogNoBarcodeFirst = true;
        var built = CatalogListFixture.Create(width, height, 0, null, items =>
        {
            var bread = new CatalogProductTileVm("bread", "Хлеб", "40.00 сом", false) { Unit = "шт", Quantity = 20 }; // без штрихкода
            var roll = new CatalogProductTileVm("roll", "Булочка", "25.00 сом", false) { Barcode = "0160", Article = "0160", ProductCode = "0160", Unit = "шт", Quantity = 20 }; // «запасной» код из артикула
            ProductUnitNormalizer.ApplyToTile(bread);
            ProductUnitNormalizer.ApplyToTile(roll);
            items.Add(bread);
            items.Add(roll);
        });
        try
        {
            built.Vm.SetViewMode(mode);
            built.Window.Show();
            UiShot.Settle(8);
            var tag = $"({width}x{height} {Code(lang)} {mode})";

            var order = VisibleOrder(built.Window);
            order.Should().HaveCountGreaterThanOrEqualTo(4, tag);
            order.Take(2).Select(p => p.Title).Should().Equal(new[] { "Булочка", "Хлеб" }, $"сначала товары без штрихкода, по названию {tag}");
            order.Skip(2).Should().OnlyContain(p => !p.HasNoFactoryBarcode, tag);
            SavePng(built.Window, $"catalog-nobarcode-{Code(lang)}-{width:0}x{height:0}-{mode.ToString().ToLowerInvariant()}-on");

            UserPreferences.SetCatalogNoBarcodeFirst(false); // тот же вызов, что у переключателя в «Настройки → Экран»
            UiShot.Settle(8);
            var off = VisibleOrder(built.Window);
            off.First().Title.Should().Be("Абрикос сушёный отборный крупный", $"правило выключено — прежний порядок по названию {tag}");
            SavePng(built.Window, $"catalog-nobarcode-{Code(lang)}-{width:0}x{height:0}-{mode.ToString().ToLowerInvariant()}-off");
        }
        finally
        {
            built.Dispose();
        }
    }
}
