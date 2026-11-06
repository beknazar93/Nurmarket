using System.Globalization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-09-09: сохранение/удаление товара напрямую в локальную SQLite (
/// LocalProductRepository) — используется вместо ICatalogApiService.CreateProductAsync/
/// UpdateProductAsync/DeleteProductAsync, когда OfflineModeHelper.UseLocalOperations (обычный
/// офлайн-режим ИЛИ активированный автономный режим — оба используют один и тот же флаг
/// PosApp.IsOfflineBootstrap). Товар, добавленный/изменённый так, никогда не попадёт на сервер —
/// это ожидаемо: автономный режим принципиально работает без NurCRM.</summary>
public static class LocalProductEditor
{
    /// <summary>Как ICatalogApiService.GetWeightProductCountAsync, но по локальной базе — для
    /// автогенерации PLU нового весового товара офлайн (см. ProductEditDialog).</summary>
    public static int GetLocalWeightProductCount() =>
        LocalProductRepository.Instance.LoadAllTiles().Count(t => t.MustWeigh);

    /// <summary>Возвращает id сохранённого товара (новый — сгенерированный локально GUID,
    /// существующий — тот же, что был).</summary>
    /// <param name="serverCard">2026-11-06 (класс Б, Б-4): ответ сервера на создание товара — из него берутся поля, которых нет в запросе (код товара, фото, признак is_weight).
    /// null — офлайн или временный товар.</param>
    public static string SaveLocally(ProductEditRequest request, string? existingId, System.Text.Json.JsonElement? serverCard = null)
    {
        var id = existingId ?? Guid.NewGuid().ToString("N");
        var priceLine = request.Price is { } price
            ? $"{price.ToString("0.00", CultureInfo.InvariantCulture)} сом"
            : "—";

        // 2026-11-06 (класс Б, Б-4): плитка строилась только из запроса и теряла единицу («м»/«л» → «шт»), вид (услуга → товар с остатком), код товара и фото.
        // Теперь: единица и вид — из запроса; код, фото и признак is_weight — из записи в базе или ответа сервера.
        var previous = existingId is null ? null : LocalProductRepository.Instance.LoadTileCopy(existingId);
        var fromServer = serverCard is { ValueKind: System.Text.Json.JsonValueKind.Object } card
            ? ProductCatalogMapper.TryTile(card, PosApp.Settings?.ApiBaseUrl ?? "")
            : null;

        var tile = new CatalogProductTileVm(id, request.Name, priceLine, request.IsWeight)
        {
            Unit = string.IsNullOrWhiteSpace(request.Unit) ? "шт" : request.Unit,
            Kind = string.IsNullOrWhiteSpace(request.Kind) ? previous?.Kind : request.Kind,
            ProductCode = previous?.ProductCode ?? fromServer?.ProductCode,
            ImageUrl = previous?.ImageUrl ?? fromServer?.ImageUrl,
            ServerIsWeight = fromServer?.ServerIsWeight ?? (previous is not null && previous.MustWeigh == request.IsWeight ? previous.ServerIsWeight : null),
            Category = request.CategoryName,
            Brand = request.BrandName,
            Barcode = request.Barcode,
            AlternateBarcodesRaw = string.IsNullOrWhiteSpace(request.AlternateBarcodes) ? null : request.AlternateBarcodes,
            // Названия/количества доп. штрихкодов, известные с прошлой загрузки карточки —
            // офлайн-режим никогда не уходит на сервер, поэтому просто переносим их как есть
            // (см. KnownAlternateBarcodeVariants в ProductEditDialog/CatalogApiService.BuildAlternateBarcodes).
            AlternateBarcodeVariants = request.KnownAlternateBarcodeVariants,
            Quantity = request.Quantity,
            PurchasePrice = request.PurchasePrice ?? 0,
            MarkupPercent = request.MarkupPercent ?? 0,
            WholesalePrice = request.WholesalePrice ?? 0,
            DiscountPercent = request.DiscountPercent ?? 0,
            Description = request.Description,
            HotkeyGroup = request.HotkeyGroup,
            Plu = request.Plu ?? (request.IsWeight ? fromServer?.Plu : null),
            Country = request.Country,
            WeightKg = request.WeightKg,
            MinQuantity = request.MinimumQuantity is > 0 ? request.MinimumQuantity : fromServer?.MinQuantity, // 2026-11-06 (В12, Б-8)
            Article = request.Article,
            IsBundle = string.Equals(request.Kind, "bundle", StringComparison.OrdinalIgnoreCase),
            BundleItems = string.Equals(request.Kind, "bundle", StringComparison.OrdinalIgnoreCase)
                ? request.BundleItems
                : null,
            // 2026-09-12: раньше это поле тут вообще не заполнялось — "Поштучная продажа" из
            // карточки товара (EnablePieceSale/PackageQuantity/PackagePiecePrice) молча
            // терялась при сохранении офлайн, ProductCatalogMapper строит PieceOption только из
            // серверного JSON ("packages"), которого в офлайне никогда не будет.
            PieceOption = request.EnablePieceSale
                && request.PackageQuantity is > 0
                && request.PackagePiecePrice is > 0
                ? new ProductPackageOption
                {
                    QuantityInPackage = request.PackageQuantity.Value,
                    PieceUnitPrice = request.PackagePiecePrice.Value,
                    Unit = request.Unit,
                }
                : null,
        };

        LocalProductRepository.Instance.UpsertFromTiles([tile]);
        RefreshInMemoryCache();
        return id;
    }

    public static void DeleteLocally(string productId)
    {
        LocalProductRepository.Instance.DeleteProduct(productId);
        RefreshInMemoryCache();
    }

    private static void RefreshInMemoryCache()
    {
        CatalogCacheService.LoadFromDatabase();
        CatalogCacheService.NotifyCatalogChanged();
    }
}
