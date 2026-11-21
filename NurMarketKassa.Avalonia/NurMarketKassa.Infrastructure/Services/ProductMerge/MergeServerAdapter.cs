using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.ProductMerge;

/// <summary>2026-11-21: настоящий путь слияния к серверу NurCRM — через уже существующие методы кассы: свежая карточка (ProductsDetailAsync), PATCH полей (PatchProductFieldsAsync),
/// акт остатков (inventory/sessions + apply — других документов прихода/списания «на разницу» у сервера нет, см. ProductActions), архив (product_archive).
/// 2026-11-21 (ответ Умара 21.11): серверное слияние POST main/products/merge/ есть — основной путь (<see cref="ProductMergeApi"/>); карточки/акт остатков/архив — запасной ручной путь при 404/405.</summary>
public sealed class MergeServerAdapter(ICatalogApiService catalog, IInventoryApiService inventory, bool canArchive, bool costingByBatches, ProductMergeApi? merge = null) : IProductMergeServer
{
    public bool CanArchive => canArchive;

    public bool CostingByBatches => costingByBatches;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Object => v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            _ => null,
        } : null;

    private static double? Num(JsonElement e, string name) =>
        Str(e, name) is { Length: > 0 } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Карточка сервера → <see cref="MergeCard"/> (общий разбор: и для чтения перед слиянием, и в проверках).</summary>
    public static MergeCard ParseCard(JsonElement p)
    {
        var alt = new List<MergeAltCode>();
        if (p.TryGetProperty("alternate_barcodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } code)
                    alt.Add(new MergeAltCode(code.Trim(), null, 0));
                else if (a.ValueKind == JsonValueKind.Object && Str(a, "barcode") is { Length: > 0 } c2)
                    alt.Add(new MergeAltCode(c2.Trim(), Str(a, "name"), Num(a, "quantity") ?? 0));
            }
        var images = new List<string>();
        if (p.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            foreach (var i in imgs.EnumerateArray())
            {
                var url = i.ValueKind == JsonValueKind.String ? i.GetString() : Str(i, "image") ?? Str(i, "url") ?? Str(i, "file") ?? Str(i, "src");
                if (!string.IsNullOrWhiteSpace(url))
                    images.Add(url!);
            }
        var weigh = p.TryGetProperty("is_weight", out var iw) && iw.ValueKind == JsonValueKind.True;
        return new MergeCard(Str(p, "id") ?? "", Str(p, "name") ?? "", Str(p, "barcode"), alt, StockSyncService.ResolveStockQuantity(p, weigh),
            Num(p, "price"), Num(p, "purchase_price"), Str(p, "category") ?? Str(p, "category_name"), Str(p, "brand") ?? Str(p, "brand_name"), Str(p, "unit"), images, ProductCatalogMapper.IsArchived(p));
    }

    public async Task<MergeCard?> ReadCardAsync(string id, CancellationToken ct)
    {
        var el = await catalog.ProductsDetailAsync(id, ct).ConfigureAwait(false);
        if (el is not { ValueKind: JsonValueKind.Object } p)
            return null;
        var card = ParseCard(p);
        return card.Archived ? null : card with { Id = string.IsNullOrEmpty(card.Id) ? id : card.Id };
    }

    public Task PatchAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct) =>
        catalog.PatchProductFieldsAsync(id, fields, ct);

    public async Task SetStocksAsync(string note, IReadOnlyList<(string Id, double Total)> targets, CancellationToken ct)
    {
        var items = targets.Select(t => new InventorySessionItem(t.Id, InventoryApiService.RoundForServer(t.Total))).ToList();
        var sessionId = await inventory.CreateSessionAsync(note, items, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("сервер не создал акт остатков");
        try
        {
            // черновик уже на сервере — проводим без отмены: отмена между «создать» и «провести» оставила бы непроведённый акт
            await inventory.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && PendingIdempotencyKey.IsOutcomeUnknown(ex))
        {
            // обрыв связи на «провести»: смотрим, проведён ли акт
            InventorySessionInfo? info = null;
            try { info = await inventory.GetSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* проверить не удалось — исход неизвестен */ }
            if (info is { IsApplied: true })
                return;
            throw new MergeOutcomeUnknownException($"акт остатков {sessionId}: связь оборвалась на проведении");
        }
    }

    public Task ArchiveAsync(string id, CancellationToken ct) => catalog.ArchiveProductAsync(id, ct);

    public Task UnarchiveAsync(string id, CancellationToken ct) => catalog.UnarchiveProductAsync(id, ct);

    public bool? ServerMergeSupported => ProductMergeApi.Supported;

    public Task<ServerMergeResponse> ServerMergeAsync(string targetId, IReadOnlyList<string> sourceIds, IReadOnlyDictionary<string, string> variantNames, bool dryRun, string? idempotencyKey, CancellationToken ct) =>
        merge is null ? Task.FromResult(ServerMergeResponse.Unavailable) : merge.MergeAsync(targetId, sourceIds, variantNames, dryRun, idempotencyKey, ct);

    public Task<ServerMergeResponse> ServerUndoAsync(string mergeId, CancellationToken ct) =>
        merge is null ? Task.FromResult(ServerMergeResponse.Unavailable) : merge.UndoAsync(mergeId, ct);
}
