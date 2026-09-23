using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис каталога: товары, остатки, версии каталога и синхронизация.
/// </summary>
public interface ICatalogApiService
{
    /// <summary>GET список товаров агента с остатками (для склада).</summary>
    Task<List<JsonElement>> GetAgentProductsAsync(CancellationToken ct = default);

    /// <summary>Синхронизация статуса «избранный» с сайтом.</summary>
    Task<bool> SetProductFavoriteAsync(string productId, bool isFavorite, CancellationToken ct = default);

    /// <summary>Поиск товаров по названию (быстрый, через потоковый парсинг).</summary>
    Task<List<ProductDto>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default);

    /// <summary>Лёгкая проверка версии каталога без полной загрузки SKU.</summary>
    Task<CatalogVersionInfo?> ProductsCatalogVersionAsync(CancellationToken ct = default);

    /// <summary>Полный каталог с пагинацией (все SKU, до limit).</summary>
    Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default);

    /// <summary>Карточка товара с картинками (как products_detail).</summary>
    Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default);

    /// <summary>Обновление остатка товара на сервере (перебор типовых путей/полей).</summary>
    Task<bool> TrySetProductStockAsync(string productId, double quantity, CancellationToken ct = default);
}
