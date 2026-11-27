using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services; // 2026-11-27: AppLanguage (сборка UiTests падала CS0246)
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (Нур-советник: доступ к разделам, правило «е» — прозрачность): под ответом советника строка «Использовано: …» (какие разделы смотрел ИИ).
/// 1024×600, ru/ky/uz: строка видна, переносится, не выходит за окно и пузырь ответа и не налезает на соседние элементы.</summary>
[Collection("UiScenarios")]
public sealed class AdvisorToolsUsedLineUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { 1024, 600, l });

    private static object? Invoke(AiAdvisorWindow window, string method, params object?[] args) =>
        typeof(AiAdvisorWindow).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, args);

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Used_line_under_answer_fits_without_overlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new AiAdvisorWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var tag = $"{width}x{height} {lang}";
            var body = (SelectableTextBlock)Invoke(window, "AddBubble", "Выручка за неделю — 152 400 сом, это на 12 % больше прошлой недели. Больше всего продали напитков.", false)!;
            var used = new[] { "Продажи и финансы · 7 дней", "Телеграм-бот → Отчёт торга", "Маркетплейс (описание)", "Склад → Срок годности", "Ремонт и сервис (открыт)" };
            Invoke(window, "AddToolsUsedLine", body, used);
            UiShot.Settle(6);

            var line = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AdvisorToolsUsed");
            line.IsEffectivelyVisible.Should().BeTrue(tag);
            line.Text.Should().Contain(used[1]);
            line.Bounds.Height.Should().BeGreaterThan(0, tag);
            var origin = line.TranslatePoint(default, window)!.Value;
            (origin.X + line.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"строка не выходит за окно ({tag})");
            origin.X.Should().BeGreaterThanOrEqualTo(0);

            // В пузыре ответа: не шире пузыря и ниже текста ответа, без наложения на соседей.
            var content = (StackPanel)line.Parent!;
            var contentOrigin = content.TranslatePoint(default, window)!.Value;
            (origin.X + line.Bounds.Width).Should().BeLessThanOrEqualTo(contentOrigin.X + content.Bounds.Width + 0.5, $"строка в пределах пузыря ({tag})");
            foreach (var sibling in content.Children.OfType<Control>().Where(c => c != line && c.IsVisible && c.Bounds.Height > 0))
            {
                var overlap = sibling.Bounds.Top < line.Bounds.Bottom - 0.5 && line.Bounds.Top < sibling.Bounds.Bottom - 0.5;
                overlap.Should().BeFalse($"строка «Использовано» не налезает на {sibling.GetType().Name} ({tag})");
            }
            line.Bounds.Top.Should().BeGreaterThanOrEqualTo(body.Bounds.Bottom - 0.5, $"строка — под ответом ({tag})");
            var clipped = TextClipChecker.Find(window, "advisor-tools-used", lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru");
            clipped.Where(v => v.Detail.Contains("AdvisorToolsUsed") || v.Detail.Contains(used[0])).Should().BeEmpty($"текст не обрезан ({tag})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
