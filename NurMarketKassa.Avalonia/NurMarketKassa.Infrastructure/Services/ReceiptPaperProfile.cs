#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Ширина термоленты: текст (колонки) и графика (точки ESC/POS).</summary>
public static class ReceiptPaperProfile
{
    public const int Paper58mm = 58;
    public const int Paper80mm = 80;

    public static int NormalizePaperWidthMm(int? value) =>
        value is >= Paper80mm ? Paper80mm : Paper58mm;

    public static int GetCharWidth(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? 48 : 32;

    public static int GetRasterWidthPixels(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? 576 : 384;

    public static string DescribePaperWidth(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm
            ? "80 мм (48 кол., 576 точек)"
            : "58 мм (32 кол., 384 точки)";
}
