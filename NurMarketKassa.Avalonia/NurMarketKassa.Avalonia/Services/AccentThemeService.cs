using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Оформление кассы: тема задаёт весь облик, а не один акцентный цвет.
///
/// 2026-09-23, заново после сноса прежних восьми тем. У старых тем менялись акцент и форма
/// углов, а поверхности оставались почти одинаковыми — переключение было еле заметно. Здесь
/// каждая тема несёт полную палитру: фон окна, панели, поля ввода, границы, цвет текста,
/// плитки каталога и цену, вкладки, — плюс собственную форму (скруглённость кнопок и
/// карточек), размер и начертание шрифта. Поэтому темы отличаются не оттенком кнопки, а всем
/// экраном сразу.
///
/// У каждой темы два варианта — светлый и тёмный: переключатель «Тема» в шапке кассы остаётся
/// на месте и переключает варианты ВНУТРИ выбранной темы.
///
/// Применяется прямой записью в Application.Resources: это единственный слой, который
/// перекрывает и ThemeDictionaries (Light/Dark), и уже загруженные MergedDictionaries, поэтому
/// весь интерфейс (везде DynamicResource) перекрашивается сразу, без правки каждого окна.
/// </summary>
public static class AccentThemeService
{
    public sealed record ThemeOption(string Id, string LabelRu, string LabelKy, string Icon, string DescRu, string DescKy);

    /// <summary>Полная палитра одного варианта темы (светлого или тёмного).</summary>
    private sealed record Palette(
        string Window, string WindowAlt, string Panel, string PanelElevated, string PanelSoft,
        string Input, string InputAlt, string Border, string BorderStrong,
        string Text, string TextMuted, string TextSoft,
        string Accent, string AccentStrong, string AccentSoft, string AccentForeground,
        string TileBg, string TileBorder, string Price, string Meta, string Stock,
        string TabBg, string TabBgHover, string TabBgSelected, string TabBorderSelected,
        string Focus, string Money, string SurfaceSubtle);

    /// <summary>Тема целиком: палитры обоих вариантов, форма и шрифт.</summary>
    private sealed record Skin(
        Palette Light, Palette Dark,
        double ButtonRadius, double CardRadius,
        double FontSize, string? FontFamily = null);

    /// <summary>Шрифт с кириллицей и кыргызскими буквами (ң, ө, ү): системный Segoe UI рисует
    /// их не во всех начертаниях, поэтому первым в списке идёт вшитый Noto Sans.</summary>
    private const string KyrgyzSafeFont = "avares://NurMarketKassa.Avalonia/Assets/Fonts#Noto Sans";

    // ------------------------------------------------------------------ базовые палитры
    // Значения совпадают с Themes/AppThemeLight.axaml и AppTheme.axaml — от них темы берут
    // отсчёт через `with`, меняя только то, что делает их собой. Так ни один ключ не забывается.

    private static readonly Palette BaseLight = new(
        Window: "#EFF3F8", WindowAlt: "#FFFFFF", Panel: "#FFFFFF", PanelElevated: "#FFFFFF", PanelSoft: "#E2E8F0",
        Input: "#FFFFFF", InputAlt: "#F1F5F9", Border: "#CBD5E1", BorderStrong: "#94A3B8",
        Text: "#0F172A", TextMuted: "#334155", TextSoft: "#64748B",
        Accent: "#F7D617", AccentStrong: "#D6B500", AccentSoft: "#FFF7C2", AccentForeground: "#000000",
        TileBg: "#FFFFFF", TileBorder: "#E2E8F0", Price: "#A16207", Meta: "#94A3B8", Stock: "#475569",
        TabBg: "#E2E8F0", TabBgHover: "#CBD5E1", TabBgSelected: "#F7D617", TabBorderSelected: "#D6B500",
        Focus: "#F7D617", Money: "#0F172A", SurfaceSubtle: "#F8FAFC");

