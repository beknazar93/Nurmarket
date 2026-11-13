using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>Итог действия с правами администратора.</summary>
public enum ElevatedResult { Done, Cancelled, Failed, NotSupported }

public sealed record ElevatedOutcome(ElevatedResult Result, string Details = "");

/// <summary>2026-11-13 (мастер «Подключить принтер»): изменения в Windows — ТОЛЬКО по кнопке и после объяснения кассиру. Каждое действие — короткий скрипт
/// PowerShell, запущенный с повышением прав (окно UAC «Разрешить этому приложению вносить изменения?»); отказ — ничего не меняется.
/// Очередь создаётся на ВСТРОЕННОМ драйвере Windows «Generic / Text Only» (prnge001.inf есть в Windows 10/11, ставится из хранилища драйверов без интернета)
/// на порту USB00x: касса шлёт в очередь «сырые» команды (RAW) — ESC/POS для чеков и TSPL для этикеток, драйвер их не меняет.
/// Скрипт передаётся через -EncodedCommand (UTF-16, Base64): имя очереди кириллицей и кавычки не ломают командную строку. Журнал — в журнал кассы.</summary>
public static class PrinterQueueInstaller
{
    public const string GenericTextOnlyDriver = "Generic / Text Only";

    /// <summary>Начало имени очередей, созданных мастером («NurCRM Чек 80мм», «NurCRM Этикетки»).</summary>
    public const string QueuePrefix = "NurCRM ";

