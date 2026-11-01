using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-31 (стресс-тест 1.17.68, п. 9): при сбое сервера отчёты молча показывали нули или «продаж нет». Общая плашка
/// «Сервер NurCRM недоступен — данные не получены» с кнопкой «Повторить» (5 языков).</summary>
internal static class ServerOfflineNotice
{
    /// <summary>2026-11-01 (В-08): одна фраза о сбое сервера (ServerOutageMonitor.UnavailableText) вместо «Нет связи с сервером».</summary>
    public static string Text => ServerOutageMonitor.UnavailableText + " — " + Tr.T("данные не получены. Проверьте интернет и повторите.",
        "маалымат алынган жок. Интернетти текшерип, кайталаңыз.", "data was not received. Check the internet and retry.",
        "veri alınamadı. İnterneti kontrol edip tekrar deneyin.", "ma'lumot olinmadi. Internetni tekshirib, qayta urinib ko'ring.");

    public static string RetryText => Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");

    /// <summary>Плашка: текст + «Повторить». retry вызывается по нажатию.</summary>
    public static Control Build(Control host, Action? retry)
    {
        var text = new TextBlock { Text = Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushDanger")); // 2026-11-01 (Н-01): от самого элемента, не от долгоживущего host
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
