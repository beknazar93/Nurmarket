using System.Diagnostics;
using System.Runtime.InteropServices;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Запуск стандартной экранной клавиатуры Windows (osk.exe).
///
/// 2026-09-30, владелец: «экранная клавиатура не работает в нашей программе». Что поправлено здесь:
/// osk.exe запускается по полному пути из System32 (на некоторых сборках Windows в PATH её нет);
/// если клавиатура уже запущена, но свёрнута или ушла за окна, — второй запуск её не показывает,
/// поэтому окно уже работающей клавиатуры поднимается наверх. Всё пишется в журнал (раздел UI).
/// Сама причина «набирается в никуда» — фокус, см. MainWindow.ToggleKeyboard.</summary>
public sealed class WindowsOperatingSystemKeyboardService : IOperatingSystemKeyboardService
{
    public void ShowSystemKeyboard()
    {
        try
        {
            // 2026-10-15: объекты Process освобождаются — вызов идёт при каждом касании поля (до раза в 700 мс), без Dispose копились дескрипторы.
            var runningProcesses = Process.GetProcessesByName("osk");
            try
            {
                foreach (var running in runningProcesses)
                {
                    var handle = running.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ShowWindow(handle, SW_SHOWNOACTIVATE);
                        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                        PosLogger.Log("OSK: клавиатура уже запущена — окно поднято наверх", "UI");
                        return;
                    }
                }
            }
            finally
            {
                foreach (var running in runningProcesses)
                    running.Dispose();
            }

            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var path = Path.Combine(system32, "osk.exe");
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = File.Exists(path) ? path : "osk.exe",
                UseShellExecute = true,
            });
            PosLogger.Log($"OSK: запущена {(File.Exists(path) ? path : "osk.exe")}", "UI");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OSK launch failed: {ex.Message}", "UI");
        }
    }

    /// <summary>2026-10-21 (пакет 6, п. 58): клавиатура Windows открыта и видна (не свёрнута) — закрыть её (WM_CLOSE).
    /// true — закрыли; false — её нет или она свёрнута (тогда её надо показать, а не закрыть).</summary>
    public static bool TryCloseVisible()
    {
        var closed = false;
        try
        {
            var running = Process.GetProcessesByName("osk");
            try
            {
                foreach (var p in running)
                {
                    var handle = p.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindowVisible(handle) || IsIconic(handle))
                        continue;
                    PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    closed = true;
                }
            }
            finally
            {
                foreach (var p in running)
                    p.Dispose();
            }
            if (closed)
                PosLogger.Log("OSK: повторное нажатие «Клавиатура» — клавиатура закрыта", "UI");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OSK close failed: {ex.Message}", "UI");
        }
        return closed;
    }

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
