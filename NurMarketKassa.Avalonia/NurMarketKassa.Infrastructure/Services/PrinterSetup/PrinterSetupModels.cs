namespace NurMarketKassa.Services.PrinterSetup;

// 2026-11-13 (владелец: «надо сделать готовыми драйверы для чековых принтеров и принтеров штрихкодов, в основном Xprinter у клиентов»):
// данные мастера «Подключить принтер» и диагностики «Принтер не печатает?». Логика без Windows — чтобы проверялась тестами на подменных данных.

/// <summary>Для чего подключаем принтер.</summary>
public enum PrinterRole
{
    /// <summary>Чековый (ESC/POS: XP-58, XP-80, POS-58/80…).</summary>
    Receipt,
    /// <summary>Этикеток и штрихкодов (TSPL: XP-365B, XP-360B, XP-420B, XP-460B…).</summary>
    Label,
    /// <summary>«Определить автоматически» — по названию модели.</summary>
    Auto,
}

/// <summary>Язык команд принтера.</summary>
public enum PrinterLanguage { Unknown, EscPos, Tspl }

/// <summary>Как найден принтер.</summary>
public enum PrinterCandidateKind
{
    /// <summary>Уже есть очередь (принтер) Windows.</summary>
    WindowsQueue,
    /// <summary>USB-принтер подключён, но очереди Windows для него нет (драйвер производителя не ставили).</summary>
    UsbWithoutQueue,
    /// <summary>Сетевой (LAN/Wi-Fi, порт 9100).</summary>
    Network,
    /// <summary>COM-порт.</summary>
    Serial,
    /// <summary>LPT-порт.</summary>
    Parallel,
    /// <summary>USB с драйвером WinUSB (Zadig) или устройство, которое видит платформа (Linux).</summary>
    Other,
}

/// <summary>USB-принтер, который Windows видит как «Поддержка USB принтера» (usbprint): путь интерфейса, VID/PID, модель из ответа принтера (IEEE 1284), порт USB00x.</summary>
/// <param name="InterfacePath">Путь интерфейса «\\?\usb#vid_…#…#{28d78fad-…}» — по нему можно писать в принтер напрямую (без очереди) и читать его состояние.</param>
/// <param name="Manufacturer">Производитель из ответа принтера («Xprinter»), может быть пустым.</param>
/// <param name="Model">Модель из ответа принтера («XP-420B»), может быть пустой.</param>
/// <param name="PortName">Порт Windows «USB001» (на него ставится очередь), null — порт ещё не создан.</param>
public sealed record UsbPrinterDevice(string InterfacePath, ushort Vid, ushort Pid, string? Manufacturer, string? Model, string? PortName);

/// <summary>Очередь (принтер) Windows: имя, порт, драйвер, состояние (PRINTER_INFO_2), сколько заданий в очереди.</summary>
public sealed record WindowsQueueInfo(string Name, string PortName, string DriverName, uint Status, uint Attributes, int Jobs);

/// <summary>Догадка о марке: «Xprinter» точно (по ответу принтера или имени) или «вероятно» (по коду производителя USB).</summary>
public sealed record PrinterBrandGuess(string? Brand, bool Probable)
{
    public static readonly PrinterBrandGuess None = new(null, false);
    public bool IsXprinter => string.Equals(Brand, PrinterUsbCatalog.XprinterBrand, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Строка списка «Найденные принтеры» в мастере.</summary>
/// <param name="DevicePath">Что сохранить в настройках: имя очереди, TCP:адрес, COM3… Для USB без очереди — путь интерфейса (запасной способ «напрямую»).</param>
/// <param name="Model">Модель для показа («XP-80C»), если известна.</param>
public sealed record PrinterCandidate(
    PrinterCandidateKind Kind,
    string Title,
    string DevicePath,
    string? PortName,
    string? DriverName,
    string? Model,
    UsbPrinterDevice? Usb,
    PrinterBrandGuess Brand,
    PrinterRole RoleGuess,
    PrinterLanguage LanguageGuess)
{
    /// <summary>Нужна очередь Windows (USB есть, очереди нет).</summary>
    public bool NeedsQueue => Kind == PrinterCandidateKind.UsbWithoutQueue && !string.IsNullOrWhiteSpace(PortName);

    public override string ToString() => Title;
}
