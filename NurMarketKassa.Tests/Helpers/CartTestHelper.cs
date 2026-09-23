using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Helpers;

internal static class CartTestHelper
{
    public static void StartEmptyCart(ICartService cart)
    {
        var root = new JsonObject
        {
            ["items"] = new JsonArray(),
            ["is_staging"] = true,
        };
        using var doc = JsonDocument.Parse(root.ToJsonString());
        cart.SetCart(doc.RootElement);
    }

    public static CatalogProductTileVm CreateProduct(
        string id,
        string title,
        decimal price,
        bool mustWeigh = false)
    {
        var vm = new CatalogProductTileVm(id, title, $"{price:0.00} сом", mustWeigh)
        {
            Unit = mustWeigh ? "кг" : "шт",
            Quantity = 100,
        };
        ProductUnitNormalizer.ApplyToTile(vm);
        return vm;
    }
}
