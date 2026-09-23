using Microsoft.Win32;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Поиск/тихая установка официальной программы Rongta RLS1000 — используется
/// вместо собственного сетевого протокола весов (см. RongtaScaleAutomationService и её
/// doc-комментарий, почему выбран этот путь, 2026-09-19). Сама RLS1000 общается с весами
/// по уже настроенному ею каналу — эта касса только запускает её и один раз нажимает
/// "Скачать PLU" за пользователя (см. RongtaScaleAutomationService).
///
/// Инсталлятор (RLS1000_SETUP_V1.129.exe, ~46.7 МБ) НЕ входит в базовый пакет кассы — по
/// тому же принципу, что офлайн-модель голосового распознавания (VoiceModelDownloadService):
/// не утяжелять установщик для магазинов без весов Rongta. Заливка на GitHub Release как
/// ассет — отдельный шаг, ещё не сделан (публикация приостановлена по просьбе владельца);
/// пока BundledInstallerPath — единственный признанный источник инсталлятора.</summary>
public static class RongtaSetupService
{
    private const string DisplayNameHint = "RLS1000";

    private static readonly string[] UninstallSubKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>Куда попадает инсталлятор после скачивания (see class doc-comment) —
    /// проверяется в первую очередь, аналогично "AnyDesk\AnyDesk.exe" рядом с кассой.</summary>
    public static string BundledInstallerPath =>
        Path.Combine(AppContext.BaseDirectory, "Rongta", "RLS1000_SETUP.exe");

    /// <summary>Ищет уже установленную RLS1000 через реестр Uninstall-записей Inno Setup
    /// (DisplayName содержит "RLS1000") — не угадывает путь, а читает InstallLocation.
    /// Резерв — фиксированные Program Files/Program Files x86 кандидаты, как
    /// KnownAnyDeskPaths в RemoteSupportWindow.axaml.cs.</summary>
    public static string? TryFindInstalledExePath()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var subKeyPath in UninstallSubKeys)
            {
                string? found;
                try
                {
                    found = SearchUninstallHive(hive, subKeyPath);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Rongta registry search failed ({subKeyPath}): {ex.Message}", "SCALES");
                    found = null;
                }
                if (found != null)
                    return found;
            }
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RLS1000", "bin", "RLS1000.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RLS1000", "bin", "RLS1000.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? SearchUninstallHive(RegistryKey hive, string subKeyPath)
    {
        using var uninstallKey = hive.OpenSubKey(subKeyPath);
        if (uninstallKey is null)
            return null;

        foreach (var name in uninstallKey.GetSubKeyNames())
        {
            using var sub = uninstallKey.OpenSubKey(name);
            var displayName = sub?.GetValue("DisplayName") as string;
            if (string.IsNullOrEmpty(displayName) ||
                displayName.IndexOf(DisplayNameHint, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var installLocation = sub?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installLocation))
                continue;

            var exe = FindExeUnder(installLocation);
            if (exe != null)
                return exe;
        }

        return null;
    }

    /// <summary>2026-09-19: путь "XX\bin\RLS1000.exe" из мануала (раздел 2.5) не подтверждён
    /// буквально — "XX" это просто "папка установки" в примере на Delphi, имя подпапки
    /// ("bin") могло быть другим в реально установленной версии. Сначала проверяем
    /// задокументированный путь, при неудаче — ищем файл рекурсивно.</summary>
    private static string? FindExeUnder(string installLocation)
    {
        try
        {
            var direct = Path.Combine(installLocation, "bin", "RLS1000.exe");
            if (File.Exists(direct))
                return direct;

            if (Directory.Exists(installLocation))
                return Directory.EnumerateFiles(installLocation, "RLS1000.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta install path search failed under '{installLocation}': {ex.Message}", "SCALES");
        }

        return null;
    }

    /// <summary>Тихая установка (Inno Setup, подтверждено побайтовой проверкой инсталлятора).
    /// /VERYSILENT глушит UI самого инсталлятора, но НЕ убирает системный UAC-запрос, если
    /// инсталлятор требует прав администратора — это неизбежно и нормально, как и при
    /// установке самой кассы.</summary>
    public static async Task<bool> InstallSilentlyAsync(string installerExePath, CancellationToken ct = default)
    {
        if (!File.Exists(installerExePath))
            return false;

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerExePath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            });
            if (process is null)
                return false;

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta silent install failed: {ex.Message}", "SCALES");
            return false;
        }
    }
}
