using NurMarketKassa.Core.Application;

namespace NurMarketKassa.ViewModels.Scanning;

/// <summary>Cross-platform wrapper over <see cref="WeightBarcodeParser"/>.</summary>
public static class WeightBarcodeParseHelper
{
    public static bool TryParse(string barcode, out WeightBarcodeParseDto result)
    {
        if (WeightBarcodeParser.TryParse(barcode, out var parsed))
        {
            result = new WeightBarcodeParseDto(
                parsed.ProductCode,
                (decimal)parsed.WeightKg,
                isWeighed: true);
            return true;
        }

        var code = (barcode ?? "").Trim();
        result = new WeightBarcodeParseDto(code, weightKg: null, isWeighed: false);
        return false;
    }
}
