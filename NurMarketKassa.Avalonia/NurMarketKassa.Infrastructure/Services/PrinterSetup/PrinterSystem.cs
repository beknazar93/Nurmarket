using System.Net.Sockets;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13: что мастер и диагностика узнают о системе. В программе — <see cref="PrinterSystem"/> (Windows: очереди, USB, порты; Linux: порты и сеть),
/// в тестах — подмена с готовыми данными (настоящие принтеры не трогаются).</summary>
public interface IPrinterSystem
{
    IReadOnlyList<WindowsQueueInfo> ListQueues();
    IReadOnlyList<UsbPrinterDevice> ListUsbPrinters();
    /// <summary>COM, LPT, WinUSB, запомненные сетевые и т.п. (прежний поиск кассы).</summary>
    IReadOnlyList<DiscoveredPrinter> ListOtherPorts();
    PrinterPortService.PortProbeResult Probe(string devicePath);
    /// <summary>Состояние от самого принтера (сеть, USB напрямую). null — ответ недоступен.</summary>
    Task<PrinterStatusReport?> QueryStatusAsync(string devicePath, PrinterLanguage language, CancellationToken ct);
}

/// <summary>Настоящая система. Только чтение; запрос состояния — короткие команды DLE EOT / ESC ! ? (бумага не расходуется).</summary>
public sealed class PrinterSystem : IPrinterSystem
{
    public static readonly PrinterSystem Instance = new();

    public IReadOnlyList<WindowsQueueInfo> ListQueues()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<WindowsQueueInfo>();
        try
        {
            return WindowsPrinterNative.ListQueues();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer setup: queues not listed: {ex.GetType().Name}", "WARNING");
            return Array.Empty<WindowsQueueInfo>();
        }
    }

    public IReadOnlyList<UsbPrinterDevice> ListUsbPrinters()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<UsbPrinterDevice>();
        try
        {
            return WindowsPrinterNative.ListUsbPrinters();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer setup: USB printers not listed: {ex.GetType().Name}", "WARNING");
            return Array.Empty<UsbPrinterDevice>();
        }
    }

    public IReadOnlyList<DiscoveredPrinter> ListOtherPorts()
    {
        try
        {
            return PrinterDiscoveryService.Discover();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer setup: ports not listed: {ex.GetType().Name}", "WARNING");
            return Array.Empty<DiscoveredPrinter>();
        }
    }

    public PrinterPortService.PortProbeResult Probe(string devicePath)
    {
        if (NetworkPrinterAddress.IsNetworkPort(devicePath))
            return PrinterPortService.ProbeNetworkIdle(devicePath, throttle: false)
                   ?? new PrinterPortService.PortProbeResult(true, Tr.T("● Занят — идёт печать", "● Бош эмес — басып жатат", "● Busy — printing", "● Meşgul — yazdırıyor", "● Band — chop etmoqda"), "tcp");
        return PrinterPortService.ProbePort(devicePath);
    }

    public async Task<PrinterStatusReport?> QueryStatusAsync(string devicePath, PrinterLanguage language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(devicePath) || LabelPrinterSelector.IsBusy(devicePath))
            return null;
        try
        {
            if (NetworkPrinterAddress.IsNetworkPort(devicePath) && NetworkPrinterAddress.TryParse(devicePath, out var host, out var port, out _))
            {
                using var client = new TcpClient { NoDelay = true };
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(NetworkPrinterAddress.ProbeTimeoutMs);
                await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                return await PrinterStatusQuery.QueryAsync(stream, language, 800, ct).ConfigureAwait(false);
            }
            if (OperatingSystem.IsWindows() && devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                await using var usb = WindowsPrinterNative.OpenUsbInterface(devicePath);
                if (usb is null)
                    return null;
                return await PrinterStatusQuery.QueryAsync(usb, language, 800, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Printer setup: status query failed ({ex.GetType().Name}).", "PRINTER");
        }
        return null;
    }
}
