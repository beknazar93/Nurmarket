using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Редактор экрана покупателя (2026-09-28, «редактор 2 экран покупателя тоже добавь»).
///
/// Открывается из Маркетплейса (вкладка «Виды кассы») и из Настройки → Монитор. Правится черновик —
/// копия UserPreferences.CustomerDisplay: вид экрана («как у кассы» или один из шести), надпись
/// сверху, приветствие, текст после оплаты, цвета (как тема кассы или свои: фон, акцент, текст),
/// светлый/тёмный вариант, размер итоговой суммы и что показывать. Справа — живой предпросмотр:
/// тот же CustomerDisplayView, что на втором мониторе, со своей моделью и образцом чека.
/// «Сохранить» переносит в настройки только поля внешнего вида (CustomerDisplaySettings.
/// CopyAppearanceFrom) и применяет их к открытому экрану; «Отмена», крестик и Esc ничего не меняют.</summary>
public partial class CustomerDisplayEditorWindow : Window
{
    private enum ColorKey { Background, Accent, Text }

    private sealed record ColorRow(Border Row, Border Swatch, TextBox Hex);

    private readonly CustomerDisplaySettings _draft;
    private readonly CustomerDisplayStateService _sampleState = new();
    private readonly CustomerDisplayViewModel _previewVm;
    private readonly Dictionary<string, Button> _styleButtons = new();
    private readonly Dictionary<ColorKey, ColorRow> _colorRows = new();
    private ColorKey _selectedColor = ColorKey.Accent;
    private string _sample = "receipt";
    // true до конца конструктора: ползунок с Minimum="60" коэрсит значение уже внутри
    // InitializeComponent (см. тот же приём в ScreenSettingsView), когда полей ещё нет.
    private bool _suppress = true;

    public CustomerDisplayEditorWindow()
    {
        InitializeComponent();
        // На компактном моноблоке 1024×768 окно 1180×760 не поместилось бы целиком.
        this.FitToScreen();
        _draft = UserPreferences.Instance.CustomerDisplay.Clone();
        _draft.Normalize();

        ApplyTexts();
        BuildStyleButtons();
        BuildColorRows();
        ColorPicker.ColorChanged += OnPickerColorChanged;
        KeyDown += OnWindowKeyDown;

        FillSample();
        _previewVm = CustomerDisplayViewModel.CreateDesignPreview(_sampleState, _draft);
        PreviewView.DataContext = _previewVm;
        Closed += (_, _) => _previewVm.Dispose();

        LoadControls();
    }

    /// <summary>true — настройки сохранены (для страниц, которые держат свою копию настроек).</summary>
    public bool Saved { get; private set; }

    /// <summary>Черновик (для проверок стенда).</summary>
    public CustomerDisplaySettings Draft => _draft;

    // ------------------------------------------------------------------ тексты

