using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-11-13: файл для CL-Works («Файл → Импорт → CSV») — запасной путь для весов CAS, которые мы по сети не ведём (CL5500, CL5200, CL7200, LP-1.6).
/// Порядок колонок — по обработке Infostart для Розницы (работает с CL3000/5000/7000); номера колонок = номера полей PLU (ptype). Без строки заголовка.
/// Кодировка по умолчанию cp1251, можно UTF-8; десятичный разделитель цены — настройка (точка по умолчанию). НА ЖИВОМ CL-Works НЕ ПРОВЕРЕНО.
/// </summary>
public static class CasCsvWriter
{
    /// <summary>Номера полей PLU в порядке колонок файла.</summary>
    public static readonly int[] Columns =
    {
        1, 2, 4, 11, 10, 30, 31, 9, 49, 80, 81, 55, 5, 100, 3, 14, 15, 26, 6, 91, 8, 13, 12, 24, 23, 85, 86,
        20, 18, 19, 16, 17, 22, 25, 35, 50, 70, 90, 71, 69, 64, 68, 60, 61, 65, 62, 66, 99, 101, 102,
    };

    /// <summary>Одна строка файла. <paramref name="decimalSeparator"/> — «.» или «,»; <paramref name="priceDecimals"/> — знаков в цене (как на весах).</summary>
    public static string BuildLine(CasPlu plu, int priceDecimals, char decimalSeparator = '.')
    {
        var (name1, name2) = CasProtocol.SplitName(plu.Name);
        var fields = new List<string>(Columns.Length);
        foreach (var column in Columns)
        {
            fields.Add(column switch
            {
                1 => plu.Department.ToString(CultureInfo.InvariantCulture),
                2 => plu.PluNumber.ToString(CultureInfo.InvariantCulture),
                4 => (plu.Weighed ? 1 : 3).ToString(CultureInfo.InvariantCulture),
                11 => plu.ItemCode.ToString(CultureInfo.InvariantCulture),
                10 => Quote(name1),
                30 => Quote(name2),
                // 2026-11-14: цена с запятой (125,50) в файле с разделителем «,» — в кавычках, иначе колонки съезжают
                6 => Quote(FormatPrice(plu.UnitPriceUnits, priceDecimals, decimalSeparator)),
                80 => plu.LabelNumber.ToString(CultureInfo.InvariantCulture),
                85 => plu.BarcodeNumber.ToString(CultureInfo.InvariantCulture),
                _ => "0",
            });
        }
        return string.Join(",", fields);
    }

    public static string FormatPrice(long units, int decimals, char separator)
    {
        decimals = Math.Clamp(decimals, 0, 3);
        if (decimals == 0)
            return units.ToString(CultureInfo.InvariantCulture);
        var pow = (long)Math.Pow(10, decimals);
        return (units / pow).ToString(CultureInfo.InvariantCulture) + separator + (units % pow).ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0');
    }

    /// <summary>Название в кавычках, если в нём есть запятая или кавычка.</summary>
    private static string Quote(string text) =>
        text.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    /// <summary>Весь файл в байтах: cp1251 (по умолчанию) или UTF-8 без BOM; строки через CRLF.</summary>
    public static byte[] Build(IEnumerable<CasPlu> plus, int priceDecimals, char decimalSeparator = '.', bool utf8 = false)
    {
        var text = new StringBuilder();
        foreach (var plu in plus)
            text.Append(BuildLine(plu, priceDecimals, decimalSeparator)).Append("\r\n");
        var s = text.ToString();
        if (utf8)
            return new UTF8Encoding(false).GetBytes(s);
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
            bytes[i] = s[i] is (char)13 or (char)10 ? (byte)s[i] : CasProtocol.Cp1251(s[i]);
        return bytes;
    }
}
