using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (просьба владельца «темы с сайта»): текст на пяти языках, который отдаёт сервер (name/description: {ru, ky, en, tr, uz} или просто строка).</summary>
public sealed class MarketplaceLocalizedText
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Текст на языке программы; нет — русский, затем любой первый.</summary>
    public string Get()
    {
        var key = UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => "ky",
            AppLanguage.English => "en",
            AppLanguage.Turkish => "tr",
            AppLanguage.Uzbek => "uz",
            _ => "ru",
        };
        if (Values.TryGetValue(key, out var own) && !string.IsNullOrWhiteSpace(own))
            return own;
        if (Values.TryGetValue("ru", out var ru) && !string.IsNullOrWhiteSpace(ru))
            return ru;
        return Values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    internal static MarketplaceLocalizedText From(JsonElement el, int maxLength)
    {
        var text = new MarketplaceLocalizedText();
        if (el.ValueKind == JsonValueKind.String)
            text.Values["ru"] = Clip(el.GetString(), maxLength);
        else if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Name.Length <= 8 && text.Values.Count < 8)
                    text.Values[p.Name.ToLowerInvariant()] = Clip(p.Value.GetString(), maxLength);
        return text;
    }

    internal static string Clip(string? value, int maxLength)
    {
        var s = (value ?? "").Trim();
        return s.Length > maxLength ? s[..maxLength] : s;
    }
}

/// <summary>Запись каталога тем и видов кассы с сервера (GET api/main/marketplace/themes/). Только данные, ничего исполняемого.</summary>
public sealed class MarketplaceThemeEntry
{
    public string Id { get; set; } = "";
    /// <summary>theme-&lt;slug&gt;.</summary>
    public string Code { get; set; } = "";
    /// <summary>theme | view | labels | scale-profile (другое — «неизвестный вид», не ставится).</summary>
    public string Kind { get; set; } = "theme";
    public MarketplaceLocalizedText Name { get; set; } = new();
    public MarketplaceLocalizedText Description { get; set; } = new();
    public string? PreviewUrl { get; set; }
    public string Version { get; set; } = "";
    /// <summary>null или 0 — бесплатно.</summary>
    public decimal? Price { get; set; }
    public string Currency { get; set; } = "сом";
    /// <summary>Сервер подтверждает: у компании есть право скачать пакет (куплено или бесплатно).</summary>
    public bool Entitled { get; set; }
    public string? PackageUrl { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    public string? MinAppVersion { get; set; }
    /// <summary>Страница темы на сайте (https); нет — касса откроет общий Маркетплейс сайта с параметром item.</summary>
    public string? WebUrl { get; set; }
    /// <summary>Основной цвет темы для значка карточки (необязательно).</summary>
    public string? Accent { get; set; }
    /// <summary>Срок покупки (yyyy-MM-dd) — только для показа.</summary>
    public string? Until { get; set; }

    [JsonIgnore]
    public bool IsFree => Price is null or <= 0;

    [JsonIgnore]
    public bool IsTheme => string.Equals(Kind, "theme", StringComparison.OrdinalIgnoreCase);

    /// <summary>Код безопасен для имён и ключей: латиница, цифры, «-», «_», до 64 знаков.</summary>
    public static bool IsSafeCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= 64 && code.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    internal static MarketplaceThemeEntry? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        string Str(string name, int max = 200) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? MarketplaceLocalizedText.Clip(v.GetString(), max) : "";
        string Id()
        {
            if (!el.TryGetProperty("id", out var v))
                return "";
            return v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" };
        }

        var entry = new MarketplaceThemeEntry { Id = Id().Trim(), Code = Str("code", 64) };
        if (!IsSafeCode(entry.Id) || !IsSafeCode(entry.Code))
            return null; // без понятного id/кода запись не показываем: из них строятся адреса и ключи
        var kind = Str("kind", 32);
        if (kind.Length == 0)
            kind = Str("type", 32);
        entry.Kind = kind.Length == 0 ? "theme" : kind.ToLowerInvariant();
        entry.Name = el.TryGetProperty("name", out var n) ? MarketplaceLocalizedText.From(n, 80) : new();
        entry.Description = el.TryGetProperty("description", out var d) ? MarketplaceLocalizedText.From(d, 600) : new();
        entry.PreviewUrl = Str("preview_url", 500) is { Length: > 0 } pu ? pu : null;
        entry.Version = Str("version", 32);
        if (el.TryGetProperty("price", out var price))
        {
            if (price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var pn))
                entry.Price = pn;
            else if (price.ValueKind == JsonValueKind.String && decimal.TryParse(price.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ps))
                entry.Price = ps;
        }
        if (el.TryGetProperty("free", out var free) && free.ValueKind == JsonValueKind.True)
            entry.Price = null;
        if (Str("currency", 8) is { Length: > 0 } cur)
            entry.Currency = cur;
        entry.Entitled = el.TryGetProperty("entitled", out var ent) && ent.ValueKind == JsonValueKind.True;
        entry.PackageUrl = Str("package_url", 500) is { Length: > 0 } pk ? pk : null;
        entry.Sha256 = Str("sha256", 80).ToLowerInvariant() is { Length: > 0 } sh ? sh : null;
        if (el.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var sz) && sz >= 0)
            entry.Size = sz;
        entry.MinAppVersion = Str("min_app_version", 32) is { Length: > 0 } mv ? mv : null;
        entry.WebUrl = Str("web_url", 500) is { Length: > 0 } wu ? wu : null;
        entry.Accent = Str("accent", 12) is { Length: > 0 } ac ? ac : null;
        entry.Until = Str("until", 32) is { Length: > 0 } un ? un : null;
        return entry;
    }
}

/// <summary>Итог обновления каталога тем.</summary>
public enum MarketplaceCatalogStatus
{
    /// <summary>Каталог получен.</summary>
    Loaded,
    /// <summary>Слишком часто (не чаще раза в 20 секунд) — остался прежний каталог.</summary>
    Throttled,
    /// <summary>Сервер не умеет (нет признака marketplace_themes или 404) — вкладка показывает только свои темы.</summary>
    NotSupported,
    /// <summary>Нет связи / вход не выполнен / ошибка сервера — каталог из кэша.</summary>
    Offline,
}

public sealed record MarketplaceCatalogResult(MarketplaceCatalogStatus Status, int Count = 0, IReadOnlyList<string>? NewlyEntitled = null);

/// <summary>Данные темы из пакета (формат «Редактора своих тем»: nurmarket-theme). Цвета уже проверены (#RRGGBB), числа ограничены.</summary>
public sealed class ThemePackageData
{
    public string Name { get; set; } = "";
    public string BaseThemeId { get; set; } = "classic";
    public Dictionary<string, string> Light { get; set; } = new();
    public Dictionary<string, string> Dark { get; set; } = new();
    public double? CornerRadius { get; set; }
    public double? FontSize { get; set; }
}
