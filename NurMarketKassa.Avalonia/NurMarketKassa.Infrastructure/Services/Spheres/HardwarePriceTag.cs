using System.Globalization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-12 (ХМ7, ТЗ хозмаг 3.5): тексты ценника «Хозтовары» — цена за единицу («520,00 сом/м²») и цена за упаковку
/// («748,80 сом/уп 1,44 м²»). Упаковка берётся из карточки товара (поле packages, количество в упаковке — для дробных единиц м, м², л, кг…);
/// нет её — из названия («Плитка 30×60, 1,44 м²»); нет и там — только цена за единицу. Чистая логика, без печати.
/// </summary>
public static class HardwarePriceTag
{
    /// <summary>Цена за единицу, цена за упаковку (null — упаковки нет) и подпись упаковки («уп 1,44 м²»).</summary>
    public readonly record struct Texts(string UnitPrice, string? PackPrice, string? PackLabel);

    public static Texts Build(CatalogProductTileVm product)
    {
        var parsed = BarcodePrintCatalog.ParsePrice(product.PriceLine);
        var unit = string.IsNullOrWhiteSpace(product.Unit) ? "" : UnitCatalog.Display(product.Unit);
        var unitPrice = BarcodePrintCatalog.LabelPriceText(product.PriceLine) + (unit.Length > 0 ? "/" + unit : "");
        if (parsed is not { } price || price.Value <= 0m || PackSize(product) is not { } pack)
            return new Texts(unitPrice, null, null);

        var suffix = price.Suffix.Length > 0 ? price.Suffix : "сом";
        var packTotal = Math.Round(price.Value * pack.Quantity, 2, MidpointRounding.AwayFromZero);
        var quantityText = pack.Quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace(".", BarcodePrintCatalog.LabelDecimalSeparator);
        var label = Tr.T("уп", "таңгак", "pack", "paket", "qadoq") + " " + quantityText + (unit.Length > 0 ? " " + unit : "");
        return new Texts(unitPrice, BarcodePrintCatalog.LabelAmount(packTotal) + " " + suffix + "/" + label, label);
    }

    /// <summary>Сколько базовых единиц в упаковке: из packages карточки (только дробные единицы) или из названия; нет — null.</summary>
    internal static (decimal Quantity, string? Name)? PackSize(CatalogProductTileVm product)
    {
        var unit = HardwareProfile.FindUnit(product.Unit);
        if (unit is null || !unit.Fractional)
            return null;
        if (product.PieceOption is { QuantityInPackage: > 0 } option && option.QuantityInPackage <= 1_000_000d)
            return ((decimal)option.QuantityInPackage, option.Name);
        var kind = unit.Code switch
        {
            "m2" => (CalcKind?)CalcKind.Tile,
            "l" => CalcKind.Paint,
            "kg" => CalcKind.Mix,
            _ => null,
        };
        return kind is { } k && ConsumptionCalculator.GuessPackSize(k, product.Title) is { } guessed ? (guessed, null) : null;
    }
}
