using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using F = NurMarketKassa.Services.Hardware.ShtrikhLabelFormat;
using P = NurMarketKassa.Services.Hardware.ShtrikhPrintProtocol;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: вкладки «Валюта» и «Макет этикетки» окна настроек весов Штрих-ПРИНТ. Владелец
/// прислал фото этикетки: под ценой «ЦЕНА, РУБ/КГ», а магазин в Кыргызстане — «добавь редактор
/// валюты и редактор штрих чека (этикетки) в штрих м».
///
/// Что можно по протоколу (подробно — в шапке <see cref="ShtrikhLabelFormat"/>):
/// • надпись «ЦЕНА, РУБ/КГ» зашита в прошивку, её текст не меняется. Мастер берёт текущий
///   формат, копирует его в «Формат 1..5», прячет надпись (Y = 0) и ставит на её место свой
///   текст 1..5 («ЦЕНА, СОМ/КГ») тем же шрифтом, затем выбирает этот формат на весах;
/// • символ валюты справа от стоимости (печать 12×24, C2h) и на дисплее (5×7, C1h) — редактор
///   по точкам с заготовками;
/// • курс валюты (2Bh) для «валютного эквивалента»;
/// • «Макет этикетки» — координаты, видимость и шрифты всех элементов своих форматов 1..5.
/// Как и остальные вкладки: сначала «Прочитать с весов», запись — только после чтения и с
/// подтверждением, при ошибке пароля обмен сразу останавливается (RunAsync).
/// </summary>
public partial class ShtrikhScaleSettingsWindow
{
    // ------------------------------------------------------------------ общее

    private byte[]? _labelLengths;       // A7h — длины строк (null — типовые)
    private int _labelNameLines = 2;     // D3h
    private int _labelMessageLines = 1;  // из 11h

    private TabItem AddCodeTab(string header, out StackPanel panel)
    {
        panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var tab = new TabItem { Header = header, Content = new ScrollViewer { Content = panel } };
        Tabs.Items.Add(tab);
        return tab;
    }

    /// <summary>Короткое название элемента этикетки на языке кассы. Неизменяемые надписи
    /// прошивки — в кавычках по-русски: весы печатают их именно так.</summary>
    private static string ElementName(ShtrikhLabelElement e) => e.Key switch
    {
        "GoodsName" => L("Наименование товара", "Товардын аталышы", "Item name", "Ürün adı", "Tovar nomi"),
        "ShopName" => L("Название магазина", "Дүкөндүн аты", "Shop name", "Mağaza adı", "Do‘kon nomi"),
        "Date" => L("Дата", "Дата", "Date", "Tarih", "Sana"),
        "Time" => L("Время", "Убакыт", "Time", "Saat", "Vaqt"),
        "ExpiryDate" => L("Годен до (дата)", "Жарактуу (дата)", "Best-before date", "Son kullanma tarihi", "Yaroqlilik sanasi"),
        "Weight" => L("Масса", "Масса", "Weight", "Ağırlık", "Massa"),
        "Tare" => L("Тара", "Тара", "Tare", "Dara", "Tara"),
        "Price" => L("Цена", "Баа", "Price", "Fiyat", "Narx"),
        "LabelNumber" => L("Номер этикетки", "Этикетка номуру", "Label no.", "Etiket no.", "Yorliq raqami"),
        "ScaleNumber" => L("Номер весов", "Тараза номуру", "Scale no.", "Tartı no.", "Tarozi raqami"),
        "GroupCode" => L("Групповой код", "Топтук код", "Group code", "Grup kodu", "Guruh kodi"),
        "Message" => L("Сообщение (состав)", "Билдирүү (курамы)", "Message (ingredients)", "Mesaj (içerik)", "Xabar (tarkibi)"),
        "Cost" => L("Стоимость", "Наркы", "Total", "Tutar", "Qiymat"),
        "Barcode" => L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"),
        "InscrPacked" => Inscription("УПАКОВАНО"),
        "InscrBestBefore" => Inscription("ГОДЕН ДО"),
        "InscrWeight" => Inscription("МАССА"),
        "InscrPrice" => Inscription("ЦЕНА, РУБ/КГ"),
        "InscrCost" => Inscription("СТОИМОСТЬ"),
        "InscrShelfLife" => Inscription("СРОК ГОДНОСТИ"),
        "InscrManufactured" => Inscription("ДАТА ИЗГОТОВЛЕНИЯ"),
        "InscrGross" => Inscription("МАССА БРУТТО"),
        "Picture1" or "Picture2" or "Picture3" or "Picture4" => L($"Рисунок {e.Key[^1]}", $"{e.Key[^1]}-сүрөт", $"Picture {e.Key[^1]}", $"Resim {e.Key[^1]}", $"{e.Key[^1]}-rasm"),
        "Frame" => L("Рамка", "Алкак", "Frame", "Çerçeve", "Ramka"),
        "ItemCode" => L("Код товара", "Товар коду", "Item code", "Ürün kodu", "Tovar kodi"),
        "PluNumber" => L("Номер ПЛУ", "ПЛУ номуру", "PLU no.", "PLU no.", "PLU raqami"),
        "SumCount" => L("Кол-во покупок (итог)", "Сатып алуулар саны (жыйынтык)", "Purchases count (total)", "Alışveriş sayısı (toplam)", "Xaridlar soni (jami)"),
        "ShelfLifeDays" => L("Срок годности, дней", "Жарактуулук мөөнөтү, күн", "Shelf life, days", "Raf ömrü, gün", "Yaroqlilik muddati, kun"),
        "ManufactureDate" => L("Дата изготовления", "Даярдалган дата", "Production date", "Üretim tarihi", "Ishlab chiqarilgan sana"),
        "Gross" => L("Масса брутто", "Брутто массасы", "Gross weight", "Brüt ağırlık", "Brutto massa"),
        "NetCalc" => L("Расчётная масса нетто", "Эсептик нетто массасы", "Calculated net weight", "Hesaplanan net ağırlık", "Hisoblangan netto massa"),
        "CurrencyEquiv" => L("Стоимость во 2-й валюте", "2-валютадагы наркы", "Total in 2nd currency", "2. para biriminde tutar", "2-valyutadagi qiymat"),
        _ when e.Kind == ShtrikhElementKind.UserText => L($"Свой текст {e.Key[^1]}", $"Өз текст {e.Key[^1]}", $"Custom text {e.Key[^1]}", $"Özel metin {e.Key[^1]}", $"O‘z matn {e.Key[^1]}"),
        _ => e.Key,
    };

    private static string Inscription(string printed) =>
        L($"Надпись «{printed}»", $"«{printed}» жазуусу", $"Caption «{printed}»", $"«{printed}» yazısı", $"«{printed}» yozuvi");

    /// <summary>Шрифты 0..6 с размером символа в мм (8 точек на мм, Приложение 3).</summary>
    private static string FontName(int font)
    {
        var (w, h) = F.FontCell(font);
        var mm = L("мм", "мм", "mm", "mm", "mm");
        return string.Create(CultureInfo.InvariantCulture, $"{font}: {w / 8.0:0.#}×{h / 8.0:0.#} ") + mm;
    }

    private static ComboBox FontCombo()
    {
        var combo = new ComboBox { MinWidth = 120 };
        for (var f = 0; f <= 6; f++)
            combo.Items.Add(FontName(f));
        return combo;
    }

