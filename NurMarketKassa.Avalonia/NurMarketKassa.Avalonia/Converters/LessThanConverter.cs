using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class LessThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double v && parameter is string s &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
        {
            return v < threshold;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
