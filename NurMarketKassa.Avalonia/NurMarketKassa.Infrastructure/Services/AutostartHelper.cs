using Microsoft.Win32;

namespace NurMarketKassa.Services;

public static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ValueName => NurMarketKassa.Services.AppMode.IsOwner ? "NurMarketOwner" : "NurMarketKassa";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Autostart status read failed: {ex.GetType().Name}", "WARNING");
            return false;
        }
    }

    public static void SetEnabled(bool enable)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Autostart is supported only on Windows.");
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (k == null)
                return;
            if (!enable)
            {
                k.DeleteValue(ValueName, false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            k.SetValue(ValueName, $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Autostart update failed: {ex.GetType().Name}", "WARNING");
        }
    }

    public static void SyncFromPreference(bool wantAutostart)
    {
        if (wantAutostart)
            SetEnabled(true);
        else
            SetEnabled(false);
    }
}
