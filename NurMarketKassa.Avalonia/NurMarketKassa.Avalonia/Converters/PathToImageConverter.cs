using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NurMarketKassa.Converters;

public class PathToImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string rawPath && !string.IsNullOrWhiteSpace(rawPath))
        {
            if (_cache.TryGetValue(rawPath, out var cached))
                return cached;

            Bitmap? result = null;
            try
            {
                // 1. Если путь уже avares://
                if (rawPath.StartsWith("avares://"))
                {
                    result = new Bitmap(AssetLoader.Open(new Uri(rawPath)));
                }
                // 2. Если указан относительный путь к Assets
                else if (rawPath.StartsWith("Assets/") || rawPath.StartsWith("Assets\\"))
                {
                    var uri = new Uri($"avares://NurMarketKassa.Avalonia/{rawPath.Replace('\\', '/')}");
                    result = new Bitmap(AssetLoader.Open(uri));
                }
                // 3. Если это прямой путь к файлу на диске
                else if (System.IO.File.Exists(rawPath))
                {
                    result = new Bitmap(rawPath);
                }
            }
            catch
            {
                result = null;
            }

            _cache[rawPath] = result;
            return result;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}