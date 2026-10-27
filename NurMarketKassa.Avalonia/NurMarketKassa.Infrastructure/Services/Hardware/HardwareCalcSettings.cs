using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-10-26 (ТЗ «Хозмаг», ХМ5): одна норма расхода «категория → расход на м²» — «Краска 0,12 л/м² в 2 слоя».
/// <see cref="Words"/> — слова, по которым норма подбирается к товару (в названии или категории; без регистра).
/// </summary>
public sealed class CalcNorm
{
    /// <summary>Ключ типовой нормы: paint, primer, adhesive, grout, putty, plaster. У добавленной владельцем — свой.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>Название. Пусто — берётся типовое на языке интерфейса (<see cref="HardwareCalcSettings.TitleOf"/>).</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Paint — расход в литрах на м²; Mix — в килограммах на м².</summary>
    [JsonPropertyName("kind")] public CalcKind Kind { get; set; } = CalcKind.Mix;
    /// <summary>Расход на м² за один слой (л или кг).</summary>
    [JsonPropertyName("perM2")] public decimal PerM2 { get; set; }
    [JsonPropertyName("layers")] public decimal Layers { get; set; } = 1m;
    [JsonPropertyName("words")] public List<string> Words { get; set; } = new();
}

/// <summary>
/// 2026-10-26 (ХМ5): правила калькулятора расхода — запас по умолчанию и нормы расхода на м² по видам материалов.
/// Типовые значения (<see cref="CreateDefault"/>) — ориентир по обычным данным производителей; владелец правит под свой товар
/// (программа владельца, ТЗ 4.2 — отдельный шаг). Хранение — ключ общих настроек компании <c>hardwareCalc</c> (JSON не больше
/// <see cref="MaxJsonBytes"/> байт): <see cref="ToJson"/> / <see cref="FromJson"/>. Подключение ключа к SettingsCloudSync.CompanyKeys
/// и свойства UserPreferences — отдельным шагом с владельцем этих файлов (СФ1); пока касса работает на типовых нормах.
/// Пустой, повреждённый или слишком большой JSON → типовые нормы, исключений наружу нет.
/// </summary>
public sealed class HardwareCalcSettings
{
    public const int MaxJsonBytes = 8 * 1024;
    public const string SettingsKey = "hardwareCalc";

    [JsonPropertyName("reserve")] public decimal ReservePercent { get; set; } = ConsumptionCalculator.DefaultReservePercent;
    [JsonPropertyName("diagonal")] public decimal DiagonalReservePercent { get; set; } = ConsumptionCalculator.DiagonalReservePercent;
    [JsonPropertyName("norms")] public List<CalcNorm> Norms { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Нормы, с которыми работает касса. Заменяются владельцем (ТЗ 4.2) через <see cref="Use"/>.</summary>
    public static HardwareCalcSettings Current { get; private set; } = CreateDefault();

    public static void Use(HardwareCalcSettings? settings) => Current = settings ?? CreateDefault();

    public static HardwareCalcSettings CreateDefault() => new()
    {
        Norms = new List<CalcNorm>
        {
            new() { Key = "paint", Kind = CalcKind.Paint, PerM2 = 0.12m, Layers = 2m,
                Words = new() { "краск", "боёк", "боек", "эмал", "paint", "boya", "bo'yoq" } },
            new() { Key = "primer", Kind = CalcKind.Paint, PerM2 = 0.10m, Layers = 1m,
                Words = new() { "грунт", "primer", "astar", "grunt" } },
            new() { Key = "adhesive", Kind = CalcKind.Mix, PerM2 = 5m, Layers = 1m,
                Words = new() { "клей", "желим", "adhesive", "yapıştırıcı", "yapistirici", "yelim" } },
            new() { Key = "grout", Kind = CalcKind.Mix, PerM2 = 0.5m, Layers = 1m,
                Words = new() { "затирк", "grout", "derz", "fuga" } },
            new() { Key = "putty", Kind = CalcKind.Mix, PerM2 = 1m, Layers = 2m,
                Words = new() { "шпакл", "шпатл", "putty", "macun", "shpatlyovka" } },
            new() { Key = "plaster", Kind = CalcKind.Mix, PerM2 = 8.5m, Layers = 1m,
                Words = new() { "штукатур", "plaster", "sıva", "suvoq" } },
        },
    };

    /// <summary>Название нормы на языке интерфейса: своё (если владелец задал) или типовое по ключу.</summary>
    public static string TitleOf(CalcNorm norm)
    {
        if (!string.IsNullOrWhiteSpace(norm.Name))
            return norm.Name!.Trim();
        return norm.Key switch
        {
            "paint" => Tr.T("Краска", "Боёк", "Paint", "Boya", "Bo'yoq"),
            "primer" => Tr.T("Грунтовка", "Грунтовка", "Primer", "Astar", "Grunt"),
            "adhesive" => Tr.T("Клей плиточный", "Плитка желими", "Tile adhesive", "Fayans yapıştırıcı", "Plitka yelimi"),
            "grout" => Tr.T("Затирка", "Затирка", "Grout", "Derz dolgu", "Zatirka"),
            "putty" => Tr.T("Шпаклёвка", "Шпаклёвка", "Putty", "Macun", "Shpatlyovka"),
            "plaster" => Tr.T("Штукатурка", "Штукатурка", "Plaster", "Sıva", "Suvoq"),
            _ => norm.Key,
        };
    }

    /// <summary>Подбор нормы по названию и категории товара: первое совпавшее слово. Нет совпадения — null (кассир выберет сам).
    /// Сначала смотрим название товара, потом категорию: «Клей плиточный» в категории «Обои и клей» должен попасть в клей.</summary>
    public CalcNorm? FindNorm(string? title, string? category)
    {
        foreach (var source in new[] { title, category })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;
            var text = source.ToLowerInvariant();
            foreach (var norm in Norms)
                if (norm.Words.Any(w => !string.IsNullOrWhiteSpace(w) && text.Contains(w.Trim().ToLowerInvariant(), StringComparison.Ordinal)))
                    return norm;
        }
        return null;
    }

