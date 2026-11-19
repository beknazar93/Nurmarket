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
            body["attributes"] = attrs.Select(a =>
            {
                var item = new Dictionary<string, object?> { ["key"] = a.Key, ["value"] = a.Value };
                if (!string.IsNullOrWhiteSpace(a.Unit))
                    item["unit"] = a.Unit;
                return item;
            }).ToArray();
        }
        if (r.SendStorageLocation)
            body["storage_location"] = r.StorageLocation?.Trim() ?? "";
        if (r.RelatedInput is { } related)
            body["related_input"] = related.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Take(100).ToArray();
    }
}
