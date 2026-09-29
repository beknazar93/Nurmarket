using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Настройки → Весы.
///
/// 2026-09-28, редизайн по просьбе владельца («сделай нормальными настройки отправки на весы»):
/// раньше здесь одной лентой шли COM-весы, «Сетевые весы» (поля ШТРИХ-ПРИНТ и четыре кнопки
/// подряд), доп. весы, табло и «PLU на весах Штрих-М» с выбором модели и дублем кнопок — владелец
/// ввёл адрес TM-30F в поля ШТРИХ-ПРИНТ. Теперь четыре раздела: «Весы на кассе» (COM, доп. весы),
/// «Весы с этикетками» (список весов, у каждых свои марка, адрес и категории; настройка одних весов —
/// окно LabelScaleEditWindow; ниже — «вес или сумма в штрихкоде» по префиксам), «Штрих-код: вес /
/// сумма» (ScaleBarcodeSetupPanel: правила, мастер «Настроить по этикетке») и «Табло цены». Поля
/// COM-весов и табло по-прежнему читает и сохраняет PosSettingsWindow по своим x:Name.</summary>
public partial class ScaleSettingsView : UserControl
{
    public event EventHandler? SaveRequested;

    /// <summary>Раздел, открытый в прошлый раз (до закрытия кассы): владелец настраивает весы
    /// в несколько заходов и возвращается туда же. -1 — ещё не открывали.</summary>
    private static int _lastSection = -1;

    public ScaleSettingsView()
    {
        InitializeComponent();
        // Правила по префиксам стоят в двух местах (кратко — у этикеточных весов, полностью — в
        // разделе «Штрих-код»): поменяли в одном — перерисовываем другое.
        BarcodePanel.RulesChanged += () => LabelRulesPanel.Refresh();
        LabelRulesPanel.RulesChanged += () => BarcodePanel.Refresh();
        LabelRulesPanel.WizardRequested += () => ShowSection(2);
    }

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    private void ScaleSettingsView_Loaded(object? sender, RoutedEventArgs e)
    {
        RefreshPluCardVisibility();
        BuildScaleList();
    }

    /// <summary>Раздел «Весы с этикетками» скрыт целиком на тарифе «Старт», пока доп. услуга не
    /// куплена (см. TariffGate.CanUseScales) — вызывается и при повторном открытии окна настроек на
    /// случай, если кассир только что купил её в Маркетплейсе (тот же приём, что и
    /// RefreshPaidFeatureVisibility в WarehouseWindow).</summary>
    public void RefreshPluCardVisibility()
    {
        var allowed = TariffGate.CanUseScales;
        TabLabelButton.IsVisible = allowed;
        SectionTabsGrid.Columns = allowed ? 4 : 3;
        // Первый заход: весы у кассы не включены, а этикеточные доступны — открываем их (там же
        // «вес или сумма в штрихкоде»); иначе — весы на кассе.
        if (_lastSection < 0)
            _lastSection = allowed && !UserPreferences.Instance.ScaleEnabled ? 1 : 0;
        if (!allowed && _lastSection == 1)
            _lastSection = 0;
        ShowSection(_lastSection);
    }

    private void SectionTab_Click(object? sender, RoutedEventArgs e)
    {
        var index = sender == TabLabelButton ? 1
            : sender == TabBarcodeButton ? 2
            : sender == TabDisplayButton ? 3
            : 0;
        ShowSection(index);
    }

    /// <summary>Показывает один раздел страницы.</summary>
    public void ShowSection(int index)
    {
        if (index == 1 && !TariffGate.CanUseScales)
            index = 0;
        _lastSection = index;
        TabWeighingButton.IsChecked = index == 0;
        TabLabelButton.IsChecked = index == 1;
        TabBarcodeButton.IsChecked = index == 2;
        TabDisplayButton.IsChecked = index == 3;
        WeighingSection.IsVisible = index == 0;
        PluCard.IsVisible = index == 1;
        BarcodeSection.IsVisible = index == 2;
        DisplaySection.IsVisible = index == 3;
        if (index == 1)
        {
            BuildScaleList();
            LabelRulesPanel.Refresh();
        }
        if (index == 2)
            BarcodePanel.Refresh();
        PageScroll.Offset = new Vector(0, 0);
    }

