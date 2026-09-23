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

        lock (GetPortLock(port))
        {
            for (var i = 0; i < attemptCount; i++)
            {
                try
                {
                    WritePayload(port, payload);
                    PosLogger.Log($"Порт {port}: отправлено {payload.Length} байт", "PRINTER");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    PosLogger.Log($"Порт {port}: попытка {i + 1}/{attemptCount} — {Describe(ex)}", "PRINTER");
                    Thread.Sleep(80 + 70 * i);
                }
            }
        }

        throw new InvalidOperationException($"Не удалось отправить данные на {port}: {last?.Message}", last);
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

    private static void WritePayload(string port, byte[] payload)
    {
        if (WinUsbPrinterPort.IsWinUsbDevicePath(port))
        {
            if (!WinUsbPrinterPort.TryParseDevicePath(port, out var vid, out var pid))
                throw new InvalidOperationException($"Не удалось разобрать адрес WinUSB-устройства «{port}».");

            WinUsbPrinterPort.SendRawBytes(vid, pid, payload);
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

        if (!RawPrinterHelper.SendBytesToPrinter(port, payload, out var win32Error))
            throw new IOException($"Очередь Windows «{port}» не приняла данные (Win32: {win32Error}).");
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

            var stdErr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(8000);

            if (proc.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? "" : $" ({stdErr.Trim()})";
                throw new InvalidOperationException(
                    $"Порт {target} не найден в системе. Проверьте кабель или наличие порта в Диспетчере устройств.{detail}");
            }

            Thread.Sleep(80);
            PosLogger.Log($"copy /b OK: {target}, bytes={payload.Length}", "PRINTER");
        }
        catch (InvalidOperationException)
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
