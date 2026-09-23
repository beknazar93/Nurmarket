using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Управление RLS1000 (официальная программа Rongta для весов) снаружи —
/// официальный задокументированный способ (мануал "Label Scale Software User Manual",
/// раздел 2.5 "Message mechanism interaction for RLS1000", пример на Delphi): найти окно
/// FindWindow('TRLS1000Form') и "нажать" F9 (Download PLU — полная перезапись PLU на весах)
/// через PostMessage(WM_KEYDOWN).
///
/// Используется ДВУМЯ способами доставки PLU (выбор в ScalesPluWindow):
/// 1. По умолчанию — SendPluAsync пишет .txp-файл (скачанный с сайта) в рабочий путь
///    RLS1000, затем жмёт F9; RLS1000 настроена читать PLU из этого файла (её собственный
///    режим "shared file").
/// 2. По просьбе владельца (2026-09-19: "выбор — с сайта или свой сервер... из локальной
///    базы") — TriggerDownloadPluAsync просто жмёт F9 БЕЗ записи файла; в этом случае
///    RLS1000 должна быть один раз настроена на режим "TCP/IP" (File → Options → TCP/IP,
///    указав адрес и порт этого компьютера — см. RongtaTcpServerService), и F9 заставит её
///    подключиться туда за PLU вместо чтения файла (тот же мануал, раздел 1: "5 способов
///    получить PLU-файл", один из них — "4. TCP/IP mode").
///
/// 2026-09-19, живая жалоба: FindWindow('TRLS1000Form') у владельца НЕ находил окно (тайм-аут
/// "RLS1000 не открыла окно за отведённое время", хотя окно реально было открыто и видно на
/// экране) — имя класса из Delphi-примера в мануале, видимо, не совпадает с реальной
/// установленной версией. Поиск по классу оставлен первой попыткой (вдруг на другой версии
/// сработает), но основной, надёжный путь теперь — поиск ПО ПРОЦЕССУ: раз мы либо сами
/// запустили exe (знаем PID), либо нашли уже работающий процесс "RLS1000" по имени, берём
/// его главное видимое окно через EnumWindows+GetWindowThreadProcessId — это не зависит от
/// того, как называется класс окна в конкретной сборке.
///
/// ЧЕСТНО: PostMessage — это "сообщение поставлено в очередь окна", НЕ подтверждение, что
/// весы обновились. RegisterWindowMessage('RLS1000') из того же мануала теоретически можно
/// слушать как индикатор завершения, но его точная семантика (успех vs. просто "принято")
/// не описана в мануале — не реализовано (см. план, сознательно вне объёма v1). UI обязан
/// показывать "команда передана", а не "успешно".</summary>
public static class RongtaScaleAutomationService
{
    private const string WindowClassName = "TRLS1000Form";
    private const string ProcessName = "RLS1000";
    private const uint WM_KEYDOWN = 0x0100;
    private const int VK_F9 = 0x78;
    private static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(15);

    private const int SW_MINIMIZE = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Перезаписывает рабочий .txp (тот же путь, который владелец один раз вручную
    /// открывает в RLS1000 через File → Open — см. план, шаг "ручная проверка перед
    /// автоматизацией"), затем как TriggerDownloadPluAsync.</summary>
    public static async Task<RongtaSendResult> SendPluAsync(
        byte[] txpContent, string workingTxpPath, string rls1000ExePath, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workingTxpPath)!);
            await File.WriteAllBytesAsync(workingTxpPath, txpContent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta: failed to write working .txp: {ex.Message}", "SCALES");
            return RongtaSendResult.Failed("Не удалось сохранить файл PLU: " + ex.Message);
        }

        return await TriggerDownloadPluAsync(rls1000ExePath, ct).ConfigureAwait(false);
    }

    /// <summary>Запускает RLS1000, если не запущена, ждёт её окно и "нажимает" F9 (Download
    /// PLU) — без записи файла (для режима TCP/IP, см. doc-comment класса).</summary>
    public static async Task<RongtaSendResult> TriggerDownloadPluAsync(string rls1000ExePath, CancellationToken ct = default)
    {
        var hWnd = FindWindow(WindowClassName, null);
        if (hWnd == IntPtr.Zero)
            hWnd = FindMainWindowOfRunningProcess();

        if (hWnd == IntPtr.Zero)
        {
            if (!TryLaunch(rls1000ExePath, out var launchError, out var launchedProcess))
                return RongtaSendResult.Failed(launchError ?? "Не удалось запустить RLS1000.");

            hWnd = await WaitForWindowAsync(WindowWaitTimeout, launchedProcess, ct).ConfigureAwait(false);
            if (hWnd == IntPtr.Zero)
                return RongtaSendResult.Failed("RLS1000 не открыла окно за отведённое время.");
        }

        PosLogger.Log($"Rongta: found RLS1000 window, title='{GetTitle(hWnd)}'", "SCALES");

        // 2026-09-19, живая жалоба владельца: "открывается программа ронта а он не должен
        // был открываться" — RLS1000 всё равно обязана запуститься (см. doc-comment класса,
        // это не обходится), но НЕ обязана быть видна кассиру. ProcessStartInfo.WindowStyle =
        // Minimized (было раньше) — это лишь подсказка при запуске, старые Delphi-программы
        // вроде RLS1000 её нередко игнорируют. ShowWindow с уже найденным hWnd — надёжнее,
        // сворачивает в любом случае (и при свежем запуске, и если окно уже было открыто).
        ShowWindow(hWnd, SW_MINIMIZE);

        var posted = PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_F9, IntPtr.Zero);
        return posted
            ? RongtaSendResult.Sent()
            : RongtaSendResult.Failed("Не удалось отправить команду окну RLS1000.");
    }

    /// <summary>Ищет уже запущенный процесс с именем "RLS1000" (владелец мог открыть его сам,
    /// как на скриншотах) и берёт его первое видимое окно — независимо от имени класса.</summary>
    private static IntPtr FindMainWindowOfRunningProcess()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                var hWnd = FindVisibleWindowForProcessId((uint)process.Id);
                if (hWnd != IntPtr.Zero)
                    return hWnd;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindVisibleWindowForProcessId(uint processId)
    {
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != processId)
                return true;
            found = hWnd;
            return false; // остановить перебор
        }, IntPtr.Zero);
        return found;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool TryLaunch(string exePath, out string? error, out Process? process)
    {
        error = null;
        process = null;
        if (!File.Exists(exePath))
        {
            error = "RLS1000 не найдена по пути: " + exePath;
            return false;
        }

        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = "Не удалось запустить RLS1000: " + ex.Message;
            return false;
        }
    }

    private static async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout, Process? launchedProcess, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hWnd = FindWindow(WindowClassName, null);
            if (hWnd == IntPtr.Zero && launchedProcess is not null)
            {
                try
                {
                    launchedProcess.Refresh();
                    if (!launchedProcess.HasExited)
                        hWnd = FindVisibleWindowForProcessId((uint)launchedProcess.Id);
                }
                catch (InvalidOperationException)
                {
                    // процесс уже завершился между проверками — пробуем по имени ниже
                }
            }

            if (hWnd == IntPtr.Zero)
                hWnd = FindMainWindowOfRunningProcess();

            if (hWnd != IntPtr.Zero)
                return hWnd;

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }
}

public sealed class RongtaSendResult
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static RongtaSendResult Sent() => new() { IsSuccess = true };
    public static RongtaSendResult Failed(string message) => new() { IsSuccess = false, ErrorMessage = message };
}
