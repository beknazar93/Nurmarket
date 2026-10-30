using System.IO.Ports;

namespace NurMarketKassa.Services;

/// <summary>Одна найденная точка подключения чекового принтера.</summary>
public sealed record DiscoveredPrinter(string Label, string DevicePath)
{
    // ComboBox без явного ItemTemplate показывает ToString() каждого элемента.
    public override string ToString() => Label;

    /// <summary>2026-10-27 (приёмка Opus 26.10): «виртуальный» принтер (Microsoft Print to PDF, XPS, OneNote, факс, PDF-принтеры) —
    /// не принтер этикеток, его нельзя запоминать в настройках как принтер этикеток.</summary>
    public bool IsVirtual => PrinterDiscoveryService.IsVirtualDocumentPrinter(Label);
}

/// <summary>
/// Ищет доступные варианты подключения чекового принтера: установленные в Windows
/// принтеры (спулер — так обычно ставятся USB-модели, включая "Generic / Text Only"),
/// LPT-порты (LPT1-LPT4) и COM-порты (старые последовательные модели).
/// </summary>
public static class PrinterDiscoveryService
{
    private static readonly string[] VirtualPrinterMarkers = ["pdf", "xps", "onenote", "fax", "document writer", "print to file", "snagit", "send to"];

    /// <summary>Имя похоже на виртуальный принтер документов (PDF/XPS/OneNote/факс). Реальные принтеры этикеток так не называются.</summary>
    public static bool IsVirtualDocumentPrinter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        foreach (var marker in VirtualPrinterMarkers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] LptPortsToProbe = ["LPT1", "LPT2", "LPT3", "LPT4"];

    private static string? _lastWinUsbLine;
    private static long _lastWinUsbLineAt;

    public static IReadOnlyList<DiscoveredPrinter> Discover()
    {
        var result = new List<DiscoveredPrinter>();

        // 2026-10-04, Android-касса: принтеры, которые видит сама платформа (USB host, Bluetooth).
        // В Windows-кассе PlatformTransport не задан — список прежний.
        try
        {
            if (PrinterPortService.PlatformTransport is { } platform)
                result.AddRange(platform.Discover());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer enumeration (platform) failed: {ex.GetType().Name}", "WARNING");
        }

        // 2026-10-29: Android — ни очереди Windows, ни COM/LPT/WinUSB нет (журнал: DllNotFoundException winspool и
        // UnauthorizedAccessException GetPortNames при каждом открытии Настроек). Только то, что видит платформа (USB host, Bluetooth, сеть).
        if (OperatingSystem.IsAndroid())
            return result;

        // 2026-10-29: Linux/macOS — очередь Windows, WinUSB и LPT там нет (только устройства, CUPS и COM-переходники).
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !OperatingSystem.IsAndroid())
        {
            try
            {
                foreach (var com in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.Ordinal))
                    result.Add(new DiscoveredPrinter($"🔌 {com} (serial)", com));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"COM port enumeration failed: {ex.GetType().Name}", "WARNING");
            }
            return result;
        }

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
            var winUsbLine = winUsbDevices.Count == 0
                ? "WinUSB scan: устройств не найдено (проверьте, что в Zadig выбран драйвер именно WinUSB, и переподключите принтер после установки драйвера)."
                : $"WinUSB scan: найдено {winUsbDevices.Count} устройств(о) — {string.Join(", ", winUsbDevices.Select(d => d.Label))}.";
            // 2026-10-21 (пакет 6, п. 65): «Настройки кассы» ищут порты дважды при открытии (принтер и дисплей цены) — та же
            // строка в журнал не чаще раза в минуту (новый результат пишется сразу).
            if (!string.Equals(winUsbLine, _lastWinUsbLine, StringComparison.Ordinal)
                || Environment.TickCount64 - _lastWinUsbLineAt > 60_000)
            {
                _lastWinUsbLine = winUsbLine;
                _lastWinUsbLineAt = Environment.TickCount64;
                PosLogger.Log(winUsbLine, "INFO");
            }
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
                result.Add(new DiscoveredPrinter(Tr.T($"🔌 USB {port} (без драйвера)", $"🔌 USB {port} (драйверсиз)", $"🔌 USB {port} (no driver)", $"🔌 USB {port} (sürücüsüz)", $"🔌 USB {port} (drayversiz)"), $@"\\.\{port}"));
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
                    result.Add(new DiscoveredPrinter(Tr.T($"🔌 {lpt} (параллельный порт)", $"🔌 {lpt} (параллелдүү порт)", $"🔌 {lpt} (parallel port)", $"🔌 {lpt} (paralel port)", $"🔌 {lpt} (parallel port)"), lpt));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"LPT probe failed for {lpt}: {ex.GetType().Name}", "WARNING");
            }
        }

        try
        {
            foreach (var com in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                result.Add(new DiscoveredPrinter(Tr.T($"🔌 {com} (последовательный порт)", $"🔌 {com} (ырааттуу порт)", $"🔌 {com} (serial port)", $"🔌 {com} (seri port)", $"🔌 {com} (ketma-ket port)"), com));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"COM port enumeration failed: {ex.GetType().Name}", "WARNING");
        }

        return result;
    }
}
