using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Л-9): окно «Написать в поддержку» на 1024×600 (ru и ky): поле описания, флажки «журнал» и «снимок», часы работы и срок ответа
/// («уточняется»), «Отправить» и «Закрыть» видны в окне, текст не обрезан. Снимки: %TEMP%\nurmarket-tests\ui-actual\support-request-{язык}-1024x600.png.</summary>
[Collection("UiScenarios")]
public sealed class SupportRequestWindowUiTests
{
    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { l });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Window_fits_1024x600_with_all_parts_visible(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new SupportRequestWindow(null, "меню", null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            var tag = lang.ToString();

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            var pending = lang == AppLanguage.Kyrgyz ? "тактала элек" : "уточняется";
            texts.Should().Contain(t => t.Contains(pending), $"часы работы и срок ответа — заглушка «{pending}» ({tag})");

            window.GetVisualDescendants().OfType<TextBox>().Should().ContainSingle("одно поле описания");
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            boxes.Should().HaveCount(2, "журнал и снимок окна");
            boxes[0].IsChecked.Should().BeTrue("журнал приложен по умолчанию");
            boxes[1].IsChecked.Should().BeFalse("снимок окна — только по желанию");
            boxes[1].IsEnabled.Should().BeFalse("владельца для снимка нет — флажок выключен");

            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("nur-primary")).ToList();
            buttons.Should().ContainSingle("одна главная кнопка — «Отправить»");
            foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Height > 0))
            {
                var bottom = b.TranslatePoint(new Point(0, b.Bounds.Height), window)!.Value.Y;
                bottom.Should().BeLessThanOrEqualTo(600, $"кнопка «{b.Content}» не уходит за нижний край 600 ({tag})");
            }

            var clipped = TextClipChecker.Find(window, "support-request", lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            clipped.Should().BeEmpty($"текст окна не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"support-request-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-1024x600.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
