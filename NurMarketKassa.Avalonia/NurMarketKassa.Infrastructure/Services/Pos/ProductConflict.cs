using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (С7, «Синхронизация и интеграция»): предупреждение о конфликте правок карточки товара. У сервера нет If-Match для карточки, поэтому касса сама сравнивает карточку,
/// прочитанную при открытии окна, со свежей перед записью — но ТОЛЬКО по полям, которые владелец сейчас правит. Остаток сюда не входит (его сверяет отдельное окно «Остаток изменился»),
/// а <c>updated_at</c> сам по себе не повод: сервер двигает его при каждой продаже. Чужая правка другого поля не мешает — PATCH шлёт только изменённое.
/// </summary>
public static class ProductConflict
{
    /// <summary>Поле карточки, изменённое на другом устройстве: ключ ответа сервера, значение при открытии окна и сейчас (пустая строка — поля нет).</summary>
    public sealed record Diff(string Key, string Opened, string Fresh);

    /// <summary>Какие поля уйдут в запросе (по флагам «менялось») — в ключах ответа сервера.</summary>
    public static List<string> EditedKeys(bool name, bool price, bool purchasePrice, bool wholesalePrice, bool description, bool location, bool attributes)
    {
        var list = new List<string>();
        if (name) list.Add("name");
        if (price) list.Add("price");
        if (purchasePrice) list.Add("purchase_price");
        if (wholesalePrice) list.Add("wholesale_price");
        if (description) list.Add("description");
        if (location) list.Add("storage_location");
        if (attributes) list.Add("attributes");
        return list;
    }

    /// <summary>Поля из <paramref name="editedKeys"/>, у которых значение в свежей карточке не совпадает с карточкой при открытии окна. Нет карточки при открытии — конфликта нет.</summary>
    public static List<Diff> Changed(JsonElement opened, JsonElement fresh, IEnumerable<string> editedKeys)
    {
        var result = new List<Diff>();
        if (opened.ValueKind != JsonValueKind.Object || fresh.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var key in editedKeys.Distinct(StringComparer.Ordinal))
        {
            var a = Norm(opened, key);
            var b = Norm(fresh, key);
            if (!string.Equals(a, b, StringComparison.Ordinal))
                result.Add(new Diff(key, a, b));
        }
        return result;
    }

    /// <summary>Значение поля для сравнения: число — как decimal («100.00» = «100»), строка — обрезанная, массив/объект — как есть; нет поля / null — пусто.</summary>
    public static string Norm(JsonElement card, string key)
    {
        if (!card.TryGetProperty(key, out var v))
            return "";
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "";
            case JsonValueKind.Number:
                return decimal.TryParse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("0.########", CultureInfo.InvariantCulture) : v.GetRawText();
            case JsonValueKind.String:
                var s = (v.GetString() ?? "").Trim();
                return key is "price" or "purchase_price" or "wholesale_price" && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("0.########", CultureInfo.InvariantCulture)
                    : s;
            default:
                return v.GetRawText();
        }
    }
}
