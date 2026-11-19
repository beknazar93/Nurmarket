using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 («Неудобства от владельца», п. 11: «настройка ценников отсутствует»). Настройки этикетки были, но свёрнуты в строку «▾ Настройки этикетки»
/// с предпросмотром 170 px — владелец их не находил. Теперь:
/// <list type="bullet">
/// <item>на вкладке «Штрихкоды» шириной от 1000 px — постоянная панель «Настройка ценника» справа: крупный живой предпросмотр сверху, ниже размер, копии,
/// поля (галочки), штрихкод, шрифт, принтер, «Мои шаблоны»; уже 1000 px (и в отдельном окне) — прежняя сворачиваемая карточка над списком;</item>
/// <item>новые настройки: «Тип штрихкода» (Авто / EAN-13 / Code 128 / QR), «Шрифт», «Размер текста» — поля шаблона этикетки, которые раньше менялись только в редакторе;</item>
/// <item>«Мои шаблоны»: сохранить текущие настройки под именем, выбрать, удалить (BarcodePrintSettings.Presets; старый файл настроек читается как раньше).</item>
/// </list></summary>
public sealed partial class BarcodePrintView
{
    private const double SidePanelWidth = 340;
    public const double SideOnWidth = 1240; // 2026-11-19: ширина вкладки, с которой панель справа включается (выключается ниже SideOffWidth — без мигания)
    public const double SideOffWidth = 1200;
    // 2026-11-20: в высоком окне (от 900 px) список карточек и без панели не страдает по высоте, а панель отнимает 352 px ширины и ломает колонки настроек (4→2 при РАСШИРЕНИИ окна, LayoutStability) — там панель включается только с 1560 px
    public const double TallWindowHeight = 900, TallSideOnWidth = 1560, TallSideOffWidth = 1520;
    private double SideOnNow => (TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 0) >= TallWindowHeight ? TallSideOnWidth : SideOnWidth;
    private bool _sideMode;
    private Grid _rootGrid = null!;
    private StackPanel _settingsLeft = null!;
    private TextBlock _sideTitle = null!;
    private readonly List<StackPanel> _inlinePanels = new();
    private ComboBox _formatCombo = null!;
    private ComboBox _fontCombo = null!;
    private ComboBox _textSizeCombo = null!;
    private ComboBox _presetCombo = null!;
    private TextBox _presetName = null!;
    private Button _presetDelete = null!;

    private static readonly string[] Fonts = { "Arial", "Tahoma", "Verdana", "Segoe UI", "Times New Roman" };
    private static readonly double[] TextSizes = { 0, 8, 10, 12, 14, 16, 20, 24 };

    private Control BuildSideTitle()
    {
        _sideTitle = new TextBlock
        {
            Text = T("Настройка ценника", "Баа белгисин жөндөө", "Price tag setup", "Fiyat etiketi ayarı", "Narx yorlig'ini sozlash"),
            FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
        };
        Use(_sideTitle, TextBlock.ForegroundProperty, "BrushText");
        return _sideTitle;
    }

