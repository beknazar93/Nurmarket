using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-09-28: форматы весового штрих-кода весов Dahua TM (TM-30F у владельца) — 15 вариантов
/// из их программы «Русский масштаб»: «Базовая настройка → Системные параметры → Common param →
/// Barcode» (список снят с окна, id 1086). Буквы: F — флаг («Префикс штрихкода» товара, madv5),
/// W — «Код товара» (mcode), E — сумма, N — вес, C — контрольная цифра, D — 13-значный код
/// товара (m13code), O — неизвестно (стоит в конце 18-значных вариантов).
///
/// Касса этот формат на весы НЕ отправляет (системные параметры — только их программой): здесь
/// он нужен, чтобы показать пример этикетки и сверить его с разбором кассы (WeightBarcodeParser).
/// Допущения (проверить первой этикеткой): в W идут МЛАДШИЕ цифры 7-значного кода; N — граммы
/// (кг с 3 знаками); E — сумма в единицах цены весов (при «Price point: Integer» — целые сомы).
/// </summary>
public static class DahuaTmBarcodeFormat
{
    /// <summary>Все варианты в порядке списка их программы.</summary>
    public static readonly IReadOnlyList<string> Variants = new[]
    {
        "FWWWWWWC",
        "FWWWWWWEEEEEC",
        "FWWWWWWNNNNNC",
        "FWWWWWWEEEEENNNNNC",
        "FWWWWWWNNNNNEEEEEC",
        "FWWWWWWEEEEENNNNNO",
        "FWWWWWWNNNNNEEEEEO",
        "FFWWWWWEEEEEC",
        "FFWWWWWNNNNNC",
        "DDDDDDDDDDDDC",
        "FFWWWWWEEEEENNNNNC",
        "FFWWWWWNNNNNEEEEEC",
        "FFWWWWWEEEEENNNNNO",
        "FFWWWWWNNNNNEEEEEO",
        "FWWWWWWEEEEEC+NNNNN",
    };

    /// <summary>Сейчас выбран на весах владельца (снимок окна их программы 28.09).</summary>
    public const string OwnerCurrent = "FFWWWWWEEEEEC";

    /// <summary>Для кассы: раскладка «по PLU» (2+5+5+1). По весу — флаг 20, по сумме — 25.</summary>
    public static string Recommended(bool byWeight) => byWeight ? "FFWWWWWNNNNNC" : "FFWWWWWEEEEEC";

    public static int RecommendedFlag(bool byWeight) => byWeight ? 20 : 25;

    public static bool HasWeight(string format) => (format ?? "").Contains('N');
    public static bool HasAmount(string format) => (format ?? "").Contains('E');

    /// <summary>Пример штрих-кода по формату. <paramref name="amountUnits"/> — сумма уже в
    /// единицах весов (сом × 10^знаков цены). Пустая строка — формат не распознан.</summary>
    public static string BuildSample(string format, int flag, long productCode, int grams, long amountUnits, string? code13 = null)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "";
        var mainPart = format;
        var addOn = "";
        var plus = format.IndexOf('+');
        if (plus >= 0)
        {
            mainPart = format[..plus];
            addOn = format[(plus + 1)..];
        }

        var main = Fill(mainPart, flag, productCode, grams, amountUnits, code13);
        if (main is null)
            return "";
        if (addOn.Length == 0)
            return main;
        var tail = Fill(addOn, flag, productCode, grams, amountUnits, code13);
        return tail is null ? "" : main + "+" + tail;
    }

    private static string? Fill(string pattern, int flag, long productCode, int grams, long amountUnits, string? code13)
    {
        var sb = new StringBuilder(pattern.Length);
        var i = 0;
        while (i < pattern.Length)
        {
            var letter = pattern[i];
            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == letter)
                run++;
            switch (letter)
            {
                case 'F':
                    sb.Append(DahuaTmProtocol.Digits(flag, run));
                    break;
                case 'W':
                    sb.Append(DahuaTmProtocol.Digits(productCode, run));
                    break;
                case 'N':
                    sb.Append(DahuaTmProtocol.Digits(grams, run));
                    break;
                case 'E':
                    sb.Append(DahuaTmProtocol.Digits(amountUnits, run));
                    break;
                case 'D':
                    var digits = new string((code13 ?? "").Where(char.IsDigit).ToArray());
                    sb.Append(digits.Length >= run ? digits[..run] : digits.PadLeft(run, '0'));
                    break;
                case 'C':
                case 'O':
                    // Контрольная цифра EAN (веса 3/1 справа) по всему, что стоит перед ней.
                    for (var k = 0; k < run; k++)
                        sb.Append(CheckDigit(sb.ToString()));
                    break;
                default:
                    return null;
            }
            i += run;
        }
        return sb.ToString();
    }

    /// <summary>Контрольная цифра EAN/UPC для строки цифр без неё.</summary>
    public static char CheckDigit(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
