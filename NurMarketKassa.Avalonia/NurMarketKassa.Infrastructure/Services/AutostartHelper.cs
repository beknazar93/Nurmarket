using Microsoft.Win32;

namespace NurMarketKassa.Services;

public static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ValueName => NurMarketKassa.Services.AppMode.IsOwner ? "NurMarketOwner" : "NurMarketKassa";

    // 2026-10-29: Linux — файл ~/.config/autostart/*.desktop, macOS — агент ~/Library/LaunchAgents/*.plist.
    // Раньше на не-Windows SetEnabled бросал PlatformNotSupportedException при каждом сохранении настроек экрана.
    private static string? UnixAutostartFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "LaunchAgents", "kg.nurmarket." + ValueName.ToLowerInvariant() + ".plist");
        if (OperatingSystem.IsLinux())
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(cfg) ? Path.Combine(home, ".config") : cfg;
            return Path.Combine(root, "autostart", ValueName.ToLowerInvariant() + ".desktop");
        }
        return null;
    }

    private static void SetEnabledUnix(bool enable)
    {
        var file = UnixAutostartFile();
        if (file is null)
            return;
        try
        {
            if (!enable)
            {
                if (File.Exists(file))
                    File.Delete(file);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            var args = NurMarketKassa.Services.AppMode.IsOwner ? "--owner" : "";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = OperatingSystem.IsMacOS()
                ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n"
                  + "  <key>Label</key><string>kg.nurmarket." + ValueName.ToLowerInvariant() + "</string>\n"
                  + "  <key>ProgramArguments</key><array><string>" + System.Security.SecurityElement.Escape(exe) + "</string>"
                  + (args.Length > 0 ? "<string>" + args + "</string>" : "") + "</array>\n"
                  + "  <key>RunAtLoad</key><true/>\n</dict>\n</plist>\n"
                : "[Desktop Entry]\nType=Application\nName=" + (args.Length > 0 ? "NurCRM Owner" : "NurCRM Kassa") + "\nExec=\"" + exe + "\" " + args
                  + "\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
            File.WriteAllText(file, text);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Autostart update failed: {ex.GetType().Name}", "WARNING");
        }
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return UnixAutostartFile() is { } f && File.Exists(f);
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
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            SetEnabledUnix(enable);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            if (!enable)
                return; // 2026-10-29: «выключить» на платформе без автозапуска — не ошибка
            throw new PlatformNotSupportedException("Autostart is supported only on Windows.");
        }
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
