using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class StringToIsVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string;
        return !string.IsNullOrEmpty(str);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
