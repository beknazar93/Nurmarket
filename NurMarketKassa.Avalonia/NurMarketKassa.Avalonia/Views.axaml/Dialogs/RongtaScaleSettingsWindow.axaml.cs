using System.Globalization;
using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;
using RongtaBarcodeTypeWriter = NurMarketKassa.AvaloniaHost.Services.RongtaBarcodeTypeWriter;

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
/// 2026-11-13 (разбор RLS1000 на локальной ловушке, docs/Отчеты/Rongta-RLS1000-разбор.md): порты весов известны (товары — TCP 4001 «!0V»,
/// RLS1000 — UDP 5001). Вкладки: «Подключение / Загрузка товаров / Штрих-код / Дата и параметры»; тип штрихкода в карточки и дата —
/// «родными» командами весов (RongtaUdpProtocol), поле «TCP-порт для проверки» убрано.
/// </summary>
public partial class RongtaScaleSettingsWindow : Window
{
    private TextBox _ipBox = null!;
    private Button _checkButton = null!;
    private TextBlock _currentTypeText = null!;
    private TextBlock _currentTypeWarning = null!;
    private Button _fixTypeButton = null!;
    private TextBlock _writeTypeText = null!;
    private TextBlock _recommendText = null!;
    private bool _verdictOk = true; // 2026-11-13: касса читает пример этикетки выбранного типа (для кнопки «Исправить: тип 07»)

    private ComboBox _typeBox = null!;
    private NumericUpDown _department = null!;
    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private NumericUpDown _samplePrice = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;
    private TextBlock _typeHint = null!;
    private TextBlock _ceilingText = null!;
    // 2026-11-13: рекомендуемый 07 — первым в списке (раньше список открывался на выбранном типе, 07 приходилось искать прокруткой).
    private readonly List<RongtaBarcodeFormat.BarcodeType> _types = RongtaBarcodeFormat.PickerOrder().ToList();

