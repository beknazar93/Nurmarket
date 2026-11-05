using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №4, А4-09): чтение картинки статического QR банка на Android и Linux без System.Drawing — через Avalonia Bitmap.
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

    private static (byte[] Bgra, int Width, int Height)? Read(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
                return null;
            var stride = width * 4;
            var buffer = new byte[stride * height];
            var native = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), native, buffer.Length, stride);
                Marshal.Copy(native, buffer, 0, buffer.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }

            var rgba = bitmap.Format == PixelFormat.Rgba8888;
            for (var i = 0; i < buffer.Length; i += 4)
            {
                var b = rgba ? buffer[i + 2] : buffer[i];
                var g = buffer[i + 1];
                var r = rgba ? buffer[i] : buffer[i + 2];
                var a = buffer[i + 3];
                if (a < 255)
                {
                    // Прозрачное — на белый фон (цвета в кадре могут быть с предумножением альфы).
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
