using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: типы весового штрих-кода Rongta RLS1000 / RLS1100 — таблица «Appendix II.
/// Barcode coding table» руководства «Label Scale Software User Manual» (стр. 28–30), только
/// коды EAN-13 (00–19, 21–29). Используется окном «Настройки весов Rongta» для живого примера
/// и сверки с раскладкой компании (WeightBarcodeParser).
///
/// Обозначения руководства: DD — номер отдела (2 цифры), D — отдел (1 цифра), «20..29» —
/// фиксированный префикс, I — код товара (по легенде «PLU No.»; в файле .txp это поле «Code»,
/// см. Appendix I: «Code — see detail in barcode format»), P — сумма, W — вес, C — контрольная
/// цифра EAN-13 (весы считают сами).
///
/// ЧТО НЕ ОПИСАНО в руководстве и здесь принято допущением (помечено в окне):
/// • сумма P печатается в сотых (как «Unit price — non-decimal pattern, 12.34 → 1234» из
///   Appendix I) при «decimal position = 2»;
/// • вес «WWWWW» без точки — единица зависит от настройки весов, поэтому такие типы (09, 19, 29)
///   считаем неподходящими для кассы.</summary>
public static class RongtaBarcodeFormat
{
    public enum PrefixKind { Department2, Fixed2, Department1 }

    public enum ValueKind { None, Price, Weight }

    /// <summary>Один тип штрих-кода. <see cref="GramsPerUnit"/> — сколько граммов в единице
    /// последней цифры веса (W.WWW → 1, WW.WW → 10, WWWW.W → 100); 0 — неизвестно.</summary>
    public sealed record BarcodeType(
        int Type,
        PrefixKind Prefix,
        int FixedPrefix,
        int CodeDigits,
        ValueKind Value,
        int ValueDigits,
        int GramsPerUnit,
        string Pattern);

    public static readonly IReadOnlyList<BarcodeType> Types = BuildTypes();

    private static List<BarcodeType> BuildTypes()
    {
        // Строки 00..09 из таблицы; 10..19 — то же с фиксированным префиксом 20..29.
        var baseRows = new (int Code, ValueKind Kind, int Digits, int Grams, string Value)[]
        {
            (10, ValueKind.None, 0, 0, ""),
            (6, ValueKind.Price, 4, 0, "PPPP"),
            (5, ValueKind.Price, 5, 0, "PPPPP"),
            (4, ValueKind.Price, 6, 0, "PPPPPP"),
            (3, ValueKind.Price, 7, 0, "PPPPPPP"),
            (6, ValueKind.Weight, 4, 1, "W.WWW"),
            (6, ValueKind.Weight, 4, 10, "WW.WW"),
            (5, ValueKind.Weight, 5, 1, "WW.WWW"),
            (5, ValueKind.Weight, 5, 100, "WWWW.W"),
            (5, ValueKind.Weight, 5, 0, "WWWWW"),
        };
        var list = new List<BarcodeType>();
        for (var i = 0; i < baseRows.Length; i++)
        {
            var r = baseRows[i];
            list.Add(new BarcodeType(i, PrefixKind.Department2, 0, r.Code, r.Kind, r.Digits, r.Grams,
                "DD " + new string('I', r.Code) + (r.Value.Length > 0 ? " " + r.Value : "") + " C"));
        }
        for (var i = 0; i < baseRows.Length; i++)
        {
            var r = baseRows[i];
            list.Add(new BarcodeType(10 + i, PrefixKind.Fixed2, 20 + i, r.Code, r.Kind, r.Digits, r.Grams,
                $"{20 + i} " + new string('I', r.Code) + (r.Value.Length > 0 ? " " + r.Value : "") + " C"));
        }

        // 21..29: отдел 1 цифра, код на цифру длиннее.
        var oneDigit = new (int Code, ValueKind Kind, int Digits, int Grams, string Value)[]
        {
            (7, ValueKind.Price, 4, 0, "PPPP"),
            (6, ValueKind.Price, 5, 0, "PPPPP"),
            (5, ValueKind.Price, 6, 0, "PPPPPP"),
            (4, ValueKind.Price, 7, 0, "PPPPPPP"),
            (7, ValueKind.Weight, 4, 1, "W.WWW"),
            (7, ValueKind.Weight, 4, 10, "WW.WW"),
            (6, ValueKind.Weight, 5, 1, "WW.WWW"),
            (6, ValueKind.Weight, 5, 100, "WWWW.W"),
            (6, ValueKind.Weight, 5, 0, "WWWWW"),
        };
        for (var i = 0; i < oneDigit.Length; i++)
        {
            var r = oneDigit[i];
            list.Add(new BarcodeType(21 + i, PrefixKind.Department1, 0, r.Code, r.Kind, r.Digits, r.Grams,
                "D " + new string('I', r.Code) + " " + r.Value + " C"));
        }
        return list;
    }

    public static BarcodeType? Find(int type) => Types.FirstOrDefault(t => t.Type == type);

    /// <summary>Собирает пример EAN-13. <paramref name="department"/> — номер отдела товара
    /// (для DD — 2 цифры, для D — 1 цифра). null — тип не EAN-13 с данными или вес не выразить.</summary>
    public static string? BuildSample(BarcodeType type, int department, long code, int grams, decimal amountSom)
    {
        var sb = new StringBuilder(13);
        switch (type.Prefix)
        {
            case PrefixKind.Department2: sb.Append(Digits(department, 2)); break;
            case PrefixKind.Fixed2: sb.Append(Digits(type.FixedPrefix, 2)); break;
            case PrefixKind.Department1: sb.Append(Digits(department, 1)); break;
        }
        sb.Append(Digits(code, type.CodeDigits));
        switch (type.Value)
        {
            case ValueKind.Price:
                sb.Append(Digits((long)Math.Round(amountSom * 100m, MidpointRounding.AwayFromZero), type.ValueDigits));
                break;
            case ValueKind.Weight:
                if (type.GramsPerUnit <= 0)
                    return null;
                sb.Append(Digits(grams / type.GramsPerUnit, type.ValueDigits));
                break;
        }
        if (sb.Length != 12)
            return null;
        sb.Append(ShtrikhBarcodeFormat.Ean13CheckDigit(sb.ToString()));
        return sb.ToString();
    }

    private static string Digits(long value, int width)
    {
        if (value < 0)
            value = 0;
        var text = value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        return text.Length > width ? text[^width..] : text;
    }
}
