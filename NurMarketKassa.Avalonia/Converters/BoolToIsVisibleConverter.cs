using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Converts bool to IsVisible bool. Pass ConverterParameter "Invert" to invert.</summary>
public sealed class BoolToIsVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (IsInvert(parameter))
            visible = !visible;
        return visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (IsInvert(parameter))
            visible = !visible;
        return visible;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s &&
        s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