    private static readonly Palette BaseDark = new(
        Window: "#1E1E1E", WindowAlt: "#252526", Panel: "#252526", PanelElevated: "#2B2B2C", PanelSoft: "#333333",
        Input: "#252526", InputAlt: "#2D2D2D", Border: "#454545", BorderStrong: "#5A5A5A",
        Text: "#FFFFFF", TextMuted: "#CCCCCC", TextSoft: "#A9A9A9",
        Accent: "#2563EB", AccentStrong: "#1D4ED8", AccentSoft: "#1E3A8A", AccentForeground: "#FFFFFF",
        TileBg: "#2B2B2C", TileBorder: "#454545", Price: "#4FA8E8", Meta: "#A9A9A9", Stock: "#A9B4C2",
        TabBg: "#2D2D30", TabBgHover: "#3A3A3C", TabBgSelected: "#007ACC", TabBorderSelected: "#007ACC",
        Focus: "#007ACC", Money: "#F8FAFC", SurfaceSubtle: "#252526");

    // ------------------------------------------------------------------ сами темы

    private static readonly Dictionary<string, Skin> Skins = new(StringComparer.OrdinalIgnoreCase)
    {
        // Родной вид кассы — то, с чем она ставится: жёлтый акцент в светлом режиме, синий в
        // тёмном, прямые углы.
        ["classic"] = new(BaseLight, BaseDark, ButtonRadius: 0, CardRadius: 0, FontSize: 14),

        // Строгий монохром: ни одного цветного пятна, кроме самой кнопки действия. Поверхности
        // холодно-серые, цена чёрная, углы заметно скруглены — вид делового приложения.
        ["graphite"] = new(
            BaseLight with
            {
                Window = "#E8EBEF", WindowAlt = "#F4F6F8", Panel = "#F8F9FB", PanelElevated = "#FFFFFF",
                PanelSoft = "#DDE1E7", Input = "#FFFFFF", InputAlt = "#EDEFF3",
                Border = "#C2C8D0", BorderStrong = "#8A929D",
                Text = "#111827", TextMuted = "#374151", TextSoft = "#6B7280",
                Accent = "#1F2937", AccentStrong = "#111827", AccentSoft = "#DFE3E8", AccentForeground = "#FFFFFF",
                TileBg = "#FFFFFF", TileBorder = "#CDD3DB", Price = "#111827", Meta = "#9CA3AF", Stock = "#4B5563",
                TabBg = "#DDE1E7", TabBgHover = "#CDD3DB", TabBgSelected = "#1F2937", TabBorderSelected = "#111827",
                Focus = "#1F2937", Money = "#111827", SurfaceSubtle = "#F3F4F6",
            },
            BaseDark with
            {
                Window = "#16181D", WindowAlt = "#1C1F25", Panel = "#1C1F25", PanelElevated = "#22262E",
                PanelSoft = "#2A2F38", Input = "#1C1F25", InputAlt = "#242932",
                Border = "#343A44", BorderStrong = "#4A515D",
                Text = "#F3F4F6", TextMuted = "#D1D5DB", TextSoft = "#9CA3AF",
                Accent = "#E5E7EB", AccentStrong = "#FFFFFF", AccentSoft = "#2A2F38", AccentForeground = "#111827",
                TileBg = "#22262E", TileBorder = "#343A44", Price = "#F3F4F6", Meta = "#9CA3AF", Stock = "#B6BCC6",
                TabBg = "#22262E", TabBgHover = "#2A2F38", TabBgSelected = "#E5E7EB", TabBorderSelected = "#FFFFFF",
                Focus = "#E5E7EB", Money = "#F3F4F6", SurfaceSubtle = "#1C1F25",
            },
            ButtonRadius: 12, CardRadius: 16, FontSize: 14),

        // Тёплая гамма: песочный фон, терракотовый акцент, самые круглые карточки. Экран
        // выглядит мягко — противоположность «Графиту».
        ["sunset"] = new(
            BaseLight with
            {
                Window = "#FBE7D8", WindowAlt = "#FFF6EE", Panel = "#FFF8F2", PanelElevated = "#FFFFFF",
                PanelSoft = "#F6DFCE", Input = "#FFFFFF", InputAlt = "#FDF3EA",
                Border = "#EFCDB2", BorderStrong = "#D89B6C",
                Text = "#4A2A17", TextMuted = "#7A4A2A", TextSoft = "#A97A56",
                Accent = "#EA580C", AccentStrong = "#C2410C", AccentSoft = "#FFE6D2", AccentForeground = "#FFFFFF",
                TileBg = "#FFFDFB", TileBorder = "#F0CDB0", Price = "#C2410C", Meta = "#B08968", Stock = "#7A4A2A",
                TabBg = "#F6DFCE", TabBgHover = "#EFCDB2", TabBgSelected = "#EA580C", TabBorderSelected = "#C2410C",
                Focus = "#EA580C", Money = "#4A2A17", SurfaceSubtle = "#FFF7F0",
            },
            BaseDark with
            {
                Window = "#241812", WindowAlt = "#2E1F17", Panel = "#2E1F17", PanelElevated = "#38261C",
                PanelSoft = "#422E22", Input = "#2E1F17", InputAlt = "#38261C",
                Border = "#563A2B", BorderStrong = "#7A5238",
                Text = "#FDEBDD", TextMuted = "#E7C7AE", TextSoft = "#BE9578",
                Accent = "#FB923C", AccentStrong = "#EA580C", AccentSoft = "#4A2A17", AccentForeground = "#2A1509",
                TileBg = "#38261C", TileBorder = "#563A2B", Price = "#FDBA74", Meta = "#BE9578", Stock = "#E7C7AE",
                TabBg = "#38261C", TabBgHover = "#422E22", TabBgSelected = "#FB923C", TabBorderSelected = "#EA580C",
                Focus = "#FB923C", Money = "#FDEBDD", SurfaceSubtle = "#2E1F17",
            },
            ButtonRadius: 18, CardRadius: 22, FontSize: 14.5),

        // Зелёная, спокойная: холодноватый мятный фон, тёмно-зелёная цена, умеренные углы.
        ["forest"] = new(
            BaseLight with
            {
                Window = "#E3EFE5", WindowAlt = "#F2F8F3", Panel = "#F6FBF7", PanelElevated = "#FFFFFF",
                PanelSoft = "#D8E8DA", Input = "#FFFFFF", InputAlt = "#EFF6F0",
                Border = "#BBD6BE", BorderStrong = "#7FAE85",
                Text = "#14281B", TextMuted = "#2F4F38", TextSoft = "#5C7E65",
                Accent = "#15803D", AccentStrong = "#166534", AccentSoft = "#D6F0DC", AccentForeground = "#FFFFFF",
                TileBg = "#FFFFFF", TileBorder = "#C3DCC7", Price = "#166534", Meta = "#8AA890", Stock = "#2F4F38",
                TabBg = "#D8E8DA", TabBgHover = "#C3DCC7", TabBgSelected = "#15803D", TabBorderSelected = "#166534",
                Focus = "#15803D", Money = "#14281B", SurfaceSubtle = "#F4F9F5",
            },
            BaseDark with
            {
                Window = "#101A13", WindowAlt = "#16241A", Panel = "#16241A", PanelElevated = "#1C2D21",
                PanelSoft = "#233829", Input = "#16241A", InputAlt = "#1C2D21",
                Border = "#2D4534", BorderStrong = "#456B4F",
                Text = "#E7F5EA", TextMuted = "#C2DEC8", TextSoft = "#8FB198",
                Accent = "#22C55E", AccentStrong = "#16A34A", AccentSoft = "#14331F", AccentForeground = "#08170D",
                TileBg = "#1C2D21", TileBorder = "#2D4534", Price = "#6EE7A0", Meta = "#8FB198", Stock = "#C2DEC8",
                TabBg = "#1C2D21", TabBgHover = "#233829", TabBgSelected = "#22C55E", TabBorderSelected = "#16A34A",
                Focus = "#22C55E", Money = "#E7F5EA", SurfaceSubtle = "#16241A",
            },
            ButtonRadius: 10, CardRadius: 14, FontSize: 14),

        // Для яркого солнца и слабого зрения: только чёрное, белое и жёлтое, границы вдвое
        // толще (BorderStrong идёт и в обычные границы), шрифт крупнее, углы прямые.
        ["contrast"] = new(
            BaseLight with
            {
                Window = "#FFFFFF", WindowAlt = "#FFFFFF", Panel = "#FFFFFF", PanelElevated = "#FFFFFF",
                PanelSoft = "#E8E8E8", Input = "#FFFFFF", InputAlt = "#F2F2F2",
                Border = "#000000", BorderStrong = "#000000",
                Text = "#000000", TextMuted = "#1A1A1A", TextSoft = "#3D3D3D",
                Accent = "#FACC15", AccentStrong = "#CA8A04", AccentSoft = "#FFF3B0", AccentForeground = "#000000",
                TileBg = "#FFFFFF", TileBorder = "#000000", Price = "#000000", Meta = "#3D3D3D", Stock = "#1A1A1A",
                TabBg = "#FFFFFF", TabBgHover = "#F2F2F2", TabBgSelected = "#FACC15", TabBorderSelected = "#000000",
                Focus = "#000000", Money = "#000000", SurfaceSubtle = "#FFFFFF",
            },
            BaseDark with
            {
                Window = "#000000", WindowAlt = "#000000", Panel = "#000000", PanelElevated = "#0A0A0A",
                PanelSoft = "#1A1A1A", Input = "#000000", InputAlt = "#101010",
                Border = "#FFFFFF", BorderStrong = "#FFFFFF",
                Text = "#FFFFFF", TextMuted = "#EDEDED", TextSoft = "#C8C8C8",
                Accent = "#FACC15", AccentStrong = "#FDE047", AccentSoft = "#2A2200", AccentForeground = "#000000",
                TileBg = "#000000", TileBorder = "#FFFFFF", Price = "#FACC15", Meta = "#C8C8C8", Stock = "#EDEDED",
                TabBg = "#000000", TabBgHover = "#1A1A1A", TabBgSelected = "#FACC15", TabBorderSelected = "#FFFFFF",
                Focus = "#FFFFFF", Money = "#FFFFFF", SurfaceSubtle = "#000000",
            },
            ButtonRadius: 0, CardRadius: 0, FontSize: 15.5),

        // Моноширинный «ламповый» вид старых касс: текст ровной сеткой, зелёный по тёмному.
        // Светлый вариант — та же сетка на бумаге.
        ["terminal"] = new(
            BaseLight with
            {
                Window = "#EAEADC", WindowAlt = "#F5F5EB", Panel = "#F7F7EE", PanelElevated = "#FDFDF7",
                PanelSoft = "#E4E4D6", Input = "#FFFFFF", InputAlt = "#F0F0E4",
                Border = "#C9C9B4", BorderStrong = "#8F8F76",
                Text = "#1B2B1B", TextMuted = "#33472F", TextSoft = "#5F7355",
                Accent = "#166534", AccentStrong = "#14532D", AccentSoft = "#DCE9D5", AccentForeground = "#F2F2E9",
                TileBg = "#FAFAF3", TileBorder = "#D8D8C4", Price = "#14532D", Meta = "#8F8F76", Stock = "#33472F",
                TabBg = "#E4E4D6", TabBgHover = "#D8D8C4", TabBgSelected = "#166534", TabBorderSelected = "#14532D",
                Focus = "#166534", Money = "#1B2B1B", SurfaceSubtle = "#F7F7EE",
            },
            BaseDark with
            {
                Window = "#07120A", WindowAlt = "#0B1A0E", Panel = "#0B1A0E", PanelElevated = "#0F2213",
                PanelSoft = "#14301A", Input = "#0B1A0E", InputAlt = "#0F2213",
                Border = "#1E4527", BorderStrong = "#2F6B3B",
                Text = "#B9F6CA", TextMuted = "#8FE3A6", TextSoft = "#5FB378",
                Accent = "#22C55E", AccentStrong = "#16A34A", AccentSoft = "#0F2213", AccentForeground = "#05190B",
                TileBg = "#0F2213", TileBorder = "#1E4527", Price = "#4ADE80", Meta = "#5FB378", Stock = "#8FE3A6",
                TabBg = "#0F2213", TabBgHover = "#14301A", TabBgSelected = "#22C55E", TabBorderSelected = "#16A34A",
                Focus = "#22C55E", Money = "#B9F6CA", SurfaceSubtle = "#0B1A0E",
            },
            ButtonRadius: 2, CardRadius: 2, FontSize: 14, FontFamily: "Consolas"),
    };

