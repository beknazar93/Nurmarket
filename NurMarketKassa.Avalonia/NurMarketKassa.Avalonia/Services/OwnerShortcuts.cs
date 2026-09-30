using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Ярлык «NurMarket Владелец» (2026-09-26). Программа владельца ставится вместе с кассой:
/// это тот же exe с ключом --owner (см. AppMode), поэтому и обновляется она вместе с кассой.
/// Ярлык создаётся при установке и при обновлении — при обновлении один раз, чтобы удалённый
/// владельцем ярлык не возвращался с каждой версией; при удалении кассы убирается.
///
/// Вызывается из хуков Velopack (Program.Main) — там на всё 15–30 секунд, поэтому только
/// файловые операции, без окон.
///
/// 2026-09-30, владелец: «в новых установках не устанавливается админка — срочно». Ярлык делался
/// через COM «WScript.Shell»: на компьютерах, где Windows Script Host отключён или его блокирует
/// антивирус, создание молча падало (исключение глоталось), и программы владельца у клиента не
/// было вовсе — другого способа её открыть нет. Теперь: (1) ярлык пишется напрямую системным
/// IShellLinkW, без WScript; (2) касса при каждом обычном запуске досоздаёт ярлык, если отметки
/// «создан» ещё нет (хук установки не сработал); (3) итог пишется в Logs\owner-shortcut.log.</summary>
internal static class OwnerShortcuts
{
    private const string ShortcutName = "NurMarket Владелец";
    private const string CreatedMarker = "owner-shortcut.created";

    /// <summary>2026-09-30, владелец: «при установке сделай так, чтобы можно было установить обе
    /// программы или только кассу». Установщик Velopack своих вопросов не задаёт, поэтому хук
    /// установки только ставит эту отметку, а касса при первом запуске спрашивает (Program.Main →
    /// <see cref="EnsureOnStartup"/>). При обновлениях отметки нет — у клиентов ничего не меняется.</summary>
    private const string ChoicePendingMarker = "install-choice.pending";

