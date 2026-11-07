using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06: разбор пакета темы с сайта. ТОЛЬКО ДАННЫЕ: JSON формата «Редактора своих тем» ({"format":"nurmarket-theme","theme":{name, baseThemeId, light{…}, dark{…}, cornerRadius, fontSize}}).
/// Ничего не исполняется и не загружается по ссылкам из пакета. Неизвестные поля игнорируются; значения ограничены по числу и размеру
/// (глубина вложенности ≤ 16, названия ≤ 60 знаков, цвета — только #RGB/#RRGGBB/#AARRGGBB, скругление 0–24, шрифт 12–18).</summary>
public static class MarketplaceThemePackage
{
    public const string Format = "nurmarket-theme";

    /// <summary>Ключи цветов темы (те же девять, что в CustomThemeColors); остальные ключи игнорируются.</summary>
    public static readonly string[] ColorKeys = ["accent", "accentText", "background", "panel", "text", "textSoft", "success", "warning", "danger"];

    private static readonly Regex Hex = new("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex BaseId = new("^[a-z0-9_-]{1,40}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Разбор. Бросает <see cref="InvalidDataException"/> с понятной причиной (ru) для журнала.</summary>
    public static ThemePackageData Parse(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("пакет не JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("пакет должен быть объектом");
            if (Prop(root, "format") is { ValueKind: JsonValueKind.String } f && !string.Equals(f.GetString(), Format, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("не пакет темы NurCRM (format)");
            var theme = Prop(root, "theme") is { ValueKind: JsonValueKind.Object } t ? t : root; // допускаем и «голую» тему без обёртки
            var light = Prop(theme, "light") is { ValueKind: JsonValueKind.Object } l ? Colors(l) : new();
            var dark = Prop(theme, "dark") is { ValueKind: JsonValueKind.Object } dk ? Colors(dk) : new();
            if (light.Count == 0 && dark.Count == 0)
                throw new InvalidDataException("в пакете нет ни одного цвета");
            var data = new ThemePackageData
            {
                Name = Text(Prop(theme, "name"), 60),
                Light = light,
                Dark = dark,
            };
            if (Text(Prop(theme, "baseThemeId"), 40) is { Length: > 0 } b && BaseId.IsMatch(b) && !b.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                data.BaseThemeId = b;
            if (Prop(theme, "cornerRadius") is { ValueKind: JsonValueKind.Number } r && r.TryGetDouble(out var rv))
                data.CornerRadius = Math.Clamp(rv, 0, 24);
            if (Prop(theme, "fontSize") is { ValueKind: JsonValueKind.Number } fs && fs.TryGetDouble(out var fv))
                data.FontSize = Math.Clamp(fv, 12, 18);
            return data;
        }
    }

    /// <summary>#RGB / #RRGGBB / #AARRGGBB → #RRGGBB; другое — null.</summary>
    public static string? NormalizeHex(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s) || !Hex.IsMatch(s))
            return null;
        s = s.TrimStart('#');
        return s.Length switch
        {
            3 => $"#{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}".ToUpperInvariant(),
            6 => "#" + s.ToUpperInvariant(),
            _ => "#" + s[2..].ToUpperInvariant(), // AARRGGBB — альфа отбрасывается, как в редакторе тем
        };
    }

    private static Dictionary<string, string> Colors(JsonElement obj)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ColorKeys)
            if (Prop(obj, key) is { ValueKind: JsonValueKind.String } v && NormalizeHex(v.GetString()) is { } hex)
                result[key] = hex;
        return result;
    }

    private static string Text(JsonElement? el, int max) =>
        el is { ValueKind: JsonValueKind.String } s ? MarketplaceLocalizedText.Clip(s.GetString(), max) : "";

    /// <summary>Свойство без учёта регистра (сервер может прислать camelCase или snake_case-подобные имена без подчёркиваний).</summary>
    private static JsonElement? Prop(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }
}
