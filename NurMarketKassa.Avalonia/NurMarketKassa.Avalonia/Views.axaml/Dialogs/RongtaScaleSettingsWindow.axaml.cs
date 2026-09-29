using System.Globalization;
using System.Net.NetworkInformation;
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
/// 2026-09-28: «Настройки весов Rongta» (RLS1000 / RLS1100) — просьба владельца «добавь окно
/// настроек для каждых весов как штрих м и ронгта». По образцу ShtrikhScaleSettingsWindow.
///
/// Что есть в руководствах Rongta и поэтому есть здесь:
/// • IP весов (заводской 192.168.1.87, «Label Scale User Manual», раздел 8) и проверка связи —
///   ping + TCP-подключение. Сетевой протокол САМИХ весов в руководствах НЕ описан, порт тоже —
///   поэтому прямых команд весам нет (решение координатора и владельца: не выдумывать байты);
/// • два способа загрузки товаров, которые касса уже умеет: файл .txp + F9 в RLS1000
///   (RongtaScaleAutomationService) и «свой сервер» — касса как TCP-сервер для RLS1000
///   (RongtaTcpServerService, раздел 2.2–2.4 «Label Scale Software User Manual»);
/// • типы весового штрих-кода (Appendix II) с живым примером и сверкой с кассой.
/// Ничего из этого на живых весах Rongta не проверено.
/// </summary>
public partial class RongtaScaleSettingsWindow : Window
{
    private TextBox _ipBox = null!;
    private NumericUpDown _portBox = null!;
    private Button _checkButton = null!;

    private ComboBox _typeBox = null!;
    private NumericUpDown _department = null!;
    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private NumericUpDown _samplePrice = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;
    private TextBlock _typeHint = null!;
    private readonly List<RongtaBarcodeFormat.BarcodeType> _types = RongtaBarcodeFormat.Types.ToList();

