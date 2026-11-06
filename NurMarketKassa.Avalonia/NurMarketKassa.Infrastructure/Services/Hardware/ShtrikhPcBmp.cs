namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-06: картинки товаров для весов «Штрих РС-200» — на их главном экране видны только товары с картинкой (<c>Pictures/&lt;код&gt;.bmp</c>, 288×288).
/// Здесь — чистая логика без Skia: вписывание с сохранением пропорций и запись BMP вручную (Skia BMP не кодирует; System.Drawing не нужен — работает на Linux и Android):
/// заголовок BITMAPFILEHEADER + BITMAPINFOHEADER, 24 бита, строки по 4 байта, снизу вверх, без прозрачности (она ложится на белый фон).</summary>
public static class ShtrikhPcBmp
{
    /// <summary>Сторона квадрата картинки товара на весах.</summary>
    public const int PictureSize = 288;

    /// <summary>Размер служебной части BMP: 14 (файл) + 40 (изображение).</summary>
    public const int HeaderSize = 54;

    /// <summary>Куда поместить картинку <paramref name="srcWidth"/>×<paramref name="srcHeight"/> в квадрат <paramref name="size"/>: по центру, пропорции сохраняются,
    /// длинная сторона равна <paramref name="size"/> (маленькие картинки увеличиваются — на весах плитка всегда 288).</summary>
    public static (int X, int Y, int Width, int Height) FitRect(int srcWidth, int srcHeight, int size = PictureSize)
    {
        if (srcWidth <= 0 || srcHeight <= 0)
            return (0, 0, size, size);
        var scale = Math.Min((double)size / srcWidth, (double)size / srcHeight);
        var w = Math.Clamp((int)Math.Round(srcWidth * scale), 1, size);
        var h = Math.Clamp((int)Math.Round(srcHeight * scale), 1, size);
        return ((size - w) / 2, (size - h) / 2, w, h);
    }

    /// <summary>BMP 24 бита из пикселей BGRA (строка = width × 4, сверху вниз). Альфа, если она есть, накладывается на белый фон.</summary>
    public static byte[] Encode24(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            throw new ArgumentException("Размер пикселей не совпадает с шириной и высотой.", nameof(bgra));

        var rowBytes = width * 3;
        var stride = (rowBytes + 3) & ~3; // строки BMP кратны 4 байтам
        var imageSize = stride * height;
        var file = new byte[HeaderSize + imageSize];

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        WriteInt(file, 2, file.Length);          // размер файла
        WriteInt(file, 10, HeaderSize);          // смещение пикселей
        WriteInt(file, 14, 40);                  // размер BITMAPINFOHEADER
        WriteInt(file, 18, width);
        WriteInt(file, 22, height);              // положительная высота — строки снизу вверх
        file[26] = 1;                            // плоскостей
        file[28] = 24;                           // бит на пиксель
        WriteInt(file, 34, imageSize);
        WriteInt(file, 38, 2835);                // 72 dpi
        WriteInt(file, 42, 2835);

        for (var y = 0; y < height; y++)
        {
            var src = y * width * 4;
            var dst = HeaderSize + (height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var b = bgra[src];
                var g = bgra[src + 1];
                var r = bgra[src + 2];
                var a = bgra[src + 3];
                if (a < 255)
                {
                    b = OverWhite(b, a);
                    g = OverWhite(g, a);
                    r = OverWhite(r, a);
                }
                file[dst] = b;
                file[dst + 1] = g;
                file[dst + 2] = r;
                src += 4;
                dst += 3;
            }
        }
        return file;
    }

    private static byte OverWhite(byte c, byte alpha) => (byte)((c * alpha + 255 * (255 - alpha)) / 255);

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
