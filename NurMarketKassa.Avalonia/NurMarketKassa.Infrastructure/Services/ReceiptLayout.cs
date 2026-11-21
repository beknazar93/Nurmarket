#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Ширина текста чека в символах (32 для 58 мм, 48 для 80 мм).</summary>
internal static class ReceiptLayout
{
    /// <summary>2026-11-21 (редактор чека): ширина на время предпросмотра (58/80 мм) на этом потоке; null — по настройкам.</summary>
    [ThreadStatic] internal static int? WidthOverride;

    internal static int CharWidth
    {
        get
        {
            if (WidthOverride is { } forced)
                return forced;
            try
            {
                var v = Environment.GetEnvironmentVariable("DESKTOP_MARKET_RECEIPT_WIDTH")?.Trim();
                if (int.TryParse(v, out var w) && w is >= 24 and <= 56)
                    return w;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Receipt width override ignored: {ex.GetType().Name}", "DEBUG");
            }

            return ReceiptPaperProfile.GetCharWidth(UserPreferences.Instance.ReceiptPaperWidthMm);
        }
    }

    internal static int RasterWidthPixels =>
        ReceiptPaperProfile.GetRasterWidthPixels(UserPreferences.Instance.ReceiptPaperWidthMm);
}
