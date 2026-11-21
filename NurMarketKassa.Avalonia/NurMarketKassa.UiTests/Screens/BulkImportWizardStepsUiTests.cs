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

/// <summary>2026-11-20 (жалоба владельца «неудобная массовая загрузка товаров из другой программы»): пошаговый мастер «Массовая загрузка».
/// Шаг 1 «Откуда» — плитки программ без прокрутки; шаг 2 «Файл» — выбор демо-файла (docs/Образцы/Автозапчасти, локально, без сервера); шаг 3 «Проверьте» —
/// итог крупно и таблица не меньше 45 % высоты окна; шаг 4 «Загрузка» — экран хода и итога (сервер заменён заглушкой: показывается без записи).
/// 1024×600 и 1366×768, ru/ky/uz; наложение полей и подписей, обрезка текста; снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\bulk-wizard-step{N}-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BulkImportWizardStepsUiTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;

    public BulkImportWizardStepsUiTests() => TestHost.Ensure();

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

    private static string? DemoCatalog()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "Образцы", "Автозапчасти", "demo-autoparts-catalog.csv");
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
    public async Task Wizard_runs_through_four_steps_fits_and_does_not_overlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        prefs.MarketSphere = MarketSpheres.Grocery;
        prefs.MarketSphereDetail = AutoPartsProfile.Id;
        BulkImportWizardWindow.RefreshCatalogBeforeCheck = false;
        var demo = DemoCatalog();
        if (demo is null)
            return; // образцов нет в этой копии
        var size = $"{width}x{height}";
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8, 30);

            // ШАГ 1: плитки программ, без прокрутки; не меньше 6 плиток (1С, МойСклад, Poster, Loyverse, Paloma365, Umag, Excel, прайс, клиенты, старая загрузка)
            Shot(window, $"bulk-wizard-step1-{Code(lang)}-{size}.png");
            Get<int>(window, "_step").Should().Be(1);
            var pageSource = Get<Control>(window, "_pageSource");
            pageSource.IsVisible.Should().BeTrue();
            var tiles = pageSource.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("modal-secondary") && b.MinHeight >= 72).ToList();
            tiles.Count.Should().BeGreaterThanOrEqualTo(6, "плитки программ");
            var scroll = pageSource.GetVisualDescendants().OfType<ScrollViewer>().First();
            (scroll.Extent.Height - scroll.Viewport.Height).Should().BeLessThanOrEqualTo(2, $"шаг 1 без прокрутки ({size} {lang}; extent {scroll.Extent.Height:0}, окно {scroll.Viewport.Height:0})");
            tiles.All(t => t.Bounds.Width > 120 && t.Bounds.Height > 60).Should().BeTrue("плитки не схлопнуты");
            Clean(window, "bulk-wizard-step1", size, lang);

            // ШАГ 2: выбор файла (путь «из окна»: остаёмся на шаге «Файл»), сведения о файле
            await window.LoadPathAsync(demo, advanceToReview: false);
            UiShot.Settle(8, 30);
            Get<int>(window, "_step").Should().Be(2);
            window.Plan.Should().NotBeNull();
            window.Plan!.Problem.Should().BeNull();
            Get<Border>(window, "_fileCard").IsVisible.Should().BeTrue("карточка выбранного файла");
            Get<TextBlock>(window, "_fileTitle").Text.Should().Be("demo-autoparts-catalog.csv");
            Get<Button>(window, "_nextBtn").IsEnabled.Should().BeTrue("«Далее» доступно после разбора файла");
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Should().Contain(t => t.Contains("170"), "число строк файла");
            Shot(window, $"bulk-wizard-step2-{Code(lang)}-{size}.png");
            Clean(window, "bulk-wizard-step2", size, lang);

            // ШАГ 3: итог крупно, таблица не меньше 45 % высоты окна
            Go(window, 3);
            UiShot.Settle(8, 30);
            window.Plan!.NewCount.Should().Be(170);
            var grid = Get<DataGrid>(window, "_grid");
            grid.IsVisible.Should().BeTrue();
            grid.Bounds.Height.Should().BeGreaterThanOrEqualTo(height * 0.45, $"таблица ≥ 45 % высоты окна ({size} {lang}; таблица {grid.Bounds.Height:0})");
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
                .Should().Contain(t => t.Contains("170") && t.Contains("0"), "итог крупно");
            Get<Button>(window, "_loadBtn").IsVisible.Should().BeTrue("главная кнопка внизу видна");
            var load = Get<Button>(window, "_loadBtn");
            load.TranslatePoint(new Point(0, load.Bounds.Height), window)!.Value.Y.Should().BeLessThanOrEqualTo(height + 0.5, "нижняя панель кнопок закреплена и видна");
            Shot(window, $"bulk-wizard-step3-{Code(lang)}-{size}.png");
            Clean(window, "bulk-wizard-step3", size, lang);

            // «Дополнительно» открывается отдельной полосой и не выдавливает таблицу по высоте
            var heightBefore = grid.Bounds.Height;
            Get<Button>(window, "_mapToggle").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8, 30);
            Get<ScrollViewer>(window, "_advCol").IsVisible.Should().BeTrue();
            grid.Bounds.Height.Should().BeGreaterThanOrEqualTo(heightBefore - 1, "полоса «Дополнительно» не отнимает высоту у таблицы");
            Shot(window, $"bulk-wizard-step3-advanced-{Code(lang)}-{size}.png");
            Clean(window, "bulk-wizard-step3-advanced", size, lang);

            // ШАГ 4: ход и итог (сервера нет — экран показывается на заглушке, запись не идёт)
            Go(window, 4);
            Get<TextBlock>(window, "_status").Text = Tr.T("Загружено 120 из 170 · 38 строк/с · осталось ~2 с", "Жүктөлдү 120 / 170 · 38 сап/сек · калды ~2 сек", "Loaded 120 of 170 · 38 rows/s · ~2 s left", "Yüklendi 120 / 170 · 38 satır/sn · ~2 sn kaldı", "Yuklandi 120 / 170 · 38 qator/s · ~2 s qoldi");
            var bar = Get<ProgressBar>(window, "_bar");
            bar.IsVisible = true;
            bar.Maximum = 170;
            bar.Value = 120;
            UiShot.Settle(8, 30);
            Get<Button>(window, "_warehouseBtn").IsVisible.Should().BeTrue("«Открыть склад» на последнем шаге");
            Shot(window, $"bulk-wizard-step4-{Code(lang)}-{size}.png");
            Clean(window, "bulk-wizard-step4", size, lang);
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            window.Close();
            UiShot.Pump();
        }
    }
}