    /// <summary>Список для галереи в Маркетплейсе — порядок здесь и есть порядок карточек.</summary>
    public static readonly ThemeOption[] AvailableThemes =
    [
        new("classic", "Классическая", "Классикалык", "",
            "Родной вид кассы: жёлтый акцент в светлом режиме, синий — в тёмном, прямые углы. То, с чем касса ставится.",
            "Кассанын жердик көрүнүшү: жарык режимде сары акцент, караңгы режимде — көк, бурчтары түз."),
        new("graphite", "Графит", "Графит", "",
            "Строгий монохром без цветных пятен: холодно-серые поверхности, чёрная цена, заметно скруглённые кнопки и карточки.",
            "Катаал монохром: муздак боз беттер, кара баа, баскычтар менен карточкалар байкаларлык тегерек."),
        new("sunset", "Закат", "Кеч батым", "",
            "Тёплая песочная гамма с терракотовым акцентом и самыми круглыми карточками — мягкий, неофициальный вид.",
            "Жылуу кум түстүү гамма, терракота акцент жана эң тегерек карточкалар — жумшак көрүнүш."),
        new("forest", "Лесная", "Токой", "",
            "Спокойная зелёная гамма: мятный фон окон, тёмно-зелёная цена, умеренно скруглённые элементы.",
            "Тынч жашыл гамма: жалбыз фон, кара жашыл баа, орточо тегеректелген элементтер."),
        new("contrast", "Контрастная", "Контраст", "",
            "Только чёрное, белое и жёлтое: чёрные границы у всех карточек, увеличенный шрифт, прямые углы. Для яркого света и слабого зрения.",
            "Кара, ак жана сары гана: бардык карточкаларда кара чек, чоңойтулган шрифт, түз бурчтар. Жарык жерге жана начар көрүүгө."),
        new("terminal", "Терминал", "Терминал", "",
            "Моноширинный шрифт и зелёный по тёмному — вид старых кассовых терминалов. Цифры выстраиваются ровной сеткой.",
            "Моношириналуу шрифт жана караңгы фондо жашыл — эски кассалык терминалдардын көрүнүшү."),
    ];