    /// <summary>Знаки, которых нет в кодовой таблице весов (WIN1251): кыргызские ң, ө, ү и т. п.</summary>
    private static string UnsupportedChars(string text)
    {
        try
        {
            var enc = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var bad = new StringBuilder();
            foreach (var ch in text.Distinct())
            {
                try
                {
                    enc.GetBytes(ch.ToString());
                }
                catch (EncoderFallbackException)
                {
                    bad.Append(ch);
                }
            }
            return bad.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Длины строк, число строк наименования и сообщения — для размеров элементов в
    /// предпросмотре. Ошибка любой из этих команд (кроме пароля и связи) не мешает работе.</summary>
    private async Task LoadLabelMetricsAsync(ShtrikhPrintLanScaleService scale)
    {
        _labelLengths = await scale.GetStringLengthsAsync().ConfigureAwait(false);
        try
        {
            _labelNameLines = await scale.GetByteParamAsync(P.CmdGetNameLines).ConfigureAwait(false);
        }
        catch (ShtrikhScaleException ex) when (!ex.IsPasswordError && ex.ErrorCode != 0)
        {
            _labelNameLines = 2;
        }
        var status = await scale.GetStatusAsync().ConfigureAwait(false);
        _status = status;
        _labelMessageLines = Math.Max(1, status.MessageLineCount);
    }

    /// <summary>Название в кавычках — если своих кавычек в нём ещё нет (у надписей они есть).</summary>
    private static string Q(ShtrikhLabelElement e)
    {
        var name = ElementName(e);
        return name.Contains('«') ? name : $"«{name}»";
    }

    private string ProblemsText(ShtrikhLabelLayout layout)
    {
        var (_, overlaps, outside) = ShtrikhLabelCanvas.FindProblems(layout, _labelLengths, _labelNameLines, _labelMessageLines);
        var parts = new List<string>();
        foreach (var e in outside)
            parts.Add(L($"{Q(e)} выходит за край этикетки", $"{Q(e)} этикетканын четинен чыгып кетет", $"{Q(e)} goes past the label edge", $"{Q(e)} etiket kenarını aşıyor", $"{Q(e)} yorliq chetidan chiqadi"));
        foreach (var (a, b) in overlaps)
            parts.Add(L($"{Q(a)} и {Q(b)} накладываются", $"{Q(a)} менен {Q(b)} бири-бирине түшөт", $"{Q(a)} and {Q(b)} overlap", $"{Q(a)} ile {Q(b)} üst üste", $"{Q(a)} va {Q(b)} ustma-ust"));
        return parts.Count == 0
            ? "✓ " + L("Наложений нет, всё помещается на этикетке.", "Бири-бирине түшкөн жок, баары этикеткага батат.", "No overlaps, everything fits on the label.", "Çakışma yok, her şey etikete sığıyor.", "Ustma-ust yo‘q, hammasi yorliqqa sig‘adi.")
            : "⚠ " + string.Join("; ", parts) + ". " + L("Весы напечатают правильно только один из наложившихся элементов.", "Тараза бири-бирине түшкөндөрдүн бирин гана туура басат.", "The scale prints only one of the overlapping elements correctly.", "Tartı çakışanlardan yalnızca birini doğru basar.", "Tarozi ustma-ust tushganlardan faqat bittasini to‘g‘ri chop etadi.");
    }

    private void BuildCurrencyAndLayoutTabs()
    {
        BuildCurrencyTab();
        BuildLayoutTab();
    }

    // ==================================================================================
    // Вкладка «Валюта»
    // ==================================================================================

    private ComboBox _wzSource = null!;
    private ComboBox _wzTarget = null!;
    private TextBox _wzPriceText = null!;
    private ComboBox _wzPriceNo = null!;
    private CheckBox _wzAlsoCost = null!;
    private TextBox _wzCostText = null!;
    private ComboBox _wzCostNo = null!;
    private CheckBox _wzSelect = null!;
    private TextBlock _wzInfo = null!;
    private ShtrikhLabelCanvas _wzCanvas = null!;
    private ShtrikhLabelLayout? _wzSourceLayout;
    private bool _wzSourceTouched;
    private bool _wzLoading;
    private int _wzCurrentFormat = -1;
    private readonly string[] _wzUserTexts = new string[5];

    private void BuildCurrencyTab()
    {
        AddCodeTab(L("Валюта", "Валюта", "Currency", "Para birimi", "Valyuta"), out var panel);

        // --- Мастер «ЦЕНА, РУБ/КГ» → своя валюта
        var card = Card(
            L("Надпись «ЦЕНА, РУБ/КГ» → своя валюта", "«ЦЕНА, РУБ/КГ» жазуусу → өз валютаңыз", "Caption «ЦЕНА, РУБ/КГ» → your currency", "«ЦЕНА, РУБ/КГ» yazısı → kendi para biriminiz", "«ЦЕНА, РУБ/КГ» yozuvi → o‘z valyutangiz"),
            L("Эта надпись зашита в прошивку весов — её текст поменять нельзя. Но её можно спрятать в своём формате этикетки (Формат 1–5) и поставить на то же место свой текст, например «ЦЕНА, СОМ/КГ». Мастер копирует выбранный формат, прячет надпись, ставит свой текст тем же шрифтом и выбирает новый формат на весах.",
              "Бул жазуу таразанын программасына бекитилген — анын текстин өзгөртүүгө болбойт. Бирок аны өз форматыңызда (Формат 1–5) жашырып, ордуна өз текстиңизди коюуга болот, мисалы «ЦЕНА, СОМ/КГ». Мастер форматты көчүрүп, жазууну жашырып, өз текстиңизди ошол эле шрифт менен коюп, жаңы форматты таразада тандайт.",
              "This caption is built into the scale firmware and cannot be edited. It can be hidden in a custom label format (Format 1–5) and replaced by your own text in the same place, e.g. «ЦЕНА, СОМ/КГ». The wizard copies the format, hides the caption, places your text with the same font and selects the new format on the scale.",
              "Bu yazı tartının yazılımına gömülüdür, metni değiştirilemez. Ancak özel bir etiket formatında (Format 1–5) gizlenip yerine kendi metniniz konabilir, örn. «ЦЕНА, СОМ/КГ». Sihirbaz formatı kopyalar, yazıyı gizler, metninizi aynı yazı tipiyle koyar ve yeni formatı tartıda seçer.",
              "Bu yozuv tarozi dasturiga o‘rnatilgan — matnini o‘zgartirib bo‘lmaydi. Lekin uni o‘z formatingizda (Format 1–5) yashirib, o‘rniga o‘z matningizni qo‘yish mumkin, masalan «ЦЕНА, СОМ/КГ». Usta formatni nusxalaydi, yozuvni yashiradi, matningizni o‘sha shrift bilan qo‘yadi va yangi formatni tarozida tanlaydi."),
            out var body);

        _wzSource = new ComboBox { MinWidth = 280 };
        foreach (var (_, label) in LabelFormats())
            _wzSource.Items.Add(label);
        _wzSource.SelectionChanged += (_, _) =>
        {
            if (_wzLoading)
                return;
            _wzSourceTouched = true;
            _wzSourceLayout = null; // другой формат — надо прочитать заново
            UpdateWizardPreview();
        };
        body.Children.Add(Row(L("Взять за основу формат", "Негиз катары формат", "Base on format", "Temel format", "Asos format"), _wzSource));

        _wzTarget = new ComboBox { MinWidth = 280 };
        for (var f = F.FirstUserFormat; f <= F.LastUserFormat; f++)
            _wzTarget.Items.Add(LabelFormats()[f].Item2);
        _wzTarget.SelectedIndex = 0;
        _wzTarget.SelectionChanged += (_, _) => UpdateWizardPreview();
        body.Children.Add(Row(L("Сохранить как", "Катары сактоо", "Save as", "Farklı kaydet", "Sifatida saqlash"), _wzTarget));

        _wzPriceText = new TextBox { Text = "ЦЕНА, СОМ/КГ", MaxLength = P.UserTextFieldLength };
        _wzPriceText.TextChanged += (_, _) => UpdateWizardPreview();
        body.Children.Add(Row(L("Текст вместо «ЦЕНА, РУБ/КГ»", "«ЦЕНА, РУБ/КГ» ордуна текст", "Text instead of «ЦЕНА, РУБ/КГ»", "«ЦЕНА, РУБ/КГ» yerine metin", "«ЦЕНА, РУБ/КГ» o‘rniga matn"), _wzPriceText));
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in new[] { "ЦЕНА, СОМ/КГ", "Цена, сом/кг", "ЦЕНА ЗА 1 КГ, СОМ", "PRICE, KGS/KG" })
        {
            var b = MakeButton(preset, false, (_, _) => _wzPriceText.Text = preset);
            b.Margin = new Thickness(0, 0, 6, 4);
            presets.Children.Add(b);
        }
        body.Children.Add(Row("", presets));

        _wzPriceNo = TextNumberCombo(0);
        body.Children.Add(Row(L("В какой свой текст записать (1–5)", "Кайсы өз текстке жазуу (1–5)", "Which custom text to use (1–5)", "Hangi özel metin (1–5)", "Qaysi o‘z matnga yozish (1–5)"), _wzPriceNo));

        _wzAlsoCost = new CheckBox { Content = L("Заменить и надпись «СТОИМОСТЬ»", "«СТОИМОСТЬ» жазуусун да алмаштыруу", "Also replace the «СТОИМОСТЬ» caption", "«СТОИМОСТЬ» yazısını da değiştir", "«СТОИМОСТЬ» yozuvini ham almashtirish") };
        _wzAlsoCost.IsCheckedChanged += (_, _) => UpdateWizardPreview();
        body.Children.Add(_wzAlsoCost);
        _wzCostText = new TextBox { Text = "СУММА, СОМ", MaxLength = P.UserTextFieldLength };
        _wzCostText.TextChanged += (_, _) => UpdateWizardPreview();
        body.Children.Add(Row(L("Текст вместо «СТОИМОСТЬ»", "«СТОИМОСТЬ» ордуна текст", "Text instead of «СТОИМОСТЬ»", "«СТОИМОСТЬ» yerine metin", "«СТОИМОСТЬ» o‘rniga matn"), _wzCostText));
        _wzCostNo = TextNumberCombo(1);
        body.Children.Add(Row(L("Его свой текст (1–5)", "Анын өз тексти (1–5)", "Its custom text (1–5)", "Özel metni (1–5)", "Uning o‘z matni (1–5)"), _wzCostNo));

        _wzSelect = new CheckBox { IsChecked = true, Content = L("После записи сразу печатать этим форматом", "Жазгандан кийин дароо ушул формат менен басуу", "Print with this format right after writing", "Yazdıktan sonra bu formatla bas", "Yozgandan so‘ng shu format bilan chop etish") };
        body.Children.Add(_wzSelect);

        body.Children.Add(ButtonRow(
            MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from scale", "Tartıdan oku", "Tarozidan o‘qish"), false, async (_, _) => await ReadWizardAsync().ConfigureAwait(true)),
            MakeButton(L("Записать в весы", "Таразага жазуу", "Write to scale", "Tartıya yaz", "Taroziga yozish"), true, async (_, _) => await WriteWizardAsync().ConfigureAwait(true))));

        _wzInfo = new TextBlock { Classes = { "label" } };
        body.Children.Add(_wzInfo);
        _wzCanvas = new ShtrikhLabelCanvas { Caption = ElementName, HorizontalAlignment = HorizontalAlignment.Left, Width = 440 };
        _wzCanvas.SampleText = e => e.Kind == ShtrikhElementKind.UserText ? WizardSampleText(e) : null;
        body.Children.Add(_wzCanvas);
        body.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            Text = L("Честно о границах: 1) у штучного товара весы сами пишут «ЦЕНА, РУБ/ШТ», а свой текст не меняется — для штучных сделайте отдельный формат и укажите его товару; 2) место своего текста по протоколу — 30 знаков, если справа на той же строке другой элемент, он может не напечататься (красная рамка на схеме); 3) на форматах «с разметкой» надписи не печатаются вовсе; 4) буквы ң, ө, ү весы не печатают. Первую этикетку после записи проверьте на весах.",
                  "Чектөөлөр: 1) даана товар үчүн тараза өзү «ЦЕНА, РУБ/ШТ» жазат, өз текст өзгөрбөйт — даана товарларга өзүнчө формат жасап, товарга көрсөтүңүз; 2) өз тексттин орду протокол боюнча 30 белги — ошол эле саптагы оң жактагы элемент басылбай калышы мүмкүн (схемадагы кызыл алкак); 3) «белгилөө менен» форматтарда жазуулар такыр басылбайт; 4) ң, ө, ү тамгаларын тараза баспайт. Жазгандан кийинки биринчи этикетканы таразадан текшериңиз.",
                  "Honest limits: 1) for piece goods the scale prints «ЦЕНА, РУБ/ШТ» by itself and your text does not change — make a separate format for piece goods and assign it to them; 2) a custom text reserves 30 characters by protocol — an element to its right on the same row may not print (red frame on the diagram); 3) formats “with pre-printed layout” print no captions at all; 4) the scale cannot print ң, ө, ү. Check the first label on the scale after writing.",
                  "Sınırlar: 1) adetli üründe tartı «ЦЕНА, РУБ/ШТ» yazar, metniniz değişmez — adetliler için ayrı format yapıp ürüne atayın; 2) özel metin protokole göre 30 karakter yer kaplar — aynı satırda sağdaki öğe basılmayabilir (şemada kırmızı çerçeve); 3) “baskılı” formatlarda yazılar hiç basılmaz; 4) tartı ң, ө, ү harflerini basamaz. Yazdıktan sonra ilk etiketi tartıda kontrol edin.",
                  "Cheklovlar: 1) donali tovarda tarozi o‘zi «ЦЕНА, РУБ/ШТ» yozadi, o‘z matningiz o‘zgarmaydi — donali tovarlar uchun alohida format qilib, tovarga belgilang; 2) o‘z matn protokol bo‘yicha 30 belgi joy egallaydi — shu qatorda o‘ngdagi element chop etilmasligi mumkin (sxemada qizil ramka); 3) «belgili» formatlarda yozuvlar umuman chop etilmaydi; 4) tarozi ң, ө, ү harflarini chop etmaydi. Yozgandan keyingi birinchi yorliqni tarozida tekshiring."),
        });
        panel.Children.Add(card);
        UpdateWizardPreview();

