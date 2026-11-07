using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-06 (В14): проверка самой проверки — «текст не обрезан» должна находить настоящую обрезку и не ругаться на целый текст.</summary>
public sealed class TextClipCheckerSelfTests
{
    [AvaloniaFact]
    public async Task Checker_FindsTextCutByNarrowButton_AndIgnoresFittingText()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var narrow = new Button { Width = 40, Content = new TextBlock { Text = "Очень длинная подпись кнопки" } };
        var fine = new Button { Content = new TextBlock { Text = "Оплатить" } };
        var wrapped = new TextBlock { Text = "Длинный текст с переносом строк, которому нужно несколько строк высоты", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Width = 120, Height = 14 };
        var window = new Window { Content = new StackPanel { Children = { narrow, fine, wrapped } } };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(400, 300));
            var found = TextClipChecker.Find(window, "self-test", "ru");
            Assert.Contains(found, v => v.Key.Contains("Очень длинная") && v.Key.EndsWith("ширина"));
            Assert.Contains(found, v => v.Key.Contains("Длинный текст") && v.Key.EndsWith("высота"));
            Assert.DoesNotContain(found, v => v.Key.Contains("Оплатить"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