    public RongtaScaleSettingsWindow()
    {
        InitializeComponent();
        // 2026-09-29: 860×700 не помещалось на 1024×768 при 125–150 % — по экрану кассы (DialogScreenFit).
        Opened += (_, _) => this.FitToKassaScreen();
        Title = L("Настройки весов Rongta", "Rongta таразасынын жөндөөлөрү", "Rongta scale settings", "Rongta tartı ayarları", "Rongta tarozi sozlamalari");
        TitleText.Text = Title;
        // 2026-09-30: весы Rongta понимают протокол Dahua (порт 4001) — касса пишет товары сама.
        IntroText.Text = L(
            "Весы Rongta (RLS1000/RLS1100): касса может записывать товары прямо в весы по сети (порт 4001, без RLS1000) или через программу RLS1000. Здесь — адрес, проверка связи, способ загрузки и штрих-код.",
            "Rongta таразасы (RLS1000/RLS1100): касса товарларды тармак аркылуу түз таразага жаза алат (4001 порт, RLS1000'сиз) же RLS1000 программасы аркылуу. Бул жерде — дарек, байланышты текшерүү, жүктөө жолу жана штрих-код.",
            "Rongta scales (RLS1000/RLS1100): the till can write goods straight into the scale over the network (port 4001, no RLS1000) or through RLS1000. Here: address, connection check, upload method and barcode.",
            "Rongta tartılar (RLS1000/RLS1100): kasa ürünleri ağ üzerinden doğrudan tartıya yazabilir (port 4001, RLS1000'siz) ya da RLS1000 ile. Burada: adres, bağlantı kontrolü, yükleme yolu ve barkod.",
            "Rongta tarozilari (RLS1000/RLS1100): kassa tovarlarni tarmoq orqali to‘g‘ridan-to‘g‘ri taroziga yoza oladi (4001 port, RLS1000'siz) yoki RLS1000 orqali. Bu yerda — manzil, aloqani tekshirish, yuklash usuli va shtrix-kod.");

        Tabs.Items.Add(MakeTab(L("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), BuildConnectionTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        UpdateHeader();
        ShowResult(L("Введите IP весов и нажмите «Проверить связь».", "Таразанын IP'син киргизип «Байланышты текшерүү» басыңыз.", "Enter the scale IP and press “Check connection”.", "Tartı IP'sini girip “Bağlantıyı kontrol et”e basın.", "Tarozi IP'sini kiriting va «Aloqani tekshirish»ni bosing."), false);
    }

    /// <summary>2026-09-30: открыть сразу вкладку «Штрих-код» (кнопка в окне «Весы»).</summary>
    public void ShowBarcodeTab() => Tabs.SelectedIndex = 2;

    private static TabItem MakeTab(string header, Control content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content } };

    private void UpdateHeader()
    {
        var ip = (_ipBox.Text ?? "").Trim();
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ")
            + (ip.Length > 0 ? ip : "—") + " · TCP " + ((int)(_portBox.Value ?? 5001)).ToString(CultureInfo.InvariantCulture);
    }

    // =====================================================================================
    // Подключение
    // =====================================================================================

    private Control BuildConnectionTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var prefs = UserPreferences.Instance;

        var card = Card(L("Адрес весов", "Таразанын дареги", "Scale address", "Tartı adresi", "Tarozi manzili"),
            L("Заводской адрес весов Rongta — 192.168.1.87. На весах он меняется так: держать [SETTING] 2 секунды → «@ IP address». Компьютер и весы должны быть в одной сети (первые три числа адреса совпадают).",
              "Rongta таразасынын заводдук дареги — 192.168.1.87. Таразада мындай өзгөртүлөт: [SETTING] 2 секунд басып туруу → «@ IP address». Компьютер менен тараза бир тармакта болушу керек (даректин алгачкы үч саны бирдей).",
              "The factory address of Rongta scales is 192.168.1.87. On the scale: hold [SETTING] for 2 seconds → “@ IP address”. The computer and the scale must be in the same network (first three numbers match).",
              "Rongta tartıların fabrika adresi 192.168.1.87'dir. Tartıda: [SETTING] 2 saniye basılı → “@ IP address”. Bilgisayar ve tartı aynı ağda olmalı (ilk üç sayı aynı).",
              "Rongta tarozilarining zavod manzili — 192.168.1.87. Tarozida: [SETTING] ni 2 soniya bosib turing → «@ IP address». Kompyuter va tarozi bitta tarmoqda bo‘lishi kerak (manzilning dastlabki uch soni bir xil)."),
            out var body);

        _ipBox = new TextBox { Text = IpOf(BrandRongta), Watermark = "192.168.1.87", MinWidth = 220 };
        _ipBox.LostFocus += (_, _) => { SaveConnection(); UpdateHeader(); };
        body.Children.Add(Row(L("IP-адрес весов", "Таразанын IP-дареги", "Scale IP address", "Tartı IP adresi", "Tarozi IP manzili"), _ipBox));

        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = prefs.RongtaScalePort, MinWidth = 140 };
        _portBox.ValueChanged += (_, _) => UpdateHeader();
        body.Children.Add(Row(L("TCP-порт для проверки", "Текшерүү үчүн TCP-порт", "TCP port to check", "Kontrol için TCP portu", "Tekshirish uchun TCP port"), _portBox));
        body.Children.Add(Text(L(
            "Порт самих весов в руководствах Rongta не указан. 5001 — порт по умолчанию из настроек RLS1000 (TCP/IP). Главное — ответ на ping: если весы отвечают, сеть в порядке; дальше обмен ведёт RLS1000.",
            "Таразанын өз порту Rongta колдонмолорунда көрсөтүлгөн эмес. 5001 — RLS1000 жөндөөлөрүндөгү (TCP/IP) демейки порт. Негизгиси — ping'ге жооп: тараза жооп берсе, тармак жакшы; андан ары алмашууну RLS1000 жүргүзөт.",
            "The scale's own port is not given in the Rongta manuals. 5001 is the RLS1000 default (TCP/IP settings). What matters is the ping reply: if the scale answers, the network is fine; RLS1000 does the rest.",
            "Tartının kendi portu Rongta kılavuzlarında yok. 5001, RLS1000 ayarlarındaki (TCP/IP) varsayılandır. Önemli olan ping yanıtıdır: tartı yanıt verirse ağ sorunsuzdur; gerisini RLS1000 yapar.",
            "Tarozining o‘z porti Rongta qo‘llanmalarida ko‘rsatilmagan. 5001 — RLS1000 sozlamalaridagi (TCP/IP) standart port. Asosiysi — ping javobi: tarozi javob bersa, tarmoq joyida; qolganini RLS1000 bajaradi."), "hint"));

        _checkButton = MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), true, async (_, _) => await CheckAsync().ConfigureAwait(true));
        body.Children.Add(ButtonRow(
            _checkButton,
            MakeButton(L("Найти весы в сети…", "Тармактан тараза табуу…", "Find scales on the network…", "Ağda tartı bul…", "Tarmoqda tarozi topish…"), false, async (_, _) =>
            {
                if (await OpenScanAsync(this, BrandRongta).ConfigureAwait(true))
                {
                    _ipBox.Text = IpOf(BrandRongta);
                    UpdateHeader();
                }
            }),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveConnection();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        panel.Children.Add(card);

        var rls = Card(L("Этот же адрес — в RLS1000", "Ушул эле дарек — RLS1000'до", "The same address goes into RLS1000", "Aynı adres RLS1000'e girilir", "Shu manzil RLS1000 ga ham kiritiladi"),
            L("RLS1000 хранит весы в своём списке: «Creat Connection» → колонка D «IP add. of label scale». Кнопка «Test Connection» в меню «Network» RLS1000 проверяет связь её собственным протоколом.",
              "RLS1000 таразаларды өз тизмесинде сактайт: «Creat Connection» → D тилкеси «IP add. of label scale». RLS1000'дун «Network» менюсундагы «Test Connection» өз протоколу менен текшерет.",
              "RLS1000 keeps scales in its own list: “Creat Connection” → column D “IP add. of label scale”. “Network → Test Connection” in RLS1000 checks the link with its own protocol.",
              "RLS1000 tartıları kendi listesinde tutar: “Creat Connection” → D sütunu “IP add. of label scale”. RLS1000'deki “Network → Test Connection” kendi protokolüyle kontrol eder.",
              "RLS1000 tarozilarni o‘z ro‘yxatida saqlaydi: «Creat Connection» → D ustuni «IP add. of label scale». RLS1000 dagi «Network → Test Connection» o‘z protokoli bilan tekshiradi."),
            out _);
        panel.Children.Add(rls);
        return panel;
    }

    private void SaveConnection()
    {
        var prefs = UserPreferences.Instance;
        prefs.RongtaScaleIp = (_ipBox.Text ?? "").Trim();
        prefs.RongtaScalePort = (int)(_portBox.Value ?? 5001);
        prefs.SaveToDisk();
    }

    private async Task CheckAsync()
    {
        SaveConnection();
        var ip = (_ipBox.Text ?? "").Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            ShowResult(L("Введите IP-адрес весов, например 192.168.1.87.", "Таразанын IP-дарегин киргизиңиз, мисалы 192.168.1.87.", "Enter the scale IP address, e.g. 192.168.1.87.", "Tartı IP adresini girin, ör. 192.168.1.87.", "Tarozi IP manzilini kiriting, masalan 192.168.1.87."), true);
            return;
        }
        var port = (int)(_portBox.Value ?? 5001);
        _checkButton.IsEnabled = false;
        ShowResult(L("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), false);
        try
        {
            var (pingOk, rtt) = await PingAsync(ip).ConfigureAwait(true);
            var tcpOk = await ScaleNetworkScanner.TcpPortOpenAsync(ip, port, 1500).ConfigureAwait(true);
            var arp = ScaleNetworkScanner.ReadArpTable()
                .FirstOrDefault(a => a.Address == ScaleNetworkScanner.ToNumber(System.Net.IPAddress.Parse(ip))).Mac;

            var lines = new List<string>
            {
                pingOk
                    ? L($"ping: весы отвечают ({rtt} мс)", $"ping: тараза жооп берет ({rtt} мс)", $"ping: the scale answers ({rtt} ms)", $"ping: tartı yanıt veriyor ({rtt} ms)", $"ping: tarozi javob beradi ({rtt} ms)")
                    : L("ping: нет ответа", "ping: жооп жок", "ping: no reply", "ping: yanıt yok", "ping: javob yo‘q"),
                tcpOk
                    ? L($"TCP {port}: порт открыт", $"TCP {port}: порт ачык", $"TCP {port}: port open", $"TCP {port}: port açık", $"TCP {port}: port ochiq")
                    : L($"TCP {port}: не принимает подключение", $"TCP {port}: туташууну кабыл албайт", $"TCP {port}: connection refused/timeout", $"TCP {port}: bağlantı kabul edilmiyor", $"TCP {port}: ulanishni qabul qilmaydi"),
            };
            if (arp is not null)
                lines.Add("MAC " + arp);

            // 2026-09-30: порт 4001 — чтение PLU №1 протоколом Dahua (только чтение), им касса
            // пишет товары напрямую. Ответ «0u0001a» — весы на связи (ячейка пустая).
            var direct = DahuaTmScaleService.TryCreate(ip, DahuaTmProtocol.DefaultPort, DahuaTmNameCodec.Rongta);
            var directCheck = direct is null ? null : await direct.TestConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            lines.Add(directCheck?.Replied == true
                ? L("TCP 4001: весы ответили — можно отправлять напрямую", "TCP 4001: тараза жооп берди — түз жөнөтсө болот", "TCP 4001: the scale answered — direct sending works", "TCP 4001: tartı yanıt verdi — doğrudan gönderim olur", "TCP 4001: tarozi javob berdi — to‘g‘ridan-to‘g‘ri yuborish mumkin")
                : L("TCP 4001: весы не ответили", "TCP 4001: тараза жооп берген жок", "TCP 4001: the scale did not answer", "TCP 4001: tartı yanıt vermedi", "TCP 4001: tarozi javob bermadi"));

            var ok = pingOk || tcpOk || arp is not null || directCheck?.Replied == true;
            var summary = ok
                ? L("Устройство по этому адресу в сети. ", "Бул даректеги түзмөк тармакта. ", "A device at this address is on the network. ", "Bu adresteki cihaz ağda. ", "Bu manzildagi qurilma tarmoqda. ")
                : L("По этому адресу никого нет: проверьте IP на весах, кабель и что весы включены. ", "Бул даректе эч ким жок: таразадагы IP'ни, кабелди жана тараза күйүк экенин текшериңиз. ", "Nobody at this address: check the IP on the scale, the cable and that the scale is on. ", "Bu adreste kimse yok: tartıdaki IP'yi, kabloyu ve tartının açık olduğunu kontrol edin. ", "Bu manzilda hech kim yo‘q: tarozidagi IP, kabel va tarozi yoqilganini tekshiring. ");
            ShowResult(summary + string.Join(" · ", lines), !ok);
            PosLogger.Log($"Rongta: проверка связи {ip}: ping={pingOk}, tcp{port}={tcpOk}, mac={arp ?? "-"}", "SCALES");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Rongta: проверка связи: {ex}", "SCALES");
            ShowResult(ex.Message, true);
        }
        finally
        {
            _checkButton.IsEnabled = true;
        }
    }

    internal static async Task<(bool Ok, long Rtt)> PingAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1500).ConfigureAwait(false);
            return (reply.Status == IPStatus.Success, reply.RoundtripTime);
        }
        catch (Exception)
        {
            return (false, 0);
        }
    }

    // =====================================================================================
    // Загрузка товаров
    // =====================================================================================

    private Control BuildUploadTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var prefs = UserPreferences.Instance;
        var useServer = string.Equals(prefs.RongtaDataSource, "server", StringComparison.OrdinalIgnoreCase);
        var useLan = prefs.RongtaDirectLan;
        string Current(bool active) => active ? "  ✓ " + L("выбрано сейчас", "азыр тандалган", "selected now", "şu an seçili", "hozir tanlangan") : "";

        // 2026-09-30: способ 3 — касса сама пишет товары на весы (протокол Dahua, TCP 4001), без
        // RLS1000. Весы владельца ответили на «!0V» и напечатали этикетки верно (6 серий проверок).
        var lanCard = Card(L("Напрямую по сети — без RLS1000 (рекомендуется)", "Тармак аркылуу түз — RLS1000'сиз (сунушталат)", "Directly over the network — no RLS1000 (recommended)", "Doğrudan ağ üzerinden — RLS1000'siz (önerilir)", "To‘g‘ridan-to‘g‘ri tarmoq orqali — RLS1000'siz (tavsiya etiladi)") + Current(useLan),
            L("Касса сама записывает отмеченные товары в ячейки PLU весов по сети (порт 4001) — как на весах TM-30F: название, цена за кг (два знака), код товара для штрих-кода, срок годности. У каждого товара постоянный номер PLU. Буквы, которых весы не печатают, касса заменяет похожими: «я» в конце названия — «Я», иногда «ш ы ь э ю» — заглавной.",
              "Касса белгиленген товарларды таразанын PLU уячаларына тармак аркылуу өзү жазат (4001 порт) — TM-30F таразасындагыдай: аталышы, кг баасы (эки белги), штрих-код үчүн товар коду, жарактуулук мөөнөтү. Ар бир товардын туруктуу PLU номери бар. Тараза баса албаган тамгаларды касса окшошуна алмаштырат: аталыштын аягындагы «я» — «Я», кээде «ш ы ь э ю» — баш тамга.",
              "The till writes the ticked goods into the scale's PLU slots itself over the network (port 4001) — like the TM-30F: name, price per kg (two decimals), item code for the barcode, shelf life. Every product keeps a fixed PLU number. Letters the scale cannot print are replaced with similar ones: a final «я» becomes «Я», sometimes «ш ы ь э ю» become capitals.",
              "Kasa işaretli ürünleri tartının PLU hücrelerine ağ üzerinden kendisi yazar (port 4001) — TM-30F'teki gibi: ad, kg fiyatı (iki ondalık), barkod için ürün kodu, raf ömrü. Her ürünün sabit PLU numarası vardır. Tartının basamadığı harfler benzerleriyle değiştirilir: sondaki «я» → «Я», bazen «ш ы ь э ю» büyük harf olur.",
              "Kassa belgilangan tovarlarni tarozining PLU kataklariga tarmoq orqali o‘zi yozadi (4001 port) — TM-30F dagi kabi: nomi, kg narxi (ikki kasr), shtrix-kod uchun tovar kodi, yaroqlilik muddati. Har bir tovarning doimiy PLU raqami bor. Tarozi chop eta olmaydigan harflar o‘xshashiga almashtiriladi: oxiridagi «я» — «Я», ba’zan «ш ы ь э ю» — bosh harf."),
            out var lanBody);
        // Префикс (отдел) штрих-кода — на вкладке «Штрих-код», рядом с типом и примером этикетки.
        var shelfBox = new NumericUpDown { Minimum = 0, Maximum = 999, Increment = 1, FormatString = "0", Value = Math.Clamp(prefs.RongtaLanShelfLifeDays, 0, 999), MinWidth = 140 };
        shelfBox.ValueChanged += (_, _) => { prefs.RongtaLanShelfLifeDays = (int)(shelfBox.Value ?? 0); prefs.SaveToDisk(); };
        lanBody.Children.Add(Row(L("Срок годности, дней (0 — не задан)", "Жарактуулук мөөнөтү, күн (0 — коюлган эмес)", "Shelf life, days (0 — not set)", "Raf ömrü, gün (0 — yok)", "Yaroqlilik muddati, kun (0 — belgilanmagan)"), shelfBox));
        lanBody.Children.Add(Text(L("Пример: «Шоколад молочный 90г» на весах будет «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ».",
            "Мисал: «Шоколад молочный 90г» таразада «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» болот.",
            "Example: «Шоколад молочный 90г» shows as «Шоколад молочнЫй 90г», «Колбаса вареная» as «Колбаса варенаЯ».",
            "Örnek: «Шоколад молочный 90г» tartıda «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» olur.",
            "Misol: «Шоколад молочный 90г» tarozida «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» bo‘ladi."), "hint"));
        if (!useLan)
        {
            lanBody.Children.Add(ButtonRow(MakeButton(L("Выбрать этот способ", "Бул жолду тандоо", "Use this method", "Bu yöntemi seç", "Shu usulni tanlash"), true, (_, _) =>
            {
                prefs.RongtaDataSource = "lan";
                prefs.SaveToDisk();
                NurMarketKassa.AvaloniaHost.Services.LabelScaleStore.CaptureActive();
                ShowResult(L("Выбрано: напрямую по сети. Откройте окно «Весы» и нажмите «Отправить на весы».", "Тандалды: тармак аркылуу түз. «Таразалар» терезесин ачып «Таразага жөнөтүү» басыңыз.", "Selected: directly over the network. Open the “Scales” window and press “Send to scale”.", "Seçildi: doğrudan ağ üzerinden. «Tartı» penceresini açıp «Tartıya gönder»e basın.", "Tanlandi: to‘g‘ridan-to‘g‘ri tarmoq orqali. «Tarozi» oynasini ochib «Taroziga yuborish»ni bosing."), false);
            })));
        }
        panel.Children.Add(lanCard);

        panel.Children.Add(Card(L("Способ 1 — файл .txp и F9 (по умолчанию)", "1-жол — .txp файлы жана F9 (демейки)", "Method 1 — .txp file and F9 (default)", "Yöntem 1 — .txp dosyası ve F9 (varsayılan)", "1-usul — .txp fayli va F9 (standart)") + Current(!useServer && !useLan),
            L("Касса скачивает список весовых товаров в формате RLS1000 (.txp, как вкладка «Rongta» на сайте), кладёт его в рабочую папку RLS1000, запускает RLS1000 и «нажимает» F9 — «Download PLU» (полная перезапись товаров на весах). Касса видит только «команда передана»: успех показывает сама RLS1000.",
              "Касса салмактуу товарлардын тизмесин RLS1000 форматында (.txp, сайттагы «Rongta» өтмөгүндөй) жүктөп алып, RLS1000'дун жумушчу папкасына салат, RLS1000'ду иштетип F9 — «Download PLU» (таразадагы товарларды толук алмаштыруу) «басат». Касса «буйрук берилди» дегенди гана көрөт: ийгиликти RLS1000 өзү көрсөтөт.",
              "The till downloads the weighed goods in RLS1000 format (.txp, like the “Rongta” tab on the website), puts it into the RLS1000 work folder, starts RLS1000 and “presses” F9 — “Download PLU” (full overwrite of goods on the scale). The till only sees “command sent”; RLS1000 itself shows success.",
              "Kasa tartılı ürünleri RLS1000 biçiminde (.txp, sitedeki “Rongta” sekmesi gibi) indirir, RLS1000 çalışma klasörüne koyar, RLS1000'i başlatır ve F9 — “Download PLU”ya (tartıdaki ürünlerin tamamen üzerine yazılması) “basar”. Kasa yalnızca “komut gönderildi”yi görür; başarıyı RLS1000 gösterir.",
              "Kassa vaznli tovarlar ro‘yxatini RLS1000 formatida (.txp, saytdagi «Rongta» bo‘limi kabi) yuklab oladi, RLS1000 ish papkasiga qo‘yadi, RLS1000 ni ishga tushirib F9 — «Download PLU» (tarozidagi tovarlarni to‘liq qayta yozish) ni «bosadi». Kassa faqat «buyruq yuborildi»ni ko‘radi; muvaffaqiyatni RLS1000 o‘zi ko‘rsatadi."),
            out _));

        var localIps = ScaleNetworkScanner.GetLocalSubnets().Select(s => s.LocalAddress.ToString()).Distinct().ToList();
        var ipsText = localIps.Count > 0 ? string.Join(", ", localIps) : "—";
        panel.Children.Add(Card(L("Способ 2 — «свой сервер» (касса отдаёт товары RLS1000 по TCP/IP)", "2-жол — «өз сервер» (касса товарларды RLS1000'го TCP/IP аркылуу берет)", "Method 2 — “own server” (the till serves goods to RLS1000 over TCP/IP)", "Yöntem 2 — “kendi sunucu” (kasa ürünleri RLS1000'e TCP/IP ile verir)", "2-usul — «o‘z server» (kassa tovarlarni RLS1000 ga TCP/IP orqali beradi)") + Current(useServer),
            L($"Касса становится TCP-сервером на порту {prefs.RongtaServerPort} и отдаёт товары из локального каталога без обращения к сайту (раздел 2.2–2.4 руководства RLS1000). Один раз в RLS1000: File → Options → вкладка TCP/IP → галочка, адрес = IP этого компьютера ({ipsText}), порт = {prefs.RongtaServerPort}. Название товара уходит латиницей (кодировка кириллицы в протоколе не описана).",
              $"Касса {prefs.RongtaServerPort} портунда TCP-сервер болуп, товарларды сайтка кайрылбай жергиликтүү каталогдон берет (RLS1000 колдонмосу, 2.2–2.4). RLS1000'до бир жолу: File → Options → TCP/IP өтмөгү → белги, дарек = бул компьютердин IP'си ({ipsText}), порт = {prefs.RongtaServerPort}. Товардын аты латынча кетет (протоколдо кирилл коддоосу жазылган эмес).",
              $"The till becomes a TCP server on port {prefs.RongtaServerPort} and serves goods from the local catalog without the website (RLS1000 manual, 2.2–2.4). Once in RLS1000: File → Options → TCP/IP tab → tick, address = this computer's IP ({ipsText}), port = {prefs.RongtaServerPort}. Names are sent in Latin letters (Cyrillic encoding is not described in the protocol).",
              $"Kasa {prefs.RongtaServerPort} portunda TCP sunucusu olur ve ürünleri siteye gitmeden yerel katalogdan verir (RLS1000 kılavuzu, 2.2–2.4). RLS1000'de bir kez: File → Options → TCP/IP sekmesi → işaret, adres = bu bilgisayarın IP'si ({ipsText}), port = {prefs.RongtaServerPort}. Ürün adları Latin harfleriyle gider (protokolde Kiril kodlaması tanımlı değil).",
              $"Kassa {prefs.RongtaServerPort} portida TCP-server bo‘lib, tovarlarni saytga murojaat qilmay mahalliy katalogdan beradi (RLS1000 qo‘llanmasi, 2.2–2.4). RLS1000 da bir marta: File → Options → TCP/IP bo‘limi → belgi, manzil = shu kompyuter IP'si ({ipsText}), port = {prefs.RongtaServerPort}. Tovar nomi lotincha yuboriladi (protokolda kirill kodlashi yozilmagan)."),
            out _));

        var exe = RongtaSetupService.TryFindInstalledExePath();
        var status = Card(L("Программа RLS1000 на этом компьютере", "Бул компьютердеги RLS1000 программасы", "RLS1000 on this computer", "Bu bilgisayardaki RLS1000", "Shu kompyuterdagi RLS1000"),
            exe is not null
                ? L("Установлена: ", "Орнотулган: ", "Installed: ", "Kurulu: ", "O‘rnatilgan: ") + exe
                : L("Не найдена. Окно «Весы» предложит установить её при первой отправке.", "Табылган жок. «Таразалар» терезеси биринчи жөнөтүүдө орнотууну сунуштайт.", "Not found. The “Scales” window offers to install it on the first upload.", "Bulunamadı. «Tartı» penceresi ilk gönderimde kurmayı önerir.", "Topilmadi. «Tarozi» oynasi birinchi yuborishda o‘rnatishni taklif qiladi."),
            out var statusBody);
        statusBody.Children.Add(ButtonRow(MakeButton(L("Открыть окно «Весы» (Rongta)", "«Таразалар» терезесин ачуу (Rongta)", "Open the “Scales” window (Rongta)", "«Tartı» penceresini aç (Rongta)", "«Tarozi» oynasini ochish (Rongta)"), true,
            (_, _) => OpenScalesWindow(this, BrandRongta))));
        panel.Children.Add(status);
        return panel;
    }

    // =====================================================================================
    // Штрих-код
    // =====================================================================================

    private Control BuildBarcodeTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };

        var card = Card(L("Тип штрих-кода на весах", "Таразадагы штрих-коддун түрү", "Barcode type on the scale", "Tartıdaki barkod türü", "Tarozidagi shtrix-kod turi"),
            L("Тип (0–99) задаётся в RLS1000: «Setting» → «barcode type», у товара — поле «Barcode Type» в PLU Manager; на весах — [SETTING] → «set default barcode type». Таблица типов — Appendix II руководства RLS1000.",
              "Түрү (0–99) RLS1000'до коюлат: «Setting» → «barcode type», товарда — PLU Manager'деги «Barcode Type» талаасы; таразада — [SETTING] → «set default barcode type». Түрлөрдүн таблицасы — RLS1000 колдонмосунун Appendix II.",
              "The type (0–99) is set in RLS1000: “Setting” → “barcode type”, per item — the “Barcode Type” field in PLU Manager; on the scale — [SETTING] → “set default barcode type”. Type table — Appendix II of the RLS1000 manual.",
              "Tür (0–99) RLS1000'de ayarlanır: “Setting” → “barcode type”, ürün başına — PLU Manager'da “Barcode Type”; tartıda — [SETTING] → “set default barcode type”. Tür tablosu — RLS1000 kılavuzu Appendix II.",
              "Turi (0–99) RLS1000 da belgilanadi: «Setting» → «barcode type», tovarda — PLU Manager dagi «Barcode Type» maydoni; tarozida — [SETTING] → «set default barcode type». Turlar jadvali — RLS1000 qo‘llanmasi, Appendix II."),
            out var body);

        body.Children.Add(Text(CompanyFormatText(), "hint"));

        _typeBox = new ComboBox { MinWidth = 360 };
        foreach (var t in _types)
            _typeBox.Items.Add($"{t.Type:00} — {t.Pattern}");
        // 2026-09-30: тип запоминается (раньше всегда открывался 07). По умолчанию 02 — так стоит на
        // весах владельца (этикетка 20 34567 00290 8: отдел 20, код 5 цифр, сумма 5 цифр).
        _typeBox.SelectedIndex = Math.Max(0, _types.FindIndex(t => t.Type == UserPreferences.Instance.RongtaBarcodeType));
        _typeBox.SelectionChanged += (_, _) =>
        {
            if (SelectedType is { } selected)
            {
                UserPreferences.Instance.RongtaBarcodeType = selected.Type;
                UserPreferences.Instance.SaveToDisk();
            }
            UpdateSample();
        };
        body.Children.Add(Row(L("Тип штрих-кода", "Штрих-коддун түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"), _typeBox));

        _typeHint = Text("", "hint");
        body.Children.Add(_typeHint);

        // 2026-09-30: отдел = «префикс штрих-кода», который касса пишет в каждый товар при отправке
        // напрямую (поле Dahua madv5; на этикетке весов владельца напечаталось 20).
        _department = new NumericUpDown { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "0", Value = Math.Clamp(UserPreferences.Instance.RongtaLanBarcodePrefix, 0, 99), MinWidth = 140 };
        _department.ValueChanged += (_, _) =>
        {
            UserPreferences.Instance.RongtaLanBarcodePrefix = (int)(_department.Value ?? 20);
            UserPreferences.Instance.SaveToDisk();
            UpdateSample();
        };
        body.Children.Add(Row(L("Отдел / префикс товара (DD)", "Товардын бөлүмү / префикси (DD)", "Item department / prefix (DD)", "Ürün reyonu / öneki (DD)", "Tovar bo‘limi / prefiksi (DD)"), _department));
        body.Children.Add(Text(L("При отправке «напрямую по сети» касса сама записывает этот отдел в каждый товар. Тип штрих-кода меняется только на весах: [SETTING] → «set default barcode type».",
            "«Тармак аркылуу түз» жөнөткөндө касса бул бөлүмдү ар бир товарга өзү жазат. Штрих-коддун түрү таразада гана өзгөрөт: [SETTING] → «set default barcode type».",
            "When sending “directly over the network” the till writes this department into every item. The barcode type is changed only on the scale: [SETTING] → “set default barcode type”.",
            "“Doğrudan ağ üzerinden” gönderimde kasa bu reyonu her ürüne yazar. Barkod türü yalnızca tartıda değişir: [SETTING] → “set default barcode type”.",
            "«To‘g‘ridan-to‘g‘ri tarmoq orqali» yuborishda kassa bu bo‘limni har bir tovarga o‘zi yozadi. Shtrix-kod turi faqat tarozida o‘zgaradi: [SETTING] → «set default barcode type»."), "hint"));

        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 9999999, Increment = 1, FormatString = "0", Value = 123, MinWidth = 140 };
        _sampleCode.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Код товара в штрих-коде", "Штрих-коддогу товар коду", "Item code in the barcode", "Barkoddaki ürün kodu", "Shtrix-koddagi tovar kodi"), _sampleCode));

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
        body.Children.Add(ButtonRow(MakeButton(L("Подобрать под кассу", "Кассага ылайыктоо", "Match the till", "Kasaya uydur", "Kassaga moslash"), true, (_, _) => MatchToKassa())));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Что проверить на весах", "Таразада эмнени текшерүү керек", "What to check on the scale", "Tartıda neyi kontrol etmeli", "Tarozida nimani tekshirish kerak"),
            L("1) Для типов 00–09 первые две цифры — «отдел» (Dept.) товара: чтобы касса узнала весовой штрих-код, отдел должен быть 20–29. Типы 10–19 ставят префикс 20–29 сами — отдел не важен. 2) «Код товара» в штрих-коде весы берут из поля товара Code (Art. No.) — оно должно совпадать с PLU/кодом товара в кассе. При способе «свой сервер» касса сейчас передаёт Code = 0 — проверьте первую этикетку. 3) Сумма в штрих-коде считается в сотых (тыйынах) при двух знаках после запятой у цены — это допущение, в руководстве не описано.",
              "1) 00–09 түрлөрүндө алгачкы эки сан — товардын «бөлүмү» (Dept.): касса салмактуу штрих-кодду таанышы үчүн бөлүм 20–29 болушу керек. 10–19 түрлөрү 20–29 префиксин өздөрү коюшат — бөлүм маанилүү эмес. 2) Штрих-коддогу «товар кодун» тараза товардын Code (Art. No.) талаасынан алат — ал кассадагы PLU/товар коду менен дал келиши керек. «Өз сервер» жолунда касса азыр Code = 0 берет — биринчи этикетканы текшериңиз. 3) Штрих-коддогу сумма баада эки белги болгондо жүздүктөрдө (тыйында) эсептелет — бул божомол, колдонмодо жазылган эмес.",
              "1) For types 00–09 the first two digits are the item “department” (Dept.): for the till to recognise a weight barcode it must be 20–29. Types 10–19 put prefix 20–29 themselves — department does not matter. 2) The scale takes the “item code” in the barcode from the item's Code (Art. No.) field — it must match the PLU/item code in the till. With “own server” the till currently sends Code = 0 — check the first label. 3) The amount is assumed to be in hundredths (tiyin) with two price decimals — this is an assumption, not in the manual.",
              "1) 00–09 türlerinde ilk iki hane ürünün “reyonu”dur (Dept.): kasanın tartı barkodunu tanıması için 20–29 olmalı. 10–19 türleri 20–29 önekini kendileri koyar — reyon önemsiz. 2) Barkoddaki “ürün kodu” ürünün Code (Art. No.) alanından alınır — kasadaki PLU/ürün koduyla aynı olmalı. “Kendi sunucu” yolunda kasa şu an Code = 0 gönderiyor — ilk etiketi kontrol edin. 3) Tutarın iki ondalıklı fiyatta yüzde birlerle (tiyin) olduğu varsayılır — kılavuzda yok.",
              "1) 00–09 turlarida dastlabki ikki raqam — tovar «bo‘limi» (Dept.): kassa vaznli shtrix-kodni tanishi uchun bo‘lim 20–29 bo‘lishi kerak. 10–19 turlari 20–29 prefiksini o‘zlari qo‘yadi — bo‘lim muhim emas. 2) Shtrix-koddagi «tovar kodi»ni tarozi tovarning Code (Art. No.) maydonidan oladi — u kassadagi PLU/tovar kodiga mos bo‘lishi kerak. «O‘z server» usulida kassa hozir Code = 0 yuboradi — birinchi yorliqni tekshiring. 3) Summa narxda ikki kasr bo‘lganda yuzdan birlarda (tiyinda) deb faraz qilinadi — qo‘llanmada yozilmagan."),
            out _));

        UpdateSample();
        return panel;
    }

    private RongtaBarcodeFormat.BarcodeType? SelectedType =>
        _typeBox.SelectedIndex >= 0 && _typeBox.SelectedIndex < _types.Count ? _types[_typeBox.SelectedIndex] : null;

    private void UpdateSample()
    {
        if (_verdictText is null || _sampleText is null)
            return;
        var type = SelectedType;
        if (type is null)
            return;

        _typeHint.Text = type.Prefix switch
        {
            RongtaBarcodeFormat.PrefixKind.Fixed2 => L($"Префикс {type.FixedPrefix} ставится всегда; ", $"{type.FixedPrefix} префикси дайыма коюлат; ", $"Prefix {type.FixedPrefix} is always used; ", $"{type.FixedPrefix} öneki her zaman kullanılır; ", $"{type.FixedPrefix} prefiksi doim qo‘yiladi; "),
            RongtaBarcodeFormat.PrefixKind.Department1 => L("Первая цифра — отдел (1 цифра); ", "Биринчи сан — бөлүм (1 сан); ", "First digit — department (1 digit); ", "İlk hane — reyon (1 hane); ", "Birinchi raqam — bo‘lim (1 raqam); "),
            _ => L("Первые две цифры — отдел товара; ", "Алгачкы эки сан — товардын бөлүмү; ", "First two digits — item department; ", "İlk iki hane — ürün reyonu; ", "Dastlabki ikki raqam — tovar bo‘limi; "),
        } + L($"код {type.CodeDigits} цифр", $"код {type.CodeDigits} сан", $"code {type.CodeDigits} digits", $"kod {type.CodeDigits} hane", $"kod {type.CodeDigits} raqam")
          + (type.Value switch
          {
              RongtaBarcodeFormat.ValueKind.Price => L($", сумма {type.ValueDigits} цифр", $", сумма {type.ValueDigits} сан", $", amount {type.ValueDigits} digits", $", tutar {type.ValueDigits} hane", $", summa {type.ValueDigits} raqam"),
              RongtaBarcodeFormat.ValueKind.Weight => L($", вес {type.ValueDigits} цифр", $", салмак {type.ValueDigits} сан", $", weight {type.ValueDigits} digits", $", ağırlık {type.ValueDigits} hane", $", vazn {type.ValueDigits} raqam")
                  + (type.GramsPerUnit switch
                  {
                      1 => L(" (в граммах)", " (граммда)", " (grams)", " (gram)", " (grammda)"),
                      10 => L(" (в десятках граммов)", " (он граммдап)", " (tens of grams)", " (onlarca gram)", " (o‘nlab gramm)"),
                      100 => L(" (в сотнях граммов)", " (жүз граммдап)", " (hundreds of grams)", " (yüzlerce gram)", " (yuzlab gramm)"),
                      _ => L(" (без точки — единица зависит от настройки весов)", " (чекитсиз — бирдик тараза жөндөөсүнө жараша)", " (no decimal point — unit depends on scale settings)", " (ondalıksız — birim tartı ayarına bağlı)", " (nuqtasiz — birlik tarozi sozlamasiga bog‘liq)"),
                  }),
              _ => L(", без веса и суммы", ", салмаксыз жана суммасыз", ", no weight or amount", ", ağırlık ve tutar yok", ", vazn va summasiz"),
          }) + ".";

        var code = (long)(_sampleCode.Value ?? 1);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var amount = Math.Round((decimal)(_samplePrice.Value ?? 250) * grams / 1000m, 2);
        var sample = RongtaBarcodeFormat.BuildSample(type, (int)(_department.Value ?? 20), code, grams, amount);
        _sampleText.Text = sample ?? "—";

        if (type.Value == RongtaBarcodeFormat.ValueKind.None)
        {
            SetVerdict(false, L("В этом типе нет веса и суммы — для весового товара не подходит.", "Бул түрдө салмак да, сумма да жок — салмактуу товарга ылайыксыз.", "This type has no weight or amount — not for weighed goods.", "Bu türde ağırlık ve tutar yok — tartılı ürün için uygun değil.", "Bu turda vazn ham, summa ham yo‘q — vaznli tovarga mos emas."));
            return;
        }
        if (sample is null)
        {
            SetVerdict(false, L("Вес «без точки» (WWWWW): его единица зависит от настроек весов, касса не может проверить — выберите другой тип.", "«Чекитсиз» салмак (WWWWW): бирдиги тараза жөндөөсүнө жараша, касса текшере албайт — башка түр тандаңыз.", "“No decimal point” weight (WWWWW): its unit depends on the scale settings, the till cannot verify it — choose another type.", "“Ondalıksız” ağırlık (WWWWW): birimi tartı ayarına bağlı, kasa doğrulayamaz — başka tür seçin.", "«Nuqtasiz» vazn (WWWWW): birligi tarozi sozlamasiga bog‘liq, kassa tekshira olmaydi — boshqa tur tanlang."));
            return;
        }

        var (ok, message) = VerifyWithKassa(sample, code, type.Value == RongtaBarcodeFormat.ValueKind.Weight, grams, amount);
        if (ok && type.Prefix == RongtaBarcodeFormat.PrefixKind.Department1)
        {
            // Отдел одной цифрой: касса берёт в префикс ДВЕ цифры — отдел и первую цифру кода.
            // Пример сходится только пока эта цифра 0 (маленький код) — предупреждаем.
            ok = false;
            message = L("сходится случайно: касса считает префиксом отдел и первую цифру кода — при больших кодах товар не найдётся. Выберите тип с двумя цифрами префикса.",
                "кокустан дал келет: касса префикс катары бөлүмдү жана коддун биринчи санын алат — чоң коддордо товар табылбайт. Эки сандуу префикси бар түрдү тандаңыз.",
                "matches by chance: the till takes the department AND the first code digit as the prefix — larger codes will not be found. Choose a type with a two-digit prefix.",
                "tesadüfen uyuyor: kasa önek olarak reyonu ve kodun ilk hanesini alır — büyük kodlarda ürün bulunmaz. İki haneli önekli bir tür seçin.",
                "tasodifan mos keladi: kassa prefiks sifatida bo‘lim va kodning birinchi raqamini oladi — katta kodlarda tovar topilmaydi. Ikki xonali prefiksli turni tanlang.");
        }
        SetVerdict(ok, message);
    }

    private void SetVerdict(bool ok, string message)
    {
        _verdictText.Text = (ok ? "✓ " : "⚠ " + L("Касса НЕ узнает товар: ", "Касса товарды ТААНЫБАЙТ: ", "The till will NOT recognise the item: ", "Kasa ürünü TANIMAZ: ", "Kassa tovarni TANIMAYDI: ")) + message;
        _verdictText.Foreground = ThemeBrush(this, ok ? "BrushSuccess" : "BrushWarning", ok ? Brushes.Green : Brushes.DarkOrange);
    }

    /// <summary>«Подобрать под кассу»: перебирает типы (сначала с фиксированным префиксом — им
    /// не нужен отдел 20–29 у каждого товара) и берёт первый, который касса разбирает верно
    /// при текущей настройке компании. В весы ничего не отправляется.</summary>
    private void MatchToKassa()
    {
        var byWeight = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
        var code = (long)(_sampleCode.Value ?? 123);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var amount = Math.Round((decimal)(_samplePrice.Value ?? 250) * grams / 1000m, 2);

        var ordered = _types
            .Where(t => t.Value == (byWeight ? RongtaBarcodeFormat.ValueKind.Weight : RongtaBarcodeFormat.ValueKind.Price)
                        && t.Prefix != RongtaBarcodeFormat.PrefixKind.Department1) // см. UpdateSample: сходятся лишь случайно
            .OrderBy(t => t.Prefix == RongtaBarcodeFormat.PrefixKind.Fixed2 ? 0 : 1)
            .ThenBy(t => t.Type);

        foreach (var type in ordered)
        {
            var departments = type.Prefix switch
            {
                RongtaBarcodeFormat.PrefixKind.Department2 => byWeight ? new[] { 20, 21, 22, 23, 24, 26, 27, 28, 29, 25 } : new[] { 25, 20, 21, 22, 23, 24, 26, 27, 28, 29 },
                RongtaBarcodeFormat.PrefixKind.Department1 => new[] { 2 },
                _ => new[] { 0 },
            };
            foreach (var dept in departments)
            {
                var sample = RongtaBarcodeFormat.BuildSample(type, dept, code, grams, amount);
                if (sample is null || !VerifyWithKassa(sample, code, byWeight, grams, amount).Ok)
                    continue;
                _typeBox.SelectedIndex = _types.IndexOf(type);
                if (type.Prefix != RongtaBarcodeFormat.PrefixKind.Fixed2)
                    _department.Value = dept;
                UpdateSample();
                ShowResult(L($"Подходит тип {type.Type:00}", $"{type.Type:00} түрү ылайыктуу", $"Type {type.Type:00} fits", $"{type.Type:00} türü uygun", $"{type.Type:00} turi mos")
                           + (type.Prefix == RongtaBarcodeFormat.PrefixKind.Fixed2 ? "" : L($" с отделом {dept} у весовых товаров", $" салмактуу товарларда {dept} бөлүмү менен", $" with department {dept} on weighed items", $" tartılı ürünlerde {dept} reyonuyla", $" vaznli tovarlarda {dept} bo‘lim bilan"))
                           + L(". Задайте его в RLS1000 и проверьте первую этикетку сканером кассы.", ". Аны RLS1000'до коюп, биринчи этикетканы касса сканери менен текшериңиз.", ". Set it in RLS1000 and check the first label with the till scanner.", ". RLS1000'de ayarlayıp ilk etiketi kasa tarayıcısıyla kontrol edin.", ". Uni RLS1000 da belgilang va birinchi yorliqni kassa skaneri bilan tekshiring."), false);
                return;
            }
        }
        ShowResult(L("Ни один тип Rongta не совпал с настройкой компании — поменяйте раскладку/режим на сайте (Весы → Настройки).", "Rongta'нын бир да түрү компаниянын жөндөөсүнө дал келген жок — сайттан раскладканы/режимди өзгөртүңүз (Таразалар → Жөндөөлөр).", "No Rongta type matches the company setting — change the layout/mode on the website (Scales → Settings).", "Hiçbir Rongta türü şirket ayarına uymadı — sitede düzeni/modu değiştirin (Tartılar → Ayarlar).", "Birorta Rongta turi kompaniya sozlamasiga mos kelmadi — saytda tartib/rejimni o‘zgartiring (Tarozilar → Sozlamalar)."), true);
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
        SaveConnection();
        Close();
    }
}
