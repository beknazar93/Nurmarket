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

/// <summary>2026-11-13: окно «Настройки весов Масса-К» (серии R и SL) — первая вкладка «Быстрая настройка» в три шага простыми словами (общий блок ScaleUi.QuickSetup), «Штрих-код» третья, технические
/// поля и отметка «проверено на живых весах» — в последней вкладке «Для мастера»; на первом экране нет слов «порт», «TCP»; одна акцентная кнопка; ВП/ВПМ названы «пока не поддерживается»;
/// текст не обрезан на 1024×600 и 1366×768 (ru, ky). Адрес весов пустой, кнопки не нажимаются — в сеть окно ничего не шлёт.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\massak-settings-tab{номер}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class MassaKScaleSettingsUiTests
{
    private static readonly string[] Forbidden = ["порт", "TCP", "UDP", "ping", "PLU №"];

    public static IEnumerable<object[]> Matrix() =>
        from size in new (int W, int H)[] { (1024, 600), (1366, 768) }
        from lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }
        select new object[] { size.W, size.H, lang };

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task QuickSetupFirst_MasterLast_FitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.MassaKScaleIp = "";
        var window = new MassaKScaleSettingsWindow();
        var failures = new List<string>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.ItemCount.Should().Be(4, "Быстрая настройка / Загрузка товаров / Штрих-код / Для мастера");
            for (var i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                UiShot.Settle(8);
                using (var shot = UiShot.Render(window))
                using (var image = SKImage.FromBitmap(shot))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 90))
                {
                    var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"massak-settings-tab{i}-{langCode}-{width}x{height}.png"), data.ToArray());
                }

                var clipped = TextClipChecker.Find(window, $"massak-settings-tab{i}", langCode);
                if (clipped.Count > 0)
                    failures.Add($"текст обрезан ({width}x{height} {lang}, вкладка {i}): " + string.Join("; ", clipped.Select(v => v.Detail)));

                if (i == 0)
                {
                    var text = UiDrive.AllText(window);
                    foreach (var word in Forbidden)
                        if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                            failures.Add($"на первом экране техническое слово «{word}» ({width}x{height} {lang})");
                    foreach (var step in new[] { "1.", "2.", "3." })
                        if (!text.Contains(step, StringComparison.Ordinal))
                            failures.Add($"нет шага «{step}» ({width}x{height} {lang})");
                    if (!text.Contains(lang == AppLanguage.Kyrgyz ? "азырынча колдоого алынбайт" : "пока не поддерживается", StringComparison.Ordinal))
                        failures.Add($"не сказано, что ВП/ВПМ пока не поддерживаются ({width}x{height} {lang})");
                    var primary = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("nur-primary"));
                    if (primary != 1)
                        failures.Add($"акцентных кнопок на первой вкладке {primary}, нужна одна ({width}x{height} {lang})");
                }
            }
            failures.Should().BeEmpty(string.Join(" | ", failures));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>«Штрих-код» — третья вкладка (на её номер завязана кнопка «Проверить этикетку»); отметка «проверено» включает «Больше не спрашивать» только после себя.</summary>
    [AvaloniaFact]
    public async Task BarcodeTab_IsThird_AndSkipConfirmNeedsVerified()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.MassaKScaleIp = "";
        prefs.MassaKVerified = false;
        prefs.MassaKSkipConfirm = false;
        var window = new MassaKScaleSettingsWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.ShowBarcodeTab();
            UiShot.Settle(8);
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.SelectedIndex.Should().Be(2);
            ((tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "").Should().Contain("Штрих-код");

            tabs.SelectedIndex = 3;
            UiShot.Settle(8);
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            var verified = boxes.First(c => UiDrive.TextOf(c)?.Contains("Проверено на весах") == true);
            var skip = boxes.First(c => UiDrive.TextOf(c)?.Contains("Больше не спрашивать") == true);
            skip.IsEnabled.Should().BeFalse("пока не проверено на весах, вопрос обязателен");
            verified.IsChecked = true;
            UiShot.Settle(4);
            skip.IsEnabled.Should().BeTrue("после отметки «проверено» вопрос можно отключить");
            verified.IsChecked = false;
            UiShot.Settle(4);
            skip.IsEnabled.Should().BeFalse();
            skip.IsChecked.Should().NotBe(true);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            prefs.MassaKVerified = false;
            prefs.MassaKSkipConfirm = false;
        }
    }
}
