using System.Collections.Concurrent;
using SkiaSharp;

namespace NurMarketKassa.Services;

// 2026-11-06 (В18): шрифты и текст для растра на SkiaSharp (этикетки, ценники, листы A4, чеки, графики) на Windows, Linux и Android без System.Drawing.
// На Linux нет Arial, Consolas и Courier New — подбираются близкие по размерам (Liberation/Arimo/Cousine/DejaVu), в конце — общие «sans-serif» и
// «monospace» (fontconfig на Linux и системные шрифты Android). Символов, которых нет в выбранном шрифте (кыргызские и узбекские ө ү ң ў қ ғ ҳ),
// ищется шрифт через SKFontManager.MatchCharacter: текст режется на отрезки, у каждого свой шрифт.

/// <summary>Стиль текста для Skia: семейство, размер в пикселях, жирный/курсив, зачёркнутый/подчёркнутый, цвет; <see cref="Alias"/> — без сглаживания (чёткий шрифт чека).</summary>
internal readonly record struct SkText(string Family, float Px, bool Bold = false, bool Italic = false, bool Strike = false, bool Underline = false,
    uint Argb = 0xFF000000, bool Alias = false);

internal static class SkiaFonts
{
    private static readonly string[] SansChain = ["Arial", "Liberation Sans", "Arimo", "Helvetica", "Segoe UI", "DejaVu Sans", "Noto Sans", "Roboto", "sans-serif"];
    private static readonly string[] MonoChain = ["Consolas", "Courier New", "Liberation Mono", "Cousine", "DejaVu Sans Mono", "Noto Sans Mono", "Roboto Mono", "monospace"];

    private static readonly ConcurrentDictionary<(string, bool, bool), SKTypeface> Faces = new();
    private static readonly ConcurrentDictionary<(string, bool, bool, int), SKTypeface?> Fallbacks = new();

    /// <summary>Шрифт по имени из шаблона/настроек; нет такого — ближайший из цепочки (моноширинный для Consolas/Courier/Mono, иначе без засечек).</summary>
    public static SKTypeface Get(string? family, bool bold, bool italic) =>
        Faces.GetOrAdd(((family ?? "").Trim().ToLowerInvariant(), bold, italic), key => Resolve(key.Item1, bold, italic));

