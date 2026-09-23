using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>
/// Maps overlay density (0.05–0.8) to a white brush with matching alpha.
/// Higher value → denser white underlay; wallpaper Image stays at Opacity=1.
/// </summary>
public sealed class OpacityToWhiteOverlayBrushConverter : IValueConverter
{
    public static readonly OpacityToWhiteOverlayBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var opacity = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.15,
        };

        opacity = Math.Clamp(opacity, 0.05, 0.8);
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 13, 204);
        return new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
