using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ4): поля запчасти в теле создания / PATCH товара. Добавляются ТОЛЬКО когда окно товара в сфере «Автозапчасти» их заполнило
/// (<see cref="ProductEditRequest.Attributes"/>, <see cref="ProductEditRequest.SendStorageLocation"/>, <see cref="ProductEditRequest.RelatedInput"/>); у остальных сфер и при
/// пустом блоке тело не меняется ни на байт.
/// </summary>
internal static class PartMetaBody
{
    public static void Apply(Dictionary<string, object?> body, ProductEditRequest r)
    {
        if (r.Attributes is { } attrs)
        {
            // 2026-11-20 (ответ Умара 20.11, А.4): весь массив целиком; ключ — латиница до 32, значение — строка до 64 или число (флаги 1/0, не true/false), не больше 30 — см. AutoPartAttrWire.
            body["attributes"] = NurMarketKassa.Models.Pos.AutoPartAttrWire.ToWire(attrs);
        }
        if (r.SendStorageLocation)
            body["storage_location"] = r.StorageLocation?.Trim() ?? "";
        if (r.RelatedInput is { } related)
            // 2026-11-20 (живая проверка «Автозапчастей»): сервер требует объекты {product, kind}, а не голые id («Связь должна быть объектом {product, kind, ratio?, ratio_unit?, sort_order?}») —
            // раньше карточка с аналогами не сохранялась вовсе. Аналог товара — kind "analog" (accessory|analog, ТЗ бэкенду 26.10).
            body["related_input"] = related.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Take(100)
                .Select((id, i) => new Dictionary<string, object?> { ["product"] = id, ["kind"] = "analog", ["sort_order"] = i }).ToArray();
    }
}
