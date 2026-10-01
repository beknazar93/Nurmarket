#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Ширина термоленты: текст (колонки) и графика (точки ESC/POS).</summary>
public static class ReceiptPaperProfile
{
    public const int Paper58mm = 58;
    public const int Paper80mm = 80;

    public static int NormalizePaperWidthMm(int? value) =>
        value is >= Paper80mm ? Paper80mm : Paper58mm;

    // 2026-10-01, владелец: «настройка чековой: возможность менять длину точки, чтобы они не
    // выходили за рамки (для 80 и 58 мм)». У части принтеров печатная ширина меньше стандартной
    // (80 мм — 512 точек и 42 символа вместо 576/48; 58 мм — 360 точек и 30 символов): линии
    // «-----»/«=====» и правый столбец сумм уходили за край и переносились. Задаётся в настройках
    // печати отдельно для каждой ширины ленты; 0 — стандартное значение. Значения ставит
    // UserPreferences (свойства ReceiptCharWidth58/80, ReceiptDots58/80).
    public const int MinCharWidth = 20, MaxCharWidth = 64;
    public const int MinDots = 240, MaxDots = 640;
    public static int CharWidth58Override { get; set; }
    public static int CharWidth80Override { get; set; }
    public static int Dots58Override { get; set; }
    public static int Dots80Override { get; set; }

    public static int GetDefaultCharWidth(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? 48 : 32;

    public static int GetDefaultRasterWidthPixels(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? 576 : 384;

    public static int GetCharWidth(int paperWidthMm)
    {
        var o = NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? CharWidth80Override : CharWidth58Override;
        return o is >= MinCharWidth and <= MaxCharWidth ? o : GetDefaultCharWidth(paperWidthMm);
    }

    public static int GetRasterWidthPixels(int paperWidthMm)
    {
        var o = NormalizePaperWidthMm(paperWidthMm) >= Paper80mm ? Dots80Override : Dots58Override;
        // Растр ESC/POS идёт байтами по 8 точек — ширину округляем вниз до кратной 8.
        return o is >= MinDots and <= MaxDots ? o / 8 * 8 : GetDefaultRasterWidthPixels(paperWidthMm);
    }

    public static string DescribePaperWidth(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) >= Paper80mm
            ? $"80 мм ({GetCharWidth(Paper80mm)} кол., {GetRasterWidthPixels(Paper80mm)} точек)"
            : $"58 мм ({GetCharWidth(Paper58mm)} кол., {GetRasterWidthPixels(Paper58mm)} точек)";
}
