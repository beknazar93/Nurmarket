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

    /// <summary>Единица, которую кладём в локальную базу. «кг»/«шт» и их латинские варианты
    /// приводятся к виду кассы, а всё остальное с сервера — «м», «л», «оп», «уп» — сохраняется
    /// как есть. 2026-09-24, живой баг: «1м пишет как 1шт» — плитка каталога показывала
    /// единицу сервера, но в базу уходило DisplayUnit(kind), то есть «шт», и после первого же
    /// перечитывания базы метры превращались в штуки.</summary>
    public static string StorageUnit(string? rawUnit, ProductUnitKind kind)
    {
        var unit = (rawUnit ?? string.Empty).Trim();
        if (kind == ProductUnitKind.Kilogram || unit.Length == 0 || IsPerfectUnit(unit))
            return DisplayUnit(kind);
        return unit;
    }

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

    /// <summary>Подготавливает товар для отображения в каталоге; false — при пустом Id/Title или нераспознанной цене.</summary>
    public static bool TryPrepareCatalogTile(CatalogProductTileVm vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Id) || string.IsNullOrWhiteSpace(vm.Title))
            return false;

        // Товар без распознаваемой цены («—») нельзя пускать в продаваемый каталог:
        // ниже по потоку ParsePrice дал бы 0 и товар ушёл бы бесплатно, а нулевая цена
        // ещё и попала бы в локальную БД.
        if (!HasSellablePrice(vm.PriceLine))
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

    /// <summary>Цена карточки распознаётся так же, как в ParsePrice потребителей каталога.</summary>
    private static bool HasSellablePrice(string? priceLine)
    {
        if (string.IsNullOrWhiteSpace(priceLine))
            return false;

        var digits = new string(priceLine.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray())
            .Replace(',', '.');

        // Отбраковываем только нераспознанную цену: цена 0, пришедшая с сервера явно,
        // остаётся легитимной карточкой.
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
               && double.IsFinite(price)
               && price >= 0;
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
