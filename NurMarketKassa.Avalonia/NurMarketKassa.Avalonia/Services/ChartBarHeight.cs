namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, Д4-41): высота столбца графика «Сводки». Столбец не уже 8 px: при одном большом дне (9,4) остальные раньше рисовались линией 2–4 px
/// («&lt;0,1», «0,3») и неделя не читалась. День без продаж — тонкая черта 2 px. Чистая логика без Avalonia (проверяется тестами).</summary>
public static class ChartBarHeight
{
    /// <summary>Наименьшая высота столбца дня, в котором были продажи.</summary>
    public const double MinVisible = 8;

    /// <summary>Высота столбца: <paramref name="area"/> × значение / <paramref name="scaleMax"/>, но не меньше <see cref="MinVisible"/>; без продаж — 2.</summary>
    public static double For(double value, double scaleMax, double area)
    {
        if (value <= 0)
            return 2;
        if (scaleMax <= 0 || double.IsNaN(scaleMax))
            return MinVisible;
        return Math.Max(MinVisible, area * value / scaleMax);
    }
}