    /// <summary>Хук установки (новая установка, не обновление): спросить при первом запуске.</summary>
    public static void MarkInstallChoicePending()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || IsSeparateOwnerPackage() || RootDir() is not { } root)
                return;
            File.WriteAllText(Path.Combine(root, ChoicePendingMarker), DateTimeOffset.Now.ToString("O"));
            Log("новая установка: выбор «обе программы / только касса» — при первом запуске");
        }
        catch (Exception ex)
        {
            Log($"отметка выбора не записана: {ex.Message}");
        }
    }

    /// <summary>Есть ли на компьютере ярлык программы владельца (рабочий стол или «Пуск»).</summary>
    public static bool IsInstalled()
    {
        try
        {
            foreach (var folder in ShortcutFolders())
                if (File.Exists(Path.Combine(folder, ShortcutName + ".lnk")))
                    return true;
        }
        catch
        {
        }
        return false;
    }

    /// <summary>Отдельная программа владельца (свой пакет с owner.mode) — у неё выбор не нужен.</summary>
    public static bool IsSeparateOwnerInstall => IsSeparateOwnerPackage();

    /// <summary>Выбор человека: true — касса и программа владельца, false — только касса. Выбор
    /// запоминается отметкой «создан»: касса больше не досоздаёт ярлык сама.</summary>
    public static void ApplyChoice(bool withOwner)
    {
        try
        {
            if (RootDir() is { } root)
            {
                var pending = Path.Combine(root, ChoicePendingMarker);
                if (File.Exists(pending))
                    File.Delete(pending);
            }

            if (withOwner)
            {
                EnsureCreated(evenIfCreatedBefore: true);
            }
            else
            {
                RemoveShortcutsOnly();
                if (MarkerPath() is { } marker)
                    File.WriteAllText(marker, "kassa-only " + DateTimeOffset.Now.ToString("O"));
            }
            Log(withOwner ? "выбор: касса и программа владельца" : "выбор: только касса");
        }
        catch (Exception ex)
        {
            Log($"выбор не применён: {ex.Message}");
        }
    }

    private static string? RootDir() => Directory.GetParent(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;

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
            {
                Log("нет пути к exe — ярлык не создан");
                return;
            }

            var created = 0;
            foreach (var folder in ShortcutFolders())
            {
                var path = Path.Combine(folder, ShortcutName + ".lnk");
                try
                {
                    if (CreateShortcut(path, exe))
                        created++;
                }
                catch (Exception ex)
                {
                    Log($"ярлык «{path}» не создан: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Отметку ставим, только если получился хотя бы один ярлык: иначе следующий запуск
            // кассы попробует ещё раз (см. EnsureOnStartup).
            if (created > 0 && marker != null)
                File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
            Log($"ярлыков создано: {created} (exe: {exe})");
        }
        catch (Exception ex)
        {
            // Ярлык — удобство: установка и обновление кассы из-за него падать не должны.
            Log($"ошибка: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Обычный запуск кассы. Первый запуск после установки — спрашиваем «обе программы или
    /// только касса» (окно Windows, Avalonia ещё не запущена). Иначе — если хук установки не смог
    /// сделать ярлык (отметки нет), делаем его сейчас; удалённый владельцем ярлык не возвращаем.</summary>
    public static void EnsureOnStartup()
    {
        if (NurMarketKassa.Services.AppMode.IsOwner || !OperatingSystem.IsWindows() || IsSeparateOwnerPackage())
            return;

        if (RootDir() is { } root && File.Exists(Path.Combine(root, ChoicePendingMarker)))
        {
            ApplyChoice(AskInstallChoice());
            return;
        }

        EnsureCreated(evenIfCreatedBefore: false);
    }

    /// <summary>Вопрос при первом запуске. Язык интерфейса ещё не выбран — текст на русском и
    /// кыргызском; кнопки «Да/Нет» Windows подписывает на языке системы.</summary>
    private static bool AskInstallChoice()
    {
        const string text =
            "Установить вместе с кассой программу владельца «NurMarket Владелец»?\n" +
            "(склад, продажи, финансы, аналитика, зарплата)\n\n" +
            "Да — касса и программа владельца\n" +
            "Нет — только касса\n\n" +
            "Передумать можно потом: Настройки → Обновления → «Программа владельца».\n\n" +
            "Касса менен бирге ээсинин программасын «NurMarket Владелец» орнотобузбу?\n" +
            "Ооба — касса жана ээсинин программасы; Жок — касса гана.";
        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        const int IDYES = 6;
        return MessageBoxW(IntPtr.Zero, text, "NurMarket Kassa — установка",
            MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Убрать только ярлыки программы владельца (выбор «только касса» / кнопка в настройках).</summary>
    public static void RemoveShortcutsOnly()
    {
        var exe = Environment.ProcessPath;
        foreach (var folder in ShortcutFolders())
        {
            var path = Path.Combine(folder, ShortcutName + ".lnk");
            try
            {
                if (File.Exists(path) && PointsTo(path, exe))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log($"ярлык «{path}» не удалён: {ex.Message}");
            }
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

    private static bool CreateShortcut(string path, string exe)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(path)) || !Directory.Exists(Path.GetDirectoryName(path)))
            return false;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetArguments("--owner");
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetIconLocation(exe, 0);
            link.SetDescription("NurMarket Владелец — склад, продажи, финансы и зарплата");
            ((IPersistFile)link).Save(path, true);
            return File.Exists(path);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static bool PointsTo(string path, string? exe)
    {
        if (string.IsNullOrEmpty(exe))
            return false;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            return string.Equals(target.ToString(), exe, StringComparison.OrdinalIgnoreCase)
                   && args.ToString().Contains("--owner", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Журнал ярлыка: в хуке установки PosLogger ещё не настроен, пишем файлом рядом с
    /// журналом кассы (%LOCALAPPDATA%\NurMarketKassa\Logs).</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "owner-shortcut.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    // ── Системный ярлык Windows (IShellLinkW + IPersistFile), без WScript ─────────────────

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
