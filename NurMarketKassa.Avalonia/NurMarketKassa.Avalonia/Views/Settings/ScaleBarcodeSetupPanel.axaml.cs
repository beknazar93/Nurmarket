using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-09-28: «Штрих-код: вес / сумма» — мастер «Настроить по этикетке», правила по
/// префиксам 20–29 и пояснение раскладки компании (см. ScaleBarcodeRules). Стоит в Настройки →
/// Весы и в окне «Настроить по этикетке» из окна «Весы».</summary>
public partial class ScaleBarcodeSetupPanel : UserControl
{
    /// <summary>Правило какого-то префикса поменялось — родитель обновляет свои примеры.</summary>
    public event Action? RulesChanged;

    /// <summary>Краткий вид (раздел «Весы с этикетками»): только список правил по префиксам и
    /// кнопка «Настроить по этикетке…» (событие <see cref="WizardRequested"/>).</summary>
    public bool RulesOnly { get; set; }

    /// <summary>В кратком виде нажали «Настроить по этикетке…».</summary>
    public event Action? WizardRequested;

    /// <summary>Префиксы, показанные в списке сверх «используемых» (из мастера, «Все префиксы»).</summary>
    private readonly HashSet<string> _extraPrefixes = new(StringComparer.Ordinal);
    private bool _showAllPrefixes;
    private ScaleLabelReading? _reading;

    public ScaleBarcodeSetupPanel()
    {
        InitializeComponent();
    }

    private void Panel_Loaded(object? sender, RoutedEventArgs e) => Refresh();

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    /// <summary>Перерисовывает список правил, пример и пояснение (язык, режим компании, правила).</summary>
    public void Refresh()
    {
        WizardCard.IsVisible = !RulesOnly;
        CompanyCard.IsVisible = !RulesOnly;
        OpenWizardButton.IsVisible = RulesOnly;
        LabelCodeBox.Watermark = L("Отсканируйте этикетку или введите 13 цифр", "Этикетканы сканерлеңиз же 13 сан киргизиңиз",
            "Scan the label or type 13 digits", "Etiketi okutun veya 13 hane girin", "Yorliqni skanerlang yoki 13 raqam kiriting");
        BuildRules();
        BuildCompanyExample();
        if (_reading is not null)
            ShowReading(_reading);
    }

    // ------------------------------------------------------------------ правила по префиксам

