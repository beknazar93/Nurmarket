using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Maps bool to IsVisible (bool). ConverterParameter "Invert" flips the value.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
            flag = !flag;
        return flag;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
            flag = !flag;
        return flag;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
