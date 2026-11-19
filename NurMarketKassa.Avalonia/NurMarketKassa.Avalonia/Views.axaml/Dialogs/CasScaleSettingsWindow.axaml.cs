using NurMarketKassa.AvaloniaHost.Services;
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
/// 2026-11-13: «Настройки весов CAS». Решение владельца 13.11: CL5000J и CL3000 — касса пишет товары по сети (протокол из документа CAS «CL5000J Communication
/// Protocol» 2007, <see cref="CasProtocol"/>, порт 20304; НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО), CL5500 / CL5200 / CL7200 / LP-1.6 — только файл для CL-Works
/// («Файл → Импорт → CSV», <see cref="CasCsvWriter"/>). Вкладки: «Быстрая настройка» (три шага, общий блок <see cref="ScaleUi.QuickSetup"/>) / «Загрузка товаров» /
/// «Штрих-код» (третья — <see cref="ShowBarcodeTab"/>) / «Для мастера». Настройки — <see cref="CasScaleSettings"/> (локально, в UserPreferences.CasScaleConfig).
/// </summary>
public partial class CasScaleSettingsWindow : Window
{
    private CasScaleSettings _s = CasScaleSettings.Current;
    private bool _loading;

    private ComboBox _modelBox = null!;
    private TextBox _ipBox = null!;
    private NumericUpDown _portBox = null!;
    private TextBlock _modelHint = null!;
    private ScaleQuickView _quick = null!;
    private readonly StackPanel _masterPanel = new() { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };

    // Сеть.
    private IReadOnlyList<LocalSubnet> _subnets = Array.Empty<LocalSubnet>();
    private TextBlock _networkText = null!;

    // Штрих-код.
    private ComboBox _formatBox = null!;
    private NumericUpDown _prefix = null!;
    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private NumericUpDown _samplePrice = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;
    private TextBlock _formatHint = null!;
    private static readonly string[] Formats =
    {
        CasBarcodeFormat.Recommended("plu", true), CasBarcodeFormat.Recommended("code", true),
        CasBarcodeFormat.Recommended("plu", false), CasBarcodeFormat.Recommended("code", false),
    };

    // Загрузка товаров.
    private ComboBox _priceDecimalsBox = null!;
    private NumericUpDown _barcodeNumber = null!;
    private NumericUpDown _labelNumber = null!;
    private RadioButton _csv1251 = null!;
    private RadioButton _csvDot = null!;