        BuildSymbolCard(panel);
        BuildRateCard(panel);
    }

    private static ComboBox TextNumberCombo(int selected)
    {
        var combo = new ComboBox { MinWidth = 120 };
        for (var i = 1; i <= 5; i++)
            combo.Items.Add(L($"Текст {i}", $"Текст {i}", $"Text {i}", $"Metin {i}", $"Matn {i}"));
        combo.SelectedIndex = selected;
        return combo;
    }

    private int WizardTarget => F.FirstUserFormat + Math.Max(0, _wzTarget.SelectedIndex);
    private int WizardPriceNo => Math.Max(0, _wzPriceNo.SelectedIndex) + 1;
    private int WizardCostNo => Math.Max(0, _wzCostNo.SelectedIndex) + 1;
    private bool WizardAlsoCost => _wzAlsoCost.IsChecked == true;

    private string? WizardSampleText(ShtrikhLabelElement e)
    {
        var n = e.Key[^1] - '0';
        if (n == WizardPriceNo)
            return _wzPriceText.Text;
        if (WizardAlsoCost && n == WizardCostNo)
            return _wzCostText.Text;
        return n is >= 1 and <= 5 ? _wzUserTexts[n - 1] : null;
    }

    /// <summary>Строит будущий формат из прочитанного. null + причина — писать нельзя.</summary>
    private (ShtrikhLabelLayout? Layout, string? Blocker, List<string> Notes) BuildWizardProposal()
    {
        var notes = new List<string>();
        if (_wzSourceLayout is not { } source)
            return (null, L("Нажмите «Прочитать с весов».", "«Таразадан окуу» басыңыз.", "Press “Read from scale”.", "“Tartıdan oku”ya basın.", "«Tarozidan o‘qish»ni bosing."), notes);
        if (!source.HasEx)
            return (null, L("Эти весы не поддерживают свои тексты на этикетке (нужен протокол 1.4 и новее).", "Бул тараза этикеткадагы өз тексттерди колдобойт (1.4 протоколу же жаңысы керек).", "This scale does not support custom label texts (protocol 1.4+ required).", "Bu tartı özel etiket metinlerini desteklemiyor (protokol 1.4+ gerekli).", "Bu tarozi yorliqdagi o‘z matnlarni qo‘llamaydi (1.4+ protokol kerak)."), notes);

        var priceText = (_wzPriceText.Text ?? "").Trim();
        if (priceText.Length == 0)
            return (null, L("Введите текст вместо «ЦЕНА, РУБ/КГ».", "«ЦЕНА, РУБ/КГ» ордуна текст жазыңыз.", "Enter the text instead of «ЦЕНА, РУБ/КГ».", "«ЦЕНА, РУБ/КГ» yerine metin girin.", "«ЦЕНА, РУБ/КГ» o‘rniga matn kiriting."), notes);
        if (WizardAlsoCost && WizardCostNo == WizardPriceNo)
            return (null, L("Для «ЦЕНА» и «СТОИМОСТЬ» выберите разные номера своих текстов.", "«ЦЕНА» жана «СТОИМОСТЬ» үчүн өз тексттердин ар башка номурун тандаңыз.", "Choose different custom text numbers for «ЦЕНА» and «СТОИМОСТЬ».", "«ЦЕНА» ve «СТОИМОСТЬ» için farklı metin numaraları seçin.", "«ЦЕНА» va «СТОИМОСТЬ» uchun turli matn raqamlarini tanlang."), notes);

        var inscrPrice = F.Element("InscrPrice");
        if (!source.IsVisible(inscrPrice))
            return (null, L("В этом формате надпись «ЦЕНА, РУБ/КГ» не печатается (например, формат «с разметкой») — менять нечего. Выберите формат, которым печатают весы.", "Бул форматта «ЦЕНА, РУБ/КГ» жазуусу басылбайт (мисалы, «белгилөө менен» формат) — өзгөртө турган эч нерсе жок. Тараза басып жаткан форматты тандаңыз.", "This format does not print «ЦЕНА, РУБ/КГ» (e.g. a pre-printed format) — nothing to replace. Choose the format the scale prints with.", "Bu format «ЦЕНА, РУБ/КГ» basmıyor (örn. baskılı format) — değiştirilecek bir şey yok. Tartının bastığı formatı seçin.", "Bu formatda «ЦЕНА, РУБ/КГ» chop etilmaydi (masalan, «belgili» format) — almashtiradigan narsa yo‘q. Tarozi chop etayotgan formatni tanlang."), notes);

        var result = source.Clone(WizardTarget);
        Replace(result, inscrPrice, F.UserText(WizardPriceNo));
        if (WizardAlsoCost)
        {
            var inscrCost = F.Element("InscrCost");
            if (source.IsVisible(inscrCost))
                Replace(result, inscrCost, F.UserText(WizardCostNo));
            else
                notes.Add(L("надписи «СТОИМОСТЬ» в этом формате нет", "бул форматта «СТОИМОСТЬ» жазуусу жок", "this format has no «СТОИМОСТЬ» caption", "bu formatta «СТОИМОСТЬ» yazısı yok", "bu formatda «СТОИМОСТЬ» yozuvi yo‘q"));
        }

        foreach (var n in WizardAlsoCost ? new[] { WizardPriceNo, WizardCostNo } : new[] { WizardPriceNo })
        {
            if (source.IsVisible(F.UserText(n)) && !string.IsNullOrWhiteSpace(_wzUserTexts[n - 1]))
                notes.Add(L($"свой текст {n} («{_wzUserTexts[n - 1]}») уже стоит на этикетке — он будет заменён", $"өз текст {n} («{_wzUserTexts[n - 1]}») этикеткада бар — ал алмаштырылат", $"custom text {n} («{_wzUserTexts[n - 1]}») is already on the label — it will be replaced", $"özel metin {n} («{_wzUserTexts[n - 1]}») zaten etikette — değiştirilecek", $"o‘z matn {n} («{_wzUserTexts[n - 1]}») yorliqda bor — almashtiriladi"));
        }

        var bad = UnsupportedChars(priceText + (WizardAlsoCost ? _wzCostText.Text ?? "" : ""));
        if (bad.Length > 0)
            notes.Add(L($"знаки «{bad}» весы не печатают (таблица WIN1251)", $"«{bad}» белгилерин тараза баспайт (WIN1251 таблицасы)", $"the scale cannot print «{bad}» (WIN1251 code page)", $"tartı «{bad}» karakterlerini basamaz (WIN1251)", $"tarozi «{bad}» belgilarini chop etmaydi (WIN1251)"));
        return (result, null, notes);
    }

    /// <summary>Прячет неизменяемую надпись и ставит свой текст на её место тем же шрифтом.</summary>
    private static void Replace(ShtrikhLabelLayout layout, ShtrikhLabelElement inscription, ShtrikhLabelElement text)
    {
        var (x, y) = layout.GetPosition(inscription);
        layout.SetPosition(text, x, y);
        if (layout.HasFonts)
            layout.SetFont(text, layout.GetFont(inscription));
        layout.SetPosition(inscription, x, 0); // Y = 0 — элемент не печатается (Приложение 3)
    }

    private void UpdateWizardPreview()
    {
        if (_wzInfo is null || _wzCanvas is null)
            return;
        var (layout, blocker, notes) = BuildWizardProposal();
        _wzCanvas.Lengths = _labelLengths;
        _wzCanvas.NameLines = _labelNameLines;
        _wzCanvas.MessageLines = _labelMessageLines;
        _wzCanvas.Layout = layout ?? _wzSourceLayout;
        _wzCanvas.SelectedKey = layout is null ? null : F.UserText(WizardPriceNo).Key;
        _wzCanvas.Refresh();
        if (blocker is not null)
        {
            _wzInfo.Text = blocker;
            _wzInfo.Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray);
            return;
        }

        var sb = new StringBuilder();
        sb.Append(L($"Будет записано: {LabelFormats()[WizardTarget].Item2} на основе «{LabelFormats()[_wzSourceLayout!.Format].Item2}». ", $"Жазылат: «{LabelFormats()[_wzSourceLayout!.Format].Item2}» негизинде {LabelFormats()[WizardTarget].Item2}. ", $"Will write: {LabelFormats()[WizardTarget].Item2} based on “{LabelFormats()[_wzSourceLayout!.Format].Item2}”. ", $"Yazılacak: “{LabelFormats()[_wzSourceLayout!.Format].Item2}” temelinde {LabelFormats()[WizardTarget].Item2}. ", $"Yoziladi: «{LabelFormats()[_wzSourceLayout!.Format].Item2}» asosida {LabelFormats()[WizardTarget].Item2}. "));
        if (_wzCurrentFormat >= 0)
            sb.Append(L($"Сейчас весы печатают форматом «{LabelFormats()[_wzCurrentFormat].Item2}». ", $"Азыр тараза «{LabelFormats()[_wzCurrentFormat].Item2}» форматы менен басат. ", $"The scale currently prints with “{LabelFormats()[_wzCurrentFormat].Item2}”. ", $"Tartı şu an “{LabelFormats()[_wzCurrentFormat].Item2}” ile basıyor. ", $"Hozir tarozi «{LabelFormats()[_wzCurrentFormat].Item2}» formati bilan chop etadi. "));
        if (_wzCurrentFormat == WizardTarget && _wzSourceLayout!.Format != WizardTarget)
            sb.Append(L("Внимание: этот свой формат сейчас выбран на весах и будет перезаписан. ", "Көңүл буруңуз: бул өз формат азыр таразада тандалган жана кайра жазылат. ", "Note: this custom format is selected on the scale now and will be overwritten. ", "Dikkat: bu özel format şu an tartıda seçili ve üzerine yazılacak. ", "Diqqat: bu o‘z format hozir tarozida tanlangan va qayta yoziladi. "));
        foreach (var n in notes)
            sb.Append("⚠ ").Append(n).Append(". ");
        sb.Append(ProblemsText(layout!));
        _wzInfo.Text = sb.ToString();
        var hasWarning = notes.Count > 0 || ShtrikhLabelCanvas.FindProblems(layout!, _labelLengths, _labelNameLines, _labelMessageLines).Bad.Count > 0;
        _wzInfo.Foreground = hasWarning ? ThemeBrush("BrushWarning", Brushes.DarkOrange) : ThemeBrush("BrushText", Brushes.Black);
    }

    private async Task ReadWizardAsync()
    {
        var chosen = _wzSourceTouched ? _wzSource.SelectedIndex : -1;
        await RunAsync(async scale =>
        {
            var current = await scale.GetByteParamAsync(P.CmdGetLabelFormat).ConfigureAwait(false);
            var source = chosen >= 0 ? chosen : Math.Min((int)current, F.MaxFormat);
            var layout = await scale.GetLabelLayoutAsync(source).ConfigureAwait(false);
            await LoadLabelMetricsAsync(scale).ConfigureAwait(false);
            var texts = new string[5];
            for (var i = 1; i <= 5; i++)
                texts[i - 1] = await scale.GetTextParamAsync(P.CmdGetUserText, P.UserTextFieldLength, i).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _wzCurrentFormat = current;
                texts.CopyTo(_wzUserTexts, 0);
                _wzLoading = true;
                _wzSource.SelectedIndex = source;
                _wzLoading = false;
                _wzSourceLayout = layout;
                // Если за основу взят свой формат — по умолчанию пишем в него же.
                if (F.IsUserFormat(source) && !_wzSourceTouched)
                    _wzTarget.SelectedIndex = source - F.FirstUserFormat;
                UpdateWizardPreview();
            });
            return L("Формат прочитан. Проверьте схему ниже и нажмите «Записать в весы».", "Формат окулду. Төмөнкү схеманы текшерип «Таразага жазуу» басыңыз.", "Format read. Check the diagram below and press “Write to scale”.", "Format okundu. Aşağıdaki şemayı kontrol edip “Tartıya yaz”a basın.", "Format o‘qildi. Quyidagi sxemani tekshirib «Taroziga yozish»ni bosing.");
        }).ConfigureAwait(true);
    }

    private async Task WriteWizardAsync()
    {
        var (layout, blocker, _) = BuildWizardProposal();
        if (layout is null)
        {
            ShowResult(blocker ?? "", true);
            return;
        }

        var target = WizardTarget;
        var priceNo = WizardPriceNo;
        var priceText = (_wzPriceText.Text ?? "").Trim();
        var alsoCost = WizardAlsoCost && F.Element("InscrCost") is var ic && _wzSourceLayout!.IsVisible(ic);
        var costNo = WizardCostNo;
        var costText = (_wzCostText.Text ?? "").Trim();
        var select = _wzSelect.IsChecked == true;
        var targetName = LabelFormats()[target].Item2;

        var confirmed = PosConfirmDialog.Show(this,
            L("Записать формат в весы?", "Форматты таразага жазасызбы?", "Write the format to the scale?", "Format tartıya yazılsın mı?", "Format taroziga yozilsinmi?"),
            L($"{targetName} на весах будет перезаписан. Свой текст {priceNo} станет «{priceText}»" + (alsoCost ? $", свой текст {costNo} — «{costText}»" : "") + "." + (select ? " Весы начнут печатать этим форматом." : ""),
              $"Таразадагы {targetName} кайра жазылат. Өз текст {priceNo} «{priceText}» болот" + (alsoCost ? $", өз текст {costNo} — «{costText}»" : "") + "." + (select ? " Тараза ушул формат менен баса баштайт." : ""),
              $"{targetName} on the scale will be overwritten. Custom text {priceNo} becomes «{priceText}»" + (alsoCost ? $", custom text {costNo} — «{costText}»" : "") + "." + (select ? " The scale will print with this format." : ""),
              $"Tartıdaki {targetName} üzerine yazılacak. Özel metin {priceNo} «{priceText}» olacak" + (alsoCost ? $", özel metin {costNo} — «{costText}»" : "") + "." + (select ? " Tartı bu formatla basacak." : ""),
              $"Tarozidagi {targetName} qayta yoziladi. O‘z matn {priceNo} «{priceText}» bo‘ladi" + (alsoCost ? $", o‘z matn {costNo} — «{costText}»" : "") + "." + (select ? " Tarozi shu format bilan chop etadi." : "")),
            L("Записать", "Жазуу", "Write", "Yaz", "Yozish"),
            L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        if (!confirmed)
            return;

        await RunAsync(async scale =>
        {
            await scale.WriteLabelLayoutAsync(layout).ConfigureAwait(false);
            await scale.SetTextParamAsync(P.CmdSetUserText, priceText, P.UserTextFieldLength, priceNo).ConfigureAwait(false);
            if (alsoCost)
                await scale.SetTextParamAsync(P.CmdSetUserText, costText, P.UserTextFieldLength, costNo).ConfigureAwait(false);
            if (select)
                await scale.SetByteParamAsync(P.CmdSetLabelFormat, target).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _wzUserTexts[priceNo - 1] = priceText;
                if (alsoCost)
                    _wzUserTexts[costNo - 1] = costText;
                if (select)
                    _wzCurrentFormat = target;
                UpdateWizardPreview();
            });
            return L($"Готово: {targetName} записан" + (select ? " и выбран на весах" : "") + ". Взвесьте товар и напечатайте этикетку, чтобы проверить.",
                $"Даяр: {targetName} жазылды" + (select ? " жана таразада тандалды" : "") + ". Текшерүү үчүн товарды таразалап этикетка басыңыз.",
                $"Done: {targetName} written" + (select ? " and selected on the scale" : "") + ". Weigh an item and print a label to check.",
                $"Tamam: {targetName} yazıldı" + (select ? " ve tartıda seçildi" : "") + ". Kontrol için bir ürün tartıp etiket basın.",
                $"Tayyor: {targetName} yozildi" + (select ? " va tarozida tanlandi" : "") + ". Tekshirish uchun tovarni tortib yorliq chop eting.");
        }).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- символ валюты

    private PixelEditor _symbolEditor = null!;
    private ComboBox _symbolKind = null!;
    private ComboBox _symbolNumber = null!;
    private TextBox _symbolText = null!;
    private TextBlock _symbolInfo = null!;

    private bool SymbolForPrint => _symbolKind.SelectedIndex <= 0;

    private void BuildSymbolCard(StackPanel panel)
    {
        var card = Card(L("Символ валюты", "Валюта белгиси", "Currency sign", "Para birimi işareti", "Valyuta belgisi"),
            L("Символ печатается сразу справа от стоимости (и от суммы во 2-й валюте), если во вкладке «Печать и этикетка» включено «знаки валют», а шрифт стоимости крупный (не 0 и не 2). На дисплее весов символ виден в режиме эквивалента стоимости. Нарисуйте его по точкам или выберите заготовку.",
              "Белги наркынын оң жагына басылат (жана 2-валютадагы сумманын), эгер «Басып чыгаруу жана этикетка» өтмөгүндө «валюта белгилери» күйгүзүлсө жана наркынын шрифти чоң болсо (0 же 2 эмес). Таразанын дисплейинде белги наркынын эквиваленти режиминде көрүнөт. Аны чекит менен тартыңыз же даярын тандаңыз.",
              "The sign is printed right after the total (and the 2nd-currency total) if “currency signs” is on in “Printing & label” and the total uses a large font (not 0 or 2). On the scale display it shows in the equivalent mode. Draw it dot by dot or pick a preset.",
              "İşaret, “Baskı ve etiket”te “para birimi işareti” açıksa ve tutar büyük yazı tipindeyse (0 veya 2 değil) tutarın hemen sağına basılır. Tartı ekranında karşılık modunda görünür. Nokta nokta çizin veya hazır birini seçin.",
              "Belgi qiymatning o‘ng tomonida chop etiladi (va 2-valyutadagi summaning), agar «Chop etish va yorliq»da «valyuta belgilari» yoqilgan va qiymat shrifti katta bo‘lsa (0 yoki 2 emas). Tarozi displeyida ekvivalent rejimida ko‘rinadi. Uni nuqtalab chizing yoki tayyorini tanlang."),
            out var body);

        _symbolKind = new ComboBox { MinWidth = 280 };
        _symbolKind.Items.Add(L("для печати на этикетке (12×24 точки)", "этикеткага басуу үчүн (12×24 чекит)", "for label printing (12×24 dots)", "etiket baskısı için (12×24 nokta)", "yorliqqa chop uchun (12×24 nuqta)"));
        _symbolKind.Items.Add(L("для дисплея весов (5×7 точек)", "тараза дисплейи үчүн (5×7 чекит)", "for the scale display (5×7 dots)", "tartı ekranı için (5×7 nokta)", "tarozi displeyi uchun (5×7 nuqta)"));
        _symbolKind.SelectedIndex = 0;
        _symbolKind.SelectionChanged += (_, _) => ResetSymbolEditor();
        body.Children.Add(Row(L("Куда", "Кайда", "Where", "Nereye", "Qayerga"), _symbolKind));

        _symbolNumber = new ComboBox { MinWidth = 280 };
        _symbolNumber.Items.Add(L("основная валюта (справа от стоимости)", "негизги валюта (наркынын оң жагында)", "main currency (after the total)", "ana para birimi (tutarın sağında)", "asosiy valyuta (qiymatdan o‘ngda)"));
        _symbolNumber.Items.Add(L("дополнительная валюта (эквивалент)", "кошумча валюта (эквивалент)", "second currency (equivalent)", "ikinci para birimi (karşılık)", "qo‘shimcha valyuta (ekvivalent)"));
        _symbolNumber.SelectedIndex = 0;
        body.Children.Add(Row(L("Какой символ", "Кайсы белги", "Which sign", "Hangi işaret", "Qaysi belgi"), _symbolNumber));

        _symbolEditor = new PixelEditor { HorizontalAlignment = HorizontalAlignment.Left };
        _symbolEditor.Changed += (_, _) => UpdateSymbolInfo();
        body.Children.Add(_symbolEditor);

        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        void Preset(string title, Action action)
        {
            var b = MakeButton(title, false, (_, _) => { action(); _symbolEditor.InvalidateVisual(); UpdateSymbolInfo(); });
            b.Margin = new Thickness(0, 0, 6, 4);
            presetRow.Children.Add(b);
        }
        Preset("с", () => _symbolEditor.Load(SymbolForPrint ? PrintPresetS : DisplayPresetS));
        Preset(L("⃀ (знак сома)", "⃀ (сом белгиси)", "⃀ (som sign)", "⃀ (som işareti)", "⃀ (so‘m belgisi)"), () => _symbolEditor.Load(SymbolForPrint ? PrintPresetSomSign : DisplayPresetSomSign));
        Preset(L("сом (мелко)", "сом (майда)", "сом (small)", "сом (küçük)", "сом (mayda)"), () => _symbolEditor.SetPixels(RenderTextToDots("сом", _symbolEditor.Cols, _symbolEditor.Rows, SymbolForPrint)));
        Preset(L("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"), () => _symbolEditor.Clear());
        Preset(L("Инвертировать", "Тескери буруу", "Invert", "Ters çevir", "Teskari"), () => _symbolEditor.Invert());
        // 2026-09-28: порядок битов символа дисплея (5×7) в протоколе описан, но на живых весах не
        // проверен — если знак вышел зеркальным, владелец отражает рисунок и загружает снова.
        Preset(L("Отразить ↔ (если на весах вышло зеркально)", "Күзгүдөй буруу ↔ (таразада тескери чыкса)", "Mirror ↔ (if it came out mirrored on the scale)", "Aynala ↔ (tartıda ters çıktıysa)", "Ko‘zgu ↔ (tarozida teskari chiqsa)"), () => _symbolEditor.Mirror());
        body.Children.Add(presetRow);

        _symbolText = new TextBox { MaxLength = 4, Width = 120, Text = "с" };
        var draw = MakeButton(L("Нарисовать текст", "Текстти тартуу", "Draw text", "Metni çiz", "Matnni chizish"), false, (_, _) =>
        {
            var text = (_symbolText.Text ?? "").Trim();
            if (text.Length > 0)
                _symbolEditor.SetPixels(RenderTextToDots(text, _symbolEditor.Cols, _symbolEditor.Rows, SymbolForPrint));
            UpdateSymbolInfo();
        });
        var textRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        textRow.Children.Add(_symbolText);
        textRow.Children.Add(draw);
        body.Children.Add(Row(L("Свой текст (1–4 знака)", "Өз текст (1–4 белги)", "Own text (1–4 chars)", "Kendi metin (1–4 karakter)", "O‘z matn (1–4 belgi)"), textRow));

        _symbolInfo = new TextBlock { Classes = { "hint" }, FontFamily = new FontFamily("Consolas, Segoe UI") };
        body.Children.Add(_symbolInfo);
        body.Children.Add(ButtonRow(MakeButton(L("Загрузить в весы", "Таразага жүктөө", "Upload to scale", "Tartıya yükle", "Taroziga yuklash"), true, async (_, _) => await UploadSymbolAsync().ConfigureAwait(true))));
        panel.Children.Add(card);
        ResetSymbolEditor();
    }

    private void ResetSymbolEditor()
    {
        if (_symbolEditor is null)
            return;
        if (SymbolForPrint)
            _symbolEditor.Configure(F.PrintSymbolWidth, F.PrintSymbolHeight, 14, PrintPresetS);
        else
            _symbolEditor.Configure(F.DisplaySymbolWidth, F.DisplaySymbolHeight, 28, DisplayPresetS);
        UpdateSymbolInfo();
    }

    private byte[] SymbolBytes() => SymbolForPrint ? F.EncodePrintSymbol(_symbolEditor.Pixels) : F.EncodeDisplaySymbol(_symbolEditor.Pixels);

    private void UpdateSymbolInfo()
    {
        if (_symbolInfo is null)
            return;
        var bytes = SymbolBytes();
        _symbolInfo.Text = L($"Будет отправлено {bytes.Length} байт: ", $"{bytes.Length} байт жөнөтүлөт: ", $"{bytes.Length} bytes will be sent: ", $"{bytes.Length} bayt gönderilecek: ", $"{bytes.Length} bayt yuboriladi: ")
            + BitConverter.ToString(bytes).Replace('-', ' ');
    }

    private async Task UploadSymbolAsync()
    {
        var forPrint = SymbolForPrint;
        var number = _symbolNumber.SelectedIndex <= 0 ? 1 : 2;
        var data = SymbolBytes();
        var what = (forPrint ? L("для печати", "басуу үчүн", "for printing", "baskı için", "chop uchun") : L("для дисплея", "дисплей үчүн", "for the display", "ekran için", "displey uchun"))
            + ", " + (number == 1 ? L("основной валюты", "негизги валютанын", "main currency", "ana para birimi", "asosiy valyuta") : L("дополнительной валюты", "кошумча валютанын", "second currency", "ikinci para birimi", "qo‘shimcha valyuta"));
        if (!PosConfirmDialog.Show(this,
                L("Загрузить символ в весы?", "Белгини таразага жүктөйсүзбү?", "Upload the sign to the scale?", "İşaret tartıya yüklensin mi?", "Belgi taroziga yuklansinmi?"),
                L($"Символ {what} на весах будет заменён.", $"Таразадагы {what} белгиси алмаштырылат.", $"The sign {what} on the scale will be replaced.", $"Tartıdaki işaret ({what}) değiştirilecek.", $"Tarozidagi belgi ({what}) almashtiriladi."),
                L("Загрузить", "Жүктөө", "Upload", "Yükle", "Yuklash"),
                L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")))
            return;

        await RunAsync(async scale =>
        {
            if (forPrint)
                await scale.LoadPrintSymbolAsync(number, data).ConfigureAwait(false);
            else
                await scale.LoadDisplaySymbolAsync(number, data).ConfigureAwait(false);
            return forPrint
                ? L("Символ загружен. Чтобы он печатался, включите «знаки валют» во вкладке «Печать и этикетка».", "Белги жүктөлдү. Басылышы үчүн «Басып чыгаруу жана этикетка» өтмөгүндө «валюта белгилерин» күйгүзүңүз.", "Sign uploaded. To print it, turn on “currency signs” in “Printing & label”.", "İşaret yüklendi. Basılması için “Baskı ve etiket”te “para birimi işareti”ni açın.", "Belgi yuklandi. Chop etilishi uchun «Chop etish va yorliq»da «valyuta belgilari»ni yoqing.")
                : L("Символ загружен. Проверьте его в меню весов «Символ основной валюты»: если он зеркальный — нажмите «Отразить ↔» и загрузите снова.", "Белги жүктөлдү. Аны тараза менюсундагы «Символ основной валюты» бөлүмүнөн текшериңиз: күзгүдөй тескери болсо — «Күзгүдөй буруу ↔» басып, кайра жүктөңүз.", "Sign uploaded. Check it in the scale menu “main currency sign”: if it is mirrored, press “Mirror ↔” and upload again.", "İşaret yüklendi. Tartı menüsündeki “ana para birimi işareti”nden kontrol edin: ayna gibi ters ise «Aynala ↔» basıp yeniden yükleyin.", "Belgi yuklandi. Uni tarozi menyusidagi «asosiy valyuta belgisi»da tekshiring: ko‘zgudek teskari bo‘lsa — «Ko‘zgu ↔» ni bosib, qayta yuklang.");
        }).ConfigureAwait(true);
    }

    // Заготовки. «с» — строчная кириллическая «эс»; «⃀» — знак сома (С с чертой снизу).
    // Для печати низ букв на строке 20 — как у цифр шрифта 5 (12×24), рядом с которыми символ стоит.
    private static readonly string[] PrintPresetS =
    {
        "............", "............", "............", "............", "............", "............",
        "............", "....#####...", "..#########.", ".###.....###", ".##.......##", "###.........",
        "##..........", "##..........", "##..........", "##..........", "###.........", ".##.......##",
        ".###.....###", "..#########.", "....#####...", "............", "............", "............",
    };

    private static readonly string[] PrintPresetSomSign =
    {
        "............", "............", "....######..", "..#########.", ".###.....###", ".##.......##",
        "###.........", "##..........", "##..........", "##..........", "##..........", "##..........",
        "##..........", "###.........", ".##.......##", ".###.....###", "..#########.", "....######..",
        "............", "############", "############", "............", "............", "............",
    };

    private static readonly string[] DisplayPresetS =
    {
        ".....", ".....", ".###.", "#....", "#....", "#...#", ".###.",
    };

    private static readonly string[] DisplayPresetSomSign =
    {
        ".###.", "#...#", "#....", "#....", "#...#", ".###.", "#####",
    };

    /// <summary>Рисует текст шрифтом Windows и укладывает его в сетку точек: рисуем крупно,
    /// находим границы «чернил», вписываем с сохранением пропорций и выравниваем по низу
    /// (у печатного символа — по строке 20, где кончаются цифры шрифта 5).</summary>
    private static bool[,] RenderTextToDots(string text, int cols, int rows, bool forPrint)
    {
        var result = new bool[rows, cols];
        const int w = 900, h = 360;
        try
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, w, h));
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Arial", FontStyle.Normal, text.Length >= 3 ? FontWeight.Normal : FontWeight.Bold), 220, Brushes.Black);
                ctx.DrawText(ft, new Point(20, 20));
            }

            var stride = w * 4;
            var buffer = new byte[stride * h];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            bool Ink(int x, int y) => buffer[y * stride + x * 4 + 1] < 128; // зелёный канал BGRA
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!Ink(x, y))
                    continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (maxX < 0)
                return result;

            var areaTop = forPrint ? 2 : 0;
            var areaBottom = forPrint ? 20 : rows - 1;
            var availH = areaBottom - areaTop + 1;
            var inkW = maxX - minX + 1;
            var inkH = maxY - minY + 1;
            // Несколько букв в 12 точек помещаются только узкими — разрешаем вытянуть их по
            // высоте (до 2,2 раза), так они читаются лучше, чем крошечные квадратные.
            var scaleX = Math.Min((double)cols / inkW, (double)availH / inkH);
            var scaleY = Math.Min((double)availH / inkH, scaleX * 2.2);
            var outW = Math.Max(1, (int)Math.Round(inkW * scaleX));
            var outH = Math.Max(1, (int)Math.Round(inkH * scaleY));
            var left = (cols - outW) / 2;
            var top = areaBottom - outH + 1;
            for (var r = 0; r < outH; r++)
            for (var c = 0; c < outW; c++)
            {
                // Доля «чернил» в соответствующем куске большой картинки.
                var sx0 = minX + (int)(c / scaleX); var sx1 = Math.Min(maxX + 1, minX + (int)((c + 1) / scaleX));
                var sy0 = minY + (int)(r / scaleY); var sy1 = Math.Min(maxY + 1, minY + (int)((r + 1) / scaleY));
                int ink = 0, all = 0;
                for (var y = sy0; y < Math.Max(sy0 + 1, sy1); y++)
                for (var x = sx0; x < Math.Max(sx0 + 1, sx1); x++)
                {
                    all++;
                    if (Ink(x, y))
                        ink++;
                }
                var rr = top + r; var cc = left + c;
                if (rr >= 0 && rr < rows && cc >= 0 && cc < cols)
                    result[rr, cc] = all > 0 && ink * 100 / all >= (text.Length >= 3 ? 28 : 40);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Символ валюты: не удалось нарисовать текст: {ex.Message}", "SCALES");
        }
        return result;
    }

    // ---------------------------------------------------------------- курс

    private NumericUpDown _rate = null!;
    private TextBlock _rateInfo = null!;
    private decimal? _rateLoaded;

    private void BuildRateCard(StackPanel panel)
    {
        var card = Card(L("Курс второй валюты", "Экинчи валютанын курсу", "Second currency rate", "İkinci para birimi kuru", "Ikkinchi valyuta kursi"),
            L("Нужен, только если на этикетке печатается «стоимость во 2-й валюте» (например, в долларах). Курс — сколько сомов за 1 единицу второй валюты. Включает подсчёт эквивалента продавец клавишей «Курс/Экв» или клавишей быстрого доступа (вкладка «Клавиатура»).",
              "Этикеткада «2-валютадагы наркы» (мисалы, доллар менен) басылса гана керек. Курс — экинчи валютанын 1 бирдигине канча сом. Эквивалентти сатуучу «Курс/Экв» баскычы же тез жетүү баскычы менен күйгүзөт («Клавиатура» өтмөгү).",
              "Only needed if the label prints the “total in 2nd currency” (e.g. in dollars). The rate is how many som per 1 unit of the second currency. The seller turns the equivalent on with the “Rate/Eqv” key or a quick-access key (“Keyboard” tab).",
              "Yalnızca etikette “2. para biriminde tutar” (örn. dolar) basılıyorsa gerekir. Kur — ikinci para biriminin 1 birimi kaç som. Karşılığı satıcı “Kur/Karş” tuşuyla veya hızlı tuşla açar (“Klavye” sekmesi).",
              "Faqat yorliqda «2-valyutadagi qiymat» (masalan, dollarda) chop etilsa kerak. Kurs — ikkinchi valyutaning 1 birligiga necha so‘m. Ekvivalentni sotuvchi «Kurs/Ekv» tugmasi yoki tezkor tugma bilan yoqadi («Klaviatura» bo‘limi)."),
            out var body);
        _rate = new NumericUpDown { Minimum = 0, Maximum = 9999.99m, Increment = 0.01m, FormatString = "0.00", MinWidth = 160, IsEnabled = false };
        body.Children.Add(Row(L("Курс, сом за 1 единицу", "Курс, 1 бирдик үчүн сом", "Rate, som per 1 unit", "Kur, 1 birim için som", "Kurs, 1 birlik uchun so‘m"), _rate));
        _rateInfo = new TextBlock { Classes = { "hint" } };
        body.Children.Add(_rateInfo);
        body.Children.Add(ButtonRow(
            MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from scale", "Tartıdan oku", "Tarozidan o‘qish"), false, async (_, _) => await RunAsync(async scale =>
            {
                var (on, rate) = await scale.GetCurrencyInfoAsync().ConfigureAwait(false);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _rateLoaded = rate;
                    _rate.Value = rate;
                    _rate.IsEnabled = true;
                    _rateInfo.Text = L("Подсчёт эквивалента сейчас: ", "Эквивалентти эсептөө азыр: ", "Equivalent calculation now: ", "Karşılık hesabı şu an: ", "Ekvivalent hisobi hozir: ") + YesNo(on);
                });
                return L("Курс прочитан.", "Курс окулду.", "Rate read.", "Kur okundu.", "Kurs o‘qildi.");
            }).ConfigureAwait(true)),
            MakeButton(L("Записать курс", "Курсту жазуу", "Write rate", "Kuru yaz", "Kursni yozish"), true, async (_, _) =>
            {
                if (_rateLoaded is null)
                {
                    ShowResult(L("Сначала «Прочитать с весов».", "Адегенде «Таразадан окуу».", "“Read from scale” first.", "Önce “Tartıdan oku”.", "Avval «Tarozidan o‘qish»."), true);
                    return;
                }
                var value = _rate.Value ?? 0m;
                if (value == _rateLoaded)
                {
                    ShowResult(L("Изменений нет — в весы ничего не отправлено.", "Өзгөртүү жок — таразага эч нерсе жөнөтүлгөн жок.", "No changes — nothing was sent to the scale.", "Değişiklik yok — tartıya bir şey gönderilmedi.", "O‘zgarish yo‘q — tarozga hech narsa yuborilmadi."), false);
                    return;
                }
                await RunAsync(async scale =>
                {
                    await scale.SetCurrencyRateAsync(value).ConfigureAwait(false);
                    _rateLoaded = value;
                    return L("Курс записан.", "Курс жазылды.", "Rate written.", "Kur yazıldı.", "Kurs yozildi.");
                }).ConfigureAwait(true);
            })));
        panel.Children.Add(card);
    }

    // ==================================================================================
    // Вкладка «Макет этикетки»
    // ==================================================================================

    private sealed class ElementRow
    {
        public ShtrikhLabelElement Element = null!;
        public CheckBox Visible = null!;
        public NumericUpDown X = null!;
        public NumericUpDown Y = null!;
        public ComboBox? Font;
        public NumericUpDown? Extra;   // высота ШК или правый нижний угол рамки (X)
        public NumericUpDown? Extra2;  // правый нижний угол рамки (Y)
        public Border Host = null!;
        public int LastY = 1;          // куда вернуть элемент при повторном включении
    }

    private ComboBox _lyTarget = null!;
    private ComboBox _lyCopyFrom = null!;
    private NumericUpDown _lyPaper = null!;
    private CheckBox _lyTestLine = null!;
    private ShtrikhLabelCanvas _lyCanvas = null!;
    private TextBlock _lyInfo = null!;
    private readonly List<ElementRow> _lyRows = new();
    private ShtrikhLabelLayout? _lyLayout;
    private ShtrikhLabelLayout? _lyLoaded;
    private bool _lySync;

    private void BuildLayoutTab()
    {
        AddCodeTab(L("Макет этикетки", "Этикетка макети", "Label layout", "Etiket düzeni", "Yorliq maketi"), out var panel);

        var card = Card(L("Свои форматы этикетки (Формат 1–5)", "Өз этикетка форматтары (Формат 1–5)", "Custom label formats (Format 1–5)", "Özel etiket formatları (Format 1–5)", "O‘z yorliq formatlari (Format 1–5)"),
            L("Где и каким шрифтом печатается каждый элемент. Стандартные форматы весов менять нельзя — возьмите их за основу кнопкой «Скопировать с формата». Элементы можно двигать мышью на схеме (шаг 1 мм) или вводить X/Y в таблице. Y = 0 (галочка снята) — элемент не печатается.",
              "Ар бир элемент кайда жана кайсы шрифт менен басылат. Таразанын стандарттык форматтарын өзгөртүүгө болбойт — «Форматтан көчүрүү» баскычы менен негиз кылып алыңыз. Элементтерди схемада чычкан менен жылдырса болот (1 мм кадам) же таблицага X/Y жазыңыз. Y = 0 (белги алынган) — элемент басылбайт.",
              "Where and with which font each element is printed. The scale's standard formats cannot be changed — use “Copy from format” to start from one. Drag elements on the diagram (1 mm steps) or type X/Y in the table. Y = 0 (unticked) — the element is not printed.",
              "Her öğenin nereye ve hangi yazı tipiyle basılacağı. Tartının standart formatları değiştirilemez — “Formattan kopyala” ile temel alın. Öğeleri şemada fareyle sürükleyin (1 mm adım) veya tabloda X/Y girin. Y = 0 (işaretsiz) — öğe basılmaz.",
              "Har bir element qayerda va qaysi shrift bilan chop etiladi. Tarozining standart formatlarini o‘zgartirib bo‘lmaydi — «Formatdan nusxalash» bilan asos qiling. Elementlarni sxemada sichqoncha bilan suring (1 mm qadam) yoki jadvalda X/Y kiriting. Y = 0 (belgi olingan) — element chop etilmaydi."),
            out var body);

        _lyTarget = new ComboBox { MinWidth = 220 };
        for (var f = F.FirstUserFormat; f <= F.LastUserFormat; f++)
            _lyTarget.Items.Add(LabelFormats()[f].Item2);
        _lyTarget.SelectedIndex = 0;
        body.Children.Add(Row(L("Какой формат редактируем", "Кайсы форматты оңдойбуз", "Format to edit", "Düzenlenecek format", "Tahrir qilinadigan format"), _lyTarget));
        body.Children.Add(ButtonRow(
            MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from scale", "Tartıdan oku", "Tarozidan o‘qish"), false, async (_, _) => await ReadLayoutAsync(null).ConfigureAwait(true)),
            MakeButton(L("Записать в весы", "Таразага жазуу", "Write to scale", "Tartıya yaz", "Taroziga yozish"), true, async (_, _) => await WriteLayoutAsync().ConfigureAwait(true)),
            MakeButton(L("Вернуть прочитанное", "Окулганды кайтаруу", "Revert to read values", "Okunan değerlere dön", "O‘qilganiga qaytarish"), false, (_, _) =>
            {
                if (_lyLoaded is not null)
                    ShowLayout(_lyLoaded.Clone());
            }),
            MakeButton(L("Печатать этим форматом", "Ушул формат менен басуу", "Print with this format", "Bu formatla bas", "Shu format bilan chop etish"), false, async (_, _) =>
            {
                var target = F.FirstUserFormat + Math.Max(0, _lyTarget.SelectedIndex);
                await RunAsync(async scale =>
                {
                    await scale.SetByteParamAsync(P.CmdSetLabelFormat, target).ConfigureAwait(false);
                    return L($"Весы печатают форматом «{LabelFormats()[target].Item2}».", $"Тараза «{LabelFormats()[target].Item2}» форматы менен басат.", $"The scale prints with “{LabelFormats()[target].Item2}”.", $"Tartı “{LabelFormats()[target].Item2}” ile basıyor.", $"Tarozi «{LabelFormats()[target].Item2}» formati bilan chop etadi.");
                }).ConfigureAwait(true);
            })));

        _lyCopyFrom = new ComboBox { MinWidth = 220 };
        foreach (var (_, label) in LabelFormats())
            _lyCopyFrom.Items.Add(label);
        _lyCopyFrom.SelectedIndex = 1;
        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        copyRow.Children.Add(_lyCopyFrom);
        copyRow.Children.Add(MakeButton(L("Скопировать с формата", "Форматтан көчүрүү", "Copy from format", "Formattan kopyala", "Formatdan nusxalash"), false,
            async (_, _) => await ReadLayoutAsync(_lyCopyFrom.SelectedIndex).ConfigureAwait(true)));
        body.Children.Add(Row(L("Взять за основу", "Негиз катары алуу", "Start from", "Temel al", "Asos qilish"), copyRow));

        _lyPaper = new NumericUpDown { Minimum = 0, Maximum = F.MaxY, Increment = 1, FormatString = "0", MinWidth = 140, IsEnabled = false };
        _lyPaper.ValueChanged += (_, _) =>
        {
            if (_lySync || _lyLayout is null)
                return;
            _lyLayout.PaperLength = (int)(_lyPaper.Value ?? 0);
            LayoutChanged();
        };
        body.Children.Add(Row(L("Длина этикетки, мм", "Этикетканын узундугу, мм", "Label length, mm", "Etiket uzunluğu, mm", "Yorliq uzunligi, mm"), _lyPaper));
        _lyTestLine = new CheckBox { Content = L("Проверочная линия в штрих-коде (видно сгоревшие точки головки)", "Штрих-коддогу текшерүү сызыгы (күйгөн чекиттер көрүнөт)", "Test line in the barcode (shows dead head dots)", "Barkodda test çizgisi (bozuk kafa noktalarını gösterir)", "Shtrix-koddagi tekshiruv chizig‘i (kuygan nuqtalarni ko‘rsatadi)"), IsEnabled = false };
        _lyTestLine.IsCheckedChanged += (_, _) =>
        {
            if (_lySync || _lyLayout is null)
                return;
            _lyLayout.BarcodeTestLine = _lyTestLine.IsChecked == true;
        };
        body.Children.Add(_lyTestLine);

        _lyInfo = new TextBlock { Classes = { "label" } };
        body.Children.Add(_lyInfo);

        _lyCanvas = new ShtrikhLabelCanvas { Caption = ElementName, AllowDrag = true, HorizontalAlignment = HorizontalAlignment.Left, Width = 440 };
        _lyCanvas.SelectionChanged += (_, key) => HighlightRow(key);
        _lyCanvas.ElementMoved += (_, key) =>
        {
            var row = _lyRows.FirstOrDefault(r => r.Element.Key == key);
            if (row is not null)
                SyncRow(row);
            LayoutChanged(refreshCanvas: false);
        };
        body.Children.Add(_lyCanvas);

        // Таблица элементов.
        var table = new StackPanel { Spacing = 2 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("230,118,118,*") };
        void Head(string text, int col)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Classes = { "label" } };
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }
        Head(L("Элемент (галочка — печатать)", "Элемент (белги — басуу)", "Element (tick — print)", "Öğe (işaret — bas)", "Element (belgi — chop etish)"), 0);
        Head("X, " + L("мм", "мм", "mm", "mm", "mm"), 1);
        Head("Y, " + L("мм", "мм", "mm", "mm", "mm"), 2);
        Head(L("Шрифт / размер", "Шрифт / өлчөм", "Font / size", "Yazı tipi / boyut", "Shrift / o‘lcham"), 3);
        table.Children.Add(header);
        foreach (var e in F.Elements)
            table.Children.Add(BuildElementRow(e));
        body.Children.Add(table);
        body.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            Text = L("Число строк наименования товара меняется во вкладке «Печать и этикетка», строк сообщения — только в меню весов. Рисунки 1–4 печатаются, только если они включены у товара. Коды товара и ПЛУ: весы старше 3.0 их не печатают.",
                  "Товардын аталышынын саптарынын саны «Басып чыгаруу жана этикетка» өтмөгүндө, билдирүүнүн саптары — таразанын менюсунда гана өзгөрөт. 1–4 сүрөттөр товарда күйгүзүлсө гана басылат. Товар коду жана ПЛУ: 3.0дөн эски таразалар баспайт.",
                  "The number of item-name lines is set in “Printing & label”, message lines only in the scale menu. Pictures 1–4 print only if enabled for the item. Item code and PLU are not printed by scales older than 3.0.",
                  "Ürün adı satır sayısı “Baskı ve etiket”te, mesaj satırları yalnızca tartı menüsünde değişir. Resim 1–4 yalnızca üründe açıksa basılır. Ürün kodu ve PLU: 3.0'dan eski tartılar basmaz.",
                  "Tovar nomi qatorlari soni «Chop etish va yorliq»da, xabar qatorlari faqat tarozi menyusida o‘zgaradi. 1–4 rasmlar faqat tovarda yoqilgan bo‘lsa chop etiladi. Tovar kodi va PLU: 3.0 dan eski tarozilar chop etmaydi."),
        });
        panel.Children.Add(card);
        ShowLayout(null);
    }

    private Control BuildElementRow(ShtrikhLabelElement e)
    {
        var row = new ElementRow { Element = e };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,118,118,*") };
        row.Visible = new CheckBox { Content = ElementName(e), VerticalAlignment = VerticalAlignment.Center };
        row.X = new NumericUpDown { Minimum = 0, Maximum = F.MaxX, Increment = 1, FormatString = "0", Width = 112, HorizontalAlignment = HorizontalAlignment.Left };
        row.Y = new NumericUpDown { Minimum = 0, Maximum = F.MaxY, Increment = 1, FormatString = "0", Width = 112, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(row.Visible);
        Grid.SetColumn(row.X, 1);
        grid.Children.Add(row.X);
        Grid.SetColumn(row.Y, 2);
        grid.Children.Add(row.Y);

        Control? third = null;
        if (e.HasFont)
        {
            row.Font = FontCombo();
            row.Font.SelectionChanged += (_, _) =>
            {
                if (_lySync || _lyLayout is null || row.Font.SelectedIndex < 0)
                    return;
                _lyLayout.SetFont(e, row.Font.SelectedIndex);
                LayoutChanged();
            };
            third = row.Font;
        }
        else if (e.Kind == ShtrikhElementKind.Barcode)
        {
            row.Extra = new NumericUpDown { Minimum = 0, Maximum = F.MaxY, Increment = 1, FormatString = "0", Width = 112 };
            row.Extra.ValueChanged += (_, _) =>
            {
                if (_lySync || _lyLayout is null)
                    return;
                _lyLayout.BarcodeHeight = (int)(row.Extra.Value ?? 0);
                LayoutChanged();
            };
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            p.Children.Add(new TextBlock { Text = L("высота, мм", "бийиктиги, мм", "height, mm", "yükseklik, mm", "balandlik, mm"), Classes = { "label" } });
            p.Children.Add(row.Extra);
            third = p;
        }
        else if (e.Kind == ShtrikhElementKind.Frame)
        {
            row.Extra = new NumericUpDown { Minimum = 0, Maximum = F.MaxX + 1, Increment = 1, FormatString = "0", Width = 112 };
            row.Extra2 = new NumericUpDown { Minimum = 0, Maximum = F.MaxY, Increment = 1, FormatString = "0", Width = 112 };
            void FrameChanged()
            {
                if (_lySync || _lyLayout is null)
                    return;
                var f = _lyLayout.Frame;
                _lyLayout.Frame = (f.Left, f.Top, (int)(row.Extra!.Value ?? 0), (int)(row.Extra2!.Value ?? 0));
                LayoutChanged();
            }
            row.Extra.ValueChanged += (_, _) => FrameChanged();
            row.Extra2.ValueChanged += (_, _) => FrameChanged();
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            p.Children.Add(new TextBlock { Text = L("до X/Y", "X/Y чейин", "to X/Y", "X/Y'ye", "X/Y gacha"), Classes = { "label" } });
            p.Children.Add(row.Extra);
            p.Children.Add(row.Extra2);
            third = p;
        }
        if (third is not null)
        {
            Grid.SetColumn(third, 3);
            grid.Children.Add(third);
        }

        row.Visible.IsCheckedChanged += (_, _) =>
        {
            if (_lySync || _lyLayout is null)
                return;
            var (x, y) = _lyLayout.GetPosition(e);
            if (row.Visible.IsChecked == true)
                _lyLayout.SetPosition(e, x, y > 0 ? y : Math.Max(1, row.LastY));
            else
            {
                if (y > 0)
                    row.LastY = y;
                _lyLayout.SetPosition(e, x, 0);
            }
            SyncRow(row);
            LayoutChanged();
        };
        row.X.ValueChanged += (_, _) => PositionEdited(row);
        row.Y.ValueChanged += (_, _) => PositionEdited(row);

        row.Host = new Border { Child = grid, Padding = new Thickness(4, 2), CornerRadius = new CornerRadius(4) };
        row.Host.PointerPressed += (_, _) =>
        {
            _lyCanvas.SelectedKey = e.Key;
            _lyCanvas.InvalidateVisual();
            HighlightRow(e.Key);
        };
        _lyRows.Add(row);
        return row.Host;
    }

    private void PositionEdited(ElementRow row)
    {
        if (_lySync || _lyLayout is null)
            return;
        var x = (int)(row.X.Value ?? 0);
        var y = (int)(row.Y.Value ?? 0);
        _lyLayout.SetPosition(row.Element, x, y);
        if (y > 0)
            row.LastY = y;
        SyncRow(row);
        LayoutChanged();
    }

    private void SyncRow(ElementRow row)
    {
        if (_lyLayout is not { } layout)
            return;
        _lySync = true;
        try
        {
            var available = layout.HasEx || row.Element.Block == 0;
            var (x, y) = layout.GetPosition(row.Element);
            row.Visible.IsChecked = y > 0;
            row.X.Value = Math.Min(x, F.MaxX);
            row.Y.Value = Math.Min(y, F.MaxY);
            if (y > 0)
                row.LastY = y;
            if (row.Font is not null)
            {
                row.Font.SelectedIndex = layout.HasFonts ? Math.Clamp(layout.GetFont(row.Element), 0, 6) : -1;
                row.Font.IsEnabled = layout.HasFonts;
            }
            if (row.Element.Kind == ShtrikhElementKind.Barcode && row.Extra is not null)
                row.Extra.Value = layout.BarcodeHeight;
            if (row.Element.Kind == ShtrikhElementKind.Frame && row.Extra is not null && row.Extra2 is not null)
            {
                var f = layout.Frame;
                row.Extra.Value = Math.Min(f.Right, F.MaxX + 1);
                row.Extra2.Value = Math.Min(f.Bottom, F.MaxY);
            }
            row.Host.IsEnabled = available;
        }
        finally
        {
            _lySync = false;
        }
    }

    private void HighlightRow(string? key)
    {
        foreach (var r in _lyRows)
            r.Host.Background = r.Element.Key == key ? ThemeBrush("BrushSurfaceSubtle", Brushes.LightGray) : Brushes.Transparent;
        var selected = _lyRows.FirstOrDefault(r => r.Element.Key == key);
        selected?.Host.BringIntoView();
    }

    private void ShowLayout(ShtrikhLabelLayout? layout)
    {
        _lyLayout = layout;
        var enabled = layout is not null;
        _lySync = true;
        try
        {
            _lyPaper.IsEnabled = enabled;
            _lyTestLine.IsEnabled = enabled && layout!.HasEx;
            _lyPaper.Value = layout?.PaperLength ?? 0;
            _lyTestLine.IsChecked = layout?.BarcodeTestLine == true;
        }
        finally
        {
            _lySync = false;
        }
        foreach (var r in _lyRows)
        {
            if (layout is null)
                r.Host.IsEnabled = false;
            else
                SyncRow(r);
        }
        LayoutChanged();
    }

    private void LayoutChanged(bool refreshCanvas = true)
    {
        _lyCanvas.Layout = _lyLayout;
        _lyCanvas.Lengths = _labelLengths;
        _lyCanvas.NameLines = _labelNameLines;
        _lyCanvas.MessageLines = _labelMessageLines;
        _lyCanvas.SampleText = e => e.Kind == ShtrikhElementKind.UserText && e.Key[^1] - '1' is >= 0 and < 5 && !string.IsNullOrWhiteSpace(_wzUserTexts[e.Key[^1] - '1']) ? _wzUserTexts[e.Key[^1] - '1'] : null;
        if (refreshCanvas)
            _lyCanvas.Refresh();
        else
            _lyCanvas.InvalidateVisual();

        if (_lyLayout is null)
        {
            _lyInfo.Text = L("Выберите формат и нажмите «Прочитать с весов».", "Форматты тандап «Таразадан окуу» басыңыз.", "Choose a format and press “Read from scale”.", "Bir format seçip “Tartıdan oku”ya basın.", "Formatni tanlab «Tarozidan o‘qish»ni bosing.");
            _lyInfo.Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray);
            return;
        }
        var problems = ShtrikhLabelCanvas.FindProblems(_lyLayout, _labelLengths, _labelNameLines, _labelMessageLines);
        var prefix = L($"{LabelFormats()[_lyLayout.Format].Item2}: ", $"{LabelFormats()[_lyLayout.Format].Item2}: ", $"{LabelFormats()[_lyLayout.Format].Item2}: ", $"{LabelFormats()[_lyLayout.Format].Item2}: ", $"{LabelFormats()[_lyLayout.Format].Item2}: ");
        if (!_lyLayout.HasEx)
            prefix += L("весы не отдали доп. элементы (старый протокол) — их не трогаем. ", "тараза кошумча элементтерди берген жок (эски протокол) — аларга тийбейбиз. ", "the scale gave no extra elements (old protocol) — they are left as is. ", "tartı ek öğeleri vermedi (eski protokol) — dokunulmuyor. ", "tarozi qo‘shimcha elementlarni bermadi (eski protokol) — ularga tegmaymiz. ");
        _lyInfo.Text = prefix + ProblemsText(_lyLayout);
        _lyInfo.Foreground = problems.Bad.Count > 0 ? ThemeBrush("BrushWarning", Brushes.DarkOrange) : ThemeBrush("BrushText", Brushes.Black);
    }

    /// <summary>«Прочитать с весов» (copyFrom = null) — читает выбранный свой формат;
    /// «Скопировать с формата N» — читает формат N, но в редакторе он станет выбранным своим.</summary>
    private async Task ReadLayoutAsync(int? copyFrom)
    {
        var target = F.FirstUserFormat + Math.Max(0, _lyTarget.SelectedIndex);
        var source = copyFrom ?? target;
        await RunAsync(async scale =>
        {
            var layout = await scale.GetLabelLayoutAsync(source).ConfigureAwait(false);
            await LoadLabelMetricsAsync(scale).ConfigureAwait(false);
            var texts = new string[5];
            for (var i = 1; i <= 5; i++)
                texts[i - 1] = await scale.GetTextParamAsync(P.CmdGetUserText, P.UserTextFieldLength, i).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                texts.CopyTo(_wzUserTexts, 0);
                if (copyFrom is null)
                {
                    _lyLoaded = layout.Clone();
                    ShowLayout(layout);
                }
                else
                {
                    ShowLayout(layout.Clone(target));
                }
            });
            return copyFrom is null
                ? L("Формат прочитан.", "Формат окулду.", "Format read.", "Format okundu.", "Format o‘qildi.")
                : L($"Скопировано с «{LabelFormats()[source].Item2}». Поправьте и нажмите «Записать в весы» — запишется в «{LabelFormats()[target].Item2}».",
                    $"«{LabelFormats()[source].Item2}» форматынан көчүрүлдү. Оңдоп «Таразага жазуу» басыңыз — «{LabelFormats()[target].Item2}» форматына жазылат.",
                    $"Copied from “{LabelFormats()[source].Item2}”. Adjust and press “Write to scale” — it goes to “{LabelFormats()[target].Item2}”.",
                    $"“{LabelFormats()[source].Item2}” formatından kopyalandı. Düzeltip “Tartıya yaz”a basın — “{LabelFormats()[target].Item2}” formatına yazılır.",
                    $"«{LabelFormats()[source].Item2}» formatidan nusxalandi. Tuzatib «Taroziga yozish»ni bosing — «{LabelFormats()[target].Item2}» formatiga yoziladi.");
        }).ConfigureAwait(true);
    }

    private async Task WriteLayoutAsync()
    {
        if (_lyLayout is null)
        {
            ShowResult(L("Сначала «Прочитать с весов» — без этого можно затереть формат пустыми значениями.", "Адегенде «Таразадан окуу» — антпесе формат бош маанилер менен өчүп калышы мүмкүн.", "Press “Read from scale” first — otherwise the format could be overwritten with empty values.", "Önce “Tartıdan oku” — aksi halde format boş değerlerle silinebilir.", "Avval «Tarozidan o‘qish» — aks holda format bo‘sh qiymatlar bilan o‘chib ketishi mumkin."), true);
            return;
        }
        var layout = _lyLayout.Clone(F.FirstUserFormat + Math.Max(0, _lyTarget.SelectedIndex));
        var name = LabelFormats()[layout.Format].Item2;
        var problems = ShtrikhLabelCanvas.FindProblems(layout, _labelLengths, _labelNameLines, _labelMessageLines);
        if (!PosConfirmDialog.Show(this,
                L("Записать формат в весы?", "Форматты таразага жазасызбы?", "Write the format to the scale?", "Format tartıya yazılsın mı?", "Format taroziga yozilsinmi?"),
                L($"{name} на весах будет перезаписан.", $"Таразадагы {name} кайра жазылат.", $"{name} on the scale will be overwritten.", $"Tartıdaki {name} üzerine yazılacak.", $"Tarozidagi {name} qayta yoziladi.")
                + (problems.Bad.Count > 0 ? " " + ProblemsText(layout) : ""),
                L("Записать", "Жазуу", "Write", "Yaz", "Yozish"),
                L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")))
            return;

        await RunAsync(async scale =>
        {
            await scale.WriteLabelLayoutAsync(layout).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _lyLoaded = layout.Clone());
            return L($"{name} записан. Чтобы весы печатали им — «Печатать этим форматом».", $"{name} жазылды. Тараза аны менен басышы үчүн — «Ушул формат менен басуу».", $"{name} written. To print with it — “Print with this format”.", $"{name} yazıldı. Bununla basmak için — “Bu formatla bas”.", $"{name} yozildi. U bilan chop etish uchun — «Shu format bilan chop etish».");
        }).ConfigureAwait(true);
    }

    // ==================================================================================
    // Редактор символа по точкам
    // ==================================================================================

    /// <summary>Сетка точек: щелчок меняет точку, протяжка мышью рисует тем же цветом.</summary>
    private sealed class PixelEditor : Control
    {
        public int Cols { get; private set; } = F.PrintSymbolWidth;
        public int Rows { get; private set; } = F.PrintSymbolHeight;
        public bool[,] Pixels { get; private set; } = new bool[F.PrintSymbolHeight, F.PrintSymbolWidth];
        private double _cell = 14;
        private bool? _paint;

        public event EventHandler? Changed;

        public void Configure(int cols, int rows, double cell, string[] preset)
        {
            Cols = cols;
            Rows = rows;
            _cell = cell;
            Pixels = new bool[rows, cols];
            Load(preset);
            InvalidateMeasure();
        }

        public void Load(string[] preset)
        {
            Pixels = new bool[Rows, Cols];
            for (var r = 0; r < Rows && r < preset.Length; r++)
            for (var c = 0; c < Cols && c < preset[r].Length; c++)
                Pixels[r, c] = preset[r][c] == '#';
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPixels(bool[,] pixels)
        {
            Pixels = new bool[Rows, Cols];
            for (var r = 0; r < Rows && r < pixels.GetLength(0); r++)
            for (var c = 0; c < Cols && c < pixels.GetLength(1); c++)
                Pixels[r, c] = pixels[r, c];
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear() => SetPixels(new bool[Rows, Cols]);

        public void Mirror()
        {
            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols / 2; c++)
                (Pixels[r, c], Pixels[r, Cols - 1 - c]) = (Pixels[r, Cols - 1 - c], Pixels[r, c]);
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Invert()
        {
            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                Pixels[r, c] = !Pixels[r, c];
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(Cols * _cell + 1 + 16 + Cols * 3, Math.Max(Rows * _cell + 1, Rows * 3));

        private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#D1D5DB")), 1);
        private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#111827"));

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(Brushes.White, new Rect(0, 0, Cols * _cell + 1, Rows * _cell + 1));
            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var rect = new Rect(c * _cell, r * _cell, _cell, _cell);
                if (Pixels[r, c])
                    context.FillRectangle(Dot, rect);
                context.DrawRectangle(null, GridPen, rect);
            }
            // Рядом — тот же символ в размере «примерно как на бумаге» (×3).
            var ox = Cols * _cell + 16;
            context.FillRectangle(Brushes.White, new Rect(ox, 0, Cols * 3, Rows * 3));
            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                if (Pixels[r, c])
                    context.FillRectangle(Dot, new Rect(ox + c * 3, r * 3, 3, 3));
            }
        }

        private (int R, int C)? Cell(Point p)
        {
            var c = (int)(p.X / _cell);
            var r = (int)(p.Y / _cell);
            return r >= 0 && r < Rows && c >= 0 && c < Cols ? (r, c) : null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Cell(e.GetPosition(this)) is not { } cell)
                return;
            _paint = !Pixels[cell.R, cell.C];
            Pixels[cell.R, cell.C] = _paint.Value;
            e.Pointer.Capture(this);
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_paint is not { } value || Cell(e.GetPosition(this)) is not { } cell || Pixels[cell.R, cell.C] == value)
                return;
            Pixels[cell.R, cell.C] = value;
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _paint = null;
            e.Pointer.Capture(null);
        }
    }
}
