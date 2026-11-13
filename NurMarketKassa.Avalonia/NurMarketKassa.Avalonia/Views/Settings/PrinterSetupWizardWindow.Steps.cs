using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-13: шаги 3–5 мастера «Подключить принтер» (подключение, параметры, пробная печать и сохранение).
public sealed partial class PrinterSetupWizardWindow
{
    // ------------------------------------------------------------------ шаг 3: подключение

    private readonly TextBlock _connStatus = Text("", 13);
    private readonly TextBox _queueNameBox = new() { Name = "WizardQueueName", Height = 44, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 120 };
    private StackPanel? _step3;
    private Button _createQueue = null!, _directUsb = null!, _driverButton = null!;
    private StackPanel _queuePanel = null!;
    private StackPanel _queueIntro = null!;
    private string? _step3For;

    /// <summary>Принтер — этикеточный (по выбору на шаге 1 или по модели при «определить автоматически»).</summary>
    private bool IsLabel => _role == PrinterRole.Label || _role == PrinterRole.Auto && _candidate?.RoleGuess == PrinterRole.Label;

    private PrinterRole EffectiveRole => IsLabel ? PrinterRole.Label : PrinterRole.Receipt;

    private Control EnterStep3()
    {
        if (_step3 is null)
        {
            _createQueue = new Button { Name = "WizardCreateQueue", MinHeight = 44, Padding = new Thickness(14, 6),
                Content = T("Создать принтер в Windows", "Windows'то принтер түзүү", "Create the printer in Windows", "Windows'ta yazıcı oluştur", "Windows'da printer yaratish") };
            _createQueue.Classes.Add("modal-primary");
            _createQueue.Click += (_, _) => CreateQueue();
            _directUsb = Action("WizardDirectUsb", T("Печатать напрямую в USB (без принтера Windows)", "USB'ге түз басуу (Windows принтерисиз)", "Print directly to USB (no Windows printer)", "Doğrudan USB'ye yazdır (Windows yazıcısı olmadan)", "To'g'ridan-to'g'ri USB'ga chop etish (Windows printerisiz)"));
            _directUsb.Click += (_, _) => UseDirectUsb();
            _driverButton = Action("WizardDriver", DriverButtonText);
            _driverButton.Click += (_, _) => PrinterDriverLinks.Open(PrinterDriverLinks.UrlFor(EffectiveRole));
            _queuePanel = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    (_queueIntro = new StackPanel { Spacing = 8, Children = { // 2026-11-13: пояснение скрывается после отказа — на 1024×600 кнопки «напрямую»/«драйвер» видны без прокрутки
                    Text(T("Для этого USB-принтера в Windows ещё нет «принтера» (очереди печати). Мастер создаст его на встроенном драйвере Windows «Generic / Text Only» — скачивать ничего не нужно. Касса будет печатать в него своими командами.",
                        "Бул USB-принтер үчүн Windows'то азырынча «принтер» (басып чыгаруу кезеги) жок. Чебер аны Windows'тун ичиндеги «Generic / Text Only» драйверинде түзөт — эч нерсе жүктөөнүн кереги жок. Касса ага өз буйруктары менен басат.",
                        "Windows has no “printer” (print queue) for this USB printer yet. The wizard will create one on the built-in Windows driver “Generic / Text Only” — nothing to download. The cash register will print to it with its own commands.",
                        "Windows'ta bu USB yazıcı için henüz «yazıcı» (yazdırma kuyruğu) yok. Sihirbaz onu Windows'un yerleşik «Generic / Text Only» sürücüsüyle oluşturacak — hiçbir şey indirmeye gerek yok. Kasa ona kendi komutlarıyla yazdıracak.",
                        "Bu USB-printer uchun Windows'da hali «printer» (chop etish navbati) yo'q. Usta uni Windows'ning o'rnatilgan «Generic / Text Only» drayverida yaratadi — hech narsa yuklab olish shart emas. Kassa unga o'z buyruqlari bilan chop etadi.")),
                    Text(T("Windows один раз попросит разрешение («Разрешить этому приложению вносить изменения?») — нажмите «Да». Если входите не под администратором, понадобится пароль администратора.",
                        "Windows бир жолу уруксат сурайт («Бул колдонмого өзгөртүү киргизүүгө уруксат берилсинби?») — «Ооба» баскычын басыңыз. Администратор эмес болуп кирсеңиз, администратордун сырсөзү керек болот.",
                        "Windows will ask for permission once (“Allow this app to make changes?”) — press “Yes”. If you are not signed in as an administrator, an administrator password will be needed.",
                        "Windows bir kez izin isteyecek («Bu uygulamanın değişiklik yapmasına izin verilsin mi?») — «Evet»e basın. Yönetici olarak oturum açmadıysanız yönetici parolası gerekecek.",
                        "Windows bir marta ruxsat so'raydi («Bu ilovaga o'zgartirish kiritishga ruxsat berilsinmi?») — «Ha» tugmasini bosing. Administrator sifatida kirmagan bo'lsangiz, administrator paroli kerak bo'ladi."), 12.5, false, "BrushTextSoft") } }),
                    Text(T("Имя принтера в Windows:", "Windows'тогу принтердин аты:", "Printer name in Windows:", "Windows'taki yazıcı adı:", "Windows'dagi printer nomi:"), 12.5, true),
                    _queueNameBox,
                    ButtonRow(_createQueue, _directUsb, _driverButton),
                },
            };
            _step3 = new StackPanel { Spacing = 10, Children = { Text(T("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), 15, true), _connStatus, _queuePanel } }; // итог (в т.ч. «что делать при отказе») — сверху, виден без прокрутки на 1024×600
        }
        if (_step3For != _candidate?.DevicePath)
        {
            _step3For = _candidate?.DevicePath;
            _connStatus.Text = "";
            _directUsb.IsVisible = false;
            _driverButton.IsVisible = false;
            if (_candidate is { NeedsQueue: true } c && (OperatingSystem.IsWindows() || CreateQueueOverride is not null))
            {
                _queuePanel.IsVisible = true;
                _queueIntro.IsVisible = true;
                _createQueue.IsEnabled = true;
                _queueNameBox.Text = PrinterInventoryBuilder.SuggestQueueName(EffectiveRole, GuessPaper(c) ?? UserPreferences.Instance.ReceiptPaperWidthMm, _sys.ListQueues().Select(q => q.Name));
                _connectionReady = false;
                _devicePath = null;
            }
            else
            {
                _queuePanel.IsVisible = false;
                _devicePath = _candidate?.DevicePath;
                _connectionReady = _devicePath is not null;
                ProbeSelected();
            }
        }
        return _step3;
    }

    private static string DriverButtonText => T("Скачать драйвер Xprinter", "Xprinter драйверин жүктөө", "Download Xprinter driver", "Xprinter sürücüsünü indir", "Xprinter drayverini yuklab olish");

    private async void ProbeSelected()
    {
        var path = _devicePath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        _connStatus.Text = T("Проверяю связь…", "Байланышты текшерип жатам…", "Checking the connection…", "Bağlantı kontrol ediliyor…", "Aloqa tekshirilmoqda…");
        PrinterPortService.PortProbeResult probe;
        try
        {
            probe = await Task.Run(() => _sys.Probe(path));
        }
        catch (Exception ex)
        {
            probe = new PrinterPortService.PortProbeResult(false, ex.GetType().Name, "unknown");
        }
        if (_devicePath != path)
            return;
        PosLogger.Log($"Мастер принтера: проверка связи «{path}» — {(probe.IsAvailable ? "ok" : "нет")}.", "PRINTER");
        _connStatus.Text = (probe.IsAvailable
                               ? T("Принтер на связи. Нажмите «Далее».", "Принтер байланышта. «Кийинки» баскычын басыңыз.", "The printer is reachable. Press “Next”.", "Yazıcıya ulaşılıyor. «İleri»ye basın.", "Printer aloqada. «Keyingi» tugmasini bosing.")
                               : T("Связи с принтером сейчас нет — проверьте питание и кабель. Можно всё равно продолжить и сделать пробную печать.", "Азыр принтер менен байланыш жок — кубатты жана кабелди текшериңиз. Баары бир улантып, сыноо басып чыгарса болот.",
                                   "The printer is not reachable right now — check power and cable. You can still continue and make a test print.", "Şu anda yazıcıya ulaşılamıyor — gücü ve kabloyu kontrol edin. Yine de devam edip deneme baskısı yapabilirsiniz.",
                                   "Hozir printer bilan aloqa yo'q — quvvat va kabelni tekshiring. Baribir davom etib, sinov chop etish mumkin."))
                           + "\n" + probe.Message;
        UpdateNext();
    }

    private async void CreateQueue()
    {
        if (_candidate is not { PortName: { } port } c)
            return;
        var name = (_queueNameBox.Text ?? "").Trim();
        if (!PrinterQueueInstaller.IsValidQueueName(name))
        {
            _connStatus.Text = T("Имя принтера не должно быть пустым и содержать знаки \\ , ! ' \"", "Принтердин аты бош болбошу жана \\ , ! ' \" белгилерин камтыбашы керек", "The printer name must not be empty or contain \\ , ! ' \"", "Yazıcı adı boş olmamalı ve \\ , ! ' \" içermemeli", "Printer nomi bo'sh bo'lmasligi va \\ , ! ' \" belgilarini o'z ichiga olmasligi kerak");
            return;
        }
        if (!_shell.TryBeginBusy())
            return;
        _createQueue.IsEnabled = false;
        _connStatus.Text = T("Жду разрешения Windows…", "Windows'тун уруксатын күтүп жатам…", "Waiting for Windows permission…", "Windows izni bekleniyor…", "Windows ruxsati kutilmoqda…");
        ElevatedOutcome outcome;
        try
        {
            outcome = CreateQueueOverride is { } fake ? await fake(name, port) : await PrinterQueueInstaller.CreateQueueAsync(name, port);
            if (outcome.Result == ElevatedResult.Done && CreateQueueOverride is null)
            {
                PrinterQueueInstaller.InvalidateCache();
                var appeared = await Task.Run(() => _sys.ListQueues().Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)));
                if (!appeared)
                    outcome = new ElevatedOutcome(ElevatedResult.Failed, "queue not found after creation");
            }
        }
        finally
        {
            _shell.EndBusy();
        }
        if (outcome.Result == ElevatedResult.Done)
        {
            _devicePath = name;
            _connectionReady = true;
            _connStatus.Text = T($"Готово: в Windows появился принтер «{name}». Нажмите «Далее».", $"Даяр: Windows'то «{name}» принтери пайда болду. «Кийинки» баскычын басыңыз.",
                $"Done: Windows now has printer “{name}”. Press “Next”.", $"Tamam: Windows'ta «{name}» yazıcısı oluşturuldu. «İleri»ye basın.", $"Tayyor: Windows'da «{name}» printeri paydo bo'ldi. «Keyingi» tugmasini bosing.");
        }
        else
        {
            _queueIntro.IsVisible = false;
            _createQueue.IsEnabled = true;
            _directUsb.IsVisible = OperatingSystem.IsWindows() || CreateQueueOverride is not null;
            _driverButton.IsVisible = true;
            var why = outcome.Result == ElevatedResult.Cancelled
                ? T("Windows не дал разрешение — ничего не изменено.", "Windows уруксат берген жок — эч нерсе өзгөргөн жок.", "Windows did not give permission — nothing was changed.", "Windows izin vermedi — hiçbir şey değiştirilmedi.", "Windows ruxsat bermadi — hech narsa o'zgartirilmadi.")
                : T("Создать принтер не получилось.", "Принтерди түзүү мүмкүн болгон жок.", "The printer could not be created.", "Yazıcı oluşturulamadı.", "Printerni yaratib bo'lmadi.");
            _connStatus.Text = why + " " + T("Что можно сделать: 1) «Печатать напрямую в USB» — касса будет печатать без принтера Windows (печать из других программ не заработает); 2) поставить драйвер производителя («Скачать драйвер Xprinter»), затем снова открыть мастер и выбрать появившийся принтер; 3) попросить администратора компьютера и нажать «Создать» ещё раз.",
                "Эмне кылса болот: 1) «USB'ге түз басуу» — касса Windows принтерисиз басат (башка программалардан басуу иштебейт); 2) өндүрүүчүнүн драйверин коюу («Xprinter драйверин жүктөө»), андан кийин чеберди кайра ачып, пайда болгон принтерди тандоо; 3) компьютердин администраторунан сурап, «Түзүү» баскычын кайра басуу.",
                "What you can do: 1) “Print directly to USB” — the cash register prints without a Windows printer (printing from other programs will not work); 2) install the manufacturer's driver (“Download Xprinter driver”), then open the wizard again and choose the new printer; 3) ask the computer's administrator and press “Create” again.",
                "Ne yapılabilir: 1) «Doğrudan USB'ye yazdır» — kasa Windows yazıcısı olmadan yazdırır (diğer programlardan yazdırma çalışmaz); 2) üretici sürücüsünü kurun («Xprinter sürücüsünü indir»), sonra sihirbazı yeniden açıp yeni yazıcıyı seçin; 3) bilgisayar yöneticisinden yardım isteyip «Oluştur»a yeniden basın.",
                "Nima qilish mumkin: 1) «To'g'ridan-to'g'ri USB'ga chop etish» — kassa Windows printerisiz chop etadi (boshqa dasturlardan chop etish ishlamaydi); 2) ishlab chiqaruvchi drayverini o'rnating («Xprinter drayverini yuklab olish»), so'ng ustani qayta ochib, paydo bo'lgan printerni tanlang; 3) kompyuter administratoridan so'rab, «Yaratish» tugmasini qayta bosing.");
        }
        UpdateNext();
    }

