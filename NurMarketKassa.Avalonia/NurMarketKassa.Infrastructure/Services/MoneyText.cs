using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-19, редизайн кассы К1 (аудит 19.10, проблема 1: в чеке и оплате «1517.96», а в шапке «12 450,00»):
/// один вид суммы на экране кассы, как в программе владельца — разряды неразрывным пробелом, копейки через запятую:
/// «1 234,50». Только ПОКАЗ: поля ввода, разбор сумм, печать чека и журналы этим не пользуются (там по-прежнему
/// «0.00» в InvariantCulture).</summary>
public static class MoneyText
{
    // Неразрывный пробел (как ru-RU «N2» у владельца): сумма не рвётся переносом между разрядами.
    private static readonly NumberFormatInfo Format = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary>«1 234,50» — без валюты.</summary>
    // 2026-10-20, живая проверка: -0,0 и суммы в (-0,005; 0) показывались «-0,00» (экран покупателя: «Скидка -0,00»),
    // NaN и бесконечность — «NaN». Ноль — всегда «0,00» без минуса; не число — прочерк «—».
    public static string Amount(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? NotANumber : NoNegativeZero(value.ToString("#,0.00", Format));

    /// <summary>«1 234,50» — без валюты.</summary>
    public static string Amount(decimal value) => NoNegativeZero(value.ToString("#,0.00", Format));

    /// <summary>Прочерк вместо суммы, которую нельзя показать (NaN, бесконечность).</summary>
    public const string NotANumber = "—";

    private static string NoNegativeZero(string text) => text == "-0,00" ? "0,00" : text;

    /// <summary>«1 234,50 сом» — с валютой на языке интерфейса.</summary>
    public static string Som(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? NotANumber : Amount(value) + " " + Currency;

    /// <summary>«1 234,50 сом» — с валютой на языке интерфейса.</summary>
    public static string Som(decimal value) => Amount(value) + " " + Currency;

    /// <summary>2026-10-20: цена из данных («3500.00 сом», «20.00») — «3 500,00 сом» для показа; не число — как было.
    /// Сами данные (PriceLine и т.п.) не меняются.</summary>
    public static string SomFromText(string? text)
    {
        var raw = (text ?? "").Trim();
        var number = raw.EndsWith(" сом", StringComparison.OrdinalIgnoreCase) ? raw[..^4].Trim() : raw;
        return number.Length > 0 && !number.Contains(',')
               && decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Som(value)
            : raw;
    }

    public static string Currency => Tr.T("сом", "сом", "som", "som", "so'm");
}
