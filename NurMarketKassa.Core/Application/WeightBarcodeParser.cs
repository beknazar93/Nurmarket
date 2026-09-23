using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Application;

/// <summary>Парсер весовых штрих-кодов с префиксами 21–29 (EAN-13).</summary>
public static class WeightBarcodeParser
{
    public static bool IsEmbeddedWeightBarcode(string barcode) => TryParse(barcode, out _);

    public static bool TryParse(string barcode, out WeightBarcodeParseResult result)
    {
        result = null!;
        var code = (barcode ?? "").Trim();
        if (code.Length != 13)
            return false;

        if (code[0] != '2' || code[1] < '1' || code[1] > '9')
            return false;

        for (var i = 0; i < code.Length; i++)
        {
            if (!char.IsDigit(code[i]))
                return false;
        }

        var productCode = code.Substring(2, 5);
        if (!int.TryParse(code.AsSpan(7, 5), out var grams) || grams <= 0)
            return false;

        var weightKg = grams / 1000.0;
        if (weightKg <= 0)
            return false;

        result = new WeightBarcodeParseResult(productCode, weightKg);
        return true;
    }
}