    private void UseDirectUsb()
    {
        if (_candidate is null)
            return;
        _devicePath = _candidate.DevicePath;
        _connectionReady = true;
        PosLogger.Log($"Мастер принтера: выбран способ «USB напрямую» ({_devicePath}).", "PRINTER");
        _connStatus.Text = T("Выбрано: печать напрямую в USB. Нажмите «Далее».", "Тандалды: USB'ге түз басуу. «Кийинки» баскычын басыңыз.", "Selected: print directly to USB. Press “Next”.", "Seçildi: doğrudan USB'ye yazdırma. «İleri»ye basın.", "Tanlandi: to'g'ridan-to'g'ri USB'ga chop etish. «Keyingi» tugmasini bosing.");
        UpdateNext();
    }

    /// <summary>Ширина ленты по модели: «80», «Q80», «C230» → 80; «58» → 58; не понятно — null.</summary>
    internal static int? GuessPaper(PrinterCandidate? c)
    {
        var text = $"{c?.Model} {c?.Title} {c?.DriverName}";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"80|76|Q\d{2,3}|C2[36]0|E2\d{2}|N160|V3", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ReceiptPaperProfile.Paper80mm;
        if (text.Contains("58", StringComparison.Ordinal))
            return ReceiptPaperProfile.Paper58mm;
        return null;
    }

