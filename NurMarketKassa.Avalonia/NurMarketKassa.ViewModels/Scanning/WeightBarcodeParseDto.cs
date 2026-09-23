namespace NurMarketKassa.ViewModels.Scanning;

/// <summary>Result of parsing an EAN-13 embedded-weight barcode (prefix 21–29).</summary>
public sealed class WeightBarcodeParseDto
{
    public WeightBarcodeParseDto(string productCode, decimal? weightKg, bool isWeighed)
    {
        ProductCode = productCode;
        WeightKg = weightKg;
        IsWeighed = isWeighed;
    }

    public string ProductCode { get; }

    public decimal? WeightKg { get; }

    public bool IsWeighed { get; }
}
