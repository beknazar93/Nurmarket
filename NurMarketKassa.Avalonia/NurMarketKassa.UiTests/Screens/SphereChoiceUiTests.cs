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

/// <summary>2026-11-20, решение владельца: окно «Какой у вас магазин?» — девять плиток (Продуктовый, Хозмаг, Строймаг, Магазин одежды, Магазин электроники, Базар, затем «Другие»:
/// Автозапчасти, Канцтовары, Услуги) на 1024×600 и 1366×768 (ru, ky, uz): порядок, текст не обрезан, окно не выше экрана, плитки прокручиваются, кнопки внутри окна.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\sphere-choice-*.png (эталоны Snapshots не трогаем).</summary>
[Collection("UiScenarios")]
public sealed class SphereChoiceUiTests : IDisposable
{
    public SphereChoiceUiTests() => TestHost.Ensure();

    public void Dispose() => ModalShell.WorkingAreaForTests = null;

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Wizard_shows_nine_tiles_in_order_fits_screen_and_scrolls(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketSphereChoiceWindow();
        try
        {
            using var shot = UiShot.Render(window);
            var expected = SphereCatalog.ChoiceList().Select(p => p.Title.Get()).ToList();
            expected.Should().HaveCount(9);

            // Плитки — настоящие кнопки с названием сферы; порядок сверху вниз как в каталоге.
            var tiles = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Content is Grid && Avalonia.Automation.AutomationProperties.GetName(b) is { Length: > 0 })
                .Select(b => (Name: Avalonia.Automation.AutomationProperties.GetName(b)!, Top: b.TranslatePoint(new Point(0, 0), window)!.Value.Y))
                .OrderBy(t => t.Top).Select(t => t.Name).ToList();
            tiles.Should().Equal(expected, $"порядок плиток ({width}x{height} {lang})");

            window.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({width}x{height} {lang})");
            window.Bounds.Width.Should().BeLessThanOrEqualTo(width);
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
                right.Should().BeLessThanOrEqualTo(window.Bounds.Width + 1, $"кнопка внутри окна ({width}x{height} {lang})");
            }

            // Девять плиток не влезают в 600 px — внутри окна есть прокрутка, и последняя плитка достижима.
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height + 1);
            if (height <= 600)
            {
                scroll.Should().NotBeNull("плитки прокручиваются на 1024x600");
                scroll!.ScrollToEnd();
                UiShot.Pump();
                scroll.Offset.Y.Should().BeGreaterThan(0);
            }

            var texts = UiDrive.AllText(window);
            texts.Should().Contain(Tr.T("Другие", "Башкалар", "Other", "Diğer", "Boshqalar"));
            TextClipChecker.Find(window, "sphere-choice", Code(lang)).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");

            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"sphere-choice-{Code(lang)}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
