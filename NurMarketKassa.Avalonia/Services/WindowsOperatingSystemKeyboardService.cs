using System.Diagnostics;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Запуск стандартной экранной клавиатуры Windows (osk.exe).</summary>
public sealed class WindowsOperatingSystemKeyboardService : IOperatingSystemKeyboardService
{
    public void ShowSystemKeyboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "osk.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OSK launch failed: {ex.Message}", "UI");
        }
    }
}