    private static SKTypeface Resolve(string family, bool bold, bool italic)
    {
        var chain = new List<string>();
        if (family.Length > 0)
            chain.Add(family);
        var mono = family.Contains("mono", StringComparison.Ordinal) || family.Contains("consolas", StringComparison.Ordinal)
                   || family.Contains("courier", StringComparison.Ordinal) || family.Contains("cousine", StringComparison.Ordinal);
        chain.AddRange(mono ? MonoChain : SansChain);
        foreach (var name in chain)
        {
            var face = TryFamily(name, bold, italic);
            if (face is not null)
                return face;
        }
        return SKTypeface.FromFamilyName(null, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
    }

    private static SKTypeface? TryFamily(string name, bool bold, bool italic)
    {
        try
        {
            var face = SKTypeface.FromFamilyName(name, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            if (face is null)
                return null;
            // FromFamilyName всегда что-то возвращает (шрифт по умолчанию) — настоящее совпадение только если имя то же;
            // «sans-serif»/«monospace» — общие имена: доверяем системе.
            if (name is "sans-serif" or "monospace" || string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                return face;
            face.Dispose();
        }
        catch (Exception)
        {
            // шрифт недоступен — пробуем следующий из цепочки
        }
        return null;
    }

    /// <summary>Шрифт для символа, которого нет в основном; null — не нашли (рисуется основной, как «пустая клетка»).</summary>
    public static SKTypeface? ForCodepoint(SKTypeface primary, int codepoint, bool bold, bool italic) =>
        Fallbacks.GetOrAdd((primary.FamilyName ?? "", bold, italic, codepoint), key =>
        {
            try
            {
                return SKFontManager.Default.MatchCharacter(primary.FamilyName, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                    SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, null, codepoint);
            }
            catch (Exception)
            {
                return null;
            }
        });
}

internal static class SkiaText
{
    /// <summary>Пункты (как у System.Drawing.Font по умолчанию) в пиксели растра 96 dpi — так рисует и GDI+ (см. LabelSceneRenderer).</summary>
    public const float PtToPx = 96f / 72f;

    private static SKFont MakeFont(SKTypeface face, SkText s) => new(face, Math.Max(s.Px, 1f))
    {
        Edging = s.Alias ? SKFontEdging.Alias : SKFontEdging.Antialias,
        Subpixel = !s.Alias,
        Hinting = s.Alias ? SKFontHinting.Full : SKFontHinting.None, // чёткий (1-бит) шрифт чека — с подгонкой под сетку точек, как GDI SingleBitPerPixelGridFit
        LinearMetrics = !s.Alias,
    };

    private static float Advance(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    /// <summary>Отрезки строки: у каждого свой шрифт (основной либо найденный для символов, которых в нём нет).</summary>
    private static List<(string Text, SKTypeface Face, bool Owned)> Runs(string text, SkText s)
    {
        var primary = SkiaFonts.Get(s.Family, s.Bold, s.Italic);
        var runs = new List<(string, SKTypeface, bool)>();
        var sb = new System.Text.StringBuilder();
        SKTypeface current = primary;
        for (var i = 0; i < text.Length;)
        {
            var cp = char.ConvertToUtf32(text, i);
            var len = char.IsSurrogatePair(text, i) ? 2 : 1;
            var face = primary;
            if (cp > 32 && !primary.ContainsGlyph(cp))
                face = SkiaFonts.ForCodepoint(primary, cp, s.Bold, s.Italic) ?? primary;
            if (!ReferenceEquals(face, current) && sb.Length > 0)
            {
                runs.Add((sb.ToString(), current, false));
                sb.Clear();
            }
            current = face;
            sb.Append(text, i, len);
            i += len;
        }
        if (sb.Length > 0)
            runs.Add((sb.ToString(), current, false));
        return runs;
    }

    /// <summary>Ширина строки в пикселях (без переноса).</summary>
    public static float Measure(string text, SkText s)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var width = 0f;
        foreach (var (part, face, _) in Runs(text, s))
        {
            using var font = MakeFont(face, s);
            width += Advance(font, part);
        }
        return width;
    }

    /// <summary>Высота строки: подъём + спуск + межстрочный (как Font.GetHeight в GDI+).</summary>
    public static float LineHeight(SkText s)
    {
        // Метрики — без подгонки под сетку точек (как GetHeight в GDI+): иначе у чёткого шрифта чека подъём и спуск округляются вверх и лента длиннее.
        using var font = MakeFont(SkiaFonts.Get(s.Family, s.Bold, s.Italic), s with { Alias = false });
        var m = font.Metrics;
        return -m.Ascent + m.Descent + m.Leading;
    }

    /// <summary>Подъём шрифта (расстояние от верха строки до базовой линии), положительный.</summary>
    public static float Ascent(SkText s)
    {
        using var font = MakeFont(SkiaFonts.Get(s.Family, s.Bold, s.Italic), s with { Alias = false });
        return -font.Metrics.Ascent;
    }

    /// <summary>Рисует строку: <paramref name="x"/>, <paramref name="top"/> — левый верхний угол строки (как DrawString(text, font, brush, x, y)).</summary>
    public static float Draw(SKCanvas canvas, string text, float x, float top, SkText s)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        using var paint = new SKPaint { Color = new SKColor(s.Argb), IsAntialias = !s.Alias };
        var cursor = x;
        var ascent = Ascent(s);
        var baseline = top + ascent;
        foreach (var (part, face, _) in Runs(text, s))
        {
            using var font = MakeFont(face, s);
            canvas.DrawText(part, cursor, baseline, font, paint);
            cursor += Advance(font, part);
        }
        var total = cursor - x;
        if (s.Strike || s.Underline)
        {
            using var primary = MakeFont(SkiaFonts.Get(s.Family, s.Bold, s.Italic), s);
            var m = primary.Metrics;
            var thickness = Math.Max(m.StrikeoutThickness ?? s.Px / 14f, 1f);
            paint.StrokeWidth = thickness;
            if (s.Strike)
            {
                var y = baseline + (m.StrikeoutPosition ?? -s.Px * 0.3f);
                canvas.DrawLine(x, y, x + total, y, paint);
            }
            if (s.Underline)
            {
                var y = baseline + Math.Max(m.UnderlinePosition ?? s.Px * 0.1f, 1f);
                canvas.DrawLine(x, y, x + total, y, paint);
            }
        }
        return total;
    }

    /// <summary>Строка, обрезанная с «…» до ширины <paramref name="maxWidth"/> (как TruncateToWidth).</summary>
    public static string Truncate(string text, float maxWidth, SkText s)
    {
        if (Measure(text, s) <= maxWidth)
            return text;
        var cut = text;
        while (cut.Length > 1 && Measure(cut + "…", s) > maxWidth)
            cut = cut[..^1];
        return cut + "…";
    }

    /// <summary>Перенос по словам до ширины <paramref name="maxWidth"/>; слово длиннее строки рвётся по символам. <paramref name="maxLines"/> &gt; 0 — лишнее
    /// отбрасывается, на последней строке ставится «…».</summary>
    public static List<string> Wrap(string text, float maxWidth, SkText s, int maxLines = 0)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, s) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = "";
                }
                var rest = word;
                while (Measure(rest, s) > maxWidth && rest.Length > 1)
                {
                    var n = rest.Length - 1;
                    while (n > 1 && Measure(rest[..n], s) > maxWidth)
                        n--;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                current = rest;
            }
            lines.Add(current);
        }
        if (maxLines > 0 && lines.Count > maxLines)
        {
            lines = lines.GetRange(0, maxLines);
            lines[^1] = Truncate(lines[^1] + "…", maxWidth, s);
        }
        return lines;
    }
}
