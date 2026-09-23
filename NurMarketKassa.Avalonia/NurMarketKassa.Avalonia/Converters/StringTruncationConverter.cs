using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class StringTruncationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        var input = value.ToString() ?? string.Empty;
        var maxLength = 30;

        if (parameter is string paramStr && int.TryParse(paramStr, out var paramLength))
            maxLength = paramLength;
        else if (parameter is int intParam)
            maxLength = intParam;

        return input.Length > maxLength
            ? input[..maxLength] + "..."
            : input;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
