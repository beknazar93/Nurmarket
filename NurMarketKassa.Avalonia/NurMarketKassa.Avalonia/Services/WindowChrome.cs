using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-23 (аудит Б-9): системная рамка Windows у главного окна, «Настроек», «Маркетплейса» и «Напоминаний
/// должникам» красилась акцентом ОС (бирюзовая шапка). Окна нужны с рамкой (перемещение, сворачивание, размер), поэтому
/// только красим заголовок под тему кассы через DWM (Windows 11, сборка 22000+; на Windows 10, Linux и Android — ничего).</summary>
internal static class WindowChrome
{
    // 2026-10-23 (аудит круг 4, Б-9): в тёмной теме заголовок оставался белым — красили сразу при смене варианта, а кисти
    // акцентной темы (Application.Resources) обновляются чуть позже. Теперь окна запоминаются, красим ещё раз отложенно
    // и по явному RepaintAll() после смены темы; плюс флаг тёмного режима рамки (кнопки свернуть/закрыть).
    private static readonly System.Collections.Generic.List<WeakReference<Window>> Tracked = new();
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int DwmwaBorderColor = 34, DwmwaCaptionColor = 35, DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Красит заголовок окна под тему; повторно — при смене темы. Ошибки глотает (рамка останется системной).</summary>
    public static void Apply(Window window)
    {
        lock (Tracked) Tracked.Add(new WeakReference<Window>(window));
        window.Opened += (_, _) => Paint(window);
        window.ActualThemeVariantChanged += (_, _) =>
        {
            Paint(window);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Paint(window), Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    /// <summary>Перекрашивает заголовки всех окон (вызывать после смены темы, когда кисти уже обновлены).</summary>
    public static void RepaintAll()
    {
        Window[] list;
        lock (Tracked)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out _));
            list = Tracked.Select(w => w.TryGetTarget(out var x) ? x : null!).Where(x => x != null).ToArray();
        }
        foreach (var w in list)
            Paint(w);
    }

    private static void Paint(Window window)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 22000)
                return;
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;
            var caption = Pick("BrushPanelElevated") ?? Pick("BrushWindowAlt");
            var text = Pick("BrushText");
            var border = Pick("BrushBorder");
            if (caption is { } c)
            {
                var dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128 ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
                Set(hwnd, DwmwaCaptionColor, c);
            }
            if (text is { } t) Set(hwnd, DwmwaTextColor, t);
            if (border is { } b) Set(hwnd, DwmwaBorderColor, b);

            Color? Pick(string key) =>
                window.TryFindResource(key, window.ActualThemeVariant, out var r) && r is ISolidColorBrush sb ? sb.Color : null;
        }
        catch
        {
            // рамка останется системной
        }
    }

    private static void Set(IntPtr hwnd, int attr, Color c)
    {
        var colorRef = c.R | (c.G << 8) | (c.B << 16); // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, attr, ref colorRef, sizeof(int));
    }
}
