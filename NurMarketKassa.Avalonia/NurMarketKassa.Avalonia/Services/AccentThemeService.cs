using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Дополнительные цветовые темы поверх обычного светлого/тёмного режима — меняют не только
/// акцентный цвет во всей программе (кнопки, вкладки, цены, фокус, плитки каталога), но и форму
/// карточек/кнопок/модальных окон, и лёгкий фоновый градиент окон (BrushWindow), одним выбором
/// в Маркетплейсе. "gold" — родной вид темы (жёлтый в светлом режиме, синий в тёмном, как было
/// изначально, прямые углы по умолчанию) и ничего не переопределяет; остальные темы задают единый
/// акцент, форму и фон для обоих режимов. Реализовано через прямую запись в Application.Resources —
/// это единственный слой, который гарантированно перекрывает и ThemeDictionaries (Light/Dark), и уже
/// загруженные MergedDictionaries, поэтому весь существующий UI (везде использующий DynamicResource
/// для этих ключей, включая плитки каталога) перекрашивается сразу, без правки каждого окна.
/// </summary>
public static class AccentThemeService
{
    public sealed record ThemeOption(string Id, string LabelRu, string LabelKy, string Icon, string DescRu, string DescKy);

    public static readonly ThemeOption[] AvailableThemes =
    [
        new("gold", "Золотая (по умолчанию)", "Алтын (алгачкы)", "👑",
            "Родной вид кассы: жёлтый акцент в светлом режиме, синий — в тёмном. Прямые углы кнопок и карточек, без изменений фона.",
            "Кассанын жердик көрүнүшү: жарык режимде сары акцент, караңгы режимде — көк. Баскыч/карточкалардын бурчтары түз, фон өзгөрбөйт."),
        new("blue", "Синяя", "Көк", "💧",
            "Синий акцент. Более строгие, прямые углы кнопок и карточек. Лёгкий голубой оттенок фона окон и плиток каталога.",
            "Көк акцент. Баскыч/карточкалардын бурчтары түз жана катаал. Терезелердин жана каталог плиткаларынын фонунда жеңил көк өң."),
        new("green", "Зелёная", "Жашыл", "🌿",
            "Зелёный акцент. Стандартная скруглённость элементов — как золотая тема, но в зелёных тонах фона и плиток каталога.",
            "Жашыл акцент. Элементтердин тегеректиги стандарттуу — алтын темадай эле, бирок фон жана каталог плиткалары жашыл өңдө."),
        new("purple", "Фиолетовая", "Кызгылт көк", "🔮",
            "Фиолетовый акцент. Заметно скруглённые кнопки и карточки, мягкий сиреневый оттенок фона и плиток каталога.",
            "Кызгылт көк акцент. Баскыч/карточкалар байкаларлык тегерек, фондо жана каталог плиткаларында жумшак сирень өңү."),
        new("orange", "Оранжевая", "Кызгылт сары", "🔥",
            "Оранжевый акцент. Максимально скруглённые кнопки и карточки — самый мягкий стиль, тёплый оттенок фона и плиток каталога.",
            "Кызгылт сары акцент. Баскыч/карточкалар эң тегерек — эң жумшак стиль, фон жана каталог плиткалары жылуу өңдө."),
        new("glass", "Жидкое стекло", "Суюк айнек", "🧊",
            "Стиль Apple Liquid Glass: полупрозрачные, матовые карточки, плитки каталога и модальные окна (с настоящим размытием фона) поверх мягкого сине-фиолетового фона. Самые скруглённые углы.",
            "Apple Liquid Glass стили: айнектей жарым-тунук, туманданган карточкалар, каталог плиткалары жана модалдык терезелер (фонду чын мурунтан бүдөмүктөтүү менен) жумшак көк-кызгылт фондун үстүндө. Эң тегерек бурчтар."),
        new("navy", "Профессиональная", "Кесипкөй", "💼",
            "Стиль ui-ux-pro-max \"Minimalism & Swiss Style\" (2026-09-15, по просьбе пользователя): тёмно-синий (нави) акцент кнопок и цен, холодный сине-серый фон окон вместо тёплого. Строгие, почти прямые углы — деловой, а не игривый вид.",
            "ui-ux-pro-max \"Minimalism & Swiss Style\" стили (2026-09-15, колдонуучунун суроосу боюнча): баскычтардын жана баалардын күлгүн-көк акценти, терезелердин муздак боз фону. Бурчтар түз жакын — оюн эмес, иштиктүү көрүнүш."),
        new("custom", "Свой цвет", "Өз түсү", "🎨",
            "Задайте акцентный цвет вручную по HEX-коду (например #FF6B00) — остальные оттенки (мягкий фон, наведение, плитки) подбираются автоматически.",
            "Акцент түсүн HEX-код менен өзүңүз коюңуз (мисалы #FF6B00) — калган өңдөр (жумшак фон, курсор үстүндө, плиткалар) автоматтык түрдө тандалат."),
    ];

