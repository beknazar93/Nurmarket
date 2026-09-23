using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NurMarketKassa.Converters;

public class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string rawPath && !string.IsNullOrWhiteSpace(rawPath))
        {
            try
            {
                // 1. Если путь уже avares://
                if (rawPath.StartsWith("avares://"))
                {
                    return new Bitmap(AssetLoader.Open(new Uri(rawPath)));
                }

                // 2. Если указан относительный путь к Assets
                if (rawPath.StartsWith("Assets/") || rawPath.StartsWith("Assets\\"))
                {
                    var uri = new Uri($"avares://NurMarketKassa.Avalonia/{rawPath.Replace('\\', '/')}");
                    return new Bitmap(AssetLoader.Open(uri));
                }

                // 3. Если это прямой путь к файлу на диске
                if (System.IO.File.Exists(rawPath))
                {
                    return new Bitmap(rawPath);
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}