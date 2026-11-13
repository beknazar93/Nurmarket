using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (Д-4, оценка UX/UI): окна старого вида переведены в общую рамку и стиль программы. Проверки с настоящими условиями
/// (могут упасть): рамка ModalShell с заголовком и ✕, шрифт не меньше 12,5, «ничего не обрезано», у «Истории смен» вкладки вместо боковой панели,
/// нет горизонтальной прокрутки таблицы на 1024×600, ошибка не наезжает на заголовок таблицы (ru/ky), плитка «Долг» нейтральна без долга.
/// Снимки «после» для глаз: %TEMP%\nurmarket-tests\ui-d4\after-*.png.</summary>
public sealed class OldWindowsD4UiTests
{
    private static string ShotDir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-d4");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static void Save(Window window, string name, UiSize? size)
    {
        using var bmp = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), data.ToArray());
    }

    /// <summary>Окна рамки ModalShell из пакета Д-4 (диалоги — по содержимому).</summary>
    public static IEnumerable<object[]> ShellDialogs()
    {
        foreach (var name in new[] { "services", "stock-transfer", "cash-history", "deferred-carts", "pay-debt", "shift-details" })
        foreach (var lang in UiShot.Languages)
            yield return [name, lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(ShellDialogs))]
    public async Task Dialog_UsesModalShell_WithTitleCloseAndReadableFonts(string screenName, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = ScreenCatalog.Get(screenName).Create();
        try
        {
            using var _ = UiShot.Render(window);
            var shell = window.GetVisualDescendants().OfType<ModalShell>().SingleOrDefault();
            Assert.True(shell != null, $"{screenName}: нет ModalShell (общей рамки модального окна)");
            Assert.False(string.IsNullOrWhiteSpace(shell!.Title), $"{screenName}: у рамки нет заголовка");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("modal-close") && b.IsEffectivelyVisible);

            // Шрифт не меньше 12,5 у всех видимых текстов (значки Segoe MDL2 и пустые пропускаем).
            var small = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FontSize < 12.5
                            && !(t.FontFamily?.Name?.Contains("MDL2", StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(t => $"«{t.Text}» {t.FontSize}").ToList();
            Assert.True(small.Count == 0, $"{screenName}: шрифт меньше 12,5: " + string.Join("; ", small));

            var clipped = TextClipChecker.Find(window, screenName, lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            Assert.True(clipped.Count == 0, $"{screenName}: обрезан текст:\n" + string.Join("\n", clipped.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> ShotMatrix()
    {
        foreach (var name in new[] { "services", "stock-transfer", "cash-history", "deferred-carts", "pay-debt", "shift-details" })
        {
            yield return [name, UiTheme.Light, AppLanguage.Russian, 0, 0];
            yield return [name, UiTheme.Dark, AppLanguage.Kyrgyz, 0, 0];
        }
        foreach (var (w, h) in new[] { (1024, 600), (1366, 768) })
        {
            yield return ["shifts-history", UiTheme.Light, AppLanguage.Russian, w, h];
            yield return ["shifts-history", UiTheme.Dark, AppLanguage.Kyrgyz, w, h];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(ShotMatrix))]
    public async Task Shots_AfterRedesign(string screenName, UiTheme theme, AppLanguage lang, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var window = ScreenCatalog.Get(screenName).Create();
        try
        {
            UiShot.Settle(10, 40);
            Save(window, $"after-{screenName}-{UiShot.Slug(theme, lang)}" + (w > 0 ? $"-{w}x{h}" : ""), w > 0 ? new UiSize(w, h) : null);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> HistoryCases()
    {
        foreach (var lang in UiShot.Languages)
        foreach (var size in new[] { new UiSize(1024, 600), new UiSize(1366, 768) })
            yield return [lang, size.Width, size.Height];
    }

    [AvaloniaTheory]
    [MemberData(nameof(HistoryCases))]
    public async Task ShiftsHistory_TabsInsteadOfSidebar_NoHorizontalScroll_ErrorDoesNotOverlapTitle(AppLanguage lang, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, lang);
        var window = new ShiftsHistoryWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiShot.Settle(15, 40);
            using var __ = UiShot.Render(window);

            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.DoesNotContain(buttons, b => b.Classes.Contains("ShiftSidebarButton"));
            Assert.Equal(2, buttons.Count(b => b.Classes.Contains("sec-seg") && b.IsEffectivelyVisible));

            // Таблица смен: горизонтальной прокрутки нет (полоса скрыта или прокручивать нечего).
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsEffectivelyVisible);
            var hbar = grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            Assert.True(hbar == null || !hbar.IsEffectivelyVisible || hbar.Maximum <= 0.5,
                $"у таблицы смен горизонтальная прокрутка (запас {hbar?.Maximum:0.#} px) на {w}x{h}");

            // Ошибка «сервер недоступен» (в тесте сервер — заглушка) не пересекается ни с одним другим текстом.
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            var offline = texts.FirstOrDefault(t => t.Text!.Contains("NurCRM"));
            Assert.True(offline != null, "заглушка «нет связи» должна показать строку ошибки (иначе проверка ничего не проверяет)");
            var a = new Rect(offline!.TranslatePoint(default, window) ?? default, offline.Bounds.Size);
            foreach (var other in texts.Where(t => t != offline))
            {
                var b = new Rect(other.TranslatePoint(default, window) ?? default, other.Bounds.Size);
                var x = a.Intersect(b);
                Assert.False(x.Width > 2 && x.Height > 2, $"ошибка «{offline.Text}» налезает на «{other.Text}» ({lang}, {w}x{h})");
            }

            var clipped = TextClipChecker.Find(window, "shifts-history", lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            Assert.True(clipped.Count == 0, "обрезан текст:\n" + string.Join("\n", clipped.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ShiftDetails_DebtTile_IsNeutralWithoutDebt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new ShiftDetailsDialog();
        try
        {
            using var _ = UiShot.Render(window);
            var tile = window.FindControl<Border>("DebtTile")!;
            var app = Application.Current!;
            app.TryFindResource("BrushDanger", app.ActualThemeVariant, out var danger);
            app.TryFindResource("BrushDangerSoft", app.ActualThemeVariant, out var dangerSoft);
            Assert.NotEqual(((ISolidColorBrush)danger!).Color, ((ISolidColorBrush)tile.BorderBrush!).Color);
            Assert.NotEqual(((ISolidColorBrush)dangerSoft!).Color, ((ISolidColorBrush)tile.Background!).Color);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
