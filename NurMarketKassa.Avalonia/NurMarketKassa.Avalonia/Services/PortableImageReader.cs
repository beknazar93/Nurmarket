using System;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №4, А4-09): чтение картинки статического QR банка на Android и Linux без System.Drawing — через Skia (SKCodec).
/// Отдаёт пиксели BGRA на белом фоне (прозрачные места QR становятся белыми) для распознавания ZXing (DynamicPaymentQrService).</summary>
internal static class PortableImageReader
{
    public static void Install() => DynamicPaymentQrService.PlatformImageReader = Read;

    /// <summary>Подставляется при загрузке сборки на Android и Linux; в Windows читается через System.Drawing.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AutoInstall()
    {
        if (!OperatingSystem.IsWindows())
            Install();
    }

    /// <summary>2026-11-06 (аудит №5, А5-13): раньше размер (предел 40 Мп) проверялся ПОСЛЕ <c>new Bitmap(path)</c>, когда память уже занята,
    /// а серая и 1-битная PNG могла прийти не в 32-битном виде. Теперь размер берётся из заголовка файла (SKCodec) до расшифровки,
    /// а пиксели всегда запрашиваются в Bgra8888 — Skia сама приводит серые/палитровые/1-битные картинки к нему.</summary>
    internal const long MaxPixels = 40_000_000;

    internal static bool IsTooLarge(int width, int height) =>
        width <= 0 || height <= 0 || (long)width * height > MaxPixels;

    internal static (byte[] Bgra, int Width, int Height)? Read(string path)
    {
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(path);
            if (codec is null)
                return null;
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (IsTooLarge(width, height))
                return null;

            var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            var res = codec.GetPixels(info, bitmap.GetPixels());
            if (res is not (SkiaSharp.SKCodecResult.Success or SkiaSharp.SKCodecResult.IncompleteInput))
                return null;

            var buffer = bitmap.Bytes; // копия: Bgra8888, строка = width * 4
            for (var i = 0; i < buffer.Length; i += 4)
            {
                var b = buffer[i];
                var g = buffer[i + 1];
                var r = buffer[i + 2];
                var a = buffer[i + 3];
                if (a < 255)
                {
                    // Прозрачное — на белый фон (цвета в кадре с предумножением альфы).
                    b = (byte)Math.Min(255, b + (255 - a));
                    g = (byte)Math.Min(255, g + (255 - a));
                    r = (byte)Math.Min(255, r + (255 - a));
                }
                buffer[i] = b;
                buffer[i + 1] = g;
                buffer[i + 2] = r;
                buffer[i + 3] = 255;
            }
            return (buffer, width, height);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Картинка QR не прочитана: {ex.Message}", "QR");
            return null;
        }
    }
}
