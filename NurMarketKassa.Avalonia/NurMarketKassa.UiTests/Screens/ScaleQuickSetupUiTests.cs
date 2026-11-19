using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (пакеты Д-7 и Н-3): окна «Настройки весов» четырёх марок (Rongta, TM-30F, Штрих-ПРИНТ, Штрих РС-200) — одна раскладка:
/// первая вкладка «Быстрая настройка» в три шага простыми словами (какие весы и где → «Проверить связь» → «Отправить товары» и «Проверить
/// этикетку»), «Штрих-код» третьей, технические поля — в последней вкладке «Для мастера»; вкладок не больше пяти, акцентная кнопка на первой
/// вкладке одна. Текст не обрезан на 1024×600 и 1366×768 (ru, ky; светлая и тёмная тема).
/// На весы тест ничего не шлёт: адреса весов пустые, кнопки не нажимаются (окно Штрих-ПРИНТ при пустом адресе при открытии молчит).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\scale-quick-{марка}-tab{номер}-{тема}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class ScaleQuickSetupUiTests
{
    private static readonly string[] Forbidden = ["порт", "TCP", "UDP", ".txp", "ping", "PLU №"];

    public static IEnumerable<object[]> Matrix() =>
        from brand in new[] { "rongta", "tm", "shtrikh", "shtrikhpc" }
        from size in new (int W, int H)[] { (1024, 600), (1366, 768) }
        from theme in new[] { UiTheme.Light, UiTheme.Dark }
        from lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }
        select new object[] { brand, size.W, size.H, theme, lang };

    private static Window Create(string brand) => brand switch
    {
        "rongta" => new RongtaScaleSettingsWindow(),
        "tm" => new TmScaleSettingsWindow(),
        "shtrikhpc" => new ShtrikhPcSettingsWindow(),
        _ => new ShtrikhScaleSettingsWindow(),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task QuickSetupFirst_MasterLast_FitsScreen(string brand, int width, int height, UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var prefs = UserPreferences.Instance;
        // Адреса пустые — ни одно окно при открытии и съёмке не обращается к весам.
        prefs.RongtaScaleIp = "";
        prefs.TmScaleIp = "";
        prefs.ScaleNetworkIp = "";
        var window = Create(brand);
        var failures = new List<string>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var tag = $"{brand} {width}x{height} {theme} {lang}";
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var count = tabs.ItemCount;
            for (var i = 0; i < count; i++)
            {
                tabs.SelectedIndex = i;
                UiShot.Settle(8);
                using (var shot = UiShot.Render(window))
                using (var image = SKImage.FromBitmap(shot))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 90))
                {
                    var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"scale-quick-{brand}-tab{i}-{theme.ToString().ToLowerInvariant()}-{langCode}-{width}x{height}.png"), data.ToArray());
                }

                var clipped = TextClipChecker.Find(window, $"scale-quick-{brand}-tab{i}", langCode);
                if (clipped.Count > 0)
                    failures.Add($"текст обрезан ({tag}, вкладка {i}): " + string.Join("; ", clipped.Select(v => v.Detail)));

                if (i == 0)
                {
                    var text = UiDrive.AllText(window);
                    foreach (var word in Forbidden)
                        if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                            failures.Add($"на первом экране техническое слово «{word}» ({tag})");
                    foreach (var step in new[] { "1.", "2.", "3." })
                        if (!text.Contains(step, StringComparison.Ordinal))
                            failures.Add($"нет шага «{step}» ({tag})");
                    var primary = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("nur-primary"));
                    if (primary != 1)
                        failures.Add($"акцентных кнопок на первой вкладке {primary}, нужна одна ({tag})");
                }
            }

            if (count is < 4 or > 5)
                failures.Add($"вкладок {count}, нужно 4–5 ({tag})");
            failures.Should().BeEmpty(string.Join(" | ", failures));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>«Штрих-код» — третья вкладка у всех окон: на её номер завязаны кнопки «Формат штрих-кода на самих весах…» и окно «Весы».</summary>
    [AvaloniaTheory]
    [InlineData("rongta")]
    [InlineData("tm")]
    [InlineData("shtrikh")]
    [InlineData("shtrikhpc")]
    public async Task BarcodeTab_IsThird(string brand)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.RongtaScaleIp = "";
        prefs.TmScaleIp = "";
        prefs.ScaleNetworkIp = "";
        var window = Create(brand);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            switch (window)
            {
                case RongtaScaleSettingsWindow w: w.ShowBarcodeTab(); break;
                case TmScaleSettingsWindow w: w.ShowBarcodeTab(); break;
                case ShtrikhPcSettingsWindow w: w.ShowBarcodeTab(); break;
                case ShtrikhScaleSettingsWindow w: w.ShowBarcodeTabFor("Сыр", 123, 250m); break;
            }
            UiShot.Settle(8);
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.SelectedIndex.Should().Be(2, "«Штрих-код» — третья вкладка");
            ((tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "").Should().Contain("Штрих-код");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
