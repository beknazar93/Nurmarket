using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Вид плашки: цвет полоски и фон (2026-11-01, Д-10).</summary>
public enum ToastKind { Success, Info, Warning, Error }

/// <summary>2026-10-27, аудит владельца С-11: после каждого «Сохранить» товара, «Провести» остатка и т. п. открывалось модальное окно
/// «Товар сохранён.» с кнопкой «Понятно» — лишний клик на каждое действие. Здесь — лёгкая плашка в правом нижнем углу (не модальная, фокус
/// не забирает), сама исчезает, щелчок по ней закрывает сразу.
/// 2026-11-01 (контрольный аудит, Д-10): это теперь ЕДИНАЯ плашка программы — AvaloniaUserPrompts.ShowToast (41 вызов: «Смена закрыта…»,
/// «Товар добавлен», предупреждения склада и т. д., раньше — модальное окно с кнопкой) и «Приходы в фоне» (BackgroundReceiptsHost) показывают
/// её же. Виды: успех, сообщение, предупреждение, ошибка (цвет полоски и фона — кисти темы); срок — по длине текста (предупреждения и ошибки
/// держатся дольше); до трёх плашек друг над другом (новая — снизу, одинаковый текст не дублируется). Ошибки и вопросы, требующие решения
/// (ShowError / ShowWarning / подтверждения), остаются окном. Безопасно вызывать с любого потока.</summary>
public static class SuccessToast
{
    private const int MaxVisible = 3;
    private static readonly List<(Window Window, string Text, bool Sticky)> Active = new();
    private static bool _hooked;

