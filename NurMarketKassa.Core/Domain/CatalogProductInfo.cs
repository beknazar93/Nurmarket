namespace NurMarketKassa.Core.Domain;

public sealed record CatalogProductInfo(
    string Id,
    string Title,
    bool MustWeigh,
    string? Barcode,
    double Quantity = 0);
