using System.Globalization;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-15, ревью: разбор суммы наличных, которую кассир вводит руками (закрытие/открытие смены).
/// Раньше поля разбирались через NumberStyles.Number + InvariantCulture, где запятая — разделитель тысяч:
/// «1500,50» с русской цифровой клавиатуры (десятичная клавиша печатает запятую) превращалось в 150050.
/// Здесь запятая и точка — обе десятичный разделитель, пробелы (в т.ч. неразрывные) убираются,
/// разделитель тысяч не допускается. Значение, которое касса сама подставляет в поле («1000.00»), разбирается как раньше.</summary>
public static class CashAmountInput
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        // Обычный, неразрывный (U+00A0) и узкий неразрывный (U+202F) пробелы — «1 500,50».
        var raw = new string((text ?? "").Where(ch => ch != ' ' && ch != (char)0x00A0 && ch != (char)0x202F).ToArray()).Trim();
        if (raw.Length == 0)
            return false;

        // «1 500,50» / «1500.50» — один разделитель, это дробная часть. Если есть и точка, и запятая
        // («1,500.50» или «1.500,50») — последний из них дробный, другой — разделитель тысяч.
        // Один и тот же знак дважды («1.500.000», «1,5,0») — неоднозначно, не угадываем: кассир увидит ошибку.
        var last = Math.Max(raw.LastIndexOf(','), raw.LastIndexOf('.'));
        if (last >= 0)
        {
            var sep = raw[last];
            var other = sep == ',' ? '.' : ',';
            if (raw.IndexOf(sep) != last)
                return false;
            // Разделитель тысяч — только между группами по 3 цифры («1,500.50»), иначе «1,5.0» стало бы 15,0.
            var groups = raw[..last].Split(other);
            if (groups.Skip(1).Any(g => g.Length != 3))
                return false;
            raw = string.Concat(groups) + "." + raw[(last + 1)..];
        }

        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static decimal? Parse(string? text) => TryParse(text, out var value) ? value : null;
}
