using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>Converts a hex color string (e.g. #DC2626) to <see cref="IBrush"/>.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(hex.Trim()));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Invalid color value ignored: {ex.GetType().Name}", "DEBUG");
            }
        }

        return new SolidColorBrush(Color.Parse("#64748B"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