    /// <summary>Ключи, которые задаёт тема. Список нужен, чтобы при переключении снимать всё
    /// разом: иначе от прошлой темы остались бы отдельные цвета, которых новая не касается.</summary>
    private static readonly string[] ThemeKeys =
    [
        "BrushWindow", "BrushWindowAlt", "BrushPanel", "BrushDialogPanel", "BrushPanelElevated", "BrushPanelSoft",
        "BrushInput", "BrushInputAlt", "BrushBorder", "BrushBorderStrong",
        "BrushText", "BrushTextMuted", "BrushTextSoft",
        "BrushAccent", "BrushPrimary", "BrushAccentStrong", "BrushAccentSoft", "BrushAccentForeground",
        "BrushCatalogTileBg", "BrushCatalogTileBorder", "BrushCatalogPrice", "BrushCatalogMeta", "BrushCatalogStock",
        "BrushCatalogTabBg", "BrushCatalogTabBgHover", "BrushCatalogTabBgSelected", "BrushCatalogTabBorderSelected",
        "BrushFocus", "BrushMoney", "BrushSurfaceSubtle",
    ];

    /// <summary>Применяет тему поверх выбранного светлого/тёмного варианта.</summary>
    public static void Apply(string? themeId, bool dark)
    {
        if (Application.Current is not { } app)
            return;

        // Модальные окна ссылаются на эти два ключа напрямую (TransparencyLevelHint и Background),
        // поэтому они должны существовать всегда, ещё до создания первого диалога. Фон самого
        // окна держим прозрачным: видимый вид даёт внутренняя карточка, у которой CornerRadius
        // действительно обрезает содержимое, а силуэт окна на уровне ОС всегда прямоугольный.
        app.Resources["DialogTransparencyHint"] =
            new List<WindowTransparencyLevel> { WindowTransparencyLevel.Transparent };
        app.Resources["DialogWindowBackground"] = Brushes.Transparent;

        var skin = Resolve(themeId);
        var p = dark ? skin.Dark : skin.Light;

        foreach (var key in ThemeKeys)
            app.Resources.Remove(key);

        app.Resources["BrushWindow"] = Brush(p.Window);
        app.Resources["BrushWindowAlt"] = Brush(p.WindowAlt);
        app.Resources["BrushPanel"] = Brush(p.Panel);
        app.Resources["BrushDialogPanel"] = Brush(p.Panel);
        app.Resources["BrushPanelElevated"] = Brush(p.PanelElevated);
        app.Resources["BrushPanelSoft"] = Brush(p.PanelSoft);
        app.Resources["BrushInput"] = Brush(p.Input);
        app.Resources["BrushInputAlt"] = Brush(p.InputAlt);
        app.Resources["BrushBorder"] = Brush(p.Border);
        app.Resources["BrushBorderStrong"] = Brush(p.BorderStrong);
        app.Resources["BrushText"] = Brush(p.Text);
        app.Resources["BrushTextMuted"] = Brush(p.TextMuted);
        app.Resources["BrushTextSoft"] = Brush(p.TextSoft);
        app.Resources["BrushAccent"] = Brush(p.Accent);
        app.Resources["BrushPrimary"] = Brush(p.Accent);
        app.Resources["BrushAccentStrong"] = Brush(p.AccentStrong);
        app.Resources["BrushAccentSoft"] = Brush(p.AccentSoft);
        app.Resources["BrushAccentForeground"] = Brush(p.AccentForeground);
        app.Resources["BrushCatalogTileBg"] = Brush(p.TileBg);
        app.Resources["BrushCatalogTileBorder"] = Brush(p.TileBorder);
        app.Resources["BrushCatalogPrice"] = Brush(p.Price);
        app.Resources["BrushCatalogMeta"] = Brush(p.Meta);
        app.Resources["BrushCatalogStock"] = Brush(p.Stock);
        app.Resources["BrushCatalogTabBg"] = Brush(p.TabBg);
        app.Resources["BrushCatalogTabBgHover"] = Brush(p.TabBgHover);
        app.Resources["BrushCatalogTabBgSelected"] = Brush(p.TabBgSelected);
        app.Resources["BrushCatalogTabBorderSelected"] = Brush(p.TabBorderSelected);
        app.Resources["BrushFocus"] = Brush(p.Focus);
        app.Resources["BrushMoney"] = Brush(p.Money);
        app.Resources["BrushSurfaceSubtle"] = Brush(p.SurfaceSubtle);

        app.Resources["SettingsButtonRadius"] = new CornerRadius(skin.ButtonRadius);
        app.Resources["SettingsCardRadius"] = new CornerRadius(skin.CardRadius);

        // FontFamily в Avalonia разбирает запись вида "avares://…#Имя, Запасной" только когда
        // ссылка на встроенный шрифт стоит ПЕРВОЙ. Поэтому тему со своим шрифтом («Терминал»)
        // нельзя просто дописать перед вшитым Noto Sans — строка перестаёт разбираться, и шрифт
        // молча остаётся прежним. Системные начертания перечисляем сами, а вшитый Noto Sans
        // держим для тем без собственного шрифта: кыргызские ң, ө, ү есть не везде.
        app.Resources["AppFontFamily"] = string.IsNullOrWhiteSpace(skin.FontFamily)
            ? new FontFamily($"{KyrgyzSafeFont}, Segoe UI")
            : new FontFamily(skin.FontFamily);
        app.Resources["AppFontSize"] = skin.FontSize;
    }

