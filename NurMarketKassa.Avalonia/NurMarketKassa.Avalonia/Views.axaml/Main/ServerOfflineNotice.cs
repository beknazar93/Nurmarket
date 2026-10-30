using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-31 (стресс-тест 1.17.68, п. 9): при сбое сервера отчёты молча показывали нули или «продаж нет». Общая плашка
/// «Нет связи с сервером — данные не получены» с кнопкой «Повторить» (5 языков).</summary>
internal static class ServerOfflineNotice
{
    public static string Text => Tr.T("Нет связи с сервером — данные не получены.", "Сервер менен байланыш жок — маалымат алынган жок.",
        "No connection to the server — data was not received.", "Sunucuyla bağlantı yok — veri alınamadı.", "Server bilan aloqa yo'q — ma'lumot olinmadi.");

    public static string RetryText => Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");

    /// <summary>Плашка: текст + «Повторить». retry вызывается по нажатию.</summary>
    public static Control Build(Control host, Action? retry)
    {
        var text = new TextBlock { Text = Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushDanger"));
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4) };
        panel.Children.Add(text);
        if (retry != null)
        {
            var button = UiKit.Ghost(host, RetryText);
            button.Click += (_, _) => retry();
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { button } });
        }
        return panel;
    }
}