    // ------------------------------------------------------------------ шаг 4: параметры

    private StackPanel? _step4;
    private readonly ComboBox _paperCombo = new() { Name = "WizardPaper", HorizontalAlignment = HorizontalAlignment.Stretch, Height = 44 };
    private readonly CheckBox _drawerCheck = new() { Name = "WizardDrawer" };
    private readonly ComboBox _pinCombo = new() { Name = "WizardDrawerPin", Width = 200, Height = 44 };
    private readonly ComboBox _labelSizeCombo = new() { Name = "WizardLabelSize", HorizontalAlignment = HorizontalAlignment.Stretch, Height = 44 };
    private readonly TextBox _gapBox = new() { Name = "WizardGap", Height = 44, Width = 120, VerticalContentAlignment = VerticalAlignment.Center };
    private RadioButton _rbTspl = null!, _rbEscPosLabel = null!, _rbRoleReceipt4 = null!, _rbRoleLabel4 = null!;
    private StackPanel _receiptParams = null!, _labelParams = null!, _roleChoice = null!;
    private string? _step4For;

    private Control EnterStep4()
    {
        if (_step4 is null)
        {
            var prefs = UserPreferences.Instance;
            _paperCombo.ItemsSource = new[]
            {
                new ComboBoxItem { Tag = "58", Content = T("58 мм — 32 знака в строке", "58 мм — сапта 32 белги", "58 mm — 32 characters per line", "58 mm — satırda 32 karakter", "58 mm — qatorda 32 belgi") },
                new ComboBoxItem { Tag = "80", Content = T("80 мм — 48 знаков в строке", "80 мм — сапта 48 белги", "80 mm — 48 characters per line", "80 mm — satırda 48 karakter", "80 mm — qatorda 48 belgi") },
                new ComboBoxItem { Tag = "80n", Content = T("80 мм — 42 знака (XP-80 и похожие)", "80 мм — 42 белги (XP-80 жана окшоштор)", "80 mm — 42 characters (XP-80 and similar)", "80 mm — 42 karakter (XP-80 ve benzerleri)", "80 mm — 42 belgi (XP-80 va o'xshashlar)") },
            };
            _pinCombo.ItemsSource = new[]
            {
                new ComboBoxItem { Tag = "0", Content = T("Контакт 2 (обычно)", "2-контакт (адатта)", "Pin 2 (usual)", "Pin 2 (genellikle)", "2-kontakt (odatda)") },
                new ComboBoxItem { Tag = "1", Content = T("Контакт 5", "5-контакт", "Pin 5", "Pin 5", "5-kontakt") },
            };
            _pinCombo.SelectedIndex = prefs.CashDrawerPin == 1 ? 1 : 0;
            _pinCombo.IsVisible = prefs.CashDrawerEnabled;
            _drawerCheck.IsCheckedChanged += (_, _) => _pinCombo.IsVisible = _drawerCheck.IsChecked == true; // контакт — только когда ящик есть
            _drawerCheck.IsChecked = prefs.CashDrawerEnabled;
            _drawerCheck.Content = new TextBlock { Text = T("Денежный ящик подключён к принтеру — открывать после продажи", "Акча кутусу принтерге туташкан — сатуудан кийин ачуу", "A cash drawer is connected to the printer — open it after a sale", "Para çekmecesi yazıcıya bağlı — satıştan sonra aç", "Pul qutisi printerga ulangan — sotuvdan keyin ochish"), TextWrapping = TextWrapping.Wrap };
            _receiptParams = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text(T("Ширина ленты", "Лентанын туурасы", "Paper width", "Rulo genişliği", "Lenta kengligi"), 12.5, true), _paperCombo,
                    _drawerCheck, _pinCombo,
                    Text(T("Обрезка: если у принтера есть нож, чек отрезается сам (проверите на пробной ленте). Нет ножа — отрывайте по зубчатой кромке.",
                        "Кесүү: принтерде бычак болсо, чек өзү кесилет (сыноо лентасында көрөсүз). Бычак жок болсо — тиштүү четинен үзүңүз.",
                        "Cutting: if the printer has a cutter, the receipt is cut automatically (you will see it on the test print). No cutter — tear along the serrated edge.",
                        "Kesme: yazıcıda bıçak varsa fiş kendiliğinden kesilir (deneme baskısında görürsünüz). Bıçak yoksa dişli kenardan yırtın.",
                        "Kesish: printerda pichoq bo'lsa, chek o'zi kesiladi (sinov lentasida ko'rasiz). Pichoq bo'lmasa — tishli chetidan yirting."), 12, false, "BrushTextSoft"),
                },
            };

