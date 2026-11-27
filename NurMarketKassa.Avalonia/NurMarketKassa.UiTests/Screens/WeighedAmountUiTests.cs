using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (владелец, фото клиента 24.11): окно «Взвесить», вкладка «По сумме». Ввели 1000 при 1 100 сом/кг — подсказка «≈ 0,909 кг · в чеке: 1 000,00 сом» (сервер принимает точную сумму строки),
/// не обрезана на 1024×600 на ru/ky/uz; без признака сервера — честное «в чеке: 999,90».</summary>
public sealed class WeighedAmountUiTests
{
    public static IEnumerable<object[]> Languages() => [[AppLanguage.Russian], [AppLanguage.Kyrgyz], [AppLanguage.Uzbek]];

    private static string Normalize(string? text) => (text ?? "").Replace('\u00A0', ' ').Replace('\u202F', ' ');

    private static TextBlock OpenAmountPreview(WeighedProductDialog window, string amount)
    {
        window.FindControl<Button>("ByAmountTab")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.FindControl<TextBox>("WeightBox")!.Text = amount;
        UiShot.Settle(6, 30);
        return window.FindControl<TextBlock>("ComputedPreviewText")!;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task ByAmount_Hint_ShowsKgAndExactReceiptSum_AndFitsAt1024(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = WeighedAmount.Enabled;
        WeighedAmount.Enabled = () => true;
        var window = new WeighedProductDialog("Бука лакым", "1 100,00 сом", null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            var preview = OpenAmountPreview(window, "1000");
            Assert.True(preview.IsVisible, "подсказка «≈ кг · в чеке» не показана");
            var text = Normalize(preview.Text);
            Assert.Contains("0,909", text);
            Assert.Contains("1 000,00", text);
            Assert.StartsWith("≈ 0,909", text);
            // не обрезана: целиком внутри окна и без переноса за границу колонки
            var topLeft = preview.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(topLeft.X >= 0 && topLeft.X + preview.Bounds.Width <= window.Bounds.Width + 0.5, $"подсказка выходит за окно ({lang})");
            Assert.True(topLeft.Y + preview.Bounds.Height <= window.Bounds.Height + 0.5, $"подсказка ниже окна ({lang})");
        }
        finally { WeighedAmount.Enabled = saved; window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public async Task ByAmount_WithoutServerFlag_ShowsHonestReceiptSum()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var saved = WeighedAmount.Enabled;
        WeighedAmount.Enabled = () => false;
        var window = new WeighedProductDialog("Бука лакым", "1 100,00 сом", null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            var text = Normalize(OpenAmountPreview(window, "1000").Text);
            Assert.Contains("0,909", text);
            Assert.Contains("999,90", text);
        }
        finally { WeighedAmount.Enabled = saved; window.Close(); UiShot.Pump(); }
    }

    // 2026-11-27 (живой проход): у кнопки «стереть» (только значок) имя для диктора было «Avalonia.Controls.PathIcon».
    [AvaloniaFact]
    public async Task BackspaceButton_HasAccessibleName()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new WeighedProductDialog("Бука лакым", "1 100,00 сом", null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            var name = Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("BackspaceButton")!);
            Assert.Equal("Стереть", name);
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public async Task ByWeight_StillShowsAmountLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new WeighedProductDialog("Бука лакым", "1 100,00 сом", null);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.FindControl<TextBox>("WeightBox")!.Text = "0,5";
            UiShot.Settle(6, 30);
            Assert.Contains("550,00", Normalize(window.FindControl<TextBlock>("ComputedPreviewText")!.Text));
        }
        finally { window.Close(); UiShot.Pump(); }
    }
}
