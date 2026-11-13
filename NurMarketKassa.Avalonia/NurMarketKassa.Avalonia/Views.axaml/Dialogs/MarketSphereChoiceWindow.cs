using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-06, владелец: «главное не трогай продуктовый, чтобы у клиентов потом проблем не было, а новым клиентам при первом
/// запуске спрашивать сферу маркета». Один раз на новой установке (UserPreferences.MarketSphereChoicePending) после входа:
/// если у компании на сервере вид магазина ещё не задан — «Какой у вас магазин?» (Продукты / Одежда и обувь / Услуги).
/// Выбор сохраняется на сервер (у владельца — для всех касс компании; у кассира сервер отвечает 403 — тогда только в этой
/// кассе). Уже работающие кассы ничего не спрашивают. Закрыли без выбора — спросим при следующем запуске.
/// </summary>
public sealed class MarketSphereChoiceWindow : Window
{
    private static bool _running;

    public string? Chosen { get; private set; }

    /// <summary>2026-11-12 (ХМ14): размер магазина, выбранный на втором шаге при сфере «Хозтовары»; иначе null.</summary>
    public HardwareSize? ChosenHardwareSize { get; private set; }

    public static async Task MaybeAskAsync(Window owner)
    {
        var prefs = UserPreferences.Instance;
        if (_running || !prefs.MarketSphereChoicePending)
            return;
        _running = true;
        try
        {
            // Ждём загрузки компании (вид магазина с сервера) — не дольше 30 с.
            for (var i = 0; i < 60 && CompanyInfoService.LastCompany is null; i++)
                await Task.Delay(500).ConfigureAwait(true);
            if (CompanyInfoService.LastCompany is null)
                return;
            // 2026-11-08: пока ждали компанию, вид могли выбрать в настройках или назначить техподдержкой (MarketSphereLock) — не спрашиваем.
            if (!prefs.MarketSphereChoicePending)
                return;
            if (MarketSphereSync.ServerSphere is { } serverSphere)
            {
                // Компания уже выбрала вид (на другой кассе или на сайте) — его применила синхронизация, не спрашиваем.
                prefs.MarketSphereChoicePending = false;
                prefs.SaveToDisk();
                PosLogger.Log($"Первый запуск: вид магазина уже задан у компании ({serverSphere}) — не спрашиваем.", "SETTINGS");
                return;
            }

            var dialog = new MarketSphereChoiceWindow();
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (dialog.Chosen is not { } sphere)
                return;

            MarketSpheres.Set(sphere);
            // 2026-11-12 (ХМ14): «Хозтовары» — размер магазина и «скан коробки» (новому магазину включён, В-Х4); на сервер уходит только базовая сфера (grocery).
            if (sphere == MarketSpheres.Hardware)
                SphereSettings.SetHardwareChoice(dialog.ChosenHardwareSize ?? HardwareSize.Shop, packScan: true);
            prefs.MarketSphereChoicePending = false;
            prefs.SaveToDisk();
            PosLogger.Log($"Первый запуск: выбран вид магазина {sphere}.", "SETTINGS");
            try
            {
                var serverSphereValue = MarketSpheres.Normalize(sphere);
                await App.GetRequiredService<ClientDebtsApiService>().SetCompanyMarketSphereAsync(serverSphereValue).ConfigureAwait(true);
                MarketSphereSync.NoteSavedOnServer(serverSphereValue);
                PosLogger.Log("Первый запуск: вид магазина сохранён на сервере для всех касс компании.", "SETTINGS");
            }
            catch (Exception ex)
            {
                // Кассир (403) или нет связи — вид остаётся в этой кассе; владелец сменит его в настройках.
                PosLogger.Log($"Первый запуск: вид магазина на сервер не сохранён ({ex.Message}) — только в этой кассе.", "SETTINGS");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Первый запуск: вопрос о виде магазина не показан ({ex.Message}).", "WARNING");
        }
        finally
        {
            _running = false;
        }
    }

    public MarketSphereChoiceWindow()
    {
        // 2026-11-06: В6 (хвост модалок) — окно на общей оболочке ModalShell (заголовок, подвал с «Позже»), плитки — настоящие кнопки (Tab, Enter/Пробел, цель ≥ 72 px),
        // значки — векторные (PathIcon), а не цветные эмодзи, как в остальных окнах кассы.
        Title = Tr.T("Какой у вас магазин?", "Дүкөнүңүз кандай?", "What kind of store do you have?", "Mağazanız ne tür?", "Do'koningiz qanday?");
        Width = 672;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new StackPanel { Spacing = 12 };
        // 2026-11-12 (ХМ14): плитки строятся из каталога сфер (три базовые + профили с ChoiceVisible — «Хозтовары»); тексты базовых те же, что были.
        foreach (var profile in SphereCatalog.ChoiceList())
        {
            var id = profile.Id;
            root.Children.Add(Tile(IconKeyFor(profile), profile.Title.Get(), profile.Description.Get(),
                () => { if (id == MarketSpheres.Hardware) ShowHardwareStep(); else { Chosen = id; Close(); } }));
        }
        _chooseStep = root;

        var later = new Button { Content = Tr.T("Позже", "Кийинчерээк", "Later", "Sonra", "Keyinroq"), IsCancel = true, MinWidth = 120 };
        later.Classes.Add("modal-cancel");
        later.Click += (_, _) => Close();
        var shell = new ModalShell
        {
            Margin = new Thickness(16),
            Footer = later,
            Content = root,
        };
        _shell = shell;
        shell[!ModalShell.TitleProperty] = new Avalonia.Data.Binding { Source = this, Path = nameof(Title) };
        // 2026-11-08, владелец: после выбора вид магазина меняет только техподдержка (MarketSphereLock) — было «в любой момент».
        shell.Subtitle = Tr.T("Касса настроится под ваш вид торговли. Выбор делается один раз: потом вид магазина меняется только через техподдержку.",
            "Касса соода түрүңүзгө ылайыкташат. Тандоо бир жолу жасалат: андан кийин дүкөндүн түрү техколдоо аркылуу гана өзгөрөт.",
            "The till will adapt to your kind of trade. You choose once: after that the store type can only be changed through support.",
            "Kasa ticaret türünüze göre ayarlanır. Seçim bir kez yapılır: sonra mağaza türü yalnızca teknik destek aracılığıyla değişir.",
            "Kassa savdo turingizga moslashadi. Tanlov bir marta qilinadi: keyin do'kon turi faqat texnik yordam orqali o'zgaradi.");
        _firstSubtitle = shell.Subtitle;
        Content = shell;
    }

    private ModalShell _shell = null!;
    private StackPanel _chooseStep = null!;
    private string? _firstSubtitle;

    /// <summary>Значок плитки (векторный, из ресурсов): у базовых — прежние, у остальных — по ключу значка профиля.</summary>
    private static string IconKeyFor(SphereProfile profile) => profile.Id switch
    {
        MarketSpheres.Grocery => "IconCart",
        MarketSpheres.Clothing => "IconTag",
        MarketSpheres.Services => "IconSettings",
        _ => profile.IconKey == "pencil" ? "IconPencil" : profile.IconKey == "phone" ? "IconPhone" : "IconPackage", // 2026-11-12 (ЭЛ1): значок «Электроники»
    };

    /// <summary>2026-11-12 (ХМ14): второй шаг «Хозтоваров» — размер магазина (хозмаг у дома / строительный маркет) и «что изменится».</summary>
    private void ShowHardwareStep()
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var size in new[] { HardwareSize.Shop, HardwareSize.Market })
        {
            var s = size;
            panel.Children.Add(Tile("IconPackage", HardwareChoiceTexts.SizeName(s), HardwareChoiceTexts.SizeDescription(s),
                () => { Chosen = MarketSpheres.Hardware; ChosenHardwareSize = s; Close(); }));
        }
        var changes = new TextBlock { Text = HardwareChoiceTexts.WhatChangesText(), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(changes, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(changes);
        var back = new Button { Content = HardwareChoiceTexts.BackText, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
        back.Classes.Add("modal-cancel");
        back.Click += (_, _) =>
        {
            _shell.Content = _chooseStep;
            _shell.Subtitle = _firstSubtitle;
        };
        panel.Children.Add(back);
        _shell.Subtitle = HardwareChoiceTexts.SizeStepSubtitle;
        _shell.Content = panel;
    }

    /// <summary>Плитка вида магазина — кнопка на всю ширину: значок слева, название и описание справа.</summary>
    private Button Tile(string iconKey, string name, string description, Action onClick)
    {
        var tile = new Button
        {
            MinHeight = 72, Padding = new Thickness(16, 12), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
        };
        Use(tile, BackgroundProperty, "BrushPanel");
        Use(tile, BorderBrushProperty, "BrushBorder");
        Use(tile, ForegroundProperty, "BrushText");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = new PathIcon { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        if (this.TryFindResource(iconKey, ActualThemeVariant, out var geometry) && geometry is Geometry geo)
            icon.Data = geo;
        Use(icon, PathIcon.ForegroundProperty, "BrushTextSoft");
        g.Children.Add(icon);
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var n = new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.Bold };
        Use(n, TextBlock.ForegroundProperty, "BrushText");
        var d = new TextBlock { Text = description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(d, TextBlock.ForegroundProperty, "BrushTextSoft");
        texts.Children.Add(n);
        texts.Children.Add(d);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        tile.Content = g;
        Avalonia.Automation.AutomationProperties.SetName(tile, name);
        tile.PointerEntered += (_, _) => Use(tile, BorderBrushProperty, "BrushAccentStrong");
        tile.PointerExited += (_, _) => Use(tile, BorderBrushProperty, "BrushBorder");
        tile.Click += (_, _) => onClick();
        return tile;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
