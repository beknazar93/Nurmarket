using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Прямая отправка сырых байтов на LPT/COM или в очередь Windows.</summary>
public static class PrinterPortService
{
    public sealed record PortProbeResult(bool IsAvailable, string Message, string PortKind);

    public static PortProbeResult ProbePort(string? rawPort)
    {
        var port = NormalizePort(rawPort);
        if (string.IsNullOrWhiteSpace(port))
            return new PortProbeResult(false, "Порт не указан", "none");

        if (WinUsbPrinterPort.IsWinUsbDevicePath(port))
        {
            return WinUsbPrinterPort.TryParseDevicePath(port, out var vid, out var pid) && WinUsbPrinterPort.Probe(vid, pid)
                ? new PortProbeResult(true, "● Доступен (WinUSB)", "winusb")
                : new PortProbeResult(false, "○ Устройство WinUSB не найдено", "winusb");
        }

        if (HardwarePortHelper.LooksLikeComPort(port))
        {
            var names = SerialPort.GetPortNames();
            var found = names.Any(p => string.Equals(p, port, StringComparison.OrdinalIgnoreCase));
            return found
                ? new PortProbeResult(true, "● Доступен (COM)", "com")
                : new PortProbeResult(false, "○ COM не найден в системе", "com");
        }

        if (HardwarePortHelper.LooksLikeLptPort(port))
        {
            // Проверяем существование устройства через QueryDosDevice — без открытия
            // дескриптора, поэтому порт не блокируется для последующей печати.
            return LptDeviceExists(port)
                ? new PortProbeResult(true, "● Доступен (LPT)", "lpt")
                : new PortProbeResult(false, "○ LPT не найден в системе (нет физического порта)", "lpt");
        }

        if (RawPrinterHelper.TryOpen(port, out var handle, out var win32Error))
        {
            handle.Dispose();
            return new PortProbeResult(true, "● Доступен (очередь Windows)", "spooler");
        }

        return new PortProbeResult(false, $"○ Недоступен (код Win32: {win32Error})", "unknown");
    }

    // 2026-09-16, живой баг из журнала кассы ("не отправляет печать ценника — код ошибки 5",
    // WinUsb Access Denied на устройстве, которое секундами раньше/позже успешно печатало):
    // PrinterKeepAliveService раз в 90с шлёт no-op на порт ЧЕКОВОГО принтера в фоне, а печать
    // ценника/этикетки идёт через этот же метод — если оба используют один физический USB-порт
    // (обычная ситуация с одним термопринтером на чеки и наклейки), их вызовы могли пересечься
    // по времени: WinUSB-устройства обычно не поддерживают два одновременных открытых хендла
    // (WinUsb_Initialize), несмотря на разрешающие флаги FILE_SHARE_* на уровне CreateFile.
    // Блокировка по порту гарантирует, что keep-alive и любая печать на ОДИН И ТОТ ЖЕ порт
    // никогда не выполняются параллельно из этого процесса.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PortLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static object GetPortLock(string port) => PortLocks.GetOrAdd(port, static _ => new object());

    // 2026-09-26, живой баг «касса иногда перестаёт печатать чеки до перезапуска»: запись в порт
    // не была ограничена по времени (WinUSB — без тайм-аута канала, copy /b — ReadToEnd до выхода
    // процесса, прямой порт — синхронная запись), а блокировка порта выше ждала бесконечно. Принтер,
    // переставший забирать данные (кончилась бумага, открыта крышка, уснул, завис), навсегда
    // оставлял первую печать висеть внутри блокировки — и все следующие чеки, keep-alive и
    // денежный ящик выстраивались за ней до перезапуска кассы. Воспроизведено стендом (канал,
    // который принимает соединение, но не читает): 1-я печать висит, 2-я ждёт порт вечно.
    // Теперь и ожидание порта, и сама запись ограничены по времени.
    private static readonly TimeSpan PortWaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>Сколько даётся одной отправке: 8 с плюс время на большой (графический) чек из
    /// расчёта 40 КБ/с — медленнее реальной печати любого термопринтера.</summary>
    internal static TimeSpan WriteTimeout(int payloadLength) =>
        TimeSpan.FromMilliseconds(Math.Min(60_000, 8_000 + payloadLength / 40));

    public static void SendRawBytes(string? rawPort, byte[] payload, int retries = 3)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
            throw new InvalidOperationException("Пустой буфер печати.");

