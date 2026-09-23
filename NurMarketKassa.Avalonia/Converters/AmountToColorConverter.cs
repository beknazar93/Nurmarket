using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class AmountToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal amount && amount < 0m)
            return new SolidColorBrush(Colors.Red);

        return new SolidColorBrush(Colors.Black);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
