using System.IO;
using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-05 (аудит скорости, п. 15 а): миниатюры товаров лежали в кэше оригиналами с сайта (медиана 900 px, максимум 6022 px,
/// 195 файлов = 15,2 МБ), а показываются в 36–180 px. Теперь при скачивании фото ужимается до 320 px по длинной стороне и
/// сохраняется WebP (качество 80). Если в сборке Skia нет WebP-кодека — JPEG 80 %, а для картинки с прозрачностью — PNG.
/// Тот же класс пересжимает старые тяжёлые файлы лениво, в фоне (<see cref="TryRecompressFile"/>).
/// Только Skia (уже подключена через Avalonia), никаких новых пакетов; вызывать вне потока интерфейса.
/// </summary>
public static class ThumbImageCompressor
{
    /// <summary>Длинная сторона миниатюры. 320 px = ширина декодирования плитки на экране с масштабом 200 %.</summary>
    public const int ThumbMaxSide = 320;

    /// <summary>Длинная сторона «большого» фото для карточки товара (просмотр на весь экран).</summary>
    public const int FullMaxSide = 1600;

    /// <summary>Часть имени файла нового формата: <c>&lt;sha&gt;.t320.webp</c>. По ней конвертер отличает готовую миниатюру от старого оригинала.</summary>
    public const string ThumbMarker = ".t320.";

    /// <summary>Сжать картинку: не больше <paramref name="maxSide"/> по длинной стороне, пропорции и прозрачность сохраняются,
    /// маленькие картинки не увеличиваются. Возвращает null, если данные не картинка или файл повреждён (исключений наружу не бросает).
    /// <paramref name="extension"/> — расширение результата с точкой: .webp, .jpg или .png.</summary>
    public static byte[]? Compress(byte[] source, int maxSide, int quality, out string extension)
    {
        extension = ".webp";
        if (source is null || source.Length < 16 || maxSide < 16)
            return null;
        try
        {
            using var data = SKData.CreateCopy(source);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                return null;
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0)
                return null;

            using var decoded = DecodeScaled(codec, maxSide);
            if (decoded is null)
                return null;

            var side = Math.Max(decoded.Width, decoded.Height);
            SKBitmap? resized = null;
            try
            {
                var final = decoded;
                if (side > maxSide)
                {
                    var k = (double)maxSide / side;
                    var nw = Math.Max(1, (int)Math.Round(decoded.Width * k));
                    var nh = Math.Max(1, (int)Math.Round(decoded.Height * k));
                    resized = decoded.Resize(new SKImageInfo(nw, nh, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                    if (resized is null)
                        return null;
                    final = resized;
                }

                var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque && HasTransparentPixel(final);
                using var image = SKImage.FromBitmap(final);

                using (var webp = image.Encode(SKEncodedImageFormat.Webp, quality))
                {
                    if (webp is not null && webp.Size > 0)
                    {
                        extension = ".webp";
                        return webp.ToArray();
                    }
                }

                // WebP-кодека нет: JPEG (без прозрачности) или PNG (с прозрачностью — JPEG залил бы её чёрным).
                if (hasAlpha)
                {
                    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (png is null || png.Size == 0)
                        return null;
                    extension = ".png";
                    return png.ToArray();
                }

                using var jpg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (jpg is null || jpg.Size == 0)
                    return null;
                extension = ".jpg";
                return jpg.ToArray();
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Расшифровать сразу в уменьшенном виде, если кодек умеет (WebP/JPEG): фото 6000 px не раскрывается в 96 МБ пикселей.</summary>
    private static SKBitmap? DecodeScaled(SKCodec codec, int maxSide)
    {
        var info = codec.Info;
        var side = Math.Max(info.Width, info.Height);
        // Берём масштаб с запасом ×2 над целью — финальное уменьшение фильтром High даёт чистую картинку.
        if (side >= maxSide * 2)
        {
            try
            {
                var scaled = codec.GetScaledDimensions((float)maxSide * 2 / side);
                if (scaled.Width > 0 && scaled.Height > 0 && (scaled.Width < info.Width || scaled.Height < info.Height))
                {
                    var scaledInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    var bmp = new SKBitmap(scaledInfo);
                    var result = codec.GetPixels(scaledInfo, bmp.GetPixels());
                    if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                        return bmp;
                    bmp.Dispose();
                }
            }
            catch
            {
                // не вышло уменьшенно — ниже полная расшифровка
            }
        }

        var fullInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var full = new SKBitmap(fullInfo);
        var res = codec.GetPixels(fullInfo, full.GetPixels());
        if (res is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return full;
        full.Dispose();
        return null;
    }

    private static bool HasTransparentPixel(SKBitmap bitmap)
    {
        // Достаточно редкой сетки: прозрачность фото товара — это поля вокруг предмета или целая область.
        var stepX = Math.Max(1, bitmap.Width / 32);
        var stepY = Math.Max(1, bitmap.Height / 32);
        for (var y = 0; y < bitmap.Height; y += stepY)
            for (var x = 0; x < bitmap.Width; x += stepX)
                if (bitmap.GetPixel(x, y).Alpha < 250)
                    return true;
        return false;
    }

    /// <summary>Сжать файл <paramref name="sourcePath"/> и записать результат в <paramref name="destPath"/> атомарно
    /// (временный файл + подмена): оригинал не трогается, пока новый файл не записан целиком. Возвращает false, если исходник
    /// повреждён или запись не удалась — тогда на диске остаётся только оригинал.</summary>
    public static bool TryRecompressFile(string sourcePath, string destPath, int maxSide, int quality)
    {
        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var packed = Compress(bytes, maxSide, quality, out _);
            return packed is not null && TryWriteAtomic(destPath, packed);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Записать байты во временный файл рядом и подменить ими <paramref name="path"/>; при ошибке временный файл удаляется.</summary>
    public static bool TryWriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* временный файл не критичен */ }
            return false;
        }
    }
}
