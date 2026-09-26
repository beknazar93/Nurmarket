using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Ярлык «NurMarket Владелец» (2026-09-26). Программа владельца ставится вместе с кассой:
/// это тот же exe с ключом --owner (см. AppMode), поэтому и обновляется она вместе с кассой.
/// Ярлык создаётся при установке и при обновлении — при обновлении один раз, чтобы удалённый
/// владельцем ярлык не возвращался с каждой версией; при удалении кассы убирается.
///
/// Вызывается из хуков Velopack (Program.Main) — там на всё 15–30 секунд, поэтому только
/// файловые операции, без окон.</summary>
internal static class OwnerShortcuts
{
    private const string ShortcutName = "NurMarket Владелец";
    private const string CreatedMarker = "owner-shortcut.created";

    public static void EnsureCreated(bool evenIfCreatedBefore)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || IsSeparateOwnerPackage())
                return;

            var marker = MarkerPath();
            if (!evenIfCreatedBefore && marker != null && File.Exists(marker))
                return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;

            foreach (var folder in ShortcutFolders())
                CreateShortcut(Path.Combine(folder, ShortcutName + ".lnk"), exe);

            if (marker != null)
                File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
        }
        catch
        {
            // Ярлык — удобство: установка и обновление кассы из-за него падать не должны.
        }
    }

    public static void Remove()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || IsSeparateOwnerPackage())
                return;

            var exe = Environment.ProcessPath;
            foreach (var folder in ShortcutFolders())
            {
                var path = Path.Combine(folder, ShortcutName + ".lnk");
                // Чужой ярлык с тем же именем (например, отдельной установки владельца) не трогаем.
                if (File.Exists(path) && PointsTo(path, exe))
                    File.Delete(path);
            }

            if (MarkerPath() is { } marker && File.Exists(marker))
                File.Delete(marker);
        }
        catch
        {
        }
    }

    /// <summary>Старый отдельный пакет владельца (owner.mode рядом с exe) — у него свои ярлыки.</summary>
    private static bool IsSeparateOwnerPackage() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, NurMarketKassa.Services.AppMode.OwnerMarkerFile));

    /// <summary>Отметка лежит в корне установки (над папкой current), которую обновления не трогают.</summary>
    private static string? MarkerPath()
    {
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return root == null ? null : Path.Combine(root.FullName, CreatedMarker);
    }

    private static string[] ShortcutFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
    ];

    private static void CreateShortcut(string path, string exe)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(path)) || !Directory.Exists(Path.GetDirectoryName(path)))
            return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        dynamic? shell = null;
        dynamic? link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            link = shell!.CreateShortcut(path);
            link.TargetPath = exe;
            link.Arguments = "--owner";
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "NurMarket Владелец — склад, продажи, финансы и зарплата";
            link.Save();
        }
        finally
        {
            if (link != null)
                Marshal.FinalReleaseComObject(link);
            if (shell != null)
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool PointsTo(string path, string? exe)
    {
        if (string.IsNullOrEmpty(exe))
            return false;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return false;

        dynamic? shell = null;
        dynamic? link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            link = shell!.CreateShortcut(path);
            string target = link.TargetPath;
            string args = link.Arguments;
            return string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)
                   && args.Contains("--owner", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (link != null)
                Marshal.FinalReleaseComObject(link);
            if (shell != null)
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