    /// <summary>«Тип штрихкода», «Шрифт», «Размер текста».</summary>
    private Control BuildTagSetupRow()
    {
        _formatCombo = new ComboBox { MinWidth = 150 };
        foreach (var s in new[] { T("Авто", "Авто", "Auto", "Otomatik", "Avto"), "EAN-13", "Code 128", "QR" })
            _formatCombo.Items.Add(s);
        ToolTip.SetTip(_formatCombo, T("Авто — по длине кода: 13 цифр печатаются как EAN-13, остальное — Code 128.", "Авто — коддун узундугу боюнча: 13 цифра EAN-13, калганы — Code 128.",
            "Auto — by code length: 13 digits print as EAN-13, the rest as Code 128.", "Otomatik — kod uzunluğuna göre: 13 hane EAN-13, diğerleri Code 128.", "Avto — kod uzunligi bo'yicha: 13 raqam EAN-13, qolganlari Code 128."));
        _fontCombo = new ComboBox { MinWidth = 150 };
        _textSizeCombo = new ComboBox { MinWidth = 110 };
        FillFontCombos();
        _formatCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _formatCombo.SelectedIndex < 0)
                return;
            _template.BarcodeFormat = (LabelBarcodeFormat)_formatCombo.SelectedIndex;
            Relayout();
            if (_template.BarcodeFormat == LabelBarcodeFormat.QrCode && LabelCodeModes.QrSizeProblem(_template, "4870145004807") is { } problem)
                SetStatus(problem);
        };
        _fontCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _fontCombo.SelectedItem is not string font)
                return;
            _template.FontFamily = font;
            TemplateChanged();
        };
        _textSizeCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _textSizeCombo.SelectedIndex < 0 || _textSizeCombo.SelectedIndex >= _textSizeValues.Count)
                return;
            _template.FontSizePx = _textSizeValues[_textSizeCombo.SelectedIndex];
            TemplateChanged();
        };
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Inline(T("Тип штрихкода", "Штрихкод түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"), _formatCombo));
        row.Children.Add(Inline(T("Шрифт", "Шрифт", "Font", "Yazı tipi", "Shrift"), _fontCombo));
        row.Children.Add(Inline(T("Размер текста", "Тексттин өлчөмү", "Text size", "Metin boyutu", "Matn o'lchami"), _textSizeCombo));
        return row;
    }

    private readonly List<double> _textSizeValues = new();

    /// <summary>Списки «Шрифт» и «Размер текста» по текущему шаблону: значение из редактора, которого нет в списке, добавляется (ничего не теряется).</summary>
    private void FillFontCombos()
    {
        _formatCombo.SelectedIndex = Math.Clamp((int)_template.BarcodeFormat, 0, 3);
        _fontCombo.Items.Clear();
        var fonts = Fonts.ToList();
        if (!string.IsNullOrWhiteSpace(_template.FontFamily) && !fonts.Contains(_template.FontFamily, StringComparer.OrdinalIgnoreCase))
            fonts.Add(_template.FontFamily);
        foreach (var f in fonts)
            _fontCombo.Items.Add(f);
        _fontCombo.SelectedIndex = Math.Max(0, fonts.FindIndex(f => string.Equals(f, _template.FontFamily, StringComparison.OrdinalIgnoreCase)));

        _textSizeValues.Clear();
        _textSizeValues.AddRange(TextSizes);
        if (_template.FontSizePx > 0 && !_textSizeValues.Any(v => Math.Abs(v - _template.FontSizePx) < 0.05))
            _textSizeValues.Add(_template.FontSizePx);
        _textSizeValues.Sort();
        _textSizeCombo.Items.Clear();
        foreach (var v in _textSizeValues)
            _textSizeCombo.Items.Add(v <= 0 ? T("Авто", "Авто", "Auto", "Otomatik", "Avto") : v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " px");
        _textSizeCombo.SelectedIndex = Math.Max(0, _textSizeValues.FindIndex(v => Math.Abs(v - Math.Max(0, _template.FontSizePx)) < 0.05));
    }

    /// <summary>Вызывается из SyncSettingsFromTemplate (под флагом _init).</summary>
    private void SyncTagSetupFromTemplate() => FillFontCombos();

    // ------------------------------------------------------------------ «Мои шаблоны»

    private Control BuildPresetsRow()
    {
        _presetCombo = new ComboBox { MinWidth = 180, MaxWidth = 300, PlaceholderText = T("Мои шаблоны…", "Менин шаблондорум…", "My templates…", "Şablonlarım…", "Mening shablonlarim…") };
        _presetName = new TextBox { Width = 180, MinHeight = 44, MaxLength = 40, Watermark = T("Название шаблона", "Шаблондун аталышы", "Template name", "Şablon adı", "Shablon nomi") };
        var save = Ghost(T("Сохранить шаблон", "Шаблонду сактоо", "Save template", "Şablonu kaydet", "Shablonni saqlash"));
        ToolTip.SetTip(save, T("Сохранить текущие размер, поля, шрифт, штрихкод и копии под этим названием", "Учурдагы өлчөм, талаалар, шрифт, штрихкод жана көчүрмөлөрдү ушул аталыш менен сактоо",
            "Save the current size, fields, font, barcode and copies under this name", "Geçerli boyut, alanlar, yazı tipi, barkod ve kopyaları bu adla kaydet", "Joriy o'lcham, maydonlar, shrift, shtrix-kod va nusxalarni shu nom bilan saqlash"));
        _presetDelete = Ghost(T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"));
        _presetDelete.IsEnabled = false;
        FillPresets(null);
        _presetCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _presetCombo.SelectedIndex < 0 || _presetCombo.SelectedIndex >= _settings.Presets.Count)
                return;
            ApplyPreset(_settings.Presets[_presetCombo.SelectedIndex]);
        };
        save.Click += (_, _) => SavePreset();
        _presetDelete.Click += (_, _) => DeletePreset();
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(Inline(T("Мои шаблоны", "Менин шаблондорум", "My templates", "Şablonlarım", "Mening shablonlarim"), _presetCombo));
        _presetName.Margin = new Thickness(0, 0, 8, 6);
        save.Margin = new Thickness(0, 0, 8, 6);
        _presetDelete.Margin = new Thickness(0, 0, 8, 6);
        row.Children.Add(_presetName);
        row.Children.Add(save);
        row.Children.Add(_presetDelete);
        return row;
    }

    private void FillPresets(string? select)
    {
        var was = _init;
        _init = true;
        try
        {
            _presetCombo.Items.Clear();
            foreach (var p in _settings.Presets)
                _presetCombo.Items.Add(p.Name);
            _presetCombo.SelectedIndex = select is null ? -1 : _settings.Presets.FindIndex(p => string.Equals(p.Name, select, StringComparison.CurrentCultureIgnoreCase));
            _presetCombo.IsEnabled = _settings.Presets.Count > 0;
            _presetDelete.IsEnabled = _presetCombo.SelectedIndex >= 0;
        }
        finally { _init = was; }
    }

    private void SavePreset()
    {
        var name = (_presetName.Text ?? "").Trim();
        if (name.Length == 0 && _presetCombo.SelectedIndex >= 0 && _presetCombo.SelectedIndex < _settings.Presets.Count)
            name = _settings.Presets[_presetCombo.SelectedIndex].Name; // имя не введено, шаблон выбран — перезаписываем его
        if (name.Length == 0)
        {
            SetStatus(T("Введите название шаблона.", "Шаблондун аталышын жазыңыз.", "Enter a template name.", "Şablon adını girin.", "Shablon nomini kiriting."));
            _presetName.Focus();
            return;
        }
        var preset = new BarcodeLabelPreset { Name = name, Template = _template.Clone(), DefaultCopies = _settings.DefaultCopies, CopiesMode = _settings.CopiesMode };
        var index = _settings.Presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0)
            _settings.Presets[index] = preset;
        else if (_settings.Presets.Count >= 30)
        {
            SetStatus(T("Шаблонов уже 30 — удалите лишний.", "Шаблондор 30 болду — ашыкчасын өчүрүңүз.", "There are already 30 templates — delete one.", "Zaten 30 şablon var — birini silin.", "Shablonlar 30 ta — ortiqchasini o'chiring."));
            return;
        }
        else
            _settings.Presets.Add(preset);
        BarcodePrintSettingsStore.Save(_settings);
        _presetName.Text = "";
        FillPresets(name);
        SetStatus(T($"Шаблон «{name}» сохранён.", $"«{name}» шаблону сакталды.", $"Template “{name}” saved.", $"«{name}» şablonu kaydedildi.", $"«{name}» shabloni saqlandi."));
    }

    private void DeletePreset()
    {
        var index = _presetCombo.SelectedIndex;
        if (index < 0 || index >= _settings.Presets.Count)
            return;
        var name = _settings.Presets[index].Name;
        _settings.Presets.RemoveAt(index);
        BarcodePrintSettingsStore.Save(_settings);
        FillPresets(null);
        SetStatus(T($"Шаблон «{name}» удалён.", $"«{name}» шаблону өчүрүлдү.", $"Template “{name}” deleted.", $"«{name}» şablonu silindi.", $"«{name}» shabloni o'chirildi."));
    }

    /// <summary>Сохранённый шаблон — в текущие настройки: шаблон этикетки целиком и копии; поля панели, карточки и предпросмотр обновляются.</summary>
    private void ApplyPreset(BarcodeLabelPreset preset)
    {
        _template = preset.Template.Clone();
        _settings.DefaultCopies = Math.Clamp(preset.DefaultCopies, 1, 99);
        _settings.CopiesMode = LabelPrintMath.NormalizeMode(preset.CopiesMode);
        BarcodePrintSettingsStore.Save(_settings);
        SyncSettingsFromTemplate();
        _init = true;
        try
        {
            _copiesBox.Value = _settings.DefaultCopies;
            _copiesModeCombo.SelectedIndex = CopiesModeIndex(_settings.CopiesMode);
            _copiesBox.IsEnabled = _settings.CopiesMode == "default";
        }
        finally { _init = false; }
        _presetDelete.IsEnabled = true;
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        TemplateChanged(); // шаблон изменён — сохранится при закрытии, предпросмотр и предупреждения пересчитаются
        RenderPage();
        UpdateSelectionUi();
    }

    // ------------------------------------------------------------------ панель справа

    /// <summary>Вкладка шириной от 1000 px в окне не ниже 690 px — настройки ценника постоянной панелью справа (выключается ниже 960 / 660 — без мигания на пороге).</summary>
    private void ApplySideLayout(double width)
    {
        if (!_embedded || width <= 0)
            return;
        // Низкое окно (касса 1024×600/640): панели во всю высоту не хватает места — остаётся сворачиваемая карточка над списком.
        var top = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 0;
        var tall = _sideMode ? top >= 660 : top >= 690;
        // 2026-11-19 («как будет в маленьких экранах?»): порог поднят с 1000/960 до 1240/1200 — на 1024×768 панель 340 px оставляла списку ~680 px (2 колонки,
        // чекбокс «Сканер: добавлять в печать» обрезан); на узких вкладках настройки остаются сворачиваемой карточкой над списком.
        var side = tall && (_sideMode ? width >= (top >= TallWindowHeight ? TallSideOffWidth : SideOffWidth) : width >= SideOnNow);
        if (side == _sideMode)
            return;
        _sideMode = side;
        var paper = (_previewCard.Child as StackPanel)?.Children.OfType<Border>().FirstOrDefault();
        foreach (var p in _inlinePanels)
        {
            // В узкой панели подпись стоит над полем, а не слева от него (иначе «Высота штрих-кода, мм» + поле не помещаются в 316 px).
            p.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
            p.Spacing = side ? 2 : 8;
            p.VerticalAlignment = side ? VerticalAlignment.Top : VerticalAlignment.Center;
        }
        _sideTitle.IsVisible = side;
        _settingsToggle.IsVisible = !side;
        if (side)
        {
            _rootGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Grid.SetColumn(_settingsCard, 1);
            Grid.SetRow(_settingsCard, 0);
            Grid.SetRowSpan(_settingsCard, 5);
            _settingsCard.Width = SidePanelWidth;
            _settingsCard.Margin = new Thickness(12, 0, 0, 0);
            _settingsGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _settingsGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(_previewCard, 0);
            Grid.SetRow(_previewCard, 1);
            Grid.SetRow(_settingsLeft, 2);
            _settingsGrid.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            _settingsLeft.Children.Remove(_sideTitle);
            if (_sideTitle.Parent is null)
                _settingsGrid.Children.Insert(0, _sideTitle);
            _previewCard.Width = double.NaN;
            _previewCard.Margin = new Thickness(0, 0, 0, 10);
            if (paper is not null)
                paper.Height = 170;
            SetSettingsOpen(true);
        }
        else
        {
            _rootGrid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(_settingsCard, 0);
            Grid.SetRow(_settingsCard, 1);
            Grid.SetRowSpan(_settingsCard, 1);
            _settingsCard.Width = double.NaN;
            _settingsCard.Margin = new Thickness(0, 0, 0, 8);
            _settingsGrid.Children.Remove(_sideTitle);
            if (_sideTitle.Parent is null)
                _settingsLeft.Children.Insert(0, _sideTitle);
            _settingsGrid.RowDefinitions = new RowDefinitions("Auto");
            _settingsGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Grid.SetRow(_settingsLeft, 0);
            Grid.SetRow(_previewCard, 0);
            Grid.SetColumn(_previewCard, 1);
            _previewCard.Width = 170;
            _previewCard.Margin = new Thickness(12, 0, 0, 0);
            if (paper is not null)
                paper.Height = 92;
            SetSettingsOpen(_settingsUserOpen && Bounds.Height >= 640);
        }
    }
}
