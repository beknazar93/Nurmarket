using SkiaSharp;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06: картинка товара (JPEG/PNG/WebP/…) → BMP 288×288 для главного экрана весов «Штрих РС-200». Вписывается с сохранением пропорций на белый
/// квадрат (прозрачность ложится на белое), BMP пишется вручную (<see cref="ShtrikhPcBmp"/>) — работает на Windows, Linux и Android без System.Drawing.
/// Только Skia (уже подключена через Avalonia). Вызывать вне потока интерфейса.</summary>
public static class ShtrikhPcPictureConverter
{
    /// <summary>null — не картинка, повреждена или слишком большая для слабого устройства.</summary>
    public static byte[]? ToBmp(byte[] source, int size = ShtrikhPcBmp.PictureSize)
    {
        if (source is null || source.Length < 16)
            return null;
        try
        {
            // Огромные PNG/GIF/BMP целиком в память не берём (как ThumbImageCompressor).
            if (ThumbImageCompressor.IsOversized(source, ThumbImageCompressor.MaxNonScalablePixels))
                return null;
            using var decoded = SKBitmap.Decode(source);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                return null;

            var (x, y, w, h) = ShtrikhPcBmp.FitRect(decoded.Width, decoded.Height, size);
            using var fitted = decoded.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            if (fitted is null)
                return null;

            using var target = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(fitted, x, y);
            }
            return ShtrikhPcBmp.Encode24(target.Bytes, size, size);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
