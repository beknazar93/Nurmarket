using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NurMarketKassa.Services;

public static class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

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
