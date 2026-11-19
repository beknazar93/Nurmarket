using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-09-28: «Весы с печатью этикеток» — марка плитками и поля только выбранной марки
/// (см. разметку). Адрес каждой марки хранится в своём поле настроек: Штрих-ПРИНТ —
/// ScaleNetworkIp/ScaleLanPort/ScaleLanPassword, Rongta — RongtaScaleIp/RongtaScalePort,
/// TM-30F — TmScaleIp/TmScalePort. Поля сохраняются при уходе с поля и перед каждой кнопкой.</summary>
public partial class LabelScaleSetupPanel : UserControl
{
    /// <summary>Панель в отдельном окне (из окна «Весы»): без кнопки «Отправить товары».</summary>
    public bool IsDialogMode { get; set; }

    /// <summary>Марку или адрес поменяли — окно «Весы» обновляет свою шапку.</summary>
    public event Action? SetupChanged;

    private string _brand = ScaleUi.BrandShtrikh;
    private bool _loading;

    // 2026-11-06: весы «Штрих РС-200» — плитка, поля папки обмена и кнопка инструкции создаются кодом (разметку панели не трогаем).
    private RadioButton _brandPcTile = null!;
    private ShtrikhPcFieldsView _pcFields = null!;
    private Button _pcGuideButton = null!;

    // 2026-11-13: весы Масса-К (серии R/SL) — плитка и поля адреса создаются кодом, как у РС-200 (разметку панели не трогаем).
    private RadioButton _brandMassaKTile = null!;
    private StackPanel _massaKFields = null!;
    private TextBox _massaKIpBox = null!;
    private TextBox _massaKPortBox = null!;

    // 2026-11-13: весы CAS (CL5000J/CL3000 по сети, остальные CL — файл для CL-Works) — плитка и поле адреса создаются кодом; модель, порт и остальное — в окне «Настройки весов CAS».
    private RadioButton _brandCasTile = null!;
    private StackPanel _casFields = null!;
    private TextBox _casIpBox = null!;

    /// <summary>Поля хоть раз заполнены из настроек. До этого SaveFields ничего не пишет — иначе
    /// пустые поля ещё не открытой панели затёрли бы сохранённые адреса.</summary>
    private bool _hasLoaded;

    public LabelScaleSetupPanel()
    {
        InitializeComponent();
        AddShtrikhPcParts();
        AddMassaKParts();
        AddCasParts();
    }

    /// <summary>2026-11-13: плитка «CAS» и поле IP. Модель (сеть или файл для CL-Works), порт 20304, префикс и знаки цены — «Настройки весов…» → окно CAS. НЕ ПРОВЕРЕНО на весах.</summary>
    private void AddCasParts()
    {
        _brandCasTile = new RadioButton { Classes = { "BrandTile" }, GroupName = "LabelScaleBrand" };
        _brandCasTile.Click += BrandTile_Click;
        var tileGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        tileGrid.Children.Add(new Border { Classes = { "tileBadge" }, Child = new TextBlock { Classes = { "tileBadgeText" }, Text = "CAS" } });
        var tileTexts = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 2 };
        tileTexts.Children.Add(new TextBlock { Classes = { "tileTitle" }, Text = "CAS" });
        tileTexts.Children.Add(new TextBlock { Classes = { "tileSub" }, Text = L("CL5000J, CL3000: по сети; CL5500, CL5200, CL7200, LP-1.6: файл для CL-Works", "CL5000J, CL3000: тармак аркылуу; CL5500, CL5200, CL7200, LP-1.6: CL-Works үчүн файл", "CL5000J, CL3000: over the network; CL5500, CL5200, CL7200, LP-1.6: file for CL-Works", "CL5000J, CL3000: ağ üzerinden; CL5500, CL5200, CL7200, LP-1.6: CL-Works için dosya", "CL5000J, CL3000: tarmoq orqali; CL5500, CL5200, CL7200, LP-1.6: CL-Works uchun fayl") });
        Grid.SetColumn(tileTexts, 1);
        tileGrid.Children.Add(tileTexts);
        var tick = new TextBlock { Classes = { "tileCheck" }, Text = "✓" };
        Grid.SetColumn(tick, 2);
        tileGrid.Children.Add(tick);
        _brandCasTile.Content = tileGrid;
        if (BrandShtrikhTile.Parent is Panel tiles)
            tiles.Children.Add(_brandCasTile);

