using SkiaSharp;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-26 (просьба владельца: «функции в Маркетплейс — с гиф, как работает»): настоящий GIF из кадров снимка окна — без новых пакетов (SkiaSharp GIF не пишет).
/// Одна общая палитра на 255 цветов + прозрачность не нужна; кадры одного размера; LZW как в GIF89a (min code size 8, коды до 12 бит, очистка таблицы при заполнении).
/// Сжатие хорошее: интерфейс — плоские цвета (кадр 640×340 ≈ 20–40 КБ).</summary>
public static class GifWriter
{
    /// <summary>Собрать GIF: кадры одного размера, задержка кадра в сотых секунды, бесконечный цикл.</summary>
    public static byte[] Encode(IReadOnlyList<SKBitmap> frames, int delayCentiseconds)
    {
        if (frames.Count == 0)
            throw new ArgumentException("Нет кадров", nameof(frames));
        int w = frames[0].Width, h = frames[0].Height;
        if (frames.Any(f => f.Width != w || f.Height != h))
            throw new ArgumentException("Кадры разного размера", nameof(frames));

        // 1) палитра: самые частые цвета (по 15 бит на цвет), средний реальный цвет ячейки
        var count = new Dictionary<int, (long N, long R, long G, long B)>();
        var pixels = frames.Select(f => f.Pixels).ToList();
        foreach (var px in pixels)
            foreach (var c in px)
            {
                var key = ((c.Red >> 3) << 10) | ((c.Green >> 3) << 5) | (c.Blue >> 3);
                count.TryGetValue(key, out var v);
                count[key] = (v.N + 1, v.R + c.Red, v.G + c.Green, v.B + c.Blue);
            }
        var palette = count.OrderByDescending(kv => kv.Value.N).Take(256)
            .Select(kv => (Key: kv.Key, R: (byte)(kv.Value.R / kv.Value.N), G: (byte)(kv.Value.G / kv.Value.N), B: (byte)(kv.Value.B / kv.Value.N))).ToList();
        var lookup = new Dictionary<int, byte>();
        byte Nearest(int key, SKColor c)
        {
            if (lookup.TryGetValue(key, out var hit))
                return hit;
            var best = 0;
            var bestD = int.MaxValue;
            for (var i = 0; i < palette.Count; i++)
            {
                int dr = palette[i].R - c.Red, dg = palette[i].G - c.Green, db = palette[i].B - c.Blue;
                var d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestD) { bestD = d; best = i; }
            }
            lookup[key] = (byte)best;
            return (byte)best;
        }

        using var ms = new MemoryStream();
        void B(params byte[] b) => ms.Write(b, 0, b.Length);
        void U16(int v) { ms.WriteByte((byte)(v & 255)); ms.WriteByte((byte)(v >> 8)); }
        B((byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a');
        U16(w); U16(h);
        B(0xF7, 0, 0); // глобальная таблица на 256 цветов, фон 0
        for (var i = 0; i < 256; i++)
            if (i < palette.Count) B(palette[i].R, palette[i].G, palette[i].B); else B(0, 0, 0);
        B(0x21, 0xFF, 0x0B); B(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0")); B(0x03, 0x01, 0, 0, 0); // бесконечный цикл
        foreach (var px in pixels)
        {
            B(0x21, 0xF9, 0x04, 0x00); U16(delayCentiseconds); B(0x00, 0x00); // задержка кадра
            B(0x2C); U16(0); U16(0); U16(w); U16(h); B(0x00);
            var idx = new byte[px.Length];
            for (var i = 0; i < px.Length; i++)
            {
                var c = px[i];
                idx[i] = Nearest(((c.Red >> 3) << 10) | ((c.Green >> 3) << 5) | (c.Blue >> 3), c);
            }
            B(8);
            var lzw = Lzw(idx, 8);
            for (var p = 0; p < lzw.Length; p += 255)
            {
                var n = Math.Min(255, lzw.Length - p);
                ms.WriteByte((byte)n);
                ms.Write(lzw, p, n);
            }
            B(0x00);
        }
        B(0x3B);
        return ms.ToArray();
    }

    /// <summary>LZW GIF (схема Кевина Вейнера): clear = 2^min, eoi = clear + 1, размер кода растёт до 12 бит.</summary>
    private static byte[] Lzw(byte[] data, int minCode)
    {
        var output = new List<byte>(data.Length / 4 + 16);
        int clear = 1 << minCode, eoi = clear + 1;
        int initBits = minCode + 1, nBits = initBits, maxCode = (1 << nBits) - 1, freeEnt = clear + 2;
        var clearFlag = false;
        uint accum = 0;
        var accumBits = 0;
        var table = new Dictionary<int, int>();

        void Output(int code)
        {
            accum |= (uint)code << accumBits;
            accumBits += nBits;
            while (accumBits >= 8)
            {
                output.Add((byte)(accum & 0xFF));
                accum >>= 8;
                accumBits -= 8;
            }
            if (freeEnt > maxCode || clearFlag)
            {
                if (clearFlag)
                {
                    nBits = initBits;
                    maxCode = (1 << nBits) - 1;
                    clearFlag = false;
                }
                else
                {
                    nBits++;
                    maxCode = nBits == 12 ? 4096 : (1 << nBits) - 1;
                }
            }
        }

        Output(clear);
        var ent = data[0];
        int prefix = ent;
        for (var i = 1; i < data.Length; i++)
        {
            int c = data[i];
            var key = (prefix << 8) | c;
            if (table.TryGetValue(key, out var found))
            {
                prefix = found;
                continue;
            }
            Output(prefix);
            prefix = c;
            if (freeEnt < 4096)
                table[key] = freeEnt++;
            else
            {
                table.Clear();
                freeEnt = clear + 2;
                clearFlag = true;
                Output(clear);
            }
        }
        Output(prefix);
        Output(eoi);
        if (accumBits > 0)
            output.Add((byte)(accum & 0xFF));
        return output.ToArray();
    }
}
