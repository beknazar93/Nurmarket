
#nullable disable
namespace NurMarketKassa.Configuration;

public sealed class CatalogUiSettings
{
    /// <summary>Максимум товаров при полной синхронизации каталога (без искусственного лимита 120).</summary>
    public int QuickCatalogLimit { get; init; } = int.MaxValue;

    /// <summary>Максимум страниц API при синхронизации каталога.</summary>
    public int CatalogMaxPages { get; init; } = 10_000;

    public int SearchLimit { get; init; } = 40;

    public int SearchDebounceMs { get; init; } = 380;
}
