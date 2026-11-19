using System.Text.Json;
using NurMarketKassa.Core.Application;

namespace NurMarketKassa.Models.Pos;

/// <summary>Атрибут товара сервера: <c>{key, value, unit?}</c> (ответ Умара 19.11: key — латиница/цифры/_ с буквы до 32, value — до 64 знаков, до 30 атрибутов).</summary>
public sealed class AutoPartAttr
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Unit { get; set; }
}

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ4): данные запчасти, которые сервер хранит в <c>attributes</c>, <c>storage_location</c> и <c>related[]</c> товара.
/// Схему базы кассы (pos_local.db) не меняем: данные лежат отдельным файлом по id товара (<c>PartMetaStore</c>), как серийники электроники.
/// Ключи атрибутов: <c>part_no_norm</c> (артикул без пробелов и дефисов), <c>oem</c>, <c>oem_2…</c>, <c>cross_1…</c>, <c>fits_1…</c> («Toyota Camry 2012-2017»),
/// <c>condition</c> (new / used / refurb — как у сервера) и <c>part_type</c> (original / oes / aftermarket — только у «new»).
/// Чужие атрибуты (их мог завести сайт) хранятся в <see cref="Other"/> и при записи карточки возвращаются как есть — PATCH не стирает их.
/// </summary>
public sealed class AutoPartInfo
{
    public const int MaxAttributes = 30;
    public const int MaxValueLength = 64;
    public const int MaxOem = 5;
    public const int MaxCross = 10;
    public const int MaxFits = 10;
    /// <summary>Мягкий лимит тарифа «Старт»: больше 5 кросс-номеров — только подсказка, ничего не блокируется (ТЗ «Тарифы»).</summary>
    public const int SoftCrossLimit = 5;

