using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Редактор своих тем (Маркетплейс → Темы → «Редактор тем», 2026-09-28).
///
/// Своя тема — встроенная тема-основа плюс девять ключевых цветов (отдельно для светлого и тёмного
/// варианта), скругление углов и размер шрифта; остальные оттенки выводит AccentThemeService.
/// Пока окно открыто, редактируемая тема применена ко всей программе (живой предпросмотр). При
/// закрытии без «Применить» касса возвращается к своей прежней теме: несохранённые правки ничего
/// не меняют. Темы хранятся в %AppData%\NurMarketKassa\custom-themes.json (CustomThemeStore),
/// переносятся файлами «Экспорт» / «Импорт». Встроенные темы здесь не меняются — от них только
/// отталкиваются, поэтому ограничения платных встроенных тем редактор не обходит.</summary>
public partial class ThemeEditorWindow : Window
{
    private enum ColorKey { Accent, AccentText, Background, Panel, Text, TextSoft, Success, Warning, Danger }

    private sealed record RowParts(Border Row, Border Swatch, TextBlock Label, TextBox Hex, Button Reset);

    private static readonly JsonSerializerOptions CompareJson = new();

    private readonly Dictionary<ColorKey, RowParts> _rows = new();
    private CustomThemeDefinition _working = new();
    private string? _savedJson;
    private bool _editDark;
    private ColorKey _selectedKey = ColorKey.Accent;
    private bool _suppress;

    public ThemeEditorWindow()
    {
        InitializeComponent();
        _editDark = UserPreferences.Instance.DarkTheme;
        ApplyTexts();
        BuildBaseCombo();
        BuildColorRows();
        ColorPicker.ColorChanged += OnPickerColorChanged;
        KeyDown += OnWindowKeyDown;
        Closed += (_, _) => RestoreAppTheme();

        var current = UserPreferences.Instance.AccentTheme;
        var existing = CustomThemeStore.Find(current) ?? CustomThemeStore.All.FirstOrDefault();
        if (existing is not null)
            LoadTheme(existing.Clone(), saved: true);
        else
            LoadTheme(CustomThemeStore.Create(AccentThemeService.Normalize(current), CustomThemeStore.DefaultName), saved: false);
    }

    /// <summary>Тема, которую надо открыть сразу (карточка «Изменить» в галерее).</summary>
    public void OpenTheme(string themeId)
    {
        if (CustomThemeStore.Find(themeId) is { } theme)
            LoadTheme(theme.Clone(), saved: true);
    }

    private bool IsDirty => _savedJson is null || _savedJson != Serialize(_working);

    private static string Serialize(CustomThemeDefinition def)
    {
        var copy = def.Clone();
        copy.UpdatedUtc = default;
        return JsonSerializer.Serialize(copy, CompareJson);
    }

    // ------------------------------------------------------------------ тексты

