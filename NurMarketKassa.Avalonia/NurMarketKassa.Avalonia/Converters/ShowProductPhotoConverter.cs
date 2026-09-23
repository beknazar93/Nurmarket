using System.Globalization;
using Avalonia.Data.Converters;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>True only when the tile has a cached photo AND the cashier-catalog photo
/// toggle (Settings) is on — lets the setting hide photos without touching every tile.</summary>
public sealed class ShowProductPhotoConverter : IValueConverter
{
    public static readonly ShowProductPhotoConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        UserPreferences.Instance.ShowCatalogPhotos && value is string { Length: > 0 };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
