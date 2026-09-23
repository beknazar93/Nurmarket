using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.ViewModels.Scanning;

public sealed class ScannedProductFoundEventArgs
{
    public ScannedProductFoundEventArgs(
        CatalogProductInfo product,
        string originalBarcode,
        string resolvedProductCode,
        bool isWeighedBarcode,
        decimal? weightKg)
    {
        Product = product;
        OriginalBarcode = originalBarcode;
        ResolvedProductCode = resolvedProductCode;
        IsWeighedBarcode = isWeighedBarcode;
        WeightKg = weightKg;
    }

    public CatalogProductInfo Product { get; }

    public string OriginalBarcode { get; }

    public string ResolvedProductCode { get; }

    public bool IsWeighedBarcode { get; }

    public decimal? WeightKg { get; }
}
