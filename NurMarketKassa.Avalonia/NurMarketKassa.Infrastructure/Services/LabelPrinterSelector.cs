using System.Collections.Concurrent;

namespace NurMarketKassa.Services;

// 2026-11-01 (просьба владельца: «2 принтера разной модели одновременно, штрихкоды сразу на выбранный принтер»):
// один общий способ ответить «на какой принтер печатать этикетки/штрихкоды/ценники». Раньше каждое окно решало само:
// склад брал сохранённый LabelPrinterDevicePath, окна ценников — первый принтер из списка (часто это принтер чеков).

/// <summary>Состояние выбранного принтера этикеток.</summary>
public enum LabelPrinterState
{
    /// <summary>Принтер выбран и найден — печать идёт на него сразу.</summary>
    Ready,
    /// <summary>Принтер этикеток ещё не выбран (Настройки → Печать → «Принтер этикеток и штрихкодов»).</summary>
    NotSelected,
    /// <summary>Принтер выбран, но сейчас не найден (выключен, USB вынут, удалён из Windows).</summary>
    NotFound,
}

public sealed record LabelPrinterResolution(LabelPrinterState State, DiscoveredPrinter? Printer)
{
    public bool IsReady => State == LabelPrinterState.Ready && Printer is not null;
}

/// <summary>Принтер этикеток — отдельный от принтера чеков (<see cref="UserPreferences.ReceiptDevicePath"/>).
/// Очередь заданий — своя на каждый принтер этикеток; общих с чеками замков нет (чеки: ReceiptPrintService.PrinterGate).</summary>
public static class LabelPrinterSelector
{
    /// <summary>Только принтеры, на которые имеет смысл печатать этикетки: без PDF/XPS/OneNote/факса.</summary>
    public static IReadOnlyList<DiscoveredPrinter> FilterChoices(IEnumerable<DiscoveredPrinter> all) =>
        all.Where(p => !p.IsVirtual).ToList();

    /// <summary>Список для выбора принтера этикеток (медленный — перебор USB/LPT/COM, звать не с потока интерфейса).</summary>
    public static IReadOnlyList<DiscoveredPrinter> GetChoices()
    {
        var list = FilterChoices(BarcodeLabelService.GetAvailablePrinters());
        _lastChoices = list;
        return list;
    }

    private static volatile IReadOnlyList<DiscoveredPrinter>? _lastChoices;

    /// <summary>Понятное имя принтера для сообщения (по последнему найденному списку); если не нашли — сам путь.</summary>
    public static string DisplayName(string? devicePath)
    {
        var found = _lastChoices?.FirstOrDefault(p => SamePrinter(p.DevicePath, devicePath))?.Label;
        if (found is not null)
            return found;
        // Список ещё не загружали: для порта/USB — «тип (адрес)», для очереди Windows — имя как есть.
        var kind = DescribeKind(devicePath);
        return BarcodeLabelService.IsRawDevicePath(devicePath ?? "") && kind.Length > 0 ? $"{kind} ({devicePath})" : devicePath ?? "";
    }

    // 2026-11-01 (проверка массовой печати, С-4): принтер для листов A4 по умолчанию — сохранённый, затем принтер Windows «по умолчанию»,
    // затем первый настоящий. Не чековый, не этикеточный, не факс/OneNote (PDF/XPS остаются в списке для ручного выбора).
    private static readonly string[] NeverDefaultMarkers = ["fax", "onenote", "send to"];

