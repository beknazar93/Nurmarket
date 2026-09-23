using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Модалка "⚙ Настройка темы" — платный редактор (см. marketplace.editorPriceLabel),
/// открывается только с карточки "Свой цвет" (и активной, и ещё не применённой — см.
/// MarketplaceView.BuildThemeGallery). Бесплатные пресеты этот диалог не открывают вовсе.</summary>
public partial class ThemeSettingsDialog : Window
{
    private readonly bool _isCustomColorTheme;
    // Стартует true (не false): слайдеры коэрсят своё дефолтное значение и стреляют
    // ValueChanged ПРЯМО во время InitializeComponent(), то есть до того, как тело
    // конструктора успевает выставить _suppressChange = true — без этого дефолта здесь
    // падало NullReferenceException (обработчик трогал ещё не назначенные именованные поля).
    private bool _suppressChange = true;
    private bool _resetRadiusRequested;
    private string _selectedFontFamily = "Segoe UI";

    private static readonly string[] FontFamilyOptions =
        ["Segoe UI", "Arial", "Verdana", "Tahoma", "Calibri", "Consolas"];

    public ThemeSettingsDialog() : this(isCustomColorTheme: false)
    {
    }

    public ThemeSettingsDialog(bool isCustomColorTheme)
    {
        InitializeComponent();
        _isCustomColorTheme = isCustomColorTheme;
        ColorSection.IsVisible = isCustomColorTheme;
        PriceBadge.IsVisible = isCustomColorTheme;

        _suppressChange = true;

        _selectedFontFamily = string.IsNullOrWhiteSpace(UserPreferences.Instance.CustomFontFamily)
            ? "Segoe UI" : UserPreferences.Instance.CustomFontFamily;
        BuildFontFamilyOptions();

        FontSizeSlider.Value = UserPreferences.Instance.CustomFontSize ?? CurrentAppliedFontSize();
        ButtonRadiusSlider.Value = UserPreferences.Instance.CustomButtonRadius ?? CurrentAppliedRadius("SettingsButtonRadius");
        CardRadiusSlider.Value = UserPreferences.Instance.CustomCardRadius ?? CurrentAppliedRadius("SettingsCardRadius");

        _suppressChange = false;
        FontSizeValueText.Text = $"{FontSizeSlider.Value:F0}px";
        UpdateRadiusText(ButtonRadiusValueText, ButtonRadiusSlider.Value);
        UpdateRadiusText(CardRadiusValueText, CardRadiusSlider.Value);

        TextColorPicker.SetHex(UserPreferences.Instance.CustomTextColor, fallbackDisplayHex: UserPreferences.Instance.DarkTheme ? "#F1F5F9" : "#1A1A1A");

        if (isCustomColorTheme)
        {
            var initialHex = string.IsNullOrWhiteSpace(UserPreferences.Instance.CustomAccentHex)
                ? "#FF6B00" : UserPreferences.Instance.CustomAccentHex!;
            AccentColorPicker.SetHex(initialHex);
        }

        // Живой предпросмотр (2026-09-07): раньше результат был виден только после
        // "Применить" — теперь макет справа перекрашивается сразу при движении любого
        // ползунка/пикера. SetHex(...) выше не поднимает ColorChanged (см. PhotoshopColorPicker),
        // поэтому первичная отрисовка предпросмотра — отдельным явным вызовом здесь.
        AccentColorPicker.ColorChanged += UpdatePreview;
        TextColorPicker.ColorChanged += UpdatePreview;

        // Слова "Итого"/"Оплатить" в макете предпросмотра — настоящий текст, не образец данных
        // (в отличие от "Молоко 3.2% 1л"), поэтому идут на языке интерфейса кассы.
        PreviewTotalLabel.Text = Tr.T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami");
        PreviewButtonText.Text = Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash");

        UpdatePreview();
    }

    public static bool Show(Window? owner, bool isCustomColorTheme) =>
        PosDialogHost.Show(new ThemeSettingsDialog(isCustomColorTheme), owner) == true;

    private double CurrentAppliedFontSize() =>
        Application.Current?.TryFindResource("AppFontSize", ActualThemeVariant, out var value) == true && value is double size
            ? size : 14;