    public RongtaScaleSettingsWindow()
    {
        InitializeComponent();
        // 2026-09-29: 860×700 не помещалось на 1024×768 при 125–150 % — по экрану кассы (DialogScreenFit).
        Opened += (_, _) => this.FitToKassaScreen();
        Title = L("Настройки весов Rongta", "Rongta таразасынын жөндөөлөрү", "Rongta scale settings", "Rongta tartı ayarları", "Rongta tarozi sozlamalari");
        TitleText.Text = Title;
        // 2026-09-30: весы Rongta понимают протокол Dahua (порт 4001) — касса пишет товары сама.
        // 2026-11-13 (разбор RLS1000): тип штрихкода и дату касса передаёт «родной» командой весов (порт 5001) — вкладки «Штрих-код» и «Дата и параметры».
        IntroText.Text = L(
            "Весы Rongta (RLS1000/RLS1100): касса записывает товары прямо в весы по сети, без программы RLS1000. Здесь — адрес весов, способ загрузки, тип штрихкода и дата.",
            "Rongta таразасы (RLS1000/RLS1100): касса товарларды RLS1000 программасыз тармак аркылуу түз таразага жазат. Бул жерде — таразанын дареги, жүктөө жолу, штрих-коддун түрү жана дата.",
            "Rongta scales (RLS1000/RLS1100): the till writes goods straight into the scale over the network, without RLS1000. Here: scale address, upload method, barcode type and date.",
            "Rongta tartılar (RLS1000/RLS1100): kasa ürünleri RLS1000 olmadan ağ üzerinden doğrudan tartıya yazar. Burada: tartı adresi, yükleme yolu, barkod türü ve tarih.",
            "Rongta tarozilari (RLS1000/RLS1100): kassa tovarlarni RLS1000'siz tarmoq orqali to‘g‘ridan-to‘g‘ri taroziga yozadi. Bu yerda — tarozi manzili, yuklash usuli, shtrix-kod turi va sana.");

        Tabs.Items.Add(MakeTab(L("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), BuildConnectionTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        Tabs.Items.Add(MakeTab(L("Дата и параметры", "Дата жана параметрлер", "Date and settings", "Tarih ve ayarlar", "Sana va parametrlar"), BuildDateTab()));
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
        // 2026-11-13: порты весов известны (товары — TCP 4001, тип штрихкода и дата — UDP 5001), поле «TCP-порт для проверки» убрано.
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ")
            + (ip.Length > 0 ? ip : "—") + L(" · товары — порт 4001 · тип штрихкода и дата — порт 5001", " · товарлар — 4001 порт · штрих-коддун түрү жана дата — 5001 порт", " · goods — port 4001 · barcode type and date — port 5001", " · ürünler — port 4001 · barkod türü ve tarih — port 5001", " · tovarlar — 4001 port · shtrix-kod turi va sana — 5001 port");
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
        // 2026-11-13 (разбор RLS1000): порты весов найдены — поле «TCP-порт для проверки» больше не нужно.
        body.Children.Add(Text(L(
            "«Проверить связь» спрашивает весы по сети (ping) и читает ячейку PLU №1 (порт 4001 — им касса пишет товары). На весах ничего не меняется.",
            "«Байланышты текшерүү» таразаны тармактан сурайт (ping) жана PLU №1 уячасын окуйт (4001 порт — аны менен касса товарларды жазат). Таразада эч нерсе өзгөрбөйт.",
            "“Check connection” asks the scale over the network (ping) and reads PLU slot No. 1 (port 4001, used by the till to write goods). Nothing changes on the scale.",
            "“Bağlantıyı kontrol et” tartıya ağdan sorar (ping) ve 1 numaralı PLU hücresini okur (port 4001 — kasa ürünleri bununla yazar). Tartıda hiçbir şey değişmez.",
            "«Aloqani tekshirish» taroziga tarmoq orqali murojaat qiladi (ping) va 1-PLU katagini o‘qiydi (4001 port — u orqali kassa tovarlarni yozadi). Tarozida hech narsa o‘zgarmaydi."), "hint"));

        _checkButton = MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), false, async (_, _) => await CheckAsync().ConfigureAwait(true));
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
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), true, (_, _) =>
            {
                SaveConnection();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        panel.Children.Add(card);

        // 2026-11-13: по живой программе RLS1000 V1.129 (русский интерфейс) — колонка «id магазина / ip магазина», «Сеть → Проверка порта» (F12).
        var rls = Card(L("Если пользуетесь программой RLS1000", "RLS1000 программасын колдонсоңуз", "If you use the RLS1000 program", "RLS1000 programını kullanıyorsanız", "RLS1000 dasturidan foydalansangiz"),
            L("В RLS1000 тот же адрес вписывают в строку весов (колонка IP). «Сеть → Проверка порта» (F12) проверяет связь. Не держите RLS1000 и кассу на связи с весами одновременно.",
              "RLS1000'до ушул эле даректи тараза сабына жазышат (IP тилкеси). «Сеть → Проверка порта» (F12) байланышты текшерет. RLS1000 менен кассаны таразага бир убакта туташтырбаңыз.",
              "In RLS1000 the same address goes into the scale row (IP column). “Network → Test port” (F12) checks the link. Do not keep RLS1000 and the till talking to the scale at the same time.",
              "RLS1000'de aynı adres tartı satırına yazılır (IP sütunu). “Ağ → Port testi” (F12) bağlantıyı kontrol eder. RLS1000 ile kasayı aynı anda tartıya bağlamayın.",
              "RLS1000 da shu manzil tarozi qatoriga yoziladi (IP ustuni). «Сеть → Проверка порта» (F12) aloqani tekshiradi. RLS1000 va kassani bir vaqtda taroziga ulamang."),
            out _);
        panel.Children.Add(rls);
        return panel;
    }

    private void SaveConnection()
    {
        var prefs = UserPreferences.Instance;
        prefs.RongtaScaleIp = (_ipBox.Text ?? "").Trim();
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
        _checkButton.IsEnabled = false;
        ShowResult(L("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), false);
        try
        {
            // 2026-11-13: проверка «TCP-порт 5001» убрана — у весов это UDP-порт RLS1000 (разбор RLS1000), TCP-проверка его всегда «не открыт».
            var (pingOk, rtt) = await PingAsync(ip).ConfigureAwait(true);
            var arp = ScaleNetworkScanner.ReadArpTable()
                .FirstOrDefault(a => a.Address == ScaleNetworkScanner.ToNumber(System.Net.IPAddress.Parse(ip))).Mac;

            var lines = new List<string>
            {
                pingOk
                    ? L($"ping: весы отвечают ({rtt} мс)", $"ping: тараза жооп берет ({rtt} мс)", $"ping: the scale answers ({rtt} ms)", $"ping: tartı yanıt veriyor ({rtt} ms)", $"ping: tarozi javob beradi ({rtt} ms)")
                    : L("ping: нет ответа", "ping: жооп жок", "ping: no reply", "ping: yanıt yok", "ping: javob yo‘q"),
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

            var ok = pingOk || arp is not null || directCheck?.Replied == true;
            var summary = ok
                ? L("Устройство по этому адресу в сети. ", "Бул даректеги түзмөк тармакта. ", "A device at this address is on the network. ", "Bu adresteki cihaz ağda. ", "Bu manzildagi qurilma tarmoqda. ")
                : L("По этому адресу никого нет: проверьте IP на весах, кабель и что весы включены. ", "Бул даректе эч ким жок: таразадагы IP'ни, кабелди жана тараза күйүк экенин текшериңиз. ", "Nobody at this address: check the IP on the scale, the cable and that the scale is on. ", "Bu adreste kimse yok: tartıdaki IP'yi, kabloyu ve tartının açık olduğunu kontrol edin. ", "Bu manzilda hech kim yo‘q: tarozidagi IP, kabel va tarozi yoqilganini tekshiring. ");
            ShowResult(summary + string.Join(" · ", lines), !ok);
            PosLogger.Log($"Rongta: проверка связи {ip}: ping={pingOk}, tcp4001={directCheck?.Replied == true}, mac={arp ?? "-"}", "SCALES");
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
        // 2026-11-06 (жалоба «дальше 9 тысяч цена не отображается»): поле цены весов — 6 цифр, при 2 знаках предел 9999,99 сом. Число знаков
        // можно уменьшить — но ТОЛЬКО вместе с тем же параметром на весах («Price point»), иначе цены уедут в 10/100 раз.
        var priceDigitsBox = new ComboBox { MinWidth = 320 };
        for (var d = 0; d <= 3; d++)
            priceDigitsBox.Items.Add($"{d} — " + L("предел цены ", "баа чеги ", "price limit ", "fiyat sınırı ", "narx chegarasi ") + ScalePriceLimits.FormatMax(ScalePriceLimits.DahuaMaxUnits, d) + L(" сом", " сом", " som", " som", " so‘m"));
        priceDigitsBox.SelectedIndex = Math.Clamp(prefs.RongtaPricePoint, 0, 3);
        priceDigitsBox.SelectionChanged += (_, _) => { if (priceDigitsBox.SelectedIndex >= 0) { prefs.RongtaPricePoint = priceDigitsBox.SelectedIndex; prefs.SaveToDisk(); } };
        lanBody.Children.Add(Row(L("Знаков после запятой в цене на весах", "Таразадагы баада үтүрдөн кийинки белгилер", "Decimals in the price on the scale", "Tartıdaki fiyatta ondalık hane", "Tarozidagi narxda verguldan keyingi xonalar"), priceDigitsBox));
        lanBody.Children.Add(Text(L("Должно совпадать с параметром весов «Price point» (у вас 2). Касса его на весах не меняет: изменили здесь — измените и на весах (меню весов → системные параметры), иначе цены на этикетках будут в 10 или 100 раз неверны. Цена выше предела не загружается — касса назовёт такие товары.",
            "Таразадагы «Price point» параметри менен дал келиши керек (сизде 2). Касса аны таразада өзгөртпөйт: бул жерде өзгөрттүңүз — таразада да өзгөртүңүз (тараза менюсу → система параметрлери), болбосо этикеткалардагы баалар 10 же 100 эсе туура эмес болот. Чектен жогору баа жүктөлбөйт — касса мындай товарларды атайт.",
            "Must match the scale's “Price point” parameter (yours is 2). The till does not change it on the scale: if you change it here, change it on the scale too (scale menu → system parameters), otherwise label prices will be 10 or 100 times wrong. A price above the limit is not loaded — the till names such goods.",
            "Tartının «Price point» parametresiyle aynı olmalı (sizde 2). Kasa bunu tartıda değiştirmez: burada değiştirirseniz tartıda da değiştirin (tartı menüsü → sistem parametreleri), aksi halde etiket fiyatları 10 veya 100 kat yanlış olur. Sınırı aşan fiyat yüklenmez — kasa bu ürünleri belirtir.",
            "Tarozining «Price point» parametriga mos bo‘lishi kerak (sizda 2). Kassa uni tarozida o‘zgartirmaydi: bu yerda o‘zgartirsangiz, tarozida ham o‘zgartiring (tarozi menyusi → tizim parametrlari), aks holda yorliqlardagi narxlar 10 yoki 100 baravar xato bo‘ladi. Chegaradan yuqori narx yuklanmaydi — kassa bunday tovarlarni aytadi."), "hint"));
        lanBody.Children.Add(Text(L("Пример: «Шоколад молочный 90г» на весах будет «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ».",
            "Мисал: «Шоколад молочный 90г» таразада «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» болот.",
            "Example: «Шоколад молочный 90г» shows as «Шоколад молочнЫй 90г», «Колбаса вареная» as «Колбаса варенаЯ».",
            "Örnek: «Шоколад молочный 90г» tartıda «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» olur.",
            "Misol: «Шоколад молочный 90г» tarozida «Шоколад молочнЫй 90г», «Колбаса вареная» — «Колбаса варенаЯ» bo‘ladi."), "hint"));
        if (!useLan)
        {
            lanBody.Children.Add(ButtonRow(MakeButton(L("Выбрать этот способ", "Бул жолду тандоо", "Use this method", "Bu yöntemi seç", "Shu usulni tanlash"), false, (_, _) =>
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
        statusBody.Children.Add(ButtonRow(MakeButton(L("Открыть окно «Весы» (Rongta)", "«Таразалар» терезесин ачуу (Rongta)", "Open the “Scales” window (Rongta)", "«Tartı» penceresini aç (Rongta)", "«Tarozi» oynasini ochish (Rongta)"), false,
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

        // 2026-11-13: крупно — какой тип сейчас, пример этикетки дороже 1000 сом, предупреждение и кнопка исправления; что касса пишет в карточки.
        var now = Card(L("Тип штрихкода сейчас", "Штрих-коддун азыркы түрү", "Current barcode type", "Şu anki barkod türü", "Hozirgi shtrix-kod turi"), null, out var nowBody);
        _currentTypeText = Themed(this, new TextBlock { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "BrushText", Brushes.Black);
        nowBody.Children.Add(_currentTypeText);
        _currentTypeWarning = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        nowBody.Children.Add(_currentTypeWarning);
        // 2026-11-13: крупная рекомендация и кнопка «Исправить: тип 07» — для ЛЮБОГО типа, с которым касса не прочтёт этикетку или есть предел суммы (раньше — только для типов по сумме).
        _recommendText = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        nowBody.Children.Add(_recommendText);
        _fixTypeButton = MakeButton("", true, (_, _) => ChooseRecommendedType());
        nowBody.Children.Add(ButtonRow(_fixTypeButton));
        _writeTypeText = Text("", "hint");
        nowBody.Children.Add(_writeTypeText);
        Button? applyAllButton = null;
        applyAllButton = MakeButton(L("Записать тип во все товары на весах…", "Түрдү таразадагы бардык товарларга жазуу…", "Write the type into all goods on the scale…", "Türü tartıdaki tüm ürünlere yaz…", "Turni tarozidagi barcha tovarlarga yozish…"), false,
            async (_, _) => await ApplyTypeToAllAsync(applyAllButton!).ConfigureAwait(true));
        nowBody.Children.Add(ButtonRow(applyAllButton,
            MakeButton(L("Не записывать тип в карточки", "Түрдү карточкаларга жазбоо", "Do not write the type into cards", "Türü kartlara yazma", "Turni kartochkalarga yozmaslik"), false, (_, _) =>
            {
                UserPreferences.Instance.RongtaServerBarcodeType = null;
                UserPreferences.Instance.SaveToDisk();
                UpdateCurrentType();
            })));
        panel.Children.Add(now);

        var card = Card(L("Тип штрих-кода на весах", "Таразадагы штрих-коддун түрү", "Barcode type on the scale", "Tartıdaki barkod türü", "Tarozidagi shtrix-kod turi"),
            L("Тип (0–99) — как весы складывают цифры штрихкода (таблица Appendix II руководства RLS1000). Касса читает этикетки по этому типу и записывает его в весовые карточки при «Отправить на весы». У каждого типа ниже — пример и проверка.",
              "Түрү (0–99) — тараза штрих-коддун сандарын кантип түзөт (RLS1000 колдонмосунун Appendix II таблицасы). Касса этикеткаларды ушул түр боюнча окуйт жана «Таразага жөнөтүү» учурунда салмактуу карточкаларга жазат. Төмөндө ар бир түргө мисал жана текшерүү бар.",
              "The type (0–99) is how the scale builds the barcode digits (Appendix II of the RLS1000 manual). The till reads labels by this type and writes it into weighed cards on “Send to scale”. Each type below has an example and a check.",
              "Tür (0–99), tartının barkod hanelerini nasıl dizdiğidir (RLS1000 kılavuzu Appendix II). Kasa etiketleri bu türe göre okur ve «Tartıya gönder»de tartılı kartlara yazar. Aşağıda her tür için örnek ve kontrol var.",
              "Tur (0–99) — tarozi shtrix-kod raqamlarini qanday tuzishi (RLS1000 qo‘llanmasi, Appendix II jadvali). Kassa yorliqlarni shu tur bo‘yicha o‘qiydi va «Taroziga yuborish»da vaznli kartochkalarga yozadi. Quyida har bir tur uchun misol va tekshiruv bor."),
            out var body);

        var formatHint = Text(CompanyFormatText(), "hint");
        body.Children.Add(formatHint);
        // 2026-11-05: единица суммы (тыйын/сом) — на этой кассе, важнее сайта (хотфикс TM-30F: 76800 = 768,00 сом, а не 76 800).
        body.Children.Add(AmountUnitPicker(this, () => { formatHint.Text = CompanyFormatText(); UpdateSample(); }));

        _typeBox = new ComboBox { MinWidth = 360, MaxDropDownHeight = 420 };
        foreach (var t in _types)
            _typeBox.Items.Add($"{t.Type:00} — {t.Pattern}" + (t.Type == RongtaBarcodeFormat.RecommendedTypeNumber ? "   ★ " + L("рекомендуется", "сунушталат", "recommended", "önerilir", "tavsiya etiladi") : ""));
        // 2026-11-13: при открытии списка выбранный тип на виду (ComboBox виртуализирует строки); 07 — первая строка, 00–09 выше остальных.
        _typeBox.DropDownOpened += (_, _) => Dispatcher.UIThread.Post(() => { if (_typeBox.SelectedIndex >= 0) _typeBox.ScrollIntoView(_typeBox.SelectedIndex); }, DispatcherPriority.Loaded);
        // 2026-09-30: тип запоминается (раньше всегда открывался 07). По умолчанию 02 — так стоит на
        // весах владельца (этикетка 20 34567 00290 8: отдел 20, код 5 цифр, сумма 5 цифр).
        _typeBox.SelectedIndex = Math.Max(0, _types.FindIndex(t => t.Type == UserPreferences.Instance.RongtaBarcodeType));
        _typeBox.SelectionChanged += (_, _) =>
        {
            if (SelectedType is { } selected)
            {
                UserPreferences.Instance.RongtaBarcodeType = selected.Type;
                // 2026-11-13: выбранная структура уходит и в записи товара способа «свой сервер» (команда 0110, поле Barcode type) — до первого выбора там 05.
                UserPreferences.Instance.RongtaServerBarcodeType = selected.Type;
                UserPreferences.Instance.SaveToDisk();
            }
            UpdateSample();
        };
        body.Children.Add(Row(L("Тип штрих-кода", "Штрих-коддун түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"), _typeBox));

        _typeHint = Text("", "hint");
        body.Children.Add(_typeHint);
        // 2026-11-06 (жалоба «у Rongta не видит суммы больше 999»): потолок суммы выбранной структуры и предупреждение по каталогу.
        _ceilingText = Text("", "hint");
        body.Children.Add(_ceilingText);

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
        // 2026-11-13 (разбор RLS1000 на локальной ловушке): в «!0V» (порт 4001) поля типа нет — тип уходит «родной» командой весов «обновить PLU» (порт 5001, байт 42 карточки).
        body.Children.Add(Text(L("Отдел касса пишет в каждый товар. Тип штрихкода касса пишет в каждую весовую карточку отдельной командой весов (порт 5001, как программа RLS1000). Если весы её не примут — тип ставят на весах: [SETTING] (держать 2–3 с) → «barcode type: set all barcodes' type» → номер типа → [PRN/ENTER].",
            "Бөлүмдү касса ар бир товарга жазат. Штрих-коддун түрүн касса ар бир салмактуу карточкага таразанын өзүнчө буйругу менен жазат (5001 порт, RLS1000 программасындай). Тараза аны кабыл албаса — түрдү таразада коюшат: [SETTING] (2–3 с кармоо) → «barcode type: set all barcodes' type» → түрдүн номери → [PRN/ENTER].",
            "The till writes the department into every item. The barcode type is written into every weighed card with a separate scale command (port 5001, like RLS1000). If the scale does not accept it, set the type on the scale: [SETTING] (hold 2–3 s) → “barcode type: set all barcodes' type” → type number → [PRN/ENTER].",
            "Kasa reyonu her ürüne yazar. Barkod türünü her tartılı karta ayrı bir tartı komutuyla yazar (port 5001, RLS1000 gibi). Tartı kabul etmezse tür tartıda ayarlanır: [SETTING] (2–3 sn basılı) → «barcode type: set all barcodes' type» → tür numarası → [PRN/ENTER].",
            "Bo‘limni kassa har bir tovarga yozadi. Shtrix-kod turini kassa har bir vaznli kartochkaga tarozining alohida buyrug‘i bilan yozadi (5001 port, RLS1000 dasturi kabi). Tarozi uni qabul qilmasa — turni tarozida belgilashadi: [SETTING] (2–3 s bosib turish) → «barcode type: set all barcodes' type» → tur raqami → [PRN/ENTER]."), "hint"));

        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 9999999, Increment = 1, FormatString = "0", Value = 123, MinWidth = 140 };
        _sampleCode.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Код товара в штрих-коде", "Штрих-коддогу товар коду", "Item code in the barcode", "Barkoddaki ürün kodu", "Shtrix-koddagi tovar kodi"), _sampleCode));

        _sampleGrams = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0", Value = 392, MinWidth = 140 };
        _sampleGrams.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Масса для примера, г", "Мисал үчүн масса, г", "Sample weight, g", "Örnek ağırlık, g", "Namuna uchun massa, g"), _sampleGrams));

        _samplePrice = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0.00", Value = 250, MinWidth = 140 };
        _samplePrice.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Цена за кг, сом", "Кг баасы, сом", "Price per kg, som", "Kg fiyatı, som", "Kg narxi, so‘m"), _samplePrice));

        // 2026-10-22 (модалки, пакет 3): цвета окна весов — привязкой к теме (ScaleUi.Themed), а не разовым чтением.
        _sampleText = Themed(this, new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Segoe UI") }, TextBlock.ForegroundProperty, "BrushText", Brushes.Black);
        body.Children.Add(_sampleText);
        _verdictText = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_verdictText);
        body.Children.Add(ButtonRow(MakeButton(L("Подобрать под кассу", "Кассага ылайыктоо", "Match the till", "Kasaya uydur", "Kassaga moslash"), false, (_, _) => MatchToKassa()),
            MakeButton(L("Выбрать структуру без ограничения суммы", "Сумма чегисиз түзүлүштү тандоо", "Choose the structure without an amount limit", "Tutar sınırı olmayan yapıyı seç", "Summa chegarasiz tuzilmani tanlash"), false, (_, _) => ChooseUnlimitedStructure())));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Что проверить на весах", "Таразада эмнени текшерүү керек", "What to check on the scale", "Tartıda neyi kontrol etmeli", "Tarozida nimani tekshirish kerak"),
            // 2026-11-13: «касса передаёт Code = 0» устарело (Code = PLU с 28.09); добавлен безопасный порядок проверки нового типа.
            L("1) Сначала один товар: отправьте его на весы, на весах откройте карточку (держать [PLU] → номер PLU) — «Bar Code Type» должен быть таким, как здесь. 2) Положите товар дороже 1000 сом и напечатайте этикетку: штрихкод должен быть. Отсканируйте его кассой — сумма должна совпасть с этикеткой. 3) Для типов 00–09 первые две цифры — отдел: для кассы он должен быть 20–29. 4) Старые этикетки (по сумме) после смены типа не сканируйте — перепечатайте.",
              "1) Адегенде бир товар: аны таразага жөнөтүңүз, таразада карточкасын ачыңыз ([PLU] кармоо → PLU номери) — «Bar Code Type» ушул жердегидей болушу керек. 2) 1000 сомдон кымбат товарды коюп, этикетка басыңыз: штрих-код болушу керек. Аны касса менен сканерлеңиз — сумма этикеткадагыдай болушу керек. 3) 00–09 түрлөрүндө алгачкы эки сан — бөлүм: касса үчүн 20–29 болушу керек. 4) Түрдү алмаштыргандан кийин эски (сумма боюнча) этикеткаларды сканерлебеңиз — кайра басыңыз.",
              "1) Start with one item: send it to the scale, open its card on the scale (hold [PLU] → PLU number) — “Bar Code Type” must be the one shown here. 2) Put an item costing over 1000 som and print a label: the barcode must be there. Scan it at the till — the amount must match the label. 3) For types 00–09 the first two digits are the department: it must be 20–29 for the till. 4) Do not scan old (amount) labels after changing the type — reprint them.",
              "1) Önce tek ürün: tartıya gönderin, tartıda kartını açın ([PLU] basılı → PLU numarası) — «Bar Code Type» buradaki gibi olmalı. 2) 1000 somdan pahalı bir ürün koyup etiket basın: barkod olmalı. Kasada okutun — tutar etiketle aynı olmalı. 3) 00–09 türlerinde ilk iki hane reyondur: kasa için 20–29 olmalı. 4) Tür değiştikten sonra eski (tutarlı) etiketleri okutmayın — yeniden basın.",
              "1) Avval bitta tovar: uni taroziga yuboring, tarozida kartochkasini oching ([PLU] ni bosib turish → PLU raqami) — «Bar Code Type» shu yerdagidek bo‘lishi kerak. 2) 1000 so‘mdan qimmat tovarni qo‘yib, yorliq chop eting: shtrix-kod bo‘lishi kerak. Uni kassada skanerlang — summa yorliqdagidek bo‘lishi kerak. 3) 00–09 turlarida dastlabki ikki raqam — bo‘lim: kassa uchun 20–29 bo‘lishi kerak. 4) Tur almashgandan keyin eski (summali) yorliqlarni skanerlamang — qayta chop eting."),
            out _));

        UpdateSample();
        return panel;
    }

    private RongtaBarcodeFormat.BarcodeType? SelectedType =>
        _typeBox.SelectedIndex >= 0 && _typeBox.SelectedIndex < _types.Count ? _types[_typeBox.SelectedIndex] : null;

    private void UpdateSample()
    {
        UpdateSampleCore();
        UpdateCurrentType(); // 2026-11-13: после сверки с кассой — кнопке «Исправить» нужен её итог (_verdictOk)
    }

    private void UpdateSampleCore()
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

        // 2026-11-06: потолок суммы структуры и предупреждение, если в каталоге есть товары дороже.
        // 2026-11-12: единица суммы в штрихкоде — по «Price point» весов (настройка на вкладке загрузки).
        var pricePoint = Math.Clamp(UserPreferences.Instance.RongtaPricePoint, 0, 3);
        var report = RongtaBarcodeFormat.CheckCatalog(type, CatalogCacheService.Products.Where(p => p.IsWeighted).Select(p => LocalCartService.ParsePrice(p.PriceLine)), pricePoint);
        _ceilingText.Text = (RongtaBarcodeFormat.CeilingText(type, pricePoint) + " " + RongtaBarcodeFormat.CatalogWarning(report)).Trim();
        Themed(this, _ceilingText, TextBlock.ForegroundProperty, report.HasProblem ? "BrushWarning" : "BrushTextSoft", report.HasProblem ? Brushes.DarkOrange : Brushes.Gray);

        var code = (long)(_sampleCode.Value ?? 1);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var amount = Math.Round((decimal)(_samplePrice.Value ?? 250) * grams / 1000m, 2);
        var sample = RongtaBarcodeFormat.BuildSample(type, (int)(_department.Value ?? 20), code, grams, amount, pricePoint);
        _sampleText.Text = sample ?? "—";

        if (!RongtaBarcodeFormat.AmountFits(type, amount, pricePoint))
        {
            // 2026-11-12: сумма примера не помещается в поле — весы напечатают этикетку без штрихкода (не обрезанную сумму).
            SetVerdict(false, RongtaBarcodeFormat.CeilingText(type, pricePoint));
            return;
        }
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
        _verdictOk = ok;
        _verdictText.Text = (ok ? "✓ " : "⚠ " + L("Касса НЕ узнает товар: ", "Касса товарды ТААНЫБАЙТ: ", "The till will NOT recognise the item: ", "Kasa ürünü TANIMAZ: ", "Kassa tovarni TANIMAYDI: ")) + message;
        Themed(this, _verdictText, TextBlock.ForegroundProperty, ok ? "BrushSuccess" : "BrushWarning", ok ? Brushes.Green : Brushes.DarkOrange);
    }

    /// <summary>2026-11-13: верхний блок вкладки «Штрих-код» — тип крупно, пример этикетки «1,150 кг × 1100 = 1265,00 сом» (случай владельца),
    /// предупреждение для типов по сумме с кнопкой исправления и что касса пишет в карточки.</summary>
    private void UpdateCurrentType()
    {
        if (_currentTypeText is null || SelectedType is not { } type)
            return;
        var pricePoint = Math.Clamp(UserPreferences.Instance.RongtaPricePoint, 0, 3);
        var kind = type.Value switch
        {
            RongtaBarcodeFormat.ValueKind.Price => L("по сумме", "сумма боюнча", "by amount", "tutara göre", "summa bo‘yicha"),
            RongtaBarcodeFormat.ValueKind.Weight => L("по весу", "салмак боюнча", "by weight", "ağırlığa göre", "vazn bo‘yicha"),
            _ => L("без веса и суммы", "салмаксыз жана суммасыз", "no weight or amount", "ağırlık ve tutar yok", "vazn va summasiz"),
        };
        const decimal sampleAmount = 1265.00m;
        var sample = RongtaBarcodeFormat.BuildSample(type, (int)(_department?.Value ?? 20), 5, 1150, sampleAmount, pricePoint);
        _currentTypeText.Text = L($"Тип {type.Type:00} — {kind} ({type.Pattern})", $"{type.Type:00} түрү — {kind} ({type.Pattern})", $"Type {type.Type:00} — {kind} ({type.Pattern})", $"Tür {type.Type:00} — {kind} ({type.Pattern})", $"{type.Type:00} turi — {kind} ({type.Pattern})")
            + "\n" + L("Пример: 1,150 кг × 1100 сом = 1265,00 сом → ", "Мисал: 1,150 кг × 1100 сом = 1265,00 сом → ", "Example: 1.150 kg × 1100 som = 1265.00 som → ", "Örnek: 1,150 kg × 1100 som = 1265,00 som → ", "Misol: 1,150 kg × 1100 so‘m = 1265,00 so‘m → ")
            + (sample ?? L("штрихкода НЕТ", "штрих-код ЖОК", "NO barcode", "barkod YOK", "shtrix-kod YO‘Q"));

        var amountLimited = type.Value == RongtaBarcodeFormat.ValueKind.Price && !RongtaBarcodeFormat.AmountFits(type, sampleAmount, pricePoint);
        _currentTypeWarning.Text = amountLimited
            ? "⚠ " + RongtaBarcodeFormat.CeilingText(type, pricePoint)
            : type.Value == RongtaBarcodeFormat.ValueKind.Weight
                ? "✓ " + L("Предела суммы нет — штрихкод печатается при любой сумме.", "Сумма чеги жок — штрих-код каалаган суммада басылат.", "No amount limit — the barcode is printed at any amount.", "Tutar sınırı yok — barkod her tutarda basılır.", "Summa chegarasi yo‘q — shtrix-kod har qanday summada chop etiladi.")
                : "";
        Themed(this, _currentTypeWarning, TextBlock.ForegroundProperty, amountLimited ? "BrushWarning" : "BrushSuccess", amountLimited ? Brushes.DarkOrange : Brushes.Green);
        // 2026-11-13: кнопка — для любого типа, кроме 07, если в нём предел суммы / нет веса или касса не прочтёт пример этикетки (тип 27 с «00…» и т. п.).
        _fixTypeButton.IsVisible = RongtaBarcodeFormat.NeedsRecommendedFix(type, _verdictOk);
        _fixTypeButton.Content = L("Исправить: тип 07 (рекомендуется)", "Оңдоо: 07 түрү (сунушталат)", "Fix: type 07 (recommended)", "Düzelt: tür 07 (önerilir)", "Tuzatish: 07 turi (tavsiya etiladi)");
        var isRecommended = type.Type == RongtaBarcodeFormat.RecommendedTypeNumber;
        _recommendText.Text = (isRecommended ? "✓ " : "★ ") + L(
            "Для Rongta рекомендуем тип 07 — проверено на весах RLS1000: штрихкод печатается при любой сумме (вес в граммах, префикс 20).",
            "Rongta үчүн 07 түрүн сунуштайбыз — RLS1000 таразасында текшерилген: штрих-код каалаган суммада басылат (салмак граммда, префикс 20).",
            "For Rongta we recommend type 07 — verified on an RLS1000 scale: the barcode is printed at any amount (weight in grams, prefix 20).",
            "Rongta için 07 türünü öneriyoruz — RLS1000 tartıda doğrulandı: barkod her tutarda basılır (ağırlık gram, önek 20).",
            "Rongta uchun 07 turini tavsiya qilamiz — RLS1000 tarozisida tekshirilgan: shtrix-kod har qanday summada chop etiladi (vazn grammda, prefiks 20).");
        Themed(this, _recommendText, TextBlock.ForegroundProperty, isRecommended ? "BrushSuccess" : "BrushText", isRecommended ? Brushes.Green : Brushes.Black);

        _writeTypeText.Text = RongtaBarcodeTypeWriter.ChosenType is int t
            ? L($"Касса записывает тип {t:00} в каждую весовую карточку при «Отправить на весы» (порт 5001).", $"Касса {t:00} түрүн «Таразага жөнөтүү» учурунда ар бир салмактуу карточкага жазат (5001 порт).", $"The till writes type {t:00} into every weighed card on “Send to scale” (port 5001).", $"Kasa {t:00} türünü «Tartıya gönder»de her tartılı karta yazar (port 5001).", $"Kassa {t:00} turini «Taroziga yuborish»da har bir vaznli kartochkaga yozadi (5001 port).")
            : L("Касса тип в карточки не записывает — на весах остаётся их тип. Выберите тип в списке ниже, чтобы касса его записывала.", "Касса түрдү карточкаларга жазбайт — таразада өз түрү калат. Касса жазышы үчүн төмөнкү тизмеден түрдү тандаңыз.", "The till does not write the type into cards — the scale keeps its own. Choose a type in the list below for the till to write it.", "Kasa türü kartlara yazmaz — tartıda kendi türü kalır. Kasanın yazması için aşağıdaki listeden bir tür seçin.", "Kassa turni kartochkalarga yozmaydi — tarozida o‘z turi qoladi. Kassa yozishi uchun quyidagi ro‘yxatdan tur tanlang.");
    }

    /// <summary>2026-11-13: «Записать тип во все товары на весах» — весовые товары, которые касса уже отправляла на эти весы, с подтверждением.</summary>
    private async Task ApplyTypeToAllAsync(Button button)
    {
        SaveConnection();
        if (RongtaBarcodeTypeWriter.ChosenType is not int type)
        {
            ShowResult(L("Сначала выберите тип штрихкода в списке.", "Адегенде тизмеден штрих-коддун түрүн тандаңыз.", "Choose a barcode type in the list first.", "Önce listeden bir barkod türü seçin.", "Avval ro‘yxatdan shtrix-kod turini tanlang."), true);
            return;
        }
        var records = RongtaBarcodeTypeWriter.FromSentGoods();
        if (records.Count == 0)
        {
            ShowResult(L("Касса ещё не отправляла весовые товары на эти весы. Откройте окно «Весы» и нажмите «Отправить на весы» — тип уйдёт вместе с товарами.", "Касса бул таразага салмактуу товар жөнөтө элек. «Таразалар» терезесин ачып «Таразага жөнөтүү» басыңыз — түр товарлар менен кетет.", "The till has not sent weighed goods to this scale yet. Open the “Scales” window and press “Send to scale” — the type goes with the goods.", "Kasa bu tartıya henüz tartılı ürün göndermedi. «Tartı» penceresini açıp «Tartıya gönder»e basın — tür ürünlerle gider.", "Kassa bu taroziga hali vaznli tovar yubormagan. «Tarozi» oynasini ochib «Taroziga yuborish»ni bosing — tur tovarlar bilan ketadi."), true);
            return;
        }
        var ip = (_ipBox.Text ?? "").Trim();
        var question = L($"Записать тип {type:00} в весовые товары на весах {ip}: {records.Count} шт.? Название и цена в этих карточках обновятся из каталога. Советуем сначала проверить на одном товаре (окно «Весы»).",
            $"{ip} таразасындагы салмактуу товарларга {type:00} түрүн жазабызбы: {records.Count} даана? Бул карточкалардагы аталыш жана баа каталогдон жаңыланат. Адегенде бир товардан текшерүүнү сунуштайбыз («Таразалар» терезеси).",
            $"Write type {type:00} into the weighed goods on scale {ip}: {records.Count} items? Names and prices in these cards are refreshed from the catalog. We advise checking one item first (the “Scales” window).",
            $"{ip} tartısındaki tartılı ürünlere {type:00} türü yazılsın mı: {records.Count} adet? Bu kartlardaki ad ve fiyat katalogdan güncellenir. Önce tek üründe denemenizi öneririz («Tartı» penceresi).",
            $"{ip} tarozisidagi vaznli tovarlarga {type:00} turi yozilsinmi: {records.Count} ta? Bu kartochkalardagi nom va narx katalogdan yangilanadi. Avval bitta tovarda tekshirishni maslahat beramiz («Tarozi» oynasi).");
        if (!await NurMarketKassa.AvaloniaHost.Services.PosDialogs.ConfirmYesNoModalAsync(this, question,
                L("Тип штрихкода на весах", "Таразадагы штрих-коддун түрү", "Barcode type on the scale", "Tartıdaki barkod türü", "Tarozidagi shtrix-kod turi"),
                L("Записать", "Жазуу", "Write", "Yaz", "Yozish")).ConfigureAwait(true))
            return;
        button.IsEnabled = false;
        ShowResult(L("Запись на весы…", "Таразага жазылууда…", "Writing to the scale…", "Tartıya yazılıyor…", "Taroziga yozilmoqda…"), false);
        try
        {
            var (ok, note) = await RongtaBarcodeTypeWriter.WriteAsync(ip, records, Math.Clamp(UserPreferences.Instance.RongtaPricePoint, 0, 3), type, CancellationToken.None).ConfigureAwait(true);
            ShowResult(note + (ok
                ? L(" Положите товар дороже 1000 сом и напечатайте этикетку — штрихкод должен быть.", " 1000 сомдон кымбат товарды коюп этикетка басыңыз — штрих-код болушу керек.", " Put an item over 1000 som and print a label — the barcode must be there.", " 1000 somdan pahalı bir ürün koyup etiket basın — barkod olmalı.", " 1000 so‘mdan qimmat tovarni qo‘yib yorliq chop eting — shtrix-kod bo‘lishi kerak.")
                : " " + RongtaBarcodeTypeWriter.NoReplyHint(type)), !ok); // 2026-11-13: запасной путь на весах и журнал обмена — одним текстом
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // =====================================================================================
    // Дата и параметры
    // =====================================================================================

    /// <summary>2026-11-13: вкладка «Дата и параметры» — дата/время ПК на весы (пакет «подключиться», как RLS1000) и что из системных параметров ставить на весах.</summary>
    private Control BuildDateTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var date = Card(L("Дата и время на весах", "Таразадагы дата жана убакыт", "Date and time on the scale", "Tartıdaki tarih ve saat", "Tarozidagi sana va vaqt"),
            L("Дату упаковки на этикетке весы берут из своих часов. Кнопка ниже передаёт весам дату и время этого компьютера — так же, как программа RLS1000 при каждом подключении (порт 5001). Товары и настройки весов не меняются.",
              "Этикеткадагы таңгактоо датасын тараза өз саатынан алат. Төмөнкү баскыч таразага ушул компьютердин датасын жана убактысын берет — RLS1000 программасы ар бир туташууда кылгандай (5001 порт). Товарлар жана тараза жөндөөлөрү өзгөрбөйт.",
              "The scale takes the packing date on the label from its own clock. The button below gives the scale this computer's date and time — the way RLS1000 does on every connection (port 5001). Goods and scale settings are not changed.",
              "Tartı etiketteki paketleme tarihini kendi saatinden alır. Aşağıdaki düğme tartıya bu bilgisayarın tarih ve saatini verir — RLS1000'in her bağlantıda yaptığı gibi (port 5001). Ürünler ve tartı ayarları değişmez.",
              "Yorliqdagi qadoqlash sanasini tarozi o‘z soatidan oladi. Quyidagi tugma taroziga shu kompyuterning sana va vaqtini beradi — RLS1000 dasturi har ulanishda qilganidek (5001 port). Tovarlar va tarozi sozlamalari o‘zgarmaydi."),
            out var dateBody);
        Button? clockButton = null;
        clockButton = MakeButton(L("Записать дату и время на весы", "Датаны жана убакытты таразага жазуу", "Write date and time to the scale", "Tarihi ve saati tartıya yaz", "Sana va vaqtni taroziga yozish"), true,
            async (_, _) => await SyncClockAsync(clockButton!).ConfigureAwait(true));
        dateBody.Children.Add(ButtonRow(clockButton));
        dateBody.Children.Add(Text(L("Весы ставят часы по ПК, если у них включено «автоматически обновлять время» (так с завода). Если часы не изменились — поставьте вручную: [SETTING] → «T date adjusting». Дата снова сбивается после выключения — батарейка часов.",
            "Таразада «автоматически обновлять время» күйүк болсо (заводдон ушундай), саатты ПК боюнча коёт. Саат өзгөрбөсө — кол менен коюңуз: [SETTING] → «T date adjusting». Өчүргөндөн кийин дата кайра бузулса — сааттын батарейкасы.",
            "The scale sets its clock from the PC if “auto update time” is on (factory default). If the clock did not change, set it by hand: [SETTING] → “T date adjusting”. If the date resets after power-off — the clock battery.",
            "Tartıda «otomatik saat güncelle» açıksa (fabrika ayarı) saati PC'ye göre kurar. Saat değişmediyse elle kurun: [SETTING] → «T date adjusting». Kapatınca tarih yine bozuluyorsa — saat pili.",
            "Tarozida «avtomatik vaqtni yangilash» yoqilgan bo‘lsa (zavoddan shunday), soatni kompyuter bo‘yicha qo‘yadi. Soat o‘zgarmasa — qo‘lda qo‘ying: [SETTING] → «T date adjusting». O‘chirgandan keyin sana yana buzilsa — soat batareyasi."), "hint"));
        panel.Children.Add(date);

        panel.Children.Add(Card(L("Системные параметры весов", "Таразанын система параметрлери", "Scale system settings", "Tartı sistem ayarları", "Tarozi tizim parametrlari"),
            L("Знаки цены, формат даты, тип штрихкода «по умолчанию» и «для всех» программа RLS1000 пишет одним блоком, который переписывает ВСЕ настройки весов. Касса этот блок не отправляет, чтобы не сбить ваши весы. Ставьте на весах ([SETTING], держать 2–3 с): «J decimal digits in unit price» = 2 и «X total price precision» = 2 (как «Знаков после запятой» на вкладке «Загрузка товаров»), «set default barcode type» — тип для новых товаров. Не трогайте «ВЫБОР ФОРМАТА ЭТИКЕТКИ» (D0/D1) и «Recover factory system para».",
              "Баанын белгилерин, дата форматын, «демейки» жана «баары үчүн» штрих-код түрүн RLS1000 программасы бир блок менен жазат, ал таразанын БАРДЫК жөндөөлөрүн кайра жазат. Касса тараза бузулбашы үчүн бул блокту жөнөтпөйт. Таразада коюңуз ([SETTING], 2–3 с кармоо): «J decimal digits in unit price» = 2 жана «X total price precision» = 2 («Товарларды жүктөө» өтмөгүндөгү «Үтүрдөн кийинки белгилер» сыяктуу), «set default barcode type» — жаңы товарлар үчүн түр. «ВЫБОР ФОРМАТА ЭТИКЕТКИ» (D0/D1) жана «Recover factory system para» тийбеңиз.",
              "Price decimals, date format and the “default” and “all items” barcode types are written by RLS1000 as one block that overwrites ALL scale settings. The till does not send that block so as not to upset your scale. Set on the scale ([SETTING], hold 2–3 s): “J decimal digits in unit price” = 2 and “X total price precision” = 2 (same as “Decimals” on the “Uploading goods” tab), “set default barcode type” — type for new items. Do not touch “LABEL FORMAT” (D0/D1) or “Recover factory system para”.",
              "Fiyat ondalığı, tarih biçimi, «varsayılan» ve «tümü için» barkod türünü RLS1000 tek blokla yazar; bu blok tartının TÜM ayarlarının üzerine yazar. Kasa tartınızı bozmamak için bu bloğu göndermez. Tartıda ayarlayın ([SETTING], 2–3 sn basılı): «J decimal digits in unit price» = 2 ve «X total price precision» = 2 («Ürün yükleme» sekmesindeki «Ondalık hane» gibi), «set default barcode type» — yeni ürünler için tür. «ETİKET BİÇİMİ» (D0/D1) ve «Recover factory system para»ya dokunmayın.",
              "Narx xonalari, sana formati, «standart» va «hammasi uchun» shtrix-kod turini RLS1000 dasturi bitta blok bilan yozadi, u tarozining BARCHA sozlamalarini qayta yozadi. Kassa tarozingizni buzmaslik uchun bu blokni yubormaydi. Tarozida belgilang ([SETTING], 2–3 s bosib turish): «J decimal digits in unit price» = 2 va «X total price precision» = 2 («Tovarlarni yuklash» bo‘limidagi «Verguldan keyingi xonalar» kabi), «set default barcode type» — yangi tovarlar uchun tur. «ВЫБОР ФОРМАТА ЭТИКЕТКИ» (D0/D1) va «Recover factory system para» ga tegmang."),
            out _));
        return panel;
    }

    private async Task SyncClockAsync(Button button)
    {
        SaveConnection();
        var ip = (_ipBox.Text ?? "").Trim();
        var scale = RongtaUdpScaleService.TryCreate(ip);
        if (scale is null)
        {
            ShowResult(L("Введите IP-адрес весов на вкладке «Подключение».", "«Туташуу» өтмөгүндө таразанын IP-дарегин киргизиңиз.", "Enter the scale IP address on the “Connection” tab.", "«Bağlantı» sekmesinde tartı IP adresini girin.", "«Ulanish» bo‘limida tarozi IP manzilini kiriting."), true);
            return;
        }
        button.IsEnabled = false;
        ShowResult(L("Связь с весами…", "Тараза менен байланыш…", "Contacting the scale…", "Tartıya bağlanılıyor…", "Tarozi bilan aloqa…"), false);
        try
        {
            var now = DateTime.Now;
            var result = await scale.ConnectOnlyAsync(now, CancellationToken.None).ConfigureAwait(true);
            PosLogger.Log($"Rongta: дата и время {now:dd.MM.yyyy HH:mm:ss} → {scale.Host}:{scale.Port} (UDP): {result.Error} {result.Detail}", "SCALES");
            var stamp = now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            ShowResult(result.Ok
                ? L($"Весы ответили; передано {stamp}. Проверьте часы на весах.", $"Тараза жооп берди; {stamp} берилди. Таразадагы саатты текшериңиз.", $"The scale answered; {stamp} sent. Check the clock on the scale.", $"Tartı yanıt verdi; {stamp} gönderildi. Tartıdaki saati kontrol edin.", $"Tarozi javob berdi; {stamp} yuborildi. Tarozidagi soatni tekshiring.")
                : L($"Весы не ответили ({RongtaBarcodeTypeWriter.ErrorText(result.Error)}). Дату можно поставить на весах: [SETTING] → «T date adjusting».", $"Тараза жооп берген жок ({RongtaBarcodeTypeWriter.ErrorText(result.Error)}). Датаны таразада коюуга болот: [SETTING] → «T date adjusting».", $"The scale did not answer ({RongtaBarcodeTypeWriter.ErrorText(result.Error)}). The date can be set on the scale: [SETTING] → “T date adjusting”.", $"Tartı yanıt vermedi ({RongtaBarcodeTypeWriter.ErrorText(result.Error)}). Tarih tartıda ayarlanabilir: [SETTING] → «T date adjusting».", $"Tarozi javob bermadi ({RongtaBarcodeTypeWriter.ErrorText(result.Error)}). Sanani tarozida qo‘yish mumkin: [SETTING] → «T date adjusting»."), !result.Ok);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>2026-11-06 (жалоба «у Rongta не видит суммы больше 999»): одной кнопкой — структура «по весу» с теми же префиксом и числом цифр кода (02 → 07): вес не имеет предела суммы.
    /// Касса сама начинает читать префикс как ВЕС (правило префикса снимается с «сумма»). Тип на самих весах касса не меняет — прямой протокол записи этого поля не описан: нужно выставить то же значение
    /// на весах ([SETTING] → «barcode type: set all barcodes' type» — 2026-11-13: default действует только на новые карточки) и в RLS1000 (Barcode Type у товаров).</summary>
    private void ChooseUnlimitedStructure()
    {
        var current = SelectedType;
        SelectStructure(current is null ? null : RongtaBarcodeFormat.RecommendedWeightType(current));
    }

    /// <summary>2026-11-13: кнопка «Исправить: тип 07 (рекомендуется)» — структура 07 (проверена вживую на RLS1000) для любого текущего типа; отдел вне 20–29 заменяется на 20.</summary>
    private void ChooseRecommendedType()
    {
        var department = (int)(_department.Value ?? 20);
        if (department is < 20 or > 29)
            _department.Value = 20; // ValueChanged запоминает отдел
        SelectStructure(RongtaBarcodeFormat.RecommendedType);
    }

    private void SelectStructure(RongtaBarcodeFormat.BarcodeType? recommended)
    {
        var current = SelectedType;
        if (current is null || recommended is null)
        {
            ShowResult(L("Подходящая структура по весу не найдена — выберите её в списке.", "Салмак боюнча ылайыктуу түзүлүш табылган жок — тизмеден тандаңыз.", "No suitable by-weight structure found — choose one in the list.", "Uygun ağırlık yapısı bulunamadı — listeden seçin.", "Mos vazn tuzilmasi topilmadi — ro‘yxatdan tanlang."), true);
            return;
        }
        var department = (int)(_department.Value ?? 20);
        var prefix = recommended.Prefix == RongtaBarcodeFormat.PrefixKind.Fixed2 ? recommended.FixedPrefix : department;
        _typeBox.SelectedIndex = _types.IndexOf(recommended); // SelectionChanged запоминает тип и обновляет пример
        if (prefix is >= 20 and <= 29)
        {
            var text = prefix.ToString("00", CultureInfo.InvariantCulture);
            NurMarketKassa.AvaloniaHost.Services.ScaleBarcodeRules.ApplyAndSave(text, NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Weight);
            ScaleLabelCodeRegistry.UnpublishAmountPrefix(text);
        }
        var kg = RongtaBarcodeFormat.MaxWeightKg(recommended).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
        // 2026-11-13: тип теперь уходит в карточки весов «родной» командой (UDP 5001) при «Отправить на весы» / «Записать тип во все товары».
        ShowResult(L($"Выбрана структура {recommended.Type:00} ({recommended.Pattern}): вес до {kg} кг, предела суммы нет; касса читает префикс как вес. Касса запишет тип {recommended.Type:00} в весовые карточки при «Отправить на весы» (или кнопкой «Записать тип во все товары на весах»). Проверьте сначала один товар: этикетка дороже 1000 сом должна выйти со штрихкодом.",
            $"{recommended.Type:00} түзүлүшү тандалды ({recommended.Pattern}): салмак {kg} кг га чейин, сумма чеги жок; касса префиксти салмак катары окуйт. Касса {recommended.Type:00} түрүн «Таразага жөнөтүү» учурунда (же «Түрдү таразадагы бардык товарларга жазуу» баскычы менен) салмактуу карточкаларга жазат. Адегенде бир товарды текшериңиз: 1000 сомдон кымбат этикетка штрих-код менен чыгышы керек.",
            $"Structure {recommended.Type:00} ({recommended.Pattern}) chosen: weight up to {kg} kg, no amount limit; the till reads the prefix as weight. The till writes type {recommended.Type:00} into weighed cards on “Send to scale” (or with “Write the type into all goods on the scale”). Check one item first: a label over 1000 som must come out with a barcode.",
            $"{recommended.Type:00} yapısı seçildi ({recommended.Pattern}): {kg} kg'a kadar ağırlık, tutar sınırı yok; kasa öneki ağırlık olarak okur. Kasa {recommended.Type:00} türünü «Tartıya gönder»de (veya «Türü tartıdaki tüm ürünlere yaz» ile) tartılı kartlara yazar. Önce tek ürünü kontrol edin: 1000 somdan pahalı etiket barkodlu çıkmalı.",
            $"{recommended.Type:00} tuzilmasi tanlandi ({recommended.Pattern}): vazn {kg} kg gacha, summa chegarasi yo‘q; kassa prefiksni vazn deb o‘qiydi. Kassa {recommended.Type:00} turini «Taroziga yuborish»da (yoki «Turni tarozidagi barcha tovarlarga yozish» tugmasi bilan) vaznli kartochkalarga yozadi. Avval bitta tovarni tekshiring: 1000 so‘mdan qimmat yorliq shtrix-kod bilan chiqishi kerak."), false);
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
                var sample = RongtaBarcodeFormat.BuildSample(type, dept, code, grams, amount, Math.Clamp(UserPreferences.Instance.RongtaPricePoint, 0, 3)); // 2026-11-12: единица суммы по Price point
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
        Themed(this, ResultBorder, Border.BorderBrushProperty, isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        // 2026-10-22 (модалки, пакет 3): BrushSurfaceSubtle в теме нет — был запасной цвет (в тёмной теме не тот); мягкий фон темы.
        // Цвета — привязкой к теме (ScaleUi.Themed): меняются вместе с темой.
        Themed(this, ResultBorder, Border.BackgroundProperty, isError ? "BrushWarningSoft" : "BrushPanelSoft", Brushes.Transparent);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        SaveConnection();
        Close();
    }
}