    private static readonly Regex PortRx = new(@"^(USB|LPT|COM)\d{1,3}:?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Подмена запуска для тестов: (скрипт, описание) → итог. В программе null — настоящий запуск с UAC.</summary>
    internal static Func<string, string, ElevatedOutcome>? RunOverride { get; set; }

    /// <summary>Имя очереди: не пустое, до 200 знаков, без «\», «,» и «!» (Windows их не принимает в имени принтера).</summary>
    public static bool IsValidQueueName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name.IndexOfAny(['\\', ',', '!', '\'', '"']) < 0;

    public static bool IsValidPortName(string? port) => !string.IsNullOrWhiteSpace(port) && PortRx.IsMatch(port.Trim());

    /// <summary>Строка PowerShell в одинарных кавычках.</summary>
    public static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string Wrap(string body, string logPath) =>
        "$ErrorActionPreference = 'Stop'\r\n" +
        $"$log = {PsQuote(logPath)}\r\n" +
        "try {\r\n" + body + "\r\n  'OK' | Set-Content -LiteralPath $log -Encoding UTF8 -ErrorAction SilentlyContinue\r\n  exit 0\r\n} catch {\r\n" +
        "  ($_ | Out-String) | Set-Content -LiteralPath $log -Encoding UTF8 -ErrorAction SilentlyContinue\r\n  exit 2\r\n}\r\n";

    /// <summary>Скрипт: поставить встроенный драйвер «Generic / Text Only» (если ещё нет) и создать очередь на порту.</summary>
    public static string BuildCreateQueueScript(string queueName, string portName, string logPath)
    {
        if (!IsValidQueueName(queueName))
            throw new ArgumentException("bad queue name", nameof(queueName));
        if (!IsValidPortName(portName))
            throw new ArgumentException("bad port name", nameof(portName));
        var body =
            $"  $drv = {PsQuote(GenericTextOnlyDriver)}\r\n" +
            "  if (-not (Get-PrinterDriver -Name $drv -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $drv }\r\n" +
            $"  if (-not (Get-Printer -Name {PsQuote(queueName)} -ErrorAction SilentlyContinue)) {{ Add-Printer -Name {PsQuote(queueName)} -DriverName $drv -PortName {PsQuote(portName.Trim().TrimEnd(':'))} }}";
        return Wrap(body, logPath);
    }

    /// <summary>Скрипт: удалить все задания очереди (в том числе чужие и «застрявшие»).</summary>
    public static string BuildClearJobsScript(string queueName, string logPath) =>
        Wrap($"  Get-PrintJob -PrinterName {PsQuote(queueName)} | Remove-PrintJob", logPath);

    /// <summary>Скрипт: снять паузу очереди и «Работать автономно».</summary>
    public static string BuildResumeScript(string queueName, string logPath) => Wrap(
        $"  $p = Get-CimInstance Win32_Printer | Where-Object {{ $_.Name -eq {PsQuote(queueName)} }} | Select-Object -First 1\r\n" +
        "  if (-not $p) { throw 'QUEUE_NOT_FOUND' }\r\n" +
        "  if ($p.WorkOffline) { $p.WorkOffline = $false; Set-CimInstance -InputObject $p }\r\n" +
        "  Invoke-CimMethod -InputObject $p -MethodName Resume | Out-Null", logPath);

    /// <summary>Скрипт: перевести очередь на другой порт (принтер воткнули в другой разъём USB — Windows создала новый порт USB002).</summary>
    public static string BuildMovePortScript(string queueName, string portName, string logPath)
    {
        if (!IsValidPortName(portName))
            throw new ArgumentException("bad port name", nameof(portName));
        return Wrap($"  Set-Printer -Name {PsQuote(queueName)} -PortName {PsQuote(portName.Trim().TrimEnd(':'))}", logPath);
    }

    public static string EncodeCommand(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static Task<ElevatedOutcome> CreateQueueAsync(string queueName, string portName) =>
        RunAsync(log => BuildCreateQueueScript(queueName, portName, log), $"создание очереди «{queueName}» (драйвер {GenericTextOnlyDriver}, порт {portName})");

    public static Task<ElevatedOutcome> ClearJobsElevatedAsync(string queueName) =>
        RunAsync(log => BuildClearJobsScript(queueName, log), $"очистка очереди «{queueName}»");

    public static Task<ElevatedOutcome> ResumeQueueAsync(string queueName) =>
        RunAsync(log => BuildResumeScript(queueName, log), $"снятие паузы и «автономно» у очереди «{queueName}»");

    public static Task<ElevatedOutcome> MovePortAsync(string queueName, string portName) =>
        RunAsync(log => BuildMovePortScript(queueName, portName, log), $"перевод очереди «{queueName}» на порт {portName}");

    private static async Task<ElevatedOutcome> RunAsync(Func<string, string> buildScript, string action)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"nurcrm-printer-setup-{Guid.NewGuid():N}.txt");
        string script;
        try
        {
            script = buildScript(logPath);
        }
        catch (ArgumentException ex)
        {
            PosLogger.Log($"Мастер принтера: {action} — неверные данные ({ex.ParamName}).", "PRINTER");
            return new ElevatedOutcome(ElevatedResult.Failed, ex.Message);
        }
        PosLogger.Log($"Мастер принтера: запрос прав администратора — {action}.", "PRINTER");
        if (RunOverride is { } fake)
            return Log(action, fake(script, action));
        if (!OperatingSystem.IsWindows())
            return Log(action, new ElevatedOutcome(ElevatedResult.NotSupported));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + EncodeCommand(script),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return Log(action, new ElevatedOutcome(ElevatedResult.Failed, "process not started"));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Log(action, new ElevatedOutcome(ElevatedResult.Failed, "timeout"));
            }
            var details = ReadLog(logPath);
            return Log(action, proc.ExitCode == 0 ? new ElevatedOutcome(ElevatedResult.Done, details) : new ElevatedOutcome(ElevatedResult.Failed, $"exit {proc.ExitCode}: {details}"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Log(action, new ElevatedOutcome(ElevatedResult.Cancelled)); // кассир нажал «Нет» в окне UAC
        }
        catch (Exception ex)
        {
            return Log(action, new ElevatedOutcome(ElevatedResult.Failed, ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            try { File.Delete(logPath); } catch { /* файл мог не создаться */ }
        }
    }

    private static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static ElevatedOutcome Log(string action, ElevatedOutcome outcome)
    {
        var details = outcome.Details.Length > 400 ? outcome.Details[..400] : outcome.Details;
        PosLogger.Log($"Мастер принтера: {action} — {outcome.Result}{(details.Length > 0 ? " (" + details.Replace("\r", " ").Replace("\n", " ") + ")" : "")}.", "PRINTER");
        return outcome;
    }

    // ------------------------------------------------------------------ очередь «Generic / Text Only» от мастера = прямые команды для этикеток

    private static readonly object CacheLock = new();
    private static Dictionary<string, string>? _driverCache;
    private static long _driverCacheAt;

    /// <summary>Очередь создана мастером на «Generic / Text Only»: этикетка уходит в неё командами принтера (TSPL/ESC/POS), а не картинкой через драйвер
    /// (картинку драйвер «Text Only» не печатает). Только очереди с именем «NurCRM …» — у прежних клиентов поведение не меняется.</summary>
    public static bool IsRawTextOnlyQueue(string? queueName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(queueName) || !queueName.StartsWith(QueuePrefix, StringComparison.Ordinal))
            return false;
        Dictionary<string, string> drivers;
        lock (CacheLock)
        {
            if (_driverCache is null || Environment.TickCount64 - _driverCacheAt > 30_000)
            {
                try
                {
                    _driverCache = WindowsPrinterNative.ListQueues().GroupBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().DriverName, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Printer setup: queue drivers not read: {ex.GetType().Name}", "DEBUG");
                    _driverCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                _driverCacheAt = Environment.TickCount64;
            }
            drivers = _driverCache;
        }
        return drivers.TryGetValue(queueName, out var drv) && string.Equals(drv, GenericTextOnlyDriver, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Сбросить запомненные драйверы очередей (после создания очереди мастером).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
            _driverCache = null;
    }
}
