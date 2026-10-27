using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-27, ТЗ «Сайт — темы для магазина одежды» (пакеты С-0 и С-1): загрузчик готовых тем сайта вынесен из SiteEditorWindow,
// чтобы им пользовались и редактор сайта, и Маркетплейс. Темы — данные: Assets/SiteThemes/themes.json (6 прежних) и
// Assets/SiteThemes/clothing/index.json (10 тем одежды). Читаются только code, name{5}, description{5}, images, patch, а также
// необязательные spheres, version, voice_words — старые файлы без них работают как раньше. Тема не трогает тексты, телефон и товары.

/// <summary>Картинка темы: адрес поля в настройках сайта, имя файла рядом с темой и вид для сервера (cover | other).</summary>
public sealed record SiteThemeImage(string Path, string File, string Kind);

/// <summary>Готовая тема сайта. Название и описание берутся на языке программы в момент обращения (язык можно сменить без перезапуска).</summary>
public sealed class SiteThemeInfo
{
    private readonly JsonObject _doc;

    internal SiteThemeInfo(string code, string folder, JsonObject doc, JsonObject patch, IReadOnlyList<SiteThemeImage> images,
        IReadOnlyList<string> spheres, int version, IReadOnlyList<(string Word, bool Prefix)> words)
    {
        Code = code;
        Folder = folder;
        _doc = doc;
        Patch = patch;
        Images = images;
        Spheres = spheres;
        Version = version;
        VoiceWords = words;
    }

    public string Code { get; }

    /// <summary>Папка темы внутри Assets/SiteThemes: "" или "clothing/".</summary>
    public string Folder { get; }

    public JsonObject Patch { get; }
    public IReadOnlyList<SiteThemeImage> Images { get; }

    /// <summary>Для каких сфер магазина тема (например, clothing). Пусто — для любого магазина.</summary>
    public IReadOnlyList<string> Spheres { get; }

    public int Version { get; }

    /// <summary>Слова для голосовых команд (Prefix — совпадение с начала слова; иначе — просто вхождение, как у прежних тем).</summary>
    public IReadOnlyList<(string Word, bool Prefix)> VoiceWords { get; }

    public string Name => Local("name");
    public string Description => Local("description");

    private string Local(string key) => _doc[key] is JsonObject n
        ? Tr.T(n["ru"]?.ToString() ?? "", n["ky"]?.ToString() ?? "", n["en"]?.ToString() ?? "", n["tr"]?.ToString() ?? "", n["uz"]?.ToString() ?? "")
        : Code;

    public bool IsFor(string sphere) => Spheres.Contains(sphere, StringComparer.OrdinalIgnoreCase);

    /// <summary>Файл темы (картинка, превью) как ресурс программы.</summary>
    public Uri AssetUri(string file) => new($"avares://NurMarketKassa.Avalonia/Assets/SiteThemes/{Folder}{file}");

    public Uri ThumbUri => AssetUri(Code + "_thumb.png");
}

public static class SiteThemeCatalog
{
    private static List<SiteThemeInfo>? _all;

    /// <summary>2026-10-27: тема, которую просили применить из Маркетплейса («Открыть в редакторе сайта»); редактор забирает её сам.</summary>
    public static string? PendingThemeCode { get; set; }

    // Слова прежних шести тем для голосовых команд — как были в SiteEditorWindow (совпадение по вхождению).
    private static readonly Dictionary<string, string[]> LegacyWords = new()
    {
        ["sunny"] = ["солнеч", "күн", "sunny", "güneş", "quyosh"],
        ["midnight"] = ["ночн", "түн", "midnight", "night", "gece", "tun"],
        ["fresh"] = ["свеж", "рынок", "базар", "fresh", "taze", "yangi"],
        ["boutique"] = ["бутик", "boutique", "butik"],
        ["alatoo"] = ["ала-тоо", "ала тоо", "алатоо", "ala-too", "alatoo", "ala too"],
        ["clean"] = ["чист", "таза", "clean", "temiz", "toza"],
    };

    /// <summary>Все темы: сначала прежние (themes.json), затем одежда (clothing/index.json). Сбой одного файла не роняет остальные.</summary>
    public static IReadOnlyList<SiteThemeInfo> All
    {
        get
        {
            if (_all is not null)
                return _all;
            var list = new List<SiteThemeInfo>();
            LoadFolder(list, "");
            LoadFolder(list, "clothing/");
            return _all = list;
        }
    }

    /// <summary>Для проверок и стендов: сбросить кеш и перечитать.</summary>
    public static void Reset() => _all = null;