    private void ApplyTexts()
    {
        Title = Tr.T("Редактор тем", "Темалар редактору", "Theme editor", "Tema düzenleyici", "Mavzu muharriri");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T(
            "Своя тема на основе любой встроенной: цвета, скругление и шрифт. Касса перекрашивается сразу — смотрите результат за окном и в предпросмотре.",
            "Каалаган орнотулган темага негизделген өз темаңыз: түстөр, бурчтардын тегеректиги жана шрифт. Касса дароо өзгөрөт — натыйжаны терезенин артынан жана алдын ала көрүүдөн караңыз.",
            "Your own theme based on any built-in one: colors, corner rounding and font. The till repaints instantly — see the result behind this window and in the preview.",
            "Herhangi bir yerleşik temaya dayalı kendi temanız: renkler, köşe yuvarlaklığı ve yazı tipi. Kasa anında yeniden boyanır — sonucu pencerenin arkasında ve önizlemede görün.",
            "Istalgan o'rnatilgan mavzuga asoslangan o'z mavzuingiz: ranglar, burchaklar yumaloqligi va shrift. Kassa darhol qayta bo'yaladi — natijani oyna ortida va oldindan ko'rishda ko'ring.");
        MyThemesLabel.Text = Tr.T("Мои темы", "Менин темаларым", "My themes", "Temalarım", "Mening mavzularim");
        NewFromLabel.Text = Tr.T("Новая тема на основе", "Жаңы тема, негизи", "New theme based on", "Şuna dayalı yeni tema", "Yangi mavzu asosi");
        CreateButton.Content = Tr.T("+ Создать тему", "+ Тема түзүү", "+ Create theme", "+ Tema oluştur", "+ Mavzu yaratish");
        DuplicateText.Text = Tr.T("Копия", "Көчүрмө", "Duplicate", "Kopya", "Nusxa");
        ImportText.Text = Tr.T("Импорт…", "Импорт…", "Import…", "İçe aktar…", "Import…");
        NameLabel.Text = Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi");
        LightVariantButton.Content = Tr.T("Светлый вариант", "Жарык вариант", "Light variant", "Açık varyant", "Yorug' variant");
        DarkVariantButton.Content = Tr.T("Тёмный вариант", "Караңгы вариант", "Dark variant", "Koyu varyant", "Qorong'i variant");
        ColorsLabel.Text = Tr.T("Цвета — нажмите на строку, чтобы выбрать цвет справа",
            "Түстөр — оң жактан түс тандоо үчүн сапты басыңыз",
            "Colors — click a row to pick its color on the right",
            "Renkler — sağdan renk seçmek için satıra tıklayın",
            "Ranglar — o'ngda rang tanlash uchun qatorni bosing");
        RadiusLabel.Text = Tr.T("Скругление углов", "Бурчтардын тегеректиги", "Corner rounding", "Köşe yuvarlaklığı", "Burchaklar yumaloqligi");
        FontLabel.Text = Tr.T("Размер шрифта", "Шрифттин өлчөмү", "Font size", "Yazı tipi boyutu", "Shrift o'lchami");
        ResetText.Text = Tr.T("Сбросить к основе", "Негизге кайтаруу", "Reset to base", "Temele sıfırla", "Asosga qaytarish");
        ExportText.Text = Tr.T("Экспорт…", "Экспорт…", "Export…", "Dışa aktar…", "Eksport…");
        DeleteText.Text = Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish");
        PreviewLabel.Text = Tr.T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish");
        CloseButton.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        SaveButton.Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
        ApplyButton.Content = Tr.T("Применить к кассе", "Кассага колдонуу", "Apply to the till", "Kasaya uygula", "Kassaga qo'llash");

        PreviewOnlineText.Text = Tr.T("В сети", "Тармакта", "Online", "Çevrimiçi", "Onlayn");
        PreviewProduct1.Text = Tr.T("Молоко 1 л", "Сүт 1 л", "Milk 1 l", "Süt 1 l", "Sut 1 l");
        PreviewProduct2.Text = Tr.T("Хлеб", "Нан", "Bread", "Ekmek", "Non");
        PreviewSearchText.Text = Tr.T("Поиск товара…", "Товарды издөө…", "Search…", "Ürün ara…", "Qidirish…");
        PreviewChipText.Text = Tr.T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha mahsulotlar");
        PreviewLine1.Text = Tr.T("Молоко × 2", "Сүт × 2", "Milk × 2", "Süt × 2", "Sut × 2");
        PreviewLine2.Text = Tr.T("Хлеб × 1", "Нан × 1", "Bread × 1", "Ekmek × 1", "Non × 1");
        PreviewSuccessText.Text = Tr.T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi");
        PreviewWarningText.Text = Tr.T("Мало", "Аз", "Low", "Az", "Kam");
        PreviewTotalLabel.Text = Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
        PreviewPayText.Text = Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash");
    }

    private static string KeyLabel(ColorKey key) => key switch
    {
        ColorKey.Accent => Tr.T("Акцент (кнопки, выделение)", "Акцент (баскычтар, белгилөө)", "Accent (buttons, selection)", "Vurgu (düğmeler, seçim)", "Urg'u (tugmalar, belgilash)"),
        ColorKey.AccentText => Tr.T("Текст на акценте", "Акценттеги текст", "Text on accent", "Vurgu üzerindeki metin", "Urg'u ustidagi matn"),
        ColorKey.Background => Tr.T("Фон окна", "Терезенин фону", "Window background", "Pencere arka planı", "Oyna foni"),
        ColorKey.Panel => Tr.T("Панели и карточки", "Панелдер жана карточкалар", "Panels and cards", "Paneller ve kartlar", "Panellar va kartochkalar"),
        ColorKey.Text => Tr.T("Основной текст", "Негизги текст", "Main text", "Ana metin", "Asosiy matn"),
        ColorKey.TextSoft => Tr.T("Второстепенный текст", "Кошумча текст", "Secondary text", "İkincil metin", "Ikkinchi darajali matn"),
        ColorKey.Success => Tr.T("Успех (оплата, «в сети»)", "Ийгилик (төлөө, «тармакта»)", "Success (payment, “online”)", "Başarı (ödeme, «çevrimiçi»)", "Muvaffaqiyat (to'lov, «onlayn»)"),
        ColorKey.Warning => Tr.T("Предупреждение", "Эскертүү", "Warning", "Uyarı", "Ogohlantirish"),
        _ => Tr.T("Ошибка и удаление", "Ката жана өчүрүү", "Error and delete", "Hata ve silme", "Xato va o'chirish"),
    };

