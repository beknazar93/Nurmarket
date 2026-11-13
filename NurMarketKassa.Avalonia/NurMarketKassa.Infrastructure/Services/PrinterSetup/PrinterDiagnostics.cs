namespace NurMarketKassa.Services.PrinterSetup;

public enum FindingLevel { Ok, Info, Warning, Error }

/// <summary>Что можно исправить кнопкой «Исправить».</summary>
public enum PrinterFix
{
    None,
    /// <summary>Открыть мастер «Подключить принтер».</summary>
    RunWizard,
    /// <summary>Создать очередь Windows на порту (FixArg — порт USB00x).</summary>
    CreateQueue,
    /// <summary>Удалить задания очереди.</summary>
    ClearJobs,
    /// <summary>Снять паузу и «Работать автономно».</summary>
    ResumeQueue,
    /// <summary>Перевести очередь на другой порт USB (FixArg — порт).</summary>
    MovePort,
    /// <summary>Открыть страницу драйверов Xprinter.</summary>
    OpenDriverPage,
}

/// <summary>Одна строка итога диагностики.</summary>
public sealed record DiagnosticFinding(FindingLevel Level, string Text, PrinterFix Fix = PrinterFix.None, string? FixArg = null);

/// <summary>2026-11-13 (владелец: Xprinter у клиентов, «принтер не печатает»): проверки по порядку — выбран ли принтер, есть ли очередь Windows, её состояние
/// (пауза, «автономно», ошибка, застрявшие задания), подключено ли USB-устройство порта (и не переехал ли принтер на другой разъём — новый порт USB00x),
/// отвечает ли сетевой принтер на IP:9100, COM/LPT есть ли в системе, и ответ самого принтера (нет бумаги, открыта крышка) — где есть двусторонняя связь.
/// Каждая строка — понятный текст на языке кассы и, где можно, кнопка «Исправить». Сама ничего не меняет.</summary>
public static class PrinterDiagnostics
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Состояние очереди (PRINTER_INFO_2.Status / Attributes)
    private const uint StPaused = 0x1, StError = 0x2, StPaperJam = 0x8, StPaperOut = 0x10, StOffline = 0x80, StNotAvailable = 0x1000,
        StUserIntervention = 0x100000, StDoorOpen = 0x400000;
    private const uint AttrWorkOffline = 0x400;

    public static async Task<IReadOnlyList<DiagnosticFinding>> RunAsync(IPrinterSystem sys, string? devicePath, PrinterRole role, PrinterLanguage language, CancellationToken ct = default)
    {
        var list = new List<DiagnosticFinding>();
        var path = (devicePath ?? "").Trim();
        PosLogger.Log($"Диагностика принтера: начало ({(role == PrinterRole.Label ? "этикетки" : "чеки")}, «{path}»).", "PRINTER");
        if (path.Length == 0)
        {
            list.Add(new(FindingLevel.Error, T("Принтер не выбран. Нажмите «Подключить принтер» — мастер сам найдёт его.", "Принтер тандалган эмес. «Принтерди туташтыруу» баскычын басыңыз — чебер аны өзү табат.",
                "No printer is selected. Press “Connect printer” and the wizard will find it.", "Yazıcı seçilmedi. «Yazıcıyı bağla» düğmesine basın — sihirbaz onu kendisi bulur.", "Printer tanlanmagan. «Printerni ulash» tugmasini bosing — usta uni o'zi topadi."), PrinterFix.RunWizard));
            return Finish(list);
        }

        var queues = sys.ListQueues();
        var usb = sys.ListUsbPrinters();
        string? statusPath = null;

        if (NetworkPrinterAddress.IsNetworkPort(path))
        {
            var probe = sys.Probe(path);
            var shown = NetworkPrinterAddress.Display(path);
            if (probe.IsAvailable)
            {
                list.Add(new(FindingLevel.Ok, T($"Сетевой принтер {shown} отвечает.", $"Тармактык принтер {shown} жооп берет.", $"Network printer {shown} responds.", $"Ağ yazıcısı {shown} yanıt veriyor.", $"Tarmoq printeri {shown} javob bermoqda.")));
                statusPath = path;
            }
            else
                list.Add(new(FindingLevel.Error, T($"Сетевой принтер {shown} не отвечает: проверьте питание, сетевой кабель и что адрес не сменился (напечатайте на принтере страницу настроек — там IP). Мастер может поискать принтер в сети.",
                    $"Тармактык принтер {shown} жооп бербейт: кубатты, тармак кабелин жана дарек өзгөрбөгөнүн текшериңиз (принтерден жөндөөлөр барагын басыңыз — анда IP бар). Чебер принтерди тармактан издей алат.",
                    $"Network printer {shown} does not respond: check power, the network cable and that the address has not changed (print the printer's settings page — it shows the IP). The wizard can search the network.",
                    $"Ağ yazıcısı {shown} yanıt vermiyor: gücü, ağ kablosunu ve adresin değişmediğini kontrol edin (yazıcıdan ayar sayfası yazdırın — IP orada). Sihirbaz ağda arayabilir.",
                    $"Tarmoq printeri {shown} javob bermayapti: quvvatni, tarmoq kabelini va manzil o'zgarmaganini tekshiring (printerdan sozlamalar sahifasini chop eting — u yerda IP bor). Usta tarmoqdan qidira oladi."), PrinterFix.RunWizard));
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var device = usb.FirstOrDefault(d => string.Equals(d.InterfacePath, path, StringComparison.OrdinalIgnoreCase));
            if (device is null)
                list.Add(new(FindingLevel.Error, UsbMissingText(null), PrinterFix.RunWizard));
            else
            {
                list.Add(new(FindingLevel.Ok, UsbPresentText(device)));
                statusPath = path;
            }
        }
        else if (HardwarePortHelper.LooksLikeComPort(path) || HardwarePortHelper.LooksLikeLptPort(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                 || !OperatingSystem.IsWindows() && queues.Count == 0)
        {
            var probe = sys.Probe(path);
            list.Add(probe.IsAvailable
                ? new DiagnosticFinding(FindingLevel.Ok, T($"Порт {path}: {probe.Message.TrimStart('●', ' ')}", $"Порт {path}: {probe.Message.TrimStart('●', ' ')}", $"Port {path}: {probe.Message.TrimStart('●', ' ')}", $"Port {path}: {probe.Message.TrimStart('●', ' ')}", $"Port {path}: {probe.Message.TrimStart('●', ' ')}"))
                : new DiagnosticFinding(FindingLevel.Error, T($"Порт {path} не найден: {probe.Message.TrimStart('○', ' ')}. Если принтер подключён по USB — нажмите «Подключить принтер».",
                    $"{path} порту табылган жок: {probe.Message.TrimStart('○', ' ')}. Принтер USB аркылуу туташса — «Принтерди туташтыруу» баскычын басыңыз.",
                    $"Port {path} not found: {probe.Message.TrimStart('○', ' ')}. If the printer is connected by USB, press “Connect printer”.",
                    $"{path} portu bulunamadı: {probe.Message.TrimStart('○', ' ')}. Yazıcı USB ile bağlıysa «Yazıcıyı bağla»ya basın.",
                    $"{path} porti topilmadi: {probe.Message.TrimStart('○', ' ')}. Printer USB orqali ulangan bo'lsa — «Printerni ulash» tugmasini bosing."), PrinterFix.RunWizard));
        }
        else
        {
            // очередь Windows
            var q = queues.FirstOrDefault(x => string.Equals(x.Name, path, StringComparison.OrdinalIgnoreCase));
            if (q is null)
            {
                list.Add(new(FindingLevel.Error, T($"Принтер «{path}» не найден в Windows (удалён или переименован). Нажмите «Подключить принтер».",
                    $"«{path}» принтери Windows'тон табылган жок (өчүрүлгөн же аты өзгөргөн). «Принтерди туташтыруу» баскычын басыңыз.",
                    $"Printer “{path}” was not found in Windows (removed or renamed). Press “Connect printer”.",
                    $"«{path}» yazıcısı Windows'ta bulunamadı (silinmiş veya adı değişmiş). «Yazıcıyı bağla»ya basın.",
                    $"«{path}» printeri Windows'da topilmadi (o'chirilgan yoki nomi o'zgargan). «Printerni ulash» tugmasini bosing."), PrinterFix.RunWizard));
                AddFreeUsb(list, queues, usb);
                return Finish(list);
            }
            list.Add(new(FindingLevel.Ok, T($"Принтер «{q.Name}» есть в Windows (драйвер «{q.DriverName}», порт {q.PortName}).", $"«{q.Name}» принтери Windows'то бар (драйвер «{q.DriverName}», порт {q.PortName}).",
                $"Printer “{q.Name}” exists in Windows (driver “{q.DriverName}”, port {q.PortName}).", $"«{q.Name}» yazıcısı Windows'ta var (sürücü «{q.DriverName}», port {q.PortName}).", $"«{q.Name}» printeri Windows'da bor (drayver «{q.DriverName}», port {q.PortName}).")));
            AddQueueState(list, q);

            var ports = PrinterInventoryBuilder.SplitPorts(q.PortName);
            if (ports.Any(p => p.StartsWith("USB", StringComparison.OrdinalIgnoreCase)))
            {
                var device = usb.FirstOrDefault(d => d.PortName is { } p && ports.Contains(p, StringComparer.OrdinalIgnoreCase));
                if (device is not null)
                {
                    list.Add(new(FindingLevel.Ok, UsbPresentText(device)));
                    if (q.Jobs == 0)
                        statusPath = device.InterfacePath;
                }
                else
                {
                    // принтер не подключён — или подключён в другой разъём (новый порт), а очередь осталась на старом
                    var moved = usb.Where(d => d.PortName is not null && !queues.Any(o => PrinterInventoryBuilder.SplitPorts(o.PortName).Contains(d.PortName!, StringComparer.OrdinalIgnoreCase))).ToList();
                    if (moved.Count == 1)
                        list.Add(new(FindingLevel.Error, T($"Принтер подключён к другому разъёму USB: Windows видит его на порту {moved[0].PortName}, а очередь «{q.Name}» ждёт его на {q.PortName}. «Исправить» — переключить очередь на {moved[0].PortName}.",
                            $"Принтер башка USB уячасына туташкан: Windows аны {moved[0].PortName} портунда көрөт, ал эми «{q.Name}» кезеги {q.PortName} портунда күтөт. «Оңдоо» — кезекти {moved[0].PortName} портуна которуу.",
                            $"The printer is plugged into another USB socket: Windows sees it on port {moved[0].PortName}, but queue “{q.Name}” expects it on {q.PortName}. “Fix” switches the queue to {moved[0].PortName}.",
                            $"Yazıcı başka bir USB yuvasına takılı: Windows onu {moved[0].PortName} portunda görüyor, «{q.Name}» kuyruğu ise {q.PortName} portunda bekliyor. «Düzelt» kuyruğu {moved[0].PortName} portuna geçirir.",
                            $"Printer boshqa USB uyasiga ulangan: Windows uni {moved[0].PortName} portida ko'rmoqda, «{q.Name}» navbati esa {q.PortName} portida kutmoqda. «Tuzatish» — navbatni {moved[0].PortName} portiga o'tkazish."), PrinterFix.MovePort, moved[0].PortName));
                    else
                        list.Add(new(FindingLevel.Error, UsbMissingText(q.PortName)));
                }
            }
        }

        if (statusPath is not null)
        {
            var status = await sys.QueryStatusAsync(statusPath, language, ct).ConfigureAwait(false);
            list.AddRange(DescribeStatus(status));
        }
        return Finish(list);
    }

    /// <summary>Состояние очереди Windows: пауза, «автономно», ошибка, нет бумаги, задания в очереди.</summary>
    internal static void AddQueueState(List<DiagnosticFinding> list, WindowsQueueInfo q)
    {
        if ((q.Status & StPaused) != 0)
            list.Add(new(FindingLevel.Error, T("Очередь принтера в Windows стоит на паузе — чеки копятся и не печатаются.", "Windows'тогу принтер кезеги тыныгууда — чектер топтолуп, басылбайт.",
                "The printer queue in Windows is paused — receipts pile up and do not print.", "Windows'taki yazıcı kuyruğu duraklatılmış — fişler birikir ve yazdırılmaz.", "Windows'dagi printer navbati pauzada — cheklar to'planib, chop etilmaydi."), PrinterFix.ResumeQueue, q.Name));
        if ((q.Attributes & AttrWorkOffline) != 0 || (q.Status & (StOffline | StNotAvailable)) != 0)
            list.Add(new(FindingLevel.Error, T("Windows считает принтер выключенным («Работать автономно» или «Не в сети»). Включите принтер и нажмите «Исправить».",
                "Windows принтерди өчүк деп эсептейт («Автономдуу иштөө» же «Тармакта эмес»). Принтерди күйгүзүп, «Оңдоо» баскычын басыңыз.",
                "Windows thinks the printer is off (“Use printer offline” or “Offline”). Turn the printer on and press “Fix”.",
                "Windows yazıcıyı kapalı sanıyor («Çevrimdışı kullan» veya «Çevrimdışı»). Yazıcıyı açın ve «Düzelt»e basın.",
                "Windows printerni o'chiq deb hisoblaydi («Avtonom ishlash» yoki «Tarmoqda emas»). Printerni yoqing va «Tuzatish» tugmasini bosing."), PrinterFix.ResumeQueue, q.Name));
        if ((q.Status & StPaperOut) != 0)
            list.Add(new(FindingLevel.Error, PaperOutText()));
        if ((q.Status & StDoorOpen) != 0)
            list.Add(new(FindingLevel.Error, CoverOpenText()));
        if ((q.Status & (StError | StPaperJam | StUserIntervention)) != 0)
            list.Add(new(FindingLevel.Error, T("Windows сообщает об ошибке принтера: проверьте бумагу и крышку, выключите и включите принтер.", "Windows принтер катасын билдирет: кагазды жана капкакты текшерип, принтерди өчүрүп-күйгүзүңүз.",
                "Windows reports a printer error: check the paper and the cover, then turn the printer off and on.", "Windows yazıcı hatası bildiriyor: kağıdı ve kapağı kontrol edin, yazıcıyı kapatıp açın.", "Windows printer xatosini bildirmoqda: qog'oz va qopqoqni tekshiring, printerni o'chirib-yoqing.")));
        if (q.Jobs > 0)
            list.Add(new(FindingLevel.Warning, T($"Заданий в очереди: {q.Jobs} — скорее всего, они застряли и держат очередь. «Исправить» — удалить их (напечатанные чеки не пропадут из кассы).",
                $"Кезекте {q.Jobs} тапшырма бар — балким, тыгылып, кезекти кармап турат. «Оңдоо» — аларды өчүрүү (басылган чектер кассадан жоголбойт).",
                $"There are {q.Jobs} job(s) in the queue — they are probably stuck and blocking it. “Fix” deletes them (receipts stay in the cash register).",
                $"Kuyrukta {q.Jobs} iş var — büyük olasılıkla takıldılar ve kuyruğu tutuyorlar. «Düzelt» onları siler (fişler kasadan kaybolmaz).",
                $"Navbatda {q.Jobs} ta vazifa bor — ehtimol, tiqilib, navbatni ushlab turibdi. «Tuzatish» — ularni o'chirish (cheklar kassadan yo'qolmaydi)."), PrinterFix.ClearJobs, q.Name));
    }

    /// <summary>Ответ принтера → строки итога. null — ответа нет (через очередь Windows это нормально).</summary>
    public static IReadOnlyList<DiagnosticFinding> DescribeStatus(PrinterStatusReport? s)
    {
        if (s is null)
            return [new(FindingLevel.Info, T("Принтер не сообщил своё состояние (не все модели это умеют) — это не ошибка.", "Принтер өз абалын билдирген жок (бардык моделдер муну жасай албайт) — бул ката эмес.",
                "The printer did not report its state (not all models can) — this is not an error.", "Yazıcı durumunu bildirmedi (her model yapamaz) — bu bir hata değil.", "Printer o'z holatini bildirmadi (hamma modellar buni qila olmaydi) — bu xato emas."))];
        var list = new List<DiagnosticFinding>();
        if (s.PaperOut)
            list.Add(new(FindingLevel.Error, PaperOutText()));
        if (s.CoverOpen)
            list.Add(new(FindingLevel.Error, CoverOpenText()));
        if (s.CutterError)
            list.Add(new(FindingLevel.Error, T("Ошибка ножа (обрезчика): выключите принтер, откройте крышку и уберите застрявшую бумагу.", "Бычак катасы: принтерди өчүрүп, капкагын ачып, тыгылган кагазды алып салыңыз.",
                "Cutter error: turn the printer off, open the cover and remove the jammed paper.", "Bıçak hatası: yazıcıyı kapatın, kapağı açın ve sıkışan kağıdı çıkarın.", "Pichoq xatosi: printerni o'chiring, qopqog'ini oching va tiqilgan qog'ozni oling.")));
        if (s.Paused)
            list.Add(new(FindingLevel.Error, T("Принтер на паузе: нажмите на нём кнопку FEED/PAUSE.", "Принтер тыныгууда: андагы FEED/PAUSE баскычын басыңыз.", "The printer is paused: press its FEED/PAUSE button.", "Yazıcı duraklatıldı: üzerindeki FEED/PAUSE düğmesine basın.", "Printer pauzada: undagi FEED/PAUSE tugmasini bosing.")));
        if ((s.Error || s.Offline) && !s.PaperOut && !s.CoverOpen && !s.CutterError)
            list.Add(new(FindingLevel.Error, T("Принтер сообщает об ошибке: выключите его на 10 секунд и включите снова.", "Принтер ката билдирет: аны 10 секундга өчүрүп, кайра күйгүзүңүз.",
                "The printer reports an error: turn it off for 10 seconds and on again.", "Yazıcı hata bildiriyor: 10 saniye kapatıp yeniden açın.", "Printer xato bildirmoqda: uni 10 soniyaga o'chirib, qayta yoqing.")));
        if (s.PaperNearEnd && !s.PaperOut)
            list.Add(new(FindingLevel.Warning, T("Бумага заканчивается — приготовьте новый рулон.", "Кагаз түгөнүп баратат — жаңы түрмөк даярдаңыз.", "Paper is running low — get a new roll ready.", "Kağıt bitmek üzere — yeni rulo hazırlayın.", "Qog'oz tugayapti — yangi rulon tayyorlang.")));
        if (list.Count == 0)
            list.Add(new(FindingLevel.Ok, T("Принтер ответил: готов к печати.", "Принтер жооп берди: басууга даяр.", "The printer answered: ready to print.", "Yazıcı yanıt verdi: yazdırmaya hazır.", "Printer javob berdi: chop etishga tayyor.")));
        return list;
    }

    /// <summary>USB-принтеры без очереди — подсказка «создать очередь».</summary>
    private static void AddFreeUsb(List<DiagnosticFinding> list, IReadOnlyList<WindowsQueueInfo> queues, IReadOnlyList<UsbPrinterDevice> usb)
    {
        foreach (var d in usb.Where(d => d.PortName is not null && !queues.Any(q => PrinterInventoryBuilder.SplitPorts(q.PortName).Contains(d.PortName!, StringComparer.OrdinalIgnoreCase))))
        {
            var name = DeviceName(d);
            list.Add(new(FindingLevel.Info, T($"Найден USB-принтер {name} без очереди Windows (порт {d.PortName}). «Исправить» — открыть мастер и подключить его.",
                $"Windows кезеги жок USB-принтер {name} табылды (порт {d.PortName}). «Оңдоо» — чеберди ачып, аны туташтыруу.",
                $"Found USB printer {name} without a Windows queue (port {d.PortName}). “Fix” opens the wizard to connect it.",
                $"Windows kuyruğu olmayan USB yazıcı {name} bulundu (port {d.PortName}). «Düzelt» sihirbazı açıp bağlar.",
                $"Windows navbatisiz USB-printer {name} topildi (port {d.PortName}). «Tuzatish» — ustani ochib, uni ulash."), PrinterFix.RunWizard));
        }
    }

    private static string DeviceName(UsbPrinterDevice d)
    {
        var brand = PrinterUsbCatalog.GuessBrand(d.Vid, d.Manufacturer, d.Model);
        var model = string.IsNullOrWhiteSpace(d.Model) ? PrinterUsbCatalog.VidPidText(d.Vid, d.Pid) : d.Model!;
        var b = PrinterUsbCatalog.BrandText(brand);
        return b.Length > 0 && !model.StartsWith(brand.Brand ?? "~", StringComparison.OrdinalIgnoreCase) ? $"{model} ({b})" : model;
    }

    private static string UsbPresentText(UsbPrinterDevice d) => T($"USB-принтер подключён: {DeviceName(d)}.", $"USB-принтер туташкан: {DeviceName(d)}.", $"USB printer connected: {DeviceName(d)}.", $"USB yazıcı bağlı: {DeviceName(d)}.", $"USB-printer ulangan: {DeviceName(d)}.");

    private static string UsbMissingText(string? port) => port is null
        ? T("USB-принтер не подключён: проверьте кабель USB и питание (лампочка на принтере), попробуйте другой разъём USB.", "USB-принтер туташкан эмес: USB кабелин жана кубатты (принтердеги лампочка) текшериңиз, башка USB уячасын байкап көрүңүз.",
            "The USB printer is not connected: check the USB cable and power (the light on the printer), try another USB socket.", "USB yazıcı bağlı değil: USB kablosunu ve gücü (yazıcıdaki ışık) kontrol edin, başka bir USB yuvası deneyin.", "USB-printer ulanmagan: USB kabeli va quvvatni (printerdagi chiroq) tekshiring, boshqa USB uyasini sinab ko'ring.")
        : T($"Принтер на порту {port} не подключён: проверьте кабель USB и питание (лампочка на принтере), попробуйте другой разъём USB.", $"{port} портундагы принтер туташкан эмес: USB кабелин жана кубатты (принтердеги лампочка) текшериңиз, башка USB уячасын байкап көрүңүз.",
            $"The printer on port {port} is not connected: check the USB cable and power (the light on the printer), try another USB socket.", $"{port} portundaki yazıcı bağlı değil: USB kablosunu ve gücü (yazıcıdaki ışık) kontrol edin, başka bir USB yuvası deneyin.", $"{port} portidagi printer ulanmagan: USB kabeli va quvvatni (printerdagi chiroq) tekshiring, boshqa USB uyasini sinab ko'ring.");

    private static string PaperOutText() => T("Нет бумаги: вставьте новый рулон (термослоем к головке) и закройте крышку.", "Кагаз жок: жаңы түрмөк салып (термо катмары башка карай), капкагын жабыңыз.",
        "Out of paper: insert a new roll (thermal side to the head) and close the cover.", "Kağıt yok: yeni rulo takın (termal yüz kafaya doğru) ve kapağı kapatın.", "Qog'oz yo'q: yangi rulon soling (termo qatlami boshchaga qaratib) va qopqoqni yoping.");

    private static string CoverOpenText() => T("Открыта крышка принтера: закройте её до щелчка.", "Принтердин капкагы ачык: аны чыкылдаганга чейин жабыңыз.", "The printer cover is open: close it until it clicks.", "Yazıcının kapağı açık: tık sesi gelene kadar kapatın.", "Printer qopqog'i ochiq: uni chiqillagunicha yoping.");

    private static IReadOnlyList<DiagnosticFinding> Finish(List<DiagnosticFinding> list)
    {
        if (!list.Any(f => f.Level is FindingLevel.Error or FindingLevel.Warning))
            list.Add(new(FindingLevel.Info, T("Явных проблем не найдено. Сделайте пробную печать; если печатаются «иероглифы» — выберите другую кодировку в мастере. Для печати из других программ (Word, браузер) поставьте драйвер производителя.",
                "Ачык көйгөй табылган жок. Сыноо басып чыгарыңыз; «иероглифтер» чыкса — чеберден башка коддоону тандаңыз. Башка программалардан (Word, браузер) басуу үчүн өндүрүүчүнүн драйверин коюңуз.",
                "No obvious problems found. Make a test print; if you get “hieroglyphs”, choose another encoding in the wizard. To print from other programs (Word, browser), install the manufacturer's driver.",
                "Belirgin bir sorun bulunamadı. Deneme baskısı yapın; «hiyeroglif» çıkarsa sihirbazda başka bir kodlama seçin. Diğer programlardan (Word, tarayıcı) yazdırmak için üretici sürücüsünü kurun.",
                "Aniq muammo topilmadi. Sinov chop eting; «ierogliflar» chiqsa — ustada boshqa kodlashni tanlang. Boshqa dasturlardan (Word, brauzer) chop etish uchun ishlab chiqaruvchi drayverini o'rnating."), PrinterFix.OpenDriverPage));
        PosLogger.Log("Диагностика принтера: " + string.Join(" | ", list.Select(f => f.Level + (f.Fix != PrinterFix.None ? "/" + f.Fix : ""))), "PRINTER");
        return list;
    }
}
