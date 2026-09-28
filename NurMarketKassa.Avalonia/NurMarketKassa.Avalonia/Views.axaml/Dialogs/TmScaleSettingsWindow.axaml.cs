using System.Diagnostics;
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
/// «Настройки весов TM-30F».
///
/// 2026-09-28 (вечер), исправлено по просьбе владельца «чтобы наша программа могла напрямую
/// отправлять на весы TM-30F». Первая версия окна была сделана по руководству JHScale (порт
/// 33581, Spec, строки формата B201E500J500) — это ДРУГИЕ весы. У владельца весы «TM-30F Series
/// BARCODE PRINTING SCALE» Shanghai Dahua Scale (серия TM-A / TM-F), их программа — «Русский
/// масштаб V1.3» (mscale.exe). Их протокол восстановлен по файлам программы
/// (<see cref="DahuaTmProtocol"/>), и касса теперь шлёт PLU на весы сама
/// (<see cref="DahuaTmScaleService"/>, порт 4001). Окно:
/// • «Подключение» — IP и порт, «Проверить связь» через драйвер (TCP + чтение PLU №1, только чтение);
/// • «Сеть» — как узнать/поменять IP весов (их программа, сетевой модуль ZLG), адреса из их базы;
/// • «Штрих-код» — 15 форматов Dahua с живым примером и сверкой с разбором кассы;
/// • «Загрузка товаров» — знаки цены (Price point), режим отправки, срок годности.
/// Конструктор JHScale (JhScaleBarcodeFormat) больше не показывается — файл оставлен.
/// На живых весах не проверено.
/// </summary>
public partial class TmScaleSettingsWindow : Window
{
    private TextBox _ipBox = null!;
    private NumericUpDown _portBox = null!;
    private Button _checkButton = null!;

    // Сеть.
    private IReadOnlyList<LocalSubnet> _subnets = Array.Empty<LocalSubnet>();
    private TextBlock _networkText = null!;

    // Штрих-код.
    private ComboBox _formatBox = null!;
    private NumericUpDown _flag = null!;
    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private NumericUpDown _samplePrice = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;

    // Загрузка товаров.
    private ComboBox _priceDecimalsBox = null!;
    private RadioButton _lineMode = null!;
    private NumericUpDown _shelfLife = null!;

    public TmScaleSettingsWindow()
    {
        InitializeComponent();
        Title = L("Настройки весов TM-30F", "TM-30F таразасынын жөндөөлөрү", "TM-30F scale settings", "TM-30F tartı ayarları", "TM-30F tarozi sozlamalari");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Весы TM-30F — производитель Shanghai Dahua Scale (серия TM-A / TM-F), их программа «Русский масштаб». Касса отправляет товары на весы сама, по сети (TCP, порт 4001) — тем же протоколом, что и их программа. Настройки самих весов (формат штрих-кода, знаки цены) меняются в «Русском масштабе».",
            "TM-30F таразасы — өндүрүүчүсү Shanghai Dahua Scale (TM-A / TM-F сериясы), программасы «Русский масштаб». Касса товарларды таразага өзү тармак аркылуу жөнөтөт (TCP, 4001 порт) — алардын программасындай эле протокол менен. Таразанын өз жөндөөлөрү (штрих-код форматы, баанын белгилери) «Русский масштаб» программасында өзгөртүлөт.",
            "The TM-30F scale is made by Shanghai Dahua Scale (TM-A / TM-F series); its software is “Russian Scale” (Русский масштаб). The till sends goods to the scale itself over the network (TCP, port 4001) using the same protocol as that software. The scale's own settings (barcode format, price decimals) are changed in “Russian Scale”.",
            "TM-30F tartının üreticisi Shanghai Dahua Scale (TM-A / TM-F serisi), programı «Русский масштаб». Kasa ürünleri tartıya kendisi ağ üzerinden gönderir (TCP, port 4001) — o programla aynı protokolle. Tartının kendi ayarları (barkod biçimi, fiyat ondalığı) «Русский масштаб» programında değiştirilir.",
            "TM-30F tarozisi — ishlab chiqaruvchi Shanghai Dahua Scale (TM-A / TM-F seriyasi), dasturi «Русский масштаб». Kassa tovarlarni taroziga o‘zi tarmoq orqali yuboradi (TCP, 4001 port) — o‘sha dastur bilan bir xil protokolda. Tarozining o‘z sozlamalari (shtrix-kod formati, narx kasrlari) «Русский масштаб» dasturida o‘zgartiriladi.");

