using System.Collections.Concurrent;
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

    /// <summary>ConverterParameter="thumb" (2026-09-07, оптимизация для слабых ПК): файлы в
    /// product_thumbs — это полноразмерные фото с сайта по 400–700 КБ (WebP 1000+ px), а
    /// показываются они в 36–180 px. new Bitmap(path) декодировал их целиком в UI-потоке
    /// (по 10–20 МБ RGBA на каждый, десятки штук на странице каталога — заметный фриз при первом
    /// показе и сотни МБ памяти). С параметром "thumb" картинка декодируется сразу в ширину
    /// 320 px. Обои кассы и фон настроек параметр не передают и грузятся в полном размере.</summary>
    private const string ThumbParameter = "thumb";
    private const int ThumbDecodeWidth = 320;

    // Catalog resyncs rebuild product tile view-models every ~45s, which re-triggers
    // this converter for every visible tile with the same path — caching avoids
    // re-decoding the same file from disk on every resync (a real freeze on weak CPUs/disks).
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        var thumb = parameter is string mode
            && string.Equals(mode, ThumbParameter, StringComparison.OrdinalIgnoreCase);
        var cacheKey = thumb ? path + "|" + ThumbParameter : path;

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (thumb)
                TouchThumb(cacheKey);
            return cached;
        }

        Bitmap? result = null;
        try
        {
            if (TryLoadFromFileSystem(path, thumb, out var fileBitmap))
            {
                result = fileBitmap;
            }
            else
            {
                foreach (var candidate in EnumerateAssetCandidates(path))
                {
                    if (TryLoadFromAvares(candidate, out var assetBitmap))
                    {
                        result = assetBitmap;
                        break;
                    }
                }
            }
        }
        catch
        {
            result = null;
        }

        AddNativePressure(result);
        _cache[cacheKey] = result;
        if (thumb)
            TouchThumb(cacheKey);
        return result;
    }

    // 2026-10-20 (живой аудит кассы, К15: память кассы 496 → 1009 МБ за 75 минут): миниатюры товаров копились в кэше без
    // предела — каждая декодированная (≈0,4 МБ пикселей вне управляемой кучи) жила до конца работы, и листание каталога
    // в несколько тысяч товаров съедало сотни МБ. Теперь в кэше — последние MaxThumbs миниатюр (несколько страниц
    // каталога); вытесненная не освобождается принудительно (её ещё может показывать плитка) — её соберёт сборщик мусора,
    // когда плитка её отпустит. Обои, фон и значки (без «thumb») — как раньше, без предела.
    private const int MaxThumbs = 240;
    private static readonly LinkedList<string> ThumbOrder = new();
    private static readonly Dictionary<string, LinkedListNode<string>> ThumbNodes = new(StringComparer.Ordinal);
    private static readonly object ThumbGate = new();

    private static void TouchThumb(string key)
    {
        lock (ThumbGate)
        {
            if (ThumbNodes.TryGetValue(key, out var node))
            {
                ThumbOrder.Remove(node);
                ThumbOrder.AddFirst(node);
                return;
            }

            ThumbNodes[key] = ThumbOrder.AddFirst(key);
            while (ThumbOrder.Count > MaxThumbs && ThumbOrder.Last is { } oldest)
            {
                ThumbOrder.RemoveLast();
                ThumbNodes.Remove(oldest.Value);
                _cache.TryRemove(oldest.Value, out _);
            }
        }
    }

    // 2026-10-23 (живой аудит, круг 6: Склад растёт на 6–8 МБ Private за открытие; стенд p10-leak): пиксели миниатюры лежат в
    // родной памяти Skia (≈0,3 МБ), а управляемый объект Bitmap крошечный — сборщик мусора не видит давления и не запускает
    // сборку второго поколения, так что вытесненные из кэша миниатюры висели сотнями МБ (стенд: 750 МБ за 8 проходов по 300
    // товаров, после GC.Collect — 100). Сообщаем сборщику размер пикселей; снимается, когда Bitmap собран (финализатор метки).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, NativePressure> PressureTokens = new();

    private sealed class NativePressure
    {
        private readonly long _bytes;
        public NativePressure(long bytes) { _bytes = bytes; GC.AddMemoryPressure(bytes); }
        ~NativePressure() => GC.RemoveMemoryPressure(_bytes);
    }

    private static void AddNativePressure(Bitmap? bitmap)
    {
        if (bitmap is null)
            return;
        try
        {
            var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            if (bytes > 0)
                PressureTokens.Add(bitmap, new NativePressure(bytes));
        }
        catch
        {
            // давление памяти — только подсказка сборщику; ошибка не должна ломать показ картинки
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryLoadFromFileSystem(string path, bool thumb, out Bitmap? bitmap)
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

        if (thumb)
        {
            using var stream = File.OpenRead(filePath);
            bitmap = Bitmap.DecodeToWidth(stream, ThumbDecodeWidth);
            return true;
        }

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