    private void ApplyTexts()
    {
        Title = Tr.T("Редактор экрана покупателя", "Сатып алуучунун экранынын редактору", "Customer display editor", "Müşteri ekranı düzenleyici", "Xaridor ekrani muharriri");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T(
            "Второй экран, который видит покупатель: вид, надписи, цвета и что показывать. Справа — как это будет выглядеть.",
            "Сатып алуучу көргөн экинчи экран: көрүнүшү, жазуулар, түстөр жана эмнени көрсөтүү. Оң жакта — кандай көрүнөөрү.",
            "The second screen your customer sees: layout, captions, colors and what to show. The preview on the right shows the result.",
            "Müşterinin gördüğü ikinci ekran: görünüm, yazılar, renkler ve neyin gösterileceği. Sağda sonucun önizlemesi var.",
            "Xaridor ko'radigan ikkinchi ekran: ko'rinish, yozuvlar, ranglar va nimani ko'rsatish. O'ngda natija qanday bo'lishi ko'rinadi.");
        StyleLabel.Text = Tr.T("Вид экрана", "Экрандын көрүнүшү", "Screen layout", "Ekran görünümü", "Ekran ko'rinishi");
        TextsLabel.Text = Tr.T("Надписи", "Жазуулар", "Captions", "Yazılar", "Yozuvlar");
        StoreTitleLabel.Text = Tr.T("Название сверху", "Үстүндөгү аталыш", "Title at the top", "Üstteki başlık", "Tepadagi nom");
        GreetingLabel.Text = Tr.T("Приветствие (когда чек пуст)", "Саламдашуу (чек бош болгондо)", "Greeting (when the receipt is empty)", "Karşılama (fiş boşken)", "Salomlashuv (chek bo'sh bo'lganda)");
        GreetingDescLabel.Text = Tr.T("Строка под приветствием", "Саламдашуунун астындагы сап", "Line under the greeting", "Karşılamanın altındaki satır", "Salomlashuv ostidagi qator");
        SuccessLabel.Text = Tr.T("После оплаты", "Төлөгөндөн кийин", "After payment", "Ödemeden sonra", "To'lovdan keyin");
        ColorsLabel.Text = Tr.T("Цвета", "Түстөр", "Colors", "Renkler", "Ranglar");
        ThemeColorsButton.Content = Tr.T("Как тема кассы", "Кассанын темасындай", "Same as the till theme", "Kasa temasıyla aynı", "Kassa mavzusidek");
        CustomColorsButton.Content = Tr.T("Свои цвета", "Өз түстөрүм", "Custom colors", "Özel renkler", "O'z ranglarim");
        VariantLabel.Text = Tr.T("Светлый или тёмный", "Жарык же караңгы", "Light or dark", "Açık veya koyu", "Yorug' yoki qorong'i");
        LightVariantButton.Content = Tr.T("Светлый", "Жарык", "Light", "Açık", "Yorug'");
        DarkVariantButton.Content = Tr.T("Тёмный", "Караңгы", "Dark", "Koyu", "Qorong'i");
        SystemVariantButton.Content = Tr.T("Как у кассы", "Кассадагыдай", "Same as the till", "Kasadaki gibi", "Kassadagidek");
        TotalSizeLabel.Text = Tr.T("Размер итоговой суммы", "Жыйынтык сумманын өлчөмү", "Total amount size", "Toplam tutarın boyutu", "Jami summa o'lchami");
        ShowLabel.Text = Tr.T("Показывать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish");
        ShowListCheck.Content = Tr.T("Список позиций", "Позициялардын тизмеси", "Item list", "Ürün listesi", "Pozitsiyalar ro'yxati");
        ShowPhotosCheck.Content = Tr.T("Фото товаров", "Товарлардын сүрөтү", "Product photos", "Ürün fotoğrafları", "Mahsulot rasmlari");
        ShowQrCheck.Content = Tr.T("QR-код оплаты", "Төлөм QR-коду", "Payment QR code", "Ödeme QR kodu", "To'lov QR-kodi");
        ShowClockCheck.Content = Tr.T("Часы", "Саат", "Clock", "Saat", "Soat");
        PreviewLabel.Text = Tr.T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish");
        SampleReceiptButton.Content = Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek");
        SampleEmptyButton.Content = Tr.T("Пустой экран", "Бош экран", "Empty screen", "Boş ekran", "Bo'sh ekran");
        SamplePaidButton.Content = Tr.T("Оплата принята", "Төлөм кабыл алынды", "Payment accepted", "Ödeme kabul edildi", "To'lov qabul qilindi");
        PreviewHintText.Text = Tr.T(
            "Образец чека. Настоящий экран покупателя изменится после «Сохранить».",
            "Чектин үлгүсү. Сатып алуучунун чыныгы экраны «Сактоо» баскандан кийин өзгөрөт.",
            "A sample receipt. The real customer display changes after you click “Save”.",
            "Örnek fiş. Gerçek müşteri ekranı «Kaydet»e bastıktan sonra değişir.",
            "Chek namunasi. Xaridorning haqiqiy ekrani «Saqlash» tugmasidan keyin o'zgaradi.");
        ResetText.Text = Tr.T("Сбросить к умолчанию", "Демейкиге кайтаруу", "Reset to defaults", "Varsayılana sıfırla", "Standart holatga qaytarish");
        CancelButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
        SaveButton.Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
    }

    private static string ColorLabel(ColorKey key) => key switch
    {
        ColorKey.Background => Tr.T("Фон", "Фон", "Background", "Arka plan", "Fon"),
        ColorKey.Accent => Tr.T("Акцент (сумма, выделение)", "Акцент (сумма, белгилөө)", "Accent (amount, highlights)", "Vurgu (tutar, vurgular)", "Urg'u (summa, belgilash)"),
        _ => Tr.T("Текст", "Текст", "Text", "Metin", "Matn"),
    };

