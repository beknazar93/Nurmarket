using Microsoft.Win32;

namespace NurMarketKassa.Services;

/// <summary>
/// Discovery for USB printers Windows exposes only as a raw virtual port (e.g. "USB001"),
/// with no spooler queue installed — typical of cheap thermal label/receipt printers.
/// Such devices never appear in <see cref="RawPrinterHelper.GetInstalledPrinterNames"/>
/// (spooler queues only), but Windows still creates a "USB Monitor" port for them that
/// accepts raw bytes via <see cref="PrinterPortService.SendRawBytes"/> (device path
/// <c>\\.\USBxxx</c>, already handled by its WriteViaDirectPort fallback).
/// </summary>
public static class UsbRawPrinterPort
{
    /// <summary>Marks a raw USB port entry in a flat printer-name list (vs. a spooler queue name).</summary>
    public const string LabelPrefix = "USB (без драйвера): ";

    /// <summary>Bare port names ("USB001", "USB002", …) that Windows registered under
    /// "USB Monitor" — one per USB printer plugged in without a spooler-level driver.</summary>
    public static IReadOnlyList<string> EnumeratePortNames()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports");
            if (key is null)
                return Array.Empty<string>();

            return key.GetValueNames()
                .Where(name => name.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"USB raw printer port enumeration failed: {ex.GetType().Name}", "WARNING");
            return Array.Empty<string>();
        }
    }

    /// <summary>Порты, которые Windows зарегистрировал под "USB Monitor" — по одному на
    /// каждый подключённый USB-принтер без драйвера в спулере.</summary>
    public static IReadOnlyList<string> EnumeratePorts() =>
        EnumeratePortNames().Select(name => LabelPrefix + name).ToList();

    public static bool IsRawPortLabel(string printerName) =>
        printerName.StartsWith(LabelPrefix, StringComparison.Ordinal);

    /// <summary>Device path PrinterPortService.SendRawBytes routes through WriteViaDirectPort.</summary>
    public static string ToDevicePath(string printerName) =>
        @"\\.\" + printerName[LabelPrefix.Length..];
}
