using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Contracts;

public interface IProductCatalogLookup
{
    CatalogProductInfo? FindByBarcode(string barcode);

    CatalogProductInfo? FindByEmbeddedCode(string embeddedProductCode);
}
