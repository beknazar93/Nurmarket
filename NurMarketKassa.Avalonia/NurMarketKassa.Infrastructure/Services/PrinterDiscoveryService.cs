using System.IO.Ports;

namespace NurMarketKassa.Services;

/// <summary>Одна найденная точка подключения чекового принтера.</summary>
public sealed record DiscoveredPrinter(string Label, string DevicePath)
{
    // ComboBox без явного ItemTemplate показывает ToString() каждого элемента.
    public override string ToString() => Label;
}

/// <summary>
/// Ищет доступные варианты подключения чекового принтера: установленные в Windows
/// принтеры (спулер — так обычно ставятся USB-модели, включая "Generic / Text Only"),
/// LPT-порты (LPT1-LPT4) и COM-порты (старые последовательные модели).
/// </summary>
public static class PrinterDiscoveryService
{
    private static readonly string[] LptPortsToProbe = ["LPT1", "LPT2", "LPT3", "LPT4"];

    public static IReadOnlyList<DiscoveredPrinter> Discover()
    {
        var result = new List<DiscoveredPrinter>();

        try
        {
            foreach (var name in RawPrinterHelper.GetInstalledPrinterNames())
                result.Add(new DiscoveredPrinter($"🖨 {name}", name));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer enumeration (spooler) failed: {ex.GetType().Name}", "WARNING");
        }

        try
        {
            // Принтеры с драйвером WinUSB (установлен вручную через Zadig) — Windows вообще не
            // знает о них как о принтере/порте, видно только напрямую по VID/PID через libusb.
            var winUsbDevices = WinUsbPrinterPort.EnumerateDevices();
            foreach (var device in winUsbDevices)
                result.Add(new DiscoveredPrinter($"🔌 WinUSB {device.Label}", device.DevicePath));

            // Диагностика для случаев "касса не видит подключённый через Zadig принтер" —
            // 0 найденных устройств чаще всего означает, что в Zadig выбран не тот драйвер
            // (libusb-win32/libusbK вместо WinUSB — у них другой device interface GUID, см.
            // WinUsbPrinterPort.WinUsbInterfaceGuid) или устройство ещё не переподключено
            // после установки драйвера.
            PosLogger.Log(
                winUsbDevices.Count == 0
                    ? "WinUSB scan: устройств не найдено (проверьте, что в Zadig выбран драйвер именно WinUSB, и переподключите принтер после установки драйвера)."
                    : $"WinUSB scan: найдено {winUsbDevices.Count} устройств(о) — {string.Join(", ", winUsbDevices.Select(d => d.Label))}.",
                "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WinUSB printer enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        try
        {
            // Дешёвые термопринтеры без спулерного драйвера видны только как "сырой" USB-порт
            // (Диспетчер устройств → "Порты USB для печати"). DevicePath в формате \\.\USBxxx
            // подхватывается WriteViaDirectPort в PrinterPortService — отдельного пути записи не нужно.
            foreach (var port in UsbRawPrinterPort.EnumeratePortNames())
                result.Add(new DiscoveredPrinter($"🔌 USB {port} (без драйвера)", $@"\\.\{port}"));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"USB raw printer port enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        foreach (var lpt in LptPortsToProbe)
        {
            try
            {
                if (PrinterPortService.LptDeviceExists(lpt))
                    result.Add(new DiscoveredPrinter($"🔌 {lpt} (параллельный порт)", lpt));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"LPT probe failed for {lpt}: {ex.GetType().Name}", "WARNING");
            }
        }

        try
        {
            foreach (var com in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                result.Add(new DiscoveredPrinter($"🔌 {com} (последовательный порт)", com));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"COM port enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        return result;
    }
}
