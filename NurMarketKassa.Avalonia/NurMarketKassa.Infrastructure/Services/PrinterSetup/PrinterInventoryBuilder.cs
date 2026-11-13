namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13 (мастер «Подключить принтер»): из сырых списков (USB-принтеры, очереди Windows, COM/LPT/сеть) — один список для кассира.
/// USB-принтер с очередью показывается один раз (как очередь), без очереди — как «USB без драйвера» (мастер предложит создать очередь).
/// Виртуальные принтеры (PDF, XPS, OneNote, факс) и служебные порты не показываются. Чистая функция — проверяется тестами.</summary>
public static class PrinterInventoryBuilder
{
    private static readonly string[] ServicePorts = ["PORTPROMPT:", "NUL:", "SHRFAX:", "FILE:", "XPSPORT:"];

    /// <summary>Порт Windows очереди — служебный (печать в файл, факс, PDF).</summary>
    public static bool IsServicePort(string? port) =>
        !string.IsNullOrWhiteSpace(port) && ServicePorts.Any(p => port.Trim().Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Порты очереди: «USB001» или «USB001,USB002» (очередь с пулом портов).</summary>
    public static IReadOnlyList<string> SplitPorts(string? portName) =>
        (portName ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<PrinterCandidate> Build(
        IEnumerable<UsbPrinterDevice> usbDevices,
        IEnumerable<WindowsQueueInfo> queues,
        IEnumerable<DiscoveredPrinter> otherPorts)
    {
        var usb = usbDevices.ToList();
        var result = new List<PrinterCandidate>();
        var claimedUsb = new HashSet<UsbPrinterDevice>();
        var queueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var q in queues)
        {
            if (string.IsNullOrWhiteSpace(q.Name) || PrinterDiscoveryService.IsVirtualDocumentPrinter(q.Name) || IsServicePort(q.PortName))
                continue;
            queueNames.Add(q.Name);
            var ports = SplitPorts(q.PortName);
            var device = usb.FirstOrDefault(d => d.PortName is { } p && ports.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (device is not null)
                claimedUsb.Add(device);
            var brand = PrinterUsbCatalog.GuessBrand(device?.Vid ?? 0, device?.Manufacturer, device?.Model, q.Name, q.DriverName);
            var role = PrinterUsbCatalog.GuessRole(device?.Model, q.Name, q.DriverName);
            result.Add(new PrinterCandidate(PrinterCandidateKind.WindowsQueue, q.Name, q.Name, q.PortName, q.DriverName, device?.Model,
                device, brand, role, PrinterUsbCatalog.LanguageFor(role)));
        }

        foreach (var d in usb.Where(d => !claimedUsb.Contains(d)))
        {
            var brand = PrinterUsbCatalog.GuessBrand(d.Vid, d.Manufacturer, d.Model);
            var role = PrinterUsbCatalog.GuessRole(d.Model);
            var model = string.IsNullOrWhiteSpace(d.Model) ? null : d.Model;
            var title = model is null
                ? Tr.T("USB-принтер", "USB-принтер", "USB printer", "USB yazıcı", "USB-printer")
                : (brand.Brand is { } b && !brand.Probable && !model.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? $"{b} {model}" : model);
            result.Add(new PrinterCandidate(PrinterCandidateKind.UsbWithoutQueue, title, d.InterfacePath, d.PortName, null, model,
                d, brand, role, PrinterUsbCatalog.LanguageFor(role)));
        }

        foreach (var p in otherPorts)
        {
            if (string.IsNullOrWhiteSpace(p.DevicePath) || p.IsVirtual)
                continue;
            // очереди Windows уже в списке; «\\.\USB001» — прежний способ «USB без драйвера», вместо него теперь USB-устройство выше
            if (queueNames.Contains(p.DevicePath) || p.DevicePath.StartsWith(@"\\.\USB", StringComparison.OrdinalIgnoreCase))
                continue;
            if (result.Any(r => LabelPrinterSelector.SamePrinter(r.DevicePath, p.DevicePath)))
                continue;
            var kind = NetworkPrinterAddress.IsNetworkPort(p.DevicePath) ? PrinterCandidateKind.Network
                : HardwarePortHelper.LooksLikeComPort(p.DevicePath) ? PrinterCandidateKind.Serial
                : HardwarePortHelper.LooksLikeLptPort(p.DevicePath) ? PrinterCandidateKind.Parallel
                : PrinterCandidateKind.Other;
            var brand = PrinterUsbCatalog.GuessBrand(TryVid(p.DevicePath), p.Label);
            var role = PrinterUsbCatalog.GuessRole(p.Label);
            result.Add(new PrinterCandidate(kind, p.Label, p.DevicePath, null, null, null, null, brand, role, PrinterUsbCatalog.LanguageFor(role)));
        }

        // Сверху — то, что скорее всего и есть принтер кассы: USB (с очередью или без), затем Xprinter и сеть, COM/LPT — в конце.
        return result
            .OrderBy(c => c.Kind switch
            {
                PrinterCandidateKind.UsbWithoutQueue => 0,
                PrinterCandidateKind.WindowsQueue when c.Usb is not null => 1,
                PrinterCandidateKind.Network => 2,
                PrinterCandidateKind.WindowsQueue => 3,
                PrinterCandidateKind.Other => 4,
                _ => 5,
            })
            .ThenBy(c => c.Brand.IsXprinter ? 0 : 1)
            .ToList();
    }

    private static ushort TryVid(string path) => PrinterUsbCatalog.TryParseVidPid(path, out var vid, out _) ? vid : (ushort)0;

    /// <summary>Подходит ли кандидат под выбранный тип принтера (Auto — все). Неизвестная роль подходит всегда.</summary>
    public static bool Matches(PrinterCandidate c, PrinterRole wanted) =>
        wanted == PrinterRole.Auto || c.RoleGuess == PrinterRole.Auto || c.RoleGuess == wanted;

    /// <summary>Понятное имя новой очереди: «NurCRM Чек 80мм», «NurCRM Чек 58мм», «NurCRM Этикетки» (+ номер, если такое имя занято).</summary>
    public static string SuggestQueueName(PrinterRole role, int paperMm, IEnumerable<string> existingNames)
    {
        var baseName = role == PrinterRole.Label
            ? Tr.T("NurCRM Этикетки", "NurCRM Этикеткалар", "NurCRM Labels", "NurCRM Etiketler", "NurCRM Yorliqlar")
            : Tr.T($"NurCRM Чек {paperMm}мм", $"NurCRM Чек {paperMm}мм", $"NurCRM Receipt {paperMm}mm", $"NurCRM Fiş {paperMm}mm", $"NurCRM Chek {paperMm}mm");
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
            return baseName;
        for (var i = 2; i < 100; i++)
            if (!taken.Contains($"{baseName} ({i})"))
                return $"{baseName} ({i})";
        return $"{baseName} {DateTime.Now:HHmmss}";
    }
}