    private static void LoadFolder(List<SiteThemeInfo> list, string folder)
    {
        var root = "avares://NurMarketKassa.Avalonia/Assets/SiteThemes/" + folder;
        List<string> codes;
        try
        {
            var orderUri = new Uri(root + (folder.Length == 0 ? "themes.json" : "index.json"));
            if (!AssetLoader.Exists(orderUri))
                return;
            using var order = AssetLoader.Open(orderUri);
            codes = JsonSerializer.Deserialize<List<string>>(order) ?? new List<string>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Темы сайта: список «{folder}» не прочитан ({ex.Message}).", "WARNING");
            return;
        }

        foreach (var code in codes)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri($"{root}{code}.json"));
                if (JsonNode.Parse(stream) is not JsonObject doc || doc["patch"] is not JsonObject patch)
                    continue;
                var images = new List<SiteThemeImage>();
                if (doc["images"] is JsonObject imgs)
                    foreach (var (path, value) in imgs)
                        if (value is JsonObject v && v["file"]?.ToString() is { Length: > 0 } file)
                            images.Add(new SiteThemeImage(path, file, v["kind"]?.ToString() ?? "other"));
                var spheres = doc["spheres"] is JsonArray sa ? sa.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                var version = doc["version"] is JsonValue vv && vv.TryGetValue<int>(out var n) ? n : 1;
                var words = new List<(string, bool)>();
                if (LegacyWords.TryGetValue(code, out var legacy))
                    words.AddRange(legacy.Select(w => (w, false)));
                if (doc["voice_words"] is JsonObject vw)
                    foreach (var (_, arr) in vw)
                        if (arr is JsonArray items)
                            words.AddRange(items.Select(x => x?.ToString()?.ToLowerInvariant() ?? "").Where(x => x.Length > 0).Select(x => (x, true)));
                list.Add(new SiteThemeInfo(code, folder, doc, patch, images, spheres, version, words));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Темы сайта: тема «{code}» не прочитана ({ex.Message}).", "WARNING");
            }
        }
    }

    /// <summary>Темы в порядке показа: сначала подходящие сфере магазина (в сфере «Одежда» — темы одежды), остальные — после, порядок внутри групп прежний.</summary>
    public static IReadOnlyList<SiteThemeInfo> Ordered(string? sphere)
    {
        var s = sphere ?? "";
        return All.Where(t => t.IsFor(s)).Concat(All.Where(t => !t.IsFor(s))).ToList();
    }

    public static SiteThemeInfo? Find(string? code) =>
        All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Тема по словам команды: побеждает самое длинное слово («тёмный люкс» раньше «тёмн», «бутик крем» раньше «бутик»);
    /// при равенстве — тема, подходящая сфере магазина, затем порядок каталога. Слова новых тем — только с начала слова («той» не найдётся в «простой»).</summary>
    public static SiteThemeInfo? FindByWords(string phrase, string? sphere)
    {
        var t = phrase.ToLowerInvariant().Replace('ё', 'е');
        SiteThemeInfo? best = null;
        var bestLen = 0;
        var bestFits = false;
        foreach (var theme in All)
        {
            var fits = !string.IsNullOrEmpty(sphere) && theme.IsFor(sphere);
            var candidates = theme.VoiceWords.Select(w => (w.Word.Replace('ё', 'е'), w.Prefix)).Append((theme.Name.ToLowerInvariant().Replace('ё', 'е'), false));
            foreach (var (word, prefix) in candidates)
            {
                if (word.Length == 0)
                    continue;
                var hit = prefix ? Regex.IsMatch(t, @"(?<!\p{L})" + Regex.Escape(word)) : t.Contains(word);
                if (!hit)
                    continue;
                if (word.Length > bestLen || (word.Length == bestLen && fits && !bestFits))
                {
                    best = theme;
                    bestLen = word.Length;
                    bestFits = fits;
                }
            }
        }
        return best;
    }

    /// <summary>В фразе есть слово какой-нибудь темы (для распознавания команды редактору сайта).</summary>
    public static bool MentionsTheme(string phrase) => FindByWords(phrase, null) is not null;

    /// <summary>Та же тема уже стоит в черновике: совпадают акцент, фон, шрифт, шапка и рамка (ТЗ С-0).</summary>
    public static bool Matches(SiteThemeInfo theme, Func<string, string> current)
    {
        if (theme.Patch["theme"] is not JsonObject th)
            return false;
        bool Same(string path, string? value) => string.Equals((value ?? "").Trim(), current(path).Trim(), StringComparison.OrdinalIgnoreCase);
        return Same("theme.colors.accent", th["colors"]?["accent"]?.ToString())
               && Same("theme.colors.background", th["colors"]?["background"]?.ToString())
               && Same("theme.font.family", th["font"]?["family"]?.ToString())
               && Same("theme.colors.header_bg", th["colors"]?["header_bg"]?.ToString())
               && Same("theme.colors.border", th["colors"]?["border"]?.ToString());
    }

    /// <summary>Для какого раздела галереи тема: clothing → «Одежда и обувь», иначе «Для любого».</summary>
    public static bool IsClothing(SiteThemeInfo theme) => theme.IsFor(MarketSpheres.Clothing);

    /// <summary>2026-10-27 (расследование витрины NBS, Н-1/Н-2): тема подходит сфере магазина — она «для любого магазина» (spheres пусто)
    /// или её spheres содержит сферу магазина. Продуктовому магазину тема одежды НЕ подходит (баннер с вешалкой, заглушка-платье).</summary>
    public static bool Fits(SiteThemeInfo theme, string? sphere) => theme.Spheres.Count == 0 || theme.IsFor(sphere ?? "");

    /// <summary>2026-10-27: названия сфер для предупреждения «тема не для вашей сферы»: сфера темы (первая из spheres) и сфера магазина.</summary>
    public static string ThemeSphereTitle(SiteThemeInfo theme) => SphereCatalog.Get(theme.Spheres.Count > 0 ? theme.Spheres[0] : null).ShortTitle.Get();

    /// <summary>Какая тема из каталога сейчас стоит в черновике (по цветам и шрифту, как <see cref="Matches"/>); null — своя настройка.</summary>
    public static SiteThemeInfo? CurrentTheme(Func<string, string> current) => All.FirstOrDefault(t => Matches(t, current));
}
