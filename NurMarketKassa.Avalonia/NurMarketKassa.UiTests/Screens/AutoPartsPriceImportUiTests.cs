using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Import;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (ТЗ «Сфера Автозапчасти», пакеты импорта): окно «Массовая загрузка» с прайсом поставщика автозапчастей
/// (шапка из двух строк, строка-группа, строка без цены, остаток диапазоном, CP1251) в сфере «Автозапчасти» на 1024×600 и 1366×768 (ru, ky, uz):
/// профиль включается сам, итог «Будет создано / обновлено / пропущено» виден, текст не обрезан, окно не больше экрана.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\autoparts-price-import-*.png.</summary>
[Collection("UiScenarios")]
public sealed class AutoPartsPriceImportUiTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ap-price-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsPriceImportUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Supplier_price_list_is_recognised_and_the_window_fits(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        prefs.MarketSphere = MarketSpheres.Grocery;
        prefs.MarketSphereDetail = AutoPartsProfile.Id;
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;

        // CP1251, шапка в две строки, группа, строка без цены, остаток диапазоном и «менее 8»
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(_dir, "price-cp1251.csv");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes(
            "Прайс-лист ООО Поставщик от 20.11.2026;;;;\r\n"
            + "Наименование;Каталожный номер;Бренд;Остаток;Цена\r\n"
            + "Фильтры масляные;;;;\r\n"
            + "Фильтр масляный Mann W 811/80;W 811/80;Mann;20–50;450\r\n"
            + "Колодки передние Ferodo FDB 1234;FDB 1234;Ferodo;менее 8;2500\r\n"
            + "Свеча NGK без цены;BKR6E;NGK;5;\r\n"));

        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadPathAsync(path);
            UiShot.Settle(8, 30);
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            window.Plan.NewCount.Should().Be(2, "две позиции с ценой; группа и строка без цены пропущены");
            window.Plan.Rows.All(r => r.Extra is AutoPartsRowData).Should().BeTrue();

            // «Предпросмотр: первые 200 строк» — раскрыть список (строки пропущенных и пометки остатка)
            var previewButton = window.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string ?? "").Contains("200"));
            previewButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(6, 30);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            texts.Should().Contain(t => t.Contains(Tr.T("Будет создано 2, обновлено 0, пропущено 2", "Түзүлөт 2, жаңыртылат 0, өткөрүлөт 2",
                "Will create 2, update 0, skip 2", "Oluşturulacak 2, güncellenecek 0, atlanacak 2", "Yaratiladi 2, yangilanadi 0, o'tkaziladi 2")), $"итог профиля ({width}x{height} {lang})");
            texts.Should().Contain(t => t.Contains("20–50"), "пометка остатка диапазоном в предпросмотре");
            texts.Should().Contain(t => t.Contains(Tr.T("группа", "топ", "group", "grup", "guruh")), "причина пропуска строки-группы");
            texts.Should().Contain(t => t.Contains(Tr.T("нет цены", "баасы жок", "no price", "fiyat yok", "narx yo'q")), "причина пропуска строки без цены");

            window.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5);
            window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5);
            TextClipChecker.Find(window, "autoparts-price-import", Code(lang)).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"autoparts-price-import-{Code(lang)}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-20: двоичный .xls (ExcelDataReader) открывается в мастере так же, как .xlsx: профиль включается сам, итог «Будет создано 2 …» виден, сообщения «сохраните как .xlsx» нет.</summary>
    [AvaloniaFact]
    public async Task Binary_xls_price_list_loads_like_xlsx()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.MarketSphere = MarketSpheres.Grocery;
        prefs.MarketSphereDetail = AutoPartsProfile.Id;
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;

        var path = Path.Combine(_dir, "price.xls");
        File.WriteAllBytes(path, MiniXls.Build("Лист1",
        [
            ["Прайс-лист ООО Поставщик от 20.11.2026"],
            ["Наименование", "Каталожный номер", "Бренд", "Остаток", "Цена"],
            ["Фильтры масляные"],
            ["Фильтр масляный Mann W 811/80", "W 811/80", "Mann", "20–50", 450d],
            ["Колодки передние Ferodo FDB 1234", "FDB 1234", "Ferodo", "менее 8", 2500d],
            ["Свеча NGK без цены", "BKR6E", "NGK", 5d],
        ]));

        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadPathAsync(path);
            UiShot.Settle(8, 30);
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            window.Plan.NewCount.Should().Be(2, "две позиции с ценой; группа и строка без цены пропущены");
            window.Plan.Rows.All(r => r.Extra is AutoPartsRowData).Should().BeTrue();
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
                .Should().NotContain(t => t.Contains("сохраните как .xlsx"), "старый формат больше не отклоняется");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Настоящий демо-каталог docs/Образцы/Автозапчасти/demo-autoparts-catalog.csv (170 позиций, штрихкоды, OEM, кроссы, ячейки): загрузка в окно без сервера.</summary>
    [AvaloniaFact]
    public async Task Demo_catalog_file_loads_170_rows_offline()
    {
        string? path = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && path is null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "Образцы", "Автозапчасти", "demo-autoparts-catalog.csv");
            if (File.Exists(p))
                path = p;
        }
        if (path is null)
            return; // образцов нет в этой копии
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.MarketSphere = MarketSpheres.Grocery;
        prefs.MarketSphereDetail = AutoPartsProfile.Id;
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadPathAsync(path);
            UiShot.Settle(8, 30);
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            window.Plan.NewCount.Should().Be(170);
            window.Plan.ErrorCount.Should().Be(0);
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Should().Contain(t => t.Contains("Будет создано 170, обновлено 0, пропущено 0"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
