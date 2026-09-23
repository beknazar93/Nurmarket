using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Helpers;

internal static class CatalogTestHelper
{
    public static CatalogProductTileVm CreateTile(
        string id,
        string title,
        string barcode,
        double price,
        bool mustWeigh = false)
    {
        var vm = new CatalogProductTileVm(id, title, $"{price:0.00} сом", mustWeigh)
        {
            Barcode = barcode,
            Unit = mustWeigh ? "кг" : "шт",
            Quantity = 10,
        };
        ProductUnitNormalizer.ApplyToTile(vm);
        return vm;
    }
}