    private void BuildRules()
    {
        var mode = (NurMarketKassa.Core.Application.WeightBarcodeParser.Mode ?? "auto").ToLowerInvariant();
        var modeText = mode switch
        {
            "weight" => L("«по весу»", "«салмак боюнча»", "“by weight”", "“ağırlığa göre”", "«vazn bo‘yicha»"),
            "amount" => L("«по сумме»", "«сумма боюнча»", "“by amount”", "“tutara göre”", "«summa bo‘yicha»"),
            _ => L("«авто»: 25 — сумма, остальные — вес", "«авто»: 25 — сумма, калгандары — салмак", "“auto”: 25 = amount, others = weight", "“otomatik”: 25 = tutar, diğerleri = ağırlık", "«avto»: 25 — summa, qolganlari — vazn"),
        };
        RulesDescText.Text = L(
            $"Что весы печатают в штрихкоде после кода товара — ВЕС или СУММУ. Выберите для каждого префикса (первые две цифры штрихкода). Правило кассы важнее режима компании с сайта ({modeText}) и действует сразу, без перезапуска. Для префиксов 21–24 и 26–29 без правила касса спросит при первом скане этикетки.",
            $"Тараза штрих-коддо товар кодунан кийин эмнени басат — САЛМАКТЫ же СУММАНЫ. Ар бир префикс үчүн тандаңыз (штрих-коддун алгачкы эки саны). Кассанын эрежеси сайттагы компаниянын режиминен ({modeText}) маанилүүрөөк жана дароо, кайра жүргүзбөй иштейт. Эрежеси жок 21–24 жана 26–29 префикстер үчүн касса этикетканы биринчи сканерлегенде сурайт.",
            $"What the scale prints in the barcode after the item code — WEIGHT or AMOUNT. Choose for each prefix (the first two digits of the barcode). The till's rule overrides the company mode from the website ({modeText}) and works immediately, no restart. For prefixes 21–24 and 26–29 without a rule the till asks on the first label scan.",
            $"Tartının barkodda ürün kodundan sonra ne bastığı — AĞIRLIK mı TUTAR mı. Her önek için seçin (barkodun ilk iki hanesi). Kasa kuralı sitedeki şirket modundan ({modeText}) önceliklidir ve hemen, yeniden başlatmadan çalışır. Kuralı olmayan 21–24 ve 26–29 önekleri için kasa ilk etiket okutmasında sorar.",
            $"Tarozi shtrix-kodda tovar kodidan keyin nimani chop etadi — VAZNNI yoki SUMMANI. Har bir prefiks uchun tanlang (shtrix-kodning dastlabki ikki raqami). Kassa qoidasi saytdagi kompaniya rejimidan ({modeText}) ustun va darhol, qayta ishga tushirmasdan ishlaydi. Qoidasi yo‘q 21–24 va 26–29 prefikslari uchun kassa yorliqni birinchi skanerlashda so‘raydi.");

        var prefixes = _showAllPrefixes
            ? ScaleBarcodeRules.AllPrefixes.ToList()
            : ScaleBarcodeRules.PrefixesInUse(_extraPrefixes);

        RulesGrid.Children.Clear();
        foreach (var prefix in prefixes)
            RulesGrid.Children.Add(BuildRuleRow(prefix));

        ShowAllButton.Content = _showAllPrefixes
            ? L("Только используемые префиксы", "Колдонулган префикстер гана", "Only prefixes in use", "Yalnızca kullanılan önekler", "Faqat ishlatilgan prefikslar")
            : L("Все префиксы 20–29", "Бардык 20–29 префикстер", "All prefixes 20–29", "Tüm 20–29 önekleri", "Barcha 20–29 prefikslar");
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        _showAllPrefixes = !_showAllPrefixes;
        BuildRules();
    }