    private sealed record AccentColors(
        string Accent, string Strong, string Soft, string Foreground,
        string WindowFrom, string WindowTo, string WindowAlt,
        string TileBg, string TileBorder, string TabBg, string TabBgHover,
        string? Panel = null, string? PanelBorder = null);
    private sealed record AccentTheme(AccentColors Light, AccentColors Dark, double ButtonRadius, double CardRadius);

    // 2026-09-22. Светлые палитры цветных тем переведены на НЕЙТРАЛЬНЫЕ поверхности: фон окон
    // и плитки каталога были залиты насыщенным оттенком акцента (#DCE8FE у синей, #DFF5E5 у
    // зелёной и т.д.), и экран получался цветным целиком. Так оформляют детские приложения; в
    // кассовых программах цветом отмечают ДЕЙСТВИЕ — кнопку, активную вкладку, выделение, — а
    // поверхности держат белыми и серыми, чтобы товар и цена читались первыми. Акцентные цвета
    // (Accent/Strong/Soft) не тронуты: кнопки остались синими, зелёными и т.д., и темы
    // по-прежнему отличаются друг от друга — но оттенком, а не заливкой.
    //
    // ПОПРАВКА того же дня: с первого захода я увёл фоны почти в один и тот же серый, и
    // переключение между синей, зелёной, фиолетовой и оранжевой перестало быть заметным —
    // владелец это сразу увидел. Оттенок вернул: он ясно читается, но остаётся спокойным,
    // а сами плитки держатся белыми, чтобы товар и цена читались первыми.
    // Тёмные палитры не тронуты: там насыщенности и так нет, а плоский серый читается хуже.
    private static readonly Dictionary<string, AccentTheme> Themes = new()
    {
        ["blue"] = new AccentTheme(
            Light: new AccentColors("#2563EB", "#1D4ED8", "#DBEAFE", "#FFFFFF", "#E7EDF7", "#F6F9FD", "#EDF2FA",
                TileBg: "#FFFFFF", TileBorder: "#D5DFED", TabBg: "#E2EAF6", TabBgHover: "#D3DFF1"),
            Dark: new AccentColors("#3B82F6", "#2563EB", "#1E3A5F", "#FFFFFF", "#16233B", "#1E1E1E", "#1B2C4A",
                TileBg: "#20304F", TileBorder: "#2E4470", TabBg: "#1B2C4A", TabBgHover: "#243B63"),
            ButtonRadius: 8, CardRadius: 10),
        ["green"] = new AccentTheme(
            Light: new AccentColors("#16A34A", "#15803D", "#DCFCE7", "#FFFFFF", "#E7F1EA", "#F6FBF8", "#EDF6F0",
                TileBg: "#FFFFFF", TileBorder: "#D2E3D8", TabBg: "#E1EFE6", TabBgHover: "#D0E7D9"),
            Dark: new AccentColors("#22C55E", "#16A34A", "#14532D", "#FFFFFF", "#142A1D", "#1E1E1E", "#173423",
                TileBg: "#1B3325", TileBorder: "#2A4A36", TabBg: "#173423", TabBgHover: "#1F4530"),
            ButtonRadius: 10, CardRadius: 12),
        // 2026-09-15, по просьбе пользователя ("сделай дизайн профессиональным используя
        // скилл") — палитра из ui-ux-pro-max, стиль "Minimalism & Swiss Style". Изначально
        // акцентом был зелёный #059669 (роль "Accent/CTA" в системе скила), но пользователю
        // не понравилось — заменено на #1E3A5F (роль "Primary" у скила), чистый тёмно-синий,
        // без зелёного вообще. Foreground = белый (тёмный навигационный фон, проверено:
        // белый текст на #1E3A5F даёт ~11.5:1 контраста).
        ["navy"] = new AccentTheme(
            Light: new AccentColors("#1E3A5F", "#16304D", "#DCE3EC", "#FFFFFF", "#EAF0F5", "#F7FAFC", "#F1F5F9",
                TileBg: "#F5F7FA", TileBorder: "#C3D0DE", TabBg: "#DCE3EC", TabBgHover: "#CAD5E2"),
            Dark: new AccentColors("#3B82F6", "#2563EB", "#1E3A5F", "#FFFFFF", "#16233B", "#1E1E1E", "#1B2C4A",
                TileBg: "#20304F", TileBorder: "#2E4470", TabBg: "#1B2C4A", TabBgHover: "#243B63"),
            ButtonRadius: 8, CardRadius: 10),
        ["purple"] = new AccentTheme(
            Light: new AccentColors("#7C3AED", "#6D28D9", "#EDE9FE", "#FFFFFF", "#EFEAF8", "#F9F7FD", "#F3EFFB",
                TileBg: "#FFFFFF", TileBorder: "#DCD3EE", TabBg: "#E9E2F7", TabBgHover: "#DED4F2"),
            Dark: new AccentColors("#8B5CF6", "#7C3AED", "#3B2A6B", "#FFFFFF", "#241A3D", "#1E1E1E", "#2C2050",
                TileBg: "#2A2049", TileBorder: "#3D2E68", TabBg: "#2C2050", TabBgHover: "#392A63"),
            ButtonRadius: 14, CardRadius: 16),
        ["orange"] = new AccentTheme(
            Light: new AccentColors("#EA580C", "#C2410C", "#FFEDD5", "#FFFFFF", "#F8EFE5", "#FDF9F5", "#FBF3EB",
                TileBg: "#FFFFFF", TileBorder: "#EFDDC9", TabBg: "#F6EADC", TabBgHover: "#F1DFCB"),
            Dark: new AccentColors("#FB923C", "#EA580C", "#4A2A0F", "#FFFFFF", "#3A2413", "#1E1E1E", "#472B14",
                TileBg: "#3B2717", TileBorder: "#573A22", TabBg: "#472B14", TabBgHover: "#573622"),
            ButtonRadius: 18, CardRadius: 18),
        ["glass"] = new AccentTheme(
            // Panel/PanelBorder — общая поверхность карточек ВЕЗДЕ в приложении (бургер-меню,
            // Настройки, KPI-карточки Финансов/Склада и т.д.), где под текстом должен быть
            // разборчивый фон, поэтому только лёгкий стеклянный оттенок (~90%), не сильная
            // прозрачность. Карточка/фон модальных окон — отдельная, заметно более прозрачная
            // глянцевая текстура (см. GlassCardBrush ниже, используется и для BrushDialogPanel, и
            // для DialogWindowBackground) — имитация глянцевой "стеклянной таблички" с мягким
            // бликом по диагонали, без живого снимка экрана (тот подход оказался хрупким — снимок
            // то сжимался под неверный размер, то размытие "вытекало" за скруглённые углы карточки).
            Light: new AccentColors("#0A84FF", "#0060DF", "#D6EAFF", "#FFFFFF", "#EAF2FF", "#F3ECFF", "#E4EEFF",
                TileBg: "#B3FFFFFF", TileBorder: "#CCFFFFFF", TabBg: "#80FFFFFF", TabBgHover: "#B3FFFFFF",
                Panel: "#E6FFFFFF", PanelBorder: "#D9FFFFFF"),
            Dark: new AccentColors("#409CFF", "#0A84FF", "#123A66", "#FFFFFF", "#0F1B33", "#1B1330", "#16223D",
                TileBg: "#992A3A5C", TileBorder: "#40FFFFFF", TabBg: "#661E2A40", TabBgHover: "#992A3A5C",
                Panel: "#E0192339", PanelBorder: "#40FFFFFF"),
            ButtonRadius: 24, CardRadius: 32),
    };