    // ------------------------------------------------------------------ список весов

    /// <summary>Строки «весы в магазине»: название, марка, адрес, категории, связь, кнопки.</summary>
    public void BuildScaleList()
    {
        ScaleListHost.Children.Clear();
        var canDelete = LabelScaleStore.All.Count > 1;
        foreach (var profile in LabelScaleStore.All)
            ScaleListHost.Children.Add(BuildScaleRow(profile, canDelete));
    }

    private Control BuildScaleRow(LabelScaleProfile profile, bool canDelete)
    {
        var isActive = profile.Id == LabelScaleStore.Active.Id;
        var badge = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = Brush("BrushAccentSoft", Brushes.LightGray),
            BorderBrush = Brush("BrushAccent", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = profile.Brand switch { "rongta" => "R", "tm" => "TM", "ai" => "AI", _ => "Ш" },
                FontWeight = FontWeight.Bold,
                Foreground = Brush("BrushText", Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock
        {
            Text = profile.Name + "  ·  " + ScaleUi.LabelBrandTitle(profile.Brand),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("BrushText", Brushes.Black),
            TextWrapping = TextWrapping.Wrap,
        });
        texts.Children.Add(Line(ProfileAddress(profile), "BrushText"));
        texts.Children.Add(Line(profile.Categories.Count == 0
            ? L("Товары: все весовые (или отмеченные вручную)", "Товарлар: бардык салмактуулар (же кол менен белгиленгендер)", "Goods: all weighed (or ticked by hand)", "Ürünler: tüm tartılılar (veya elle işaretlenenler)", "Tovarlar: barcha vaznlilar (yoki qo‘lda belgilanganlar)")
            : L("Категории: ", "Категориялар: ", "Categories: ", "Kategoriler: ", "Kategoriyalar: ") + string.Join(", ", profile.Categories), "BrushTextSoft"));
        if (profile.Brand != ScaleUi.BrandAi)
        {
            var (level, ipText) = ScaleUi.CheckScaleIp(profile.Ip);
            if (level is ScaleIpLevel.Warning or ScaleIpLevel.Error)
                texts.Children.Add(Line("⚠ " + ipText, "BrushWarning"));
            else if (isActive)
                texts.Children.Add(Line(ScaleUi.LastCheckText(profile.Brand), "BrushTextSoft"));
        }

        var setup = RowButton(L("Настроить…", "Жөндөө…", "Configure…", "Ayarla…", "Sozlash…"), false);
        setup.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;
            await ScaleUi.OpenLabelScaleSetupAsync(owner, profile).ConfigureAwait(true);
            BuildScaleList();
            LabelRulesPanel.Refresh();
        };
        var send = RowButton(profile.Brand == ScaleUi.BrandAi
            ? L("Подготовить файл →", "Файл даярдоо →", "Prepare the file →", "Dosyayı hazırla →", "Faylni tayyorlash →")
            : L("Отправить товары →", "Товарларды жөнөтүү →", "Send goods →", "Ürünleri gönder →", "Tovarlarni yuborish →"), true);
        send.Click += (_, _) =>
        {
            LabelScaleStore.Activate(profile);
            var owner = TopLevel.GetTopLevel(this) as Window;
            var window = App.GetRequiredService<ScalesPluWindow>();
            if (owner is not null)
                window.Show(owner);
            else
                window.Show();
            BuildScaleList();
        };
        var buttons = new WrapPanel { Margin = new Thickness(54, 8, -8, -8) };
        buttons.Children.Add(setup);
        buttons.Children.Add(send);
        if (canDelete)
        {
            var delete = RowButton(L("Удалить", "Өчүрүү", "Delete", "Sil", "O‘chirish"), false);
            delete.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner)
                    return;
                var ok = await PosDialogHost.ShowAsync(new PosConfirmDialog(
                    L("Удалить весы?", "Таразаны өчүрөсүзбү?", "Delete the scale?", "Tartı silinsin mi?", "Tarozi o‘chirilsinmi?"),
                    L($"«{profile.Name}» уберутся из списка кассы. На самих весах ничего не изменится.",
                      $"«{profile.Name}» кассанын тизмесинен алынат. Таразанын өзүндө эч нерсе өзгөрбөйт.",
                      $"“{profile.Name}” will be removed from the till's list. Nothing changes on the scale itself.",
                      $"«{profile.Name}» kasanın listesinden kaldırılır. Tartının kendisinde hiçbir şey değişmez.",
                      $"«{profile.Name}» kassa ro‘yxatidan olib tashlanadi. Tarozining o‘zida hech narsa o‘zgarmaydi."),
                    accent: PosConfirmAccent.Danger), owner).ConfigureAwait(true) == true;
                if (!ok)
                    return;
                LabelScaleStore.Delete(profile.Id);
                BuildScaleList();
            };
            buttons.Children.Add(delete);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        grid.Children.Add(badge);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = Brush("BrushSurfaceSubtle", Brushes.WhiteSmoke),
            BorderBrush = Brush(isActive ? "BrushAccent" : "BrushBorder", Brushes.LightGray),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
    }

    private TextBlock Line(string text, string brushKey) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Brush(brushKey, Brushes.Gray),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button RowButton(string text, bool primary) => new()
    {
        Content = text,
        Classes = { primary ? "btn-primary" : "SettingsFlatButton" },
        Height = 38,
        Padding = new Thickness(14, 0),
        MinWidth = 110,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 8),
    };

    /// <summary>«192.168.0.150:4001 · напрямую по сети» для весов из списка (не только выбранных).</summary>
    private static string ProfileAddress(LabelScaleProfile p)
    {
        if (p.Brand == ScaleUi.BrandAi)
            return L("файл для программы весов", "тараза программасы үчүн файл", "file for the scale's software", "tartı programı için dosya", "tarozi dasturi uchun fayl");
        var address = string.IsNullOrWhiteSpace(p.Ip)
            ? L("адрес не задан", "дарек коюлган эмес", "address not set", "adres girilmemiş", "manzil kiritilmagan")
            : $"{p.Ip}:{p.Port}";
        var route = p.Brand switch
        {
            "rongta" => p.RongtaSource == "server"
                ? L("свой сервер кассы", "кассанын өз сервери", "till's own server", "kasanın kendi sunucusu", "kassaning o‘z serveri")
                : L("через сайт и RLS1000", "сайт жана RLS1000 аркылуу", "via the website and RLS1000", "site ve RLS1000 üzerinden", "sayt va RLS1000 orqali"),
            "tm" => L("напрямую по сети", "тармак аркылуу түз", "directly over the network", "doğrudan ağ üzerinden", "to‘g‘ridan-to‘g‘ri tarmoq orqali"),
            _ => p.ShtrikhDirect
                ? L("напрямую по сети", "тармак аркылуу түз", "directly over the network", "doğrudan ağ üzerinden", "to‘g‘ridan-to‘g‘ri tarmoq orqali")
                : L("через сервер NurCRM", "NurCRM сервери аркылуу", "via the NurCRM server", "NurCRM sunucusu üzerinden", "NurCRM serveri orqali"),
        };
        return address + " · " + route;
    }

    private async void AddScale_Click(object? sender, RoutedEventArgs e)
    {
        var profile = LabelScaleStore.Add();
        BuildScaleList();
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            // Новые весы сразу в окно настроек: адрес и марку всё равно вводить.
            await ScaleUi.OpenLabelScaleSetupAsync(owner, profile).ConfigureAwait(true);
            BuildScaleList();
        }
    }

    /// <summary>Перечитывает весы (после окон, которые их меняют).</summary>
    public void LoadScaleBrand() => BuildScaleList();

    /// <summary>Заполняет раздел этикеточных весов. Вызывается вместе с остальной загрузкой
    /// настроек экрана (PosSettingsWindow).</summary>
    public void LoadLanScaleSettings()
    {
        BuildScaleList();
        LabelRulesPanel.Refresh();
        BarcodePanel.Refresh();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);
}