        _casIpBox = new TextBox { Classes = { "ModernTextBox" }, Height = 36, Watermark = "192.168.1.80" };
        _casIpBox.TextChanged += IpBox_TextChanged;
        _casIpBox.LostFocus += Field_LostFocus;
        _casFields = new StackPanel { Spacing = 10 };
        var ipColumn = new StackPanel();
        ipColumn.Children.Add(new TextBlock { Classes = { "FieldLabel" }, Text = L("IP-адрес весов (для моделей по сети)", "Таразанын IP-дареги (тармак аркылуу моделдер үчүн)", "Scale IP address (for network models)", "Tartı IP adresi (ağ modelleri için)", "Tarozi IP manzili (tarmoq modellari uchun)") });
        ipColumn.Children.Add(_casIpBox);
        _casFields.Children.Add(ipColumn);
        _casFields.Children.Add(new TextBlock
        {
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Text = L("Модель, порт, префикс штрихкода и знаки цены — «Настройки весов…» → «Быстрая настройка». CL5000J и CL3000 касса заполняет по сети (по документу CAS, на весах пока не проверено); для CL5500, CL5200, CL7200 и LP-1.6 она готовит файл для CL-Works.",
                "Модель, порт, штрих-код префикси жана баанын белгилери — «Тараза жөндөөлөрү…» → «Ыкчам жөндөө». CL5000J жана CL3000 таразаларын касса тармак аркылуу толтурат (CAS документи боюнча, таразада азырынча текшерилген эмес); CL5500, CL5200, CL7200 жана LP-1.6 үчүн CL-Works'ко файл даярдайт.",
                "Model, port, barcode prefix and price decimals — “Scale settings…” → “Quick setup”. The till fills the CL5000J and CL3000 over the network (per the CAS document, not verified on a scale yet); for CL5500, CL5200, CL7200 and LP-1.6 it prepares a file for CL-Works.",
                "Model, port, barkod öneki ve fiyat ondalığı — «Tartı ayarları…» → «Hızlı kurulum». CL5000J ve CL3000'i kasa ağ üzerinden doldurur (CAS belgesine göre, tartıda henüz doğrulanmadı); CL5500, CL5200, CL7200 ve LP-1.6 için CL-Works dosyası hazırlar.",
                "Model, port, shtrix-kod prefiksi va narx kasrlari — «Tarozi sozlamalari…» → «Tez sozlash». CL5000J va CL3000 ni kassa tarmoq orqali to‘ldiradi (CAS hujjatiga ko‘ra, tarozida hali tekshirilmagan); CL5500, CL5200, CL7200 va LP-1.6 uchun CL-Works fayli tayyorlanadi."),
        });
        if (TmFields.Parent is Panel fields)
            fields.Children.Insert(fields.Children.IndexOf(TmFields) + 1, _casFields);
    }

    /// <summary>2026-11-13: плитка «Масса-К» (серии R/SL), поля IP и порта (TCP 5001) и подсказка. Протокол открытый (massa.ru), на живых весах не проверен.</summary>
    private void AddMassaKParts()
    {
        _brandMassaKTile = new RadioButton { Classes = { "BrandTile" }, GroupName = "LabelScaleBrand" };
        _brandMassaKTile.Click += BrandTile_Click;
        var tileGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        tileGrid.Children.Add(new Border { Classes = { "tileBadge" }, Child = new TextBlock { Classes = { "tileBadgeText" }, Text = "МК" } });
        var tileTexts = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 2 };
        tileTexts.Children.Add(new TextBlock { Classes = { "tileTitle" }, Text = ScaleUi.BrandTitle(ScaleUi.BrandMassaK) });
        tileTexts.Children.Add(new TextBlock { Classes = { "tileSub" }, Text = L("Серии R, SL (MK-RP10, MK-RL10…): прямо по сети", "R, SL сериялары (MK-RP10, MK-RL10…): тармак аркылуу түз", "R, SL series (MK-RP10, MK-RL10…): directly over the network", "R, SL serisi (MK-RP10, MK-RL10…): doğrudan ağ üzerinden", "R, SL seriyalari (MK-RP10, MK-RL10…): tarmoq orqali to‘g‘ridan-to‘g‘ri") });
        Grid.SetColumn(tileTexts, 1);
        tileGrid.Children.Add(tileTexts);
        var tick = new TextBlock { Classes = { "tileCheck" }, Text = "✓" };
        Grid.SetColumn(tick, 2);
        tileGrid.Children.Add(tick);
        _brandMassaKTile.Content = tileGrid;
        if (BrandShtrikhTile.Parent is Panel tiles)
            tiles.Children.Add(_brandMassaKTile);

        _massaKIpBox = new TextBox { Classes = { "ModernTextBox" }, Height = 36, Watermark = "192.168.0.150" };
        _massaKIpBox.TextChanged += IpBox_TextChanged;
        _massaKIpBox.LostFocus += Field_LostFocus;
        _massaKPortBox = new TextBox { Classes = { "ModernTextBox" }, Height = 36, Watermark = "5001" };
        _massaKPortBox.LostFocus += Field_LostFocus;
        var ipColumn = new StackPanel();
        ipColumn.Children.Add(new TextBlock { Classes = { "FieldLabel" }, Text = L("IP-адрес весов", "Таразанын IP-дареги", "Scale IP address", "Tartı IP adresi", "Tarozi IP manzili") });
        ipColumn.Children.Add(_massaKIpBox);
        var portColumn = new StackPanel();
        portColumn.Children.Add(new TextBlock { Classes = { "FieldLabel" }, Text = L("TCP-порт", "TCP-порт", "TCP port", "TCP portu", "TCP port") });
        portColumn.Children.Add(_massaKPortBox);
        Grid.SetColumn(portColumn, 2);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,100") };
        row.Children.Add(ipColumn);
        row.Children.Add(portColumn);
        _massaKFields = new StackPanel { Spacing = 10 };
        _massaKFields.Children.Add(row);
        _massaKFields.Children.Add(new TextBlock
        {
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Text = L("Касса сама пишет весовые товары на весы Масса-К (серии R, SL) по сети, порт по умолчанию 5001; справочник весов не стирается. Протокол открытый, но на живых весах ещё не проверен — «Настройки весов…» → «Быстрая настройка».",
                "Касса салмактуу товарларды Масса-К (R, SL сериялары) таразасына тармак аркылуу өзү жазат, демейки порт 5001; тараза справочниги өчүрүлбөйт. Протокол ачык, бирок жандуу таразада азырынча текшерилген эмес — «Тараза жөндөөлөрү…» → «Ыкчам жөндөө».",
                "The till writes weighed goods to the Massa-K scale (R, SL series) itself over the network, default port 5001; the scale's directory is not erased. The protocol is open but not yet verified on live scales — “Scale settings…” → “Quick setup”.",
                "Kasa tartılı ürünleri Massa-K tartıya (R, SL serisi) ağ üzerinden kendisi yazar, varsayılan port 5001; tartının rehberi silinmez. Protokol açık ama canlı tartıda henüz doğrulanmadı — «Tartı ayarları…» → «Hızlı kurulum».",
                "Kassa vaznli tovarlarni Massa-K tarozisiga (R, SL seriyalari) tarmoq orqali o‘zi yozadi, standart port 5001; tarozi ma’lumotnomasi o‘chirilmaydi. Protokol ochiq, lekin jonli tarozida hali tekshirilmagan — «Tarozi sozlamalari…» → «Tez sozlash»."),
        });
        if (TmFields.Parent is Panel fields)
            fields.Children.Insert(fields.Children.IndexOf(TmFields) + 1, _massaKFields);
    }

    /// <summary>2026-11-06: плитка марки «Штрих РС-200», её поля подключения и кнопка «Инструкция подключения».</summary>
    private void AddShtrikhPcParts()
    {
        _brandPcTile = new RadioButton { Classes = { "BrandTile" }, GroupName = "LabelScaleBrand" };
        _brandPcTile.Click += BrandTile_Click;
        var tileGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        tileGrid.Children.Add(new Border { Classes = { "tileBadge" }, Child = new TextBlock { Classes = { "tileBadgeText" }, Text = "РС" } });
        var tileTexts = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 2 };
        tileTexts.Children.Add(new TextBlock { Classes = { "tileTitle" }, Text = ScaleUi.BrandTitle(ScaleUi.BrandShtrikhPc) });
        // 2026-11-12: в подписи — модель с шильдика (ШТРИХ РС200 С2/С3) и «с экраном», чтобы владелец не выбирал плитку «Штрих-ПРИНТ (Штрих-М)» по логотипу «ШТРИХ-М».
        tileTexts.Children.Add(new TextBlock { Classes = { "tileSub" }, Text = L("ШТРИХ РС200 С2/С3 (с экраном): обмен файлом через папку", "ШТРИХ РС200 С2/С3 (экраны бар): папка аркылуу файл алмашуу", "SHTRIH PC200 C2/C3 (with a screen): file exchange via a folder", "ŞTRİH PC200 C2/C3 (ekranlı): klasörle dosya alışverişi", "SHTRIX PC200 C2/C3 (ekranli): papka orqali fayl almashuv") });
        Grid.SetColumn(tileTexts, 1);
        tileGrid.Children.Add(tileTexts);
        var tick = new TextBlock { Classes = { "tileCheck" }, Text = "✓" };
        Grid.SetColumn(tick, 2);
        tileGrid.Children.Add(tick);
        _brandPcTile.Content = tileGrid;
        if (BrandShtrikhTile.Parent is Panel tiles)
            tiles.Children.Add(_brandPcTile);

        _pcFields = new ShtrikhPcFieldsView();
        _pcFields.Changed += () => { UpdateStatus(); SetupChanged?.Invoke(); };
        if (ShtrikhFields.Parent is Panel fields)
            fields.Children.Insert(fields.Children.IndexOf(ShtrikhFields) + 1, _pcFields);
        // 2026-11-06 (P3-63): «Очистить базу» и список файлов обмена — после кнопок и результата проверки связи, а не перед ними.
        if (StatusBorder.Parent is Panel statusHost)
            statusHost.Children.Insert(statusHost.Children.IndexOf(StatusBorder) + 1, _pcFields.AdvancedPart);

        _pcGuideButton = new Button { Classes = { "ScaleSecondary" }, Content = L("Инструкция подключения", "Туташтыруу нускамасы", "Connection guide", "Bağlantı kılavuzu", "Ulanish yo‘riqnomasi") };
        _pcGuideButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new ScaleGuideWindow(ScaleUi.BrandShtrikhPc).ShowDialog(owner).ConfigureAwait(true);
        };
        ButtonsRow.Children.Insert(ButtonsRow.Children.IndexOf(DeviceSettingsButton) + 1, _pcGuideButton);
    }

    private static string L(string ru, string ky, string en, string tr, string uz) => NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi.OwnerPath(Tr.T(ru, ky, en, tr, uz)); // 2026-11-11: в программе владельца путь «Товары → Весы»

    private IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    private void Panel_Loaded(object? sender, RoutedEventArgs e) => LoadFromPreferences();

    /// <summary>Заполняет плитки и поля из настроек. Ничего не сохраняет.</summary>
    public void LoadFromPreferences()
    {
        _loading = true;
        try
        {
            var prefs = UserPreferences.Instance;
            _brand = ScaleUi.NormalizeBrand(prefs.ScaleBrand);
            BrandShtrikhTile.IsChecked = _brand == ScaleUi.BrandShtrikh;
            BrandRongtaTile.IsChecked = _brand == ScaleUi.BrandRongta;
            BrandTmTile.IsChecked = _brand == ScaleUi.BrandTm;
            BrandAiTile.IsChecked = _brand == ScaleUi.BrandAi;
            _brandPcTile.IsChecked = _brand == ScaleUi.BrandShtrikhPc;
            _brandMassaKTile.IsChecked = _brand == ScaleUi.BrandMassaK; // 2026-11-13
            _massaKIpBox.Text = prefs.MassaKScaleIp ?? "";
            _massaKPortBox.Text = prefs.MassaKScalePort.ToString(CultureInfo.InvariantCulture);
            _brandCasTile.IsChecked = _brand == ScaleUi.BrandCas; // 2026-11-13
            _casIpBox.Text = NurMarketKassa.Services.Hardware.CasScaleSettings.Current.Ip;
            _pcFields.Load();

            ShtrikhIpBox.Text = prefs.ScaleNetworkIp ?? "";
            ShtrikhPortBox.Text = prefs.ScaleLanPort.ToString(CultureInfo.InvariantCulture);
            ShtrikhPasswordBox.Text = prefs.ScaleLanPassword ?? "";
            ShtrikhRouteDirect.IsChecked = prefs.ShtrikhDirectLan;
            ShtrikhRouteServer.IsChecked = !prefs.ShtrikhDirectLan;

            RongtaIpBox.Text = ScaleUi.IpOf(ScaleUi.BrandRongta);
            RongtaPortBox.Text = prefs.RongtaScalePort.ToString(CultureInfo.InvariantCulture);
            var ownServer = string.Equals(prefs.RongtaDataSource, "server", StringComparison.OrdinalIgnoreCase);
            RongtaRouteServer.IsChecked = ownServer;
            RongtaRouteLan.IsChecked = prefs.RongtaDirectLan;
            RongtaRouteSite.IsChecked = !ownServer && !prefs.RongtaDirectLan;
            RongtaServerPortBox.Text = prefs.RongtaServerPort.ToString(CultureInfo.InvariantCulture);

            TmIpBox.Text = prefs.TmScaleIp ?? "";
            TmPortBox.Text = prefs.TmScalePort.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
            _hasLoaded = true;
        }
        ApplyBrand();
    }

    /// <summary>Показывает поля выбранной марки, подписи и состояние связи.</summary>
    private void ApplyBrand()
    {
        ShtrikhFields.IsVisible = _brand == ScaleUi.BrandShtrikh;
        RongtaFields.IsVisible = _brand == ScaleUi.BrandRongta;
        TmFields.IsVisible = _brand == ScaleUi.BrandTm;
        AiFields.IsVisible = _brand == ScaleUi.BrandAi;
        _massaKFields.IsVisible = _brand == ScaleUi.BrandMassaK; // 2026-11-13
        _casFields.IsVisible = _brand == ScaleUi.BrandCas; // 2026-11-13
        _pcFields.IsVisible = _brand == ScaleUi.BrandShtrikhPc;
        _pcFields.AdvancedPart.IsVisible = _brand == ScaleUi.BrandShtrikhPc;

        ConnectionTitle.Text = _brand == ScaleUi.BrandAi
            ? ScaleUi.LabelBrandTitle(_brand)
            : L("Подключение: ", "Туташуу: ", "Connection: ", "Bağlantı: ", "Ulanish: ") + ScaleUi.LabelBrandTitle(_brand);

        var hasNetwork = _brand != ScaleUi.BrandAi;
        CheckButton.IsVisible = hasNetwork;
        // 2026-11-06: у РС-200 нет поиска по сети — вместо него инструкция подключения
        // 2026-11-07 (живая проверка 1.17.81, Б-F1): кнопка «Настройки весов…» у РС-200 была скрыта — окно параметров (Config.txt) нельзя было открыть; теперь видна у всех марок, кроме AI.
        var isPc = _brand == ScaleUi.BrandShtrikhPc;
        FindButton.IsVisible = hasNetwork && !isPc && _brand != ScaleUi.BrandMassaK && _brand != ScaleUi.BrandCas; // 2026-11-13: поиска Масса-К и CAS в сети пока нет
        DeviceSettingsButton.IsVisible = hasNetwork;
        _pcGuideButton.IsVisible = isPc;
        SendButton.IsVisible = !IsDialogMode;
        SendButton.Content = _brand == ScaleUi.BrandAi
            ? L("Подготовить файл", "Файл даярдоо", "Prepare the file", "Dosyayı hazırla", "Faylni tayyorlash")
            : L("Отправить товары", "Товарларды жөнөтүү", "Send goods", "Ürünleri gönder", "Tovarlarni yuborish");

        ShtrikhRouteHint.Text = ShtrikhRouteDirect.IsChecked == true
            ? L("Касса сама отправляет товары на весы по сети (UDP), без сервера. Нужны адрес, порт и пароль из системного меню весов.",
                "Касса товарларды таразага тармак аркылуу (UDP) өзү жөнөтөт, серверсиз. Таразанын системалык менюсундагы дарек, порт жана сырсөз керек.",
                "The till sends goods to the scale itself over the network (UDP), without the server. It needs the address, port and password from the scale's system menu.",
                "Kasa ürünleri tartıya ağ üzerinden (UDP) sunucusuz kendisi gönderir. Tartının sistem menüsündeki adres, port ve şifre gerekir.",
                "Kassa tovarlarni taroziga tarmoq orqali (UDP) serversiz o‘zi yuboradi. Tarozining tizim menyusidagi manzil, port va parol kerak.")
            : L("Сервер NurCRM сам передаёт товары на весы (проверенный путь). Адрес здесь нужен только для «Проверить связь».",
                "NurCRM сервери товарларды таразага өзү берет (текшерилген жол). Бул жердеги дарек «Байланышты текшерүү» үчүн гана керек.",
                "The NurCRM server passes the goods to the scale itself (the proven path). The address here is only for “Check connection”.",
                "NurCRM sunucusu ürünleri tartıya kendisi iletir (denenmiş yol). Buradaki adres yalnızca «Bağlantıyı kontrol et» içindir.",
                "NurCRM serveri tovarlarni taroziga o‘zi uzatadi (sinalgan yo‘l). Bu yerdagi manzil faqat «Aloqani tekshirish» uchun kerak.");

        var ownServer = RongtaRouteServer.IsChecked == true;
        RongtaServerPortRow.IsVisible = ownServer;
        RongtaRouteHint.Text = RongtaRouteLan.IsChecked == true
            ? L("Касса сама записывает отмеченные товары на весы по сети (порт 4001), без RLS1000 и без сайта — как на TM-30F. Нужен только IP весов.",
                "Касса белгиленген товарларды таразага тармак аркылуу өзү жазат (4001 порт), RLS1000'сиз жана сайтсыз — TM-30F'тегидей. Таразанын IP'си гана керек.",
                "The till writes the ticked goods to the scale itself over the network (port 4001), without RLS1000 or the website — like the TM-30F. Only the scale IP is needed.",
                "Kasa işaretli ürünleri tartıya ağ üzerinden kendisi yazar (port 4001), RLS1000 ve site olmadan — TM-30F'teki gibi. Yalnızca tartı IP'si gerekir.",
                "Kassa belgilangan tovarlarni taroziga tarmoq orqali o‘zi yozadi (4001 port), RLS1000 va saytsiz — TM-30F dagi kabi. Faqat tarozi IP'si kerak.")
            : ownServer
            ? L("Список строится из каталога кассы; касса ждёт подключения программы весов на этот порт и сама «нажимает» в RLS1000 загрузку (если RLS1000 нет — запустите загрузку в программе весов сами, есть 90 секунд).",
                "Тизме кассанын каталогунан түзүлөт; касса тараза программасынын ушул портко туташуусун күтөт жана RLS1000'де жүктөөнү өзү «басат» (RLS1000 жок болсо — тараза программасында жүктөөнү өзүңүз баштаңыз, 90 секунд бар).",
                "The list is built from the till catalog; the till waits for the scale software to connect to this port and “presses” download in RLS1000 itself (without RLS1000, start the upload in the scale software yourself, you have 90 seconds).",
                "Liste kasa kataloğundan oluşturulur; kasa tartı programının bu porta bağlanmasını bekler ve RLS1000'de yüklemeye kendisi «basar» (RLS1000 yoksa yüklemeyi tartı programında kendiniz başlatın, 90 saniyeniz var).",
                "Ro‘yxat kassa katalogidan tuziladi; kassa tarozi dasturining shu portga ulanishini kutadi va RLS1000 da yuklashni o‘zi «bosadi» (RLS1000 bo‘lmasa — yuklashni tarozi dasturida o‘zingiz boshlang, 90 soniya bor).")
            : L("Касса скачивает файл .txp с сайта NurCRM (как вкладка «Rongta» на сайте) и запускает загрузку в RLS1000. На весы уходит весь список весовых товаров.",
                "Касса NurCRM сайтынан .txp файлын жүктөйт (сайттагы «Rongta» өтмөгүндөй) жана RLS1000'де жүктөөнү баштайт. Таразага бардык салмактуу товарлардын тизмеси кетет.",
                "The till downloads the .txp file from the NurCRM website (like the “Rongta” tab there) and starts the upload in RLS1000. The whole list of weighed goods goes to the scale.",
                "Kasa NurCRM sitesinden .txp dosyasını indirir (sitedeki «Rongta» sekmesi gibi) ve RLS1000'de yüklemeyi başlatır. Tartıya tüm tartılı ürün listesi gider.",
                "Kassa NurCRM saytidan .txp faylini yuklab oladi (saytdagi «Rongta» bo‘limi kabi) va RLS1000 da yuklashni boshlaydi. Taroziga barcha vaznli tovarlar ro‘yxati ketadi.");

        UpdateIpHint();
        UpdateStatus();
    }

    private TextBox? CurrentIpBox => _brand switch
    {
        ScaleUi.BrandRongta => RongtaIpBox,
        ScaleUi.BrandTm => TmIpBox,
        ScaleUi.BrandMassaK => _massaKIpBox, // 2026-11-13
        ScaleUi.BrandCas => _casIpBox, // 2026-11-13
        ScaleUi.BrandAi => null,
        ScaleUi.BrandShtrikhPc => null, // 2026-11-06: вместо IP — папка обмена
        _ => ShtrikhIpBox,
    };

    /// <summary>Подсказка под адресом: опечатка, чужая сеть и т. п. (ScaleUi.CheckScaleIp).</summary>
    private void UpdateIpHint()
    {
        var box = CurrentIpBox;
        if (box is null)
        {
            IpHintBorder.IsVisible = false;
            return;
        }
        var (level, text) = ScaleUi.CheckScaleIp(box.Text);
        IpHintBorder.IsVisible = level != ScaleIpLevel.None;
        if (level == ScaleIpLevel.None)
            return;
        var (bg, border, mark) = level switch
        {
            ScaleIpLevel.Ok => ("BrushSuccessSoft", "BrushSuccess", "✓ "),
            ScaleIpLevel.Warning => ("BrushWarningSoft", "BrushWarning", "⚠ "),
            _ => ("BrushDangerSoft", "BrushDanger", "✗ "),
        };
        IpHintBorder.Background = Brush(bg, Brushes.LightYellow);
        IpHintBorder.BorderBrush = Brush(border, Brushes.Goldenrod);
        IpHintText.Text = mark + text;
    }

    /// <summary>Строка состояния: адрес/способ и последняя проверка связи.</summary>
    private void UpdateStatus()
    {
        if (_brand == ScaleUi.BrandAi)
        {
            StatusBorder.IsVisible = false;
            return;
        }
        StatusBorder.IsVisible = true;
        var state = ScaleUi.LastCheckOf(_brand);
        StatusText.Text = ScaleUi.AddressLine(_brand) + Environment.NewLine + ScaleUi.LastCheckText(_brand);
        StatusBorder.Background = Brush(state is null ? "BrushSurfaceSubtle" : state.Ok ? "BrushSuccessSoft" : "BrushWarningSoft", Brushes.WhiteSmoke);
        StatusBorder.BorderBrush = Brush(state is null ? "BrushBorder" : state.Ok ? "BrushSuccess" : "BrushWarning", Brushes.LightGray);
    }

    // ------------------------------------------------------------------ сохранение

    private void Field_LostFocus(object? sender, RoutedEventArgs e) => SaveFields();

    private void IpBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        UpdateIpHint();
    }

    /// <summary>Пишет поля ВСЕХ марок в их собственные настройки (не смешивая адреса) и сохраняет
    /// файл. Пустой пароль Штрих-ПРИНТ не затирает сохранённый (пустой весы не примут).</summary>
    public void SaveFields()
    {
        if (_loading || !_hasLoaded)
            return;
        var prefs = UserPreferences.Instance;

        prefs.ScaleNetworkIp = (ShtrikhIpBox.Text ?? "").Trim();
        if (TryPort(ShtrikhPortBox.Text, out var shtrikhPort))
            prefs.ScaleLanPort = shtrikhPort;
        var password = (ShtrikhPasswordBox.Text ?? "").Trim();
        if (password.Length > 0)
            prefs.ScaleLanPassword = password;
        prefs.ShtrikhDirectLan = ShtrikhRouteDirect.IsChecked == true;

        var rongtaIp = (RongtaIpBox.Text ?? "").Trim();
        // Rongta раньше делила адрес со Штрихом (IpOf подставляет его): не пишем подставленное
        // значение обратно, пока его не поменяли.
        if (!string.IsNullOrEmpty(prefs.RongtaScaleIp) || !string.Equals(rongtaIp, prefs.ScaleNetworkIp ?? "", StringComparison.Ordinal))
            prefs.RongtaScaleIp = rongtaIp;
        if (TryPort(RongtaPortBox.Text, out var rongtaPort))
            prefs.RongtaScalePort = rongtaPort;
        prefs.RongtaDataSource = RongtaRouteServer.IsChecked == true ? "server" : RongtaRouteLan.IsChecked == true ? "lan" : "site";
        if (TryPort(RongtaServerPortBox.Text, out var serverPort))
            prefs.RongtaServerPort = serverPort;

        prefs.TmScaleIp = (TmIpBox.Text ?? "").Trim();
        if (TryPort(TmPortBox.Text, out var tmPort))
            prefs.TmScalePort = tmPort;

        prefs.MassaKScaleIp = (_massaKIpBox.Text ?? "").Trim(); // 2026-11-13
        if (TryPort(_massaKPortBox.Text, out var massaKPort))
            prefs.MassaKScalePort = massaKPort;

        // 2026-11-13: CAS — только адрес (остальное в окне CAS); не пишем настройки, пока адрес не менялся
        var casIp = (_casIpBox.Text ?? "").Trim();
        var cas = NurMarketKassa.Services.Hardware.CasScaleSettings.Current;
        if (!string.Equals(cas.Ip, casIp, StringComparison.Ordinal))
        {
            cas.Ip = casIp;
            NurMarketKassa.Services.Hardware.CasScaleSettings.Save(cas);
        }

        prefs.SaveToDisk();
        _pcFields.Save(); // 2026-11-06: папка обмена весов РС-200
        UpdateStatus();
        SetupChanged?.Invoke();
    }

    private static bool TryPort(string? text, out int port) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // ------------------------------------------------------------------ марка и способ

    private async void BrandTile_Click(object? sender, RoutedEventArgs e)
    {
        var brand = sender == BrandRongtaTile ? ScaleUi.BrandRongta
            : sender == BrandTmTile ? ScaleUi.BrandTm
            : sender == BrandAiTile ? ScaleUi.BrandAi
            : sender == _brandPcTile ? ScaleUi.BrandShtrikhPc
            : sender == _brandMassaKTile ? ScaleUi.BrandMassaK // 2026-11-13
            : sender == _brandCasTile ? ScaleUi.BrandCas // 2026-11-13
            : ScaleUi.BrandShtrikh;
        SaveFields();
        _brand = brand;
        UserPreferences.Instance.ScaleBrand = brand;
        UserPreferences.Instance.SaveToDisk();
        // 2026-09-28 (просьба владельца): у TM-30F выбран формат с суммой без веса — префикс весов
        // сразу получает правило «сумма» (Настройки → Весы → «Штрих-код: вес / сумма»).
        if (brand == ScaleUi.BrandTm)
            ScaleBarcodeRules.EnsureTmAmountRule();
        // 2026-11-06: у РС-200 свой разбор этикеток (префикс, число знаков кода, «Код-Вес»…) — включается вместе с выбором марки.
        // 2026-11-06 (аудит Этапа 2, С-3): раскладку РС-200 (префикс 27 и др.) молча не применяем — она меняет чтение этикеток ДРУГИХ весов с тем же префиксом; сначала вопрос.
        if (brand == ScaleUi.BrandShtrikhPc)
        {
            try
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                await ScaleBarcodeRules.OfferShtrikhPcPresetAsync(question => PosDialogs.ConfirmYesNoModalAsync(owner, question, null,
                    Tr.T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"), Tr.T("Не менять", "Өзгөртпөө", "Keep as is", "Değiştirme", "O'zgartirmaslik"))).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы Штрих РС-200: вопрос о раскладке не показан ({ex.Message}) — раскладка не менялась.", "SCALES");
            }
        }
        ApplyBrand();
        SetupChanged?.Invoke();
    }

    private void Route_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        ApplyBrand();
    }

    // ------------------------------------------------------------------ кнопки

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        CheckButton.IsEnabled = false;
        StatusBorder.IsVisible = true;
        StatusText.Text = ScaleUi.AddressLine(_brand) + Environment.NewLine
            + L("Проверяю связь с весами…", "Тараза менен байланыш текшерилүүдө…", "Checking the connection to the scale…", "Tartı bağlantısı kontrol ediliyor…", "Tarozi bilan aloqa tekshirilmoqda…");
        try
        {
            await ScaleUi.CheckConnectionAsync(_brand).ConfigureAwait(true);
        }
        finally
        {
            CheckButton.IsEnabled = true;
            UpdateStatus();
            SetupChanged?.Invoke();
            // 2026-11-06 (P3-63): результат проверки всегда прокручивается в видимую часть окна (не остаётся за подвалом).
            Dispatcher.UIThread.Post(() => StatusBorder.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    private async void Find_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        SaveFields();
        if (await ScaleUi.OpenScanAsync(owner, _brand).ConfigureAwait(true))
            LoadFromPreferences();
        SetupChanged?.Invoke();
    }

    private async void DeviceSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        SaveFields();
        await ScaleUi.OpenBrandSettingsAsync(owner, _brand).ConfigureAwait(true);
        // Окна марок сами пишут адрес/порт/марку — перечитываем.
        LoadFromPreferences();
        SetupChanged?.Invoke();
    }

    private void Send_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = App.GetRequiredService<ScalesPluWindow>();
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}