    private static readonly string[] OverriddenKeys =
    [
        "BrushAccent", "BrushPrimary", "BrushAccentStrong", "BrushAccentSoft", "BrushAccentForeground",
        "BrushSuccessSoft", "BrushCatalogPrice",
        "BrushCatalogTabBgSelected", "BrushCatalogTabBorderSelected", "BrushFocus",
        "BrushCatalogTileBg", "BrushCatalogTileBorder", "BrushCatalogTabBg", "BrushCatalogTabBgHover",
        "BrushWindow", "BrushWindowAlt", "BrushPanel", "BrushDialogPanel", "BrushBorder",
        "SettingsButtonRadius", "SettingsCardRadius",
    ];

    /// <summary>Применяет выбранную тему поверх текущего светлого/тёмного режима.
    /// "gold" (или неизвестный id) снимает переопределения — возвращает родные цвета и форму темы.</summary>
    public static void Apply(string? themeId, bool dark)
    {
        if (Application.Current is not { } app)
            return;

        // Модальные окна ссылаются на эти два ключа напрямую (TransparencyLevelHint="{DynamicResource
        // DialogTransparencyHint}", Background="{DynamicResource DialogWindowBackground}"), поэтому
        // они должны существовать ВСЕГДА, ещё до создания первого диалога — иначе окно останется
        // полностью непрозрачным (родное поведение без хинта).
        // ВАЖНО: само окно (Window) — всегда прямоугольник на уровне ОС, скруглить его силуэт
        // нельзя. Раньше DialogWindowBackground тоже красился в видимую стеклянную текстуру —
        // из-за этого в уголках, где скруглённая карточка (Border с CornerRadius) отступает
        // внутрь от прямого угла окна, торчал явно прямоугольный кусочек фона окна — "не
        // скруглённые углы". Поэтому фон САМОГО окна всегда остаётся полностью прозрачным
        // (невидимым) — скруглённый стеклянный вид даёт только внутренняя карточка (см.
        // BrushDialogPanel в Apply() ниже), у которой ClipToBounds/CornerRadius по-настоящему
        // обрезают текстуру по скруглённой форме.
        var wantsGlass = string.Equals(themeId, "glass", StringComparison.OrdinalIgnoreCase);
        // п.1.3: вкл/выкл самого эффекта "Жидкое стекло" отдельно от выбора темы — выключено
        // держит форму/скруглённость темы "glass", но без глянцевой полупрозрачной текстуры
        // и без запроса ОС-blur (см. ветку ниже и MainWindow.RefreshBackgroundWallpaper).
        var liquidGlassEnabled = NurMarketKassa.Services.UserPreferences.Instance.LiquidGlassEnabled;
        var wantsLiquidGlass = wantsGlass && liquidGlassEnabled;
        var glassOpacityPercent = NurMarketKassa.Services.UserPreferences.Instance.GlassOpacityPercent;
        app.Resources["DialogTransparencyHint"] = wantsLiquidGlass
            ? new List<WindowTransparencyLevel> { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent }
            : new List<WindowTransparencyLevel> { WindowTransparencyLevel.Transparent };
        app.Resources["DialogWindowBackground"] = Brushes.Transparent;
        ApplyFontResources(app);

        AccentTheme? theme = null;
        if (string.Equals(themeId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var customHex = NurMarketKassa.Services.UserPreferences.Instance.CustomAccentHex;
            if (TryNormalizeHex(customHex, out var normalizedHex))
                theme = BuildCustomTheme(normalizedHex);
        }
        else if (!string.IsNullOrWhiteSpace(themeId))
        {
            Themes.TryGetValue(themeId, out var presetTheme);
            theme = presetTheme;
        }

        if (theme is null)
        {
            // "gold"/неизвестная тема/не заданный кастомный цвет — снимаем переопределения
            // цвета, но скруглённость (SettingsButtonRadius/CardRadius) всё равно применяем
            // ниже: это независимая настройка (см. п.1.1), а не часть цветовой темы.
            foreach (var key in OverriddenKeys)
                app.Resources.Remove(key);
            ApplyRadiusResources(app, themeButtonRadius: 0, themeCardRadius: 0);
            return;
        }

        var colors = dark ? theme.Dark : theme.Light;
        app.Resources["BrushAccent"] = Brush(colors.Accent);
        app.Resources["BrushPrimary"] = Brush(colors.Accent);
        app.Resources["BrushAccentStrong"] = Brush(colors.Strong);
        app.Resources["BrushAccentSoft"] = Brush(colors.Soft);
        app.Resources["BrushAccentForeground"] = Brush(colors.Foreground);
        // BrushSuccess акцентом НЕ переопределяется. Раньше переопределялся — и «успех»
        // красился произвольным цветом темы: на серых/приглушённых акцентах текст вроде
        // «Добавлено сотрудников: 2» становился нечитаемым (замер на скриншоте владельца:
        // #1C79C9 на #6D6D6D = 1.14:1 при норме 4.5:1). Этот ключ везде используется только
        // как цвет ТЕКСТА, а акцент подбирается как цвет ЗАЛИВКИ кнопок — разные задачи,
        // разные требования к контрасту. Зелёный из темы остаётся зелёным при любом акценте.
        app.Resources["BrushSuccessSoft"] = Brush(colors.Soft);
        // Цена на плитке — САМОЕ читаемое место каталога, поэтому она тёмная, а не акцентная.
        // Раньше здесь стоял Brush(colors.Accent): цены светились цветом кнопок, спорили с
        // ними за внимание и хуже читались на белой плитке (синий #2563EB на белом — 5.2:1
        // против 16:1 у тёмного). Акцент остаётся за тем, что можно нажать.
        // ОБЯЗАТЕЛЬНО зависит от режима: одним значением на обе темы нельзя. Тёмная цена
        // на тёмной плитке пропадает — ровно это я и допустил, поставив сюда #0F172A для
        // светлой и тёмной сразу.
        app.Resources["BrushCatalogPrice"] = Brush(dark ? "#F8FAFC" : "#0F172A");
        app.Resources["BrushCatalogTabBgSelected"] = Brush(colors.Accent);
        app.Resources["BrushCatalogTabBorderSelected"] = Brush(colors.Strong);
        app.Resources["BrushCatalogTileBg"] = Brush(colors.TileBg);
        app.Resources["BrushCatalogTileBorder"] = Brush(colors.TileBorder);
        app.Resources["BrushCatalogTabBg"] = Brush(colors.TabBg);
        app.Resources["BrushCatalogTabBgHover"] = Brush(colors.TabBgHover);
        app.Resources["BrushFocus"] = Brush(colors.Accent);
        // ОДИН фон на всё полотно кассы. До 2026-09-22 здесь было пять разных тонов друг на
        // друге: шапка, поле вокруг каталога (градиент), панель каталога, панель чека и низ
        // экрана — каждый своего оттенка. Глаз читал это как набор заплаток, и именно это
        // владелец назвал «очень некрасиво».
        //
        // Теперь уровней два, как и положено: ПОЛОТНО (шапка, каталог, чек, низ — всё одним
        // тоном) и ПРИПОДНЯТОЕ на нём (плитки товаров, карточки, диалоги). Тогда взгляд
        // отделяет товар от фона, а не считает границы между кусками фона.
        //
        // Градиента тоже больше нет: на полный экран он давал заметную полосу посередине,
        // а на плоском фоне плитки читаются ровнее.
        var canvas = Brush(colors.WindowFrom);
        app.Resources["BrushWindow"] = canvas;
        app.Resources["BrushWindowAlt"] = Brush(colors.WindowFrom);
        app.Resources["BrushPanel"] = Brush(colors.Panel ?? colors.WindowFrom);
        if (colors.PanelBorder is { } panelBorderHex)
            app.Resources["BrushBorder"] = Brush(panelBorderHex);
        else
            app.Resources.Remove("BrushBorder");
        if (wantsLiquidGlass)
            app.Resources["BrushDialogPanel"] = GlassCardBrush(dark, glassOpacityPercent);
        else if (wantsGlass)
            app.Resources["BrushDialogPanel"] = MatteGlassBrush(dark);
        else
            app.Resources.Remove("BrushDialogPanel");
        ApplyRadiusResources(app, theme.ButtonRadius, theme.CardRadius);
    }

    /// <summary>Шрифт приложения (семейство + базовый размер) — независим от цветовой темы,
    /// как и скруглённость (см. ApplyRadiusResources); настраивается в модалке "⚙ Настройка
    /// темы". null в UserPreferences означает "как всегда было" (Noto Sans, 14px — см. App.axaml
    /// AppFontFamily). Noto Sans (встроенный ресурс приложения, Assets/Fonts, 2026-09-04)
    /// добавлен ПОСЛЕДНИМ в fallback-цепочку даже при выбранном пользователем кастомном шрифте
    /// (тема из Маркетплейса) — гарантирует, что кыргызские буквы ө/ү/ң не превратятся в
    /// квадраты, каким бы ни был выбранный шрифт, не меняя вид остальных символов, которые в
    /// этом шрифте и так есть.</summary>
    private const string KyrgyzSafeFontFallback = "avares://NurMarketKassa.Avalonia/Assets/Fonts#Noto Sans";

    private static void ApplyFontResources(Application app)
    {
        var prefs = NurMarketKassa.Services.UserPreferences.Instance;
        var family = string.IsNullOrWhiteSpace(prefs.CustomFontFamily)
            ? $"{KyrgyzSafeFontFallback}, Segoe UI"
            : $"{prefs.CustomFontFamily}, {KyrgyzSafeFontFallback}";
        app.Resources["AppFontFamily"] = new FontFamily(family);
        app.Resources["AppFontSize"] = prefs.CustomFontSize ?? 14.0;

        // Цвет текста — та же логика оверрайда, что у остальных "общих для всех тем" настроек
        // (радиус/шрифт): один и тот же выбранный цвет действует и в светлом, и в тёмном режиме,
        // как и остальные Custom*-оверрайды в этом классе.
        if (TryNormalizeHex(prefs.CustomTextColor, out var textColorHex))
            app.Resources["BrushText"] = Brush(textColorHex);
        else
            app.Resources.Remove("BrushText");
    }

    /// <summary>Скруглённость кнопок/карточек — независимая от цвета настройка (п.1.1):
    /// ползунки в Маркетплейс → Темы оверрайдят значение активной темы (или 0 у "золотой"),
    /// null в UserPreferences означает "как в теме".</summary>
    private static void ApplyRadiusResources(Application app, double themeButtonRadius, double themeCardRadius)
    {
        var prefs = NurMarketKassa.Services.UserPreferences.Instance;
        var buttonRadius = prefs.CustomButtonRadius ?? themeButtonRadius;
        var cardRadius = prefs.CustomCardRadius ?? themeCardRadius;
        app.Resources["SettingsButtonRadius"] = new CornerRadius(buttonRadius);
        app.Resources["SettingsCardRadius"] = new CornerRadius(cardRadius);
    }

    /// <summary>Проверяет и нормализует пользовательский HEX ("#RGB"/"#RRGGBB"/без решётки) в
    /// "#RRGGBB", которое Avalonia's Color.Parse гарантированно понимает.</summary>
    private static bool TryNormalizeHex(string? hex, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var trimmed = hex.Trim();
        if (!trimmed.StartsWith('#'))
            trimmed = "#" + trimmed;

        try
        {
            var color = Color.Parse(trimmed);
            normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Строит полный набор оттенков (светлый/тёмный режим) из ОДНОГО акцентного
    /// цвета — смешиванием с белым/чёрным, а не готовой палитрой как у пресетов. Радиус по
    /// умолчанию как у "зелёной" темы (умеренная скруглённость); реальное значение всё равно
    /// перекрывается CustomButtonRadius/CustomCardRadius в большинстве случаев (см. п.1.1).</summary>
    private static AccentTheme BuildCustomTheme(string accentHex)
    {
        var strong = Darken(accentHex, 0.18);
        var soft = Lighten(accentHex, 0.85);
        var foreground = IsLight(accentHex) ? "#000000" : "#FFFFFF";
        var light = new AccentColors(
            accentHex, strong, soft, foreground,
            WindowFrom: Lighten(accentHex, 0.94), WindowTo: Lighten(accentHex, 0.97), WindowAlt: Lighten(accentHex, 0.9),
            TileBg: Lighten(accentHex, 0.95), TileBorder: Lighten(accentHex, 0.65),
            TabBg: Lighten(accentHex, 0.88), TabBgHover: Lighten(accentHex, 0.78));

        var darkAccent = Lighten(accentHex, 0.15);
        var darkForeground = IsLight(darkAccent) ? "#000000" : "#FFFFFF";
        var dark = new AccentColors(
            darkAccent, accentHex, Darken(accentHex, 0.65), darkForeground,
            WindowFrom: Darken(accentHex, 0.88), WindowTo: "#1E1E1E", WindowAlt: Darken(accentHex, 0.85),
            TileBg: Darken(accentHex, 0.82), TileBorder: Darken(accentHex, 0.55),
            TabBg: Darken(accentHex, 0.85), TabBgHover: Darken(accentHex, 0.72));

        return new AccentTheme(light, dark, ButtonRadius: 10, CardRadius: 12);
    }

    private static string Lighten(string hex, double amount)
    {
        var c = Color.Parse(hex);
        byte Blend(byte channel) => (byte)Math.Clamp(Math.Round(channel + (255 - channel) * amount), 0, 255);
        return $"#{Blend(c.R):X2}{Blend(c.G):X2}{Blend(c.B):X2}";
    }

    private static string Darken(string hex, double amount)
    {
        var c = Color.Parse(hex);
        byte Blend(byte channel) => (byte)Math.Clamp(Math.Round(channel * (1 - amount)), 0, 255);
        return $"#{Blend(c.R):X2}{Blend(c.G):X2}{Blend(c.B):X2}";
    }

    private static bool IsLight(string hex)
    {
        var c = Color.Parse(hex);
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.6;
    }

    /// <summary>Акцентный hex темы (светлый вариант) — используется там, где нужен именно цвет,
    /// а не набор ресурсов (например, чтобы подхватить акцент в настройках экрана покупателя).</summary>
    public static string? GetAccentHex(string themeId)
    {
        if (Themes.TryGetValue(themeId, out var theme))
            return theme.Light.Accent;
        if (string.Equals(themeId, "custom", StringComparison.OrdinalIgnoreCase))
            return TryNormalizeHex(NurMarketKassa.Services.UserPreferences.Instance.CustomAccentHex, out var hex) ? hex : "#F7D617";
        if (string.Equals(themeId, "gold", StringComparison.OrdinalIgnoreCase))
            return "#F7D617";
        return null;
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static LinearGradientBrush WindowGradient(string fromHex, string toHex) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(fromHex), 0),
            new GradientStop(Color.Parse(toHex), 1),
        },
    };

    /// <summary>Базовое значение GlassOpacityPercent, под которое подобраны исходные alpha
    /// у стопов ниже (80 = "80% прозрачности") — при этом значении ползунок в Маркетплейсе
    /// ничего не меняет, GetOpacityScale возвращает 1.</summary>
    private const double GlassOpacityBaseline = 80;

    /// <summary>Глянцевая "стеклянная табличка" — диагональный градиент с ярким бликом посередине
    /// и более тусклыми углами, как у полупрозрачной керамической/акриловой пластины. Прозрачность
    /// настраивается ползунком (Маркетплейс → Темы → "Жидкое стекло", UserPreferences.
    /// GlassOpacityPercent, 0–100: 100 — почти полностью прозрачно, 0 — почти непрозрачно) —
    /// исходные alpha у стопов ниже подобраны под 80% и линейно масштабируются от этой базы.
    /// Используется и для карточки диалога (BrushDialogPanel), и для фона самого окна
    /// (DialogWindowBackground) — одна и та же текстура растягивается под размер каждой конкретной
    /// модалки самим Avalonia (Border/Window просто заливаются этой кистью, без снимков экрана).</summary>
    /// <summary>"Жидкое стекло" выключено (п.1.3) — карточка остаётся матовой и слабо
    /// прозрачной: плоский однотонный цвет с высокой альфой (не глянцевый градиент с бликом,
    /// как в GlassCardBrush), почти как обычная непрозрачная тема, лишь с лёгким намёком на
    /// прозрачность.</summary>
    private static SolidColorBrush MatteGlassBrush(bool dark) =>
        new(Color.Parse(dark ? "#E8232A45" : "#E8F5F6FA"));

    private static LinearGradientBrush GlassCardBrush(bool dark, double opacityPercent)
    {
        var stops = dark
            ? new[] { "#1A2A3A5C", "#4D405878", "#334A6088", "#1A2A3A5C", "#141E2A44" }
            : new[] { "#1AE8ECF2", "#4DFFFFFF", "#33FAFBFD", "#1ADEE4EC", "#14D2D9E3" };
        var offsets = new[] { 0.0, 0.32, 0.5, 0.7, 1.0 };

        // Прозрачность растёт => alpha-компонента должна ПАДАТЬ, отсюда (100 - opacityPercent).
        var clampedPercent = Math.Clamp(opacityPercent, 0, 100);
        var alphaScale = (100 - clampedPercent) / (100 - GlassOpacityBaseline);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        for (var i = 0; i < stops.Length; i++)
            brush.GradientStops.Add(new GradientStop(ScaleAlpha(stops[i], alphaScale), offsets[i]));
        return brush;
    }

    private static Color ScaleAlpha(string hex, double alphaScale)
    {
        var color = Color.Parse(hex);
        var newAlpha = (byte)Math.Clamp(Math.Round(color.A * alphaScale), 0, 255);
        return new Color(newAlpha, color.R, color.G, color.B);
    }
}