    /// <summary>Какой расчёт предложить для товара: по названию и категории. Сначала ламинат, потом нормы смесей и краски
    /// («Клей для плитки» — это клей, а не плитка), потом плитка и обои. Не узнали — плитка (самый частый случай).</summary>
    public CalcKind GuessKind(string? title, string? category)
    {
        foreach (var source in new[] { title, category })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;
            var text = source.ToLowerInvariant();
            if (ContainsAny(text, "ламинат", "laminat"))
                return CalcKind.Laminate;
            if (FindNorm(source, null) is { } norm)
                return norm.Kind == CalcKind.Paint ? CalcKind.Paint : CalcKind.Mix;
            if (ContainsAny(text, "плитк", "кафел", "керамогранит", "tile", "fayans", "plitka"))
                return CalcKind.Tile;
            if (ContainsAny(text, "обои", "обоев", "wallpaper", "duvar kağıdı", "oboy"))
                return CalcKind.Wallpaper;
        }
        return CalcKind.Tile;
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.Ordinal));

    public string ToJson() =>JsonSerializer.Serialize(this, Json);

    /// <summary>Разбор JSON ключа <c>hardwareCalc</c>. Пусто / не JSON / больше 8 КБ / ни одной нормы / расход не больше нуля → типовые.</summary>
    public static HardwareCalcSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return CreateDefault();
        try
        {
            var parsed = JsonSerializer.Deserialize<HardwareCalcSettings>(json, Json);
            if (parsed is null)
                return CreateDefault();
            parsed.ReservePercent = Clamp(parsed.ReservePercent, ConsumptionCalculator.DefaultReservePercent);
            parsed.DiagonalReservePercent = Clamp(parsed.DiagonalReservePercent, ConsumptionCalculator.DiagonalReservePercent);
            parsed.Norms = (parsed.Norms ?? new List<CalcNorm>())
                .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Key) && n.PerM2 > 0m && n.Layers > 0m
                            && n.Kind is CalcKind.Paint or CalcKind.Mix)
                .Select(n => { n.Words ??= new List<string>(); return n; })
                .ToList();
            return parsed.Norms.Count == 0 ? CreateDefault() : parsed;
        }
        catch (Exception)
        {
            return CreateDefault();
        }
    }

    private static decimal Clamp(decimal value, decimal fallback) => value is < 0m or > 100m ? fallback : value;
}
