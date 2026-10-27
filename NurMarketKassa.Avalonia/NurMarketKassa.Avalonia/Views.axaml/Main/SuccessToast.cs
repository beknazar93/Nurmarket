using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-27, аудит владельца С-11: после каждого «Сохранить» товара, «Провести» остатка и т. п. открывалось модальное окно
/// «Товар сохранён.» с кнопкой «Понятно» — лишний клик на каждое действие. Здесь — лёгкая плашка в правом нижнем углу (не модальная, фокус
/// не забирает), сама исчезает через 3 секунды, щелчок по ней закрывает сразу. Для сообщений об УСПЕХЕ; предупреждения и ошибки остаются окном.
/// Одновременно одна плашка: новая заменяет прежнюю. Безопасно вызывать с любого потока.</summary>
public static class SuccessToast
{
    private static Window? _toast;

    public static void Show(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(text));
            return;
        }

        try
        {
            try
            {
                _toast?.Close();
            }
            catch
            {
                // окно уже закрыто
            }

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
            card.Bind(Border.BackgroundProperty, window.GetResourceObservable("BrushToastNeutralBg"));
            card.Bind(Border.BorderBrushProperty, window.GetResourceObservable("BrushBorder"));
            accent.Bind(Border.BackgroundProperty, window.GetResourceObservable("BrushSuccess"));

            void Place()
            {
                try
                {
                    var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
                    if (screen is null)
                        return;
                    var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
                    var area = screen.WorkingArea;
                    var width = (int)Math.Ceiling((window.Bounds.Width > 0 ? window.Bounds.Width : 400) * scale);
                    var height = (int)Math.Ceiling((window.Bounds.Height > 0 ? window.Bounds.Height : 70) * scale);
                    window.Position = new PixelPoint(area.Right - width - (int)(12 * scale), area.Bottom - height - (int)(12 * scale));
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Плашка успеха не поставлена в угол: {ex.Message}", "DEBUG");
                }
            }

            card.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                    window.Close();
            };
            window.Opened += (_, _) => Place();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_toast, window))
                    _toast = null;
            };
            _toast = window;
            window.Show();
            Place();
            DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(_toast, window))
                    window.Close();
            }, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            // Плашка не показалась — действие уже выполнено; в журнал, без окна.
            PosLogger.Log($"Плашка успеха не показана: {ex.Message}", "WARNING");
        }
    }
}
