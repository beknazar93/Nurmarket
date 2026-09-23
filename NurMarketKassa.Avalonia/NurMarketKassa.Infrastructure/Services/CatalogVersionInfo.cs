namespace NurMarketKassa.Services;

/// <summary>Маркер версии каталога с сервера (без полной загрузки SKU).</summary>
public sealed class CatalogVersionInfo
{
    public string Token { get; init; } = "";

    public long? CatalogVersion { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string Source { get; init; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Token);
}
