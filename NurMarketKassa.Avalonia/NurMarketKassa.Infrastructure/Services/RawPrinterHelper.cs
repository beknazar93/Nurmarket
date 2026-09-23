using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace NurMarketKassa.Services;

public static class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.Drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(
        uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    private const uint PRINTER_ENUM_LOCAL = 0x00000002;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    /// <summary>Все принтеры, установленные в Windows (локальные + подключённые по сети), включая
    /// виртуальные вроде "Generic / Text Only" — многие чековые/этикеточные принтеры ставятся именно так.</summary>
    public static IReadOnlyList<string> GetInstalledPrinterNames()
    {
        const uint level = 4; // PRINTER_INFO_4: минимальный уровень, отдающий только имена — не требует прав администратора.
        const uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;

        EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
                return Array.Empty<string>();

            var names = new List<string>((int)returned);
            var size = Marshal.SizeOf<PRINTER_INFO_4>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(buffer + i * size);
                if (!string.IsNullOrWhiteSpace(info.pPrinterName))
                    names.Add(info.pPrinterName);
            }
            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_4
    {
        public string pPrinterName;
        public string pServerName;
        public uint Attributes;
    }

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO docInfo);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.Drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.Drv", EntryPoint = "SetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint command);

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_6
    {
        public uint dwStatus;
    }

    private const uint PrinterControlResume = 2;
    private const uint PrinterStatusPaused = 0x00000001;
    private const uint PrinterStatusPowerSave = 0x01000000;

    /// <summary>2026-09-14, живой баг: встроенный (через очередь Windows) принтер время от
    /// времени "засыпает" (PRINTER_STATUS_POWER_SAVE) или сама очередь Windows встаёт на паузу
    /// (PRINTER_STATUS_PAUSED — Windows делает это сама после нескольких неудачных попыток
    /// достучаться до заснувшего устройства) — и StartDocPrinter/WritePrinter при этом всё равно
    /// молча возвращают успех: спулер просто принял задание в очередь, а печатать его физически
    /// некому/некогда. Кассир видит "чек напечатан", а бумага не выходит, пока кто-то вручную не
    /// откроет очередь принтера и не нажмёт "Продолжить". Снимаем паузу автоматически перед
    /// каждой печатью и даём заснувшему принтеру время проснуться, вместо того чтобы слепо
    /// доверять успешному коду возврата спулера.</summary>
    private static void WakeUpIfSleepingOrPaused(IntPtr hPrinter)
    {
        var status = TryGetStatus(hPrinter);
        if (status == 0)
            return;

        if ((status & PrinterStatusPaused) != 0)
        {
            PosLogger.Log("Принтер: очередь Windows была на паузе — снимаю паузу перед печатью.", "PRINTER");
            SetPrinter(hPrinter, 0, IntPtr.Zero, PrinterControlResume);
        }

        if ((status & PrinterStatusPowerSave) != 0)
        {
            PosLogger.Log("Принтер в режиме энергосбережения — жду пробуждения перед печатью.", "PRINTER");
            Thread.Sleep(800);
        }
    }

    private static uint TryGetStatus(IntPtr hPrinter)
    {
        try
        {
            GetPrinter(hPrinter, 6, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return 0;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetPrinter(hPrinter, 6, buffer, needed, out _))
                    return 0;

                return Marshal.PtrToStructure<PRINTER_INFO_6>(buffer).dwStatus;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer status check failed: {ex.GetType().Name}", "DEBUG");
            return 0;
        }
    }

    internal static bool TryOpen(string printerName, out PrinterSafeHandle handle, out int win32Error)
    {
        win32Error = 0;
        handle = null!;

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        handle = new PrinterSafeHandle(hPrinter);
        return true;
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes, out int win32Error)
    {
        win32Error = 0;
        if (!TryOpen(printerName, out var printer, out win32Error))
            return false;

        using (printer)
        {
            WakeUpIfSleepingOrPaused(printer.DangerousGetHandle());

            var docInfo = new DOC_INFO { pDocName = "Receipt", pDataType = "RAW" };
            if (!StartDocPrinter(printer.DangerousGetHandle(), 1, ref docInfo))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                if (!StartPagePrinter(printer.DangerousGetHandle()))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                var unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                    if (!WritePrinter(printer.DangerousGetHandle(), unmanagedBytes, bytes.Length, out var written)
                        || written != bytes.Length)
                    {
                        win32Error = Marshal.GetLastWin32Error();
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanagedBytes);
                }

                EndPagePrinter(printer.DangerousGetHandle());
                EndDocPrinter(printer.DangerousGetHandle());
                return true;
            }
            catch
            {
                try
                {
                    EndDocPrinter(printer.DangerousGetHandle());
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Printer document cleanup failed: {ex.GetType().Name}", "WARNING");
                }
                throw;
            }
        }
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes) =>
        SendBytesToPrinter(printerName, bytes, out _);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDataType;
    }

    internal sealed class PrinterSafeHandle : SafeHandle
    {
        public PrinterSafeHandle(IntPtr handle) : base(handle, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }
}
