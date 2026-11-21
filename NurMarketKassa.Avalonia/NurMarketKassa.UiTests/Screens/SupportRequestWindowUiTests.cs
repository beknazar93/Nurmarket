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
            // 2026-11-20: данных о часах работы нет — строки скрыты, фразы «уточняется» в окне быть не должно
            texts.Should().NotContain(t => t.Contains("уточняется") || t.Contains("тактала элек"), $"часы работы без данных скрыты ({tag})");

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

    /// <summary>2026-11-20: запись со статусом WhatsApp в «Моих обращениях» — «Отправлено через WhatsApp» и «Открыть WhatsApp ещё раз»,
    /// без «Канал не подключён» и «Повторить»; текст не обрезан на 1024×600 и 1366×768 (ru, ky, uz).</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian, 1024, 600)]
    [InlineData(AppLanguage.Kyrgyz, 1024, 600)]
    [InlineData(AppLanguage.Uzbek, 1024, 600)]
    [InlineData(AppLanguage.Russian, 1366, 768)]
    [InlineData(AppLanguage.Kyrgyz, 1366, 768)]
    [InlineData(AppLanguage.Uzbek, 1366, 768)]
    public async Task WhatsApp_row_has_no_dead_end_texts(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new SupportRequestWindow(null, "меню", null);
        try
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var history = (StackPanel)typeof(SupportRequestWindow).GetField("_history", flags)!.GetValue(window)!;
            var build = typeof(SupportRequestWindow).GetMethod("BuildRow", flags)!;
            var record = new SupportRequestRecord
            {
                Id = "261120-121338", CreatedAt = new DateTime(2026, 11, 20, 12, 13, 0), Description = "не печатает чек",
                Status = SupportRequestRecord.StatusWhatsApp,
            };
            history.Children.Clear();
            history.Children.Add((Control)build.Invoke(window, [record])!);

            using var _ = UiShot.Render(window, new UiSize(width, height));
            var tag = $"{lang} {width}x{height}";
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content?.ToString() ?? "").ToList();
            var sent = lang switch { AppLanguage.Kyrgyz => "WhatsApp аркылуу жөнөтүлдү", AppLanguage.Uzbek => "WhatsApp orqali yuborildi", _ => "Отправлено через WhatsApp" };
            var again = lang switch { AppLanguage.Kyrgyz => "WhatsApp'ты кайра ачуу", AppLanguage.Uzbek => "WhatsApp'ni yana ochish", _ => "Открыть WhatsApp ещё раз" };
            texts.Should().Contain(sent, $"статус WhatsApp ({tag})");
            buttons.Should().Contain(again, $"кнопка «ещё раз» ({tag})");
            texts.Should().NotContain(t => t.Contains("Канал не подключён") || t.Contains("Канал туташкан эмес") || t.Contains("Kanal ulanmagan"), $"тупика нет ({tag})");
            buttons.Should().NotContain(b => b == "Повторить" || b == "Кайталоо" || b == "Qayta urinish", $"«Повторить» для WhatsApp-статуса не нужен ({tag})");
            var clipped = TextClipChecker.Find(window, "support-request-whatsapp", lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru");
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