    /// <summary>2026-11-02 (аудит №2, Д3-07): закрыть все плашки (и остановить их таймеры). Окно плашки — отдельное окно без владельца: пока оно
    /// открыто, программа с режимом «выход по последнему окну» не завершается (залипающая плашка «приход не прошёл» держала процесс после закрытия
    /// кассы, следующий запуск упирался в «касса уже запущена»). Вызывается сама, когда закрылось последнее обычное окно (<see cref="Hook"/>).</summary>
    public static void CloseAll()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CloseAll);
            return;
        }

        foreach (var item in Active.ToList())
            CloseQuietly(item.Window);
    }

    private static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Window.WindowClosedEvent.AddClassHandler<Window>((closing, _) =>
        {
            try
            {
                if (Active.Count == 0 || Active.Any(a => ReferenceEquals(a.Window, closing)))
                    return;
                var others = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                    .Any(w => !ReferenceEquals(w, closing) && w.IsVisible && !Active.Any(a => ReferenceEquals(a.Window, w))) ?? true;
                if (!others)
                    CloseAll();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Плашка: проверка закрытия окон не выполнена: {ex.Message}", "DEBUG");
            }
        });
    }

    public static void Show(string text) => Show(text, ToastKind.Success);

    /// <param name="text">Текст плашки.</param>
    /// <param name="kind">Вид: цвет полоски и фон.</param>
    /// <param name="duration">null — по длине текста: успех/сообщение 3–7 с, предупреждение/ошибка 6–14 с.</param>
    /// <param name="onClick">Что сделать по щелчку (кроме закрытия плашки), например открыть окно «Приходы в фоне».</param>
    /// <param name="sticky">2026-11-02 (приёмка 1.17.73, п. 1): «залипающая» — не гаснет сама, пока её не нажмут (для ошибок, которые нельзя пропустить;
    /// при нехватке места вытесняется последней, а не первой).</param>
    public static void Show(string text, ToastKind kind, TimeSpan? duration = null, Action? onClick = null, bool sticky = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(text, kind, duration, onClick, sticky));
            return;
        }

        try
        {
            Hook();
            // Тот же текст уже на экране — старую убираем, новая встаёт заново (срок отсчитывается сначала), а не дублируется.
            foreach (var same in Active.Where(a => string.Equals(a.Text, text, StringComparison.Ordinal)).ToList())
                CloseQuietly(same.Window);
            while (Active.Count >= MaxVisible)
            {
                var victim = Active.FirstOrDefault(a => !a.Sticky);
                CloseQuietly((victim.Window ?? Active[0].Window));
            }

            var warn = kind is ToastKind.Warning or ToastKind.Error;
            var message = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center };
            var accent = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0) };
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), BorderThickness = new Thickness(1), Margin = new Thickness(8),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { accent, message } },
            };
            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                CanResize = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Focusable = false,
                Content = card,
            };
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("DialogWindowBackground"));
            window.Bind(Window.TransparencyLevelHintProperty, window.GetResourceObservable("DialogTransparencyHint"));
            message.Bind(TextBlock.ForegroundProperty, window.GetResourceObservable("BrushText"));
            card.Bind(Border.BackgroundProperty, window.GetResourceObservable(warn ? "BrushToastWarnBg" : "BrushToastNeutralBg"));
            card.Bind(Border.BorderBrushProperty, window.GetResourceObservable("BrushBorder"));
            accent.Bind(Border.BackgroundProperty, window.GetResourceObservable(kind switch
            {
                ToastKind.Error => "BrushDanger",
                ToastKind.Warning => "BrushWarning",
                ToastKind.Info => "BrushAccentText",
                _ => "BrushSuccess",
            }));
            Avalonia.Automation.AutomationProperties.SetName(window, text);
            // 2026-11-05 (аудит №3, Д4-02): плашку читает экранный диктор без фокуса (живая область): ошибки и предупреждения — сразу, остальное — вежливо.
            var liveSetting = kind is ToastKind.Error or ToastKind.Warning
                ? Avalonia.Automation.AutomationLiveSetting.Assertive
                : Avalonia.Automation.AutomationLiveSetting.Polite;
            Avalonia.Automation.AutomationProperties.SetLiveSetting(window, liveSetting);
            Avalonia.Automation.AutomationProperties.SetLiveSetting(card, liveSetting);
            Avalonia.Automation.AutomationProperties.SetName(message, text);
            Avalonia.Automation.AutomationProperties.SetLiveSetting(message, liveSetting);

            card.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left)
                    return;
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Плашка: действие по щелчку не выполнено: {ex.Message}", "DEBUG");
                }

                CloseQuietly(window);
            };
            // 2026-11-02 (Д3-07): таймер — свой объект: при наведении мыши плашка не гаснет (читать долго), останавливается при закрытии.
            var timer = new DispatcherTimer { Interval = duration ?? DefaultDuration(text, warn) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CloseQuietly(window);
            };
            card.PointerEntered += (_, _) => timer.Stop();
            card.PointerExited += (_, _) =>
            {
                if (sticky)
                    return;
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Start();
            };
            window.Opened += (_, _) => Reposition();
            window.Closed += (_, _) =>
            {
                timer.Stop();
                Active.RemoveAll(a => ReferenceEquals(a.Window, window));
                Reposition();
            };
            Active.Add((window, text, sticky));
            window.Show();
            Reposition();
            if (!sticky)
                timer.Start();
        }
        catch (Exception ex)
        {
            // Плашка не показалась — действие уже выполнено; в журнал, без окна.
            PosLogger.Log($"Плашка не показана: {ex.Message}", "WARNING");
        }
    }

    private static TimeSpan DefaultDuration(string text, bool warn)
    {
        var seconds = 2.5 + text.Length / 18.0;
        return TimeSpan.FromSeconds(warn ? Math.Clamp(seconds + 2, 6, 14) : Math.Clamp(seconds, 3, 7));
    }

    private static void CloseQuietly(Window window)
    {
        Active.RemoveAll(a => ReferenceEquals(a.Window, window));
        try
        {
            window.Close();
        }
        catch
        {
            // окно уже закрыто
        }
    }

    /// <summary>Плашки — в правом нижнем углу рабочей области столбиком снизу вверх (самая новая — внизу).</summary>
    private static void Reposition()
    {
        try
        {
            if (Active.Count == 0)
                return;
            var first = Active[^1].Window;
            var screen = first.Screens.ScreenFromWindow(first) ?? first.Screens.Primary;
            if (screen is null)
                return;
            var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var area = screen.WorkingArea;
            var bottom = area.Bottom - (int)(12 * scale);
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var w = Active[i].Window;
                var width = (int)Math.Ceiling((w.Bounds.Width > 0 ? w.Bounds.Width : 400) * scale);
                var height = (int)Math.Ceiling((w.Bounds.Height > 0 ? w.Bounds.Height : 70) * scale);
                w.Position = new PixelPoint(area.Right - width - (int)(12 * scale), bottom - height);
                bottom -= height + (int)(4 * scale);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Плашка не поставлена в угол: {ex.Message}", "DEBUG");
        }
    }
}
