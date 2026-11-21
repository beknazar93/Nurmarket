using Avalonia;
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

/// <summary>2026-11-20 (С7): окно «Данные изменились на другом устройстве» (обновить / перезаписать / отмена): три кнопки, текст не обрезан на 1024×600 и 1366×768 (ru, ky, uz),
/// выбор возвращается. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\product-conflict-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class ProductConflictDialogUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Dialog_ShowsThreeChoices_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var diffs = new[]
        {
            new ProductConflict.Diff("price", "100", "130"),
            new ProductConflict.Diff("purchase_price", "80", "90"),
            new ProductConflict.Diff("attributes", "[{\"key\":\"oem\",\"value\":\"1\"}]", "[]"),
        };
        var window = new ProductConflictDialog("Фильтр масляный Mann W 811/80 длинное название товара для проверки переноса строки", diffs);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.Bounds.Height > 0).ToList();
            buttons.Should().HaveCount(4, $"обновить / перезаписать / отмена + ✕ оболочки ({tag})");
            foreach (var btn in buttons)
            {
                var p = btn.TranslatePoint(default, window)!.Value;
                (p.Y + btn.Bounds.Height).Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» внутри окна ({tag})");
                (p.X + btn.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» не выходит по ширине ({tag})");
            }
            UiDrive.AllText(window).Should().Contain("100 → 130");

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru";
            var clipped = TextClipChecker.Find(window, "product-conflict", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"product-conflict-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_ButtonsReturnChoice()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new ProductConflictDialog("Фильтр", [new ProductConflict.Diff("price", "1", "2")]);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(4);
            var overwrite = window.GetVisualDescendants().OfType<Button>().First(b => UiDrive.TextOf(b) == "Перезаписать");
            overwrite.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.Choice.Should().Be(ConflictChoice.Overwrite);
        }
        finally
        {
            UiShot.Pump();
        }
    }
}