    public CasScaleSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FitToKassaScreen();
        Title = L("Настройки весов CAS", "CAS таразасынын жөндөөлөрү", "CAS scale settings", "CAS tartı ayarları", "CAS tarozi sozlamalari");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Весы CAS: выберите модель и пройдите три шага ниже. CL5000J и CL3000 касса заполняет сама по сети (пока не проверено на весах), для остальных моделей CAS готовит файл для программы CL-Works.",
            "CAS таразасы: моделди тандап, төмөнкү үч кадамды аткарыңыз. CL5000J жана CL3000 таразаларын касса тармак аркылуу өзү толтурат (таразада азырынча текшерилген эмес), CAS'тын калган моделдери үчүн CL-Works программасына файл даярдалат.",
            "CAS scale: choose the model and follow the three steps below. The till fills CL5000J and CL3000 itself over the network (not verified on a scale yet); for the other CAS models it prepares a file for the CL-Works software.",
            "CAS tartı: modeli seçin ve aşağıdaki üç adımı izleyin. CL5000J ve CL3000'i kasa ağ üzerinden kendisi doldurur (tartıda henüz doğrulanmadı); diğer CAS modelleri için CL-Works programına dosya hazırlanır.",
            "CAS tarozisi: modelni tanlang va quyidagi uch qadamni bajaring. CL5000J va CL3000 ni kassa tarmoq orqali o‘zi to‘ldiradi (tarozida hali tekshirilmagan); CAS ning boshqa modellari uchun CL-Works dasturiga fayl tayyorlanadi.");

        _loading = true;
        Tabs.Items.Add(MakeTab(QuickTabTitle, BuildQuickTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        BuildMasterTab();
        Tabs.Items.Add(MakeTab(MasterTabTitle, _masterPanel));
        _loading = false;
        ApplyModel();
        ShowResult(L("Выберите модель, введите IP весов и нажмите «Проверить связь».", "Моделди тандаңыз, таразанын IP'син киргизип «Байланышты текшерүү» басыңыз.", "Choose the model, enter the scale IP and press “Check connection”.", "Modeli seçin, tartı IP'sini girin ve «Bağlantıyı kontrol et»e basın.", "Modelni tanlang, tarozi IP'sini kiriting va «Aloqani tekshirish»ni bosing."), false);
    }

    /// <summary>Открыть сразу на вкладке «Штрих-код» (кнопка «Формат штрих-кода на самих весах…», окно «Весы»).</summary>
    public void ShowBarcodeTab() => Tabs.SelectedIndex = 2;

    private static TabItem MakeTab(string header, Control content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content } };

    private static string ModelLabel(string model) =>
        CasScaleSettings.ModelTitle(model) + " — " + (CasScaleSettings.IsNetworkModel(model)
            ? L("по сети", "тармак аркылуу", "over the network", "ağ üzerinden", "tarmoq orqali")
            : L("файл для CL-Works", "CL-Works үчүн файл", "file for CL-Works", "CL-Works için dosya", "CL-Works uchun fayl"));

    private void UpdateHeader()
    {
        var ip = (_ipBox.Text ?? "").Trim();
        ConnectionText.Text = CasScaleSettings.ModelTitle(_s.Model) + " · " + (_s.IsNetwork
            ? L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ") + (ip.Length > 0 ? ip : "—")
            : L("файл для CL-Works", "CL-Works үчүн файл", "file for CL-Works", "CL-Works için dosya", "CL-Works uchun fayl"));
    }

    // =====================================================================================
    // Быстрая настройка
    // =====================================================================================

    private Control BuildQuickTab()
    {
        _ipBox = new TextBox { Text = _s.Ip, Watermark = "192.168.1.80", MinWidth = 220 };
        _ipBox.LostFocus += (_, _) => { SaveConnection(); UpdateHeader(); UpdateNetwork(); };
        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = _s.Port, MinWidth = 140 };
        _portBox.ValueChanged += (_, _) => { if (!_loading) SaveConnection(); UpdateNetwork(); };

        // Модель — над тремя шагами: от неё зависит, сеть это или файл.
        _modelBox = new ComboBox { MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var m in CasScaleSettings.Models)
            _modelBox.Items.Add(ModelLabel(m));
        _modelBox.SelectedIndex = Math.Max(0, Array.IndexOf(CasScaleSettings.Models, _s.Model));
        _modelBox.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            SaveConnection();
            ApplyModel();
        };
        _modelHint = Text("", "hint");
        var modelCard = Card(L("Модель весов", "Таразанын модели", "Scale model", "Tartı modeli", "Tarozi modeli"), null, out var modelBody);
        modelBody.Children.Add(_modelBox);
        modelBody.Children.Add(_modelHint);

        _quick = QuickSetup(this, new ScaleQuickOptions
        {
            Brand = BrandCas,
            WhereLabel = L("Адрес весов (IP)", "Таразанын дареги (IP)", "Scale address (IP)", "Tartı adresi (IP)", "Tarozi manzili (IP)"),
            WhereBox = _ipBox,
            Save = () => { SaveAll(); UpdateHeader(); UpdateNetwork(); },
            SendGoods = () => { SaveAll(); OpenScalesWindow(this, BrandCas); },
            CheckLabel = ShowBarcodeTab,
            ShowResult = ShowResult,
        });

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        panel.Children.Add(modelCard);
        panel.Children.Add(_quick.Root);
        _quick.Root.Margin = default;
        return panel;
    }

    /// <summary>Модель изменилась: сеть или файл — что показать и что можно нажимать.</summary>
    private void ApplyModel()
    {
        _s = CasScaleSettings.Current;
        var network = _s.IsNetwork;
        _ipBox.IsEnabled = network;
        _quick.CheckButton.IsEnabled = network;
        _modelHint.Text = network
            ? L("Касса сама запишет товары на весы по сети. Протокол взят из документа CAS (CL5000J Communication Protocol); на живых весах пока не проверено — первую отправку делайте на одном товаре.",
                "Касса товарларды таразага тармак аркылуу өзү жазат. Протокол CAS документинен алынган (CL5000J Communication Protocol); таразада азырынча текшерилген эмес — биринчи жөнөтүүнү бир товар менен жасаңыз.",
                "The till writes the goods to the scale itself over the network. The protocol is taken from the CAS document (CL5000J Communication Protocol); not verified on a real scale yet — send one item first.",
                "Kasa ürünleri tartıya ağ üzerinden kendisi yazar. Protokol CAS belgesinden alındı (CL5000J Communication Protocol); gerçek tartıda henüz doğrulanmadı — ilk gönderimi tek ürünle yapın.",
                "Kassa tovarlarni taroziga tarmoq orqali o‘zi yozadi. Protokol CAS hujjatidan olingan (CL5000J Communication Protocol); haqiqiy tarozida hali tekshirilmagan — birinchi yuborishni bitta tovar bilan qiling.")
            : L("Эти модели касса по сети не ведёт: в окне «Весы» кнопка «Сохранить файл для CL-Works» сделает файл, а в программе CL-Works — «Файл → Импорт → CSV», затем передача на весы.",
                "Бул моделдерди касса тармак аркылуу жүргүзбөйт: «Таразалар» терезесиндеги «CL-Works үчүн файлды сактоо» баскычы файл жасайт, ал эми CL-Works программасында — «Файл → Импорт → CSV», андан кийин таразага өткөрүү.",
                "The till does not talk to these models over the network: the “Save file for CL-Works” button in the “Scales” window makes a file; in CL-Works use “File → Import → CSV”, then send it to the scale.",
                "Kasa bu modellerle ağ üzerinden konuşmaz: «Tartı» penceresindeki «CL-Works için dosyayı kaydet» düğmesi dosya hazırlar; CL-Works'te «Dosya → İçe aktar → CSV», sonra tartıya aktarın.",
                "Kassa bu modellar bilan tarmoq orqali ishlamaydi: «Tarozi» oynasidagi «CL-Works uchun faylni saqlash» tugmasi fayl tayyorlaydi; CL-Works dasturida — «Fayl → Import → CSV», so‘ng taroziga uzating.");
        UpdateHeader();
        _quick.RefreshFromLastCheck();
        UpdateSample();
        UpdateNetwork();
    }

    // =====================================================================================
    // Загрузка товаров
    // =====================================================================================

    private Control BuildUploadTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Как касса заполняет весы CAS", "Касса CAS таразасын кантип толтурат", "How the till fills a CAS scale", "Kasa CAS tartıyı nasıl doldurur", "Kassa CAS tarozisini qanday to‘ldiradi"),
            L("Окно «Весы» → марка CAS → отметьте товары → «Отправить на весы»: касса шлёт по одной карточке и ждёт ответ весов. Номер PLU закреплён за товаром (колонка PLU), код в штрихкоде — колонка «Код в ШК». Очистку памяти, этикетки, отделы и формат штрихкода касса на весы не пишет никогда.",
              "«Таразалар» терезеси → CAS маркасы → товарларды белгилеңиз → «Таразага жөнөтүү»: касса карточкаларды бирден жөнөтөт жана тараза жообун күтөт. PLU номери товарга бекитилген (PLU тилкеси), штрих-коддогу код — «ШКдагы код» тилкеси. Эстутумду тазалоону, этикеткаларды, бөлүмдөрдү жана штрих-код форматын касса таразага эч качан жазбайт.",
              "“Scales” window → brand CAS → tick the goods → “Send to scale”: the till sends one card at a time and waits for the scale reply. The PLU number is fixed per item (PLU column), the code in the barcode is the “Code in barcode” column. The till never writes memory clearing, labels, departments or the barcode format to the scale.",
              "«Tartı» penceresi → CAS markası → ürünleri işaretleyin → «Tartıya gönder»: kasa kartları tek tek gönderir ve tartının yanıtını bekler. PLU numarası ürüne sabittir (PLU sütunu), barkoddaki kod «Barkoddaki kod» sütunudur. Kasa belleği silme, etiket, reyon ve barkod biçimini tartıya asla yazmaz.",
              "«Tarozi» oynasi → CAS markasi → tovarlarni belgilang → «Taroziga yuborish»: kassa kartochkalarni bittadan yuboradi va tarozi javobini kutadi. PLU raqami tovarga biriktirilgan (PLU ustuni), shtrix-koddagi kod — «Shtrix-koddagi kod» ustuni. Kassa xotirani tozalash, yorliqlar, bo‘limlar va shtrix-kod formatini taroziga hech qachon yozmaydi."),
            out var body);

        _priceDecimalsBox = new ComboBox { MinWidth = 320 };
        _priceDecimalsBox.Items.Add(L("0 — целые сомы [123]", "0 — бүтүн сом [123]", "0 — whole som [123]", "0 — tam som [123]", "0 — butun so‘m [123]"));
        _priceDecimalsBox.Items.Add("1 [12.3]");
        _priceDecimalsBox.Items.Add("2 [1.23]");
        _priceDecimalsBox.Items.Add("3 [0.123]");
        _priceDecimalsBox.SelectedIndex = _s.PricePoint;
        body.Children.Add(Row(L("Знаков после запятой в цене на весах", "Таразадагы баада үтүрдөн кийинки белгилер", "Decimals in the price on the scale", "Tartıdaki fiyatta ondalık hane", "Tarozidagi narxda verguldan keyingi xonalar"), _priceDecimalsBox));
        body.Children.Add(Text(L("Должно совпадать с настройкой весов; число показывает «Проверить связь» (на весах CL5000J значение читается командой состояния). Цена на весах — не больше 999999 в наименьших единицах (при двух знаках — 9999,99 сом): дороже товар касса не отправит и назовёт его.",
            "Таразанын жөндөөсү менен дал келиши керек; санды «Байланышты текшерүү» көрсөтөт (CL5000J таразасында маани абал буйругу менен окулат). Таразадагы баа — эң кичине бирдикте 999999дан ашпайт (эки белгиде — 9999,99 сом): кымбат товарды касса жөнөтпөйт жана атын айтат.",
            "Must match the scale setting; “Check connection” shows the number (on a CL5000J it is read with the status command). The price on the scale is at most 999999 in the smallest units (with two decimals — 9999.99 som): a dearer item is not sent and is named.",
            "Tartı ayarıyla aynı olmalı; sayıyı «Bağlantıyı kontrol et» gösterir (CL5000J'de durum komutuyla okunur). Tartıdaki fiyat en küçük birimde en çok 999999'dur (iki hanede — 9999,99 som): daha pahalı ürün gönderilmez ve adı söylenir.",
            "Tarozi sozlamasiga mos kelishi kerak; sonni «Aloqani tekshirish» ko‘rsatadi (CL5000J da holat buyrug‘i bilan o‘qiladi). Tarozidagi narx eng kichik birlikda 999999 dan oshmaydi (ikki xonada — 9999,99 so‘m): qimmat tovar yuborilmaydi va nomi aytiladi."), "hint"));

        _barcodeNumber = new NumericUpDown { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "0", Value = _s.BarcodeNumber, MinWidth = 140 };
        body.Children.Add(Row(L("Номер формата штрихкода на весах (0 — как настроено на весах)", "Таразадагы штрих-код форматынын номери (0 — таразада кандай коюлса ошондой)", "Barcode format number on the scale (0 — as set on the scale)", "Tartıdaki barkod biçimi numarası (0 — tartıda ayarlandığı gibi)", "Tarozidagi shtrix-kod formati raqami (0 — tarozida qanday o‘rnatilgan bo‘lsa shunday)"), _barcodeNumber));
        _labelNumber = new NumericUpDown { Minimum = 0, Maximum = 999, Increment = 1, FormatString = "0", Value = _s.LabelNumber, MinWidth = 140 };
        body.Children.Add(Row(L("Номер формата этикетки (0 — по умолчанию весов)", "Этикетка форматынын номери (0 — таразанын демейкиси)", "Label format number (0 — the scale default)", "Etiket biçimi numarası (0 — tartının varsayılanı)", "Yorliq formati raqami (0 — tarozining odatiysi)"), _labelNumber));

        body.Children.Add(ButtonRow(
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveAll();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            }),
            MakeButton(L("Открыть окно «Весы» (CAS)", "«Таразалар» терезесин ачуу (CAS)", "Open the “Scales” window (CAS)", "«Tartı» penceresini aç (CAS)", "«Tarozi» oynasini ochish (CAS)"), false,
                (_, _) =>
                {
                    SaveAll();
                    OpenScalesWindow(this, BrandCas);
                })));
        panel.Children.Add(card);

        var fileCard = Card(L("Файл для CL-Works", "CL-Works үчүн файл", "File for CL-Works", "CL-Works için dosya", "CL-Works uchun fayl"),
            L("Для CL5500, CL5200, CL7200 и LP-1.6 (и как запасной путь для остальных): окно «Весы» → «Сохранить файл для CL-Works». В CL-Works: «Файл → Импорт → CSV», проверьте товары, затем «Обмен данными → Передать данные». Формат колонок взят из практики выгрузок для CL-Works; на живом CL-Works не проверялся.",
              "CL5500, CL5200, CL7200 жана LP-1.6 үчүн (жана калгандарына запас жол катары): «Таразалар» терезеси → «CL-Works үчүн файлды сактоо». CL-Works'то: «Файл → Импорт → CSV», товарларды текшерип, андан кийин «Обмен данными → Передать данные». Тилкелердин форматы CL-Works'ко чыгаруу тажрыйбасынан алынды; тирүү CL-Works'то текшерилген эмес.",
              "For CL5500, CL5200, CL7200 and LP-1.6 (and as a fallback for the others): “Scales” window → “Save file for CL-Works”. In CL-Works: “File → Import → CSV”, check the goods, then “Data exchange → Send data”. The column layout comes from common CL-Works export practice; not verified on a live CL-Works.",
              "CL5500, CL5200, CL7200 ve LP-1.6 için (ve diğerleri için yedek yol olarak): «Tartı» penceresi → «CL-Works için dosyayı kaydet». CL-Works'te: «Dosya → İçe aktar → CSV», ürünleri kontrol edin, sonra «Veri alışverişi → Verileri gönder». Sütun düzeni yaygın CL-Works dışa aktarma pratiğinden alındı; canlı CL-Works'te doğrulanmadı.",
              "CL5500, CL5200, CL7200 va LP-1.6 uchun (va boshqalar uchun zaxira yo‘l sifatida): «Tarozi» oynasi → «CL-Works uchun faylni saqlash». CL-Works da: «Fayl → Import → CSV», tovarlarni tekshiring, so‘ng «Ma’lumot almashuvi → Ma’lumotlarni uzatish». Ustunlar tartibi CL-Works eksport amaliyotidan olingan; jonli CL-Works da tekshirilmagan."),
            out var fileBody);
        _csv1251 = new RadioButton { GroupName = "CasCsvEnc", Content = L("Кодировка Windows-1251 (по умолчанию)", "Windows-1251 коддоосу (демейки)", "Windows-1251 encoding (default)", "Windows-1251 kodlaması (varsayılan)", "Windows-1251 kodlashi (odatiy)"), IsChecked = !_s.CsvUtf8 };
        var csvUtf8 = new RadioButton { GroupName = "CasCsvEnc", Content = "UTF-8", IsChecked = _s.CsvUtf8 };
        fileBody.Children.Add(_csv1251);
        fileBody.Children.Add(csvUtf8);
        _csvDot = new RadioButton { GroupName = "CasCsvDec", Content = L("Цена с точкой: 125.50 (по умолчанию)", "Баа чекит менен: 125.50 (демейки)", "Price with a dot: 125.50 (default)", "Fiyat noktalı: 125.50 (varsayılan)", "Narx nuqta bilan: 125.50 (odatiy)"), IsChecked = _s.CsvDecimalSeparator != "," };
        var csvComma = new RadioButton { GroupName = "CasCsvDec", Content = L("Цена с запятой: 125,50", "Баа үтүр менен: 125,50", "Price with a comma: 125,50", "Fiyat virgüllü: 125,50", "Narx vergul bilan: 125,50"), IsChecked = _s.CsvDecimalSeparator == "," };
        fileBody.Children.Add(_csvDot);
        fileBody.Children.Add(csvComma);
        fileBody.Children.Add(MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
        {
            SaveAll();
            ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
        }));
        panel.Children.Add(fileCard);
        return panel;
    }

    // =====================================================================================
    // Штрих-код
    // =====================================================================================

    private Control BuildBarcodeTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Штрих-код этикетки CAS", "CAS этикеткасынын штрих-коду", "CAS label barcode", "CAS etiket barkodu", "CAS yorlig‘i shtrix-kodi"),
            L("Формат штрихкода задаётся на самих весах (меню 1260 «Barcode», общий — 1520). D — отдел (у нас это префикс 20–29), I — код товара, W — вес, P — сумма, C — контрольная цифра. Касса читает 13 цифр: префикс 2 + код + значение + контрольная — формат ниже подходит под раскладку компании. Отдел с номером префикса должен быть заведён на весах.",
              "Штрих-коддун форматы таразанын өзүндө коюлат (1260 «Barcode» менюсу, жалпы — 1520). D — бөлүм (бизде бул 20–29 префикси), I — товар коду, W — салмак, P — сумма, C — текшерүү цифрасы. Касса 13 санды окуйт: 2 префикс + код + маани + текшерүү — төмөнкү формат компаниянын раскладкасына ылайык. Префикс номериндеги бөлүм таразада түзүлгөн болушу керек.",
              "The barcode format is set on the scale itself (menu 1260 “Barcode”, global — 1520). D — department (here: the prefix 20–29), I — item code, W — weight, P — amount, C — check digit. The till reads 13 digits: 2-digit prefix + code + value + check — the format below fits the company layout. A department with the prefix number must exist on the scale.",
              "Barkod biçimi tartının kendisinde ayarlanır (menü 1260 «Barcode», genel — 1520). D — reyon (bizde 20–29 öneki), I — ürün kodu, W — ağırlık, P — tutar, C — kontrol hanesi. Kasa 13 hane okur: 2 önek + kod + değer + kontrol — aşağıdaki biçim şirket düzenine uyar. Önek numaralı reyon tartıda tanımlı olmalı.",
              "Shtrix-kod formati tarozining o‘zida o‘rnatiladi (1260 menyu «Barcode», umumiy — 1520). D — bo‘lim (bizda bu 20–29 prefiksi), I — tovar kodi, W — vazn, P — summa, C — nazorat raqami. Kassa 13 raqamni o‘qiydi: 2 prefiks + kod + qiymat + nazorat — quyidagi format kompaniya tartibiga mos. Prefiks raqamli bo‘lim tarozida yaratilgan bo‘lishi kerak."),
            out var body);
        var companyHint = Text(CompanyFormatText(), "hint");
        body.Children.Add(companyHint);
        body.Children.Add(AmountUnitPicker(this, () => { companyHint.Text = CompanyFormatText(); UpdateSample(); }));

        _formatBox = new ComboBox { MinWidth = 320, FontFamily = new FontFamily("Consolas, Segoe UI") };
        foreach (var f in Formats)
            _formatBox.Items.Add(f);
        _formatBox.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, CasBarcodeFormat.Recommended(WeightBarcodeParser.Layout, !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase))));
        _formatBox.SelectionChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Формат на весах", "Таразадагы формат", "Format on the scale", "Tartıdaki biçim", "Tarozidagi format"), _formatBox));
        _formatHint = Text("", "hint");
        body.Children.Add(_formatHint);

        _prefix = new NumericUpDown { Minimum = 20, Maximum = 29, Increment = 1, FormatString = "00", Value = _s.BarcodePrefix, MinWidth = 140 };
        _prefix.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Префикс (отдел на весах)", "Префикс (таразадагы бөлүм)", "Prefix (department on the scale)", "Önek (tartıdaki reyon)", "Prefiks (tarozidagi bo‘lim)"), _prefix));
        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 999999, Increment = 1, FormatString = "0", Value = 12, MinWidth = 140 };
        _sampleCode.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Код товара для примера", "Мисал үчүн товар коду", "Sample item code", "Örnek ürün kodu", "Namuna tovar kodi"), _sampleCode));
        _sampleGrams = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0", Value = 392, MinWidth = 140 };
        _sampleGrams.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Масса для примера, г", "Мисал үчүн масса, г", "Sample weight, g", "Örnek ağırlık, g", "Namuna uchun massa, g"), _sampleGrams));
        _samplePrice = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0.00", Value = 250, MinWidth = 140 };
        _samplePrice.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Цена за кг, сом", "Кг баасы, сом", "Price per kg, som", "Kg fiyatı, som", "Kg narxi, so‘m"), _samplePrice));

        _sampleText = Themed(this, FontFromScale(new TextBlock { FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Segoe UI") }, "NurFontDisplay"), TextBlock.ForegroundProperty, "BrushText", Brushes.Black);
        body.Children.Add(_sampleText);
        _verdictText = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_verdictText);

        body.Children.Add(ButtonRow(
            MakeButton(L("Рекомендуемый для кассы", "Касса үчүн сунушталган", "Recommended for the till", "Kasa için önerilen", "Kassa uchun tavsiya"), false, (_, _) =>
            {
                var byWeight = !string.Equals(WeightBarcodeParser.Mode, "amount", StringComparison.OrdinalIgnoreCase);
                _formatBox.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, CasBarcodeFormat.Recommended(WeightBarcodeParser.Layout, byWeight)));
                _prefix.Value = byWeight ? 20 : 25;
            }),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveAll();
                ShowResult(L("Сохранено в кассе. Префикс уйдёт на весы номером отдела; формат и отдел задайте на весах (меню 1260, 1520).", "Кассада сакталды. Префикс таразага бөлүм номери катары кетет; формат менен бөлүмдү таразада коюңуз (1260, 1520 менюлары).", "Saved in the till. The prefix goes to the scale as the department number; set the format and the department on the scale (menus 1260, 1520).", "Kasada kaydedildi. Önek tartıya reyon numarası olarak gider; biçimi ve reyonu tartıda ayarlayın (menü 1260, 1520).", "Kassada saqlandi. Prefiks taroziga bo‘lim raqami sifatida ketadi; format va bo‘limni tarozida o‘rnating (1260, 1520 menyu)."), false);
            })));
        panel.Children.Add(card);
        UpdateSample();
        return panel;
    }

    private string SelectedFormat => _formatBox.SelectedIndex >= 0 && _formatBox.SelectedIndex < Formats.Length ? Formats[_formatBox.SelectedIndex] : Formats[0];

    private int PricePoint => _priceDecimalsBox is { SelectedIndex: >= 0 } ? _priceDecimalsBox.SelectedIndex : _s.PricePoint;

    private void UpdateSample()
    {
        if (_verdictText is null || _sampleText is null || _samplePrice is null || _prefix is null || _sampleCode is null || _sampleGrams is null || _formatBox is null)
            return;
        var format = SelectedFormat;
        var byWeight = format.Contains('W');
        var code = (long)(_sampleCode.Value ?? 12);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var decimals = PricePoint;
        var (amount, units) = CasBarcodeFormat.SampleAmount((decimal)(_samplePrice.Value ?? 250), grams, decimals); // 2026-11-14: расчёт вынесен из окна
        var sample = CasBarcodeFormat.BuildSample(format, (int)(_prefix.Value ?? 20), code, byWeight ? grams : units);
        _sampleText.Text = sample ?? "—";
        _formatHint.Text = L($"Задайте на весах: {format} (код — {CasBarcodeFormat.CodeDigits(format)} цифр, значение — {CasBarcodeFormat.ValueDigits(format)}). Раскладка компании: «{(string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? "по коду" : "по PLU")}».",
            $"Таразада коюңуз: {format} (код — {CasBarcodeFormat.CodeDigits(format)} сан, маани — {CasBarcodeFormat.ValueDigits(format)}). Компаниянын раскладкасы: «{(string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? "код боюнча" : "PLU боюнча")}».",
            $"Set on the scale: {format} (code — {CasBarcodeFormat.CodeDigits(format)} digits, value — {CasBarcodeFormat.ValueDigits(format)}). Company layout: “{(string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? "by code" : "by PLU")}”.",
            $"Tartıda ayarlayın: {format} (kod — {CasBarcodeFormat.CodeDigits(format)} hane, değer — {CasBarcodeFormat.ValueDigits(format)}). Şirket düzeni: «{(string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? "koda göre" : "PLU'ya göre")}».",
            $"Tarozida o‘rnating: {format} (kod — {CasBarcodeFormat.CodeDigits(format)} raqam, qiymat — {CasBarcodeFormat.ValueDigits(format)}). Kompaniya tartibi: «{(string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? "kod bo‘yicha" : "PLU bo‘yicha")}».");
        if (sample is null)
        {
            SetVerdict(false, L("код или значение не помещаются в поля формата — весы напечатают этикетку без штрихкода или с обрезанным",
                "код же маани форматтын талааларына батпайт — тараза этикетканы штрих-коддусуз же кесилген штрих-код менен басат",
                "the code or value does not fit the format fields — the scale prints the label without a barcode or with a truncated one",
                "kod veya değer biçim alanlarına sığmıyor — tartı etiketi barkodsuz veya kesik barkodla basar",
                "kod yoki qiymat format maydonlariga sig‘maydi — tarozi yorliqni shtrix-kodsiz yoki qirqilgan shtrix-kod bilan chop etadi"));
            return;
        }
        var (ok, message) = VerifyWithKassa(sample, code, byWeight, grams, amount);
        SetVerdict(ok, message);
    }

    private void SetVerdict(bool ok, string message)
    {
        _verdictText.Text = (ok ? "✓ " : "⚠ " + L("Касса НЕ узнает товар: ", "Касса товарды ТААНЫБАЙТ: ", "The till will NOT recognise the item: ", "Kasa ürünü TANIMAZ: ", "Kassa tovarni TANIMAYDI: ")) + message;
        Themed(this, _verdictText, TextBlock.ForegroundProperty, ok ? "BrushSuccess" : "BrushWarning", ok ? Brushes.Green : Brushes.DarkOrange);
    }

    // =====================================================================================
    // Для мастера
    // =====================================================================================

    private void BuildMasterTab()
    {
        var panel = _masterPanel;
        var card = Card(L("Порт и протокол", "Порт жана протокол", "Port and protocol", "Port ve protokol", "Port va protokol"),
            L("Весы CL5000J / CL3000: TCP, порт 20304 (так в CL-Works; задаётся на весах, меню 1913). Кадр: код команды из двух букв, адрес, длина, данные, контрольная сумма (документ CAS «CL5000J Communication Protocol», 2007). «Проверить связь» — только чтение состояния (команда «RN»), на весах ничего не меняется. Весь обмен пишется в журнал.",
              "CL5000J / CL3000 таразалары: TCP, 20304 порту (CL-Works'то ушундай; таразада коюлат, 1913 меню). Кадр: эки тамгалуу буйрук коду, дарек, узундук, маалымат, контролдук сумма (CAS документи «CL5000J Communication Protocol», 2007). «Байланышты текшерүү» — абалды гана окуу («RN» буйругу), таразада эч нерсе өзгөрбөйт. Бүт алмашуу журналга жазылат.",
              "CL5000J / CL3000 scales: TCP, port 20304 (as in CL-Works; set on the scale, menu 1913). Frame: a two-letter command code, address, length, data, checksum (CAS document “CL5000J Communication Protocol”, 2007). “Check connection” only reads the status (command “RN”) — nothing changes on the scale. The whole exchange is written to a log.",
              "CL5000J / CL3000 tartılar: TCP, port 20304 (CL-Works'teki gibi; tartıda ayarlanır, menü 1913). Çerçeve: iki harfli komut kodu, adres, uzunluk, veri, sağlama toplamı (CAS belgesi «CL5000J Communication Protocol», 2007). «Bağlantıyı kontrol et» yalnızca durumu okur (komut «RN») — tartıda hiçbir şey değişmez. Tüm iletişim günlüğe yazılır.",
              "CL5000J / CL3000 tarozilari: TCP, 20304 port (CL-Works da shunday; tarozida o‘rnatiladi, 1913 menyu). Kadr: ikki harfli buyruq kodi, manzil, uzunlik, ma’lumot, nazorat yig‘indisi (CAS hujjati «CL5000J Communication Protocol», 2007). «Aloqani tekshirish» faqat holatni o‘qiydi («RN» buyrug‘i) — tarozida hech narsa o‘zgarmaydi. Butun almashuv jurnalga yoziladi."),
            out var body);
        body.Children.Add(Row(L("TCP-порт", "TCP-порт", "TCP port", "TCP portu", "TCP port"), _portBox));
        body.Children.Add(ButtonRow(
            MakeButton(L("Журнал обмена", "Алмашуу журналы", "Exchange log", "İletişim günlüğü", "Almashuv jurnali"), false, (_, _) => OpenExchangeLog()),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveAll();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        panel.Children.Add(card);

        panel.Children.Add(Card(L("Как включить сеть на весах CAS", "CAS таразасында тармакты кантип күйгүзүү", "How to enable the network on a CAS scale", "CAS tartıda ağ nasıl açılır", "CAS tarozisida tarmoqni qanday yoqish"),
            L("На весах: меню 1913 (TCP/IP) — IP-адрес из сети кассы (первые три числа как у компьютера), маска, шлюз и порт (по умолчанию 20304). Формат штрихкода: меню 1260 (создать формат) и 1520 (общий формат). Отдел с номером префикса и формат этикетки заводятся на весах или в CL-Works — касса их не пишет.",
              "Таразада: 1913 меню (TCP/IP) — кассанын тармагындагы IP-дарек (алгачкы үч сан компьютердикиндей), маска, шлюз жана порт (демейки 20304). Штрих-код форматы: 1260 меню (формат түзүү) жана 1520 (жалпы формат). Префикс номериндеги бөлүм менен этикетка форматы таразада же CL-Works'то түзүлөт — касса аларды жазбайт.",
              "On the scale: menu 1913 (TCP/IP) — an IP address from the till's network (the first three numbers like the computer's), mask, gateway and port (20304 by default). Barcode format: menu 1260 (create a format) and 1520 (global format). The department with the prefix number and the label format are created on the scale or in CL-Works — the till does not write them.",
              "Tartıda: menü 1913 (TCP/IP) — kasa ağından bir IP adresi (ilk üç sayı bilgisayarınki gibi), maske, ağ geçidi ve port (varsayılan 20304). Barkod biçimi: menü 1260 (biçim oluştur) ve 1520 (genel biçim). Önek numaralı reyon ve etiket biçimi tartıda veya CL-Works'te oluşturulur — kasa onları yazmaz.",
              "Tarozida: 1913 menyu (TCP/IP) — kassa tarmog‘idan IP manzil (dastlabki uch son kompyuterniki kabi), niqob, shlyuz va port (odatda 20304). Shtrix-kod formati: 1260 menyu (format yaratish) va 1520 (umumiy format). Prefiks raqamli bo‘lim va yorliq formati tarozida yoki CL-Works da yaratiladi — kassa ularni yozmaydi."),
            out _));

        var netCard = Card(L("Сеть компьютера и весов", "Компьютердин жана таразанын тармагы", "Computer and scale network", "Bilgisayar ve tartı ağı", "Kompyuter va tarozi tarmog‘i"),
            L("Компьютер и весы должны быть в одной сети: первые три числа адреса совпадают (при маске 255.255.255.0).",
              "Компьютер менен тараза бир тармакта болушу керек: даректин алгачкы үч саны дал келет (255.255.255.0 маскасында).",
              "The computer and the scale must be on the same network: the first three numbers of the address match (with mask 255.255.255.0).",
              "Bilgisayar ve tartı aynı ağda olmalı: adresin ilk üç sayısı aynı olmalı (255.255.255.0 maskesiyle).",
              "Kompyuter va tarozi bir tarmoqda bo‘lishi kerak: manzilning dastlabki uch soni mos keladi (255.255.255.0 niqobida)."),
            out var netBody);
        try
        {
            _subnets = ScaleNetworkScanner.GetLocalSubnets();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CAS: список сетей компьютера не получен: {ex.Message}", "SCALES");
        }
        _networkText = Themed(this, new TextBlock { FontFamily = new FontFamily("Consolas, Segoe UI"), TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "BrushText", Brushes.Black);
        netBody.Children.Add(new Border { Classes = { "card" }, Child = _networkText });
        panel.Children.Add(netCard);
        UpdateNetwork();
    }

    private void UpdateNetwork()
    {
        if (_networkText is null)
            return;
        var lines = new List<string>();
        var ipText = (_ipBox?.Text ?? "").Trim();
        var hasIp = IPAddress.TryParse(ipText, out var scaleIp);
        lines.Add(L("IP весов: ", "Таразанын IP'си: ", "Scale IP: ", "Tartı IP'si: ", "Tarozi IP'si: ") + (hasIp ? ipText : "—") + $"   TCP {(_portBox?.Value is { } p ? (int)p : CasProtocol.DefaultPort)}");
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

    /// <summary>Открывает журнал обмена с весами (cas-exchange.log) программой по умолчанию.</summary>
    private void OpenExchangeLog()
    {
        var path = CasScaleService.ExchangeLogPath;
        if (!File.Exists(path))
        {
            ShowResult(L("Журнала ещё нет — он появится после первой проверки связи или отправки: ", "Журнал азырынча жок — ал биринчи текшерүүдөн же жөнөтүүдөн кийин пайда болот: ", "There is no log yet — it appears after the first check or upload: ", "Henüz günlük yok — ilk kontrol veya gönderimden sonra oluşur: ", "Jurnal hali yo‘q — birinchi tekshiruv yoki yuborishdan keyin paydo bo‘ladi: ") + path, false);
            return;
        }
        if (!PlatformLauncher.OpenFile(path))
            ShowResult(path, true);
    }

    // =====================================================================================
    // Сохранение
    // =====================================================================================

    private void SaveConnection()
    {
        var s = CasScaleSettings.Current;
        s.Ip = (_ipBox.Text ?? "").Trim();
        s.Port = (int)(_portBox.Value ?? CasProtocol.DefaultPort);
        s.Model = _modelBox.SelectedIndex >= 0 && _modelBox.SelectedIndex < CasScaleSettings.Models.Length ? CasScaleSettings.Models[_modelBox.SelectedIndex] : CasScaleSettings.ModelCl5000J;
        CasScaleSettings.Save(s);
        UserPreferences.Instance.SaveToDisk();
        _s = CasScaleSettings.Current;
        // 2026-11-13: профиль весов (список «Настройки → Весы») хранит адрес и порт этой марки
        if (UserPreferences.Instance.ScaleBrand == BrandCas)
            LabelScaleStore.CaptureActive();
    }

    private void SaveAll()
    {
        SaveConnection();
        var s = CasScaleSettings.Current;
        if (_prefix is not null)
            s.BarcodePrefix = (int)(_prefix.Value ?? 20);
        if (_priceDecimalsBox is not null)
            s.PricePoint = Math.Clamp(_priceDecimalsBox.SelectedIndex, 0, 3);
        if (_barcodeNumber is not null)
            s.BarcodeNumber = (int)(_barcodeNumber.Value ?? 0);
        if (_labelNumber is not null)
            s.LabelNumber = (int)(_labelNumber.Value ?? 0);
        if (_csv1251 is not null)
            s.CsvUtf8 = _csv1251.IsChecked != true;
        if (_csvDot is not null)
            s.CsvDecimalSeparator = _csvDot.IsChecked == true ? "." : ",";
        CasScaleSettings.Save(s);
        UserPreferences.Instance.SaveToDisk();
        _s = CasScaleSettings.Current;
    }

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        Themed(this, ResultBorder, Border.BorderBrushProperty, isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        Themed(this, ResultBorder, Border.BackgroundProperty, isError ? "BrushWarningSoft" : "BrushPanelSoft", Brushes.Transparent);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        // как у остальных окон весов: закрытие сохраняет — владелец не обязан жать «Сохранить»
        SaveAll();
        Close();
    }
}