    // ------------------------------------------------------------------ построение

    private void BuildBaseCombo()
    {
        BaseCombo.ItemsSource = AccentThemeService.AvailableThemes.Select(t => new ComboBoxItem { Content = t.Label, Tag = t.Id }).ToList();
        var current = AccentThemeService.Normalize(UserPreferences.Instance.AccentTheme);
        var baseId = CustomThemeStore.Find(current)?.BaseThemeId ?? current;
        BaseCombo.SelectedIndex = Math.Max(0, Array.FindIndex(AccentThemeService.AvailableThemes, t => t.Id == baseId));
    }

    private void BuildColorRows()
    {
        ColorRowsPanel.Children.Clear();
        _rows.Clear();
        foreach (var key in Enum.GetValues<ColorKey>())
        {
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorderStrong"));
            var label = new TextBlock { Text = KeyLabel(key), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 8, 0) };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var hex = new TextBox { Tag = key };
            hex.Classes.Add("te-hex");
            hex.TextChanged += Hex_TextChanged;
            hex.GotFocus += (_, _) => SelectKey(key);
            var reset = new Button
            {
                Tag = key, Width = 30, MinHeight = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
                Content = new PathIcon { Data = Application.Current?.TryFindResource("IconUndo", out var icon) == true ? icon as Geometry : null, Width = 13, Height = 13 },
            };
            reset.Classes.Add("lx-ghost");
            ToolTip.SetTip(reset, Tr.T("Вернуть цвет основы", "Негиздин түсүн кайтаруу", "Restore the base color", "Temel rengi geri al", "Asos rangini qaytarish"));
            reset.Click += ResetColor_Click;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            grid.Children.Add(swatch);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            Grid.SetColumn(hex, 2);
            grid.Children.Add(hex);
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);

