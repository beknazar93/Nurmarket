using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: «Настройки весов TM-30F» (JHScale, серия TM-F / TM-xA) — просьба владельца
/// «добавь окно настроек для каждых весов». Протокол этих весов закрыт (загрузка — только их
/// программой «TM-xA data management software» или файлом A_xxx.TMS с флешки), поэтому окно
/// не шлёт весам команд, а:
/// • проверяет связь: ping + TCP 33581 (Spec166 «Scale's server port» по умолчанию, руководство
///   2014 V2.50A, таблица Spec — раздел «Definitions of Spec data parameters»);
/// • подсказывает, какие Spec выставить на весах (IP, маска, шлюз, режим сервер/клиент, IP ПК);
/// • собирает строку формата штрих-кода (Table 5-3/5-4) с живым примером и сверкой с кассой;
/// • ведёт к выгрузке файла товаров (окно «Весы», марка TM-30F).
/// На живых весах не проверено.
/// </summary>
public partial class TmScaleSettingsWindow : Window
{
    private TextBox _ipBox = null!;
    private Button _checkButton = null!;

    // Сеть.
    private IReadOnlyList<LocalSubnet> _subnets = Array.Empty<LocalSubnet>();
    private ComboBox _adapterBox = null!;
    private RadioButton _serverMode = null!;
    private CheckBox _dhcpBox = null!;
    private TextBox _scaleIpBox = null!;
    private TextBlock _specText = null!;

    // Штрих-код.
    private readonly ComboBox[] _source = new ComboBox[6];
    private readonly NumericUpDown[] _length = new NumericUpDown[6];
    private readonly NumericUpDown[] _shift = new NumericUpDown[6];
    private readonly ComboBox[] _overflow = new ComboBox[6];
    private TextBox _formatBox = null!;
    private NumericUpDown _flag = null!;
    private NumericUpDown _samplePlu = null!;
    private NumericUpDown _sampleGrams = null!;
    private NumericUpDown _samplePrice = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;
    private bool _syncing;

    public TmScaleSettingsWindow()
    {
        InitializeComponent();
        Title = L("Настройки весов TM-30F", "TM-30F таразасынын жөндөөлөрү", "TM-30F scale settings", "TM-30F tartı ayarları", "TM-30F tarozi sozlamalari");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Весы TM-30F (JHScale, серия TM-F / TM-xA) загружаются только их программой «TM-xA data management software» (вход admin / 200806) или файлом с флешки — протокол закрыт. Здесь: проверка связи, какие параметры Spec выставить на весах и формат штрих-кода, который поймёт касса.",
            "TM-30F таразасы (JHScale, TM-F / TM-xA сериясы) өз программасы «TM-xA data management software» (кирүү admin / 200806) же флешкадагы файл аркылуу гана жүктөлөт — протокол жабык. Бул жерде: байланышты текшерүү, таразага кайсы Spec параметрлерин коюу жана касса түшүнө турган штрих-код форматы.",
            "TM-30F scales (JHScale, TM-F / TM-xA series) are loaded only by their “TM-xA data management software” (login admin / 200806) or a file from a USB stick — the protocol is closed. Here: connection check, which Spec parameters to set on the scale and a barcode format the till understands.",
            "TM-30F tartılar (JHScale, TM-F / TM-xA serisi) yalnızca kendi “TM-xA data management software” programıyla (giriş admin / 200806) veya USB bellekteki dosyayla yüklenir — protokol kapalı. Burada: bağlantı kontrolü, tartıda hangi Spec parametrelerinin ayarlanacağı ve kasanın anlayacağı barkod biçimi.",
            "TM-30F tarozilari (JHScale, TM-F / TM-xA seriyasi) faqat o‘z «TM-xA data management software» dasturi (kirish admin / 200806) yoki fleshkadagi fayl orqali yuklanadi — protokol yopiq. Bu yerda: aloqani tekshirish, tarozida qaysi Spec parametrlarini qo‘yish va kassa tushunadigan shtrix-kod formati.");

