using System.Globalization;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-11-13: формат штрихкода этикетки весов CAS и пример этикетки. Формат задаётся НА ВЕСАХ строкой символов (руководство оператора CL5000, меню 1260 и 1520):
/// D — отдел (у нас это префикс 20…29), I — код товара, W — вес, P — цена, C — контрольная цифра EAN-13; число повторов буквы = число цифр поля.
/// Касса читает 13 цифр: раскладка «по PLU» — префикс 2 + код 5 + значение 5 + контрольная, «по коду» — префикс 2 + код 6 + значение 4 + контрольная.
/// Строки формата и «W — в граммах» взяты из руководства и НЕ ПРОВЕРЕНЫ на живых весах: касса формат на весы не пишет, а показывает, что задать.
/// </summary>
public static class CasBarcodeFormat
{
    /// <summary>Строка формата для весов: «по весу» — W, «по сумме» — P; раскладка кассы «code» (6+4) или «plu» (5+5).</summary>
    public static string Recommended(string? layout, bool byWeight)
    {
        var isCode = string.Equals(layout, "code", StringComparison.OrdinalIgnoreCase);
        var value = byWeight ? 'W' : 'P';
        return isCode ? "DDIIIIII" + new string(value, 4) + "C" : "DDIIIII" + new string(value, 5) + "C";
    }

    /// <summary>2026-11-14: цена в наименьших единицах весов (125,50 при 2 знаках → 12550). Расчёт здесь, а не в окнах (check-money-in-views).</summary>
    public static long ToUnits(decimal price, int decimals) =>
        (long)Math.Round(price * (decimal)Math.Pow(10, Math.Clamp(decimals, 0, 3)), MidpointRounding.AwayFromZero);

    /// <summary>2026-11-14: сумма и её значение в наименьших единицах для примера этикетки: цена за кг × масса в граммах.</summary>
    public static (decimal Amount, long Units) SampleAmount(decimal pricePerKg, int grams, int decimals)
    {
        var amount = Math.Round(pricePerKg * grams / 1000m, Math.Clamp(decimals, 0, 3), MidpointRounding.AwayFromZero);
        return (amount, ToUnits(amount, decimals));
    }

    public static int CodeDigits(string format) => format.Count(c => c == 'I');

    public static int ValueDigits(string format) => format.Count(c => c is 'W' or 'P');

    /// <summary>Контрольная цифра EAN-13 по первым 12 цифрам.</summary>
    public static char Ean13Check(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>
    /// Пример штрихкода: префикс (отдел) + код + значение + контрольная. <paramref name="value"/> — граммы (W) или сумма в наименьших единицах цены весов (P).
    /// null — код или значение не помещаются в поля формата (весы напечатают этикетку без штрихкода или с обрезанным).
    /// </summary>
    public static string? BuildSample(string format, int prefix, long code, long value)
    {
        var codeDigits = CodeDigits(format);
        var valueDigits = ValueDigits(format);
        if (format.Length != 13 || codeDigits == 0 || valueDigits == 0 || prefix is < 0 or > 99 || code < 0 || value < 0)
            return null;
        if (code >= Pow10(codeDigits) || value >= Pow10(valueDigits))
            return null;
        var body = prefix.ToString("00", CultureInfo.InvariantCulture)
                   + code.ToString(CultureInfo.InvariantCulture).PadLeft(codeDigits, '0')
                   + value.ToString(CultureInfo.InvariantCulture).PadLeft(valueDigits, '0');
        return body.Length == 12 ? body + Ean13Check(body) : null;
    }

    private static long Pow10(int digits)
    {
        long v = 1;
        for (var i = 0; i < digits; i++)
            v *= 10;
        return v;
    }
}