            var row = new Border { Child = grid, Tag = key };
            row.Classes.Add("te-row");
            row.PointerPressed += (_, _) => SelectKey(key);
            ColorRowsPanel.Children.Add(row);
            _rows[key] = new RowParts(row, swatch, label, hex, reset);
        }
    }

    private void RefreshThemeList()
    {
        _suppress = true;
        try
        {
            var themes = CustomThemeStore.All.Select(t => t.Id == _working.Id ? _working : t).ToList();
            if (themes.All(t => t.Id != _working.Id))
                themes.Add(_working);

            var items = new List<ListBoxItem>();
            foreach (var theme in themes)
            {
                var colors = AccentThemeService.GetEffectiveColors(theme, _editDark);
                var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = -4, VerticalAlignment = VerticalAlignment.Center };
                foreach (var hex in new[] { colors.Background, colors.Panel, colors.Accent, colors.Text })
                {
                    swatches.Children.Add(new Border
                    {
                        Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.Parse(hex ?? "#888888")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#66808080")), BorderThickness = new Thickness(1),
                    });
                }
                var name = new TextBlock
                {
                    Text = theme.Name + (theme.Id == _working.Id && IsDirty ? " •" : ""),
                    FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
                var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                content.Children.Add(swatches);
                Grid.SetColumn(name, 1);
                content.Children.Add(name);
                items.Add(new ListBoxItem { Content = content, Tag = theme.Id });
            }

            ThemeList.ItemsSource = items;
            ThemeList.SelectedItem = items.FirstOrDefault(i => (string)i.Tag! == _working.Id);
            DuplicateButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
        }
        finally
        {
            _suppress = false;
        }
    }

    // ------------------------------------------------------------------ загрузка и применение

    private void LoadTheme(CustomThemeDefinition def, bool saved)
    {
        _working = def;
        _savedJson = saved ? Serialize(def) : null;
        _suppress = true;
        try
        {
            NameBox.Text = def.Name;
            var baseLabel = AccentThemeService.AvailableThemes.FirstOrDefault(t => t.Id == def.BaseThemeId)?.Label ?? def.BaseThemeId;
            BaseInfoText.Text = Tr.T($"Основа: {baseLabel}", $"Негизи: {baseLabel}", $"Based on: {baseLabel}", $"Temel: {baseLabel}", $"Asosi: {baseLabel}");
            RadiusSlider.Value = def.CornerRadius ?? AccentThemeService.GetBaseCornerRadius(def.BaseThemeId);
            FontSlider.Value = def.FontSize ?? AccentThemeService.GetBaseFontSize(def.BaseThemeId);
            RadiusValueText.Text = $"{RadiusSlider.Value:0} px";
            FontValueText.Text = $"{FontSlider.Value:0.#} px";
        }
        finally
        {
            _suppress = false;
        }

        RefreshVariantButtons();
        RefreshRows(null);
        SelectKey(_selectedKey);
        ApplyPreview();
        RefreshThemeList();
        SetStatus(saved ? "" : Tr.T("Новая тема — не забудьте сохранить.", "Жаңы тема — сактоону унутпаңыз.",
            "New theme — don't forget to save it.", "Yeni tema — kaydetmeyi unutmayın.", "Yangi mavzu — saqlashni unutmang."), warn: false);
    }

    /// <summary>Редактируемая тема — сразу на всю программу, вместе с нужным вариантом (светлым или тёмным).</summary>
    private void ApplyPreview()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _editDark ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentThemeService.ApplyDefinition(_working, _editDark);
        UpdateContrastHint();
    }

    /// <summary>Окно закрыто: касса возвращается к своей теме из настроек (если тему применили —
    /// это она и есть, если нет — прежняя, и несохранённые правки исчезают).</summary>
    private static void RestoreAppTheme()
    {
        try
        {
            App.ApplyTheme(UserPreferences.Instance.DarkTheme);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор тем: не удалось вернуть тему кассы: {ex.Message}", "WARNING");
        }
    }

    private void RefreshRows(ColorKey? typingKey)
    {
        var effective = AccentThemeService.GetEffectiveColors(_working, _editDark);
        var overrides = _working.ColorsFor(_editDark);
        _suppress = true;
        try
        {
            foreach (var (key, parts) in _rows)
            {
                var hex = Get(effective, key) ?? "#888888";
                var overridden = Get(overrides, key) is not null;
                parts.Swatch.Background = new SolidColorBrush(Color.Parse(hex));
                parts.Label.FontWeight = overridden ? FontWeight.SemiBold : FontWeight.Normal;
                parts.Reset.IsVisible = overridden;
                if (typingKey != key)
                    parts.Hex.Text = hex;
                parts.Row.Classes.Set("selected", key == _selectedKey);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SelectKey(ColorKey key)
    {
        _selectedKey = key;
        foreach (var (k, parts) in _rows)
            parts.Row.Classes.Set("selected", k == key);
        var hex = Get(AccentThemeService.GetEffectiveColors(_working, _editDark), key) ?? "#888888";
        PickerLabel.Text = Tr.T($"Цвет: {KeyLabel(key)}", $"Түс: {KeyLabel(key)}", $"Color: {KeyLabel(key)}", $"Renk: {KeyLabel(key)}", $"Rang: {KeyLabel(key)}");
        ColorPicker.SetHex(hex, hex);
    }

    private void SetOverride(ColorKey key, string? hex, ColorKey? typingKey = null)
    {
        Set(_working.ColorsFor(_editDark), key, hex);
        RefreshRows(typingKey);
        ApplyPreview();
        RefreshDirtyMarker();
    }

    private void RefreshDirtyMarker()
    {
        if (ThemeList.SelectedItem is ListBoxItem { Content: Grid grid } && grid.Children.OfType<TextBlock>().FirstOrDefault() is { } name)
            name.Text = _working.Name + (IsDirty ? " •" : "");
    }

    private void UpdateContrastHint()
    {
        var c = AccentThemeService.GetEffectiveColors(_working, _editDark);
        var problems = new List<string>();
        void Check(string? fg, string? bg, double min, string what)
        {
            if (fg is null || bg is null)
                return;
            var ratio = AccentThemeService.Contrast(fg, bg);
            if (ratio < min)
                problems.Add(Tr.T($"{what}: контраст {ratio:0.0}:1, нужно от {min:0.#}:1",
                    $"{what}: контраст {ratio:0.0}:1, {min:0.#}:1 же андан жогору керек",
                    $"{what}: contrast {ratio:0.0}:1, needs at least {min:0.#}:1",
                    $"{what}: kontrast {ratio:0.0}:1, en az {min:0.#}:1 olmalı",
                    $"{what}: kontrast {ratio:0.0}:1, kamida {min:0.#}:1 kerak"));
        }

        Check(c.Text, c.Panel, 4.5, KeyLabel(ColorKey.Text));
        Check(c.TextSoft, c.Panel, 3.0, KeyLabel(ColorKey.TextSoft));
        Check(c.AccentText, c.Accent, 3.0, KeyLabel(ColorKey.AccentText));
        ContrastText.IsVisible = problems.Count > 0;
        ContrastText.Text = "⚠ " + string.Join("\n⚠ ", problems);
    }

    private void RefreshVariantButtons()
    {
        LightVariantButton.Classes.Set("active", !_editDark);
        DarkVariantButton.Classes.Set("active", _editDark);
    }

    private void SetStatus(string text, bool warn)
    {
        StatusText.Text = text;
        StatusText.Foreground = (IBrush?)this.FindResource(warn ? "BrushDanger" : "BrushTextSoft") ?? Brushes.Gray;
    }

    private static string? Get(CustomThemeColors c, ColorKey key) => key switch
    {
        ColorKey.Accent => c.Accent,
        ColorKey.AccentText => c.AccentText,
        ColorKey.Background => c.Background,
        ColorKey.Panel => c.Panel,
        ColorKey.Text => c.Text,
        ColorKey.TextSoft => c.TextSoft,
        ColorKey.Success => c.Success,
        ColorKey.Warning => c.Warning,
        _ => c.Danger,
    };

    private static void Set(CustomThemeColors c, ColorKey key, string? hex)
    {
        switch (key)
        {
            case ColorKey.Accent: c.Accent = hex; break;
            case ColorKey.AccentText: c.AccentText = hex; break;
            case ColorKey.Background: c.Background = hex; break;
            case ColorKey.Panel: c.Panel = hex; break;
            case ColorKey.Text: c.Text = hex; break;
            case ColorKey.TextSoft: c.TextSoft = hex; break;
            case ColorKey.Success: c.Success = hex; break;
            case ColorKey.Warning: c.Warning = hex; break;
            default: c.Danger = hex; break;
        }
    }

    // ------------------------------------------------------------------ события

    private void OnPickerColorChanged()
    {
        if (_suppress || !ColorPicker.TryGetHex(out var hex))
            return;
        SetOverride(_selectedKey, CustomThemeStore.NormalizeHex(hex));
    }

    private void Hex_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || sender is not TextBox { Tag: ColorKey key } box)
            return;
        var hex = CustomThemeStore.NormalizeHex(box.Text);
        if (hex is null || (box.Text?.Trim().TrimStart('#').Length ?? 0) < 6)
            return; // ещё набирают
        // TextChanged приходит и после того, как поле заполнил сам редактор (событие отложенное):
        // тот же цвет, что уже действует, — не правка кассира, свой цвет из него не делаем.
        if (string.Equals(hex, Get(AccentThemeService.GetEffectiveColors(_working, _editDark), key), StringComparison.OrdinalIgnoreCase))
            return;
        SetOverride(key, hex, typingKey: key);
        if (key == _selectedKey)
        {
            _suppress = true;
            try { ColorPicker.SetHex(hex, hex); }
            finally { _suppress = false; }
        }
    }

    private void ResetColor_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ColorKey key })
        {
            SetOverride(key, null);
            if (key == _selectedKey)
                SelectKey(key);
        }
    }

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;
        _working.Name = NameBox.Text ?? "";
        RefreshDirtyMarker();
    }

    private void RadiusSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Ползунок с Minimum > 0 выставляет значение ещё внутри InitializeComponent — до того,
        // как поля окна заполнены (тот же случай описан в ScreenSettingsView).
        if (RadiusValueText is null)
            return;
        RadiusValueText.Text = $"{e.NewValue:0} px";
        if (_suppress)
            return;
        _working.CornerRadius = e.NewValue;
        ApplyPreview();
        RefreshDirtyMarker();
    }

    private void FontSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (FontValueText is null)
            return;
        FontValueText.Text = $"{e.NewValue:0.#} px";
        if (_suppress)
            return;
        _working.FontSize = e.NewValue;
        ApplyPreview();
        RefreshDirtyMarker();
    }

    private void LightVariant_Click(object? sender, RoutedEventArgs e) => SwitchVariant(false);

    private void DarkVariant_Click(object? sender, RoutedEventArgs e) => SwitchVariant(true);

    private void SwitchVariant(bool dark)
    {
        if (_editDark == dark)
            return;
        _editDark = dark;
        RefreshVariantButtons();
        RefreshRows(null);
        SelectKey(_selectedKey);
        ApplyPreview();
        RefreshThemeList();
    }

    private void ThemeList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ThemeList.SelectedItem is not ListBoxItem { Tag: string id } || id == _working.Id)
            return;
        if (!ConfirmLeaveUnsaved())
        {
            RefreshThemeList();
            return;
        }
        if (CustomThemeStore.Find(id) is { } theme)
            LoadTheme(theme.Clone(), saved: true);
    }

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveUnsaved())
            return;
        var baseId = (BaseCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "classic";
        LoadTheme(CustomThemeStore.Create(baseId, CustomThemeStore.DefaultName), saved: false);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Duplicate_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveUnsaved())
            return;
        var suffix = Tr.T("копия", "көчүрмө", "copy", "kopya", "nusxa");
        LoadTheme(CustomThemeStore.Create(_working.BaseThemeId, $"{_working.Name} ({suffix})", _working), saved: false);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _working.Light = new CustomThemeColors();
        _working.Dark = new CustomThemeColors();
        _working.CornerRadius = null;
        _working.FontSize = null;
        _suppress = true;
        try
        {
            RadiusSlider.Value = AccentThemeService.GetBaseCornerRadius(_working.BaseThemeId);
            FontSlider.Value = AccentThemeService.GetBaseFontSize(_working.BaseThemeId);
        }
        finally
        {
            _suppress = false;
        }
        RefreshRows(null);
        SelectKey(_selectedKey);
        ApplyPreview();
        RefreshDirtyMarker();
        SetStatus(Tr.T("Цвета, скругление и шрифт — как у основы. Сохраните, чтобы закрепить.",
            "Түстөр, тегеректик жана шрифт — негиздегидей. Бекитүү үчүн сактаңыз.",
            "Colors, rounding and font now match the base. Save to keep it.",
            "Renkler, yuvarlaklık ve yazı tipi temel ile aynı. Kalıcı yapmak için kaydedin.",
            "Ranglar, yumaloqlik va shrift — asosdagidek. Mustahkamlash uchun saqlang."), warn: false);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => SaveWorking();

    private void SaveWorking()
    {
        _working.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? CustomThemeStore.DefaultName : NameBox.Text.Trim();
        CustomThemeStore.Save(_working);
        var stored = CustomThemeStore.Find(_working.Id)!;
        _working.Name = stored.Name;
        _savedJson = Serialize(_working);
        _suppress = true;
        try { NameBox.Text = _working.Name; }
        finally { _suppress = false; }
        RefreshThemeList();
        SetStatus(Tr.T($"Тема «{_working.Name}» сохранена.", $"«{_working.Name}» темасы сакталды.",
            $"Theme “{_working.Name}” saved.", $"«{_working.Name}» teması kaydedildi.", $"«{_working.Name}» mavzusi saqlandi."), warn: false);
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        SaveWorking();
        // Светлый/тёмный режим кассы не трогаем — тема несёт оба варианта.
        App.ApplyAccentTheme(_working.Id);
        Close(true);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = PosConfirmDialog.Show(this,
            Tr.T("Удалить тему?", "Теманы өчүрөсүзбү?", "Delete the theme?", "Tema silinsin mi?", "Mavzu o'chirilsinmi?"),
            Tr.T($"Тема «{_working.Name}» будет удалена с этой кассы.", $"«{_working.Name}» темасы бул кассадан өчүрүлөт.",
                $"The theme “{_working.Name}” will be removed from this till.", $"«{_working.Name}» teması bu kasadan silinecek.",
                $"«{_working.Name}» mavzusi bu kassadan o'chiriladi."),
            Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger);
        if (!confirmed)
            return;

        var deletedId = _working.Id;
        var baseId = _working.BaseThemeId;
        CustomThemeStore.Delete(deletedId);
        if (string.Equals(UserPreferences.Instance.AccentTheme, deletedId, StringComparison.OrdinalIgnoreCase))
            App.ApplyAccentTheme(baseId);

        var next = CustomThemeStore.All.FirstOrDefault();
        if (next is not null)
            LoadTheme(next.Clone(), saved: true);
        else
            LoadTheme(CustomThemeStore.Create(baseId, CustomThemeStore.DefaultName), saved: false);
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (StorageProvider is not { CanSave: true } storage)
                return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = ExportText.Text,
                SuggestedFileName = CustomThemeStore.SuggestFileName(_working),
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType(Tr.T("Тема NurMarket", "NurMarket темасы", "NurMarket theme", "NurMarket teması", "NurMarket mavzusi")) { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if (file is null)
                return;
            await using (var stream = await file.OpenWriteAsync().ConfigureAwait(true))
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(CustomThemeStore.Export(_working)).ConfigureAwait(true);
            SetStatus(Tr.T($"Тема выгружена в файл {file.Name}.", $"Тема {file.Name} файлына түшүрүлдү.",
                $"The theme was exported to {file.Name}.", $"Tema {file.Name} dosyasına aktarıldı.", $"Mavzu {file.Name} fayliga eksport qilindi."), warn: false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Экспорт темы: {ex}", "WARNING");
            SetStatus(Tr.T("Не удалось сохранить файл темы.", "Теманын файлын сактоо мүмкүн болгон жок.", "Could not save the theme file.",
                "Tema dosyası kaydedilemedi.", "Mavzu faylini saqlab bo'lmadi."), warn: true);
        }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveUnsaved())
            return;
        try
        {
            if (StorageProvider is not { CanOpen: true } storage)
                return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = ImportText.Text,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Tr.T("Тема NurMarket", "NurMarket темасы", "NurMarket theme", "NurMarket teması", "NurMarket mavzusi")) { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            var file = files.FirstOrDefault();
            if (file is null)
                return;
            string json;
            await using (var stream = await file.OpenReadAsync().ConfigureAwait(true))
            using (var reader = new StreamReader(stream))
                json = await reader.ReadToEndAsync().ConfigureAwait(true);
            if (json.Length > 200_000)
                throw new InvalidDataException("file is too large");

            var imported = CustomThemeStore.Import(json);
            LoadTheme(imported.Clone(), saved: true);
            SetStatus(Tr.T($"Тема «{imported.Name}» добавлена.", $"«{imported.Name}» темасы кошулду.",
                $"Theme “{imported.Name}” added.", $"«{imported.Name}» teması eklendi.", $"«{imported.Name}» mavzusi qo'shildi."), warn: false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Импорт темы: {ex.Message}", "WARNING");
            SetStatus(Tr.T("Этот файл не похож на тему NurMarket.", "Бул файл NurMarket темасына окшобойт.",
                "This file does not look like a NurMarket theme.", "Bu dosya bir NurMarket temasına benzemiyor.",
                "Bu fayl NurMarket mavzusiga o'xshamaydi."), warn: true);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveUnsaved())
            return;
        Close(false);
    }

    /// <summary>Несохранённые правки: «Сохранить» — сохраняем и продолжаем, «Не сохранять» —
    /// отбрасываем. true — можно уходить с текущей темы.</summary>
    private bool ConfirmLeaveUnsaved()
    {
        if (!IsDirty)
            return true;
        var save = PosConfirmDialog.Show(this,
            Tr.T("Сохранить изменения?", "Өзгөртүүлөрдү сактайсызбы?", "Save changes?", "Değişiklikler kaydedilsin mi?", "O'zgarishlar saqlansinmi?"),
            Tr.T($"В теме «{_working.Name}» есть несохранённые изменения.", $"«{_working.Name}» темасында сакталбаган өзгөртүүлөр бар.",
                $"The theme “{_working.Name}” has unsaved changes.", $"«{_working.Name}» temasında kaydedilmemiş değişiklikler var.",
                $"«{_working.Name}» mavzusida saqlanmagan o'zgarishlar bor."),
            Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"),
            Tr.T("Не сохранять", "Сактабоо", "Don't save", "Kaydetme", "Saqlamaslik"));
        if (save)
            SaveWorking();
        return true;
    }

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
            Close_Click(this, new RoutedEventArgs());
        }
    }
}
