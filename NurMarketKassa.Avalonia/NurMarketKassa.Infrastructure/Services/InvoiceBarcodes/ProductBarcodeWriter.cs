using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Итог записи доп. штрихкода: Ok — записан (или уже был); иначе <see cref="Message"/> — что сказать, <see cref="Duplicate"/> — кто владеет кодом (409 barcode_duplicate).</summary>
public sealed record BarcodeWriteOutcome(bool Ok, string Message = "", ServerBarcodeOwner? Duplicate = null, bool UsedFallback = false);

/// <summary>2026-11-21 (ответ Умара: в бою POST/DELETE products/&lt;id&gt;/barcodes/): запись ОДНОГО доп. штрихкода товара склада, не трогая остальные. Версия карточки (If-Match) — если известна;
/// 409 version_conflict — карточку перечитываем (новая версия) и повторяем один раз. Старый сервер (404/405) — прежний путь: прочитать карточку, слить список, PATCH alternate_barcodes целиком.
/// 409 barcode_duplicate разбирается здесь же (ServerBarcodeErrors) — окна показывают «код уже у товара «…»» без поиска по каталогу.</summary>
public static class ProductBarcodeWriter
{
    /// <summary>Добавить один доп. штрихкод. Повтор того же кода сервер принимает без изменений (200).</summary>
    public static async Task<BarcodeWriteOutcome> AddAsync(ICatalogApiService api, string productId, AlternateBarcodeVariant variant, string? version, CancellationToken ct = default)
    {
        var current = version;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await api.AddProductBarcodeAsync(productId, variant.Barcode, variant.Name, InvoiceBarcodeRules.ServerQuantity(variant.Quantity), current, ct).ConfigureAwait(false);
                return new BarcodeWriteOutcome(true);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                return await FallbackAsync(api, productId, alts => InvoiceBarcodeRules.MergeVariants(alts, variant), ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (attempt == 0 && ServerBarcodeErrors.IsVersionConflict(ex))
            {
                current = await FreshVersionAsync(api, productId, ct).ConfigureAwait(false); // перечитали карточку — одна повторная попытка
            }
            catch (ApiException ex)
            {
                return Failed(ex);
            }
        }
    }

    /// <summary>Убрать один доп. штрихкод (код, которого нет, сервер игнорирует — 200/404 без ошибки для кассы не различаем: 404 уходит в прежний путь).</summary>
    public static async Task<BarcodeWriteOutcome> RemoveAsync(ICatalogApiService api, string productId, string barcode, string? version, CancellationToken ct = default)
    {
        var current = version;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await api.DeleteProductBarcodeAsync(productId, barcode, current, ct).ConfigureAwait(false);
                return new BarcodeWriteOutcome(true);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                return await FallbackAsync(api, productId, alts => alts.Where(a => !InvoiceBarcodeRules.Same(a.Barcode, barcode)).ToList(), ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (attempt == 0 && ServerBarcodeErrors.IsVersionConflict(ex))
            {
                current = await FreshVersionAsync(api, productId, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                return Failed(ex);
            }
        }
    }

    /// <summary>Свежая версия карточки с сервера; null — не прочиталась или сервер версию не отдаёт (повтор уйдёт без If-Match).</summary>
    private static async Task<string?> FreshVersionAsync(ICatalogApiService api, string productId, CancellationToken ct)
    {
        try
        {
            return await api.ProductsDetailAsync(productId, ct).ConfigureAwait(false) is { } card ? ServerBarcodeErrors.VersionOf(card) : null;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>Старый сервер: карточка → изменённый список → PATCH целиком (прежнее поведение).</summary>
    private static async Task<BarcodeWriteOutcome> FallbackAsync(ICatalogApiService api, string productId, Func<List<AlternateBarcodeVariant>, List<AlternateBarcodeVariant>> change, CancellationToken ct)
    {
        try
        {
            if (await api.ProductsDetailAsync(productId, ct).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Object } card)
                return new BarcodeWriteOutcome(false, Tr.T("Карточка товара не прочиталась с сервера — код не записан.", "Товардын карточкасы серверден окулган жок — код жазылган жок.",
                    "The product card could not be read from the server — the code was not written.", "Ürün kartı sunucudan okunamadı — kod yazılmadı.", "Mahsulot kartochkasi serverdan o'qilmadi — kod yozilmadi."), UsedFallback: true);
            var list = change(InvoiceBarcodeBinder.ParseAlternates(card));
            await api.PatchProductFieldsAsync(productId, new Dictionary<string, object?> { ["alternate_barcodes"] = InvoiceBarcodeRules.ToServerList(list) }, ct).ConfigureAwait(false);
            return new BarcodeWriteOutcome(true, UsedFallback: true);
        }
        catch (ApiException ex)
        {
            return Failed(ex) with { UsedFallback = true };
        }
    }

    private static BarcodeWriteOutcome Failed(ApiException ex) =>
        ServerBarcodeErrors.TryDuplicate(ex) is { } dup
            ? new BarcodeWriteOutcome(false, ServerBarcodeErrors.DuplicateText(dup), dup)
            : new BarcodeWriteOutcome(false, ex.Message);
}
