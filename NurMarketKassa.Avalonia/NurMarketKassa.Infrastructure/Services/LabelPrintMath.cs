namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (перенос из веб-редактора ценников): чистые правила окна «Печать штрихкодов и ценников» без экрана — «Сколько на товар»
/// (по одной / по остатку / вручную), границы «Своего размера», точки принтера и сдвиги. Окно и тесты считают одним и тем же кодом.</summary>
public static class LabelPrintMath
{
    /// <summary>Потолок этикеток на один товар (защита от остатка 5000 штук и от случайного ввода).</summary>
    public const int MaxCopies = 99;

    /// <summary>Разрешение термопринтеров этикеток (TSPL-растр и предпросмотр) — 8 точек на мм.</summary>
    public const int Dpi = 203;

    // «Свой размер»: ширина × высота в мм (те же пределы, что были в окне и в редакторе этикетки).
    public const double MinWidthMm = 15, MaxWidthMm = 150, MinHeightMm = 10, MaxHeightMm = 150;

    /// <summary>Предел сдвига по X/Y в мм (как в редакторе этикетки) — в точках ±160.</summary>
    public const double MaxOffsetMm = 20;

    /// <summary>"default" — «По одной» (число «Копий каждой»), "stock" — «По остатку», "manual" — «Вручную» (по умолчанию 1, число задаётся в карточке товара).
    /// Любое другое значение из старого файла настроек — "default".</summary>
    public static string NormalizeMode(string? mode) => mode is "stock" or "manual" ? mode : "default";

    /// <summary>Число этикеток на товар без собственного числа: по режиму. <paramref name="stockCopies"/> — уже посчитанное «по остатку» (1…99).</summary>
    public static int BaseCopies(string? mode, int defaultCopies, int stockCopies) =>
        Math.Clamp(NormalizeMode(mode) switch { "stock" => stockCopies, "manual" => 1, _ => defaultCopies }, 1, MaxCopies);

    /// <summary>Этикеток на товар: собственное число карточки, иначе по режиму; всегда 1…99.</summary>
    public static int CopiesOf(string? mode, int defaultCopies, int stockCopies, int? own) =>
        own is { } v ? Math.Clamp(v, 1, MaxCopies) : BaseCopies(mode, defaultCopies, stockCopies);

    /// <summary>Границы «Своего размера»: NaN и ноль заменяются размером 40×30, остальное — в пределы 15…150 × 10…150 мм.</summary>
    public static (double W, double H) ClampSize(double w, double h)
    {
        if (double.IsNaN(w) || w <= 0) w = 40;
        if (double.IsNaN(h) || h <= 0) h = 30;
        return (Math.Clamp(w, MinWidthMm, MaxWidthMm), Math.Clamp(h, MinHeightMm, MaxHeightMm));
    }

    /// <summary>Размер этикетки в точках принтера — как строится растр (мм × dpi / 25,4, не меньше 10).</summary>
    public static (int W, int H) LabelDots(double widthMm, double heightMm, int dpi = Dpi) =>
        (Math.Max((int)(widthMm / 25.4 * dpi), 10), Math.Max((int)(heightMm / 25.4 * dpi), 10));

    public static int MmToDots(double mm, int dpi = Dpi) => (int)Math.Round(mm * dpi / 25.4);

    /// <summary>Точки → мм для хранения сдвига (в настройках кассы сдвиг в мм); три знака хватает, чтобы точки при обратном переводе не менялись.</summary>
    public static double DotsToMm(int dots, int dpi = Dpi) => Math.Round(dots * 25.4 / dpi, 3);

    /// <summary>Сдвиг в точках в пределах ±20 мм.</summary>
    public static int ClampOffsetDots(int dots) => Math.Clamp(dots, -MmToDots(MaxOffsetMm), MmToDots(MaxOffsetMm));
}