        Tabs.Items.Add(MakeTab(L("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), BuildConnectionTab()));
        Tabs.Items.Add(MakeTab(L("Сеть (Spec)", "Тармак (Spec)", "Network (Spec)", "Ağ (Spec)", "Tarmoq (Spec)"), BuildNetworkTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        UpdateHeader();
        ShowResult(L("Введите IP весов и нажмите «Проверить связь» или найдите весы в сети.", "Таразанын IP'син киргизип «Байланышты текшерүү» басыңыз же таразаны тармактан табыңыз.", "Enter the scale IP and press “Check connection”, or find the scale on the network.", "Tartı IP'sini girip “Bağlantıyı kontrol et”e basın veya tartıyı ağda bulun.", "Tarozi IP'sini kiriting va «Aloqani tekshirish»ni bosing yoki tarozini tarmoqdan toping."), false);
    }

    private static TabItem MakeTab(string header, Control content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content } };

    private void UpdateHeader()
    {
        var ip = (_ipBox.Text ?? "").Trim();
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ") + (ip.Length > 0 ? ip : "—")
            + $" · TCP {ScaleNetworkScanner.TmServerPort}";
    }

    // =====================================================================================
    // Подключение
    // =====================================================================================

    private Control BuildConnectionTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Адрес весов", "Таразанын дареги", "Scale address", "Tartı adresi", "Tarozi manzili"),
            L("По умолчанию весы берут адрес от роутера (DHCP) — узнать его проще всего поиском в сети. Постоянный адрес задаётся на весах в Spec150–153 (вкладка «Сеть»).",
              "Демейки боюнча тараза даректи роутерден алат (DHCP) — аны тармактан издөө менен табуу жеңил. Туруктуу дарек таразада Spec150–153'тө коюлат («Тармак» өтмөгү).",
              "By default the scale gets its address from the router (DHCP) — the easiest way to learn it is the network search. A fixed address is set on the scale in Spec150–153 (“Network” tab).",
              "Varsayılan olarak tartı adresini yönlendiriciden alır (DHCP) — en kolayı ağ aramasıdır. Sabit adres tartıda Spec150–153'te ayarlanır (“Ağ” sekmesi).",
              "Odatda tarozi manzilni routerdan oladi (DHCP) — uni tarmoqdan qidirish eng oson. Doimiy manzil tarozida Spec150–153 da beriladi («Tarmoq» bo‘limi)."),
            out var body);

        _ipBox = new TextBox { Text = IpOf(BrandTm), Watermark = "192.168.1.50", MinWidth = 220 };
        _ipBox.LostFocus += (_, _) => { SaveIp(); UpdateHeader(); UpdateSpec(); };
        body.Children.Add(Row(L("IP-адрес весов", "Таразанын IP-дареги", "Scale IP address", "Tartı IP adresi", "Tarozi IP manzili"), _ipBox));
        body.Children.Add(Text(L(
            $"«Проверить связь»: ping и TCP-подключение к порту {ScaleNetworkScanner.TmServerPort} — это «порт сервера весов» (Spec166) по руководству; он открыт, когда весы в режиме сервера (Spec043 = 1, по умолчанию). Данные этим подключением касса не передаёт.",
            $"«Байланышты текшерүү»: ping жана {ScaleNetworkScanner.TmServerPort} портуна TCP-туташуу — бул колдонмо боюнча «тараза серверинин порту» (Spec166); тараза сервер режиминде (Spec043 = 1, демейки) болгондо ачык. Касса бул туташуу аркылуу маалымат бербейт.",
            $"“Check connection”: ping and a TCP connection to port {ScaleNetworkScanner.TmServerPort} — the “scale server port” (Spec166) per the manual; it is open when the scale is in server mode (Spec043 = 1, default). The till sends no data over it.",
            $"“Bağlantıyı kontrol et”: ping ve {ScaleNetworkScanner.TmServerPort} portuna TCP bağlantısı — kılavuza göre “tartı sunucu portu” (Spec166); tartı sunucu modundayken (Spec043 = 1, varsayılan) açıktır. Kasa bu bağlantıyla veri göndermez.",
            $"«Aloqani tekshirish»: ping va {ScaleNetworkScanner.TmServerPort} portiga TCP-ulanish — qo‘llanma bo‘yicha «tarozi server porti» (Spec166); tarozi server rejimida (Spec043 = 1, standart) bo‘lganda ochiq. Kassa bu ulanish orqali ma’lumot yubormaydi."), "hint"));

        _checkButton = MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), true, async (_, _) => await CheckAsync().ConfigureAwait(true));
        body.Children.Add(ButtonRow(
            _checkButton,
            MakeButton(L("Найти весы в сети…", "Тармактан тараза табуу…", "Find scales on the network…", "Ağda tartı bul…", "Tarmoqda tarozi topish…"), false, async (_, _) =>
            {
                if (await OpenScanAsync(this, BrandTm).ConfigureAwait(true))
                {
                    _ipBox.Text = IpOf(BrandTm);
                    UpdateHeader();
                    UpdateSpec();
                }
            }),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveIp();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        panel.Children.Add(card);
        return panel;
    }

    private void SaveIp()
    {
        var prefs = UserPreferences.Instance;
        prefs.TmScaleIp = (_ipBox.Text ?? "").Trim();
        prefs.SaveToDisk();
    }

    private async Task CheckAsync()
    {
        SaveIp();
        var ip = (_ipBox.Text ?? "").Trim();
        if (!IPAddress.TryParse(ip, out var address))
        {
            ShowResult(L("Введите IP-адрес весов.", "Таразанын IP-дарегин киргизиңиз.", "Enter the scale IP address.", "Tartı IP adresini girin.", "Tarozi IP manzilini kiriting."), true);
            return;
        }
        _checkButton.IsEnabled = false;
        ShowResult(L("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), false);
        try
        {
            var (pingOk, rtt) = await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true);
            var tcpOk = await ScaleNetworkScanner.TcpPortOpenAsync(ip, ScaleNetworkScanner.TmServerPort, 1500).ConfigureAwait(true);
            var mac = ScaleNetworkScanner.ReadArpTable().FirstOrDefault(a => a.Address == ScaleNetworkScanner.ToNumber(address)).Mac;

            var parts = new List<string>
            {
                pingOk ? $"ping {rtt} " + L("мс", "мс", "ms", "ms", "ms") : L("ping: нет ответа", "ping: жооп жок", "ping: no reply", "ping: yanıt yok", "ping: javob yo‘q"),
                $"TCP {ScaleNetworkScanner.TmServerPort}: " + (tcpOk ? L("открыт", "ачык", "open", "açık", "ochiq") : L("закрыт", "жабык", "closed", "kapalı", "yopiq")),
            };
            if (mac is not null)
                parts.Add("MAC " + mac);

            string summary;
            bool error;
            if (tcpOk)
            {
                summary = L("Весы на связи: порт сервера весов TM открыт. ", "Тараза байланышта: TM таразасынын сервер порту ачык. ", "Scale connected: the TM scale server port is open. ", "Tartı bağlı: TM tartı sunucu portu açık. ", "Tarozi aloqada: TM tarozi server porti ochiq. ");
                error = false;
            }
            else if (pingOk || mac is not null)
            {
                summary = L("Устройство в сети, но порт 33581 закрыт: весы в режиме клиента (Spec043 = 2), порт изменён (Spec166) или это не весы. ", "Түзмөк тармакта, бирок 33581 порт жабык: тараза клиент режиминде (Spec043 = 2), порт өзгөртүлгөн (Spec166) же бул тараза эмес. ", "The device is on the network but port 33581 is closed: the scale is in client mode (Spec043 = 2), the port was changed (Spec166) or it is not the scale. ", "Cihaz ağda ama 33581 portu kapalı: tartı istemci modunda (Spec043 = 2), port değişmiş (Spec166) veya bu tartı değil. ", "Qurilma tarmoqda, lekin 33581 port yopiq: tarozi mijoz rejimida (Spec043 = 2), port o‘zgartirilgan (Spec166) yoki bu tarozi emas. ");
                error = false;
            }
            else
            {
                summary = L("По этому адресу никого нет. Проверьте кабель, что весы включены и адрес (при DHCP он мог смениться — найдите весы поиском). ", "Бул даректе эч ким жок. Кабелди, тараза күйүк экенин жана даректи текшериңиз (DHCP'де өзгөрүшү мүмкүн — издөө менен табыңыз). ", "Nobody at this address. Check the cable, that the scale is on, and the address (with DHCP it may have changed — use the search). ", "Bu adreste kimse yok. Kabloyu, tartının açık olduğunu ve adresi kontrol edin (DHCP ile değişmiş olabilir — aramayı kullanın). ", "Bu manzilda hech kim yo‘q. Kabelni, tarozi yoqilganini va manzilni tekshiring (DHCP da o‘zgargan bo‘lishi mumkin — qidiruvdan foydalaning). ");
                error = true;
            }
            ShowResult(summary + string.Join(" · ", parts), error);
            PosLogger.Log($"TM-30F: проверка связи {ip}: ping={pingOk}, tcp33581={tcpOk}, mac={mac ?? "-"}", "SCALES");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"TM-30F: проверка связи: {ex}", "SCALES");
            ShowResult(ex.Message, true);
        }
        finally
        {
            _checkButton.IsEnabled = true;
        }
    }

    // =====================================================================================
    // Сеть (Spec)
    // =====================================================================================

    private Control BuildNetworkTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Какие Spec выставить на весах", "Таразага кайсы Spec коюу керек", "Which Spec to set on the scale", "Tartıda hangi Spec ayarlanmalı", "Tarozida qaysi Spec qo‘yish kerak"),
            L("Значения считаются по сети этого компьютера. Как вводить на весах: [Prog] → [1] → [2] (P12 SP.000) → [×] номер Spec → [Confirm] → значение → [Amend] сохранить. После смены сети перезапустите весы.",
              "Маанилер бул компьютердин тармагы боюнча эсептелет. Таразада кантип киргизүү: [Prog] → [1] → [2] (P12 SP.000) → [×] Spec номери → [Confirm] → маани → [Amend] сактоо. Тармакты өзгөрткөндөн кийин таразаны кайра күйгүзүңүз.",
              "Values are computed from this computer's network. On the scale: [Prog] → [1] → [2] (P12 SP.000) → [×] Spec number → [Confirm] → value → [Amend] to save. Restart the scale after network changes.",
              "Değerler bu bilgisayarın ağından hesaplanır. Tartıda: [Prog] → [1] → [2] (P12 SP.000) → [×] Spec numarası → [Confirm] → değer → [Amend] kaydet. Ağ değişince tartıyı yeniden başlatın.",
              "Qiymatlar shu kompyuter tarmog‘idan hisoblanadi. Tarozida: [Prog] → [1] → [2] (P12 SP.000) → [×] Spec raqami → [Confirm] → qiymat → [Amend] saqlash. Tarmoq o‘zgargach tarozini qayta yoqing."),
            out var body);

