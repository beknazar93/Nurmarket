#nullable enable
namespace NurMarketKassa.Configuration;

public sealed class ReceiptPrinterSettings
{
    public bool Enabled { get; init; }

    public string DevicePath { get; init; } = "LPT1";

    public string TextEncoding { get; init; } = "wpc1251";

    public int? EscPosTableByte { get; init; }

    public int? EscRByte { get; init; }

    public int RetryCount { get; init; } = 3;
}
