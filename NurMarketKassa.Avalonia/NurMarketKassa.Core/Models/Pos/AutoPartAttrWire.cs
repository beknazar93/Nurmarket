using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Models.Pos;

/// <summary>
/// 2026-11-20 (ответ Умара 20.11, А.4 и В.3): правила записи <c>attributes</c> товара на сервер. PATCH заменяет массив ЦЕЛИКОМ, поэтому шлём все атрибуты товара — свои (part_no_norm, oem, cross_N, fits_N,
/// condition, part_type, warranty_days, electric, no_return) и чужие без изменений; иначе пропадут <c>part_no_norm</c> и ключи, заведённые сайтом или другим устройством. Формат: <c>key</c> — латиница,
/// цифры и «_» (с буквы, до 32 знаков); <c>value</c> — строка до 64 знаков или число (true/false нельзя — флаги шлём 1/0); не больше 30 атрибутов. Ошибка сервера — 400 invalid_attributes.
/// </summary>
public static class AutoPartAttrWire
{
    public const int MaxAttributes = 30;
    public const int MaxValueLength = 64;
    private static readonly Regex KeyRule = new("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.Compiled);

    /// <summary>Ключи-флаги и числа: уходят числом (1/0, дни гарантии), как в примере сервера.</summary>
    private static readonly HashSet<string> NumericKeys = new(StringComparer.Ordinal) { "warranty_days", "electric", "no_return" };

    /// <summary>Ключи, которыми владеет окно товара: если их нет в записываемом списке — владелец их убрал (удалил кросс-номер, очистил артикул).</summary>
    public static bool IsOwnedFamily(string key) =>
        key is "part_no_norm" or "condition" or "part_type" or "oem"
        || key.StartsWith("oem_", StringComparison.Ordinal) || key.StartsWith("cross_", StringComparison.Ordinal) || key.StartsWith("fits_", StringComparison.Ordinal);

    /// <summary>Флаги и гарантия: окно товара пишет их только когда владелец их отметил; нет в списке — значение на сервере не трогаем.</summary>
    public static bool IsOptionalOwned(string key) => NumericKeys.Contains(key);

    public static bool IsValidKey(string? key) => key is not null && KeyRule.IsMatch(key);

    /// <summary>Привести список к правилам сервера: неверные ключи и пустые значения отбрасываются, true/false → 1/0, значение до 64 знаков, повтор ключа — последний, не больше 30.</summary>
    public static List<AutoPartAttr> Sanitize(IEnumerable<AutoPartAttr> attrs)
    {
        var result = new List<AutoPartAttr>();
        foreach (var a in attrs)
        {
            var key = (a.Key ?? "").Trim();
            if (!IsValidKey(key))
                continue;
            var value = (a.Value ?? "").Trim();
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                value = "1";
            else if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                value = "0";
            if (value.Length == 0)
                continue;
            if (value.Length > MaxValueLength)
                value = value[..MaxValueLength];
            var item = new AutoPartAttr { Key = key, Value = value, Unit = a.Unit, IsNumber = a.IsNumber };
            var i = result.FindIndex(x => x.Key == key);
            if (i >= 0)
                result[i] = item;
            else if (result.Count < MaxAttributes)
                result.Add(item);
        }
        return result;
    }

    /// <summary>Значение для JSON: число (целое или десятичное) — когда ключ числовой или сервер прислал число; иначе строка.</summary>
    public static object ValueForWire(AutoPartAttr a)
    {
        var text = (a.Value ?? "").Trim();
        if ((NumericKeys.Contains(a.Key) || a.IsNumber) && text.Length > 0)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return l;
            if (a.IsNumber && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return text;
    }

    /// <summary>Массив атрибутов для тела запроса (после <see cref="Sanitize"/>).</summary>
    public static object[] ToWire(IEnumerable<AutoPartAttr> attrs) =>
        Sanitize(attrs).Select(a =>
        {
            var item = new Dictionary<string, object?> { ["key"] = a.Key, ["value"] = ValueForWire(a) };
            if (!string.IsNullOrWhiteSpace(a.Unit))
                item["unit"] = a.Unit;
            return (object)item;
        }).ToArray();

    /// <summary>Свежие атрибуты карточки с сервера (<c>attributes</c> в ответе products/{id}/); null — поля нет.</summary>
    public static List<AutoPartAttr>? ReadServer(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<AutoPartAttr>();
        foreach (var a in attrs.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String)
                continue;
            var kind = a.TryGetProperty("value", out var v) ? v.ValueKind : JsonValueKind.Null;
            var value = kind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => "",
            };
            list.Add(new AutoPartAttr
            {
                Key = k.GetString()?.Trim() ?? "",
                Value = value,
                Unit = a.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                IsNumber = kind == JsonValueKind.Number,
            });
        }
        return list;
    }

    /// <summary>
    /// Склейка перед PATCH: берём СВЕЖИЙ список с сервера и накладываем на него то, что записывает окно товара (<paramref name="own"/>). Чужие ключи сервера остаются как есть;
    /// «свои» семейства (артикул, OEM, кроссы, «подходит к», состояние) заменяются тем, что в окне (нет в окне — удалены); флаги и гарантия — только если окно их задало.
    /// Если свежего списка нет (<paramref name="server"/> == null) — пишется то, что в окне.
    /// </summary>
    public static List<AutoPartAttr> Merge(IReadOnlyList<AutoPartAttr> own, IReadOnlyList<AutoPartAttr>? server)
    {
        if (server is null)
            return Sanitize(own);
        var ownKeys = own.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var merged = new List<AutoPartAttr>();
        foreach (var s in server)
        {
            if (IsOwnedFamily(s.Key))
                continue; // перенесём из окна ниже в том же порядке
            if (IsOptionalOwned(s.Key) && ownKeys.Contains(s.Key))
                continue;
            merged.Add(s);
        }
        // Чужие ключи из окна (копия, снятая при открытии карточки) не берём: свежие с сервера главнее, иначе чужая правка, сделанная пока окно было открыто, затёрлась бы старым значением.
        merged.AddRange(own.Where(a => IsOwnedFamily(a.Key) || IsOptionalOwned(a.Key)));
        return Sanitize(merged);
    }
}