    /// <summary>Цвет-образец для карточки темы в Маркетплейсе.</summary>
    public static string? GetAccentHex(string themeId) =>
        Skins.TryGetValue(themeId ?? string.Empty, out var skin) ? skin.Light.Accent : null;

    /// <summary>Цвета для мини-предпросмотра в галерее тем. Граница плитки идёт отдельно:
    /// у «Контрастной» плитка белая на белом фоне, и без её собственной чёрной рамки
    /// предпросмотр выглядел бы пустым квадратом.</summary>
    public static (string Window, string Tile, string TileBorder, string Accent, string Text)? GetPreview(string themeId, bool dark)
    {
        if (!Skins.TryGetValue(themeId ?? string.Empty, out var skin))
            return null;

        var p = dark ? skin.Dark : skin.Light;
        return (p.Window, p.TileBg, p.TileBorder, p.Accent, p.Text);
    }

    /// <summary>Приводит сохранённый в настройках id к существующему. В user-settings.json у
    /// работающих касс остались снятые темы ("gold", "navy", "glass"…) — без приведения в
    /// галерее не подсвечивалась бы ни одна карточка.</summary>
    public static string Normalize(string? themeId) =>
        !string.IsNullOrWhiteSpace(themeId) && Skins.ContainsKey(themeId) ? themeId : "classic";

    private static Skin Resolve(string? themeId) =>
        !string.IsNullOrWhiteSpace(themeId) && Skins.TryGetValue(themeId, out var skin)
            ? skin
            : Skins["classic"];

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