    private Control BuildRuleRow(string prefix)
    {
        var kind = ScaleBarcodeRules.Effective(prefix);
        var isExplicit = ScaleBarcodeRules.IsExplicit(prefix);
        // 1.17.27: для префиксов 21–24/26–29 без правила касса сама спрашивает при первом скане
        // (BasketPanelViewModel.ConfirmWeightBarcodeKindAsync) — ни «Вес», ни «Сумма» ещё не выбраны.
        var asks = ScaleBarcodeRules.AsksOnScan(prefix);

        var weight = new RadioButton
        {
            Classes = { "RulePill" },
            GroupName = "rule" + prefix + GetHashCode(),
            Content = L("Вес", "Салмак", "Weight", "Ağırlık", "Vazn"),
            IsChecked = !asks && kind == WeightBarcodeValueKind.Weight,
        };
        var amount = new RadioButton
        {
            Classes = { "RulePill" },
            GroupName = "rule" + prefix + GetHashCode(),
            Content = L("Сумма", "Сумма", "Amount", "Tutar", "Summa"),
            IsChecked = !asks && kind == WeightBarcodeValueKind.Amount,
        };
        weight.Click += (_, _) => SetRule(prefix, WeightBarcodeValueKind.Weight);
        amount.Click += (_, _) => SetRule(prefix, WeightBarcodeValueKind.Amount);

        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pills.Children.Add(weight);
        pills.Children.Add(amount);

        var source = new TextBlock
        {
            Text = isExplicit
                ? L("правило кассы", "кассанын эрежеси", "till rule", "kasa kuralı", "kassa qoidasi")
                : asks
                    ? L("касса спросит при первом скане", "касса биринчи сканда сурайт", "the till asks on the first scan", "kasa ilk okutmada sorar", "kassa birinchi skanda so‘raydi")
                    : L("по режиму компании", "компаниянын режими боюнча", "company mode", "şirket moduna göre", "kompaniya rejimi bo‘yicha"),
            FontSize = 11,
            FontWeight = isExplicit ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Brush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var example = new TextBlock
        {
            Text = asks ? "" : ScaleBarcodeRules.ExampleFor(prefix, kind),
            FontSize = 12,
            Foreground = Brush("BrushText", Brushes.Black),
            TextWrapping = TextWrapping.Wrap,
        };

        // Строка правила: [префикс] [Вес][Сумма] пояснение / живой пример.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var badge = new Border
        {
            Width = 48,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = Brush(asks ? "BrushPanelSoft" : kind == WeightBarcodeValueKind.Amount ? "BrushWarningSoft" : "BrushAccentSoft", Brushes.LightGray),
            BorderBrush = Brush(asks ? "BrushBorderStrong" : kind == WeightBarcodeValueKind.Amount ? "BrushWarning" : "BrushAccent", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = prefix,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                Foreground = Brush("BrushText", Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(badge);
        Grid.SetColumn(pills, 1);
        grid.Children.Add(pills);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        texts.Children.Add(source);
        if (!string.IsNullOrEmpty(example.Text))
            texts.Children.Add(example);
        Grid.SetColumn(texts, 2);
        grid.Children.Add(texts);

        return new Border
        {
            Background = Brush("BrushSurfaceSubtle", Brushes.WhiteSmoke),
            BorderBrush = Brush("BrushBorder", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private void SetRule(string prefix, WeightBarcodeValueKind kind)
    {
        ScaleBarcodeRules.ApplyAndSave(prefix, kind);
        BuildRules();
        if (_reading is not null && _reading.Prefix == prefix)
            ShowReading(_reading with { CurrentKind = kind });
        RulesChanged?.Invoke();
    }

    private void OpenWizard_Click(object? sender, RoutedEventArgs e) => WizardRequested?.Invoke();

    private void ResetRules_Click(object? sender, RoutedEventArgs e)
    {
        ScaleBarcodeRules.ResetAll();
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log("Весы: правила штрихкода по префиксам сброшены к режиму компании", "SCALES");
        BuildRules();
        if (_reading is not null)
            ShowReading(_reading with { CurrentKind = ScaleBarcodeRules.Effective(_reading.Prefix) });
        RulesChanged?.Invoke();
    }

    // ------------------------------------------------------------------ мастер «Настроить по этикетке»

    private void LabelCodeBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ParseLabel();
            e.Handled = true;
        }
    }

    /// <summary>Сканер вводит 13 цифр почти мгновенно — разбираем, как только их 13, не дожидаясь
    /// Enter (не все сканеры его шлют).</summary>
    private void LabelCodeBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var digits = (LabelCodeBox.Text ?? "").Count(char.IsDigit);
        if (digits == 13)
            ParseLabel();
    }

    private void Parse_Click(object? sender, RoutedEventArgs e) => ParseLabel();

    /// <summary>Разбирает введённый штрихкод и показывает два варианта.</summary>
    public void ParseLabel()
    {
        var reading = ScaleBarcodeRules.Read(LabelCodeBox.Text, out var error);
        _reading = reading;
        WeightOptionButton.Classes.Remove("chosen");
        AmountOptionButton.Classes.Remove("chosen");
        if (reading is null)
        {
            WizardSegments.IsVisible = false;
            WizardProductText.IsVisible = false;
            WizardChoice.IsVisible = false;
            ShowWizardResult("⚠ " + error, isError: true);
            return;
        }

        WizardResultBorder.IsVisible = false;
        ShowReading(reading);
    }

    private void ShowReading(ScaleLabelReading reading)
    {
        WizardSegments.IsVisible = true;
        FillSegments(WizardSegments, reading.Prefix, reading.ProductCode, reading.ValueDigits, reading.Barcode[12..]);

        WizardProductText.IsVisible = true;
        var price = ScaleBarcodeRules.Money(reading.PricePerKg);
        WizardProductText.Text = reading.Product is { } p
            ? L($"Товар: {p.Title} (код {reading.ProductCode.TrimStart('0')}), цена {price} сом за кг.",
                $"Товар: {p.Title} (коду {reading.ProductCode.TrimStart('0')}), баасы кг үчүн {price} сом.",
                $"Item: {p.Title} (code {reading.ProductCode.TrimStart('0')}), price {price} som per kg.",
                $"Ürün: {p.Title} (kod {reading.ProductCode.TrimStart('0')}), fiyat kg başına {price} som.",
                $"Tovar: {p.Title} (kod {reading.ProductCode.TrimStart('0')}), narxi kg uchun {price} so‘m.")
            : L($"Товар с кодом {reading.ProductCode.TrimStart('0')} в каталоге кассы не найден — сравните числа с этикеткой без цены.",
                $"{reading.ProductCode.TrimStart('0')} коддуу товар кассанын каталогунан табылган жок — сандарды этикетка менен баасыз салыштырыңыз.",
                $"No item with code {reading.ProductCode.TrimStart('0')} in the till catalog — compare the numbers with the label without the price.",
                $"Kasa kataloğunda {reading.ProductCode.TrimStart('0')} kodlu ürün yok — sayıları etiketle fiyatsız karşılaştırın.",
                $"Kassa katalogida {reading.ProductCode.TrimStart('0')} kodli tovar yo‘q — sonlarni yorliq bilan narxsiz solishtiring.");

        WizardChoice.IsVisible = true;
        WizardQuestion.Text = L($"Что напечатано на этикетке? Нажмите вариант, который совпадает, — касса запомнит его для префикса {reading.Prefix}:",
            $"Этикеткага эмне басылган? Дал келген вариантты басыңыз — касса аны {reading.Prefix} префикси үчүн эстеп калат:",
            $"What is printed on the label? Press the matching option — the till will remember it for prefix {reading.Prefix}:",
            $"Etikette ne basılı? Eşleşen seçeneğe basın — kasa bunu {reading.Prefix} öneki için hatırlar:",
            $"Yorliqda nima chop etilgan? Mos variantni bosing — kassa uni {reading.Prefix} prefiksi uchun eslab qoladi:");

        var hasPrice = reading.PricePerKg > 0;
        WeightOptionKind.Text = L("ВЕС", "САЛМАК", "WEIGHT", "AĞIRLIK", "VAZN");
        WeightOptionValue.Text = ScaleBarcodeRules.Kg(reading.WeightKg);
        WeightOptionDetail.Text = hasPrice
            ? ScaleBarcodeRules.LineText(reading.WeightKg, reading.PricePerKg)
            : L($"цифры {reading.ValueDigits} — граммы", $"{reading.ValueDigits} сандары — грамм", $"digits {reading.ValueDigits} are grams", $"{reading.ValueDigits} haneleri gram", $"{reading.ValueDigits} raqamlari — gramm");

        AmountOptionKind.Text = L("СУММА", "СУММА", "AMOUNT", "TUTAR", "SUMMA");
        AmountOptionValue.Text = ScaleBarcodeRules.Money(reading.AmountSom) + " " + ScaleBarcodeRules.Som();
        AmountOptionDetail.Text = hasPrice
            ? "= " + ScaleBarcodeRules.LineText(reading.WeightFromAmountKg, reading.PricePerKg)
            : L($"цифры {reading.ValueDigits} — тыйыны", $"{reading.ValueDigits} сандары — тыйын", $"digits {reading.ValueDigits} are tiyin", $"{reading.ValueDigits} haneleri tiyin", $"{reading.ValueDigits} raqamlari — tiyin");

        var now = L("← так касса читает сейчас", "← касса азыр ушундай окуйт", "← how the till reads it now", "← kasa şu an böyle okuyor", "← kassa hozir shunday o‘qiydi");
        // Префикс без правила: касса ещё спросит при скане — «как читает сейчас» не показываем.
        var asks = ScaleBarcodeRules.AsksOnScan(reading.Prefix);
        WeightOptionNote.Text = !asks && reading.CurrentKind == WeightBarcodeValueKind.Weight ? now : "";
        AmountOptionNote.Text = !asks && reading.CurrentKind == WeightBarcodeValueKind.Amount ? now : "";
        WeightOptionButton.Classes.Set("current", !asks && reading.CurrentKind == WeightBarcodeValueKind.Weight);
        AmountOptionButton.Classes.Set("current", !asks && reading.CurrentKind == WeightBarcodeValueKind.Amount);
    }

    private void WeightOption_Click(object? sender, RoutedEventArgs e) => ChooseReading(WeightBarcodeValueKind.Weight);

    private void AmountOption_Click(object? sender, RoutedEventArgs e) => ChooseReading(WeightBarcodeValueKind.Amount);

    private void ChooseReading(WeightBarcodeValueKind kind)
    {
        if (_reading is null)
            return;
        ScaleBarcodeRules.ApplyAndSave(_reading.Prefix, kind);
        ShowChosen(kind);
        RulesChanged?.Invoke();
    }

    /// <summary>Показывает итог выбора: правило записано, как теперь будет в чеке.</summary>
    private void ShowChosen(WeightBarcodeValueKind kind)
    {
        if (_reading is null)
            return;
        _extraPrefixes.Add(_reading.Prefix);
        _reading = _reading with { CurrentKind = kind };
        ShowReading(_reading);
        WeightOptionButton.Classes.Set("chosen", kind == WeightBarcodeValueKind.Weight);
        AmountOptionButton.Classes.Set("chosen", kind == WeightBarcodeValueKind.Amount);
        BuildRules();

        var line = _reading.Product is { } p && _reading.PricePerKg > 0
            ? $"{p.Title}: " + ScaleBarcodeRules.LineText(kind == WeightBarcodeValueKind.Amount ? _reading.WeightFromAmountKg : _reading.WeightKg, _reading.PricePerKg)
            : "";
        var kindUpper = kind == WeightBarcodeValueKind.Amount
            ? L("СУММУ", "СУММА", "AMOUNT", "TUTAR", "SUMMA")
            : L("ВЕС", "САЛМАК", "WEIGHT", "AĞIRLIK", "VAZN");
        ShowWizardResult("✓ " + L($"Готово: этикетки с префиксом {_reading.Prefix} касса читает как {kindUpper}.",
                             $"Даяр: {_reading.Prefix} префикстүү этикеткаларды касса {kindUpper} катары окуйт.",
                             $"Done: the till reads labels with prefix {_reading.Prefix} as {kindUpper}.",
                             $"Tamam: kasa {_reading.Prefix} önekli etiketleri {kindUpper} olarak okur.",
                             $"Tayyor: kassa {_reading.Prefix} prefiksli yorliqlarni {kindUpper} sifatida o‘qiydi.")
                         + (line.Length > 0 ? L(" В чеке: ", " Чекте: ", " On the receipt: ", " Fişte: ", " Chekda: ") + line : ""),
            isError: false);
    }

    private void ShowWizardResult(string text, bool isError)
    {
        WizardResultBorder.IsVisible = true;
        WizardResultText.Text = text;
        WizardResultBorder.Background = Brush(isError ? "BrushWarningSoft" : "BrushSuccessSoft", Brushes.LightYellow);
        WizardResultBorder.BorderBrush = Brush(isError ? "BrushWarning" : "BrushSuccess", Brushes.Goldenrod);
    }

    /// <summary>Штрихкод по частям: префикс | код товара | вес или сумма | контрольная цифра.</summary>
    private void FillSegments(WrapPanel panel, string prefix, string code, string value, string check)
    {
        var isCode = string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);
        panel.Children.Clear();
        panel.Children.Add(Segment(prefix, L("префикс", "префикс", "prefix", "önek", "prefiks"), "BrushAccentSoft", "BrushAccent"));
        panel.Children.Add(Segment(code, isCode
            ? L("код товара", "товар коду", "item code", "ürün kodu", "tovar kodi")
            : L("PLU товара", "товардын PLU'су", "item PLU", "ürün PLU", "tovar PLU"), "BrushPanelSoft", "BrushBorderStrong"));
        panel.Children.Add(Segment(value, L("вес или сумма", "салмак же сумма", "weight or amount", "ağırlık veya tutar", "vazn yoki summa"), "BrushWarningSoft", "BrushWarning"));
        panel.Children.Add(Segment(check, L("контр.", "текш.", "check", "kontrol", "nazorat"), "BrushSurfaceSubtle", "BrushBorder"));
    }

    private Border Segment(string digits, string caption, string bg, string border)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = digits,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Consolas, Segoe UI"),
            Foreground = Brush("BrushText", Brushes.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 10,
            Foreground = Brush("BrushTextSoft", Brushes.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new Border
        {
            Classes = { "Segment" },
            Background = Brush(bg, Brushes.WhiteSmoke),
            BorderBrush = Brush(border, Brushes.LightGray),
            Child = stack,
        };
    }

    // ------------------------------------------------------------------ раскладка компании

    private void BuildCompanyExample()
    {
        var (codeLength, valueLength) = ScaleBarcodeRules.Layout();
        var prefix = "20";
        var kind = ScaleBarcodeRules.Effective(prefix);
        var example = ScaleBarcodeRules.ExampleFor(prefix, kind);
        var barcode = example.Length >= 13 ? example[..13] : "";
        if (barcode.Length == 13)
            FillSegments(ExampleSegments, barcode[..2], barcode.Substring(2, codeLength), barcode.Substring(2 + codeLength, valueLength), barcode[12..]);
        ExampleText.Text = L("Пример: ", "Мисал: ", "Example: ", "Örnek: ", "Namuna: ") + example;

        var unit = string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.AmountUnit, "som", StringComparison.OrdinalIgnoreCase)
            ? L("Сумма в штрихкоде — в сомах.", "Штрих-коддогу сумма — сом менен.", "The amount in the barcode is in som.", "Barkoddaki tutar som cinsinden.", "Shtrix-koddagi summa — so‘mda.")
            : L("Сумма в штрихкоде — в тыйынах (1320 = 13,20 сом).", "Штрих-коддогу сумма — тыйын менен (1320 = 13,20 сом).", "The amount in the barcode is in tiyin (1320 = 13.20 som).", "Barkoddaki tutar tiyin cinsinden (1320 = 13,20 som).", "Shtrix-koddagi summa — tiyinda (1320 = 13,20 so‘m).");
        CompanyFormatText.Text = ScaleUi.CompanyFormatText() + " " + unit;

        var brand = ScaleUi.NormalizeBrand(UserPreferences.Instance.ScaleBrand);
        OnScaleRow.IsVisible = brand != ScaleUi.BrandAi;
        OnScaleButton.Content = L("Формат штрих-кода на самих весах: ", "Таразанын өзүндөгү штрих-код форматы: ", "Barcode format on the scale itself: ", "Tartının kendi barkod biçimi: ", "Tarozining o‘zidagi shtrix-kod formati: ")
                                + ScaleUi.LabelBrandTitle(brand) + "…";
    }

    /// <summary>Окно настроек выбранной марки сразу на вкладке «Штрих-код».</summary>
    private async void OnScale_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        switch (ScaleUi.NormalizeBrand(UserPreferences.Instance.ScaleBrand))
        {
            case ScaleUi.BrandTm:
                var tm = new TmScaleSettingsWindow();
                tm.ShowBarcodeTab();
                await tm.ShowDialog(owner).ConfigureAwait(true);
                break;
            case ScaleUi.BrandRongta:
                var rongta = new RongtaScaleSettingsWindow();
                rongta.Tabs.SelectedIndex = 2; // «Штрих-код»
                await rongta.ShowDialog(owner).ConfigureAwait(true);
                break;
            case ScaleUi.BrandShtrikh:
                var tile = CatalogCacheService.Products.FirstOrDefault(p => p.IsWeighted && p.Plu is > 0);
                var shtrikh = new ShtrikhScaleSettingsWindow();
                shtrikh.ShowBarcodeTabFor(tile?.Title ?? "", tile?.Plu ?? 1, (decimal)(tile is null ? 0 : LocalCartService.ParsePrice(tile.PriceLine)));
                await shtrikh.ShowDialog(owner).ConfigureAwait(true);
                break;
        }
        Refresh();
        RulesChanged?.Invoke();
    }
}