        var port = NormalizePort(rawPort);
        if (string.IsNullOrWhiteSpace(port))
            throw new InvalidOperationException("Не указан порт принтера.");

        var attemptCount = Math.Clamp(retries, 1, 8);
        Exception? last = null;

        var portLock = GetPortLock(port);
        if (!Monitor.TryEnter(portLock, PortWaitLimit))
        {
            PosLogger.Log($"Порт {port}: занят предыдущей отправкой дольше {PortWaitLimit.TotalSeconds:0} с.", "PRINTER");
            throw new PrinterStalledException(
                $"Принтер на {port} занят: предыдущая печать не завершилась за {PortWaitLimit.TotalSeconds:0} с.");
        }

        try
        {
            for (var i = 0; i < attemptCount; i++)
            {
                try
                {
                    WritePayloadWithTimeout(port, payload, isKeepAlive: false);
                    PosLogger.Log($"Порт {port}: отправлено {payload.Length} байт", "PRINTER");
                    return;
                }
                catch (Exception ex) when (ex is PrinterStalledException or TimeoutException)
                {
                    // Принтер не забирает данные: повтор только удвоит ожидание кассира, а если принтер
                    // «оживёт» посреди повтора — чек выйдет дважды.
                    last = ex;
                    PosLogger.Log($"Порт {port}: попытка {i + 1}/{attemptCount} — {Describe(ex)}; повтор не делаю.", "PRINTER");
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    PosLogger.Log($"Порт {port}: попытка {i + 1}/{attemptCount} — {Describe(ex)}", "PRINTER");
                    Thread.Sleep(80 + 70 * i);
                }
            }
        }
        finally
        {
            Monitor.Exit(portLock);
        }

