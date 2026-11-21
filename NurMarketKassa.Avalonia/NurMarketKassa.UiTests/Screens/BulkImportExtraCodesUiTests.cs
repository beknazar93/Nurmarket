using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (просьба владельца: доп. штрихкоды и «пачка/штука» в массовой загрузке): шаг «Проверьте» с образцом docs/Образцы/bulk-import-extra-barcodes.csv —
/// итог «доп. кодов: N», колонки «Доп. коды» и «Категория» видны, таблица занимает основное место, «Дополнительно» с новыми переключателями не налезает и не обрезается;
/// файл «не про товары» (почта в штрихкоде) — плашка вместо 100 красных строк. 1024×600 и 1366×768, ru/ky/uz; снимки: %TEMP%\nurmarket-tests\ui-actual\bulk-extra-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BulkImportExtraCodesUiTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;

    public BulkImportExtraCodesUiTests() => TestHost.Ensure();

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        ModalShell.WorkingAreaForTests = null;
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static T Get<T>(BulkImportWizardWindow w, string name) => (T)typeof(BulkImportWizardWindow).GetField(name, Flags)!.GetValue(w)!;

    private static void Go(BulkImportWizardWindow w, int step) => typeof(BulkImportWizardWindow).GetMethod("GoToStep", Flags)!.Invoke(w, [step]);

    private static void Shot(Window w, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        using var bmp = UiShot.Render(w);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, file), data.ToArray());
    }

    private static string? Sample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "Образцы", "bulk-import-extra-barcodes.csv");
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    private static void Clean(BulkImportWizardWindow window, string scope, string size, AppLanguage lang)
    {
        var overlaps = FormFieldsOverlapUiTests.FindViolations(window, scope).Select(v => v.Key + " — " + v.Detail).ToList();
        overlaps.Should().BeEmpty($"поля и подписи не налезают друг на друга ({scope} {size} {lang})");
        TextClipChecker.Find(window, scope, Code(lang)).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"текст не обрезан ({scope} {size} {lang})");
        window.Bounds.Height.Should().BeLessThanOrEqualTo(double.Parse(size.Split('x')[1]) + 0.5, $"окно не выше экрана ({scope} {size} {lang})");
        window.Bounds.Width.Should().BeLessThanOrEqualTo(double.Parse(size.Split('x')[0]) + 0.5, $"окно не шире экрана ({scope} {size} {lang})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Review_step_shows_extra_codes_column_and_summary_and_advanced_switches_do_not_overlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var sample = Sample();
        if (sample is null)
            return; // образца нет в этой копии
        var size = $"{width}x{height}";
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadPathAsync(sample, advanceToReview: false);
            UiShot.Settle(8, 30);
            var plan = window.Plan;
            plan.Should().NotBeNull();
            plan!.Problem.Should().BeNull();
            plan.ErrorCount.Should().Be(0);
            plan.AltCodeCount.Should().Be(6, "6 доп. кодов в образце (из них 3 упаковки)");
            plan.AltPackCount.Should().Be(3);

            Go(window, 3);
            UiShot.Settle(8, 30);
            var grid = Get<DataGrid>(window, "_grid");
            grid.IsVisible.Should().BeTrue();
            grid.Bounds.Height.Should().BeGreaterThanOrEqualTo(height * 0.40, $"таблица занимает основное место ({size} {lang}; таблица {grid.Bounds.Height:0})");
            grid.Columns[6].IsVisible.Should().BeTrue("колонка «Доп. коды» видна, когда у строк есть доп. коды");
            grid.Columns[4].IsVisible.Should().BeTrue("колонка «Категория» видна, когда столбец сопоставлен");
            Get<TextBlock>(window, "_bigSummary").Text.Should().Contain("6", "в итоге шага — число доп. кодов");
            Get<Border>(window, "_notProducts").IsVisible.Should().BeFalse("это список товаров — плашки «не список товаров» нет");
            Shot(window, $"bulk-extra-step3-{Code(lang)}-{size}.png");
            Clean(window, "bulk-extra-step3", size, lang);

            Get<Button>(window, "_mapToggle").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8, 30);
            Get<ScrollViewer>(window, "_advCol").IsVisible.Should().BeTrue();
            Get<CheckBox>(window, "_altExistingCheck").IsVisible.Should().BeTrue();
            Get<CheckBox>(window, "_altSkipBadCheck").IsVisible.Should().BeTrue();
            Shot(window, $"bulk-extra-step3-advanced-{Code(lang)}-{size}.png");
            Clean(window, "bulk-extra-step3-advanced", size, lang);
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task File_that_is_not_a_product_list_shows_a_notice_instead_of_red_rows(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var size = $"{width}x{height}";
        var csv = "Название;Штрихкод;Цена в мес, сом\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Фирма {i};owner{i}@mail.test;{1000 + i}"));
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", $"not-products-{Guid.NewGuid():N}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadPathAsync(path, advanceToReview: false);
            UiShot.Settle(8, 30);
            Go(window, 3);
            UiShot.Settle(8, 30);
            var notice = Get<Border>(window, "_notProducts");
            notice.IsVisible.Should().BeTrue("«Похоже, это не список товаров»");
            Get<DataGrid>(window, "_grid").IsVisible.Should().BeFalse("сорок красных строк не показываются основным результатом");
            Get<Button>(window, "_loadBtn").IsEnabled.Should().BeFalse("загрузить как товары нельзя, пока не выбрано действие");
            notice.GetVisualDescendants().OfType<Button>().Count().Should().Be(3, "три действия: клиенты, другой файл, всё равно загрузить");
            Shot(window, $"bulk-extra-notproducts-{Code(lang)}-{size}.png");
            Clean(window, "bulk-extra-notproducts", size, lang);
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            window.Close();
            UiShot.Pump();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
