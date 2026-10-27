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
    private readonly ReceiptQrView _qr = new() { Size = 248, Dark = Brushes.Black, Light = Brushes.White };
    private readonly Border _qrFrame;
    private readonly TextBlock _timer = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _address = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _ipCombo = new() { Height = 44, MinWidth = 200, IsVisible = false };
    private readonly StackPanel _phones = new() { Spacing = 6 };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _key;
    private bool _paired;
    private bool _suppressIp;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public PhoneLinkPairWindow()
    {
        Title = T("Подключить телефон", "Телефонду туташтыруу", "Connect a phone", "Telefonu bağla", "Telefonni ulash");
        SectionCards.AsModal(this, 780);

        _qrFrame = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _qr,
        };
        Avalonia.Automation.AutomationProperties.SetName(_qrFrame, T("QR-код кассы для телефона", "Телефон үчүн кассанын QR-коду", "Till QR code for the phone", "Telefon için kasa QR kodu", "Telefon uchun kassa QR kodi"));

        var steps = new StackPanel { Spacing = 10 };
        steps.Children.Add(Step("1", T("Откройте приложение NurMarket на телефоне: Профиль → «Связь с кассой».",
            "Телефондогу NurMarket тиркемесин ачыңыз: Профиль → «Касса менен байланыш».",
            "Open the NurMarket app on the phone: Profile → “Till link”.",
            "Telefonda NurMarket uygulamasını açın: Profil → «Kasa bağlantısı».",
            "Telefonda NurMarket ilovasini oching: Profil → «Kassa bilan aloqa».")));
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
        wifi.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        steps.Children.Add(wifi);

        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _timer.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _address.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        steps.Children.Add(_status);
        steps.Children.Add(_timer);

        var newCode = SectionCards.Secondary(T("Новый код", "Жаңы код", "New code", "Yeni kod", "Yangi kod"));
        newCode.Click += (_, _) => NewCode();
        newCode.MinHeight = 44;
        newCode.HorizontalAlignment = HorizontalAlignment.Left;
        steps.Children.Add(newCode);

        var qrColumn = new StackPanel { Spacing = 8, Width = 280 };
        qrColumn.Children.Add(_qrFrame);
        qrColumn.Children.Add(_address);
        qrColumn.Children.Add(_ipCombo);
        _ipCombo.SelectionChanged += (_, _) => { if (!_suppressIp) RedrawQr(); };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,24,*") };
        Grid.SetColumn(qrColumn, 0);
        Grid.SetColumn(steps, 2);
        top.Children.Add(qrColumn);
        top.Children.Add(steps);

        var listTitle = new TextBlock { Text = T("Подключённые телефоны", "Туташтырылган телефондор", "Connected phones", "Bağlı telefonlar", "Ulangan telefonlar"), FontSize = 14, FontWeight = FontWeight.SemiBold };
        listTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

        var firewall = new TextBlock
        {
            Text = T("Если Windows спросит про брандмауэр («Разрешить доступ») — нажмите «Разрешить» для частной сети. Без этого телефон не достучится до кассы. Касса сама правил брандмауэра не создаёт.",
                "Эгер Windows брандмауэр жөнүндө сураса («Мүмкүнчүлүк берүү») — жеке тармак үчүн «Уруксат берүү» баскычын басыңыз. Антпесе телефон кассага жете албайт. Касса брандмауэр эрежелерин өзү түзбөйт.",
                "If Windows asks about the firewall (“Allow access”), press “Allow” for the private network. Without it the phone cannot reach the till. The till never creates firewall rules itself.",
                "Windows güvenlik duvarını sorarsa («Erişime izin ver»), özel ağ için «İzin ver» düğmesine basın. Aksi halde telefon kasaya ulaşamaz. Kasa güvenlik duvarı kuralı oluşturmaz.",
                "Windows brandmauer haqida so'rasa («Ruxsat berish»), xususiy tarmoq uchun «Ruxsat berish» tugmasini bosing. Aks holda telefon kassaga yeta olmaydi. Kassa brandmauer qoidalarini o'zi yaratmaydi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = OperatingSystem.IsWindows(),
        };
        firewall.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

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
            RefreshPhones();
            PhoneLinkHost.Pairing.Paired += OnPaired;
            PhoneLinkHost.Pairing.Changed += OnPairingChanged;
            _tick.Tick += (_, _) => UpdateTimer();
            _tick.Start();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            PhoneLinkHost.Pairing.Paired -= OnPaired;
            PhoneLinkHost.Pairing.Changed -= OnPairingChanged;
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
        badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
        ((TextBlock)badge.Child!).Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var label = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
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

    private void SetStatus(string text, string brush)
    {
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
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
            empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            _phones.Children.Add(empty);
            return;
        }
        foreach (var phone in list)
        {
            var seen = phone.LastSeenUtc ?? phone.PairedUtc;
            var name = new TextBlock { Text = phone.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var when = new TextBlock
            {
                Text = T("на связи: ", "байланышта: ", "last seen: ", "son görülme: ", "aloqada: ") + seen.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            };
            when.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
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
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
            _phones.Children.Add(card);
        }
    }
}
