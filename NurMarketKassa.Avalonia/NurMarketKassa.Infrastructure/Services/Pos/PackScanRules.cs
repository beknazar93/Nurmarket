using System.Globalization;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-12 (ХМ3, ТЗ хозмаг): «скан коробки = N штук». Дополнительный штрихкод товара с количеством N («Кол-во в упаковке» в карточке:
/// коробка саморезов ×200) при скане добавляет в чек N базовых единиц одной строкой; повторный скан прибавляет ещё N.
/// Действует ТОЛЬКО в сфере «Хозтовары» (профиль с <see cref="SphereProfile.PackScan"/>) и только если владелец включил галочку
/// «Скан коробки добавляет все штуки» (<see cref="SphereSettings.GetHardwarePackScan"/>, по умолчанию у перешедших магазинов выкл, В-Х4).
/// Во всех остальных случаях возвращается 0 — скан работает как раньше (+1 и имя варианта). Чистая логика, корзину не трогает.
/// </summary>
public static class PackScanRules
{
    /// <summary>Разумный предел количества в одной коробке (защита от опечатки «2000000» в карточке).</summary>
    public const double MaxPackQuantity = 100_000d;

    /// <summary>Галочка включена и сфера подходит.</summary>
    public static bool Enabled =>
        MarketSpheres.IsHardware && MarketSpheres.Profile.PackScan && SphereSettings.GetHardwarePackScan();

    /// <summary>Подпись коробки по отсканированному коду («Коробка ×200») или null — скан обычный. N — сколько базовых единиц добавить.</summary>
    public static AlternateBarcodeVariant? Find(CatalogProductTileVm product, string? scannedBarcode, out double quantity)
    {
        quantity = 0;
        if (!Enabled || product is null || product.MustWeigh || product.HasPieceOption || string.IsNullOrWhiteSpace(scannedBarcode))
            return null;
        // 2026-11-13 (ответ Умара, строймаркет): у товара есть серверные упаковки (sale_units) — они считают остаток на сервере (qty × factor); местное «коробка = N штук» не включаем, чтобы не пересчитать дважды.
        if (ProductSaleUnits.Has(product.Id))
            return null;
        var code = scannedBarcode.Trim();
        // Основной штрихкод — это одна единица товара, а не коробка.
        if (string.Equals(product.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
            return null;
        var variant = product.AlternateBarcodeVariants?.FirstOrDefault(v =>
            string.Equals(v.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
        if (variant is null)
            return null;
        var n = variant.Quantity;
        if (!double.IsFinite(n) || n <= 1d || n > MaxPackQuantity)
            return null;
        // Дробное N (мешок 25,5 кг) — только когда дробное количество включено для этой единицы (ХМ2); иначе ровно +1, как раньше.
        if (n != Math.Truncate(n) && QuantityRules.ForTile(product) is null)
            return null;
        quantity = Math.Round(n, 3);
        return variant;
    }

    /// <summary>Количество в тексте поля «Количество» кассы: целое без дроби, дробное до 3 знаков (точка).</summary>
    public static string QuantityText(double quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>«Коробка ×200» (или своё имя упаковки у варианта).</summary>
    public static string Label(AlternateBarcodeVariant variant, double quantity)
    {
        var name = string.IsNullOrWhiteSpace(variant.Name)
            ? Tr.T("Коробка", "Куту", "Box", "Kutu", "Quti")
            : variant.Name!.Trim();
        return $"{name} ×{QuantityText(quantity)}";
    }
}
