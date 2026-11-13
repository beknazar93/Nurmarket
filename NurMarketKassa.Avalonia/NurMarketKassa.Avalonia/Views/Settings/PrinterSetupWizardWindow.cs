using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Итог мастера: что настроено (для обновления страницы «Печать»).</summary>
public sealed record PrinterWizardResult(PrinterRole Role, string DevicePath);

/// <summary>2026-11-13 (владелец: «надо сделать готовыми драйверы для чековых принтеров и принтеров штрихкодов, в основном Xprinter у клиентов»):
/// мастер «Подключить принтер» без драйвера производителя. Шаги: 1 — тип (чековый / этикеток / определить автоматически); 2 — поиск (USB-принтеры и очереди
/// Windows с пометкой «вероятно Xprinter», COM/LPT, сеть — только по кнопке «Искать в сети»); 3 — подключение (нет очереди Windows — создать её на встроенном
/// драйвере «Generic / Text Only» с разрешением Windows, отказ — USB напрямую или драйвер производителя); 4 — параметры (лента 58/80 мм, ящик; размер этикетки,
/// зазор, язык TSPL/ESC/POS); 5 — пробная печать, выбор кодировки по ленте, «Напечаталось правильно? Да / Нет — что не так». Настройки меняются ТОЛЬКО кнопкой
/// «Сохранить» — в те же поля, что Настройки → Печать (если принтер уже настроен, мастер не обязателен). Все шаги — в журнал кассы.</summary>
public sealed partial class PrinterSetupWizardWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private const int Steps = 5;
    private readonly IPrinterSystem _sys;
    private readonly bool _askRole;
    private PrinterRole _role;
    private int _step = 1;
    private PrinterCandidate? _candidate;
    private string? _devicePath;
    private bool _connectionReady;
    private readonly List<string> _networkFound = new();
    private CancellationTokenSource? _scanCts;
    private bool _closed;

    // ---- подмены для тестов (настоящие принтеры не трогаются)
    /// <summary>Отправка байтов принтеру; null — настоящая печать (PrinterPortService).</summary>
    public Func<string, byte[], Task>? SendOverride { get; set; }
    /// <summary>Поиск в сети; null — настоящий (порт 9100 в подсети).</summary>
    public Func<IProgress<NetworkPrinterScanner.Progress>, CancellationToken, Task<IReadOnlyList<string>>>? NetworkScanOverride { get; set; }
    /// <summary>Создание очереди; null — настоящее (PowerShell с UAC).</summary>
    public Func<string, string, Task<ElevatedOutcome>>? CreateQueueOverride { get; set; }
    /// <summary>Сохранять настройки на диск (тесты — false).</summary>
    public bool PersistSettings { get; set; } = true;

    private readonly ModalShell _shell;
    private readonly ContentControl _body = new();
    private readonly TextBlock _stepText = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold };
    private readonly Button _back, _next, _cancel;

    public PrinterSetupWizardWindow(PrinterRole role, IPrinterSystem? system = null)
    {
        _sys = system ?? PrinterSystem.Instance;
        _role = role;
        _askRole = role == PrinterRole.Auto;
        Title = WizardTitle;
        Width = 672;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        _stepText.Bind(TextBlock.ForegroundProperty, _stepText.GetResourceObservable("BrushTextSoft"));

        _cancel = new Button { Name = "WizardCancel", Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        _cancel.Classes.Add("modal-cancel");
        _cancel.Click += (_, _) => Finish(null);
        _back = new Button { Name = "WizardBack", Content = T("Назад", "Артка", "Back", "Geri", "Orqaga") };
        _back.Classes.Add("modal-cancel");
        _back.Click += (_, _) => Go(_step - 1);
        _next = new Button { Name = "WizardNext", Content = NextText, IsDefault = true };
        _next.Classes.Add("modal-primary");
        _next.Click += (_, _) => OnNext();

        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = WizardTitle,
            Subtitle = T("Без драйвера производителя — Xprinter и другие ESC/POS / TSPL", "Өндүрүүчүнүн драйверисиз — Xprinter жана башка ESC/POS / TSPL",
                "No manufacturer driver needed — Xprinter and other ESC/POS / TSPL", "Üretici sürücüsü olmadan — Xprinter ve diğer ESC/POS / TSPL", "Ishlab chiqaruvchi drayverisiz — Xprinter va boshqa ESC/POS / TSPL"),
            Hint = _stepText,
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _back, _next } },
            Content = _body,
        };
        Content = _shell;
        BuildStep1();
        Closed += (_, _) => _scanCts?.Cancel();
        PosLogger.Log($"[UI] Мастер принтера: открыт ({RoleLog(role)}).", "PRINTER");
        Go(1);
    }

    public static string WizardTitle => T("Подключить принтер", "Принтерди туташтыруу", "Connect printer", "Yazıcıyı bağla", "Printerni ulash");
    private static string NextText => T("Далее", "Кийинки", "Next", "İleri", "Keyingi");
    private static string SaveText => T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
    private static string RoleLog(PrinterRole r) => r switch { PrinterRole.Receipt => "чековый", PrinterRole.Label => "этикетки", _ => "определить" };

    /// <summary>Текущий шаг (для тестов).</summary>
    public int Step => _step;
    /// <summary>Выбранный принтер (для тестов).</summary>
    public PrinterCandidate? Candidate => _candidate;
    /// <summary>Путь, на который будет печать (для тестов).</summary>
    public string? DevicePath => _devicePath;

    // ------------------------------------------------------------------ навигация

    private void Go(int step)
    {
        if (step < 1 || step > Steps)
            return;
        _step = step;
        _stepText.Text = T($"Шаг {step} из {Steps}", $"{Steps} кадамдын {step}-кадамы", $"Step {step} of {Steps}", $"Adım {step} / {Steps}", $"{Steps} qadamdan {step}-qadam");
        _body.Content = step switch
        {
            1 => _step1,
            2 => EnterStep2(),
            3 => EnterStep3(),
            4 => EnterStep4(),
            _ => EnterStep5(),
        };
        _back.IsVisible = step > 1;
        _next.Content = step == Steps ? SaveText : NextText;
        UpdateNext();
    }

    private void UpdateNext()
    {
        _next.IsEnabled = _step switch
        {
            2 => _list.SelectedItem is PrinterCandidate,
            3 => _connectionReady && !string.IsNullOrWhiteSpace(_devicePath),
            _ => true,
        };
    }

    private void OnNext()
    {
        if (!_next.IsEnabled)
            return;
        if (_step == 1)
            _role = _rbLabel.IsChecked == true ? PrinterRole.Label : _rbReceipt.IsChecked == true ? PrinterRole.Receipt : PrinterRole.Auto;
        if (_step == 2)
        {
            _scanCts?.Cancel();
            var picked = _list.SelectedItem as PrinterCandidate;
            if (!ReferenceEquals(picked, _candidate))
            {
                _candidate = picked;
                _devicePath = null;
                _connectionReady = false;
            }
            PosLogger.Log($"Мастер принтера: выбран «{_candidate?.Title}» ({_candidate?.Kind}, {_candidate?.DevicePath}).", "PRINTER");
        }
        if (_step == Steps)
        {
            Save();
            return;
        }
        Go(_step + 1);
    }

    private void Finish(PrinterWizardResult? result)
    {
        if (_closed)
            return;
        _closed = true;
        _scanCts?.Cancel();
        if (result is null)
            PosLogger.Log("[UI] Мастер принтера: закрыт без сохранения.", "PRINTER");
        Close(result);
    }

    // ------------------------------------------------------------------ общие элементы

    /// <summary>Кнопки в ряд с переносом (у WrapPanel в Avalonia 11.2 нет ItemSpacing — отступ задаётся каждой кнопке).</summary>
    internal static WrapPanel ButtonRow(params Control[] items)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 8, 8);
            row.Children.Add(item);
        }
        return row;
    }

    private static TextBlock Text(string text, double size = 13, bool bold = false, string brush = "BrushText")
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private static Button Action(string name, string text)
    {
        var b = new Button { Name = name, Content = text, MinHeight = 44, Padding = new Thickness(14, 6) };
        b.Classes.Add("modal-cancel");
        return b;
    }

    private static RadioButton Radio(string name, string group, string text, bool isChecked) => new()
    {
        Name = name, GroupName = group, IsChecked = isChecked, MinHeight = 40,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 },
    };

    // ------------------------------------------------------------------ шаг 1: тип принтера

    private StackPanel _step1 = null!;
    private RadioButton _rbReceipt = null!, _rbLabel = null!, _rbAuto = null!;

    private void BuildStep1()
    {
        _rbReceipt = Radio("WizardRoleReceipt", "wizRole", T("Чековый принтер — чеки на ленте 58 или 80 мм (XP-58, XP-80, POS-58/80…)", "Чек принтери — 58 же 80 мм лентадагы чектер (XP-58, XP-80, POS-58/80…)",
            "Receipt printer — receipts on 58 or 80 mm tape (XP-58, XP-80, POS-58/80…)", "Fiş yazıcısı — 58 veya 80 mm rulo fişler (XP-58, XP-80, POS-58/80…)", "Chek printeri — 58 yoki 80 mm lentadagi cheklar (XP-58, XP-80, POS-58/80…)"), _role == PrinterRole.Receipt);
        _rbLabel = Radio("WizardRoleLabel", "wizRole", T("Принтер этикеток и штрихкодов (XP-365B, XP-360B, XP-420B, XP-460B…)", "Этикетка жана штрих-код принтери (XP-365B, XP-360B, XP-420B, XP-460B…)",
            "Label and barcode printer (XP-365B, XP-360B, XP-420B, XP-460B…)", "Etiket ve barkod yazıcısı (XP-365B, XP-360B, XP-420B, XP-460B…)", "Yorliq va shtrix-kod printeri (XP-365B, XP-360B, XP-420B, XP-460B…)"), _role == PrinterRole.Label);
        _rbAuto = Radio("WizardRoleAuto", "wizRole", T("Не знаю — определить автоматически по модели", "Билбейм — моделы боюнча автоматтык аныктоо", "Not sure — detect automatically by model", "Bilmiyorum — modele göre otomatik belirle", "Bilmayman — model bo'yicha avtomatik aniqlash"), _role == PrinterRole.Auto);
        _step1 = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text(T("Какой принтер подключаем?", "Кайсы принтерди туташтырабыз?", "Which printer are we connecting?", "Hangi yazıcıyı bağlıyoruz?", "Qaysi printerni ulaymiz?"), 15, true),
                _rbReceipt, _rbLabel, _rbAuto,
                Text(T("Включите принтер и подключите кабель USB (или сеть). Драйвер с диска или сайта ставить не нужно — касса печатает напрямую. Мастер ничего не меняет в Windows без вашего разрешения.",
                    "Принтерди күйгүзүп, USB кабелин (же тармакты) туташтырыңыз. Дисктен же сайттан драйвер коюунун кереги жок — касса түз басат. Чебер Windows'то уруксатыңызсыз эч нерсени өзгөртпөйт.",
                    "Turn the printer on and plug in the USB cable (or network). No driver from a disc or website is needed — the cash register prints directly. The wizard changes nothing in Windows without your permission.",
                    "Yazıcıyı açın ve USB kablosunu (veya ağı) takın. Diskten veya siteden sürücü kurmaya gerek yok — kasa doğrudan yazdırır. Sihirbaz izniniz olmadan Windows'ta hiçbir şeyi değiştirmez.",
                    "Printerni yoqing va USB kabelini (yoki tarmoqni) ulang. Diskdan yoki saytdan drayver o'rnatish shart emas — kassa to'g'ridan-to'g'ri chop etadi. Usta ruxsatingizsiz Windows'da hech narsani o'zgartirmaydi."), 12.5, false, "BrushTextSoft"),
            },
        };
    }

    // ------------------------------------------------------------------ шаг 2: поиск

    private StackPanel? _step2;
    private readonly ListBox _list = new() { Name = "WizardPrinterList", MinHeight = 120, MaxHeight = 260 };
    private readonly TextBlock _searchStatus = Text("", 12.5, false, "BrushTextSoft");
    private Button _scanNet = null!;
    private readonly TextBox _ipBox = new() { Name = "WizardIpBox", Height = 44, VerticalContentAlignment = VerticalAlignment.Center };
    private IReadOnlyList<PrinterCandidate> _all = Array.Empty<PrinterCandidate>();

    private Control EnterStep2()
    {
        if (_step2 is null)
        {
            _list.ItemTemplate = new FuncDataTemplate<PrinterCandidate>((c, _) => c is null ? new TextBlock() : CandidateRow(c), false);
            _list.SelectionChanged += (_, _) => UpdateNext();
            var refresh = Action("WizardRefresh", T("Обновить список", "Тизмени жаңылоо", "Refresh list", "Listeyi yenile", "Ro'yxatni yangilash"));
            refresh.Click += (_, _) => Refresh();
            _scanNet = Action("WizardScanNetwork", T("Искать в сети", "Тармактан издөө", "Search network", "Ağda ara", "Tarmoqdan qidirish"));
            _scanNet.Click += (_, _) => ScanNetwork();
            _ipBox.Watermark = T("IP сетевого принтера, например 192.168.1.50", "Тармактык принтердин IP, мисалы 192.168.1.50", "Network printer IP, e.g. 192.168.1.50", "Ağ yazıcısı IP'si, ör. 192.168.1.50", "Tarmoq printeri IP, masalan 192.168.1.50");
            var addIp = Action("WizardAddIp", T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish"));
            addIp.Click += (_, _) => AddIp();
            var ipRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
            Grid.SetColumn(addIp, 2);
            ipRow.Children.Add(_ipBox);
            ipRow.Children.Add(addIp);
            _step2 = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text(T("Найденные принтеры — выберите свой:", "Табылган принтерлер — өзүңүздүкүн тандаңыз:", "Printers found — choose yours:", "Bulunan yazıcılar — kendinizinkini seçin:", "Topilgan printerlar — o'zingiznikini tanlang:"), 15, true),
                    _list, _searchStatus,
                    ButtonRow(refresh, _scanNet),
                    ipRow,
                },
            };
            Refresh();
        }
        return _step2;
    }

    private Control CandidateRow(PrinterCandidate c)
    {
        var title = Text(c.Title, 14, true);
        var detail = Text(Detail(c), 12, false, c.NeedsQueue ? "BrushWarning" : "BrushTextSoft");
        return new StackPanel { Spacing = 2, Margin = new Thickness(2, 4), Children = { title, detail } };
    }

    /// <summary>Вторая строка пункта списка: как подключён, марка, порт, что нужно сделать.</summary>
    public static string Detail(PrinterCandidate c)
    {
        var parts = new List<string>();
        switch (c.Kind)
        {
            case PrinterCandidateKind.WindowsQueue:
                parts.Add(T("принтер Windows", "Windows принтери", "Windows printer", "Windows yazıcısı", "Windows printeri"));
                if (!string.IsNullOrWhiteSpace(c.DriverName))
                    parts.Add(T($"драйвер «{c.DriverName}»", $"драйвер «{c.DriverName}»", $"driver “{c.DriverName}”", $"sürücü «{c.DriverName}»", $"drayver «{c.DriverName}»"));
                if (!string.IsNullOrWhiteSpace(c.PortName))
                    parts.Add(T($"порт {c.PortName}", $"порт {c.PortName}", $"port {c.PortName}", $"port {c.PortName}", $"port {c.PortName}"));
                if (c.Usb is not null)
                    parts.Add(T("USB подключён", "USB туташкан", "USB connected", "USB bağlı", "USB ulangan"));
                break;
            case PrinterCandidateKind.UsbWithoutQueue:
                parts.Add(T("USB, драйвер не установлен", "USB, драйвер орнотулган эмес", "USB, no driver installed", "USB, sürücü kurulu değil", "USB, drayver o'rnatilmagan"));
                if (!string.IsNullOrWhiteSpace(c.PortName))
                    parts.Add(T($"порт {c.PortName}", $"порт {c.PortName}", $"port {c.PortName}", $"port {c.PortName}", $"port {c.PortName}"));
                if (c.Usb is { } u)
                    parts.Add(PrinterUsbCatalog.VidPidText(u.Vid, u.Pid));
                parts.Add(T("мастер подключит его", "чебер аны туташтырат", "the wizard will connect it", "sihirbaz onu bağlayacak", "usta uni ulaydi"));
                break;
            default:
                parts.Add(LabelPrinterSelector.DescribeKind(c.DevicePath));
                break;
        }
        var brand = PrinterUsbCatalog.BrandText(c.Brand);
        if (brand.Length > 0 && !c.Title.Contains(c.Brand.Brand ?? "~", StringComparison.OrdinalIgnoreCase))
            parts.Insert(0, brand);
        else if (brand.Length > 0 && c.Brand.Probable)
            parts.Insert(0, brand);
        if (c.RoleGuess == PrinterRole.Label)
            parts.Add(T("этикеточный", "этикеткалык", "label printer", "etiket yazıcısı", "yorliq printeri"));
        else if (c.RoleGuess == PrinterRole.Receipt)
            parts.Add(T("чековый", "чектик", "receipt printer", "fiş yazıcısı", "chek printeri"));
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private async void Refresh()
    {
        _searchStatus.Text = T("Ищу принтеры…", "Принтерлерди издеп жатам…", "Looking for printers…", "Yazıcılar aranıyor…", "Printerlar qidirilmoqda…");
        var keep = (_list.SelectedItem as PrinterCandidate)?.DevicePath;
        IReadOnlyList<PrinterCandidate> found;
        try
        {
            var net = _networkFound.ToList();
            found = await Task.Run(() =>
            {
                var others = _sys.ListOtherPorts().ToList();
                foreach (var n in net)
                    if (!others.Any(o => LabelPrinterSelector.SamePrinter(o.DevicePath, n)))
                        others.Add(NetworkPrinterAddress.ToDiscovered(n));
                return PrinterInventoryBuilder.Build(_sys.ListUsbPrinters(), _sys.ListQueues(), others);
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер принтера: поиск не удался ({ex.GetType().Name}).", "PRINTER");
            found = Array.Empty<PrinterCandidate>();
        }
        // подходящие под выбранный тип — сверху
        _all = found.OrderBy(c => PrinterInventoryBuilder.Matches(c, _role) ? 0 : 1).ToList();
        _list.ItemsSource = _all;
        _list.SelectedItem = _all.FirstOrDefault(c => c.DevicePath == keep)
                             ?? (_all.Count(c => c.Usb is not null) == 1 ? _all.First(c => c.Usb is not null) : null);
        PosLogger.Log($"Мастер принтера: найдено {_all.Count} (USB без очереди {_all.Count(c => c.Kind == PrinterCandidateKind.UsbWithoutQueue)}, очередей {_all.Count(c => c.Kind == PrinterCandidateKind.WindowsQueue)}, Xprinter {_all.Count(c => c.Brand.IsXprinter)}).", "PRINTER");
        _searchStatus.Text = _all.Count == 0
            ? T("Принтеры не найдены. Проверьте, что принтер включён и кабель USB вставлен, подождите 10 секунд и нажмите «Обновить список». Сетевой принтер — «Искать в сети» или введите его IP.",
                "Принтерлер табылган жок. Принтер күйүк жана USB кабели сайылганын текшерип, 10 секунд күтүп, «Тизмени жаңылоо» баскычын басыңыз. Тармактык принтер — «Тармактан издөө» же анын IP'син киргизиңиз.",
                "No printers found. Check that the printer is on and the USB cable is plugged in, wait 10 seconds and press “Refresh list”. For a network printer, press “Search network” or enter its IP.",
                "Yazıcı bulunamadı. Yazıcının açık ve USB kablosunun takılı olduğunu kontrol edin, 10 saniye bekleyip «Listeyi yenile»ye basın. Ağ yazıcısı için «Ağda ara»ya basın veya IP'sini girin.",
                "Printerlar topilmadi. Printer yoqilgan va USB kabeli ulanganini tekshiring, 10 soniya kuting va «Ro'yxatni yangilash» tugmasini bosing. Tarmoq printeri — «Tarmoqdan qidirish» yoki uning IP'sini kiriting.")
            : T($"Найдено: {_all.Count}. Сверху — самые вероятные.", $"Табылды: {_all.Count}. Жогоруда — эң ыктымалдуулары.", $"Found: {_all.Count}. The most likely ones are at the top.", $"Bulunan: {_all.Count}. En olası olanlar üstte.", $"Topildi: {_all.Count}. Eng ehtimolligi yuqorida.");
        UpdateNext();
    }

    private async void ScanNetwork()
    {
        if (_scanCts is { IsCancellationRequested: false })
        {
            _scanCts.Cancel(); // вторая нажатие — «Остановить»
            return;
        }
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        _scanNet.Content = T("Остановить поиск", "Издөөнү токтотуу", "Stop search", "Aramayı durdur", "Qidiruvni to'xtatish");
        var progress = new Progress<NetworkPrinterScanner.Progress>(p => _searchStatus.Text = T(
            $"Сеть: проверено {p.Done} из {p.Total}, найдено {p.Found}", $"Тармак: {p.Total} ичинен {p.Done} текшерилди, {p.Found} табылды", $"Network: checked {p.Done} of {p.Total}, found {p.Found}",
            $"Ağ: {p.Total} adresten {p.Done} kontrol edildi, {p.Found} bulundu", $"Tarmoq: {p.Total} tadan {p.Done} tekshirildi, {p.Found} topildi"));
        PosLogger.Log("Мастер принтера: поиск в сети (порт 9100) по кнопке.", "PRINTER");
        try
        {
            var found = NetworkScanOverride is { } fake
                ? await fake(progress, ct)
                : await Task.Run(() => NetworkPrinterScanner.ScanAsync(progress, ct), CancellationToken.None);
            foreach (var f in found)
                if (!_networkFound.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _networkFound.Add(f);
            PosLogger.Log($"Мастер принтера: в сети найдено {found.Count}.", "PRINTER");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Мастер принтера: поиск в сети не удался ({ex.GetType().Name}).", "PRINTER");
        }
        finally
        {
            _scanNet.Content = T("Искать в сети", "Тармактан издөө", "Search network", "Ağda ara", "Tarmoqdan qidirish");
            _scanCts?.Dispose();
            _scanCts = null;
        }
        if (!_closed)
            Refresh();
    }

    private void AddIp()
    {
        var text = (_ipBox.Text ?? "").Trim();
        if (!NetworkPrinterAddress.TryParse(text, out var host, out var port, out var error))
        {
            _searchStatus.Text = error ?? "";
            return;
        }
        var canon = NetworkPrinterAddress.Format(host, port);
        if (!_networkFound.Contains(canon, StringComparer.OrdinalIgnoreCase))
            _networkFound.Add(canon);
        _ipBox.Text = "";
        Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            if (_all.FirstOrDefault(c => LabelPrinterSelector.SamePrinter(c.DevicePath, canon)) is { } item)
                _list.SelectedItem = item;
        }, DispatcherPriority.Background);
    }

    /// <summary>Открыть мастер над окном <paramref name="owner"/>. null — закрыт без сохранения.</summary>
    public static async Task<PrinterWizardResult?> ShowAsync(Window? owner, PrinterRole role)
    {
        var window = new PrinterSetupWizardWindow(role);
        return await window.ShowDialog<PrinterWizardResult?>(PosDialogHost.ResolveOwner(owner)).ConfigureAwait(true);
    }
}
