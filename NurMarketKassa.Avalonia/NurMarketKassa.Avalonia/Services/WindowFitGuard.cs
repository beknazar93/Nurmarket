using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-30, баг владельца (скриншоты: «Закрыть смену» и «Розничный» обрезаны справа, кнопка «закрыть» окна за экраном): окно кассы
/// оказывалось шире рабочей области своего экрана (старый размер/положение с другого монитора, смена масштаба Windows, отключение
/// монитора, проекция экрана покупателя). Страж проверяет главное окно после показа и при смене экрана/масштаба/положения:
/// размер = не больше рабочей области (WorkingArea), положение — внутри неё. Развёрнутое окно, которое ОС сделала больше экрана,
/// возвращается в обычное, подогнанное под экран. Не больше 4 исправлений за 30 с (защита от «борьбы» с другими обработчиками
/// режима окна). Состояние «Полный экран» не меняется, если окно не вылезает за экран.</summary>
public static class WindowFitGuard
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();

    public static void Attach(Window window)
    {
        if (OperatingSystem.IsAndroid() || Attached.TryGetValue(window, out _))
            return;
        Attached.Add(window, new object());

        var fixes = new Queue<DateTime>();
        DispatcherTimer? debounce = null;

        void Schedule(string reason)
        {
            debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            debounce.Stop();
            debounce.Tick -= OnTick;
            debounce.Tick += OnTick;
            pendingReason = reason;
            debounce.Start();
        }

        void OnTick(object? s, EventArgs e)
        {
            debounce?.Stop();
            Check(window, pendingReason, fixes);
        }

        window.Opened += (_, _) => { Schedule("показ окна"); };
        window.ScalingChanged += (_, _) => Schedule("смена масштаба Windows");
        window.PositionChanged += (_, _) => Schedule("перенос окна");
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
                Schedule("смена режима окна");
        };
        var screens = window.Screens;
        if (screens is not null)
            screens.Changed += (_, _) => Schedule("смена мониторов");
        if (window.IsVisible)
            Schedule("окно уже показано");
    }

    private static string pendingReason = "";

    /// <summary>Чистая проверка: окно размером (<paramref name="width"/>, <paramref name="height"/>) px с левым верхним углом в (<paramref name="x"/>, <paramref name="y"/>)
    /// выходит за область <paramref name="area"/> (с допуском <paramref name="tolerance"/> px).</summary>
    internal static bool Overflows(double x, double y, double width, double height, PixelRect area, double tolerance) =>
        x < area.X - tolerance || y < area.Y - tolerance
        || x + width > area.X + area.Width + tolerance || y + height > area.Y + area.Height + tolerance
        || width > area.Width + tolerance || height > area.Height + tolerance;

    private static void Check(Window window, string reason, Queue<DateTime> fixes)
    {
        try
        {
            if (!window.IsVisible || window.WindowState == WindowState.Minimized)
                return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null)
                return;
            var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
            var state = window.WindowState;
            var area = state == WindowState.FullScreen ? screen.Bounds : screen.WorkingArea;
            if (area.Width <= 0 || area.Height <= 0)
                return;

            var client = window.ClientSize;
            var frame = window.FrameSize ?? client;
            bool bad;
            if (state == WindowState.Normal)
                bad = Overflows(window.Position.X, window.Position.Y, frame.Width * scale, frame.Height * scale, area, 3);
            else
                // Развёрнутое окно: допуск 16 px (рамка ОС), позиция не важна — важно, что оно не больше экрана.
                bad = client.Width * scale > area.Width + 16 || client.Height * scale > area.Height + 16;
            if (!bad)
                return;

            var now = DateTime.UtcNow;
            while (fixes.Count > 0 && now - fixes.Peek() > TimeSpan.FromSeconds(30))
                fixes.Dequeue();
            if (fixes.Count >= 4)
                return;
            fixes.Enqueue(now);

            PosLogger.Log(
                $"Окно {window.GetType().Name} вылезает за экран ({reason}): состояние {state}, окно {client.Width * scale:0}x{client.Height * scale:0} px, " +
                $"позиция {window.Position.X},{window.Position.Y}, область {area.X},{area.Y} {area.Width}x{area.Height} px, масштаб {scale * 100:0}% — подгоняю.", "UI");

            // Размеры — в единицах Avalonia без рамки ОС (как в DialogScreenFit.ChromeSize).
            var chrome = frame.Width > client.Width || frame.Height > client.Height
                ? new Size(Math.Min(frame.Width - client.Width, 40), Math.Min(frame.Height - client.Height, 80))
                : default;
            if (state != WindowState.Normal)
                window.WindowState = WindowState.Normal;
            var maxW = Math.Max(320, area.Width / scale - chrome.Width);
            var maxH = Math.Max(240, area.Height / scale - chrome.Height);
            if (window.MinWidth > maxW)
                window.MinWidth = Math.Floor(maxW);
            if (window.MinHeight > maxH)
                window.MinHeight = Math.Floor(maxH);
            window.Width = Math.Min(state == WindowState.Normal ? client.Width : maxW, maxW);
            window.Height = Math.Min(state == WindowState.Normal ? client.Height : maxH, maxH);
            var wPx = (window.Width + chrome.Width) * scale;
            var hPx = (window.Height + chrome.Height) * scale;
            var px = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.X + area.Width - (int)wPx));
            var py = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - (int)hPx));
            window.Position = new PixelPoint(px, py);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно {window.GetType().Name}: проверка размера по экрану не удалась: {ex.Message}", "WARNING");
        }
    }
}
