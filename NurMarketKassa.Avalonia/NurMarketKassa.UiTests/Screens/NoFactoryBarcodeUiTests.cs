using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (просьба владельца): отметка «Без заводского штрихкода» — галочка в карточке товара (касса и программа владельца — одно окно), пункт в меню «⋯» строки склада
/// и порядок каталога кассы (отмеченный «Хлеб» с обычным 13-значным кодом стоит первым). 1024×600 и 1366×768, ru / ky / uz. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\nofactory-*.png.</summary>
[Collection("UiScenarios")]
public sealed class NoFactoryBarcodeUiTests : IDisposable
{
    private readonly string? _json = UserPreferences.Instance.NoFactoryBarcodeIdsJson;
    private readonly bool _first = UserPreferences.Instance.CatalogNoBarcodeFirst;

    public NoFactoryBarcodeUiTests()
    {
        UserPreferences.Instance.NoFactoryBarcodeIdsJson = null;
        UserPreferences.Instance.CatalogNoBarcodeFirst = true;
        NoFactoryBarcodes.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.NoFactoryBarcodeIdsJson = _json;
        UserPreferences.Instance.CatalogNoBarcodeFirst = _first;
        NoFactoryBarcodes.ResetForTests();
        ModalShell.WorkingAreaForTests = null;
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
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string LabelText() => Tr.T("Без заводского штрихкода (хлеб, выпечка…)", "Заводдук штрих-коду жок (нан, бышырылган азыктар…)", "No factory barcode (bread, baked goods…)",
        "Fabrika barkodu yok (ekmek, unlu mamuller…)", "Zavod shtrixkodi yo'q (non, pishiriqlar…)");

    // ───────────── карточка товара ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ProductCard_HasTheCheckbox_ItFitsAndReflectsTheMark(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var bread = new CatalogProductTileVm("pe-bread", "Хлеб", "40.00 сом", false) { Barcode = "5852439227269", Unit = "шт", Quantity = 5 };
        NoFactoryBarcodes.TrySet(new[] { "pe-bread" }, true);
        var dlg = new ProductEditDialog(null!, bread);
        try
        {
            using var _ = UiShot.Render(dlg);
            UiShot.Settle(8);
            var check = UiDrive.Find<CheckBox>(dlg, "NoFactoryBarcodeCheck");
            check.IsEffectivelyVisible.Should().BeTrue();
            check.IsChecked.Should().BeTrue("товар уже отмечен");
            UiDrive.AllText(dlg).Should().Contain(LabelText());
            var topLeft = check.TranslatePoint(default, dlg)!.Value;
            (topLeft.X + check.Bounds.Width).Should().BeLessThanOrEqualTo(dlg.Bounds.Width + 1, $"галочка не выходит за окно ({width}x{height} {lang})");
            FormFieldsOverlapUiTests.FindViolations(dlg, "no-factory-card").Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"поля и подписи не налезают ({width}x{height} {lang})");
            TextClipChecker.Find(dlg, "no-factory-card", Code(lang)).Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");
            SavePng(dlg, $"nofactory-card-{Code(lang)}-{width}x{height}");

            // снять галочку и «сохранить» (запись отметки — тем же методом, что после ответа сервера)
            check.IsChecked = false;
            typeof(ProductEditDialog).GetMethod("ApplyNoFactoryMark", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dlg, ["pe-bread", null]);
            NoFactoryBarcodes.IsMarked("pe-bread").Should().BeFalse("галочку сняли — отметка снята");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ProductCard_NewProduct_CheckedBox_MarksTheCreatedId()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dlg = new ProductEditDialog();
        try
        {
            using var _ = UiShot.Render(dlg);
            var check = UiDrive.Find<CheckBox>(dlg, "NoFactoryBarcodeCheck");
            check.IsChecked.Should().BeFalse("у нового товара галочки нет");
            check.IsChecked = true;
            typeof(ProductEditDialog).GetMethod("ApplyNoFactoryMark", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dlg, ["pe-new", null]);
            NoFactoryBarcodes.IsMarked("pe-new").Should().BeTrue();
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    // ───────────── склад: меню «⋯» ─────────────

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Warehouse_RowMenu_TogglesTheMark(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var product = new CatalogProductTileVm("wh-bread", "Хлеб", "40.00 сом", false) { Barcode = "5852439227269", Unit = "шт", Quantity = 5 };

            string[] Names() => window.BuildRowMenu(product).Items.OfType<MenuItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i) ?? "").ToArray();
            MenuItem Item() => window.BuildRowMenu(product).Items.OfType<MenuItem>().First(i => (Avalonia.Automation.AutomationProperties.GetName(i) ?? "").Contains(
                Tr.T("заводского", "Заводдук", "factory", "Fabrika", "Zavod"), StringComparison.OrdinalIgnoreCase));

            Names().Should().Contain(n => n.Contains(Tr.T("заводского", "Заводдук", "factory", "Fabrika", "Zavod"), StringComparison.OrdinalIgnoreCase));
            NoFactoryBarcodes.IsMarked("wh-bread").Should().BeFalse();

            Item().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); // «отметить»
            NoFactoryBarcodes.IsMarked("wh-bread").Should().BeTrue();

            Names().Should().Contain(n => !n.Contains('(') && n.Contains(Tr.T("заводского", "Заводдук", "factory", "Fabrika", "Zavod"), StringComparison.OrdinalIgnoreCase),
                "у отмеченного товара пункт предлагает снять отметку");
            Item().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); // «снять»
            NoFactoryBarcodes.IsMarked("wh-bread").Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── каталог кассы ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Catalog_MarkedBreadWithNormalCode_StandsFirst(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var built = CatalogListFixture.Create(width, height, 0, null, items =>
        {
            var bread = new CatalogProductTileVm("nf-bread", "Хлеб", "40.00 сом", false) { Barcode = "5852439227269", Unit = "шт", Quantity = 20 }; // внутренний 13-значный код
            ProductUnitNormalizer.ApplyToTile(bread);
            items.Add(bread);
        });
        try
        {
            built.Window.Show();
            UiShot.Settle(8);
            VisibleOrder(built.Window).First().Title.Should().NotBe("Хлеб", "пока не отмечен — по названию");

            NoFactoryBarcodes.TrySet(new[] { "nf-bread" }, true); // как галочка в карточке или пункт «⋯» склада
            UiShot.Settle(8);
            var order = VisibleOrder(built.Window);
            order.Count.Should().BeGreaterThanOrEqualTo(4);
            order.First().Title.Should().Be("Хлеб", $"отмеченный «Хлеб» первый ({width}x{height} {lang})");
            SavePng(built.Window, $"nofactory-catalog-{Code(lang)}-{width}x{height}-marked");

            NoFactoryBarcodes.TrySet(new[] { "nf-bread" }, false);
            UiShot.Settle(8);
            VisibleOrder(built.Window).First().Title.Should().NotBe("Хлеб", "отметку сняли");
        }
        finally
        {
            built.Dispose();
        }
    }

    private static List<CatalogProductTileVm> VisibleOrder(Window window)
    {
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name is "CardsListBox" or "TableListBox" && l.IsEffectivelyVisible);
        return list.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.DataContext is CatalogProductTileVm)
            .Select(i => (Item: (CatalogProductTileVm)i.DataContext!, P: i.TranslatePoint(default, window) ?? default))
            .OrderBy(x => Math.Round(x.P.Y / 8)).ThenBy(x => x.P.X)
            .Select(x => x.Item)
            .ToList();
    }
}
