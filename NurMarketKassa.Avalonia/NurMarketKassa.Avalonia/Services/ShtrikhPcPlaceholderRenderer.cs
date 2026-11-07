using SkiaSharp;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (жалоба владельца: «товары отправляем, а на экране весов их нет»): плитка-заглушка для товара без фото — BMP 288×288 (24 бита, как для фото):
/// мягкий цвет фона из хэша названия, крупное название (перенос по словам до 5 строк, кегль подбирается, длинные слова режутся с «…»), внизу мелко код товара и, если
/// включено, цена. Рисует Skia (SKCanvas/SKFont), шрифт с кириллицей и киргизскими буквами ө ү ң подбирается из списка, иначе через SKFontManager.MatchCharacter;
/// System.Drawing не нужен — работает на Windows, Linux и Android. Вызывать вне потока интерфейса.</summary>
public static class ShtrikhPcPlaceholderRenderer
{
    private const int Margin = 18;
    private const int MaxLines = 5;
    private const int MaxFont = 46;
    private const int MinFont = 16;
    private const int FooterFont = 22;

    private static readonly string[] Families = { "Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans", "Roboto", "Noto Sans" };
    private const string ProbeGlyphs = "ЖжЩщЫыӨөҮүҢң";

    private static readonly object FontLock = new();
    private static SKTypeface? _bold;
    private static SKTypeface? _regular;

    /// <summary>Кириллица и «ө ү ң» есть во всех глифах шрифта.</summary>
    private static bool Covers(SKTypeface typeface)
    {
        try
        {
            return typeface.GetGlyphs(ProbeGlyphs).All(g => g != 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SKTypeface Resolve(bool bold)
    {
        lock (FontLock)
        {
            ref var cached = ref (bold ? ref _bold : ref _regular);
            if (cached is not null)
                return cached;
            var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
            foreach (var family in Families)
            {
                var typeface = SKTypeface.FromFamilyName(family, style);
                if (typeface is not null && Covers(typeface))
                    return cached = typeface;
            }
            // Ни один из знакомых шрифтов не покрывает кириллицу — просим систему подобрать шрифт под букву «Ж».
            var matched = SKFontManager.Default.MatchCharacter(0x0416);
            return cached = matched ?? SKTypeface.Default;
        }
    }

    private static float Measure(SKFont font, string text) => font.MeasureText(font.Typeface.GetGlyphs(text));

    /// <summary>BMP 288×288 с плиткой. <paramref name="priceText"/> — цена внизу справа (null — не показывать). null — не удалось нарисовать.</summary>
    public static byte[]? Render(string? name, long code, string? priceText, int size = ShtrikhPcBmp.PictureSize)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(name) ? code.ToString() : name.Trim();
            var (r, g, b) = ShtrikhPcTile.BackgroundColor(title, code);
            var dark = ShtrikhPcTile.UseDarkText(r, g, b);
            var textColor = dark ? new SKColor(0x1F, 0x25, 0x33) : SKColors.White;

            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(r, g, b));
            using var paint = new SKPaint { IsAntialias = true, Color = textColor };

            var footerTop = size - Margin - FooterFont - 8;           // над нижней строкой
            var areaTop = Margin;
            var areaHeight = footerTop - areaTop - 6;
            var areaWidth = size - 2 * Margin;

            // Крупное название: наибольший кегль, при котором слова не режутся «…» и строки (до 5) помещаются по высоте.
            using var bold = new SKFont(Resolve(bold: true), MinFont) { Edging = SKFontEdging.SubpixelAntialias };
            List<string> lines = new();
            var chosen = MinFont;
            for (var fs = MaxFont; fs >= MinFont; fs -= 2)
            {
                bold.Size = fs;
                var candidate = ShtrikhPcTile.WrapWords(title, text => Measure(bold, text), areaWidth, MaxLines);
                var cut = candidate.Count > 0 && candidate[^1].EndsWith('…') && !title.EndsWith('…');
                var heightOk = candidate.Count * fs * 1.16f <= areaHeight;
                lines = candidate;
                chosen = fs;
                if (!cut && heightOk)
                    break;
            }
            bold.Size = chosen;
            var lineHeight = chosen * 1.16f;
            var blockHeight = lines.Count * lineHeight;
            var top = areaTop + Math.Max(0, (areaHeight - blockHeight) / 2);
            var ascent = -bold.Metrics.Ascent;
            for (var i = 0; i < lines.Count; i++)
            {
                var width = Measure(bold, lines[i]);
                canvas.DrawText(lines[i], (size - width) / 2f, top + i * lineHeight + ascent, bold, paint);
            }

            // Тонкая черта и подвал: код товара слева, цена справа.
            using var line = new SKPaint { Color = textColor.WithAlpha(70), StrokeWidth = 2, IsAntialias = false };
            canvas.DrawLine(Margin, footerTop, size - Margin, footerTop, line);
            using var footer = new SKFont(Resolve(bold: false), FooterFont) { Edging = SKFontEdging.SubpixelAntialias };
            var baseline = size - Margin - 2;
            canvas.DrawText(code.ToString(), Margin, baseline, footer, paint);
            if (!string.IsNullOrWhiteSpace(priceText))
            {
                var width = Measure(footer, priceText);
                canvas.DrawText(priceText, size - Margin - width, baseline, footer, paint);
            }

            canvas.Flush();
            return ShtrikhPcBmp.Encode24(bitmap.Bytes, size, size);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
