using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13: окно «Настройки весов CAS» (CL5000J/CL3000 по сети, остальные CL — файл для CL-Works): первая вкладка «Быстрая настройка» в три шага, «Штрих-код» третья,
/// вкладок 4–5, акцентная кнопка на первой вкладке одна (nur-primary), текст не обрезан на 1024×600 и 1366×768 (ru, ky; светлая и тёмная тема), для обеих групп моделей.
/// На весы тест ничего не шлёт: адрес пустой, кнопки не нажимаются. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\cas-{модель}-tab{номер}-{тема}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class CasScaleSettingsUiTests
{
    private static readonly string[] Forbidden = ["порт", "TCP", "UDP", ".txp", "ping", "PLU №"];

    public static IEnumerable<object[]> Matrix() =>
        from model in new[] { CasScaleSettings.ModelCl5000J, CasScaleSettings.ModelCl5500 }
        from size in new (int W, int H)[] { (1024, 600), (1366, 768) }
        from theme in new[] { UiTheme.Light, UiTheme.Dark }
        from lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }
        select new object[] { model, size.W, size.H, theme, lang };

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task QuickSetupFirst_BarcodeThird_FitsScreen(string model, int width, int height, UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        CasScaleSettings.Save(new CasScaleSettings { Ip = "", Model = model });
        var window = new CasScaleSettingsWindow();
        var failures = new List<string>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var tag = $"{model} {width}x{height} {theme} {lang}";
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
                    File.WriteAllBytes(Path.Combine(dir, $"cas-{model}-tab{i}-{theme.ToString().ToLowerInvariant()}-{langCode}-{width}x{height}.png"), data.ToArray());
                }

                var clipped = TextClipChecker.Find(window, $"cas-{model}-tab{i}", langCode);
                if (clipped.Count > 0)
                    failures.Add($"текст обрезан ({tag}, вкладка {i}): " + string.Join("; ", clipped.Select(v => v.Detail)));

                if (i == 0)
                {
                    var text = UiDrive.AllText(window);
                    foreach (var word in Forbidden)
                        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}])" + System.Text.RegularExpressions.Regex.Escape(word), System.Text.RegularExpressions.RegexOptions.IgnoreCase)) // 2026-11-19: слово целиком — «Импорт» (CL-Works) не «порт"
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

    [AvaloniaFact]
    public async Task BarcodeTab_IsThird_AndShowBarcodeTabSelectsIt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CasScaleSettings.Save(new CasScaleSettings { Ip = "", Model = CasScaleSettings.ModelCl5000J });
        var window = new CasScaleSettingsWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.ShowBarcodeTab();
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
