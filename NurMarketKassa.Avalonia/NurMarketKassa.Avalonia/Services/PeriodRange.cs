using System.Globalization;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-20, проверка фильтров (просьба владельца «фильтры не работают»): одни правила периода во всех окнах —
/// «Неделя» = последние 7 дней включая сегодня, «Месяц» = с 1-го числа текущего месяца (как «Возвраты поставщикам» и «Сводка»).
/// Раньше «Неделя» в одних окнах была с понедельника, в других — 8 дней; «Месяц» — 30 дней назад или с 1-го.
/// И подпись дат рядом с сегментами: «01.10–20.10».</summary>
public static class PeriodRange
{
    public static DateTime WeekFrom(DateTime today) => today.Date.AddDays(-6);

    public static DateTime MonthFrom(DateTime today) => new(today.Year, today.Month, 1);

    /// <summary>«20.10», «01.10–20.10», через год — «20.12.2025–05.01.2026».</summary>
    public static string Label(DateTime from, DateTime to)
    {
        var f = from.Date;
        var t = to.Date;
        if (f > t)
            (f, t) = (t, f);
        var withYear = f.Year != t.Year || t.Year != DateTime.Today.Year;
        var fmt = withYear ? "dd.MM.yyyy" : "dd.MM";
        return f == t
            ? f.ToString(fmt, CultureInfo.InvariantCulture)
            : f.ToString(fmt, CultureInfo.InvariantCulture) + "–" + t.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