        _subnets = ScaleNetworkScanner.GetLocalSubnets();
        _adapterBox = new ComboBox { MinWidth = 360 };
        foreach (var s in _subnets)
            _adapterBox.Items.Add($"{s.AdapterName} — {s.LocalAddress}/{s.PrefixLength}");
        _adapterBox.SelectedIndex = _subnets.Count > 0 ? 0 : -1;
        _adapterBox.SelectionChanged += (_, _) => UpdateSpec();
        body.Children.Add(Row(L("Сеть компьютера", "Компьютердин тармагы", "Computer network", "Bilgisayar ağı", "Kompyuter tarmog‘i"), _adapterBox));

        _serverMode = new RadioButton { GroupName = "TmMode", IsChecked = true, Content = L("Весы — сервер, компьютер подключается к ним (Spec043 = 1, по умолчанию)", "Тараза — сервер, компьютер ага туташат (Spec043 = 1, демейки)", "Scale is the server, the computer connects to it (Spec043 = 1, default)", "Tartı sunucu, bilgisayar ona bağlanır (Spec043 = 1, varsayılan)", "Tarozi — server, kompyuter unga ulanadi (Spec043 = 1, standart)") };
        var clientMode = new RadioButton { GroupName = "TmMode", Content = L("Весы — клиент, сами подключаются к компьютеру (Spec043 = 2; если сети разные)", "Тараза — клиент, компьютерге өзү туташат (Spec043 = 2; тармактар башка болсо)", "Scale is a client and connects to the computer (Spec043 = 2; if networks differ)", "Tartı istemci, bilgisayara kendisi bağlanır (Spec043 = 2; ağlar farklıysa)", "Tarozi — mijoz, kompyuterga o‘zi ulanadi (Spec043 = 2; tarmoqlar har xil bo‘lsa)") };
        _serverMode.IsCheckedChanged += (_, _) => UpdateSpec();
        body.Children.Add(_serverMode);
        body.Children.Add(clientMode);

