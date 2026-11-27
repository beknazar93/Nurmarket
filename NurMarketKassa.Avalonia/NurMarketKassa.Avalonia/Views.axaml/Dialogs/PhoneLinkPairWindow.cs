using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PhoneLink;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, пакет К-П3 (урезанный, ТЗ «Мобилка и касса»): окно «Подключить телефон». Три шага, QR кассы (ZXing,
/// чёрным по белому — телефон читает с 40 см), обратный отсчёт 5 минут, «Телефон “Айбек” подключён» и список подключённых
/// телефонов с кнопкой «Отключить». Каждое открытие и каждое «Новый код» создают НОВЫЙ ключ; закрытие окна отменяет ожидание.
/// Окно строится в коде (как другие диалоги); цели касания ≥ 44 px, у кнопок AutomationProperties.Name.</summary>
internal sealed class PhoneLinkPairWindow : Window
{
    // 2026-11-27, жалоба «не работает подключение сканера с телефона»: QR крупнее (≈300 px), модуль — целое число пикселей,
    // белое поле — 4 модуля (как требует стандарт QR); было 248 px и поле 14 px (≈2,5 модуля) — камера телефона код с экрана не брала.
    private const double QrTargetSize = 300;
    private readonly ReceiptQrView _qr = new() { Size = QrTargetSize, Dark = Brushes.Black, Light = Brushes.White };
    private readonly Border _qrFrame;
    private readonly TextBlock _timer = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _address = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _ipCombo = new() { Height = 44, MinWidth = 200, IsVisible = false };
    private readonly StackPanel _phones = new() { Spacing = 6 };
    // 2026-11-27 (диагностика «телефон не подключается»): «телефон стучался, но…» и предупреждение брандмауэра Windows.
    private readonly TextBlock _knock = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _firewallWarn = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private int _firewallCheckId;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _key;
    private bool _paired;
    private bool _suppressIp;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public PhoneLinkPairWindow()
    {
        Title = T("Подключить телефон", "Телефонду туташтыруу", "Connect a phone", "Telefonu bağla", "Telefonni ulash");
        SectionCards.AsModal(this, 860); // 2026-11-27: было 780 — QR стал крупнее

        _qrFrame = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _qr,
        };
        Avalonia.Automation.AutomationProperties.SetName(_qrFrame, T("QR-код кассы для телефона", "Телефон үчүн кассанын QR-коду", "Till QR code for the phone", "Telefon için kasa QR kodu", "Telefon uchun kassa QR kodi"));

        var steps = new StackPanel { Spacing = 10 };
        steps.Children.Add(Step("1", T("Откройте приложение NurCRM на телефоне: Профиль → «Связь с кассой».",
            "Телефондогу NurCRM тиркемесин ачыңыз: Профиль → «Касса менен байланыш».",
            "Open the NurCRM app on the phone: Profile → “Till link”.",
            "Telefonda NurCRM uygulamasını açın: Profil → «Kasa bağlantısı».",
            "Telefonda NurCRM ilovasini oching: Profil → «Kassa bilan aloqa».")));
        steps.Children.Add(Step("2", T("Нажмите «Сканировать QR» и наведите камеру на код слева.",
            "«QR сканерлөө» баскычын басып, камераны сол жактагы кодго багыттаңыз.",
            "Tap “Scan QR” and point the camera at the code on the left.",
            "«QR tara» düğmesine basıp kamerayı soldaki koda doğrultun.",
            "«QR skanerlash» tugmasini bosib, kamerani chapdagi kodga qarating.")));
        steps.Children.Add(Step("3", T("Дождитесь сообщения «Телефон подключён». Дальше сканируйте товары камерой телефона — они сразу попадут в чек этой кассы.",
            "«Телефон туташты» деген билдирүүнү күтүңүз. Андан соң товарларды телефондун камерасы менен сканерлеңиз — алар түз эле ушул кассанын чегине түшөт.",
            "Wait for “Phone connected”. Then scan goods with the phone camera — they go straight into this till's receipt.",
            "«Telefon bağlandı» mesajını bekleyin. Sonra ürünleri telefon kamerasıyla tarayın — doğrudan bu kasanın fişine düşer.",
            "«Telefon ulandi» xabarini kuting. Keyin tovarlarni telefon kamerasi bilan skanerlang — ular bevosita shu kassa chekiga tushadi.")));

        var wifi = new TextBlock
        {
            Text = T("Телефон должен быть в той же Wi-Fi сети, что и касса.", "Телефон кассадагы Wi-Fi тармагында болушу керек.",
                "The phone must be on the same Wi-Fi network as the till.", "Telefon kasayla aynı Wi-Fi ağında olmalı.", "Telefon kassa bilan bir xil Wi-Fi tarmog'ida bo'lishi kerak."),
            FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        wifi.Bind(TextBlock.ForegroundProperty, wifi.GetResourceObservable("BrushText"));
        steps.Children.Add(wifi);

        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushText"));
        _timer.Bind(TextBlock.ForegroundProperty, _timer.GetResourceObservable("BrushTextSoft"));
        _address.Bind(TextBlock.ForegroundProperty, _address.GetResourceObservable("BrushTextSoft"));
        steps.Children.Add(_status);
        steps.Children.Add(_timer);
        _knock.Bind(TextBlock.ForegroundProperty, _knock.GetResourceObservable("BrushText"));
        _firewallWarn.Bind(TextBlock.ForegroundProperty, _firewallWarn.GetResourceObservable("BrushDanger"));
        steps.Children.Add(_knock);
        steps.Children.Add(_firewallWarn);

        var newCode = SectionCards.Secondary(T("Новый код", "Жаңы код", "New code", "Yeni kod", "Yangi kod"));
        newCode.Click += (_, _) => NewCode();
        newCode.MinHeight = 44;
        newCode.HorizontalAlignment = HorizontalAlignment.Left;
        steps.Children.Add(newCode);

        var qrColumn = new StackPanel { Spacing = 8, Width = 372 };
        qrColumn.Children.Add(_qrFrame);
        qrColumn.Children.Add(_address);
        qrColumn.Children.Add(_ipCombo);
        _ipCombo.SelectionChanged += (_, _) => { if (!_suppressIp) { RedrawQr(); CheckFirewall(); } };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,24,*") };
        Grid.SetColumn(qrColumn, 0);
        Grid.SetColumn(steps, 2);
        top.Children.Add(qrColumn);
        top.Children.Add(steps);

        var listTitle = new TextBlock { Text = T("Подключённые телефоны", "Туташтырылган телефондор", "Connected phones", "Bağlı telefonlar", "Ulangan telefonlar"), FontSize = 14, FontWeight = FontWeight.SemiBold };
        listTitle.Bind(TextBlock.ForegroundProperty, listTitle.GetResourceObservable("BrushText"));

        var firewall = new TextBlock
        {
            Text = T("Если Windows спросит про брандмауэр («Разрешить доступ») — нажмите «Разрешить» для частной сети. Без этого телефон не достучится до кассы. Касса сама правил брандмауэра не создаёт.",
                "Эгер Windows брандмауэр жөнүндө сураса («Мүмкүнчүлүк берүү») — жеке тармак үчүн «Уруксат берүү» баскычын басыңыз. Антпесе телефон кассага жете албайт. Касса брандмауэр эрежелерин өзү түзбөйт.",
                "If Windows asks about the firewall (“Allow access”), press “Allow” for the private network. Without it the phone cannot reach the till. The till never creates firewall rules itself.",
                "Windows güvenlik duvarını sorarsa («Erişime izin ver»), özel ağ için «İzin ver» düğmesine basın. Aksi halde telefon kasaya ulaşamaz. Kasa güvenlik duvarı kuralı oluşturmaz.",
                "Windows brandmauer haqida so'rasa («Ruxsat berish»), xususiy tarmoq uchun «Ruxsat berish» tugmasini bosing. Aks holda telefon kassaga yeta olmaydi. Kassa brandmauer qoidalarini o'zi yaratmaydi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = OperatingSystem.IsWindows(),
        };
        firewall.Bind(TextBlock.ForegroundProperty, firewall.GetResourceObservable("BrushTextSoft"));

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(top);
        content.Children.Add(listTitle);
        content.Children.Add(_phones);
        content.Children.Add(firewall);

        var close = SectionCards.Primary(T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        close.IsCancel = true;
        close.MinHeight = 44;
        close.Click += (_, _) => Close();
        Content = SectionCards.Shell(Title!, T("Телефон станет беспроводным сканером этой кассы", "Телефон ушул кассанын зымсыз сканери болот",
            "The phone becomes a wireless scanner for this till", "Telefon bu kasanın kablosuz tarayıcısı olur", "Telefon shu kassaning simsiz skaneriga aylanadi"),
            content, SectionCards.Buttons(close));

        Opened += (_, _) =>
        {
            LoadAddresses();
            NewCode();
            CheckFirewall();
            RefreshPhones();
            PhoneLinkHost.Pairing.Paired += OnPaired;
            PhoneLinkHost.Pairing.Changed += OnPairingChanged;
            PhoneLinkHost.Knocked += OnKnocked;
            _tick.Tick += (_, _) => UpdateTimer();
            _tick.Start();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            PhoneLinkHost.Pairing.Paired -= OnPaired;
            PhoneLinkHost.Pairing.Changed -= OnPairingChanged;
            PhoneLinkHost.Knocked -= OnKnocked;
            PhoneLinkHost.Pairing.CancelPairing(); // закрыли окно — код больше не действует
        };
    }

    private Control Step(string number, string text)
    {
        var badge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number, FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("BrushPanelSoft"));
        ((TextBlock)badge.Child!).Bind(TextBlock.ForegroundProperty, ((TextBlock)badge.Child!).GetResourceObservable("BrushText"));
        var label = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushText"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*") };
        Grid.SetColumn(label, 2);
        row.Children.Add(badge);
        row.Children.Add(label);
        return row;
    }

    private void LoadAddresses()
    {
        var ips = PhoneLinkHost.LocalIPv4();
        _suppressIp = true;
        try
        {
            _ipCombo.ItemsSource = ips;
            _ipCombo.SelectedIndex = ips.Count > 0 ? 0 : -1;
            _ipCombo.IsVisible = ips.Count > 1;
        }
        finally
        {
            _suppressIp = false;
        }
    }

    /// <summary>Новый ключ и новый QR (ожидание 5 минут).</summary>
    private void NewCode()
    {
        _paired = false;
        if (!PhoneLinkHost.IsListening)
        {
            _key = null;
            RedrawQr();
            return;
        }
        _key = PhoneLinkHost.BeginPairingKey();
        RedrawQr();
    }

    private void RedrawQr()
    {
        var ip = _ipCombo.SelectedItem as string;
        string? qr = null;
        if (_key is not null && ip is not null)
            qr = PhoneLinkHost.BuildQr(_key, ip);

        _qr.Data = qr;
        FitQr();
        _qrFrame.IsVisible = qr is not null;
        _address.Text = qr is null ? "" : T($"Адрес кассы в сети: {ip}:{PhoneLinkHost.Port}", $"Кассанын тармактагы дареги: {ip}:{PhoneLinkHost.Port}",
            $"Till address on the network: {ip}:{PhoneLinkHost.Port}", $"Kasanın ağ adresi: {ip}:{PhoneLinkHost.Port}", $"Kassaning tarmoqdagi manzili: {ip}:{PhoneLinkHost.Port}");

        if (!PhoneLinkHost.IsListening)
            SetStatus(T("Приём с телефонов выключен — включите его в настройках.", "Телефондон кабыл алуу өчүк — жөндөөлөрдөн күйгүзүңүз.",
                "Phone input is off — turn it on in settings.", "Telefondan giriş kapalı — ayarlardan açın.", "Telefondan qabul o'chiq — sozlamalardan yoqing."), "BrushDanger");
        else if (ip is null)
            SetStatus(T("Не найден адрес кассы в локальной сети. Подключите кассу к Wi-Fi или кабелю и откройте окно заново.",
                "Кассанын жергиликтүү тармактагы дареги табылган жок. Кассаны Wi-Fi же кабелге туташтырып, терезени кайра ачыңыз.",
                "The till's local network address was not found. Connect the till to Wi-Fi or a cable and reopen the window.",
                "Kasanın yerel ağ adresi bulunamadı. Kasayı Wi-Fi'ye veya kabloya bağlayıp pencereyi yeniden açın.",
                "Kassaning mahalliy tarmoq manzili topilmadi. Kassani Wi-Fi yoki kabelga ulab, oynani qayta oching."), "BrushDanger");
        else
            SetStatus(T("Ожидание телефона…", "Телефонду күтүүдө…", "Waiting for the phone…", "Telefon bekleniyor…", "Telefon kutilmoqda…"), "BrushText");
        UpdateTimer();
    }

    /// <summary>2026-11-27: целый размер модуля (не меньше 4 px) и белое поле в 4 модуля вокруг кода.</summary>
    private void FitQr()
    {
        var modules = _qr.Modules;
        if (modules <= 0)
            return;
        var cell = Math.Max(4, Math.Floor(QrTargetSize / modules));
        _qr.Size = cell * modules;
        _qrFrame.Padding = new Thickness(cell * 4);
        _qrFrame.CornerRadius = new CornerRadius(Math.Min(12, cell * 2));
    }

    private void SetStatus(string text, string brush)
    {
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(brush));
    }

    private void UpdateTimer()
    {
        if (_paired)
        {
            _timer.Text = T("Нужно подключить ещё один телефон — нажмите «Новый код».", "Дагы бир телефон туташтыруу керекпи — «Жаңы код» баскычын басыңыз.",
                "To connect another phone, press “New code”.", "Başka bir telefon bağlamak için «Yeni kod» düğmesine basın.", "Yana bir telefon ulash uchun «Yangi kod» tugmasini bosing.");
            return;
        }
        if (!PhoneLinkHost.Pairing.HasPending(out var until))
        {
            if (_key is not null)
            {
                _qrFrame.IsVisible = false;
                SetStatus(T("Код устарел — нажмите «Новый код».", "Код эскирди — «Жаңы код» баскычын басыңыз.", "The code expired — press “New code”.",
                    "Kodun süresi doldu — «Yeni kod» düğmesine basın.", "Kod eskirdi — «Yangi kod» tugmasini bosing."), "BrushDanger");
                _timer.Text = "";
            }
            return;
        }
        var left = until - DateTime.UtcNow;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        _timer.Text = T($"Код действует ещё {(int)left.TotalMinutes}:{left.Seconds:00}", $"Код дагы {(int)left.TotalMinutes}:{left.Seconds:00} аракеттенет",
            $"The code is valid for {(int)left.TotalMinutes}:{left.Seconds:00} more", $"Kod {(int)left.TotalMinutes}:{left.Seconds:00} daha geçerli", $"Kod yana {(int)left.TotalMinutes}:{left.Seconds:00} amal qiladi");
    }

    private void OnPaired(string id, string name) => Dispatcher.UIThread.Post(() =>
    {
        _paired = true;
        _qrFrame.IsVisible = false;
        SetStatus(T($"Телефон “{name}” подключён", $"“{name}” телефону туташты", $"Phone “{name}” is connected", $"“{name}” telefonu bağlandı", $"“{name}” telefoni ulandi"), "BrushSuccess");
        UpdateTimer();
        RefreshPhones();
    });

    /// <summary>2026-11-27: телефон достучался до кассы, но не подключился — говорим почему (адрес телефона, без ключей).</summary>
    private void OnKnocked(string address, string op, string code) => Dispatcher.UIThread.Post(() =>
    {
        if (op != "hello" || code == PhoneLinkCodes.Ok)
            return;
        _knock.Text = code switch
        {
            PhoneLinkCodes.UnknownPhone or PhoneLinkCodes.BadSig or PhoneLinkCodes.Revoked => T(
                $"Телефон {address} достучался до кассы, но код не подошёл (устарел или заменён). Нажмите «Новый код» и отсканируйте его снова.",
                $"{address} телефону кассага жетти, бирок код туура келген жок (эскирген же алмашкан). «Жаңы код» баскычын басып, кайра сканерлеңиз.",
                $"Phone {address} reached the till, but the code did not match (expired or replaced). Press “New code” and scan it again.",
                $"{address} telefonu kasaya ulaştı, ancak kod uymadı (süresi doldu veya değişti). «Yeni kod» düğmesine basıp tekrar tarayın.",
                $"{address} telefoni kassaga yetib keldi, lekin kod mos kelmadi (eskirgan yoki almashtirilgan). «Yangi kod» tugmasini bosib, qayta skanerlang."),
            PhoneLinkCodes.BadTime => T(
                $"Телефон {address} достучался до кассы, но время на телефоне отличается больше чем на 10 минут. Включите на телефоне «Время сети» и попробуйте снова.",
                $"{address} телефону кассага жетти, бирок телефондогу убакыт 10 мүнөттөн ашык айырмаланат. Телефондо «Тармак убактысын» күйгүзүп, кайра аракет кылыңыз.",
                $"Phone {address} reached the till, but its clock differs by more than 10 minutes. Turn on “Network time” on the phone and try again.",
                $"{address} telefonu kasaya ulaştı, ancak saati 10 dakikadan fazla farklı. Telefonda «Ağ saati»ni açıp tekrar deneyin.",
                $"{address} telefoni kassaga yetib keldi, lekin undagi vaqt 10 daqiqadan ko'proq farq qiladi. Telefonda «Tarmoq vaqti»ni yoqib, qayta urinib ko'ring."),
            _ => T($"Телефон {address} достучался до кассы, но подключение не удалось ({code}). Нажмите «Новый код» и попробуйте снова.",
                $"{address} телефону кассага жетти, бирок туташуу ишке ашкан жок ({code}). «Жаңы код» баскычын басып, кайра аракет кылыңыз.",
                $"Phone {address} reached the till, but connecting failed ({code}). Press “New code” and try again.",
                $"{address} telefonu kasaya ulaştı, ancak bağlantı başarısız oldu ({code}). «Yeni kod» düğmesine basıp tekrar deneyin.",
                $"{address} telefoni kassaga yetib keldi, lekin ulanib bo'lmadi ({code}). «Yangi kod» tugmasini bosib, qayta urinib ko'ring."),
        };
        _knock.IsVisible = true;
    });

    /// <summary>2026-11-27: пустит ли брандмауэр Windows телефон к кассе в сети выбранного адреса (проверка в фоне, только чтение).</summary>
    private void CheckFirewall()
    {
        var ip = _ipCombo.SelectedItem as string;
        var port = PhoneLinkHost.Port;
        var id = ++_firewallCheckId;
        _firewallWarn.IsVisible = false;
        if (!OperatingSystem.IsWindows() || ip is null || port == 0)
            return;
        _ = System.Threading.Tasks.Task.Run(() => PhoneLinkFirewall.Check(ip, port)).ContinueWith(t =>
        {
            if (t.IsFaulted || id != _firewallCheckId)
                return;
            var r = t.Result;
            if (r.Verdict is PhoneLinkFirewall.Verdict.Allowed or PhoneLinkFirewall.Verdict.Unknown)
                return;
            PosLogger.Log($"PHONELINK: брандмауэр Windows не пустит телефон к порту {port} ({r.Verdict}, профиль сети {r.Profile}).", "WARNING");
            _firewallWarn.Text = r.Profile == PhoneLinkFirewall.ProfilePublic
                ? T("Брандмауэр Windows не пустит телефон: эта сеть отмечена как «Общедоступная», а кассе разрешены только частные сети. Сделайте сеть частной: Параметры Windows → Сеть и Интернет → Wi-Fi (или Ethernet) → свойства сети → «Частная сеть».",
                    "Windows брандмауэри телефонду өткөрбөйт: бул тармак «Жалпыга ачык» деп белгиленген, ал эми кассага жеке тармактар гана уруксат. Тармакты жеке кылыңыз: Windows параметрлери → Тармак жана Интернет → Wi-Fi (же Ethernet) → тармак касиеттери → «Жеке тармак».",
                    "Windows Firewall will block the phone: this network is marked “Public”, and the till is allowed only on private networks. Make the network private: Windows Settings → Network & Internet → Wi-Fi (or Ethernet) → network properties → “Private network”.",
                    "Windows Güvenlik Duvarı telefonu engelleyecek: bu ağ «Genel» olarak işaretli ve kasaya yalnızca özel ağlarda izin var. Ağı özel yapın: Windows Ayarları → Ağ ve İnternet → Wi-Fi (veya Ethernet) → ağ özellikleri → «Özel ağ».",
                    "Windows brandmaueri telefonni o'tkazmaydi: bu tarmoq «Umumiy» deb belgilangan, kassaga esa faqat xususiy tarmoqlarda ruxsat bor. Tarmoqni xususiy qiling: Windows sozlamalari → Tarmoq va Internet → Wi-Fi (yoki Ethernet) → tarmoq xususiyatlari → «Xususiy tarmoq».")
                : T("Брандмауэр Windows не пустит телефон к кассе. Откройте «Брандмауэр Защитника Windows» → «Разрешение взаимодействия с приложением» → отметьте NurCRM (NurMarketKassa) для частной сети.",
                    "Windows брандмауэри телефонду кассага өткөрбөйт. «Windows Defender брандмауэрин» ачып → «Тиркеме менен өз ара аракеттенүүгө уруксат» → жеке тармак үчүн NurCRM (NurMarketKassa) белгилеңиз.",
                    "Windows Firewall will block the phone. Open “Windows Defender Firewall” → “Allow an app through the firewall” → tick NurCRM (NurMarketKassa) for private networks.",
                    "Windows Güvenlik Duvarı telefonu engelleyecek. «Windows Defender Güvenlik Duvarı» → «Bir uygulamaya güvenlik duvarından izin ver» → özel ağ için NurCRM (NurMarketKassa) işaretleyin.",
                    "Windows brandmaueri telefonni kassaga o'tkazmaydi. «Windows Defender brandmaueri» → «Ilovaga brandmauer orqali ruxsat berish» → xususiy tarmoq uchun NurCRM (NurMarketKassa) ni belgilang.");
            _firewallWarn.IsVisible = true;
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnPairingChanged() => Dispatcher.UIThread.Post(RefreshPhones);

    private void RefreshPhones()
    {
        _phones.Children.Clear();
        var list = PhoneLinkHost.Pairing.Phones();
        if (list.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Пока нет подключённых телефонов.", "Азырынча туташтырылган телефон жок.", "No phones connected yet.", "Henüz bağlı telefon yok.", "Hozircha ulangan telefon yo'q."),
                FontSize = 13,
            };
            empty.Bind(TextBlock.ForegroundProperty, empty.GetResourceObservable("BrushTextSoft"));
            _phones.Children.Add(empty);
            return;
        }
        foreach (var phone in list)
        {
            var seen = phone.LastSeenUtc ?? phone.PairedUtc;
            var name = new TextBlock { Text = phone.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
            var when = new TextBlock
            {
                Text = T("на связи: ", "байланышта: ", "last seen: ", "son görülme: ", "aloqada: ") + seen.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            };
            when.Bind(TextBlock.ForegroundProperty, when.GetResourceObservable("BrushTextSoft"));
            var disconnect = SectionCards.Secondary(T("Отключить", "Ажыратуу", "Disconnect", "Bağlantıyı kes", "Uzish"));
            disconnect.MinHeight = 44;
            Avalonia.Automation.AutomationProperties.SetName(disconnect, T("Отключить телефон ", "Телефонду ажыратуу ", "Disconnect phone ", "Telefonun bağlantısını kes ", "Telefonni uzish ") + phone.Name);
            var id = phone.Id;
            disconnect.Click += (_, _) => PhoneLinkHost.Revoke(id); // 2026-10-27: с проверкой права

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, when } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(disconnect, 1);
            row.Children.Add(info);
            row.Children.Add(disconnect);
            var card = new Border { Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(10), Child = row };
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanelSoft"));
            _phones.Children.Add(card);
        }
    }
}