        Tabs.Items.Add(MakeTab(L("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), BuildConnectionTab()));
        Tabs.Items.Add(MakeTab(L("Сеть", "Тармак", "Network", "Ağ", "Tarmoq"), BuildNetworkTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        UpdateHeader();
        ShowResult(L("Введите IP весов и нажмите «Проверить связь» или найдите весы в сети.", "Таразанын IP'син киргизип «Байланышты текшерүү» басыңыз же таразаны тармактан табыңыз.", "Enter the scale IP and press “Check connection”, or find the scale on the network.", "Tartı IP'sini girip “Bağlantıyı kontrol et”e basın veya tartıyı ağda bulun.", "Tarozi IP'sini kiriting va «Aloqani tekshirish»ni bosing yoki tarozini tarmoqdan toping."), false);
    }

    /// <summary>Открыть сразу на вкладке «Штрих-код» (кнопка «Штрих-код этикетки…» окна «Весы»).</summary>
    public void ShowBarcodeTab() => Tabs.SelectedIndex = 2;

    private static TabItem MakeTab(string header, Control content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content } };

    private int Port => (int)(_portBox.Value ?? DahuaTmProtocol.DefaultPort);

    private void UpdateHeader()
    {
        var ip = (_ipBox.Text ?? "").Trim();
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ") + (ip.Length > 0 ? ip : "—")
            + $" · TCP {Port} · Dahua TM-A / TM-F";
    }

    // =====================================================================================
    // Подключение
    // =====================================================================================

    private Control BuildConnectionTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Адрес весов", "Таразанын дареги", "Scale address", "Tartı adresi", "Tarozi manzili"),
            L("IP весов — тот же, что в их программе: «Настройка коммуникации → Настройка Ethernet». Порт — 4001 (так в их программе; менять, только если его поменяли в сетевом модуле весов).",
              "Таразанын IP'си — алардын программасындагыдай эле: «Настройка коммуникации → Настройка Ethernet». Порт — 4001 (алардын программасында ушундай; таразанын тармак модулунда өзгөртүлсө гана алмаштырыңыз).",
              "The scale IP is the same as in its software: “Communication settings → Ethernet settings”. The port is 4001 (as in that software; change it only if it was changed in the scale's network module).",
              "Tartı IP'si programındakiyle aynıdır: «Настройка коммуникации → Настройка Ethernet». Port 4001'dir (programda böyle; yalnızca tartının ağ modülünde değiştirildiyse değiştirin).",
              "Tarozi IP'si ularning dasturidagidek: «Настройка коммуникации → Настройка Ethernet». Port — 4001 (dasturda shunday; faqat tarozining tarmoq modulida o‘zgartirilgan bo‘lsa almashtiring)."),
            out var body);

        _ipBox = new TextBox { Text = IpOf(BrandTm), Watermark = "192.168.1.151", MinWidth = 220 };
        _ipBox.LostFocus += (_, _) => { SaveConnection(); UpdateHeader(); UpdateNetwork(); };
        body.Children.Add(Row(L("IP-адрес весов", "Таразанын IP-дареги", "Scale IP address", "Tartı IP adresi", "Tarozi IP manzili"), _ipBox));

        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = UserPreferences.Instance.TmScalePort, MinWidth = 140 };
        _portBox.ValueChanged += (_, _) => UpdateHeader();
        body.Children.Add(Row(L("TCP-порт", "TCP-порт", "TCP port", "TCP portu", "TCP port"), _portBox));

        body.Children.Add(Text(L(
            "«Проверить связь»: подключение к весам и команда ЧТЕНИЯ записи PLU №1 (!0J0001A) — весы при этом ничего не меняют. Весь обмен пишется в журнал (кнопка «Журнал обмена»).",
            "«Байланышты текшерүү»: таразага туташуу жана PLU №1 жазуусун ОКУУ буйругу (!0J0001A) — тараза эч нерсени өзгөртпөйт. Бардык алмашуу журналга жазылат («Алмашуу журналы» баскычы).",
            "“Check connection”: connects to the scale and sends a READ command for PLU No. 1 (!0J0001A) — the scale changes nothing. The whole exchange is written to a log (“Exchange log” button).",
            "“Bağlantıyı kontrol et”: tartıya bağlanır ve PLU No. 1 kaydını OKUMA komutu (!0J0001A) gönderir — tartı hiçbir şeyi değiştirmez. Tüm iletişim günlüğe yazılır («İletişim günlüğü» düğmesi).",
            "«Aloqani tekshirish»: taroziga ulanish va PLU №1 yozuvini O‘QISH buyrug‘i (!0J0001A) — tarozi hech narsani o‘zgartirmaydi. Butun almashuv jurnalga yoziladi («Almashuv jurnali» tugmasi)."), "hint"));

        _checkButton = MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), true, async (_, _) => await CheckAsync().ConfigureAwait(true));
        body.Children.Add(ButtonRow(
            _checkButton,
            MakeButton(L("Найти весы в сети…", "Тармактан тараза табуу…", "Find scales on the network…", "Ağda tartı bul…", "Tarmoqda tarozi topish…"), false, async (_, _) =>
            {
                if (await OpenScanAsync(this, BrandTm).ConfigureAwait(true))
                {
                    _ipBox.Text = IpOf(BrandTm);
                    UpdateHeader();
                    UpdateNetwork();
                }
            }),
            MakeButton(L("Журнал обмена", "Алмашуу журналы", "Exchange log", "İletişim günlüğü", "Almashuv jurnali"), false, (_, _) => OpenExchangeLog()),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveConnection();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        panel.Children.Add(card);
        return panel;
    }

    private void SaveConnection()
    {
        var prefs = UserPreferences.Instance;
        prefs.TmScaleIp = (_ipBox.Text ?? "").Trim();
        prefs.TmScalePort = Port;
        prefs.SaveToDisk();
    }

    /// <summary>Открывает журнал обмена с весами (tm30f-exchange.log) программой по умолчанию.</summary>
    private void OpenExchangeLog()
    {
        var path = DahuaTmScaleService.ExchangeLogPath;
        if (!File.Exists(path))
        {
            ShowResult(L("Журнала ещё нет — он появится после первой проверки связи или отправки: ", "Журнал азырынча жок — ал биринчи текшерүүдөн же жөнөтүүдөн кийин пайда болот: ", "There is no log yet — it appears after the first check or upload: ", "Henüz günlük yok — ilk kontrol veya gönderimden sonra oluşur: ", "Jurnal hali yo‘q — birinchi tekshiruv yoki yuborishdan keyin paydo bo‘ladi: ") + path, false);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowResult(path + " — " + ex.Message, true);
        }
    }

    private async Task CheckAsync()
    {
        SaveConnection();
        var ip = (_ipBox.Text ?? "").Trim();
        var scale = DahuaTmScaleService.TryCreate(ip, Port);
        if (scale is null)
        {
            ShowResult(L("Введите IP-адрес весов.", "Таразанын IP-дарегин киргизиңиз.", "Enter the scale IP address.", "Tartı IP adresini girin.", "Tarozi IP manzilini kiriting."), true);
            return;
        }
        _checkButton.IsEnabled = false;
        ShowResult(L("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), false);
        try
        {
            var result = await scale.TestConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            PosLogger.Log($"TM-30F (Dahua): проверка связи {ip}:{Port}: connected={result.Connected}, replied={result.Replied}, kind={result.ReplyKind}, error={result.Error} {result.Detail}", "SCALES");

            if (!result.Connected)
            {
                var (pingOk, rtt) = await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true);
                ShowResult(pingOk
                    ? L($"Устройство {ip} отвечает на ping ({rtt} мс), но порт {Port} закрыт. Это не весы, у сетевого модуля весов другой порт или весы заняты их программой — закройте «Русский масштаб» и повторите.",
                        $"{ip} түзмөгү ping'ге жооп берет ({rtt} мс), бирок {Port} порт жабык. Бул тараза эмес, таразанын тармак модулунун порту башка же тараза алардын программасы менен бош эмес — «Русский масштаб» программасын жаап, кайталаңыз.",
                        $"Device {ip} answers ping ({rtt} ms) but port {Port} is closed. It is not the scale, the scale's network module uses another port, or the scale is busy with its software — close “Russian Scale” and retry.",
                        $"{ip} cihazı ping'e yanıt veriyor ({rtt} ms) ama {Port} portu kapalı. Bu tartı değil, tartının ağ modülü başka port kullanıyor ya da tartı kendi programıyla meşgul — «Русский масштаб»ı kapatıp tekrar deneyin.",
                        $"{ip} qurilmasi ping'ga javob beradi ({rtt} ms), lekin {Port} port yopiq. Bu tarozi emas, tarozi tarmoq modulining porti boshqa yoki tarozi o‘z dasturi bilan band — «Русский масштаб»ni yopib, qayta urinib ko‘ring.")
                    : L($"По адресу {ip} никто не отвечает. Проверьте кабель, что весы включены, и что компьютер в той же сети, что и весы (вкладка «Сеть»).",
                        $"{ip} дареги боюнча эч ким жооп бербейт. Кабелди, тараза күйүк экенин жана компьютер тараза менен бир тармакта экенин текшериңиз («Тармак» өтмөгү).",
                        $"Nobody answers at {ip}. Check the cable, that the scale is on, and that the computer is on the same network as the scale (“Network” tab).",
                        $"{ip} adresinde yanıt veren yok. Kabloyu, tartının açık olduğunu ve bilgisayarın tartıyla aynı ağda olduğunu kontrol edin («Ağ» sekmesi).",
                        $"{ip} manzilida hech kim javob bermayapti. Kabelni, tarozi yoqilganini va kompyuter tarozi bilan bir tarmoqda ekanini tekshiring («Tarmoq» bo‘limi)."), true);
                return;
            }

            if (result.Replied)
            {
                ShowResult(L($"Весы на связи ({result.ConnectMs} мс) и ответили на чтение PLU №1: «{DahuaTmProtocol.Visible(result.ReplyText.Trim())}». Можно отправлять товары (окно «Весы», марка TM-30F).",
                             $"Тараза байланышта ({result.ConnectMs} мс) жана PLU №1 окууга жооп берди: «{DahuaTmProtocol.Visible(result.ReplyText.Trim())}». Товарларды жөнөтсө болот («Таразалар» терезеси, TM-30F маркасы).",
                             $"The scale is connected ({result.ConnectMs} ms) and answered the PLU No. 1 read: “{DahuaTmProtocol.Visible(result.ReplyText.Trim())}”. You can send goods (“Scales” window, TM-30F).",
                             $"Tartı bağlı ({result.ConnectMs} ms) ve PLU No. 1 okumasına yanıt verdi: «{DahuaTmProtocol.Visible(result.ReplyText.Trim())}». Ürün gönderebilirsiniz («Tartı» penceresi, TM-30F).",
                             $"Tarozi aloqada ({result.ConnectMs} ms) va PLU №1 o‘qishga javob berdi: «{DahuaTmProtocol.Visible(result.ReplyText.Trim())}». Tovarlarni yuborish mumkin («Tarozi» oynasi, TM-30F)."), false);
                return;
            }

            ShowResult(L($"Подключение к {ip}:{Port} есть, но на команду чтения весы не ответили за 2,5 с. Возможно, у весов другой протокол или они заняты. Отправку можно попробовать — ответы весов будут видны в журнале обмена.",
                         $"{ip}:{Port} менен туташуу бар, бирок тараза окуу буйругуна 2,5 с ичинде жооп берген жок. Балким, таразанын протоколу башка же ал бош эмес. Жөнөтүүнү сынап көрсө болот — тараза жооптору алмашуу журналында көрүнөт.",
                         $"Connected to {ip}:{Port}, but the scale did not answer the read command within 2.5 s. The scale may use another protocol or be busy. You can still try sending — the scale's replies will be visible in the exchange log.",
                         $"{ip}:{Port} bağlantısı var ama tartı okuma komutuna 2,5 sn içinde yanıt vermedi. Tartı başka bir protokol kullanıyor veya meşgul olabilir. Göndermeyi yine deneyebilirsiniz — tartının yanıtları iletişim günlüğünde görünür.",
                         $"{ip}:{Port} bilan ulanish bor, lekin tarozi o‘qish buyrug‘iga 2,5 s ichida javob bermadi. Balki tarozining protokoli boshqa yoki u band. Yuborishni sinab ko‘rish mumkin — tarozi javoblari almashuv jurnalida ko‘rinadi."), true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"TM-30F (Dahua): проверка связи: {ex}", "SCALES");
            ShowResult(ex.Message, true);
        }
        finally
        {
            _checkButton.IsEnabled = true;
        }
    }

    // =====================================================================================
    // Сеть
    // =====================================================================================

    /// <summary>Адреса весов из базы их программы (mscale.mdb → t_comm_ethernet на ПК владельца).</summary>
    private static readonly string[] KnownAddresses = { "192.168.0.150", "192.168.1.151", "192.168.1.152", "192.168.1.153", "192.168.1.154" };

    private Control BuildNetworkTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };

        panel.Children.Add(Card(L("Как узнать IP весов", "Таразанын IP'син кантип билүү", "How to find the scale IP", "Tartı IP'si nasıl öğrenilir", "Tarozi IP'sini qanday bilish"),
            L("1) В программе «Русский масштаб»: меню «Настройка коммуникации → Настройка Ethernet» — там список весов и их адреса (галочка — с какими весами программа работает). В её базе записаны адреса: " + string.Join(", ", KnownAddresses) + ". 2) Кнопка «Найти весы в сети» на вкладке «Подключение». 3) Если адреса не подходят — сетевой модуль весов (ZLG ZNE-100T) настраивается программой «Настройка весовой сетевой карты» из того же меню (файл netcom\\DHNETCONEn v1.0.exe в папке их программы): там видны IP модуля, маска, шлюз и порт 4001.",
              "1) «Русский масштаб» программасында: «Настройка коммуникации → Настройка Ethernet» менюсу — ал жерде таразалардын тизмеси жана даректери бар (белги — программа кайсы тараза менен иштейт). Анын базасында даректер: " + string.Join(", ", KnownAddresses) + ". 2) «Туташуу» өтмөгүндөгү «Тармактан тараза табуу» баскычы. 3) Даректер туура келбесе — таразанын тармак модулу (ZLG ZNE-100T) ошол эле менюдагы «Настройка весовой сетевой карты» программасы менен жөндөлөт (алардын программасынын папкасындагы netcom\\DHNETCONEn v1.0.exe файлы): ал жерде модулдун IP'си, маскасы, шлюзу жана 4001 порту көрүнөт.",
              "1) In “Russian Scale”: menu “Communication settings → Ethernet settings” lists the scales and their addresses (the tick shows which scales the software works with). Its database contains: " + string.Join(", ", KnownAddresses) + ". 2) The “Find scales on the network” button on the “Connection” tab. 3) If the addresses do not fit, the scale's network module (ZLG ZNE-100T) is configured with “Scale network card settings” from the same menu (netcom\\DHNETCONEn v1.0.exe in its folder): it shows the module IP, mask, gateway and port 4001.",
              "1) «Русский масштаб»da: «Настройка коммуникации → Настройка Ethernet» menüsü tartıları ve adreslerini listeler (işaret, programın hangi tartıyla çalıştığını gösterir). Veritabanındaki adresler: " + string.Join(", ", KnownAddresses) + ". 2) «Bağlantı» sekmesindeki «Ağda tartı bul» düğmesi. 3) Adresler uymazsa tartının ağ modülü (ZLG ZNE-100T) aynı menüdeki «Настройка весовой сетевой карты» ile ayarlanır (program klasöründeki netcom\\DHNETCONEn v1.0.exe): modülün IP'si, maskesi, ağ geçidi ve 4001 portu orada görünür.",
              "1) «Русский масштаб» dasturida: «Настройка коммуникации → Настройка Ethernet» menyusi — u yerda tarozilar ro‘yxati va manzillari (belgi — dastur qaysi tarozi bilan ishlaydi). Uning bazasidagi manzillar: " + string.Join(", ", KnownAddresses) + ". 2) «Ulanish» bo‘limidagi «Tarmoqda tarozi topish» tugmasi. 3) Manzillar mos kelmasa — tarozining tarmoq moduli (ZLG ZNE-100T) o‘sha menyudagi «Настройка весовой сетевой карты» dasturi bilan sozlanadi (dastur papkasidagi netcom\\DHNETCONEn v1.0.exe): u yerda modul IP'si, niqobi, shlyuzi va 4001 porti ko‘rinadi."),
            out _));

        var card = Card(L("Сеть компьютера и весов", "Компьютердин жана таразанын тармагы", "Computer and scale network", "Bilgisayar ve tartı ağı", "Kompyuter va tarozi tarmog‘i"),
            L("Компьютер и весы должны быть в одной сети: первые три числа адреса совпадают (при маске 255.255.255.0). Если нет — поменяйте адрес весов в сетевом модуле или добавьте компьютеру второй адрес в той же сети.",
              "Компьютер менен тараза бир тармакта болушу керек: даректин алгачкы үч саны дал келет (255.255.255.0 маскасында). Болбосо — таразанын даректин тармак модулунда өзгөртүңүз же компьютерге ошол эле тармакта экинчи дарек кошуңуз.",
              "The computer and the scale must be on the same network: the first three numbers of the address match (with mask 255.255.255.0). If not, change the scale address in its network module or give the computer a second address in that network.",
              "Bilgisayar ve tartı aynı ağda olmalı: adresin ilk üç sayısı aynı olmalı (255.255.255.0 maskesiyle). Değilse tartının adresini ağ modülünde değiştirin veya bilgisayara o ağda ikinci bir adres ekleyin.",
              "Kompyuter va tarozi bir tarmoqda bo‘lishi kerak: manzilning dastlabki uch soni mos keladi (255.255.255.0 niqobida). Aks holda tarozi manzilini tarmoq modulida o‘zgartiring yoki kompyuterga shu tarmoqda ikkinchi manzil qo‘shing."),
            out var body);
        _subnets = ScaleNetworkScanner.GetLocalSubnets();
        _networkText = new TextBlock { FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush(this, "BrushText", Brushes.Black) };
        body.Children.Add(new Border { Classes = { "card" }, Child = _networkText });
        panel.Children.Add(card);
        UpdateNetwork();
        return panel;
    }

    private void UpdateNetwork()
    {
        if (_networkText is null)
            return;
        var lines = new List<string>();
        var ipText = (_ipBox?.Text ?? "").Trim();
        var hasIp = IPAddress.TryParse(ipText, out var scaleIp);
        lines.Add(L("IP весов: ", "Таразанын IP'си: ", "Scale IP: ", "Tartı IP'si: ", "Tarozi IP'si: ") + (hasIp ? ipText : "—") + $"   TCP {(_portBox is null ? DahuaTmProtocol.DefaultPort : Port)}");
        lines.Add("");
        if (_subnets.Count == 0)
            lines.Add(L("Сетевых подключений у компьютера не найдено.", "Компьютерде тармак туташуулары табылган жок.", "The computer has no network connections.", "Bilgisayarda ağ bağlantısı bulunamadı.", "Kompyuterda tarmoq ulanishlari topilmadi."));
        var anyMatch = false;
        foreach (var s in _subnets)
        {
            var prefix = Math.Clamp(s.PrefixLength, 0, 32);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var same = hasIp && (ScaleNetworkScanner.ToNumber(scaleIp!) & mask) == (ScaleNetworkScanner.ToNumber(s.LocalAddress) & mask);
            anyMatch |= same;
            lines.Add($"{s.AdapterName}: {s.LocalAddress}/{prefix}" + (same ? "   ✓ " + L("весы в этой сети", "тараза ушул тармакта", "scale is in this network", "tartı bu ağda", "tarozi shu tarmoqda") : ""));
        }
        if (hasIp && _subnets.Count > 0 && !anyMatch)
        {
            lines.Add("");
            lines.Add("⚠ " + L("Адрес весов не из сети ни одного подключения компьютера — связи не будет.", "Таразанын дареги компьютердин эч бир туташуусунун тармагынан эмес — байланыш болбойт.", "The scale address is not in the network of any computer connection — there will be no link.", "Tartı adresi bilgisayarın hiçbir bağlantısının ağında değil — bağlantı olmaz.", "Tarozi manzili kompyuterning hech bir ulanishi tarmog‘ida emas — aloqa bo‘lmaydi."));
        }
        _networkText.Text = string.Join(Environment.NewLine, lines);
    }

    // =====================================================================================
    // Штрих-код
    // =====================================================================================

    private Control BuildBarcodeTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Формат штрих-кода весов", "Таразанын штрих-код форматы", "Scale barcode format", "Tartı barkod biçimi", "Tarozi shtrix-kod formati"),
            L("F — флаг («Префикс штрихкода» товара, касса шлёт его с каждым товаром), W — код товара, N — вес, E — сумма, C — контрольная цифра. Касса разбирает 13 цифр: 2 цифры префикса + 5 цифр PLU + 5 цифр значения + контрольная — это FFWWWWWNNNNNC (вес) или FFWWWWWEEEEEC (сумма).",
              "F — желек (товардын «Префикс штрихкода», касса аны ар бир товар менен жөнөтөт), W — товар коду, N — салмак, E — сумма, C — текшерүү цифрасы. Касса 13 санды окуйт: 2 префикс + 5 PLU + 5 маани + текшерүү — бул FFWWWWWNNNNNC (салмак) же FFWWWWWEEEEEC (сумма).",
              "F — flag (the item's “barcode prefix”, the till sends it with every item), W — item code, N — weight, E — amount, C — check digit. The till reads 13 digits: 2-digit prefix + 5-digit PLU + 5-digit value + check — that is FFWWWWWNNNNNC (weight) or FFWWWWWEEEEEC (amount).",
              "F — bayrak (ürünün «barkod öneki», kasa onu her ürünle gönderir), W — ürün kodu, N — ağırlık, E — tutar, C — kontrol hanesi. Kasa 13 hane okur: 2 önek + 5 PLU + 5 değer + kontrol — yani FFWWWWWNNNNNC (ağırlık) veya FFWWWWWEEEEEC (tutar).",
              "F — bayroq (tovarning «shtrix-kod prefiksi», kassa uni har bir tovar bilan yuboradi), W — tovar kodi, N — vazn, E — summa, C — nazorat raqami. Kassa 13 raqamni o‘qiydi: 2 prefiks + 5 PLU + 5 qiymat + nazorat — bu FFWWWWWNNNNNC (vazn) yoki FFWWWWWEEEEEC (summa)."),
            out var body);
        body.Children.Add(Text(CompanyFormatText(), "hint"));

        _formatBox = new ComboBox { MinWidth = 320, FontFamily = new FontFamily("Consolas, Segoe UI") };
        var byWeightNow = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        foreach (var v in DahuaTmBarcodeFormat.Variants)
        {
            var note = v == DahuaTmBarcodeFormat.Recommended(byWeightNow)
                ? "   ← " + L("рекомендуемый", "сунушталган", "recommended", "önerilen", "tavsiya etilgan")
                : v == DahuaTmBarcodeFormat.OwnerCurrent
                    ? "   ← " + L("сейчас на весах (28.09)", "азыр таразада (28.09)", "on the scale now (28.09)", "şu an tartıda (28.09)", "hozir tarozida (28.09)")
                    : "";
            _formatBox.Items.Add(v + note);
        }
        var saved = UserPreferences.Instance.TmScaleDahuaBarcode;
        var index = DahuaTmBarcodeFormat.Variants.ToList().IndexOf(saved ?? DahuaTmBarcodeFormat.Recommended(byWeightNow));
        _formatBox.SelectedIndex = index >= 0 ? index : DahuaTmBarcodeFormat.Variants.ToList().IndexOf(DahuaTmBarcodeFormat.Recommended(byWeightNow));
        _formatBox.SelectionChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Формат (Barcode)", "Формат (Barcode)", "Format (Barcode)", "Biçim (Barcode)", "Format (Barcode)"), _formatBox));

        _flag = new NumericUpDown { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "00", Value = UserPreferences.Instance.TmScaleBarcodePrefix, MinWidth = 140 };
        _flag.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Префикс штрихкода (F)", "Штрих-код префикси (F)", "Barcode prefix (F)", "Barkod öneki (F)", "Shtrix-kod prefiksi (F)"), _flag));

        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0", Value = 12, MinWidth = 140 };
        _sampleCode.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Код товара (PLU) для примера", "Мисал үчүн товар коду (PLU)", "Sample item code (PLU)", "Örnek ürün kodu (PLU)", "Namuna tovar kodi (PLU)"), _sampleCode));
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
                _formatBox.SelectedIndex = DahuaTmBarcodeFormat.Variants.ToList().IndexOf(DahuaTmBarcodeFormat.Recommended(byWeight));
                _flag.Value = DahuaTmBarcodeFormat.RecommendedFlag(byWeight);
            }),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveBarcode();
                ShowResult(L("Сохранено в кассе. Префикс уйдёт на весы с товарами; сам формат выберите на весах в «Русском масштабе» (см. ниже).", "Кассада сакталды. Префикс таразага товарлар менен кетет; форматты өзүн таразада «Русский масштаб» аркылуу тандаңыз (төмөндү караңыз).", "Saved in the till. The prefix goes to the scale with the goods; choose the format itself on the scale in “Russian Scale” (see below).", "Kasada kaydedildi. Önek ürünlerle tartıya gider; biçimin kendisini tartıda «Русский масштаб» ile seçin (aşağıya bakın).", "Kassada saqlandi. Prefiks taroziga tovarlar bilan ketadi; formatning o‘zini tarozida «Русский масштаб» orqali tanlang (pastga qarang)."), false);
            })));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Как поменять формат на весах", "Форматты таразада кантип өзгөртүү", "How to change the format on the scale", "Tartıda biçim nasıl değiştirilir", "Tarozida formatni qanday o‘zgartirish"),
            L("Касса системные параметры весов не отправляет — это делается их программой. «Русский масштаб» → «Базовая настройка → Системные параметры» → «Common param» → «Barcode»: выберите формат → «Download». Там же «Price point» — сколько знаков после запятой в цене (у кассы — вкладка «Загрузка товаров»). Допущения, проверьте первой этикеткой: в W идут младшие 5 цифр кода товара; N — граммы; E — сумма в единицах цены весов. У товара в кассе должен быть PLU — он и есть код товара на весах.",
              "Касса таразанын системалык параметрлерин жөнөтпөйт — муну алардын программасы жасайт. «Русский масштаб» → «Базовая настройка → Системные параметры» → «Common param» → «Barcode»: форматты тандап → «Download». Ошол жерде «Price point» — баада үтүрдөн кийин канча белги (кассада — «Товарларды жүктөө» өтмөгү). Божомолдор, биринчи этикетка менен текшериңиз: W'га товар кодунун кичи 5 саны кетет; N — грамм; E — сумма тараза баасынын бирдигинде. Кассадагы товардын PLU'су болушу керек — ал таразадагы товар коду.",
              "The till does not send the scale's system parameters — that is done with its software. “Russian Scale” → “Basic settings → System parameters” → “Common param” → “Barcode”: pick the format → “Download”. “Price point” there is the number of decimals in the price (in the till — the “Uploading goods” tab). Assumptions, check with the first label: W gets the lower 5 digits of the item code; N is grams; E is the amount in the scale's price units. The item in the till must have a PLU — it is the item code on the scale.",
              "Kasa tartının sistem parametrelerini göndermez — bu tartının programıyla yapılır. «Русский масштаб» → «Базовая настройка → Системные параметры» → «Common param» → «Barcode»: biçimi seçin → «Download». Oradaki «Price point» fiyattaki ondalık sayısıdır (kasada — «Ürün yükleme» sekmesi). Varsayımlar, ilk etiketle kontrol edin: W'ya ürün kodunun son 5 hanesi gider; N gram; E tartının fiyat biriminde tutardır. Kasadaki ürünün PLU'su olmalı — tartıdaki ürün kodu odur.",
              "Kassa tarozining tizim parametrlarini yubormaydi — bu ularning dasturi bilan qilinadi. «Русский масштаб» → «Базовая настройка → Системные параметры» → «Common param» → «Barcode»: formatni tanlang → «Download». U yerdagi «Price point» — narxda verguldan keyin nechta belgi (kassada — «Tovarlarni yuklash» bo‘limi). Farazlar, birinchi yorliq bilan tekshiring: W ga tovar kodining kichik 5 raqami tushadi; N — gramm; E — tarozi narx birligidagi summa. Kassadagi tovarning PLU'si bo‘lishi kerak — u tarozidagi tovar kodi."),
            out _));

        UpdateSample();
        return panel;
    }

    private string SelectedFormat =>
        _formatBox.SelectedIndex >= 0 && _formatBox.SelectedIndex < DahuaTmBarcodeFormat.Variants.Count
            ? DahuaTmBarcodeFormat.Variants[_formatBox.SelectedIndex]
            : DahuaTmBarcodeFormat.Recommended(true);

    private void SaveBarcode()
    {
        var prefs = UserPreferences.Instance;
        prefs.TmScaleDahuaBarcode = SelectedFormat;
        prefs.TmScaleBarcodePrefix = (int)(_flag.Value ?? 20);
        prefs.SaveToDisk();
    }

    private void UpdateSample()
    {
        if (_verdictText is null || _sampleText is null || _samplePrice is null || _flag is null || _sampleCode is null || _sampleGrams is null)
            return;
        var format = SelectedFormat;
        var code = (long)(_sampleCode.Value ?? 12);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var decimals = _priceDecimalsBox is { SelectedIndex: >= 0 } ? _priceDecimalsBox.SelectedIndex : UserPreferences.Instance.TmScalePricePoint;
        var amount = Math.Round((decimal)(_samplePrice.Value ?? 250) * grams / 1000m, decimals, MidpointRounding.AwayFromZero);
        var sample = DahuaTmBarcodeFormat.BuildSample(format, (int)(_flag.Value ?? 20), code, grams, DahuaTmProtocol.ScalePrice(amount, decimals));
        _sampleText.Text = sample.Length > 0 ? sample : "—";

        var hasWeight = DahuaTmBarcodeFormat.HasWeight(format);
        var hasAmount = DahuaTmBarcodeFormat.HasAmount(format);
        if (!hasWeight && !hasAmount)
        {
            SetVerdict(false, L("в формате нет ни веса (N), ни суммы (E)", "форматта салмак (N) да, сумма (E) да жок", "the format has neither weight (N) nor amount (E)", "biçimde ne ağırlık (N) ne tutar (E) var", "formatda vazn (N) ham, summa (E) ham yo‘q"));
            return;
        }
        // Если в формате и вес, и сумма, касса читает то, что стоит в позициях 8–12.
        var weightFirst = hasWeight && (!hasAmount || format.IndexOf('N') < format.IndexOf('E'));
        var (ok, message) = VerifyWithKassa(sample, code, weightFirst, grams, amount);
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
        var card = Card(L("Как касса отправляет товары на TM-30F", "Касса товарларды TM-30F'ке кантип жөнөтөт", "How the till sends goods to the TM-30F", "Kasa ürünleri TM-30F'e nasıl gönderir", "Kassa tovarlarni TM-30F ga qanday yuboradi"),
            L("Окно «Весы» → марка «TM-30F (Dahua)» → отметьте товары → «Отправить на весы». Касса подключается к весам и шлёт по одной записи PLU, дожидаясь ответа весов (как их программа: ждёт 2,5 с, повторяет до 4 раз). Номер PLU — по порядку от «Начальный PLU» (или PLU товара, если снять «Нумеровать подряд»); код товара в штрих-коде — колонка «Код в штрих-коде» (по умолчанию PLU товара). Очистку PLU, этикетки, горячие клавиши и системные параметры касса не отправляет никогда.",
              "«Таразалар» терезеси → «TM-30F (Dahua)» маркасы → товарларды белгилеңиз → «Таразага жөнөтүү». Касса таразага туташып, PLU жазууларын бирден жөнөтөт, тараза жооп бергенин күтөт (алардын программасындай: 2,5 с күтөт, 4 жолуга чейин кайталайт). PLU номери — «Баштапкы PLU»дан тартип менен (же «Катары менен номерлөө» алынса, товардын PLU'су); штрих-коддогу товар коду — «Штрих-коддогу код» тилкеси (демейки — товардын PLU'су). PLU тазалоону, этикеткаларды, ысык баскычтарды жана системалык параметрлерди касса эч качан жөнөтпөйт.",
              "“Scales” window → brand “TM-30F (Dahua)” → tick the goods → “Send to scale”. The till connects to the scale and sends the PLU records one by one, waiting for the scale's reply (like its software: waits 2.5 s, retries up to 4 times). The PLU number goes in order from “Start PLU” (or the item's PLU if “Number sequentially” is off); the item code in the barcode is the “Code in barcode” column (the item PLU by default). The till never sends PLU clearing, labels, hotkeys or system parameters.",
              "«Tartı» penceresi → «TM-30F (Dahua)» markası → ürünleri işaretleyin → «Tartıya gönder». Kasa tartıya bağlanır ve PLU kayıtlarını tek tek, tartının yanıtını bekleyerek gönderir (programı gibi: 2,5 sn bekler, 4 kereye kadar tekrarlar). PLU numarası «Başlangıç PLU»dan sırayla (veya «Sırayla numarala» kapalıysa ürünün PLU'su); barkoddaki ürün kodu «Barkoddaki kod» sütunu (varsayılan ürünün PLU'su). Kasa PLU silme, etiket, kısayol tuşu ve sistem parametresi asla göndermez.",
              "«Tarozi» oynasi → «TM-30F (Dahua)» markasi → tovarlarni belgilang → «Taroziga yuborish». Kassa taroziga ulanadi va PLU yozuvlarini bittadan, tarozi javobini kutib yuboradi (ularning dasturidek: 2,5 s kutadi, 4 martagacha takrorlaydi). PLU raqami — «Boshlang‘ich PLU»dan tartib bilan (yoki «Ketma-ket raqamlash» o‘chirilsa, tovar PLU'si); shtrix-koddagi tovar kodi — «Shtrix-koddagi kod» ustuni (odatiy — tovar PLU'si). Kassa PLU tozalash, yorliqlar, tezkor tugmalar va tizim parametrlarini hech qachon yubormaydi."),
            out var body);

        _priceDecimalsBox = new ComboBox { MinWidth = 320 };
        _priceDecimalsBox.Items.Add("Integer [123] — " + L("целые сомы", "бүтүн сом", "whole som", "tam som", "butun so‘m"));
        _priceDecimalsBox.Items.Add("Max 1 bit after PT [12.3]");
        _priceDecimalsBox.Items.Add("Max 2 bit after PT [1.23]");
        _priceDecimalsBox.Items.Add("Max 3 bit after PT [0.123]");
        _priceDecimalsBox.SelectedIndex = Math.Clamp(UserPreferences.Instance.TmScalePricePoint, 0, 3);
        _priceDecimalsBox.SelectionChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Цена на весах (Price point)", "Таразадагы баа (Price point)", "Price on the scale (Price point)", "Tartıdaki fiyat (Price point)", "Tarozidagi narx (Price point)"), _priceDecimalsBox));
        body.Children.Add(Text(L("Должно совпадать с «Price point» в системных параметрах весов («Русский масштаб» → «Системные параметры»). На весах владельца 28.09 стояло «Integer [123]». Если не совпадёт — все цены на весах будут в 10/100/1000 раз больше или меньше.",
            "Таразанын системалык параметрлериндеги «Price point» менен дал келиши керек («Русский масштаб» → «Системные параметры»). 28.09 ээсинин таразасында «Integer [123]» болчу. Дал келбесе — таразадагы бардык баалар 10/100/1000 эсе чоң же кичине болот.",
            "Must match “Price point” in the scale's system parameters (“Russian Scale” → “System parameters”). The owner's scale had “Integer [123]” on 28.09. If it does not match, all prices on the scale will be 10/100/1000 times larger or smaller.",
            "Tartının sistem parametrelerindeki «Price point» ile aynı olmalı («Русский масштаб» → «Системные параметры»). Sahibin tartısında 28.09'da «Integer [123]» vardı. Uyuşmazsa tartıdaki tüm fiyatlar 10/100/1000 kat büyük veya küçük olur.",
            "Tarozi tizim parametrlaridagi «Price point» bilan mos kelishi kerak («Русский масштаб» → «Системные параметры»). 28.09 da egasining tarozisida «Integer [123]» edi. Mos kelmasa — tarozidagi barcha narxlar 10/100/1000 marta katta yoki kichik bo‘ladi."), "hint"));

        _shelfLife = new NumericUpDown { Minimum = 0, Maximum = 999, Increment = 1, FormatString = "0", Value = UserPreferences.Instance.TmScaleShelfLifeDays, MinWidth = 140 };
        body.Children.Add(Row(L("Срок годности, дней (0 — нет)", "Жарактуулук мөөнөтү, күн (0 — жок)", "Shelf life, days (0 — none)", "Raf ömrü, gün (0 — yok)", "Yaroqlilik muddati, kun (0 — yo‘q)"), _shelfLife));

        _lineMode = new RadioButton { GroupName = "TmSendMode", Content = L("По одной записи с ответом весов (как их программа) — рекомендуется", "Тараза жооп берген сайын бирден (алардын программасындай) — сунушталат", "One record at a time with the scale's reply (like its software) — recommended", "Tartı yanıtıyla tek tek (programı gibi) — önerilir", "Tarozi javobi bilan bittadan (ularning dasturidek) — tavsiya etiladi") };
        var batchMode = new RadioButton { GroupName = "TmSendMode", Content = L("Все записи одним пакетом (запасной вариант, если «по одной» упирается в таймауты)", "Бардык жазуулар бир пакет менен (запас вариант, «бирден» таймаутка кептелсе)", "All records in one packet (fallback if “one at a time” keeps timing out)", "Tüm kayıtlar tek pakette (yedek; «tek tek» zaman aşımına düşerse)", "Barcha yozuvlar bitta paketda (zaxira, «bittadan» taymautga tiqilsa)") };
        if (string.Equals(UserPreferences.Instance.TmScaleSendMode, "batch", StringComparison.OrdinalIgnoreCase))
            batchMode.IsChecked = true;
        else
            _lineMode.IsChecked = true;
        body.Children.Add(_lineMode);
        body.Children.Add(batchMode);

        body.Children.Add(ButtonRow(
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveUpload();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            }),
            MakeButton(L("Открыть окно «Весы» (TM-30F)", "«Таразалар» терезесин ачуу (TM-30F)", "Open the “Scales” window (TM-30F)", "«Tartı» penceresini aç (TM-30F)", "«Tarozi» oynasini ochish (TM-30F)"), true,
                (_, _) =>
                {
                    SaveAll();
                    OpenScalesWindow(this, BrandTm);
                })));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Запасной путь — файл для «Русского масштаба»", "Запас жол — «Русский масштаб» үчүн файл", "Fallback — a file for “Russian Scale”", "Yedek yol — «Русский масштаб» için dosya", "Zaxira yo‘l — «Русский масштаб» uchun fayl"),
            L("Если прямая отправка не пойдёт: окно «Весы» → «Файл для «Русского масштаба»» сохранит товары в формате импорта DIGI_TOP2000 (PLU, название, цена, код, срок; кодировка Windows-1251). В их программе: «Настройки товаров» → «Импорт» → «Text Files (*.txt, *.plu)» → формат DIGI_TOP2000 → затем «Скачать» (Download) на весы. Тип товара и префикс штрихкода в этом формате не передаются — их программа поставит «Взвешивание».",
              "Түз жөнөтүү болбосо: «Таразалар» терезеси → «Русский масштаб» үчүн файл» товарларды DIGI_TOP2000 импорт форматында сактайт (PLU, аталышы, баасы, коду, мөөнөтү; Windows-1251 коддоосу). Алардын программасында: «Настройки товаров» → «Импорт» → «Text Files (*.txt, *.plu)» → DIGI_TOP2000 форматы → андан кийин таразага «Скачать» (Download). Товардын түрү жана штрих-код префикси бул форматта берилбейт — алардын программасы «Взвешивание» коёт.",
              "If direct sending does not work: “Scales” window → “File for Russian Scale” saves the goods in the DIGI_TOP2000 import format (PLU, name, price, code, shelf life; Windows-1251 encoding). In its software: “Merchandise settings” → “Import” → “Text Files (*.txt, *.plu)” → DIGI_TOP2000 format → then “Download” to the scale. Item type and barcode prefix are not carried by this format — the software will set “Weighing”.",
              "Doğrudan gönderim olmazsa: «Tartı» penceresi → «Русский масштаб için dosya» ürünleri DIGI_TOP2000 içe aktarma biçiminde kaydeder (PLU, ad, fiyat, kod, raf ömrü; Windows-1251). Programında: «Настройки товаров» → «Импорт» → «Text Files (*.txt, *.plu)» → DIGI_TOP2000 biçimi → sonra tartıya «Скачать» (Download). Ürün tipi ve barkod öneki bu biçimde aktarılmaz — program «Взвешивание» koyar.",
              "To‘g‘ridan-to‘g‘ri yuborish ishlamasa: «Tarozi» oynasi → «Русский масштаб uchun fayl» tovarlarni DIGI_TOP2000 import formatida saqlaydi (PLU, nomi, narxi, kodi, muddati; Windows-1251). Ularning dasturida: «Настройки товаров» → «Импорт» → «Text Files (*.txt, *.plu)» → DIGI_TOP2000 formati → so‘ng taroziga «Скачать» (Download). Tovar turi va shtrix-kod prefiksi bu formatda berilmaydi — dastur «Взвешивание» qo‘yadi."),
            out _));
        return panel;
    }

    private void SaveUpload()
    {
        var prefs = UserPreferences.Instance;
        prefs.TmScalePricePoint = Math.Clamp(_priceDecimalsBox.SelectedIndex, 0, 3);
        prefs.TmScaleShelfLifeDays = (int)(_shelfLife.Value ?? 0);
        prefs.TmScaleSendMode = _lineMode.IsChecked == true ? "line" : "batch";
        prefs.SaveToDisk();
    }

    private void SaveAll()
    {
        SaveConnection();
        SaveBarcode();
        SaveUpload();
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
        // 2026-09-28: как и раньше, закрытие сохраняет — владелец не обязан жать «Сохранить».
        SaveAll();
        Close();
    }
}
