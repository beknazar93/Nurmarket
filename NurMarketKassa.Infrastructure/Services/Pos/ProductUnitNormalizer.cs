using System.Globalization;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

public enum ProductUnitKind
{
    Kilogram,
    Piece
}

/// <summary>Нормализация единиц измерения: «кг» (весовые) и «шт» (штучные); некорректные единицы не скрываются.</summary>
public static class ProductUnitNormalizer
{
    private static readonly HashSet<string> PerfectUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "кг", "kg", "шт", "sht"
    };

    public static bool IsPerfectUnit(string? rawUnit)
    {
        var unit = (rawUnit ?? string.Empty).Trim().ToLowerInvariant();
        return PerfectUnits.Contains(unit);
    }

    /// <summary>Вкладка «Весовые (кг)»: единица «кг»/«kg» или флаг MustWeigh.</summary>
    public static bool IsWeightCatalogTab(string? rawUnit, bool mustWeigh)
    {
        var unit = (rawUnit ?? string.Empty).Trim().ToLowerInvariant();
        return unit is "кг" or "kg" || mustWeigh;
    }

    /// <summary>Товар продаётся на вес — открываем диалог взвешивания вместо мгновенного добавления в чек.</summary>
    public static bool RequiresWeighing(CatalogProductTileVm product)
    {
        if (product.MustWeigh)
            return true;

        var unit = (product.Unit ?? string.Empty).Trim().ToLowerInvariant();
        return unit is "кг" or "kg" or "г" or "g";
    }

    public static ProductUnitKind Classify(string? rawUnit, bool apiMustWeigh = false) =>
        IsWeightCatalogTab(rawUnit, apiMustWeigh)
            ? ProductUnitKind.Kilogram
            : ProductUnitKind.Piece;

    public static string DisplayUnit(ProductUnitKind kind) =>
        kind == ProductUnitKind.Kilogram ? "кг" : "шт";

    /// <summary>Строгая классификация: только «кг»/kg и «шт»/sht; мусорные значения отклоняются.</summary>
    public static bool TryClassifyStrict(string? rawUnit, bool apiMustWeigh, out ProductUnitKind kind)
    {
        if (IsPerfectUnit(rawUnit))
        {
            var unit = (rawUnit ?? string.Empty).Trim().ToLowerInvariant();
            kind = unit is "кг" or "kg" ? ProductUnitKind.Kilogram : ProductUnitKind.Piece;
            return true;
        }

        if (string.IsNullOrEmpty((rawUnit ?? string.Empty).Trim()) && apiMustWeigh)
        {
            kind = ProductUnitKind.Kilogram;
            return true;
        }

        kind = default;
        return false;
    }

    public static void ApplyToTile(CatalogProductTileVm vm) => TryPrepareCatalogTile(vm);

    /// <summary>Подготавливает товар для отображения в каталоге; false — только при пустом Id/Title.</summary>
    public static bool TryPrepareCatalogTile(CatalogProductTileVm vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Id) || string.IsNullOrWhiteSpace(vm.Title))
            return false;

        var rawUnit = (vm.Unit ?? string.Empty).Trim();
        var isWeightTab = IsWeightCatalogTab(rawUnit, vm.MustWeigh);

        vm.IsUnitInvalid = !IsPerfectUnit(rawUnit);
        vm.MustWeigh = isWeightTab;

        if (vm.IsUnitInvalid && !string.IsNullOrEmpty(rawUnit))
        {
            vm.Unit = rawUnit;
            ApplyStockWithRawUnit(vm, rawUnit, isWeightTab);
        }
        else
        {
            var kind = isWeightTab ? ProductUnitKind.Kilogram : ProductUnitKind.Piece;
            vm.Unit = DisplayUnit(kind);
            StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, isWeightTab);
        }

        return true;
    }

    private static void ApplyStockWithRawUnit(CatalogProductTileVm vm, string unitDisplay, bool isWeightTab)
    {
        var qtyCulture = CultureInfo.GetCultureInfo("ru-RU");
        var qtyStr = isWeightTab
            ? vm.Quantity.ToString("F2", qtyCulture)
            : vm.Quantity.ToString("F0", qtyCulture);
        vm.StockInfo = $"{qtyStr} {unitDisplay}";
        vm.IsLowStock = vm.Quantity < StockSyncService.LowStockThreshold;
    }
}