        _dhcpBox = new CheckBox { IsChecked = string.IsNullOrWhiteSpace(UserPreferences.Instance.TmScaleIp), Content = L("Адрес весов от роутера (DHCP) — Spec153 = 0", "Таразанын дареги роутерден (DHCP) — Spec153 = 0", "Scale address from the router (DHCP) — Spec153 = 0", "Tartı adresi yönlendiriciden (DHCP) — Spec153 = 0", "Tarozi manzili routerdan (DHCP) — Spec153 = 0") };
        _dhcpBox.IsCheckedChanged += (_, _) => UpdateSpec();
        body.Children.Add(_dhcpBox);

        _scaleIpBox = new TextBox { Text = UserPreferences.Instance.TmScaleIp ?? "", Watermark = "192.168.1.50", MinWidth = 220 };
        _scaleIpBox.LostFocus += (_, _) => UpdateSpec();
        body.Children.Add(Row(L("Постоянный адрес весов", "Таразанын туруктуу дареги", "Fixed scale address", "Sabit tartı adresi", "Tarozining doimiy manzili"), _scaleIpBox));

        _specText = new TextBlock { FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush(this, "BrushText", Brushes.Black) };
        body.Children.Add(new Border { Classes = { "card" }, Child = _specText });
        panel.Children.Add(card);
        UpdateSpec();
        return panel;
    }

    private void UpdateSpec()
    {
        if (_specText is null)
            return;
        var subnet = _adapterBox.SelectedIndex >= 0 && _adapterBox.SelectedIndex < _subnets.Count ? _subnets[_adapterBox.SelectedIndex] : null;
        var client = _serverMode.IsChecked != true;
        var dhcp = _dhcpBox.IsChecked == true;
        _scaleIpBox.IsEnabled = !dhcp;

        static string Octets(IPAddress? a) => a is null ? "—" : string.Join("  ", a.GetAddressBytes().Select(b => b.ToString(CultureInfo.InvariantCulture)));

        var lines = new List<string>
        {
            "Spec043 = " + (client ? "2" : "1") + "   " + (client ? L("(клиент)", "(клиент)", "(client)", "(istemci)", "(mijoz)") : L("(сервер)", "(сервер)", "(server)", "(sunucu)", "(server)")),
        };

        if (dhcp)
        {
            lines.Add("Spec153 = 0   " + L("(DHCP; Spec150–152 не важны)", "(DHCP; Spec150–152 маанилүү эмес)", "(DHCP; Spec150–152 do not matter)", "(DHCP; Spec150–152 önemsiz)", "(DHCP; Spec150–152 muhim emas)"));
        }
        else
        {
            var ok = IPAddress.TryParse((_scaleIpBox.Text ?? "").Trim(), out var scaleIp);
            lines.Add("Spec150–153 = " + (ok ? Octets(scaleIp) : L("введите адрес выше", "жогоруга дарек киргизиңиз", "enter the address above", "yukarıya adres girin", "yuqoriga manzil kiriting")));
            if (ok && scaleIp!.GetAddressBytes()[3] == 0)
                lines.Add("  ⚠ " + L("последнее число 0 весы считают DHCP", "акыркы сан 0 болсо тараза DHCP деп эсептейт", "a last number of 0 means DHCP to the scale", "son sayı 0 ise tartı DHCP sayar", "oxirgi son 0 bo‘lsa tarozi DHCP deb hisoblaydi"));
            if (ok && subnet is not null)
            {
                var n = ScaleNetworkScanner.ToNumber(scaleIp!);
                if (n < subnet.FirstHost || n > subnet.LastHost)
                    lines.Add("  ⚠ " + L($"адрес не из сети компьютера ({subnet.RangeText})", $"дарек компьютердин тармагынан эмес ({subnet.RangeText})", $"the address is outside the computer network ({subnet.RangeText})", $"adres bilgisayar ağının dışında ({subnet.RangeText})", $"manzil kompyuter tarmog‘idan emas ({subnet.RangeText})"));
            }
            var gw = subnet?.Gateway;
            lines.Add("Spec158–161 = " + (gw is null ? L("шлюза нет — оставьте 192 168 0 1", "шлюз жок — 192 168 0 1 калтырыңыз", "no gateway — keep 192 168 0 1", "ağ geçidi yok — 192 168 0 1 bırakın", "shlyuz yo‘q — 192 168 0 1 qoldiring") : Octets(gw)) + "   " + L("(шлюз)", "(шлюз)", "(gateway)", "(ağ geçidi)", "(shlyuz)"));
            var prefix = subnet is null ? 24 : Math.Clamp(subnet.PrefixLength, 0, 32);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            lines.Add("Spec162–165 = " + Octets(ScaleNetworkScanner.ToAddress(mask)) + "   " + L("(маска)", "(маска)", "(mask)", "(maske)", "(niqob)"));
        }

        if (client)
            lines.Add("Spec154–157 = " + Octets(subnet?.LocalAddress) + "   " + L("(IP этого компьютера)", "(бул компьютердин IP'си)", "(this computer's IP)", "(bu bilgisayarın IP'si)", "(shu kompyuter IP'si)"));

        lines.Add("Spec166–169 = 33581 · 33582 · 33583 · 33584   " + L("(порты — НЕ менять)", "(порттор — ӨЗГӨРТПӨҢҮЗ)", "(ports — do NOT change)", "(portlar — DEĞİŞTİRMEYİN)", "(portlar — O‘ZGARTIRMANG)"));
        _specText.Text = string.Join(Environment.NewLine, lines);
    }

    // =====================================================================================
    // Штрих-код
    // =====================================================================================

    private static readonly (char Source, Func<string> Title)[] SourceTitles =
    {
        ('A', () => L("A — не печатать", "A — баспоо", "A — do not print", "A — basma", "A — chop etmaslik")),
        ('B', () => L("B — флаг (Spec002)", "B — желек (Spec002)", "B — flag (Spec002)", "B — bayrak (Spec002)", "B — bayroq (Spec002)")),
        ('E', () => L("E — номер PLU", "E — PLU номери", "E — PLU number", "E — PLU numarası", "E — PLU raqami")),
        ('F', () => L("F — код товара (Item-Code)", "F — товар коду (Item-Code)", "F — item code (Item-Code)", "F — ürün kodu (Item-Code)", "F — tovar kodi (Item-Code)")),
        ('J', () => L("J — вес / количество", "J — салмак / саны", "J — weight / count", "J — ağırlık / adet", "J — vazn / soni")),
        ('K', () => L("K — сумма PLU", "K — PLU суммасы", "K — PLU total price", "K — PLU tutarı", "K — PLU summasi")),
    };

    private Control BuildBarcodeTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Конструктор формата штрих-кода", "Штрих-код форматынын конструктору", "Barcode format builder", "Barkod biçimi oluşturucu", "Shtrix-kod formati konstruktori"),
            L("Формат весов — 6 групп по 4 знака: источник, длина, сдвиг, переполнение. Заводской «B-Item 1» (B201E500K500…) печатает СУММУ. Для кассы лучше вес: флаг 20 + PLU + вес.",
              "Тараза форматы — 4 белгиден 6 топ: булак, узундук, жылдыруу, ашып кетүү. Заводдук «B-Item 1» (B201E500K500…) СУММАНЫ басат. Касса үчүн салмак жакшы: желек 20 + PLU + салмак.",
              "The scale format is 6 groups of 4 characters: source, length, shift, overflow. The factory “B-Item 1” (B201E500K500…) prints the TOTAL PRICE. Weight is better for the till: flag 20 + PLU + weight.",
              "Tartı biçimi 4 karakterlik 6 gruptur: kaynak, uzunluk, kaydırma, taşma. Fabrika “B-Item 1” (B201E500K500…) TUTARI basar. Kasa için ağırlık daha iyi: bayrak 20 + PLU + ağırlık.",
              "Tarozi formati — 4 belgidan 6 guruh: manba, uzunlik, siljitish, to‘lib ketish. Zavod «B-Item 1» (B201E500K500…) SUMMANI chop etadi. Kassa uchun vazn yaxshiroq: bayroq 20 + PLU + vazn."),
            out var body);
        body.Children.Add(Text(CompanyFormatText(), "hint"));

        var headers = new Grid { ColumnDefinitions = new ColumnDefinitions("40,230,125,125,190") };
        string[] titles =
        {
            "#",
            L("Источник", "Булак", "Source", "Kaynak", "Manba"),
            L("Длина", "Узундук", "Length", "Uzunluk", "Uzunlik"),
            L("Сдвиг", "Жылдыруу", "Shift", "Kaydırma", "Siljitish"),
            L("Если не влезает", "Батпаса", "On overflow", "Taşarsa", "Sig‘masa"),
        };
        for (var i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "hint" } };
            Grid.SetColumn(t, i);
            headers.Children.Add(t);
        }
        body.Children.Add(headers);

        string[] overflowTitles =
        {
            L("0 — не печатать", "0 — баспоо", "0 — do not print", "0 — basma", "0 — chop etmaslik"),
            L("1 — обрезать", "1 — кесүү", "1 — truncate", "1 — kes", "1 — kesish"),
            L("2 — заполнить 0", "2 — 0 менен толтуруу", "2 — fill with 0", "2 — 0 ile doldur", "2 — 0 bilan to‘ldirish"),
            L("3 — заполнить 9", "3 — 9 менен толтуруу", "3 — fill with 9", "3 — 9 ile doldur", "3 — 9 bilan to‘ldirish"),
        };
        for (var g = 0; g < 6; g++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("40,230,125,125,190") };
            row.Children.Add(new TextBlock { Text = (g + 1).ToString(CultureInfo.InvariantCulture), Classes = { "label" } });
            _source[g] = new ComboBox { MinWidth = 220 };
            foreach (var (_, title) in SourceTitles)
                _source[g].Items.Add(title());
            _length[g] = new NumericUpDown { Minimum = 0, Maximum = 9, Increment = 1, FormatString = "0", Value = 0, Width = 115 };
            _shift[g] = new NumericUpDown { Minimum = 0, Maximum = 9, Increment = 1, FormatString = "0", Value = 0, Width = 115 };
            _overflow[g] = new ComboBox { MinWidth = 180 };
            foreach (var o in overflowTitles)
                _overflow[g].Items.Add(o);
            Grid.SetColumn(_source[g], 1);
            Grid.SetColumn(_length[g], 2);
            Grid.SetColumn(_shift[g], 3);
            Grid.SetColumn(_overflow[g], 4);
            row.Children.Add(_source[g]);
            row.Children.Add(_length[g]);
            row.Children.Add(_shift[g]);
            row.Children.Add(_overflow[g]);
            body.Children.Add(row);

            _source[g].SelectionChanged += (_, _) => GroupsChanged();
            _length[g].ValueChanged += (_, _) => GroupsChanged();
            _shift[g].ValueChanged += (_, _) => GroupsChanged();
            _overflow[g].SelectionChanged += (_, _) => GroupsChanged();
        }

        _formatBox = new TextBox { MaxLength = 24, FontFamily = new FontFamily("Consolas, Segoe UI"), MinWidth = 280 };
        _formatBox.TextChanged += (_, _) => FormatTextChanged();
        body.Children.Add(Row(L("Строка формата (Des)", "Формат сабы (Des)", "Format string (Des)", "Biçim dizesi (Des)", "Format qatori (Des)"), _formatBox));

        _flag = new NumericUpDown { Minimum = 0, Maximum = 9999999, Increment = 1, FormatString = "0", Value = 20, MinWidth = 140 };
        _flag.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Флаг (Spec002)", "Желек (Spec002)", "Flag (Spec002)", "Bayrak (Spec002)", "Bayroq (Spec002)"), _flag));

        _samplePlu = new NumericUpDown { Minimum = 1, Maximum = 999999, Increment = 1, FormatString = "0", Value = 12, MinWidth = 140 };
        _samplePlu.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("PLU / код для примера", "Мисал үчүн PLU / код", "Sample PLU / code", "Örnek PLU / kod", "Namuna PLU / kod"), _samplePlu));
        _sampleGrams = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0", Value = 392, MinWidth = 140 };
        _sampleGrams.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Масса для примера, г", "Мисал үчүн масса, г", "Sample weight, g", "Örnek ağırlık, g", "Namuna uchun massa, g"), _sampleGrams));
        _samplePrice = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0.00", Value = 250, MinWidth = 140 };
        _samplePrice.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Цена за кг, сом", "Кг баасы, сом", "Price per kg, som", "Kg fiyatı, som", "Kg narxi, so‘m"), _samplePrice));

        _sampleText = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Segoe UI"), Foreground = ThemeBrush(this, "BrushText", Brushes.Black) };
        body.Children.Add(_sampleText);
        _verdictText = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_verdictText);

        body.Children.Add(ButtonRow(
            MakeButton(L("Рекомендуемый для кассы", "Касса үчүн сунушталган", "Recommended for the till", "Kasa için önerilen", "Kassa uchun tavsiya"), true, (_, _) =>
            {
                var byWeight = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
                SetFormat(JhScaleBarcodeFormat.Recommended(WeightBarcodeParser.Layout, byWeight));
                _flag.Value = byWeight ? 20 : 25;
            }),
            MakeButton(L("Заводской B-Item 1", "Заводдук B-Item 1", "Factory B-Item 1", "Fabrika B-Item 1", "Zavod B-Item 1"), false, (_, _) => SetFormat(JhScaleBarcodeFormat.FactoryItem1)),
            MakeButton(L("Сохранить формат", "Форматты сактоо", "Save format", "Biçimi kaydet", "Formatni saqlash"), false, (_, _) =>
            {
                var groups = JhScaleBarcodeFormat.Parse(_formatBox.Text);
                if (groups is null)
                {
                    ShowResult(L("Строка формата должна быть из 24 знаков: 6 групп «буква + 3 цифры».", "Формат сабы 24 белгиден болушу керек: 6 топ «тамга + 3 сан».", "The format string must be 24 characters: 6 groups of “letter + 3 digits”.", "Biçim dizesi 24 karakter olmalı: 6 grup “harf + 3 rakam”.", "Format qatori 24 belgidan iborat bo‘lishi kerak: 6 guruh «harf + 3 raqam»."), true);
                    return;
                }
                UserPreferences.Instance.TmScaleBarcodeFormat = JhScaleBarcodeFormat.Format(groups);
                UserPreferences.Instance.SaveToDisk();
                ShowResult(L("Формат сохранён в кассе. На весы его нужно ввести вручную (см. ниже) или программой весов.", "Формат кассада сакталды. Таразага аны колго (төмөндү караңыз) же тараза программасы менен киргизүү керек.", "Format saved in the till. Enter it on the scale by hand (see below) or with the scale software.", "Biçim kasada kaydedildi. Tartıya elle (aşağıya bakın) veya tartı programıyla girin.", "Format kassada saqlandi. Uni taroziga qo‘lda (pastga qarang) yoki tarozi dasturi bilan kiriting."), false);
            })));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Как ввести формат на весах", "Форматты таразага кантип киргизүү", "How to enter the format on the scale", "Biçim tartıya nasıl girilir", "Formatni taroziga qanday kiritish"),
            L("1) [Prog] → [2] → [5] (P25) → номер штрих-кода 10 (свои форматы — 10…99) → [→] … BAR.02 Type = 1 (EAN13) → [→] … BAR.06 Des: строка формата → [Amend]. 2) [Prog] → [1] → [2] (Spec): Spec001 = 10 (формат штрих-кода товара), Spec002 = флаг (например 20). 3) Напечатайте этикетку и отсканируйте её кассой. Допущения: вес J печатается без точки (при весе в кг с 3 знаками — граммы), сумма K — в тыйынах; сдвиг — отбрасывание младших цифр. В руководстве это не расписано — проверьте первой этикеткой.",
              "1) [Prog] → [2] → [5] (P25) → штрих-код номери 10 (өз форматтар — 10…99) → [→] … BAR.02 Type = 1 (EAN13) → [→] … BAR.06 Des: формат сабы → [Amend]. 2) [Prog] → [1] → [2] (Spec): Spec001 = 10 (товардын штрих-код форматы), Spec002 = желек (мисалы 20). 3) Этикетка басып, аны касса менен сканерлеңиз. Божомолдор: J салмагы чекитсиз басылат (кг 3 белги болсо — грамм), K суммасы — тыйында; жылдыруу — кичи сандарды таштоо. Колдонмодо жазылган эмес — биринчи этикетка менен текшериңиз.",
              "1) [Prog] → [2] → [5] (P25) → barcode number 10 (custom formats 10…99) → [→] … BAR.02 Type = 1 (EAN13) → [→] … BAR.06 Des: the format string → [Amend]. 2) [Prog] → [1] → [2] (Spec): Spec001 = 10 (item barcode format), Spec002 = flag (e.g. 20). 3) Print a label and scan it with the till. Assumptions: weight J is printed without a decimal point (grams for kg with 3 decimals), amount K in tiyin; shift drops lower digits. Not spelled out in the manual — check with the first label.",
              "1) [Prog] → [2] → [5] (P25) → barkod numarası 10 (özel biçimler 10…99) → [→] … BAR.02 Type = 1 (EAN13) → [→] … BAR.06 Des: biçim dizesi → [Amend]. 2) [Prog] → [1] → [2] (Spec): Spec001 = 10 (ürün barkod biçimi), Spec002 = bayrak (ör. 20). 3) Etiket basıp kasayla okutun. Varsayımlar: J ağırlığı ondalıksız basılır (3 ondalıklı kg için gram), K tutarı tiyin; kaydırma alt haneleri atar. Kılavuzda açık değil — ilk etiketle kontrol edin.",
              "1) [Prog] → [2] → [5] (P25) → shtrix-kod raqami 10 (o‘z formatlar — 10…99) → [→] … BAR.02 Type = 1 (EAN13) → [→] … BAR.06 Des: format qatori → [Amend]. 2) [Prog] → [1] → [2] (Spec): Spec001 = 10 (tovar shtrix-kod formati), Spec002 = bayroq (masalan 20). 3) Yorliq chop etib, kassada skanerlang. Farazlar: J vazni nuqtasiz chop etiladi (kg 3 kasrda — gramm), K summasi — tiyinda; siljitish — kichik raqamlarni tashlash. Qo‘llanmada yozilmagan — birinchi yorliq bilan tekshiring."),
            out _));

        var saved = UserPreferences.Instance.TmScaleBarcodeFormat;
        var byWeightNow = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        SetFormat(JhScaleBarcodeFormat.Parse(saved) is not null ? saved! : JhScaleBarcodeFormat.Recommended(WeightBarcodeParser.Layout, byWeightNow));
        return panel;
    }

    private void SetFormat(string format)
    {
        _formatBox.Text = format; // FormatTextChanged разнесёт по группам
    }

    private void FormatTextChanged()
    {
        if (_syncing)
            return;
        var groups = JhScaleBarcodeFormat.Parse(_formatBox.Text);
        if (groups is null)
        {
            UpdateSample();
            return;
        }
        _syncing = true;
        try
        {
            for (var g = 0; g < 6; g++)
            {
                var idx = Array.FindIndex(SourceTitles, s => s.Source == groups[g].Source);
                if (idx < 0)
                {
                    // Буква, которую конструктор не предлагает, — показываем как есть.
                    _source[g].Items.Add(groups[g].Source + " — ?");
                    idx = _source[g].Items.Count - 1;
                }
                _source[g].SelectedIndex = idx;
                _length[g].Value = groups[g].Length;
                _shift[g].Value = groups[g].Shift;
                _overflow[g].SelectedIndex = groups[g].Overflow;
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateSample();
    }

    private void GroupsChanged()
    {
        if (_syncing || _formatBox is null)
            return;
        var groups = new List<JhScaleBarcodeFormat.Group>(6);
        for (var g = 0; g < 6; g++)
        {
            var idx = _source[g].SelectedIndex;
            var source = idx >= 0 && idx < SourceTitles.Length ? SourceTitles[idx].Source
                : (_source[g].SelectedItem as string ?? "A")[0];
            groups.Add(new JhScaleBarcodeFormat.Group(source, (int)(_length[g].Value ?? 0), (int)(_shift[g].Value ?? 0), Math.Max(0, _overflow[g].SelectedIndex)));
        }
        _syncing = true;
        try
        {
            _formatBox.Text = JhScaleBarcodeFormat.Format(groups);
        }
        finally
        {
            _syncing = false;
        }
        UpdateSample();
    }

    private void UpdateSample()
    {
        if (_verdictText is null || _sampleText is null || _samplePrice is null)
            return;
        var groups = JhScaleBarcodeFormat.Parse(_formatBox.Text);
        if (groups is null)
        {
            _sampleText.Text = "—";
            SetVerdict(false, L("строка формата должна быть из 24 знаков (6 групп «буква + 3 цифры»)", "формат сабы 24 белгиден турушу керек (6 топ «тамга + 3 сан»)", "the format string must be 24 characters (6 groups of “letter + 3 digits”)", "biçim dizesi 24 karakter olmalı (6 grup “harf + 3 rakam”)", "format qatori 24 belgidan iborat bo‘lishi kerak (6 guruh «harf + 3 raqam»)"));
            return;
        }

        var code = (long)(_samplePlu.Value ?? 12);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var amount = Math.Round((decimal)(_samplePrice.Value ?? 250) * grams / 1000m, 2);
        var sample = JhScaleBarcodeFormat.BuildSample(groups, (int)(_flag.Value ?? 20), code, code, grams, amount);
        _sampleText.Text = sample.Length > 0 ? sample : "—";

        var hasWeight = groups.Any(g => g.Source == 'J' && g.Length > 0);
        var hasAmount = groups.Any(g => g.Source == 'K' && g.Length > 0);
        if (!hasWeight && !hasAmount)
        {
            SetVerdict(false, L("в формате нет ни веса (J), ни суммы (K)", "форматта салмак (J) да, сумма (K) да жок", "the format has neither weight (J) nor amount (K)", "biçimde ne ağırlık (J) ne tutar (K) var", "formatda vazn (J) ham, summa (K) ham yo‘q"));
            return;
        }
        var (ok, message) = VerifyWithKassa(sample, code, hasWeight, grams, amount);
        SetVerdict(ok, message);
    }

    private void SetVerdict(bool ok, string message)
    {
        _verdictText.Text = (ok ? "✓ " : "⚠ " + L("Касса НЕ узнает товар: ", "Касса товарды ТААНЫБАЙТ: ", "The till will NOT recognise the item: ", "Kasa ürünü TANIMAZ: ", "Kassa tovarni TANIMAYDI: ")) + message;
        _verdictText.Foreground = ThemeBrush(this, ok ? "BrushSuccess" : "BrushWarning", ok ? Brushes.Green : Brushes.DarkOrange);
    }

    // =====================================================================================
    // Загрузка товаров
    // =====================================================================================

    private Control BuildUploadTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Как загрузить товары на TM-30F", "TM-30F'ке товарларды кантип жүктөө", "How to load goods onto the TM-30F", "Ürünler TM-30F'e nasıl yüklenir", "Tovarlarni TM-30F ga qanday yuklash"),
            L("1) В окне «Весы» (марка TM-30F) нажмите «Сохранить файл для весов» — касса сохранит CSV: PLU; название; единица; цена (разделитель «;», UTF-8). 2) В программе «TM-xA data management software» (admin / 200806) импортируйте CSV в таблицу PLU. 3) Отправьте на весы по сети (весы в режиме сервера, адрес — вкладка «Подключение») или экспортом на флешку: файл JHSCALE\\A_xxx.TMS весы подхватят сами (Spec042 = 1). Номер PLU в файле = PLU товара в кассе — по нему касса найдёт товар в штрих-коде.",
              "1) «Таразалар» терезесинде (TM-30F маркасы) «Файлды тараза үчүн сактоо» басыңыз — касса CSV сактайт: PLU; аталышы; бирдиги; баасы (бөлгүч «;», UTF-8). 2) «TM-xA data management software» программасында (admin / 200806) CSV'ни PLU таблицасына импорттоңуз. 3) Таразага тармак аркылуу (тараза сервер режиминде, дарек — «Туташуу» өтмөгү) же флешкага экспорт менен жибериңиз: JHSCALE\\A_xxx.TMS файлын тараза өзү алат (Spec042 = 1). Файлдагы PLU номери = кассадагы товардын PLU'су — касса штрих-коддо товарды ушул боюнча табат.",
              "1) In the “Scales” window (TM-30F) press “Save file for the scale” — the till saves a CSV: PLU; name; unit; price (“;” separator, UTF-8). 2) In “TM-xA data management software” (admin / 200806) import the CSV into the PLU table. 3) Send it to the scale over the network (scale in server mode, address — “Connection” tab) or export to a USB stick: the scale picks up JHSCALE\\A_xxx.TMS itself (Spec042 = 1). The PLU number in the file = the item PLU in the till — the till finds the item in the barcode by it.",
              "1) «Tartı» penceresinde (TM-30F) “Tartı için dosyayı kaydet”e basın — kasa CSV kaydeder: PLU; ad; birim; fiyat (“;” ayırıcı, UTF-8). 2) “TM-xA data management software”de (admin / 200806) CSV'yi PLU tablosuna aktarın. 3) Tartıya ağ üzerinden (tartı sunucu modunda, adres — “Bağlantı” sekmesi) veya USB belleğe dışa aktararak gönderin: tartı JHSCALE\\A_xxx.TMS dosyasını kendisi alır (Spec042 = 1). Dosyadaki PLU numarası = kasadaki ürün PLU'su — kasa barkoddaki ürünü buna göre bulur.",
              "1) «Tarozi» oynasida (TM-30F) «Tarozi uchun faylni saqlash»ni bosing — kassa CSV saqlaydi: PLU; nomi; birligi; narxi («;» ajratgich, UTF-8). 2) «TM-xA data management software» dasturida (admin / 200806) CSV ni PLU jadvaliga import qiling. 3) Taroziga tarmoq orqali (tarozi server rejimida, manzil — «Ulanish» bo‘limi) yoki fleshkaga eksport bilan yuboring: JHSCALE\\A_xxx.TMS faylini tarozi o‘zi oladi (Spec042 = 1). Fayldagi PLU raqami = kassadagi tovar PLU'si — kassa shtrix-koddagi tovarni shu bo‘yicha topadi."),
            out var body);
        body.Children.Add(ButtonRow(MakeButton(L("Открыть окно «Весы» (TM-30F)", "«Таразалар» терезесин ачуу (TM-30F)", "Open the “Scales” window (TM-30F)", "«Tartı» penceresini aç (TM-30F)", "«Tarozi» oynasini ochish (TM-30F)"), true,
            (_, _) => OpenScalesWindow(this, BrandTm))));
        panel.Children.Add(card);
        return panel;
    }

    // =====================================================================================

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        ResultBorder.BorderBrush = ThemeBrush(this, isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        ResultBorder.Background = ThemeBrush(this, isError ? "BrushWarningSoft" : "BrushSurfaceSubtle", Brushes.Transparent);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        SaveIp();
        Close();
    }
}
