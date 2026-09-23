using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Принудительное обновление признаков товара (весовой/штучный) при синхронизации с сайтом.</summary>
public static class CatalogProductClassifier
{
    public static void ReclassifyTiles(
        List<CatalogProductTileVm> kgList,
        List<CatalogProductTileVm> pieceList)
    {
        var all = kgList.Concat(pieceList).DistinctBy(x => x.Id).ToList();
        SplitIntoCatalogLists(all, kgList, pieceList);
    }

    public static void SplitIntoCatalogLists(
        IEnumerable<CatalogProductTileVm> source,
        List<CatalogProductTileVm> kgList,
        List<CatalogProductTileVm> pieceList)
    {
        kgList.Clear();
        pieceList.Clear();

        foreach (var vm in source)
        {
            if (!ProductUnitNormalizer.TryPrepareCatalogTile(vm))
                continue;

            if (vm.MustWeigh)
                kgList.Add(vm);
            else
                pieceList.Add(vm);
        }
    }
}
