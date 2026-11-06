using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-11-06 (В11, Д4-30): кнопка «?» вместо абзаца-пояснения мелким серым шрифтом на каждом экране (Отчёты, Зарплата…): абзац нужен один раз, а мешал
/// ежедневно. По нажатию — всплывающее окошко с тем же текстом (читается в момент открытия, поэтому смена языка не ломает). Цвета — из темы (DynamicResource через
/// сам элемент), размер 40×40 как у кнопки «Обновить».</summary>
public static class HintButton
{
    /// <param name="text">Текст пояснения; вызывается при каждом открытии окошка (там же язык и свежие цифры).</param>
    public static Button Create(Func<string?> text)
    {
        var body = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(4) };
        body.Bind(TextBlock.ForegroundProperty, body.GetResourceObservable("BrushText"));
        var flyout = new Flyout { Content = body, Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) => body.Text = text() ?? "";

        var label = Tr.T("Пояснение", "Түшүндүрмө", "Explanation", "Açıklama", "Izoh");
        var button = new Button
        {
            Content = "?",
            Classes = { "sec-outline" },
            Width = 40, Height = 40, MinWidth = 40, MinHeight = 40,
            Padding = new Thickness(0),
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = flyout,
        };
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        return button;
    }
}
