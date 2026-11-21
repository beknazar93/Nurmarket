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

    /// <summary>2026-11-20: режим «Базара» (розница / опт), выбранный на втором шаге; иначе null.</summary>
    public BazaarMode? ChosenBazaarMode { get; private set; }

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

            await ShowAndApplyAsync(owner).ConfigureAwait(true);
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

    /// <summary>2026-11-13 (Н-1, мастер первого запуска): показать «Какой у вас магазин?» и применить выбор — тот же путь, что при первом запуске (MaybeAskAsync):
    /// вид в этой кассе, «Хозтовары» — размер магазина, сохранение на сервер для всех касс (кассир / нет связи — только в этой кассе).
    /// Возвращает выбранный вид или null (закрыли без выбора).</summary>
    public static async Task<string?> ShowAndApplyAsync(Window owner)
    {
        var prefs = UserPreferences.Instance;
        var dialog = new MarketSphereChoiceWindow();
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        if (dialog.Chosen is not { } sphere)
            return null;

        MarketSpheres.Set(sphere);
        // 2026-11-12 (ХМ14): «Хозтовары» — размер магазина и «скан коробки» (новому магазину включён, В-Х4); на сервер уходит только базовая сфера (grocery).
        // 2026-11-20: Хозмаг и Строймаг — отдельные плитки: размер (Хозмаг = у дома, Строймаг = маркет) пишется сразу, второго шага нет.
        if (sphere == HardwareProfile.HozmagId || sphere == HardwareProfile.StroymagId)
            SphereSettings.SetHardwareChoice(sphere == HardwareProfile.StroymagId ? HardwareSize.Market : HardwareSize.Shop, packScan: true);
        else if (sphere == BazaarProfile.Id)
            SphereSettings.SetBazaarMode(dialog.ChosenBazaarMode ?? BazaarMode.Retail); // 2026-11-20: «Базар» — режим розница / опт
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
        return sphere;
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
        // 2026-11-20: порядок — главные шесть, затем подпись «Другие» и остальные (SphereCatalog.MainIds / OtherIds); при малой высоте экрана плитки прокручиваются (ModalShell.ContentScroll).
        var otherShown = false;
        foreach (var profile in SphereCatalog.ChoiceList())
        {
            var id = profile.Id;
            if (!otherShown && !SphereCatalog.MainIds.Contains(id))
            {
                otherShown = true;
                var other = new TextBlock { Text = Tr.T("Другие", "Башкалар", "Other", "Diğer", "Boshqalar"), FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6, 0, 0) };
                Use(other, TextBlock.ForegroundProperty, "BrushTextSoft");
                root.Children.Add(other);
            }
            root.Children.Add(Tile(IconKeyFor(profile), profile.Title.Get(), profile.Description.Get(),
                () => { if (id == BazaarProfile.Id) ShowBazaarStep(); else { Chosen = id; Close(); } }));
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
        BazaarProfile.Id => "IconStore", // 2026-11-20: «Базар»
        HardwareProfile.StroymagId => "IconWarehouse", // 2026-11-20: у Хозмага и Строймага был один и тот же значок «коробка» — Строймагу даём «склад»
        _ => profile.IconKey == "pencil" ? "IconPencil" : profile.IconKey == "phone" ? "IconPhone" : profile.IconKey == "car" ? "IconCar" : "IconPackage", // 2026-11-12 (ЭЛ1): значок «Электроники»; 2026-11-20 (АЗ1): «Автозапчасти»
    };

    /// <summary>2026-11-20: второй шаг «Базара» — розница или опт (режим внутри сферы; потом меняется в Настройках → Операции). Бывший шаг «размер магазина» у Хозмага / Строймага
    /// не нужен: это две отдельные плитки.</summary>
    private void ShowBazaarStep()
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var mode in new[] { BazaarMode.Retail, BazaarMode.Wholesale })
        {
            var m = mode;
            panel.Children.Add(Tile("IconStore", BazaarProfile.ModeName(m), BazaarProfile.ModeHint(m),
                () => { Chosen = BazaarProfile.Id; ChosenBazaarMode = m; Close(); }));
        }
        var back = new Button { Content = HardwareChoiceTexts.BackText, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
        back.Classes.Add("modal-cancel");
        back.Click += (_, _) =>
        {
            _shell.Content = _chooseStep;
            _shell.Subtitle = _firstSubtitle;
        };
        panel.Children.Add(back);
        _shell.Subtitle = Tr.T("Режим можно изменить потом в Настройках → Операции.", "Режимди кийин Жөндөөлөр → Операциялар бөлүмүндө өзгөртсө болот.",
            "You can change the mode later in Settings → Operations.", "Modu daha sonra Ayarlar → İşlemler bölümünden değiştirebilirsiniz.", "Rejimni keyin Sozlamalar → Operatsiyalar bo'limida o'zgartirish mumkin.");
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