    public string PartNo { get; set; } = "";
    public List<string> Oem { get; set; } = new();
    public List<string> Cross { get; set; } = new();
    public List<string> Fits { get; set; } = new();
    /// <summary>new / used / refurb или пусто.</summary>
    public string Condition { get; set; } = "";
    /// <summary>original / oes / aftermarket или пусто.</summary>
    public string PartType { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> Related { get; set; } = new();
    public List<AutoPartAttr> Other { get; set; } = new();

    /// <summary>Состояние для значка: original / oes / aftermarket / used / refurb; пусто — не задано.</summary>
    public string State => Condition switch
    {
        "used" => "used",
        "refurb" => "refurb",
        "new" => PartType is "original" or "oes" or "aftermarket" ? PartType : "",
        _ => PartType is "original" or "oes" or "aftermarket" ? PartType : "",
    };

    public bool IsEmpty => PartNo.Length == 0 && Oem.Count == 0 && Cross.Count == 0 && Fits.Count == 0 && Condition.Length == 0
                           && PartType.Length == 0 && Location.Length == 0 && Related.Count == 0;

    /// <summary>Все нормализованные номера товара (кроме артикула, он берётся из карточки): <c>part_no_norm</c>, OEM, кроссы.</summary>
    public IEnumerable<string> Numbers()
    {
        if (PartNo.Length > 0)
            yield return PartNo;
        foreach (var o in Oem)
            yield return o;
        foreach (var c in Cross)
            yield return c;
    }

    /// <summary>Состояние из выпадающего списка → condition + part_type.</summary>
    public static (string Condition, string PartType) FromState(string state) => state switch
    {
        "original" => ("new", "original"),
        "oes" => ("new", "oes"),
        "aftermarket" => ("new", "aftermarket"),
        "used" => ("used", ""),
        "refurb" => ("refurb", ""),
        _ => ("", ""),
    };

    /// <summary>Разобрать товар из ответа сервера (products/list, products/{id}). null — у товара нет ничего «автозапчастного».</summary>
    public static AutoPartInfo? Parse(JsonElement product)
    {
        if (product.ValueKind != JsonValueKind.Object)
            return null;
        var info = new AutoPartInfo();
        var oem = new List<(int Order, string Value)>();
        var cross = new List<(int Order, string Value)>();
        var fits = new List<(int Order, string Value)>();
        if (product.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attrs.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("key", out var kEl) || kEl.ValueKind != JsonValueKind.String)
                    continue;
                var key = kEl.GetString()?.Trim() ?? "";
                var value = ReadValue(a);
                string? unit = a.TryGetProperty("unit", out var uEl) && uEl.ValueKind == JsonValueKind.String ? uEl.GetString() : null;
                if (key.Length == 0)
                    continue;
                if (key == "part_no_norm")
                    info.PartNo = PartNumbers.Norm(value);
                else if (key == "condition")
                    info.Condition = value.Trim().ToLowerInvariant() is "new" or "used" or "refurb" ? value.Trim().ToLowerInvariant() : "";
                else if (key == "part_type")
                    info.PartType = value.Trim().ToLowerInvariant() is "original" or "oes" or "aftermarket" ? value.Trim().ToLowerInvariant() : "";
                else if (key == "oem" || key.StartsWith("oem_", StringComparison.Ordinal))
                    AddIndexed(oem, key, "oem", value);
                else if (key.StartsWith("cross_", StringComparison.Ordinal))
                    AddIndexed(cross, key, "cross", value);
                else if (key.StartsWith("fits_", StringComparison.Ordinal))
                    AddIndexed(fits, key, "fits", value, keepText: true);
                else
                    info.Other.Add(new AutoPartAttr { Key = key, Value = value, Unit = unit });
            }
        }
        info.Oem = Ordered(oem);
        info.Cross = Ordered(cross);
        info.Fits = Ordered(fits, keepText: true);
        if (product.TryGetProperty("storage_location", out var loc) && loc.ValueKind == JsonValueKind.String)
            info.Location = loc.GetString()?.Trim() ?? "";
        if (product.TryGetProperty("related", out var rel) && rel.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rel.EnumerateArray())
            {
                // related[] — либо id (число / строка), либо объект с полем id.
                string? id = r.ValueKind switch
                {
                    JsonValueKind.String => r.GetString(),
                    JsonValueKind.Number => r.GetRawText(),
                    JsonValueKind.Object when r.TryGetProperty("id", out var idEl) => idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(id) && !info.Related.Contains(id.Trim()))
                    info.Related.Add(id.Trim());
            }
        }
        return info.IsEmpty && info.Other.Count == 0 ? null : info;
    }

    private static string ReadValue(JsonElement attr)
    {
        if (!attr.TryGetProperty("value", out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    /// <summary>«cross_3» → порядок 3; значение может содержать несколько номеров через запятую (так разрешает сервер) — раскладываем.</summary>
    private static void AddIndexed(List<(int Order, string Value)> list, string key, string prefix, string value, bool keepText = false)
    {
        var tail = key.Length > prefix.Length + 1 ? key[(prefix.Length + 1)..] : "1";
        var order = int.TryParse(tail, out var n) ? n : (key == prefix ? 1 : 1000 + list.Count);
        if (keepText)
        {
            if (!string.IsNullOrWhiteSpace(value))
                list.Add((order, value.Trim()));
            return;
        }
        foreach (var part in PartNumbers.ParseList(value))
            list.Add((order, part));
    }

    private static List<string> Ordered(List<(int Order, string Value)> list, bool keepText = false)
    {
        var result = new List<string>();
        foreach (var (_, value) in list.OrderBy(x => x.Order))
            if (!result.Contains(value, keepText ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                result.Add(value);
        return result;
    }

    /// <summary>Атрибуты для записи на сервер: свои (part_no_norm, oem, cross_N, fits_N, condition, part_type) + чужие без изменений; не больше 30.
    /// Каждое значение — до 64 знаков. Что не поместилось (лимит 30 атрибутов) — отбрасывается с конца: сначала подходящие авто, потом кроссы.</summary>
    public List<AutoPartAttr> BuildAttributes()
    {
        var result = new List<AutoPartAttr>();
        string Cut(string v) => v.Length <= MaxValueLength ? v : v[..MaxValueLength];
        void Add(string key, string value)
        {
            if (value.Length > 0 && result.Count < MaxAttributes)
                result.Add(new AutoPartAttr { Key = key, Value = Cut(value) });
        }
        // Чужие — первыми: они были на карточке до нас, терять их нельзя.
        foreach (var o in Other.Take(MaxAttributes))
            result.Add(new AutoPartAttr { Key = o.Key, Value = Cut(o.Value), Unit = o.Unit });
        Add("part_no_norm", PartNo);
        Add("condition", Condition);
        Add("part_type", PartType);
        for (var i = 0; i < Oem.Count; i++)
            Add(i == 0 ? "oem" : $"oem_{i + 1}", Oem[i]);
        for (var i = 0; i < Cross.Count; i++)
            Add($"cross_{i + 1}", Cross[i]);
        for (var i = 0; i < Fits.Count; i++)
            Add($"fits_{i + 1}", Fits[i]);
        return result;
    }

    public AutoPartInfo Clone() => new()
    {
        PartNo = PartNo, Oem = new(Oem), Cross = new(Cross), Fits = new(Fits), Condition = Condition, PartType = PartType,
        Location = Location, Related = new(Related),
        Other = Other.Select(o => new AutoPartAttr { Key = o.Key, Value = o.Value, Unit = o.Unit }).ToList(),
    };
}

/// <summary>
/// 2026-11-20: мост между плиткой каталога (Core) и данными запчастей (Infrastructure, <c>PartMetaStore</c>). Хранилище подключает себя при загрузке сборки;
/// вне сферы «Автозапчасти» <see cref="Active"/> возвращает false и плитка выглядит как раньше.
/// </summary>
public static class AutoPartHub
{
    public static Func<string, AutoPartInfo?>? Lookup { get; set; }
    public static Func<bool>? IsActive { get; set; }
    /// <summary>Короткая подпись состояния для значка плитки (на языке кассы): state → «АНАЛОГ».</summary>
    public static Func<string, string>? StateBadge { get; set; }
    /// <summary>Слово «яч.» на языке кассы.</summary>
    public static Func<string>? CellLabel { get; set; }

    public static bool Active => IsActive?.Invoke() ?? false;

    /// <summary>Данные запчасти по id товара — только в сфере «Автозапчасти».</summary>
    public static AutoPartInfo? Of(string? productId) =>
        string.IsNullOrEmpty(productId) || !Active ? null : Lookup?.Invoke(productId);
}
