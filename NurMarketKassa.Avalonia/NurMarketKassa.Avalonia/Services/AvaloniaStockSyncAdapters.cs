using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

public sealed class AvaloniaServerStockGateway : IServerStockGateway
{
    private readonly ICatalogApiService _catalogApi;

    public AvaloniaServerStockGateway(ICatalogApiService catalogApi) => _catalogApi = catalogApi;

    public async Task<double?> GetServerStockAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        try
        {
            var detail = await _catalogApi.ProductsDetailAsync(productId, cancellationToken)
                .ConfigureAwait(false);
            if (detail is { } element)
                return StockSyncService.ResolveStockQuantity(
                    element,
                    CartDisplayHelper.ProductMustWeigh(element));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product stock detail failed, using agent list: {ex}", "WARNING");
        }

        var products = await _catalogApi.GetAgentProductsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var element in products)
        {
            if (!TryReadId(element, out var id) || !string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
                continue;
            return StockSyncService.ResolveStockQuantity(
                element,
                CartDisplayHelper.ProductMustWeigh(element));
        }
        return null;
    }

    public Task<bool> TrySetServerStockAsync(
        string productId,
        double quantity,
        CancellationToken cancellationToken = default) =>
        _catalogApi.TrySetProductStockAsync(productId, quantity, cancellationToken);

    private static bool TryReadId(JsonElement element, out string id)
    {
        id = string.Empty;
        if (!element.TryGetProperty("id", out var value))
            return false;
        id = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString() ?? string.Empty;
        return id.Length > 0;
    }
}

public sealed class AvaloniaLocalStockAdapter : ILocalStockProvider, IStockCatalogUpdater
{
    public double GetExpectedQuantity(string productId) =>
        LocalProductRepository.Instance.TryGetTileById(productId)?.Quantity ?? 0;

    public void UpdateCatalogStock(string productId, double quantity)
    {
        var existing = LocalProductRepository.Instance.TryGetTileById(productId);
        LocalProductRepository.Instance.UpdateStock(productId, quantity, existing?.MustWeigh == true);
    }
}
