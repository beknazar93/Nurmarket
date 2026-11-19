using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Н-2, одно поле ввода в кассе): количество перед кодом — «3*4600000000001», «1,5*код», а также «3*» отдельно
/// (количество для следующего скана). Нижней строки «− 1 +» в виде «Классика» больше нет — количество набирают так, как на обычных кассах.
/// Чистая логика без окон — проверяется тестом (ScanQuantityPrefixTests).</summary>
public static class ScanQuantityPrefix
{
    /// <summary>Больше этого в «N*» не принимаем: длинное число перед «*» — это штрихкод со знаком «*» внутри, а не количество.</summary>
    public const double MaxQuantity = 9999;

    /// <summary>Разбирает «количество*код». true — в начале стоит количество (1–4 цифры, дробная часть до 3 знаков через запятую или точку) и знак «*» или «×».
    /// <paramref name="rest"/> — код или название после знака (может быть пустым: «3*» — количество ждёт следующий скан).
    /// Ноль, отрицательное и нечисловое («*», «0*», «abc*») — false: строка идёт в поиск как есть.</summary>
    public static bool TryParse(string? input, out double quantity, out string rest)
    {
        quantity = 0;
        rest = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var text = input.Trim();
        var star = text.IndexOfAny(['*', '×']);
        if (star <= 0)
            return false;
        var number = text[..star].Trim();
        if (number.Length == 0 || number.Length > 8)
            return false;
        var separators = 0;
        var whole = 0;
        var fraction = 0;
        foreach (var ch in number)
        {
            if (ch is ',' or '.')
            {
                separators++;
                continue;
            }
            if (ch is < '0' or > '9')
                return false;
            if (separators == 0)
                whole++;
            else
                fraction++;
        }
        if (separators > 1 || whole is 0 or > 4 || (separators == 1 && fraction is 0 or > 3))
            return false;
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0 || value > MaxQuantity)
            return false;
        quantity = value;
        rest = text[(star + 1)..].Trim();
        return true;
    }

    /// <summary>Количество строкой для поля «Количество» кассы и метки «× 3 — следующий товар»: «3», «1,5» (запятая — общий показ количества MoneyText.Qty;
    /// разбор поля принимает и запятую, и точку).</summary>
    public static string QuantityText(double quantity) => MoneyText.Qty(Math.Round(quantity, 3));

    /// <summary>Похоже на весовую этикетку весов (13 цифр, начинается с 20–29): вес или сумма уже в коде, «N*» к ней не применяется.</summary>
    public static bool LooksLikeScaleLabel(string? code)
    {
        var text = code?.Trim() ?? "";
        return text.Length == 13 && text[0] == '2' && text.All(char.IsDigit);
    }
}
