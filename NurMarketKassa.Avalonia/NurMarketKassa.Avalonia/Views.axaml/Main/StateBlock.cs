using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вид блока состояния.</summary>
public enum StateKind { Loading, Empty, Error, Offline }

/// <summary>2026-11-12 (оценка UX/UI, пакет Д-2): ОДИН блок «загрузка / пусто / ошибка с «Повторить» / сервер недоступен» вместо шести разных способов
/// (красная строка внизу, серая строка в углу, три одинаковых сообщения в «Долгах», сырой ответ сервера в тексте). Блок стоит по центру списка:
/// значок, заголовок, одна причина, «Повторить», а технический текст (тип сбоя, поля ответа сервера) — только под сворачиваемым «Подробнее»
/// для техподдержки и в журнале (его пишет окно: PosLogger). Блок помечен классом <c>state-block</c> и <c>Tag</c> = <see cref="StateKind"/> (для тестов и диктора).
/// Карточка «Сервер NurCRM недоступен» (<see cref="ServerOfflineNotice"/>) — теперь тот же блок (<see cref="Offline"/>).</summary>
public static class StateBlock
{
    public static string RetryText => Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish");

    public static string DetailsText => Tr.T("Подробнее", "Кеңири", "Details", "Ayrıntılar", "Batafsil");

    /// <summary>Какой вариант показать для сбоя: сервер/сеть недоступны — «Offline», всё остальное — «Error».</summary>
    public static StateKind Classify(Exception? ex) => ServerOutageMonitor.IsServerUnavailable(ex) ? StateKind.Offline : StateKind.Error;

    /// <summary>Загрузка: полоса прогресса и подпись (без подписи — «Загружаю…»).</summary>
    public static Control Loading(string? text = null)
    {
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, Width = 220, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock
        {
            Text = text ?? Tr.T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…"),
            FontSize = 13, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Soft(label);
        var panel = new StackPanel { Spacing = 12, Children = { bar, label } };
        return Shell(StateKind.Loading, panel, label.Text);
    }

    /// <summary>Пусто: значок, фраза (или заголовок и подсказка, что делать); без кнопки.</summary>
    public static Control Empty(string title, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Badge("IconSearch", "!", danger: false));
        if (string.IsNullOrWhiteSpace(hint))
            panel.Children.Add(Body(title)); // одна фраза — тоном тела, без крупного заголовка
        else
        {
            panel.Children.Add(Title(title));
            panel.Children.Add(Body(hint!));
        }
        return Shell(StateKind.Empty, panel, string.IsNullOrWhiteSpace(hint) ? title : title + ". " + hint);
    }

    /// <summary>Сбой загрузки. Сервер/сеть недоступны — карточка «Сервер NurCRM недоступен»; иначе заголовок <paramref name="what"/> («Не удалось загрузить долги»)
    /// и одна понятная причина (<paramref name="reason"/> — своя формулировка окна, например «Нет доступа…», иначе общая). Сырой ответ сервера — только в «Подробнее».</summary>
    public static Control Failure(Control host, Exception? ex, string what, Action? retry, string? reason = null)
    {
        if (Classify(ex) == StateKind.Offline)
            return Offline(host, retry, ex);
        return Build(StateKind.Error, host, what, reason ?? ServerOutageMonitor.FriendlyError(ex), retry, TechnicalDetails(ex));
    }

    /// <summary>Сервер NurCRM недоступен: заголовок, пояснение, «Повторить».</summary>
    public static Control Offline(Control host, Action? retry, Exception? ex = null) =>
        Build(StateKind.Offline, host, ServerOutageMonitor.UnavailableText,
            Tr.T("Данные не получены. Проверьте интернет и повторите.", "Маалымат алынган жок. Интернетти текшерип, кайталаңыз.",
                "Data was not received. Check the internet and retry.", "Veri alınamadı. İnterneti kontrol edip tekrar deneyin.",
                "Ma'lumot olinmadi. Internetni tekshirib, qayta urinib ko'ring."),
            retry, TechnicalDetails(ex));

    /// <summary>Общая сборка блока ошибки / «нет связи».</summary>
    public static Control Build(StateKind kind, Control host, string title, string? body, Action? retry, string? details)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Badge("IconWarning", "!", danger: true));
        panel.Children.Add(Title(title));
        if (!string.IsNullOrWhiteSpace(body) && !string.Equals(body!.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
            panel.Children.Add(Body(body!));
        if (retry != null)
        {
            var button = UiKit.Ghost(host, RetryText);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(0, 6, 0, 0);
            button.Click += (_, _) => retry();
            panel.Children.Add(button);
        }
        if (!string.IsNullOrWhiteSpace(details))
        {
            var text = new TextBlock
            {
                Text = details, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxWidth = 460,
                TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Center,
            };
            Soft(text);
            var more = new Button
            {
                Content = DetailsText, FontSize = 12, MinHeight = 32, Padding = new Thickness(10, 0), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, Focusable = true,
            };
            more.Bind(Button.ForegroundProperty, more.GetResourceObservable("BrushTextSoft"));
            more.Click += (_, _) => text.IsVisible = !text.IsVisible;
            panel.Children.Add(more);
            panel.Children.Add(text);
        }
        return Shell(kind, panel, string.IsNullOrWhiteSpace(body) ? title : title + ". " + body);
    }

    /// <summary>Технический текст для техподдержки: тип сбоя, сообщение и поля ответа сервера; не более 600 знаков. null — нечего показать.</summary>
    public static string? TechnicalDetails(Exception? ex)
    {
        if (ex is null)
            return null;
        var text = ex.GetType().Name + ": " + ex.Message;
        try
        {
            var fields = ServerTelegramBotApi.DescribeFields(ex);
            if (!string.IsNullOrWhiteSpace(fields) && !text.Contains(fields, StringComparison.Ordinal))
                text += "\n" + fields;
        }
        catch (Exception)
        {
            // подробности — вспомогательные; сбой разбора не должен ломать показ ошибки
        }
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private static Border Shell(StateKind kind, Control content, string automationName)
    {
        var shell = new Border
        {
            Child = content, Tag = kind, Margin = new Thickness(24, 36), MaxWidth = 520, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        shell.Classes.Add("state-block");
        Avalonia.Automation.AutomationProperties.SetName(shell, automationName);
        return shell;
    }

    private static Control Badge(string iconKey, string fallbackGlyph, bool danger)
    {
        var circle = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), HorizontalAlignment = HorizontalAlignment.Center };
        circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable(danger ? "BrushDangerSoft" : "BrushPanelSoft"));
        var glyph = new TextBlock { Text = fallbackGlyph, FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(TextBlock.ForegroundProperty, glyph.GetResourceObservable(danger ? "BrushDanger" : "BrushTextSoft"));
        if (!danger && Application.Current?.TryFindResource(iconKey, out var geo) == true && geo is Geometry g)
        {
            var icon = new PathIcon { Data = g, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable("BrushTextSoft"));
            circle.Child = icon;
        }
        else
            circle.Child = glyph;
        return circle;
    }

    private static TextBlock Title(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    private static TextBlock Body(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Soft(t);
        return t;
    }

    private static void Soft(TextBlock t) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
}
