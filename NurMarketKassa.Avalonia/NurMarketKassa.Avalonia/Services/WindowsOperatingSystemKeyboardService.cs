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
            foreach (var running in Process.GetProcessesByName("osk"))
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

            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var path = Path.Combine(system32, "osk.exe");
            Process.Start(new ProcessStartInfo
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

    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
