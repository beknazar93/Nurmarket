using System.Globalization;
using Avalonia.Data.Converters;

namespace NurMarketKassa.AvaloniaHost.Converters;

public sealed class PriceAmountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string priceLine || string.IsNullOrWhiteSpace(priceLine))
            return "—";

        const string suffix = " сом";
        var idx = priceLine.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? priceLine[..idx].Trim() : priceLine.Trim();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
