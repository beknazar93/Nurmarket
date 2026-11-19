using System.IO.Compression;
using SkiaSharp;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-19 (снимки базы знаний 2.0): сжатие картинок для Assets/kb без новых зависимостей — палитра до 256 цветов (медианное сечение),
/// PNG с палитрой (zlib из .NET) и GIF89a (LZW) для обучающих анимаций. Интерфейсные снимки — плоские цвета и сглаженный текст, 256 цветов хватает;
/// размер файла в 3–5 раз меньше обычного PNG, поэтому установщик почти не растёт.</summary>
public static class KbImageEncoder
{
    /// <summary>Результат квантования: палитра RGB (до 256 цветов) и индекс цвета на каждый пиксель.</summary>
    public sealed record Indexed(int Width, int Height, byte[] Palette, int PaletteSize, byte[] Pixels);

    public static Indexed Quantize(SKBitmap source, int maxColors = 256)
    {
        // Прозрачные углы окон (скруглённая рамка) кладём на белый фон — иначе альфа отбрасывается и углы чернеют.
        using var bmp = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }
        var w = bmp.Width;
        var h = bmp.Height;
        var bgra = true;
        var raw = bmp.GetPixelSpan().ToArray();
        BlackFrameToWhite(raw, w, h);
        // Цвет пикселя → 0xRRGGBB (кадры непрозрачные: окно снимается на сплошном фоне).
        var keys = new int[w * h];
        var counts = new Dictionary<int, int>(1 << 14);
        for (var i = 0; i < keys.Length; i++)
        {
            var o = i * 4;
            var r = bgra ? raw[o + 2] : raw[o];
            var g = raw[o + 1];
            var b = bgra ? raw[o] : raw[o + 2];
            var key = (r << 16) | (g << 8) | b;
            keys[i] = key;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var entries = counts.Select(kv => (Key: kv.Key, Count: kv.Value)).ToList();
        var boxes = new List<List<(int Key, int Count)>> { entries };
        while (boxes.Count < maxColors)
        {
            // Делим коробку с наибольшим «весом разброса» по самому длинному каналу в медиане по количеству пикселей.
            var idx = -1;
            long best = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2)
                    continue;
                var score = Spread(boxes[i], out _);
                if (score > best)
                {
                    best = score;
                    idx = i;
                }
            }
            if (idx < 0 || best == 0)
                break;
            var box = boxes[idx];
            Spread(box, out var channel);
            var sorted = box.OrderBy(e => (e.Key >> channel) & 255).ToList();
            var total = sorted.Sum(e => (long)e.Count);
            long run = 0;
            var cut = 1;
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                run += sorted[i].Count;
                cut = i + 1;
                if (run * 2 >= total)
                    break;
            }
            boxes[idx] = sorted.Take(cut).ToList();
            boxes.Add(sorted.Skip(cut).ToList());
        }

        var palette = new byte[boxes.Count * 3];
        var map = new Dictionary<int, byte>(entries.Count);
        for (var bi = 0; bi < boxes.Count; bi++)
        {
            long sr = 0, sg = 0, sb = 0, n = 0;
            foreach (var (key, count) in boxes[bi])
            {
                sr += (long)((key >> 16) & 255) * count;
                sg += (long)((key >> 8) & 255) * count;
                sb += (long)(key & 255) * count;
                n += count;
                map[key] = (byte)bi;
            }
            palette[bi * 3] = (byte)(sr / n);
            palette[bi * 3 + 1] = (byte)(sg / n);
            palette[bi * 3 + 2] = (byte)(sb / n);
        }
        // Самые частые цвета (фон, белый, чёрный текст) оставляем точными: палитра по средней могла их сдвинуть на 1–2 единицы.
        foreach (var top in entries.OrderByDescending(e => e.Count).Take(8))
        {
            var bi = map[top.Key];
            if (boxes[bi].Count == 1 || boxes[bi].MaxBy(e => e.Count).Key == top.Key)
            {
                palette[bi * 3] = (byte)((top.Key >> 16) & 255);
                palette[bi * 3 + 1] = (byte)((top.Key >> 8) & 255);
                palette[bi * 3 + 2] = (byte)(top.Key & 255);
            }
        }
        var pixels = new byte[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            pixels[i] = map[keys[i]];
        return new Indexed(w, h, palette, boxes.Count, pixels);
    }

    /// <summary>Копия кадра, где чёрный фон Headless вокруг закруглённого окна (связанный с краем) стал прозрачным — чтобы наложить окно на другой кадр.</summary>
    public static SKBitmap BlackFrameToTransparent(SKBitmap source)
    {
        var copy = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(copy))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
        }
        var raw = copy.GetPixelSpan().ToArray();
        BlackFrameToWhite(raw, copy.Width, copy.Height, transparent: true);
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, copy.GetPixels(), raw.Length);
        return copy;
    }

    /// <summary>В Headless окно с закруглённой рамкой снимается на чёрном фоне: чёрные пиксели, связанные с краем кадра, делаем белыми
    /// (чёрные плашки внутри окна с краем не связаны — их не трогаем).</summary>
    private static void BlackFrameToWhite(byte[] raw, int w, int h, bool transparent = false)
    {
        bool IsBlack(int i) => raw[i * 4] < 12 && raw[i * 4 + 1] < 12 && raw[i * 4 + 2] < 12;
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        void Push(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
                return;
            var i = y * w + x;
            if (seen[i] || !IsBlack(i))
                return;
            seen[i] = true;
            stack.Push(i);
        }
        for (var x = 0; x < w; x++) { Push(x, 0); Push(x, h - 1); }
        for (var y = 0; y < h; y++) { Push(0, y); Push(w - 1, y); }
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            raw[i * 4] = raw[i * 4 + 1] = raw[i * 4 + 2] = transparent ? (byte)0 : (byte)255;
            raw[i * 4 + 3] = transparent ? (byte)0 : (byte)255;
            int x = i % w, y = i / w;
            Push(x + 1, y); Push(x - 1, y); Push(x, y + 1); Push(x, y - 1);
        }
    }

    private static long Spread(List<(int Key, int Count)> box, out int channel)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        long weight = 0;
        foreach (var (key, count) in box)
        {
            int r = (key >> 16) & 255, g = (key >> 8) & 255, b = key & 255;
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            if (g < minG) minG = g;
            if (g > maxG) maxG = g;
            if (b < minB) minB = b;
            if (b > maxB) maxB = b;
            weight += count;
        }
        int dr = maxR - minR, dg = maxG - minG, db = maxB - minB;
        // Зелёный глаз различает лучше всего — его разброс весит чуть больше.
        var max = Math.Max(dr, Math.Max(dg * 6 / 5, db));
        channel = dg * 6 / 5 == max ? 8 : dr == max ? 16 : 0;
        return (long)max * (long)Math.Sqrt(weight);
    }

    // ------------------------------------------------------------------ PNG

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        var q = Quantize(bitmap);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, q.Width);
        WriteBe(ihdr, 4, q.Height);
        ihdr[8] = 8; // 8 бит на индекс
        ihdr[9] = 3; // палитра
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "PLTE", q.Palette.AsSpan(0, q.PaletteSize * 3).ToArray());

        // Фильтры строк: для каждой строки берём лучший из «нет», «слева», «сверху» по сумме модулей.
        var w = q.Width;
        var raw = new byte[(w + 1) * q.Height];
        var none = new byte[w];
        var sub = new byte[w];
        var up = new byte[w];
        for (var y = 0; y < q.Height; y++)
        {
            var row = q.Pixels.AsSpan(y * w, w);
            long sNone = 0, sSub = 0, sUp = 0;
            for (var x = 0; x < w; x++)
            {
                none[x] = row[x];
                sub[x] = (byte)(row[x] - (x > 0 ? row[x - 1] : 0));
                up[x] = (byte)(row[x] - (y > 0 ? q.Pixels[(y - 1) * w + x] : 0));
                sNone += Math.Abs((int)(sbyte)none[x]);
                sSub += Math.Abs((int)(sbyte)sub[x]);
                sUp += Math.Abs((int)(sbyte)up[x]);
            }
            byte type = 0;
            var chosen = none;
            var best = sNone;
            if (sSub < best) { best = sSub; type = 1; chosen = sub; }
            if (sUp < best) { type = 2; chosen = up; }
            raw[y * (w + 1)] = type;
            Buffer.BlockCopy(chosen, 0, raw, y * (w + 1) + 1, w);
        }
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, true))
                zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteBe(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
            typed[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typed, 4, data.Length);
        s.Write(typed);
        var crc = Crc32(typed);
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 255] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ------------------------------------------------------------------ GIF

    /// <summary>Анимированный GIF: полные кадры (без дельты — так их читает окно базы знаний), у каждого своя палитра, пауза в сотых долях секунды.</summary>
    public static byte[] EncodeGif(IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> delaysCs)
    {
        using var ms = new MemoryStream();
        var w = frames[0].Width;
        var h = frames[0].Height;
        ms.Write("GIF89a"u8);
        ms.Write([(byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0x00, 0, 0]); // без общей палитры
        // Петля: бесконечно.
        ms.Write([0x21, 0xFF, 0x0B]);
        ms.Write("NETSCAPE2.0"u8);
        ms.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
        for (var f = 0; f < frames.Count; f++)
        {
            var q = Quantize(frames[f]);
            var bits = 1;
            while ((1 << bits) < q.PaletteSize)
                bits++;
            bits = Math.Max(bits, 2);
            var size = 1 << bits;
            var delay = delaysCs[f];
            ms.Write([0x21, 0xF9, 0x04, 0x00, (byte)delay, (byte)(delay >> 8), 0x00, 0x00]);
            ms.Write([0x2C, 0, 0, 0, 0, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), (byte)(0x80 | (bits - 1))]);
            var pal = new byte[size * 3];
            Buffer.BlockCopy(q.Palette, 0, pal, 0, q.PaletteSize * 3);
            ms.Write(pal);
            ms.WriteByte((byte)bits);
            var lzw = Lzw(q.Pixels, bits);
            for (var o = 0; o < lzw.Length; o += 255)
            {
                var n = Math.Min(255, lzw.Length - o);
                ms.WriteByte((byte)n);
                ms.Write(lzw, o, n);
            }
            ms.WriteByte(0);
        }
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    private static byte[] Lzw(byte[] pixels, int minCodeSize)
    {
        var output = new MemoryStream();
        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var codeSize = minCodeSize + 1;
        var next = eoi + 1;
        var dict = new Dictionary<int, int>();
        uint buffer = 0;
        var nbits = 0;

        void Emit(int code)
        {
            buffer |= (uint)code << nbits;
            nbits += codeSize;
            while (nbits >= 8)
            {
                output.WriteByte((byte)(buffer & 255));
                buffer >>= 8;
                nbits -= 8;
            }
        }

        Emit(clear);
        int prefix = pixels[0];
        for (var i = 1; i < pixels.Length; i++)
        {
            var c = pixels[i];
            var key = (prefix << 8) | c;
            if (dict.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }
            Emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            else
            {
                Emit(clear);
                dict.Clear();
                codeSize = minCodeSize + 1;
                next = eoi + 1;
            }
            prefix = c;
        }
        Emit(prefix);
        Emit(eoi);
        if (nbits > 0)
            output.WriteByte((byte)(buffer & 255));
        return output.ToArray();
    }
}
