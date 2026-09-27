using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Две-три буквы названия товара для плитки без фото (раскладки «Минимал» и «Карточки»,
/// 2026-09-28): «Кока-Кола 1 л» → «КК», «Хлеб» → «ХЛ». Так плитки без фотографии не выглядят
/// пустыми, а товар узнаётся по первым буквам.</summary>
public sealed class MonogramConverter : IValueConverter
{
    public static readonly MonogramConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").Trim();
        if (text.Length == 0)
            return "?";

        var words = text.Split(new[] { ' ', '-', '_', '.', ',', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0]))
            .ToArray();
        if (words.Length >= 2)
            return string.Concat(char.ToUpper(words[0][0], culture), char.ToUpper(words[1][0], culture));

        var letters = new string(text.Where(char.IsLetterOrDigit).Take(2).ToArray());
        return letters.Length == 0 ? "?" : letters.ToUpper(culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Спокойный цвет плитки по названию товара: один и тот же товар всегда одного цвета,
/// соседние обычно разные. Палитра приглушённая, белые буквы на ней читаются (контраст ≥ 4.5:1),
/// поэтому плитка одинаково выглядит и в светлой, и в тёмной теме.</summary>
public sealed class TileColorConverter : IValueConverter
{
    public static readonly TileColorConverter Instance = new();

    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#2563EB")),
        new SolidColorBrush(Color.Parse("#0F766E")),
        new SolidColorBrush(Color.Parse("#7C3AED")),
        new SolidColorBrush(Color.Parse("#B45309")),
        new SolidColorBrush(Color.Parse("#BE123C")),
        new SolidColorBrush(Color.Parse("#15803D")),
        new SolidColorBrush(Color.Parse("#0369A1")),
        new SolidColorBrush(Color.Parse("#A21CAF")),
        new SolidColorBrush(Color.Parse("#4D7C0F")),
        new SolidColorBrush(Color.Parse("#C2410C")),
        new SolidColorBrush(Color.Parse("#334155")),
        new SolidColorBrush(Color.Parse("#9D174D")),
    ];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        // Свой устойчивый хеш: string.GetHashCode меняется от запуска к запуску.
        uint hash = 2166136261;
        foreach (var ch in text)
            hash = (hash ^ ch) * 16777619;
        return Palette[hash % (uint)Palette.Length];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Сумма для таблиц и итогов раскладок: «1 234.50» — разряды через узкий пробел, копейки
/// через точку, как везде в кассе. Принимает число или строку-число ("1234.50").</summary>
public sealed class MoneyConverter : IValueConverter
{
    public static readonly MoneyConverter Instance = new();

    private static readonly NumberFormatInfo Format = new()
    {
        NumberDecimalSeparator = ".",
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3],
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double amount;
        switch (value)
        {
            case double d: amount = d; break;
            case decimal m: amount = (double)m; break;
            case float f: amount = f; break;
            case int i: amount = i; break;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed): amount = parsed; break;
            default: return value?.ToString() ?? "";
        }

        return amount.ToString("#,0.00", Format);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true, когда число равно параметру — подсветка выбранной вкладки каталога
/// (Catalog.SelectedTabIndex) у чипов раскладок.</summary>
public sealed class IndexEqualsConverter : IValueConverter
{
    public static readonly IndexEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter?.ToString(), out var expected) && index == expected;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true для непустой строки.</summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public static readonly NotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