    private double CurrentAppliedRadius(string resourceKey) =>
        Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true && value is CornerRadius radius
            ? radius.TopLeft : 0;

    private void BuildFontFamilyOptions()
    {
        FontFamilyList.Items.Clear();
        foreach (var family in FontFamilyOptions)
        {
            var isSelected = string.Equals(family, _selectedFontFamily, System.StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Classes = { isSelected ? "btn-primary" : "btn-secondary" },
                Content = family,
                FontFamily = new FontFamily(family),
                Height = 34,
                Padding = new Thickness(12, 4),
                Margin = new Thickness(0, 0, 8, 8),
                Tag = family,
            };
            button.Click += FontFamilyButton_Click;
            FontFamilyList.Items.Add(button);
        }
    }

    private void FontFamilyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string family })
            return;
        _selectedFontFamily = family;
        BuildFontFamilyOptions();
        UpdatePreview();
    }

    private void FontSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressChange)
            return;
        FontSizeValueText.Text = $"{e.NewValue:F0}px";
        UpdatePreview();
    }

    private static void UpdateRadiusText(TextBlock target, double value) =>
        target.Text = $"{value:F0}px";

    private void ButtonRadiusSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressChange)
            return;
        // Движение слайдера после «Сброс» снова означает явное значение (2026-09-07, был баг:
        // флаг сброса оставался и «Применить» игнорировал новое положение слайдера).
        _resetRadiusRequested = false;
        UpdateRadiusText(ButtonRadiusValueText, e.NewValue);
        UpdatePreview();
    }

    private void CardRadiusSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressChange)
            return;
        _resetRadiusRequested = false;
        UpdateRadiusText(CardRadiusValueText, e.NewValue);
        UpdatePreview();
    }

    private void RadiusResetButton_Click(object? sender, RoutedEventArgs e)
    {
        _suppressChange = true;
        ButtonRadiusSlider.Value = 0;
        CardRadiusSlider.Value = 0;
        _suppressChange = false;
        // "Сброс" здесь означает "снять оверрайд" — фактическое значение темы подставится
        // заново при следующем открытии диалога; но пока диалог открыт, проще всего явно
        // показать 0 и снять оверрайд в момент "Применить" (см. ApplyButton_Click).
        _resetRadiusRequested = true;
        UpdateRadiusText(ButtonRadiusValueText, 0);
        UpdateRadiusText(CardRadiusValueText, 0);
        UpdatePreview();
    }

    private void TextColorResetButton_Click(object? sender, RoutedEventArgs e)
    {
        TextColorErrorText.IsVisible = false;
        TextColorPicker.SetHex(null, fallbackDisplayHex: UserPreferences.Instance.DarkTheme ? "#F1F5F9" : "#1A1A1A");
        UpdatePreview();
    }

    /// <summary>Перекрашивает макет справа под текущее положение всех элементов управления —
    /// НЕ трогает реальную тему кассы (это делает только "Применить", см. ApplyButton_Click).
    /// Акцент для предпросмотра пресетной (не "Свой цвет") темы берётся из уже применённого
    /// BrushAccent — иначе при редактировании шрифта/скруглений пресета макет был бы бесцветным.</summary>
    private void UpdatePreview()
    {
        var accentBrush = ParseBrushOrFallback(CurrentPreviewAccentHex(), Brushes.Orange);
        var textBrush = ParseBrushOrFallback(CurrentPreviewTextHex(), Brushes.Black);
        var buttonRadius = new CornerRadius(ButtonRadiusSlider.Value);
        var cardRadius = new CornerRadius(CardRadiusSlider.Value);
        var fontFamily = new FontFamily(_selectedFontFamily);

        // Предпросмотр — узкая карточка 320px, а не вся касса: если растить в ней шрифт
        // ровно на ту же величину, что и слайдер (до 20px "боевого" размера), крупные строки
        // визуально наезжали друг на друга (2026-09-07, сообщено пользователем). Кегли
        // предпросмотра растут вместе со слайдером, но в собственных, более узких пределах.
        var sliderSize = FontSizeSlider.Value;
        var lineSize = Math.Clamp(sliderSize - 1, 11, 15);
        var tilePriceSize = Math.Clamp(sliderSize + 2, 13, 17);
        var totalSize = Math.Clamp(sliderSize + 4, 16, 20);

        PreviewTile.CornerRadius = cardRadius;
        PreviewButton.Background = accentBrush;
        PreviewButton.CornerRadius = buttonRadius;
        PreviewButtonText.FontFamily = fontFamily;

        PreviewTileTitle.FontFamily = fontFamily;
        PreviewTileTitle.FontSize = lineSize;
        PreviewTileTitle.Foreground = textBrush;
        PreviewTilePrice.FontFamily = fontFamily;
        PreviewTilePrice.FontSize = tilePriceSize;
        PreviewTilePrice.Foreground = accentBrush;

        PreviewLineTitle.FontFamily = fontFamily;
        PreviewLineTitle.FontSize = lineSize;
        PreviewLineTitle.Foreground = textBrush;
        PreviewLinePrice.FontFamily = fontFamily;
        PreviewLinePrice.FontSize = lineSize;
        PreviewLinePrice.Foreground = accentBrush;

        PreviewTotal.FontFamily = fontFamily;
        PreviewTotal.FontSize = totalSize;
        PreviewTotal.Foreground = accentBrush;
    }

    private string CurrentPreviewAccentHex()
    {
        if (_isCustomColorTheme && AccentColorPicker.TryGetHex(out var hex))
            return hex;
        if (Application.Current?.TryFindResource("BrushAccent", ActualThemeVariant, out var resource) == true
            && resource is ISolidColorBrush solid)
            return $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}";
        return "#FF6B00";
    }

    private string CurrentPreviewTextHex() =>
        TextColorPicker.RawText.Trim().Length > 0 && TextColorPicker.TryGetHex(out var hex)
            ? hex
            : "#0F172A";

    private static IBrush ParseBrushOrFallback(string hex, IBrush fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return fallback;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        string? normalizedHex = null;
        if (_isCustomColorTheme)
        {
            if (!AccentColorPicker.TryGetHex(out normalizedHex))
            {
                ColorErrorText.Text = Tr.T("Некорректный HEX-код цвета.", "Түстүн HEX-коду туура эмес.", "Invalid color HEX code.", "Geçersiz renk HEX kodu.", "Rangning HEX kodi noto'g'ri.");
                ColorErrorText.IsVisible = true;
                return;
            }
        }

        // Цвет текста опционален (в отличие от акцентного цвета темы) — пустое поле снимает
        // оверрайд, а невалидный непустой HEX блокирует "Применить", как и у цвета темы.
        string? normalizedTextColor = null;
        if (TextColorPicker.RawText.Trim().Length > 0)
        {
            if (!TextColorPicker.TryGetHex(out normalizedTextColor))
            {
                TextColorErrorText.Text = Tr.T("Некорректный HEX-код цвета текста.", "Тексттин HEX-коду туура эмес.", "Invalid text color HEX code.", "Geçersiz metin rengi HEX kodu.", "Matn rangining HEX kodi noto'g'ri.");
                TextColorErrorText.IsVisible = true;
                return;
            }
        }
        TextColorErrorText.IsVisible = false;

        var prefs = UserPreferences.Instance;
        prefs.CustomFontFamily = _selectedFontFamily;
        prefs.CustomFontSize = FontSizeSlider.Value;
        prefs.CustomTextColor = normalizedTextColor;
        prefs.CustomButtonRadius = _resetRadiusRequested ? null : ButtonRadiusSlider.Value;
        prefs.CustomCardRadius = _resetRadiusRequested ? null : CardRadiusSlider.Value;

        if (_isCustomColorTheme && normalizedHex != null)
        {
            prefs.CustomAccentHex = normalizedHex;
            // ApplyAccentTheme сохраняет AccentTheme и сам вызывает SaveToDisk/Apply — важно,
            // что CustomFontFamily/Size/TextColor/Radius/AccentHex уже проставлены до вызова.
            App.ApplyAccentTheme("custom");
        }
        else
        {
            prefs.SaveToDisk();
            AccentThemeService.Apply(prefs.AccentTheme, prefs.DarkTheme);
        }

        Close(true);
    }
}