        if (last is PrinterStalledException or TimeoutException)
            throw new PrinterStalledException(last.Message, last);
        throw new InvalidOperationException($"Не удалось отправить данные на {port}: {last?.Message}", last);
    }

    /// <summary>Для фоновых отправок (keep-alive): только если порт сейчас свободен. Не ждёт чужую
    /// печать и не встаёт за ней в очередь — следующий тик всё равно придёт. В очередь Windows не
    /// добавляет задание, если в ней уже что-то лежит: выключенный принтер иначе копил бы по
    /// заданию каждые полторы минуты, и чек стоял бы за ними.</summary>
    public static bool TrySendKeepAlive(string? rawPort, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var port = NormalizePort(rawPort);
        if (string.IsNullOrWhiteSpace(port) || payload.Length == 0)
            return false;

        var portLock = GetPortLock(port);
        if (!Monitor.TryEnter(portLock))
            return false;

        try
        {
            if (IsSpoolerPort(port) && RawPrinterHelper.HasQueuedJobs(port))
                return false;

            WritePayloadWithTimeout(port, payload, isKeepAlive: true);
            return true;
        }
        finally
        {
            Monitor.Exit(portLock);
        }
    }

    /// <summary>Имя принтера Windows (очередь печати), а не LPT/COM/WinUSB/сетевой путь —
    /// та же развилка, что в <see cref="WritePayload"/>.</summary>
    public static bool IsSpoolerPort(string port) =>
        !WinUsbPrinterPort.IsWinUsbDevicePath(port)
        && !HardwarePortHelper.LooksLikeComPort(port)
        && !HardwarePortHelper.LooksLikeLptPort(port)
        && !port.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Запись с жёстким лимитом времени. Сами способы записи тоже ограничены (WinUSB —
    /// тайм-аут канала, copy /b — снятие процесса, COM — WriteTimeout), это страховка на случай,
    /// если драйвер всё же не вернёт управление: запись идёт в отдельном потоке, при тайм-ауте он
    /// доживает сам, а порт освобождается для следующих чеков.</summary>
    private static void WritePayloadWithTimeout(string port, byte[] payload, bool isKeepAlive)
    {
        var timeout = WriteTimeout(payload.Length);
        var write = Task.Factory.StartNew(
            () => WritePayload(port, payload, isKeepAlive),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        bool finished;
        try
        {
            finished = write.Wait(timeout);
        }
        catch (AggregateException)
        {
            finished = true;
        }

        if (!finished)
        {
            _ = write.ContinueWith(
                t => PosLogger.Log(
                    $"Порт {port}: зависшая отправка завершилась позже {(t.IsFaulted ? "ошибкой — " + t.Exception?.GetBaseException().Message : "успешно")}.",
                    "PRINTER"),
                TaskScheduler.Default);
            throw new PrinterStalledException(
                $"Принтер на {port} не принял данные за {timeout.TotalSeconds:0} с — нет бумаги, открыта крышка, выключен или завис.");
        }

        write.GetAwaiter().GetResult();
    }

    public static string NormalizePort(string? raw) =>
        HardwarePortHelper.NormalizeLptPort(raw, "");

    /// <summary>Проверяет, существует ли LPT-устройство в системе (QueryDosDevice), не открывая порт.</summary>
    public static bool LptDeviceExists(string? rawPort)
    {
        var port = NormalizePort(rawPort);
        if (string.IsNullOrWhiteSpace(port))
            return false;

        // Имя для QueryDosDevice — без префикса \\.\ и без завершающего двоеточия.
        var deviceName = port.StartsWith(@"\\.\", StringComparison.Ordinal) ? port[4..] : port;
        deviceName = deviceName.TrimEnd(':');

        try
        {
            var buffer = new char[1024];
            var len = QueryDosDevice(deviceName, buffer, (uint)buffer.Length);
            if (len != 0)
                return true;

            // ERROR_INSUFFICIENT_BUFFER (122) тоже означает, что устройство существует.
            return Marshal.GetLastWin32Error() == 122;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer port probe failed: {ex.GetType().Name}", "DEBUG");
            return false;
        }
    }

    private static void WritePayload(string port, byte[] payload, bool isKeepAlive)
    {
        if (WinUsbPrinterPort.IsWinUsbDevicePath(port))
        {
            if (!WinUsbPrinterPort.TryParseDevicePath(port, out var vid, out var pid))
                throw new InvalidOperationException($"Не удалось разобрать адрес WinUSB-устройства «{port}».");

            // Тайм-аут канала чуть короче общего лимита записи, чтобы сработал именно он: WinUSB
            // тогда сам прерывает передачу и отпускает устройство.
            WinUsbPrinterPort.SendRawBytes(vid, pid, payload,
                (uint)Math.Max(3_000, WriteTimeout(payload.Length).TotalMilliseconds - 2_000));
            return;
        }

        if (HardwarePortHelper.LooksLikeComPort(port))
        {
            WriteViaSerialPort(port, payload);
            return;
        }

        if (HardwarePortHelper.LooksLikeLptPort(port) || port.StartsWith(@"\\", StringComparison.Ordinal))
        {
            WriteViaDirectPort(port, payload);
            return;
        }

        if (isKeepAlive)
        {
            if (!RawPrinterHelper.SendBytesToPrinter(port, payload, out var keepAliveError, RawPrinterHelper.KeepAliveDocumentName))
                throw new IOException($"Очередь Windows «{port}» не приняла данные (Win32: {keepAliveError}).");
            return;
        }

        var outcome = RawPrinterHelper.SendAndConfirm(port, payload, out var win32Error, out var stuckReason);
        if (outcome == RawPrinterHelper.JobOutcome.NotAccepted)
            throw new IOException($"Очередь Windows «{port}» не приняла данные (Win32: {win32Error}).");
        if (outcome == RawPrinterHelper.JobOutcome.Stuck)
            throw new PrinterStalledException(
                $"Принтер «{port}» не печатает: {stuckReason}. Задание снято из очереди Windows, чтобы чек не вышел позже сам.");
    }

    private static void WriteViaSerialPort(string port, byte[] payload)
    {
        using var serial = new SerialPort(port, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 5000,
            ReadTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };

        try
        {
            serial.Open();
            serial.Write(payload, 0, payload.Length);
            serial.BaseStream.Flush();
            Thread.Sleep(50);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"COM-порт {port} занят другим процессом.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"COM-порт {port}: {ex.Message}", ex);
        }
        finally
        {
            if (serial.IsOpen)
                serial.Close();
        }
    }

    private static void WriteViaDirectPort(string port, byte[] payload)
    {
        // На Windows LPT FileStream часто возвращает успех, но принтер молчит.
        // copy /b — проверенный способ доставки сырых ESC/POS на параллельный порт.
        if (HardwarePortHelper.LooksLikeLptPort(port))
        {
            WriteViaCopyCommand(port, payload);
            return;
        }

        Exception? streamError = null;
        try
        {
            using var stream = OpenDeviceStream(port);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
            Thread.Sleep(50);
            PosLogger.Log($"Прямая запись OK: {port}, bytes={payload.Length}", "PRINTER");
            return;
        }
        catch (Exception ex)
        {
            streamError = ex;
            PosLogger.Log($"Прямая запись не удалась ({port}): {Describe(ex)}", "PRINTER");
        }

        try
        {
            WriteViaCopyCommand(port, payload);
        }
        catch (Exception copyEx)
        {
            throw new InvalidOperationException(
                $"Не удалось отправить чек на {port}. Прямая запись: {streamError?.Message}; copy /b: {copyEx.Message}",
                copyEx);
        }
    }

    private static Stream OpenDeviceStream(string port)
    {
        var candidates = BuildOpenCandidates(port);
        Exception? last = null;

        foreach (var candidate in candidates)
        {
            try
            {
                return new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.WriteThrough);
            }
            catch (Exception ex)
            {
                last = ex;
            }

            try
            {
                var handle = CreateFile(
                    candidate,
                    NativeGenericWrite,
                    FileShare.ReadWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException($"CreateFile({candidate}) код {err}.");
                }

                return new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new IOException($"Не удалось открыть порт «{port}».", last);
    }

    private static IEnumerable<string> BuildOpenCandidates(string port)
    {
        var normalized = port.Trim();
        if (normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            yield return normalized;
            if (normalized.Length > 4)
                yield return normalized[4..];
            yield break;
        }

        if (HardwarePortHelper.LooksLikeLptPort(normalized))
        {
            yield return $@"\\.\{normalized}";
            yield return normalized;
            yield break;
        }

        yield return normalized;
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
            yield return $@"\\.\{normalized}";
    }

    private static void WriteViaCopyCommand(string port, byte[] payload)
    {
        // Если порт LPT и его физически нет в системе — не пытаемся слать вслепую.
        if (HardwarePortHelper.LooksLikeLptPort(port) && !LptDeviceExists(port))
        {
            throw new InvalidOperationException(
                $"Порт {port} не найден в системе. Проверьте кабель или наличие порта в Диспетчере устройств.");
        }

        // copy /b принимает короткое имя устройства (LPT1), а не путь \\.\LPT1.
        var target = port.StartsWith(@"\\.\", StringComparison.Ordinal) ? port[4..] : port;
        var tempFile = Path.Combine(Path.GetTempPath(), $"NurCrmKassa-print-{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllBytes(tempFile, payload);
            // Лимит на copy /b: на выключенном/зависшем LPT-принтере copy ждёт бесконечно. Раньше
            // здесь стоял ReadToEnd ДО ожидания выхода — он ждал закрытия потока, то есть того же
            // бесконечного copy, и держал порт занятым до перезапуска кассы.
            var copyTimeout = (int)Math.Max(3_000, WriteTimeout(payload.Length).TotalMilliseconds - 2_000);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c copy /b \"{tempFile}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (proc == null)
                throw new InvalidOperationException("Не удалось запустить copy /b.");

            var stdErrTask = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(copyTimeout))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    PosLogger.Log($"copy /b: не удалось снять зависший процесс: {killEx.Message}", "PRINTER");
                }

                throw new PrinterStalledException(
                    $"Принтер на {target} не принял чек за {copyTimeout / 1000} с — нет бумаги, открыта крышка, выключен или завис.");
            }

            proc.WaitForExit();
            var stdErr = stdErrTask.Wait(1_000) ? stdErrTask.Result : "";

            if (proc.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? "" : $" ({stdErr.Trim()})";
                throw new InvalidOperationException(
                    $"Порт {target} не найден в системе. Проверьте кабель или наличие порта в Диспетчере устройств.{detail}");
            }

            Thread.Sleep(80);
            PosLogger.Log($"copy /b OK: {target}, bytes={payload.Length}", "PRINTER");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PrinterStalledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось отправить чек на {target} через copy /b: {ex.Message}",
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Printer temporary file cleanup failed: {ex.GetType().Name}", "WARNING");
            }
        }
    }

    private static string Describe(Exception ex) =>
        ex switch
        {
            Win32Exception w => $"{w.Message} (Win32 {w.NativeErrorCode})",
            UnauthorizedAccessException => "Отказано в доступе. Запустите кассу от имени администратора или закройте программу, занявшую порт.",
            _ => ex.Message,
        };

    /// <summary>Принтер не забирает данные или не печатает задание (нет бумаги, крышка, выключен,
    /// завис). Повторять отправку бессмысленно — это не сбой связи, а состояние принтера.</summary>
    public sealed class PrinterStalledException : IOException
    {
        public PrinterStalledException(string message, Exception? inner = null) : base(message, inner)
        {
        }
    }

    private const uint NativeGenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);
}
