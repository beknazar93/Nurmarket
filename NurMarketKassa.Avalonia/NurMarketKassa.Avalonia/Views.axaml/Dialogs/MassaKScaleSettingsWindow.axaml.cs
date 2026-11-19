using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-13: «Настройки весов Масса-К» (серии R и SL). Решение владельца 13.11: добавить весы Масса-К. Протокол открытый (massa.ru: «Описание форматов файлов
/// и протокола обмена данными с терминалами серии R», ред. 6.44; «Протокол обмена весов серии SL»; «Весы Масса-К. Подключение к компьютеру»), разбор —
/// <see cref="MassaKProtocol"/>; ВП/ВПМ — другое поколение, пока не поддерживается (нужна запись обмена с живых весов); MK-A21 — весы без печати этикеток.
/// Вкладки: «Быстрая настройка» (три шага, общий блок <see cref="ScaleUi.QuickSetup"/>) / «Загрузка товаров» / «Штрих-код» (<see cref="ShowBarcodeTab"/>) / «Для мастера».
/// Настройки — UserPreferences.MassaK*. НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО: пока владелец не отметит «проверено», отправка идёт с подтверждением.
/// </summary>
public partial class MassaKScaleSettingsWindow : Window
{
    private TextBox _ipBox = null!;
    private NumericUpDown _portBox = null!;
    private ScaleQuickView _quick = null!;
    private readonly StackPanel _masterPanel = new() { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };

    // Загрузка товаров.
    private NumericUpDown _prefix = null!;
    private NumericUpDown _shelfLife = null!;
    private CheckBox _latin = null!;

    // Штрих-код.
    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private TextBlock _sampleText = null!;
    private TextBlock _verdictText = null!;

    // Для мастера.
    private CheckBox _verified = null!;
    private CheckBox _skipConfirm = null!;
    private IReadOnlyList<LocalSubnet> _subnets = Array.Empty<LocalSubnet>();
    private TextBlock _networkText = null!;

    public MassaKScaleSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FitToKassaScreen();
        Title = L("Настройки весов Масса-К", "Масса-К таразасынын жөндөөлөрү", "Massa-K scale settings", "Massa-K tartı ayarları", "Massa-K tarozi sozlamalari");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Весы Масса-К (серии R и SL, например MK-RP10, MK-RL10): касса сама пишет весовые товары на весы. Пройдите три шага ниже; первую отправку делайте на одном товаре — на живых весах это ещё не проверено.",
            "Масса-К таразалары (R жана SL сериялары, мисалы MK-RP10, MK-RL10): касса салмактуу товарларды таразага өзү жазат. Төмөнкү үч кадамды аткарыңыз; биринчи жөнөтүүнү бир товар менен жасаңыз — жандуу таразада бул азырынча текшерилген эмес.",
            "Massa-K scales (series R and SL, e.g. MK-RP10, MK-RL10): the till writes the weighed goods to the scale itself. Follow the three steps below; send one item first — this is not verified on live scales yet.",
            "Massa-K tartılar (R ve SL serisi, ör. MK-RP10, MK-RL10): kasa tartılı ürünleri tartıya kendisi yazar. Aşağıdaki üç adımı izleyin; ilk gönderimi tek ürünle yapın — canlı tartıda bu henüz doğrulanmadı.",
            "Massa-K tarozilari (R va SL seriyalari, masalan MK-RP10, MK-RL10): kassa vaznli tovarlarni taroziga o‘zi yozadi. Quyidagi uch qadamni bajaring; birinchi yuborishni bitta tovar bilan qiling — jonli tarozida bu hali tekshirilmagan.");

        Tabs.Items.Add(MakeTab(QuickTabTitle, BuildQuickTab()));
        Tabs.Items.Add(MakeTab(L("Загрузка товаров", "Товарларды жүктөө", "Uploading goods", "Ürün yükleme", "Tovarlarni yuklash"), BuildUploadTab()));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        BuildMasterTab();
        Tabs.Items.Add(MakeTab(MasterTabTitle, _masterPanel));
        UpdateHeader();
        ShowResult(L("Введите IP весов и нажмите «Проверить связь».", "Таразанын IP'син киргизип «Байланышты текшерүү» басыңыз.", "Enter the scale IP and press “Check connection”.", "Tartı IP'sini girin ve «Bağlantıyı kontrol et»e basın.", "Tarozi IP'sini kiriting va «Aloqani tekshirish»ni bosing."), false);
    }

    /// <summary>Открыть сразу на вкладке «Штрих-код» (кнопка «Проверить этикетку» первой вкладки).</summary>
    public void ShowBarcodeTab() => Tabs.SelectedIndex = 2;

    private static TabItem MakeTab(string header, Control content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content } };

    private int Port => (int)(_portBox.Value ?? MassaKProtocol.DefaultPort);

    private void UpdateHeader()
    {
        var ip = (_ipBox.Text ?? "").Trim();
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ") + (ip.Length > 0 ? ip : "—"); // как у остальных окон весов: без номера порта — он во вкладке «Для мастера»
    }

    // =====================================================================================
    // Быстрая настройка
    // =====================================================================================

    private Control BuildQuickTab()
    {
        var prefs = UserPreferences.Instance;
        _ipBox = new TextBox { Text = prefs.MassaKScaleIp ?? "", Watermark = "192.168.0.150", MinWidth = 220 };
        _ipBox.LostFocus += (_, _) => { SaveConnection(); UpdateHeader(); UpdateNetwork(); };
        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = prefs.MassaKScalePort, MinWidth = 140 };
        _portBox.ValueChanged += (_, _) => { UpdateHeader(); UpdateNetwork(); };

        var modelCard = Card(L("Какие весы подходят", "Кайсы тараза ылайыктуу", "Which scales fit", "Hangi tartılar uygun", "Qaysi tarozi mos"), null, out var modelBody);
        modelBody.Children.Add(Text(L("✓ Серии R и SL (терминалы RP, R2P, RL, R2L; MK-RP10, MK-RL10 и подобные): касса пишет товары сама.",
            "✓ R жана SL сериялары (RP, R2P, RL, R2L терминалдары; MK-RP10, MK-RL10 жана ушул сыяктуу): касса товарларды өзү жазат.",
            "✓ Series R and SL (terminals RP, R2P, RL, R2L; MK-RP10, MK-RL10 and similar): the till writes the goods itself.",
            "✓ R ve SL serisi (RP, R2P, RL, R2L terminalleri; MK-RP10, MK-RL10 ve benzerleri): kasa ürünleri kendisi yazar.",
            "✓ R va SL seriyalari (RP, R2P, RL, R2L terminallari; MK-RP10, MK-RL10 va shunga o‘xshash): kassa tovarlarni o‘zi yozadi.")));
        modelBody.Children.Add(Text(L("— ВП и ВПМ (старое поколение): пока не поддерживается, нужна запись обмена с живых весов.",
            "— ВП жана ВПМ (эски муун): азырынча колдоого алынбайт, жандуу таразадан алмашуунун жазуусу керек.",
            "— VP and VPM (old generation): not supported yet, a recording of the exchange with live scales is needed.",
            "— VP ve VPM (eski nesil): henüz desteklenmiyor, canlı tartılarla iletişim kaydı gerekiyor.",
            "— VP va VPM (eski avlod): hozircha qo‘llab-quvvatlanmaydi, jonli tarozilar bilan almashuv yozuvi kerak.")));
        modelBody.Children.Add(Text(L("— MK-A21 и другие МК-А: весы без печати этикеток, товары на них не загружаются (их используют только для веса).",
            "— MK-A21 жана башка МК-А: этикетка басбаган таразалар, товарлар аларга жүктөлбөйт (алар салмак үчүн гана колдонулат).",
            "— MK-A21 and other МК-А: scales without label printing, goods are not loaded to them (they are used for weight only).",
            "— MK-A21 ve diğer МК-А: etiket basmayan tartılar, ürünler onlara yüklenmez (yalnızca ağırlık için kullanılır).",
            "— MK-A21 va boshqa МК-А: yorliq chop etmaydigan tarozilar, tovarlar ularga yuklanmaydi (faqat vazn uchun ishlatiladi).")));

        _quick = QuickSetup(this, new ScaleQuickOptions
        {
            Brand = BrandMassaK,
            WhereLabel = L("Адрес весов (IP)", "Таразанын дареги (IP)", "Scale address (IP)", "Tartı adresi (IP)", "Tarozi manzili (IP)"),
            WhereBox = _ipBox,
            Save = () => { SaveAll(); UpdateHeader(); UpdateNetwork(); },
            SendGoods = () => { SaveAll(); OpenScalesWindow(this, BrandMassaK); },
            CheckLabel = ShowBarcodeTab,
            ShowResult = ShowResult,
        });

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        panel.Children.Add(_quick.Root);
        _quick.Root.Margin = default;
        panel.Children.Add(modelCard);
        return panel;
    }

    // =====================================================================================
    // Загрузка товаров
    // =====================================================================================

    private Control BuildUploadTab()
    {
        var prefs = UserPreferences.Instance;
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Как касса заполняет весы Масса-К", "Касса Масса-К таразасын кантип толтурат", "How the till fills a Massa-K scale", "Kasa Massa-K tartıyı nasıl doldurur", "Kassa Massa-K tarozisini qanday to‘ldiradi"),
            L("Окно «Весы» → марка Масса-К → отметьте весовые товары → «Отправить на весы»: касса пишет по две записи на товар (товар и PLU), справочник весов не стирается. Номер PLU закреплён за товаром (колонка PLU) и печатается в штрихкоде этикетки. Штучные товары пока не отправляются: у них Масса-К печатает в штрихкоде количество и префикс на единицу больше.",
              "«Таразалар» терезеси → Масса-К маркасы → салмактуу товарларды белгилеңиз → «Таразага жөнөтүү»: касса ар бир товарга эки жазуу жазат (товар жана PLU), тараза справочниги өчүрүлбөйт. PLU номери товарга бекитилген (PLU тилкеси) жана этикетка штрих-кодунда басылат. Дааналык товарлар азырынча жөнөтүлбөйт: аларда Масса-К штрих-кодго санды жана бирге чоң префиксти басат.",
              "“Scales” window → brand Massa-K → tick the weighed goods → “Send to scale”: the till writes two records per product (product and PLU), the scale's directory is not erased. The PLU number is fixed per product (PLU column) and printed in the label barcode. Piece goods are not sent yet: Massa-K prints the quantity and a prefix one higher in their barcode.",
              "«Tartı» penceresi → Massa-K markası → tartılı ürünleri işaretleyin → «Tartıya gönder»: kasa ürün başına iki kayıt yazar (ürün ve PLU), tartının rehberi silinmez. PLU numarası ürüne sabittir (PLU sütunu) ve etiket barkoduna basılır. Adetli ürünler henüz gönderilmez: bunlarda Massa-K barkoda adedi ve bir fazla öneki basar.",
              "«Tarozi» oynasi → Massa-K markasi → vaznli tovarlarni belgilang → «Taroziga yuborish»: kassa har bir tovarga ikkita yozuv yozadi (tovar va PLU), tarozi ma’lumotnomasi o‘chirilmaydi. PLU raqami tovarga biriktirilgan (PLU ustuni) va yorliq shtrix-kodida bosiladi. Donali tovarlar hozircha yuborilmaydi: ularda Massa-K shtrix-kodga sonni va bittaga katta prefiksni bosadi."),
            out var body);

        _prefix = new NumericUpDown { Minimum = 20, Maximum = 29, Increment = 1, FormatString = "00", Value = Math.Clamp(prefs.MassaKBarcodePrefix, 20, 29), MinWidth = 140 };
        _prefix.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Префикс штрихкода (20–29)", "Штрих-код префикси (20–29)", "Barcode prefix (20–29)", "Barkod öneki (20–29)", "Shtrix-kod prefiksi (20–29)"), _prefix));
        body.Children.Add(Text(L("Касса пишет префикс в карточку товара на весах; шаблон этикетки весов подставляет его в начало штрихкода. Для весов обычно 20.",
            "Касса префиксти таразадагы товар карточкасына жазат; тараза этикеткасынын шаблону аны штрих-коддун башына коёт. Таразалар үчүн адатта 20.",
            "The till writes the prefix into the product card on the scale; the scale's label template puts it at the start of the barcode. Usually 20.",
            "Kasa öneki tartıdaki ürün kartına yazar; tartının etiket şablonu onu barkodun başına koyar. Genellikle 20.",
            "Kassa prefiksni tarozidagi tovar kartochkasiga yozadi; tarozi yorlig‘i shabloni uni shtrix-kod boshiga qo‘yadi. Odatda 20."), "hint"));

        _shelfLife = new NumericUpDown { Minimum = 0, Maximum = 999, Increment = 1, FormatString = "0", Value = prefs.MassaKShelfLifeDays, MinWidth = 140 };
        body.Children.Add(Row(L("Срок годности, дней (0 — не задан)", "Жарактуулук мөөнөтү, күн (0 — коюлган эмес)", "Shelf life, days (0 — not set)", "Raf ömrü, gün (0 — ayarlı değil)", "Yaroqlilik muddati, kun (0 — belgilanmagan)"), _shelfLife));
        body.Children.Add(Text(L("Одинаковый для всех отправляемых товаров; на весах хранится в минутах.", "Бардык жөнөтүлүүчү товарлар үчүн бирдей; тарозидо мүнөттө сакталат.", "The same for all sent goods; stored on the scale in minutes.", "Gönderilen tüm ürünler için aynı; tartıda dakika olarak saklanır.", "Yuboriladigan barcha tovarlar uchun bir xil; tarozida daqiqada saqlanadi."), "hint"));

        _latin = new CheckBox { IsChecked = prefs.MassaKLatinNames, Content = Text(L("Название товара латиницей (если на этикетке вместо букв мусор)", "Товардын аталышы латын тамгалары менен (этикеткада тамгалардын ордуна таштанды чыкса)", "Product name in Latin letters (if the label prints garbage instead of letters)", "Ürün adı Latin harfleriyle (etiket harf yerine bozuk karakter basarsa)", "Tovar nomi lotin harflarida (yorliqda harflar o‘rniga axlat chiqsa)")) };
        body.Children.Add(_latin);
        body.Children.Add(Text(L("По умолчанию — кириллица (Windows-1251); кыргызские буквы ү, ө, ң заменяются на у, о, н.", "Демейки — кириллица (Windows-1251); кыргыз тамгалары ү, ө, ң — у, о, н менен алмаштырылат.", "By default Cyrillic (Windows-1251); the Kyrgyz letters ү, ө, ң are replaced by у, о, н.", "Varsayılan Kiril (Windows-1251); Kırgız harfleri ү, ө, ң у, о, н ile değiştirilir.", "Odatda kirill (Windows-1251); qirg‘iz harflari ү, ө, ң — у, о, н ga almashtiriladi."), "hint"));

        body.Children.Add(ButtonRow(MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
        {
            SaveAll();
            ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
        })));
        panel.Children.Add(card);
        return panel;
    }

    // =====================================================================================
    // Штрих-код
    // =====================================================================================

    private static bool ByCodeLayout => string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);

    private Control BuildBarcodeTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var card = Card(L("Штрих-код этикетки Масса-К", "Масса-К этикеткасынын штрих-коду", "Massa-K label barcode", "Massa-K etiket barkodu", "Massa-K yorlig‘i shtrix-kodi"),
            L("Формат штрихкода задаёт шаблон этикетки на самих весах (редактор этикеток Масса-К или шаблон LITE: префикс 20–26 и тип штрихкода). Касса понимает такие 13 цифр: «префикс + PLU (5) + вес в граммах (5) + контрольная» или «префикс + PLU (6) + вес (4) + контрольная» — по раскладке компании. Штрихкод должен печатать номер PLU (поле «PLU товара»), а не код товара.",
              "Штрих-кодтун форматын таразадагы этикетка шаблону берет (Масса-К этикетка редактору же LITE шаблону: 20–26 префикси жана штрих-код түрү). Касса мындай 13 санды түшүнөт: «префикс + PLU (5) + граммдагы салмак (5) + текшерүү» же «префикс + PLU (6) + салмак (4) + текшерүү» — компаниянын раскладкасы боюнча. Штрих-код PLU номерин («Товардын PLU» талаасы) басышы керек, товар кодун эмес.",
              "The barcode format is set by the label template on the scale itself (the Massa-K label editor or the LITE template: prefix 20–26 and the barcode type). The till understands these 13 digits: “prefix + PLU (5) + weight in grams (5) + check” or “prefix + PLU (6) + weight (4) + check” — by the company layout. The barcode must print the PLU number (the “Product PLU” field), not the product code.",
              "Barkod biçimini tartıdaki etiket şablonu belirler (Massa-K etiket düzenleyicisi veya LITE şablonu: 20–26 öneki ve barkod türü). Kasa şu 13 haneyi anlar: «önek + PLU (5) + gram cinsinden ağırlık (5) + kontrol» veya «önek + PLU (6) + ağırlık (4) + kontrol» — şirket düzenine göre. Barkod ürün kodunu değil, PLU numarasını («Ürün PLU» alanı) basmalıdır.",
              "Shtrix-kod formatini tarozidagi yorliq shabloni belgilaydi (Massa-K yorliq muharriri yoki LITE shabloni: 20–26 prefiks va shtrix-kod turi). Kassa bunday 13 raqamni tushunadi: «prefiks + PLU (5) + grammdagi vazn (5) + nazorat» yoki «prefiks + PLU (6) + vazn (4) + nazorat» — kompaniya tartibi bo‘yicha. Shtrix-kod tovar kodini emas, PLU raqamini («Tovar PLU» maydoni) chop etishi kerak."),
            out var body);
        var companyHint = Text(CompanyFormatText(), "hint");
        body.Children.Add(companyHint);
        body.Children.Add(AmountUnitPicker(this, () => { companyHint.Text = CompanyFormatText(); UpdateSample(); }));

        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 999999, Increment = 1, FormatString = "0", Value = 12, MinWidth = 140 };
        _sampleCode.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("PLU товара для примера", "Мисал үчүн товардын PLU'су", "Sample product PLU", "Örnek ürün PLU'su", "Namuna tovar PLU'si"), _sampleCode));
        _sampleGrams = new NumericUpDown { Minimum = 1, Maximum = 99999, Increment = 1, FormatString = "0", Value = 392, MinWidth = 140 };
        _sampleGrams.ValueChanged += (_, _) => UpdateSample();
        body.Children.Add(Row(L("Масса для примера, г", "Мисал үчүн масса, г", "Sample weight, g", "Örnek ağırlık, g", "Namuna uchun massa, g"), _sampleGrams));

        _sampleText = Themed(this, FontFromScale(new TextBlock { FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Segoe UI") }, "NurFontDisplay"), TextBlock.ForegroundProperty, "BrushText", Brushes.Black);
        body.Children.Add(_sampleText);
        _verdictText = new TextBlock { Classes = { "label" }, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_verdictText);
        panel.Children.Add(card);
        UpdateSample();
        return panel;
    }

    private void UpdateSample()
    {
        if (_verdictText is null || _sampleText is null || _prefix is null || _sampleCode is null || _sampleGrams is null)
            return;
        var plu = (int)(_sampleCode.Value ?? 12);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var prefix = (int)(_prefix.Value ?? 20);
        var byCode = ByCodeLayout;
        var maxPlu = byCode ? 999_999 : 99_999;
        var maxGrams = byCode ? 9_999 : 99_999;
        if (plu > maxPlu || grams > maxGrams)
        {
            _sampleText.Text = "—";
            SetVerdict(false, L("PLU или вес не помещаются в поля штрихкода — весы напечатают этикетку без штрихкода или с обрезанным",
                "PLU же салмак штрих-коддун талааларына батпайт — тараза этикетканы штрих-коддусуз же кесилген штрих-код менен басат",
                "the PLU or weight does not fit the barcode fields — the scale prints the label without a barcode or with a truncated one",
                "PLU veya ağırlık barkod alanlarına sığmıyor — tartı etiketi barkodsuz veya kesik barkodla basar",
                "PLU yoki vazn shtrix-kod maydonlariga sig‘maydi — tarozi yorliqni shtrix-kodsiz yoki qirqilgan shtrix-kod bilan chop etadi"));
            return;
        }
        var body = byCode ? $"{prefix:00}{plu:000000}{grams:0000}" : $"{prefix:00}{plu:00000}{grams:00000}";
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var sample = body + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
        _sampleText.Text = sample;
        var (ok, message) = VerifyWithKassa(sample, plu, true, grams, 0m);
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
        var prefs = UserPreferences.Instance;
        var panel = _masterPanel;
        var card = Card(L("Порт и протокол", "Порт жана протокол", "Port and protocol", "Port ve protokol", "Port va protokol"),
            L("TCP, порт 5001 (так в документе «Подключение к компьютеру»; если в меню весов порта нет — весы слушают 5001). Кадр: F8 55 CE, длина, команда, данные, CRC16 (документы Масса-К, терминалы серии R ред. 6.44 и серия SL). «Проверить связь» — только чтение: серийный номер, состояние файлов, масса на платформе. Запись — одиночные записи в файл товаров (101) и файл PLU (105); стирание файлов касса не отправляет никогда. Весь обмен пишется в журнал.",
              "TCP, 5001 порту («Компьютерге туташуу» документинде ушундай; тараза менюсунда порт жок болсо — тараза 5001 портун угат). Кадр: F8 55 CE, узундук, буйрук, маалымат, CRC16 (Масса-К документтери, R сериясындагы терминалдар ред. 6.44 жана SL сериясы). «Байланышты текшерүү» — окуу гана: сериялык номер, файлдардын абалы, платформадагы масса. Жазуу — товарлар файлына (101) жана PLU файлына (105) жеке жазуулар; файлдарды өчүрүүнү касса эч качан жөнөтпөйт. Бүт алмашуу журналга жазылат.",
              "TCP, port 5001 (as in the document “Connecting to a computer”; if the scale menu has no port, the scale listens on 5001). Frame: F8 55 CE, length, command, data, CRC16 (Massa-K documents, terminals of series R rev. 6.44 and series SL). “Check connection” only reads: the serial number, the file state, the weight on the platform. Writing — single records into the goods file (101) and the PLU file (105); the till never sends file erasing. The whole exchange is written to a log.",
              "TCP, port 5001 («Bilgisayara bağlantı» belgesinde böyle; tartı menüsünde port yoksa tartı 5001'i dinler). Çerçeve: F8 55 CE, uzunluk, komut, veri, CRC16 (Massa-K belgeleri, R serisi terminaller sürüm 6.44 ve SL serisi). «Bağlantıyı kontrol et» yalnızca okur: seri numarası, dosya durumu, platformdaki ağırlık. Yazma — ürün dosyasına (101) ve PLU dosyasına (105) tek kayıtlar; kasa dosya silmeyi asla göndermez. Tüm iletişim günlüğe yazılır.",
              "TCP, 5001 port («Kompyuterga ulash» hujjatida shunday; tarozi menyusida port bo‘lmasa — tarozi 5001 ni tinglaydi). Kadr: F8 55 CE, uzunlik, buyruq, ma’lumot, CRC16 (Massa-K hujjatlari, R seriyasi terminallari 6.44-tahrir va SL seriyasi). «Aloqani tekshirish» faqat o‘qiydi: seriya raqami, fayllar holati, platformadagi vazn. Yozish — tovarlar fayliga (101) va PLU fayliga (105) alohida yozuvlar; fayllarni o‘chirishni kassa hech qachon yubormaydi. Butun almashuv jurnalga yoziladi."),
            out var body);
        body.Children.Add(Row(L("TCP-порт", "TCP-порт", "TCP port", "TCP portu", "TCP port"), _portBox));
        body.Children.Add(ButtonRow(
            MakeButton(L("Журнал обмена", "Алмашуу журналы", "Exchange log", "İletişim günlüğü", "Almashuv jurnali"), false, (_, _) => OpenExchangeLog()),
            MakeButton(L("Пробный пакет в журнал", "Сыноо пакетти журналга", "Trial packet to the log", "Deneme paketini günlüğe", "Sinov paketini jurnalga"), false, async (_, _) => await TrialPacketAsync().ConfigureAwait(true)),
            MakeButton(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), false, (_, _) =>
            {
                SaveAll();
                ShowResult(L("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."), false);
            })));
        body.Children.Add(Text(L("«Пробный пакет» записывает в журнал кадры для тестового товара «Тест Масса-К» (PLU 1) — в сеть ничего не уходит.",
            "«Сыноо пакет» журналга «Тест Масса-К» сыноо товарынын (PLU 1) кадрларын жазат — тармакка эч нерсе кетпейт.",
            "“Trial packet” writes the frames for the test product “Тест Масса-К” (PLU 1) to the log — nothing goes to the network.",
            "«Deneme paketi» «Тест Масса-К» deneme ürününün (PLU 1) çerçevelerini günlüğe yazar — ağa hiçbir şey gitmez.",
            "«Sinov paketi» «Тест Масса-К» sinov tovari (PLU 1) kadrlarini jurnalga yozadi — tarmoqqa hech narsa ketmaydi."), "hint"));
        panel.Children.Add(card);

        var verifyCard = Card(L("Проверено на живых весах", "Жандуу таразада текшерилди", "Verified on a live scale", "Canlı tartıda doğrulandı", "Jonli tarozida tekshirildi"),
            L("Пока не отмечено, касса перед каждой отправкой на Масса-К спрашивает подтверждение, а автоотправка выключена. Отметьте после первой удачной отправки: на весах верные название и цена, этикетка печатается, касса читает её штрихкод.",
              "Белгиленбегенче касса Масса-К'га ар бир жөнөтүүдөн мурун ырастоо сурайт, ал эми авто жөнөтүү өчүк. Биринчи ийгиликтүү жөнөтүүдөн кийин белгилеңиз: таразада аталышы менен баасы туура, этикетка басылат, касса анын штрих-кодун окуйт.",
              "Until ticked, the till asks for confirmation before every send to Massa-K and auto-send is off. Tick it after the first successful send: the name and price on the scale are right, the label prints and the till reads its barcode.",
              "İşaretlenene kadar kasa Massa-K'ya her gönderimden önce onay ister ve otomatik gönderim kapalıdır. İlk başarılı gönderimden sonra işaretleyin: tartıda ad ve fiyat doğru, etiket basılıyor, kasa barkodunu okuyor.",
              "Belgilanmaguncha kassa Massa-K ga har bir yuborishdan oldin tasdiq so‘raydi, avto yuborish esa o‘chiq. Birinchi muvaffaqiyatli yuborishdan keyin belgilang: tarozida nomi va narxi to‘g‘ri, yorliq chop etiladi, kassa uning shtrix-kodini o‘qiydi."),
            out var verifyBody);
        _verified = new CheckBox { IsChecked = prefs.MassaKVerified, Content = Text(L("Проверено на весах: этикетка вышла верно", "Таразада текшерилди: этикетка туура чыкты", "Verified on the scale: the label came out right", "Tartıda doğrulandı: etiket doğru çıktı", "Tarozida tekshirildi: yorliq to‘g‘ri chiqdi")) }; // 2026-11-14: формулировка владельца
        _skipConfirm = new CheckBox { IsChecked = prefs.MassaKSkipConfirm && prefs.MassaKVerified, IsEnabled = prefs.MassaKVerified, Content = Text(L("Больше не спрашивать подтверждение перед отправкой", "Жөнөтүүдөн мурун ырастоону экинчи сурабоо", "Do not ask for confirmation before sending any more", "Göndermeden önce artık onay sorma", "Yuborishdan oldin tasdiqni boshqa so‘ramaslik")) };
        _verified.IsCheckedChanged += (_, _) =>
        {
            var on = _verified.IsChecked == true;
            _skipConfirm.IsEnabled = on;
            if (!on)
                _skipConfirm.IsChecked = false;
        };
        verifyBody.Children.Add(_verified);
        verifyBody.Children.Add(_skipConfirm);
        panel.Children.Add(verifyCard);

        panel.Children.Add(Card(L("Как включить сеть на весах Масса-К", "Масса-К таразасында тармакты кантип күйгүзүү", "How to enable the network on a Massa-K scale", "Massa-K tartıda ağ nasıl açılır", "Massa-K tarozisida tarmoqni qanday yoqish"),
            L("Меню администратора весов → настройки Ethernet: IP-адрес из сети кассы (первые три числа как у компьютера), маска, шлюз и порт (5001). Параметры сети задаются на самих весах (по USB или RS-232 программой производителя, либо с клавиатуры весов — см. руководство по эксплуатации, раздел «Подключение терминала через сеть Ethernet»).",
              "Таразанын администратор менюсу → Ethernet жөндөөлөрү: кассанын тармагындагы IP-дарек (алгачкы үч сан компьютердикиндей), маска, шлюз жана порт (5001). Тармак параметрлери таразанын өзүндө коюлат (USB же RS-232 аркылуу өндүрүүчүнүн программасы менен же таразанын клавиатурасынан — пайдалануу боюнча колдонмодогу «Терминалды Ethernet тармагы аркылуу туташтыруу» бөлүмүн караңыз).",
              "Scale administrator menu → Ethernet settings: an IP address from the till's network (the first three numbers like the computer's), mask, gateway and port (5001). The network parameters are set on the scale itself (over USB or RS-232 with the manufacturer's software, or from the scale keyboard — see the manual, section “Connecting the terminal over Ethernet”).",
              "Tartı yönetici menüsü → Ethernet ayarları: kasa ağından bir IP adresi (ilk üç sayı bilgisayarınki gibi), maske, ağ geçidi ve port (5001). Ağ parametreleri tartının kendisinde ayarlanır (USB veya RS-232 üzerinden üreticinin programıyla ya da tartı klavyesinden — kılavuzdaki «Terminalin Ethernet ağıyla bağlanması» bölümüne bakın).",
              "Tarozi administrator menyusi → Ethernet sozlamalari: kassa tarmog‘idagi IP manzil (dastlabki uch son kompyuterniki kabi), niqob, shlyuz va port (5001). Tarmoq parametrlari tarozining o‘zida belgilanadi (USB yoki RS-232 orqali ishlab chiqaruvchi dasturi bilan yoki tarozi klaviaturasidan — qo‘llanmadagi «Terminalni Ethernet tarmog‘i orqali ulash» bo‘limiga qarang)."),
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
            PosLogger.Log($"Масса-К: список сетей компьютера не получен: {ex.Message}", "SCALES");
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
        lines.Add(L("IP весов: ", "Таразанын IP'си: ", "Scale IP: ", "Tartı IP'si: ", "Tarozi IP'si: ") + (hasIp ? ipText : "—") + $"   TCP {(_portBox?.Value is { } p ? (int)p : MassaKProtocol.DefaultPort)}");
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

    /// <summary>Открывает журнал обмена с весами (massak-exchange.log) программой по умолчанию.</summary>
    private void OpenExchangeLog()
    {
        var path = MassaKScaleService.ExchangeLogPath;
        if (!File.Exists(path))
        {
            ShowResult(L("Журнала ещё нет — он появится после первой проверки связи или отправки: ", "Журнал азырынча жок — ал биринчи текшерүүдөн же жөнөтүүдөн кийин пайда болот: ", "There is no log yet — it appears after the first check or upload: ", "Henüz günlük yok — ilk kontrol veya gönderimden sonra oluşur: ", "Jurnal hali yo‘q — birinchi tekshiruv yoki yuborishdan keyin paydo bo‘ladi: ") + path, false);
            return;
        }
        if (!PlatformLauncher.OpenFile(path))
            ShowResult(path, true);
    }

    /// <summary>Кадры тестового товара — только в журнал (dryRun), весам ничего не уходит.</summary>
    private async Task TrialPacketAsync()
    {
        SaveAll();
        var scale = MassaKScaleService.TryCreate((_ipBox.Text ?? "").Trim(), Port) ?? new MassaKScaleService(IPAddress.Loopback, Port);
        var goods = new MassaKGoods { Id = 1, Code = "1", Price = 123.45m, Name = "Тест Масса-К", BarcodePrefix = (int)(_prefix.Value ?? 20) };
        var result = await scale.UploadAsync(new[] { (goods, 1L) }, _latin.IsChecked == true, true, null, CancellationToken.None).ConfigureAwait(true);
        ShowResult(L($"Пробный пакет ({result.Total} товар) записан в журнал {MassaKScaleService.ExchangeLogPath} (строки DRY-TX). Весам ничего не отправлено.",
            $"Сыноо пакет ({result.Total} товар) {MassaKScaleService.ExchangeLogPath} журналына жазылды (DRY-TX саптары). Таразага эч нерсе жөнөтүлгөн жок.",
            $"Trial packet ({result.Total} product) written to the log {MassaKScaleService.ExchangeLogPath} (DRY-TX lines). Nothing was sent to the scale.",
            $"Deneme paketi ({result.Total} ürün) {MassaKScaleService.ExchangeLogPath} günlüğüne yazıldı (DRY-TX satırları). Tartıya hiçbir şey gönderilmedi.",
            $"Sinov paketi ({result.Total} tovar) {MassaKScaleService.ExchangeLogPath} jurnaliga yozildi (DRY-TX qatorlari). Taroziga hech narsa yuborilmadi."), false);
    }

    // =====================================================================================
    // Сохранение
    // =====================================================================================

    private void SaveConnection()
    {
        var prefs = UserPreferences.Instance;
        prefs.MassaKScaleIp = (_ipBox.Text ?? "").Trim();
        prefs.MassaKScalePort = Port;
        prefs.SaveToDisk();
        // профиль весов (список «Настройки → Весы») хранит адрес и порт этой марки
        if (prefs.ScaleBrand == BrandMassaK)
            LabelScaleStore.CaptureActive();
    }

    private void SaveAll()
    {
        var prefs = UserPreferences.Instance;
        prefs.MassaKScaleIp = (_ipBox.Text ?? "").Trim();
        prefs.MassaKScalePort = Port;
        if (_prefix is not null)
            prefs.MassaKBarcodePrefix = Math.Clamp((int)(_prefix.Value ?? 20), 0, 99);
        if (_shelfLife is not null)
            prefs.MassaKShelfLifeDays = Math.Clamp((int)(_shelfLife.Value ?? 0), 0, 999);
        if (_latin is not null)
            prefs.MassaKLatinNames = _latin.IsChecked == true;
        if (_verified is not null)
        {
            prefs.MassaKVerified = _verified.IsChecked == true;
            prefs.MassaKSkipConfirm = prefs.MassaKVerified && _skipConfirm.IsChecked == true;
        }
        prefs.SaveToDisk();
        if (prefs.ScaleBrand == BrandMassaK)
            LabelScaleStore.CaptureActive();
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
