using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NurMarketKassa.AvaloniaHost.Converters;

/// <summary>
/// Resolves pack://, avares://, file names and absolute paths to <see cref="Bitmap"/>.
/// </summary>
public sealed class AssetPathToBitmapConverter : IValueConverter
{
    public static readonly AssetPathToBitmapConverter Instance = new();

    private const string AvaloniaAssetsRoot = "avares://NurMarketKassa.Avalonia/Assets/";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (TryLoadFromFileSystem(path, out var fileBitmap))
                return fileBitmap;

            foreach (var candidate in EnumerateAssetCandidates(path))
            {
                if (TryLoadFromAvares(candidate, out var assetBitmap))
                    return assetBitmap;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryLoadFromFileSystem(string path, out Bitmap? bitmap)
    {
        bitmap = null;

        if (path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
            return false;

        var filePath = path;
        if (!Path.IsPathRooted(filePath) && File.Exists(Path.Combine(AppContext.BaseDirectory, filePath)))
            filePath = Path.Combine(AppContext.BaseDirectory, filePath);

        if (!File.Exists(filePath))
            return false;

        bitmap = new Bitmap(filePath);
        return true;
    }

    private static IEnumerable<string> EnumerateAssetCandidates(string path)
    {
        if (path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            yield return path;
            yield break;
        }

        if (path.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = ExtractFileName(path);
            if (!string.IsNullOrEmpty(fileName))
                yield return AvaloniaAssetsRoot + fileName;
            yield break;
        }

        if (path.Contains('/'))
        {
            var fileName = ExtractFileName(path);
            if (!string.IsNullOrEmpty(fileName))
                yield return AvaloniaAssetsRoot + fileName;
        }

        yield return AvaloniaAssetsRoot + path.TrimStart('/');
    }

    private static string? ExtractFileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    private static bool TryLoadFromAvares(string uriString, out Bitmap? bitmap)
    {
        bitmap = null;

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return false;

        if (!AssetLoader.Exists(uri))
            return false;

        using var stream = AssetLoader.Open(uri);
        bitmap = new Bitmap(stream);
        return true;
    }
}