    public static DiscoveredPrinter? PickA4Default(IReadOnlyList<DiscoveredPrinter> plain, string? savedPath)
    {
        bool Free(DiscoveredPrinter p) => !IsSameAsReceiptPrinter(p.DevicePath) && !SamePrinter(p.DevicePath, UserPreferences.Instance.LabelPrinterDevicePath);
        bool Allowed(DiscoveredPrinter p) => Free(p) && !NeverDefaultMarkers.Any(m => p.Label.Contains(m, StringComparison.OrdinalIgnoreCase));

        var saved = string.IsNullOrWhiteSpace(savedPath) ? null : plain.FirstOrDefault(p => SamePrinter(p.DevicePath, savedPath));
        if (saved is not null)
            return saved;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var def = new System.Drawing.Printing.PrinterSettings().PrinterName;
                var byDefault = plain.FirstOrDefault(p => SamePrinter(p.DevicePath, def));
                if (byDefault is not null && Allowed(byDefault) && !byDefault.IsVirtual)
                    return byDefault;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Default Windows printer not read: {ex.GetType().Name}", "WARNING");
        }
        return plain.FirstOrDefault(p => Allowed(p) && !p.IsVirtual) ?? plain.FirstOrDefault(Allowed);
    }

    /// <summary>Очередь Windows «Generic / Text Only» принимает только текст: картинку этикетки она напечатает пустой или мусором.</summary>
    public static bool LooksLikeTextOnlyQueue(string? printerLabelOrName) =>
        !string.IsNullOrWhiteSpace(printerLabelOrName)
        && (printerLabelOrName.Contains("text only", StringComparison.OrdinalIgnoreCase)
            || printerLabelOrName.Contains("generic / text", StringComparison.OrdinalIgnoreCase));

    public static string TextOnlyWarning => Tr.T(
        "Этот принтер установлен в Windows как «Generic / Text Only» — он принимает только текст, этикетка (картинка со штрихкодом) выйдет пустой или мусором. Поставьте драйвер производителя или подключите принтер напрямую (USB без драйвера / WinUSB / COM) — тогда этикетка печатается командами принтера.",
        "Бул принтер Windows'то «Generic / Text Only» катары орнотулган — ал текстти гана кабыл алат, этикетка (штрих-коддуу сүрөт) бош же бузулуп чыгат. Өндүрүүчүнүн драйверин коюңуз же принтерди түз туташтырыңыз (драйверсиз USB / WinUSB / COM) — анда этикетка принтердин буйруктары менен басылат.",
        "This printer is installed in Windows as “Generic / Text Only” — it accepts text only, so the label (an image with a barcode) will come out blank or as garbage. Install the manufacturer's driver or connect the printer directly (USB without driver / WinUSB / COM) — then the label is printed with printer commands.",
        "Bu yazıcı Windows'ta «Generic / Text Only» olarak kurulmuş — yalnızca metin kabul eder, etiket (barkodlu resim) boş veya anlamsız çıkar. Üretici sürücüsünü kurun veya yazıcıyı doğrudan bağlayın (sürücüsüz USB / WinUSB / COM) — o zaman etiket yazıcı komutlarıyla basılır.",
        "Bu printer Windows'da «Generic / Text Only» sifatida o'rnatilgan — u faqat matnni qabul qiladi, yorliq (shtrix-kodli rasm) bo'sh yoki bema'ni chiqadi. Ishlab chiqaruvchi drayverini o'rnating yoki printerni to'g'ridan-to'g'ri ulang (drayversiz USB / WinUSB / COM) — shunda yorliq printer buyruqlari bilan chop etiladi.");

    // 2026-11-01 (С-9): печать этикеток и листов использует System.Drawing — работает только в Windows.
    public static bool PrintingSupported => OperatingSystem.IsWindows();

    public static string UnsupportedText => Tr.T(
        "Печать этикеток, штрихкодов и ценников пока работает только в Windows. Предпросмотр доступен, печатать здесь нельзя.",
        "Этикеткаларды, штрих-коддорду жана баа белгилерин басып чыгаруу азырынча Windows'то гана иштейт. Алдын ала көрүү бар, бирок бул жерден басууга болбойт.",
        "Printing of labels, barcodes and price tags works only in Windows for now. Preview is available, but printing is not possible here.",
        "Etiket, barkod ve fiyat etiketi yazdırma şimdilik yalnızca Windows'ta çalışır. Önizleme var, ancak burada yazdırılamaz.",
        "Yorliq, shtrix-kod va narx yorlig'ini chop etish hozircha faqat Windows'da ishlaydi. Oldindan ko'rish mavjud, lekin bu yerda chop etib bo'lmaydi.");

    // 2026-11-01 (С-5): подтверждение для больших пачек.
    public const int BigBatchLabels = 1000;

    /// <summary>Вопрос «Напечатать N этикеток?» с оценкой ленты (длина этикетки + зазор) или листов A4. Пустая строка — подтверждать не нужно.</summary>
    public static string BigBatchQuestion(int labels, double labelHeightMm, double gapMm, int sheets, string printerLabel)
    {
        if (labels <= BigBatchLabels)
            return "";
        var meters = labels * (Math.Max(labelHeightMm, 0) + Math.Max(gapMm, 0)) / 1000.0;
        var sheetPart = sheets > 0
            ? Tr.T($"Листов A4: {sheets}.", $"A4 барактары: {sheets}.", $"A4 sheets: {sheets}.", $"A4 sayfa: {sheets}.", $"A4 varaqlar: {sheets}.")
            : Tr.T($"Ленты примерно {meters:0.#} м.", $"Лента болжол менен {meters:0.#} м.", $"About {meters:0.#} m of tape.", $"Yaklaşık {meters:0.#} m şerit.", $"Taxminan {meters:0.#} m lenta.");
        return Tr.T($"Напечатать {labels} этикеток на «{printerLabel}»? {sheetPart} Остановить такую печать будет долго.",
            $"«{printerLabel}» принтерине {labels} этикетка басылсынбы? {sheetPart} Мындай басууну токтотуу узакка созулат.",
            $"Print {labels} labels on “{printerLabel}”? {sheetPart} Stopping such a job takes a long time.",
            $"«{printerLabel}» yazıcısında {labels} etiket yazdırılsın mı? {sheetPart} Böyle bir işi durdurmak uzun sürer.",
            $"«{printerLabel}» printerida {labels} ta yorliq chop etilsinmi? {sheetPart} Bunday ishni to'xtatish uzoq davom etadi.");
    }

    /// <summary>Подсказка, когда принтер этикеток недоступен, а список принтеров заново не перебирали: не выбран или не найден.</summary>
    public static string MissingHint() => HintText(string.IsNullOrWhiteSpace(UserPreferences.Instance.LabelPrinterDevicePath)
        ? LabelPrinterState.NotSelected
        : LabelPrinterState.NotFound);

    /// <summary>Выбранный принтер этикеток среди <paramref name="choices"/> (или среди свежего списка, если не передан).</summary>
    public static LabelPrinterResolution Resolve(IEnumerable<DiscoveredPrinter>? choices = null)
    {
        var saved = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(saved))
            return new LabelPrinterResolution(LabelPrinterState.NotSelected, null);
        var found = (choices is null ? GetChoices() : FilterChoices(choices)).FirstOrDefault(p => SamePrinter(p.DevicePath, saved));
        return found is null
            ? new LabelPrinterResolution(LabelPrinterState.NotFound, null)
            : new LabelPrinterResolution(LabelPrinterState.Ready, found);
    }

    /// <summary>2026-11-01 (ревью): выбранный принтер без полного перебора устройств — для печати «одной кнопкой» (20 этикеток подряд = 20 кликов).
    /// Сетевой адрес не требует перебора; остальные берутся из последнего найденного списка; перебор — только если там нет.</summary>
    public static LabelPrinterResolution ResolveFast()
    {
        var saved = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(saved))
            return new LabelPrinterResolution(LabelPrinterState.NotSelected, null);
        if (NetworkPrinterAddress.IsNetworkPort(saved) && NetworkPrinterAddress.TryParse(saved, out _, out _, out _))
            return new LabelPrinterResolution(LabelPrinterState.Ready, NetworkPrinterAddress.ToDiscovered(saved));
        var cached = _lastChoices?.FirstOrDefault(p => !p.IsVirtual && SamePrinter(p.DevicePath, saved));
        return cached is not null ? new LabelPrinterResolution(LabelPrinterState.Ready, cached) : Resolve();
    }

    /// <summary>Тот же физический порт/очередь (как ключ блокировки порта в PrinterPortService).</summary>
    public static bool SamePrinter(string? a, string? b)
    {
        var x = PrinterPortService.NormalizePort(a);
        var y = PrinterPortService.NormalizePort(b);
        return x.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Принтер этикеток совпал с принтером чеков — оба задания пойдут в один порт и будут ждать друг друга.</summary>
    public static bool IsSameAsReceiptPrinter(string? labelPrinterPath, string? receiptPrinterPath = null) =>
        SamePrinter(labelPrinterPath, receiptPrinterPath ?? UserPreferences.Instance.ReceiptDevicePath);

    /// <summary>Как подключён принтер — короткая подпись для настроек.</summary>
    public static string DescribeKind(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return "";
        if (NetworkPrinterAddress.IsNetworkPort(devicePath))
            return Tr.T($"сеть (LAN) {NetworkPrinterAddress.Display(devicePath)}", $"тармак (LAN) {NetworkPrinterAddress.Display(devicePath)}", $"network (LAN) {NetworkPrinterAddress.Display(devicePath)}", $"ağ (LAN) {NetworkPrinterAddress.Display(devicePath)}", $"tarmoq (LAN) {NetworkPrinterAddress.Display(devicePath)}");
        if (WinUsbPrinterPort.IsWinUsbDevicePath(devicePath))
            return Tr.T("USB напрямую (WinUSB)", "USB түз (WinUSB)", "USB direct (WinUSB)", "USB doğrudan (WinUSB)", "USB to'g'ridan-to'g'ri (WinUSB)");
        if (UsbRawPrinterPort.IsRawPortLabel(devicePath) || devicePath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return Tr.T("USB без драйвера", "USB драйверсиз", "USB without driver", "USB sürücüsüz", "USB drayversiz");
        if (HardwarePortHelper.LooksLikeLptPort(devicePath))
            return Tr.T("параллельный порт (LPT)", "параллелдүү порт (LPT)", "parallel port (LPT)", "paralel port (LPT)", "parallel port (LPT)");
        if (HardwarePortHelper.LooksLikeComPort(devicePath))
            return Tr.T("последовательный порт (COM)", "ырааттуу порт (COM)", "serial port (COM)", "seri port (COM)", "ketma-ket port (COM)");
        return Tr.T("принтер Windows (драйвер)", "Windows принтери (драйвер)", "Windows printer (driver)", "Windows yazıcısı (sürücü)", "Windows printeri (drayver)");
    }

    /// <summary>Каким способом на этот принтер уходит этикетка: через драйвер Windows или напрямую командами принтера.</summary>
    public static string DescribeLanguage(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return "";
        if (!BarcodeLabelService.IsRawDevicePath(devicePath))
            return Tr.T("печать через драйвер Windows (картинка по размеру этикетки)", "Windows драйвери аркылуу басуу (этикетка өлчөмүндөгү сүрөт)",
                "printed through the Windows driver (image sized to the label)", "Windows sürücüsü ile yazdırma (etiket boyutunda resim)", "Windows drayveri orqali chop etish (yorliq o'lchamidagi rasm)");
        return UserPreferences.Instance.LabelRawLanguage switch
        {
            "tspl" => Tr.T("команды TSPL (TSC, Xprinter, Gprinter)", "TSPL буйруктары (TSC, Xprinter, Gprinter)", "TSPL commands (TSC, Xprinter, Gprinter)", "TSPL komutları (TSC, Xprinter, Gprinter)", "TSPL buyruqlari (TSC, Xprinter, Gprinter)"),
            "escpos_gap" => Tr.T("ESC/POS с подачей по зазору", "ESC/POS, боштук боюнча берүү менен", "ESC/POS with gap feed", "ESC/POS, boşluğa göre besleme ile", "ESC/POS, tirqish bo'yicha uzatish bilan"),
            _ => Tr.T("ESC/POS (картинка)", "ESC/POS (сүрөт)", "ESC/POS (image)", "ESC/POS (resim)", "ESC/POS (rasm)"),
        };
    }

    // ---- 2026-11-01 (П-7): живой статус принтера этикеток ----

    public enum LiveStatus { Unknown, Ready, NoAnswer, Offline, PaperOut, Error, Busy }

    /// <summary>Идёт ли сейчас печать на этот принтер (тогда статус не опрашивается, чтобы не мешать).</summary>
    public static bool IsBusy(string? devicePath)
    {
        var key = PrinterPortService.NormalizePort(devicePath);
        return key.Length > 0 && Queues.TryGetValue(key, out var gate) && gate.CurrentCount == 0;
    }

    /// <summary>Состояние принтера без печати: порт/USB — отвечает ли (ProbePort), очередь Windows — offline / нет бумаги / ошибка.
    /// Не блокирует печать; вызывать не с потока интерфейса.</summary>
    public static LiveStatus CheckLive(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return LiveStatus.Unknown;
        if (IsBusy(devicePath))
            return LiveStatus.Busy;
        try
        {
            // 2026-11-01 (ревью): сеть — проба под замком порта без ожидания и не чаще раза в 10 с; идёт печать — «Печатает», без подключения.
            if (NetworkPrinterAddress.IsNetworkPort(devicePath))
            {
                var probe = PrinterPortService.ProbeNetworkIdle(devicePath, throttle: true);
                return probe is null ? LiveStatus.Busy : probe.IsAvailable ? LiveStatus.Ready : LiveStatus.NoAnswer;
            }
            if (BarcodeLabelService.IsRawDevicePath(devicePath))
            {
                var port = UsbRawPrinterPort.IsRawPortLabel(devicePath) ? UsbRawPrinterPort.ToDevicePath(devicePath) : devicePath;
                return PrinterPortService.ProbePort(port).IsAvailable ? LiveStatus.Ready : LiveStatus.NoAnswer;
            }
            return RawPrinterHelper.GetQueueState(devicePath) switch
            {
                RawPrinterHelper.QueueState.Ready => LiveStatus.Ready,
                RawPrinterHelper.QueueState.Offline => LiveStatus.Offline,
                RawPrinterHelper.QueueState.PaperOut => LiveStatus.PaperOut,
                RawPrinterHelper.QueueState.Error => LiveStatus.Error,
                _ => LiveStatus.Unknown,
            };
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label printer status failed: {ex.GetType().Name}", "DEBUG");
            return LiveStatus.Unknown;
        }
    }

    public static string LiveText(LiveStatus s) => s switch
    {
        LiveStatus.Ready => Tr.T("Готов", "Даяр", "Ready", "Hazır", "Tayyor"),
        LiveStatus.NoAnswer => Tr.T("Не отвечает", "Жооп бербейт", "Not responding", "Yanıt vermiyor", "Javob bermayapti"),
        LiveStatus.Offline => Tr.T("Принтер выключен", "Принтер өчүк", "Printer is off", "Yazıcı kapalı", "Printer o'chiq"),
        LiveStatus.PaperOut => Tr.T("Нет бумаги", "Кагаз жок", "Out of paper", "Kağıt yok", "Qog'oz yo'q"),
        LiveStatus.Error => Tr.T("Ошибка принтера", "Принтер катасы", "Printer error", "Yazıcı hatası", "Printer xatosi"),
        LiveStatus.Busy => Tr.T("Печатает", "Басып жатат", "Printing", "Yazdırıyor", "Chop etmoqda"),
        _ => Tr.T("Выбран (состояние неизвестно)", "Тандалды (абалы белгисиз)", "Selected (state unknown)", "Seçildi (durum bilinmiyor)", "Tanlangan (holati noma'lum)"),
    };

    // ---- очередь заданий принтера этикеток ----

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Queues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(90);

    /// <summary>Задание печати этикетки/ценника — по очереди на ЭТОТ принтер. Другой принтер (чеков или второй этикеточный)
    /// и чековая очередь не ждут и не блокируются; в один и тот же порт задания всё равно идут по одному (PrinterPortService).</summary>
    public static LabelPrintResult RunQueued(string? printerPath, Func<LabelPrintResult> job)
    {
        var key = PrinterPortService.NormalizePort(printerPath);
        if (key.Length == 0)
            return job();
        var gate = Queues.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(QueueWait))
        {
            PosLogger.Log($"Очередь принтера этикеток {key}: предыдущее задание не завершилось за {QueueWait.TotalSeconds:0} с.", "PRINTER");
            return LabelPrintResult.Failed;
        }
        try
        {
            return job();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Одна строка в журнал: что и куда ушло (без названий товаров и штрихкодов).</summary>
    public static void LogSent(DiscoveredPrinter? printer, string devicePath, int labels)
    {
        var name = printer?.Label ?? devicePath;
        PosLogger.Log($"Печать этикеток: {labels} шт. → принтер этикеток «{name}» ({DescribeKind(devicePath)}).", "PRINTER");
    }

    /// <summary>Строка для окна: «Отправлено на &lt;принтер&gt;».</summary>
    public static string SentText(string printerLabel) =>
        Tr.T($"Отправлено на {printerLabel}", $"{printerLabel} принтерине жөнөтүлдү", $"Sent to {printerLabel}", $"{printerLabel} yazıcısına gönderildi", $"{printerLabel} printeriga yuborildi");

    /// <summary>2026-11-02: часть этикеток ушла, исход остального неизвестен — повтор автоматически не делается.</summary>
    public static string PartialText(string? printerDevicePath = null)
    {
        // 2026-11-02 (С3-16): «N из M» именно этого принтера (при нескольких одновременных печатях общее поле показывало чужое)
        var p = printerDevicePath is { Length: > 0 } ? BarcodeLabelService.PartialFor(printerDevicePath) : BarcodeLabelService.LastPartial;
        var sent = p?.Sent ?? 0;
        var total = p?.Total ?? 0;
        return total > 0
            ? Tr.T($"Напечатано {sent} из {total}, проверьте ленту. Остальное автоматически не повторялось, чтобы не напечатать дубли.", $"{sent}/{total} басылды, лентаны текшериңиз. Калганы кайталанган жок — кайталанма болбошу үчүн.",
                $"Printed {sent} of {total}, check the tape. The rest was not retried automatically, to avoid duplicates.", $"{sent}/{total} basıldı, bandı kontrol edin. Kalanı kopya olmaması için otomatik tekrarlanmadı.", $"{sent}/{total} chop etildi, lentani tekshiring. Qolgani dublikat bo'lmasligi uchun avtomatik takrorlanmadi.")
            : Tr.T("Печать прервалась, часть этикеток могла напечататься — проверьте ленту. Автоповтора не было.", "Басуу үзгүлтүккө учурады, этикеткалардын бир бөлүгү басылышы мүмкүн — лентаны текшериңиз. Автоматтык кайталоо болгон жок.",
                "Printing was interrupted; some labels may have been printed — check the tape. No automatic retry was made.", "Yazdırma kesildi, bazı etiketler basılmış olabilir — bandı kontrol edin. Otomatik yeniden deneme yapılmadı.", "Chop etish uzildi, ba'zi yorliqlar chop etilgan bo'lishi mumkin — lentani tekshiring. Avtomatik takrorlash bo'lmadi.");
    }

    /// <summary>Подсказка, если принтер этикеток не выбран или не найден.</summary>
    public static string HintText(LabelPrinterState state) => state == LabelPrinterState.NotFound
        ? Tr.T("Выбранный принтер этикеток не найден — проверьте питание и кабель или выберите другой в Настройки → Печать.",
            "Тандалган этикетка принтери табылган жок — кубатты жана кабелди текшериңиз же Жөндөөлөр → Басып чыгаруу бөлүмүнөн башкасын тандаңыз.",
            "The selected label printer was not found — check power and cable, or choose another in Settings → Printing.",
            "Seçilen etiket yazıcısı bulunamadı — güç ve kabloyu kontrol edin veya Ayarlar → Yazdırma'dan başkasını seçin.",
            "Tanlangan yorliq printeri topilmadi — quvvat va kabelni tekshiring yoki Sozlamalar → Chop etish bo'limidan boshqasini tanlang.")
        : Tr.T("Принтер этикеток не выбран — выберите его один раз в Настройки → Печать → «Принтер этикеток и штрихкодов».",
            "Этикетка принтери тандалган эмес — аны Жөндөөлөр → Басып чыгаруу → «Этикетка жана штрих-код принтери» бөлүмүнөн бир жолу тандаңыз.",
            "No label printer selected — choose it once in Settings → Printing → “Label and barcode printer”.",
            "Etiket yazıcısı seçilmedi — Ayarlar → Yazdırma → «Etiket ve barkod yazıcısı» bölümünden bir kez seçin.",
            "Yorliq printeri tanlanmagan — uni Sozlamalar → Chop etish → «Yorliq va shtrix-kod printeri» bo'limida bir marta tanlang.");
}