    /// <summary>Название вида кассы по id (KassaLayouts).</summary>
    private static string StyleName(string id) =>
        KassaLayouts.All.FirstOrDefault(o => o.Id == id)?.Label() ?? id;

    // ------------------------------------------------------------------ построение

    private void BuildStyleButtons()
    {
        StylePanel.Children.Clear();
        _styleButtons.Clear();
        var kassa = KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode);
        AddStyleButton(CustomerDisplaySettings.StyleAuto,
            Tr.T($"Как у кассы — {StyleName(kassa)}", $"Кассадагыдай — {StyleName(kassa)}", $"Same as the till — {StyleName(kassa)}",
                $"Kasadaki gibi — {StyleName(kassa)}", $"Kassadagidek — {StyleName(kassa)}"));
        foreach (var option in KassaLayouts.All)
            AddStyleButton(option.Id, option.Label());
    }

    private void AddStyleButton(string id, string label)
    {
        var button = new Button { Content = label, Tag = id };
        button.Classes.Add("ce-chip");
        button.Click += Style_Click;
        StylePanel.Children.Add(button);
        _styleButtons[id] = button;
    }

    private void BuildColorRows()
    {
        ColorRowsPanel.Children.Clear();
        _colorRows.Clear();
        foreach (var key in Enum.GetValues<ColorKey>())
        {
            var swatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorderStrong"));
            var label = new TextBlock { Text = ColorLabel(key), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 8, 0) };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var hex = new TextBox { Tag = key, Width = 92, Height = 32, MinHeight = 32, Padding = new Thickness(8, 0), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
            hex.Classes.Add("ce-input");
            hex.TextChanged += Hex_TextChanged;
            hex.GotFocus += (_, _) => SelectColor(key);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(swatch);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            Grid.SetColumn(hex, 2);
            grid.Children.Add(hex);

            var row = new Border { Child = grid, Tag = key };
            row.Classes.Add("ce-row");
            row.PointerPressed += (_, _) => SelectColor(key);
            ColorRowsPanel.Children.Add(row);
            _colorRows[key] = new ColorRow(row, swatch, hex);
        }
    }

    // ------------------------------------------------------------------ черновик ↔ поля

    private void LoadControls()
    {
        _suppress = true;
        try
        {
            StoreTitleBox.Text = _draft.StoreTitle;
            StoreTitleBox.Watermark = string.IsNullOrWhiteSpace(UserPreferences.Instance.StoreName) ? "MARKET PLUS" : UserPreferences.Instance.StoreName;
            GreetingTitleBox.Text = IsDefault(_draft.EmptyTitle, CustomerDisplaySettings.DefaultEmptyTitle) ? "" : _draft.EmptyTitle;
            GreetingTitleBox.Watermark = Tr.T("Добро пожаловать!", "Кош келиңиз!", "Welcome!", "Hoş geldiniz!", "Xush kelibsiz!");
            GreetingDescBox.Text = IsDefault(_draft.EmptyDescription, CustomerDisplaySettings.DefaultEmptyDescription) ? "" : _draft.EmptyDescription;
            GreetingDescBox.Watermark = Tr.T("Ваши покупки появятся на этом экране", "Сатып алууларыңыз ушул экранда көрүнөт",
                "Your purchases will appear on this screen", "Alışverişiniz bu ekranda görünecek", "Xaridlaringiz shu ekranda ko'rinadi");
            SuccessTextBox.Text = IsDefault(_draft.SuccessText, CustomerDisplaySettings.DefaultSuccessText) ? "" : _draft.SuccessText;
            SuccessTextBox.Watermark = Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışverişiniz için teşekkürler!", "Xaridingiz uchun rahmat!");
            TotalSizeSlider.Value = Math.Round(_draft.TotalScale * 100);
            TotalSizeValueText.Text = $"{TotalSizeSlider.Value:0}%";
            ShowListCheck.IsChecked = _draft.ShowItemList;
            ShowPhotosCheck.IsChecked = _draft.ShowProductImage;
            ShowQrCheck.IsChecked = _draft.ShowPaymentQr;
            ShowClockCheck.IsChecked = _draft.ShowDateTime;
        }
        finally
        {
            _suppress = false;
        }

        RefreshChips();
        RefreshColorRows();
        SelectColor(_selectedColor);
    }

    /// <summary>Пустое поле = текст по умолчанию (на языке программы). Так на кыргызской кассе
    /// в поле не висит русское «Добро пожаловать!» из старых настроек.</summary>
    private static bool IsDefault(string? value, string defaultText) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), defaultText, StringComparison.Ordinal);

    private void RefreshChips()
    {
        var style = string.IsNullOrWhiteSpace(_draft.DisplayStyle) ? CustomerDisplaySettings.StyleAuto : _draft.DisplayStyle;
        foreach (var (id, button) in _styleButtons)
            SetActive(button, string.Equals(id, style, StringComparison.OrdinalIgnoreCase));
        var effective = CustomerDisplayViewModel.ResolveStyle(_draft.DisplayStyle);
        StyleHintText.Text = string.Equals(style, CustomerDisplaySettings.StyleAuto, StringComparison.OrdinalIgnoreCase)
            ? Tr.T($"Экран меняется вместе с видом кассы. Сейчас: «{StyleName(effective)}».",
                $"Экран кассанын көрүнүшү менен бирге өзгөрөт. Азыр: «{StyleName(effective)}».",
                $"The screen changes together with the till layout. Now: “{StyleName(effective)}”.",
                $"Ekran kasa görünümüyle birlikte değişir. Şu an: «{StyleName(effective)}».",
                $"Ekran kassa ko'rinishi bilan birga o'zgaradi. Hozir: «{StyleName(effective)}».")
            : Tr.T("Экран всегда в этом виде, какой бы вид ни был у кассы.",
                "Кассанын көрүнүшү кандай болбосун, экран дайыма ушул көрүнүштө.",
                "The screen always uses this layout, whatever the till layout is.",
                "Kasa görünümü ne olursa olsun ekran hep bu görünümde kalır.",
                "Kassa ko'rinishi qanday bo'lmasin, ekran doim shu ko'rinishda.");

        var themeColors = _draft.UseThemeColors != false;
        SetActive(ThemeColorsButton, themeColors);
        SetActive(CustomColorsButton, !themeColors);
        CustomColorsPanel.IsVisible = !themeColors;
        var themeName = AccentThemeService.AvailableThemes.FirstOrDefault(t => t.Id == AccentThemeService.Normalize(UserPreferences.Instance.AccentTheme))?.Label
                        ?? CustomThemeStore.Find(UserPreferences.Instance.AccentTheme)?.Name
                        ?? "";
        ColorsHintText.Text = themeColors
            ? Tr.T($"Фон, панели, текст и акцент — из темы кассы «{themeName}», в том числе своей темы из редактора тем.",
                $"Фон, панелдер, текст жана акцент — кассанын «{themeName}» темасынан, анын ичинде темалар редакторундагы өз темаңыздан.",
                $"Background, panels, text and accent come from the till theme “{themeName}”, including your own theme from the theme editor.",
                $"Arka plan, paneller, metin ve vurgu kasa temasından («{themeName}») alınır; tema düzenleyicideki kendi temanız da dahil.",
                $"Fon, panellar, matn va urg'u kassaning «{themeName}» mavzusidan olinadi, mavzu muharriridagi o'z mavzuingiz ham.")
            : Tr.T("Нажмите на строку и выберите цвет. Панели остаются белыми (в тёмном варианте — тёмными).",
                "Сапты басып, түс тандаңыз. Панелдер ак бойдон калат (караңгы вариантта — караңгы).",
                "Click a row and pick a color. Panels stay white (dark in the dark variant).",
                "Bir satıra tıklayıp renk seçin. Paneller beyaz kalır (koyu varyantta koyu).",
                "Qatorni bosib, rang tanlang. Panellar oq bo'lib qoladi (qorong'i variantda — qorong'i).");

        SetActive(LightVariantButton, _draft.Theme == CustomerDisplayTheme.Light);
        SetActive(DarkVariantButton, _draft.Theme == CustomerDisplayTheme.Dark);
        SetActive(SystemVariantButton, _draft.Theme == CustomerDisplayTheme.System);

        SetActive(SampleReceiptButton, _sample == "receipt");
        SetActive(SampleEmptyButton, _sample == "empty");
        SetActive(SamplePaidButton, _sample == "paid");
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active"))
                button.Classes.Add("active");
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    private string GetColor(ColorKey key) => key switch
    {
        ColorKey.Background => _draft.BackgroundColor,
        ColorKey.Accent => _draft.AccentColor,
        _ => _draft.TextColor,
    };

    /// <summary>Цвет, который сейчас реально на экране (для пустого «Текста» — подобранный к фону).</summary>
    private string ShownColor(ColorKey key)
    {
        var value = GetColor(key);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        var dark = _draft.Theme == CustomerDisplayTheme.Dark
                   || (_draft.Theme == CustomerDisplayTheme.System && UserPreferences.Instance.DarkTheme);
        return dark ? "#FFFFFF" : "#0F172A";
    }

    private void SetColor(ColorKey key, string? hex)
    {
        switch (key)
        {
            case ColorKey.Background: _draft.BackgroundColor = hex ?? "#F8FAFC"; break;
            case ColorKey.Accent: _draft.AccentColor = hex ?? "#FACC15"; break;
            default: _draft.TextColor = hex ?? ""; break;
        }
    }

    private void RefreshColorRows(ColorKey? typingKey = null)
    {
        _suppress = true;
        try
        {
            foreach (var (key, row) in _colorRows)
            {
                var shown = ShownColor(key);
                row.Swatch.Background = Color.TryParse(shown, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                if (key != typingKey)
                    row.Hex.Text = GetColor(key);
                row.Hex.Watermark = key == ColorKey.Text ? Tr.T("авто", "авто", "auto", "otomatik", "avto") : null;
                if (key == _selectedColor)
                    row.Row.Classes.Add("selected");
                else
                    row.Row.Classes.Remove("selected");
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SelectColor(ColorKey key)
    {
        _selectedColor = key;
        RefreshColorRows();
        _suppress = true;
        try
        {
            var hex = ShownColor(key);
            ColorPicker.SetHex(hex, hex);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void ApplyPreview()
    {
        _previewVm.ApplySettings(_draft);
        RefreshChips();
        SetStatus("");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------------ образец чека

    private void FillSample()
    {
        _sampleState.SetPaymentStatus(_sample == "paid" ? CustomerDisplayPaymentStatus.Success : CustomerDisplayPaymentStatus.Idle);
        _sampleState.UpdateCart(_sample == "empty"
            ? new CustomerDisplayCartSnapshot()
            : CustomerDisplayViewModel.BuildSampleSnapshot(paid: _sample == "paid"));
    }

    // ------------------------------------------------------------------ события

    private void Style_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;
        _draft.DisplayStyle = id;
        ApplyPreview();
    }

    private void Text_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;
        var storeTitle = StoreTitleBox.Text?.Trim() ?? "";
        // Пустое поле — текст по умолчанию (показывается на языке программы).
        var emptyTitle = string.IsNullOrWhiteSpace(GreetingTitleBox.Text) ? CustomerDisplaySettings.DefaultEmptyTitle : GreetingTitleBox.Text.Trim();
        var emptyDescription = string.IsNullOrWhiteSpace(GreetingDescBox.Text) ? CustomerDisplaySettings.DefaultEmptyDescription : GreetingDescBox.Text.Trim();
        var successText = string.IsNullOrWhiteSpace(SuccessTextBox.Text) ? CustomerDisplaySettings.DefaultSuccessText : SuccessTextBox.Text.Trim();
        // TextChanged приходит и после заполнения полей из черновика (уже вне _suppress) —
        // тогда ничего не поменялось, и предпросмотр не должен прыгать на другой образец.
        var greetingChanged = emptyTitle != _draft.EmptyTitle || emptyDescription != _draft.EmptyDescription;
        var successChanged = successText != _draft.SuccessText;
        if (!greetingChanged && !successChanged && storeTitle == _draft.StoreTitle)
            return;

        _draft.StoreTitle = storeTitle;
        _draft.EmptyTitle = emptyTitle;
        _draft.EmptyDescription = emptyDescription;
        _draft.SuccessText = successText;
        // Правят приветствие — показываем пустой экран, правят «после оплаты» — оплату.
        if (greetingChanged)
            SwitchSample("empty");
        else if (successChanged)
            SwitchSample("paid");
        ApplyPreview();
    }

    private void ThemeColors_Click(object? sender, RoutedEventArgs e)
    {
        _draft.UseThemeColors = true;
        ApplyPreview();
    }

    private void CustomColors_Click(object? sender, RoutedEventArgs e)
    {
        _draft.UseThemeColors = false;
        ApplyPreview();
        RefreshColorRows();
        SelectColor(_selectedColor);
    }

    private void Variant_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<CustomerDisplayTheme>(tag, out var theme))
            return;
        _draft.Theme = theme;
        ApplyPreview();
        RefreshColorRows();
    }

    private void Hex_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || sender is not TextBox { Tag: ColorKey key } box)
            return;
        var text = box.Text?.Trim() ?? "";
        // Поле заполнили из черновика (TextChanged пришёл позже) — не правка.
        if (string.Equals(text, GetColor(key), StringComparison.OrdinalIgnoreCase))
            return;
        if (text.Length == 0 && key == ColorKey.Text)
        {
            SetColor(key, null);
        }
        else
        {
            var hex = CustomThemeStore.NormalizeHex(text);
            if (hex is null)
                return;
            SetColor(key, hex);
            _suppress = true;
            try { ColorPicker.SetHex(hex, hex); }
            finally { _suppress = false; }
        }
        _selectedColor = key;
        RefreshColorRows(typingKey: key);
        ApplyPreview();
    }

    private void OnPickerColorChanged()
    {
        if (_suppress || !ColorPicker.TryGetHex(out var hex))
            return;
        var normalized = CustomThemeStore.NormalizeHex(hex);
        // Палитре подставили текущий цвет строки — это не выбор нового цвета (иначе «авто» у
        // текста само превращалось бы в явный цвет).
        if (normalized is null || string.Equals(normalized, ShownColor(_selectedColor), StringComparison.OrdinalIgnoreCase))
            return;
        SetColor(_selectedColor, normalized);
        RefreshColorRows();
        ApplyPreview();
    }

    private void TotalSizeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress)
            return;
        TotalSizeValueText.Text = $"{e.NewValue:0}%";
        _draft.TotalScale = Math.Round(e.NewValue) / 100.0;
        ApplyPreview();
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        _draft.ShowItemList = ShowListCheck.IsChecked == true;
        _draft.ShowProductImage = ShowPhotosCheck.IsChecked == true;
        _draft.ShowPaymentQr = ShowQrCheck.IsChecked == true;
        _draft.ShowDateTime = ShowClockCheck.IsChecked == true;
        if (_sample == "empty")
            SwitchSample("receipt");
        ApplyPreview();
    }

    private void SampleState_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sample })
            SwitchSample(sample);
        RefreshChips();
    }

    private void SwitchSample(string sample)
    {
        if (_sample == sample)
            return;
        _sample = sample;
        FillSample();
    }

    /// <summary>Внешний вид — как у новой кассы: вид «как у кассы», цвета темы, тексты по умолчанию,
    /// всё показывается. Монитор, окно, реклама и колонки не трогаются. До «Сохранить» — только черновик.</summary>
    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        var defaults = new CustomerDisplaySettings();
        defaults.Normalize();
        defaults.UseThemeColors = true;
        _draft.CopyAppearanceFrom(defaults);
        LoadControls();
        ApplyPreview();
        SetStatus(Tr.T("Вернули настройки по умолчанию — нажмите «Сохранить», чтобы применить.",
            "Демейки жөндөөлөр кайтарылды — колдонуу үчүн «Сактоо» басыңыз.",
            "Defaults restored — click “Save” to apply them.",
            "Varsayılanlar geri yüklendi — uygulamak için «Kaydet»e basın.",
            "Standart sozlamalar qaytarildi — qo'llash uchun «Saqlash»ni bosing."));
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var settings = UserPreferences.Instance.CustomerDisplay.Clone();
        settings.CopyAppearanceFrom(_draft);
        settings.Normalize();
        try
        {
            // Сохраняет на диск и сразу применяет к открытому экрану покупателя.
            App.GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettings(settings);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор экрана покупателя: экран не обновлён, настройки записаны напрямую: {ex.Message}", "CUSTOMER_DISPLAY");
            UserPreferences.Instance.CustomerDisplay = settings;
            UserPreferences.Instance.SaveToDisk();
        }

        Saved = true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