            _rbTspl = Radio("WizardLangTspl", "wizLang", T("TSPL — Xprinter XP-365B, XP-360B, XP-420B, XP-460B, TSC, Gprinter", "TSPL — Xprinter XP-365B, XP-360B, XP-420B, XP-460B, TSC, Gprinter",
                "TSPL — Xprinter XP-365B, XP-360B, XP-420B, XP-460B, TSC, Gprinter", "TSPL — Xprinter XP-365B, XP-360B, XP-420B, XP-460B, TSC, Gprinter", "TSPL — Xprinter XP-365B, XP-360B, XP-420B, XP-460B, TSC, Gprinter"), true);
            _rbEscPosLabel = Radio("WizardLangEscPos", "wizLang", T("ESC/POS — чековый принтер, печатающий этикетки", "ESC/POS — этикетка басуучу чек принтери", "ESC/POS — a receipt printer printing labels", "ESC/POS — etiket basan fiş yazıcısı", "ESC/POS — yorliq chop etuvchi chek printeri"), false);
            _gapBox.Text = prefs.LabelGapMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            var gapRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text(T("Зазор между этикетками, мм:", "Этикеткалардын ортосундагы боштук, мм:", "Gap between labels, mm:", "Etiketler arası boşluk, mm:", "Yorliqlar orasidagi tirqish, mm:"), 12.5), _gapBox } };
            gapRow.Children[0].VerticalAlignment = VerticalAlignment.Center;
            _labelParams = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text(T("Размер этикетки (ширина × высота)", "Этикетканын өлчөмү (туурасы × бийиктиги)", "Label size (width × height)", "Etiket boyutu (genişlik × yükseklik)", "Yorliq o'lchami (kenglik × balandlik)"), 12.5, true), _labelSizeCombo,
                    gapRow,
                    Text(T("Язык команд принтера", "Принтердин буйрук тили", "Printer command language", "Yazıcı komut dili", "Printer buyruq tili"), 12.5, true), _rbTspl, _rbEscPosLabel,
                },
            };

            _rbRoleReceipt4 = Radio("WizardRole4Receipt", "wizRole4", T("Это чековый принтер", "Бул чек принтери", "This is a receipt printer", "Bu bir fiş yazıcısı", "Bu chek printeri"), true);
            _rbRoleLabel4 = Radio("WizardRole4Label", "wizRole4", T("Это принтер этикеток", "Бул этикетка принтери", "This is a label printer", "Bu bir etiket yazıcısı", "Bu yorliq printeri"), false);
            _rbRoleReceipt4.IsCheckedChanged += (_, _) => ShowParams();
            _roleChoice = new StackPanel { Spacing = 4, Children = { Text(T("Тип принтера", "Принтердин түрү", "Printer type", "Yazıcı türü", "Printer turi"), 12.5, true), _rbRoleReceipt4, _rbRoleLabel4 } };

            _step4 = new StackPanel { Spacing = 10, Children = { Text(T("Параметры", "Параметрлер", "Settings", "Ayarlar", "Parametrlar"), 15, true), _roleChoice, _receiptParams, _labelParams } };
        }
        if (_step4For != _candidate?.DevicePath)
        {
            _step4For = _candidate?.DevicePath;
            var prefs = UserPreferences.Instance;
            var paper = GuessPaper(_candidate) ?? ReceiptPaperProfile.NormalizePaperWidthMm(prefs.ReceiptPaperWidthMm);
            var narrow = paper >= 80 && (prefs.ReceiptCharWidth80 == PrinterSetupApply.Narrow80Chars || (_candidate?.Model ?? _candidate?.Title ?? "").Contains("XP-80", StringComparison.OrdinalIgnoreCase));
            _paperCombo.SelectedIndex = paper >= 80 ? (narrow ? 2 : 1) : 0;
            var template = LabelTemplateStore.Load();
            var sizes = new List<(double W, double H)> { (58, 40), (40, 30), (30, 20), (58, 30), (43, 25), (60, 40), (100, 150) };
            if (!sizes.Any(s => Math.Abs(s.W - template.WidthMm) < 0.05 && Math.Abs(s.H - template.HeightMm) < 0.05))
                sizes.Insert(0, (template.WidthMm, template.HeightMm));
            _labelSizeCombo.ItemsSource = sizes.Select(s => new ComboBoxItem
            {
                Tag = s,
                Content = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} × {1:0.#} {2}", s.W, s.H, T("мм", "мм", "mm", "mm", "mm")),
            }).ToList();
            _labelSizeCombo.SelectedIndex = Math.Max(0, sizes.FindIndex(s => Math.Abs(s.W - template.WidthMm) < 0.05 && Math.Abs(s.H - template.HeightMm) < 0.05));
            var esc = _candidate?.LanguageGuess == PrinterLanguage.EscPos && _role == PrinterRole.Label;
            _rbEscPosLabel.IsChecked = esc;
            _rbTspl.IsChecked = !esc;
            _roleChoice.IsVisible = _askRole;
            _rbRoleLabel4.IsChecked = _askRole && _candidate?.RoleGuess == PrinterRole.Label;
            _rbRoleReceipt4.IsChecked = !(_rbRoleLabel4.IsChecked ?? false);
        }
        ShowParams();
        return _step4;
    }

    private void ShowParams()
    {
        if (_askRole)
            _role = _rbRoleLabel4.IsChecked == true ? PrinterRole.Label : PrinterRole.Receipt;
        _receiptParams.IsVisible = !IsLabel;
        _labelParams.IsVisible = IsLabel;
    }

    private (int Paper, bool Narrow) PaperChoice => ((_paperCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "80" => (80, false),
        "80n" => (80, true),
        _ => (58, false),
    };

    private (double W, double H) LabelSize => (_labelSizeCombo.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<double, double> t ? t : (58, 40);

    private double Gap => double.TryParse((_gapBox.Text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? Math.Clamp(g, 0, 10) : 2;

    private PrinterLanguage LabelLanguage => _rbEscPosLabel.IsChecked == true ? PrinterLanguage.EscPos : PrinterLanguage.Tspl;
}
