using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В18, АЛ-1): графики отчётов (столбцы, Парето, круговая) на SkiaSharp — работают на Windows, Linux и Android. Раньше их рисовал System.Drawing
/// (<see cref="AnalyticsChartRenderer"/>), которого на Linux и Android нет, и график просто пропускался. Те же размеры, поля, цвета, шкалы и подписи; картинка в
/// логических точках, растр вдвое плотнее (как у GDI-версии). На Windows <see cref="AnalyticsChartRenderer"/> по-прежнему рисует GDI+.</summary>
public static class AnalyticsChartSkia
{
    private const float Scale = 2f;
    private const uint Ink = 0xFF0F172A, Muted = 0xFF64748B, Grid = 0xFFE2E8F0;

    private static readonly uint[] Palette = [0xFF2563EB, 0xFF16A34A, 0xFFEA580C, 0xFF7C3AED, 0xFFDC2626, 0xFF0D9488, 0xFFCA8A04];

    private static SkText F(float pt, bool bold = false, uint color = Ink) => new("Segoe UI", pt * SkiaText.PtToPx, bold, false, false, false, color);

    private static SKPaint Fill(uint argb) => new() { Color = new SKColor(argb), IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPaint Stroke(uint argb, float width, bool dashed = false) => new()
    {
        Color = new SKColor(argb), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width,
        PathEffect = dashed ? SKPathEffect.CreateDash([3f * width, 1f * width], 0) : null,
    };

    private static SKBitmap NewCanvas(int width, int height, out SKCanvas canvas)
    {
        var bitmap = new SKBitmap(new SKImageInfo((int)(width * Scale), (int)(height * Scale), SKColorType.Rgba8888, SKAlphaType.Opaque));
        canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(Scale);
        return bitmap;
    }

    private static byte[] Png(SKBitmap bitmap, SKCanvas canvas)
    {
        canvas.Dispose();
        using (bitmap)
            return SkiaRaster.ToPng(bitmap);
    }

    /// <summary>Крупные суммы сокращаем: «1 234 567» на подписи столбца не помещается и налезает на соседний.</summary>
    internal static string FormatShort(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##", ReportFormat.Culture) + " " + Tr.T("млн", "млн", "M", "Mn", "mln"),
        >= 1_000 => (value / 1_000).ToString("0.#", ReportFormat.Culture) + " " + Tr.T("тыс", "миң", "K", "B", "ming"),
        _ => value.ToString("0.##", ReportFormat.Culture),
    };

    /// <summary>«Круглая» шкала: шаг 1, 2, 2,5 или 5 × 10ⁿ и максимум, кратный шагу.</summary>
    internal static (double Max, double Step) NiceScale(double dataMax, int ticks)
    {
        dataMax = Math.Max(dataMax, 0.0001);
        var rough = dataMax / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.Select(m => m * magnitude).First(s => s >= rough);
        return (Math.Ceiling(dataMax / step) * step, step);
    }

    public static byte[] RenderBars(string title, IReadOnlyList<(string Label, double Value)> data, int width = 900, int height = 420)
    {
        var bitmap = NewCanvas(width, height, out var canvas);
        var labelFont = F(8.5f);
        var valueFont = F(8.5f, true);
        SkiaText.Draw(canvas, title, 18, 14, F(13, true));
        if (data.Count == 0)
        {
            SkiaText.Draw(canvas, ReportLabels.NoData, 18, 60, F(8.5f, false, Muted));
            return Png(bitmap, canvas);
        }

        const int left = 70, right = 24, top = 56, bottom = 58;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var (max, step) = NiceScale(data.Max(d => d.Value), 5);
        var muted = labelFont with { Argb = Muted };

        using (var gridPen = Stroke(Grid, 1))
            for (var tick = 0.0; tick <= max + step / 2; tick += step)
            {
                var y = top + plotHeight - (float)(plotHeight * tick / max);
                canvas.DrawLine(left, y, left + plotWidth, y, gridPen);
                var tickText = FormatShort(tick);
                SkiaText.Draw(canvas, tickText, left - 8 - SkiaText.Measure(tickText, muted), y - 8, muted);
            }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.62f, 90f);
        var widestLabel = data.Max(d => SkiaText.Measure(d.Label, labelFont));
        var labelEvery = Math.Max(1, (int)Math.Ceiling((widestLabel + 6) / slot));
        if (labelEvery > 3)
            labelEvery = 1;
        var widestValue = data.Max(d => SkiaText.Measure(FormatShort(d.Value), valueFont));
        var showValues = widestValue + 2 <= slot;

        using var fill = Fill(Palette[0]);
        for (var i = 0; i < data.Count; i++)
        {
            var (label, value) = data[i];
            var barHeight = (float)(plotHeight * (value / max));
            var x = left + slot * i + (slot - barWidth) / 2f;
            var y = top + plotHeight - barHeight;
            canvas.DrawRect(new SKRect(x, y, x + barWidth, y + Math.Max(barHeight, 1)), fill);
            if (showValues)
            {
                var valueText = FormatShort(value);
                SkiaText.Draw(canvas, valueText, x + (barWidth - SkiaText.Measure(valueText, valueFont)) / 2, y - 16, valueFont);
            }
            if (i % labelEvery != 0)
                continue;
            var shortLabel = SkiaText.Truncate(label ?? "", slot * labelEvery - 4, muted);
            SkiaText.Draw(canvas, shortLabel, x + (barWidth - SkiaText.Measure(shortLabel, muted)) / 2, top + plotHeight + 8, muted);
        }
        return Png(bitmap, canvas);
    }

    public static byte[] RenderPareto(string title, IReadOnlyList<(string Label, double Value, double Cumulative)> data, string valueLegend, int width = 980, int height = 460)
    {
        const uint colorValue = 0xFF4E86C7, colorCumulative = 0xFFED7D31, colorThreshold = 0xFFA5A5A5;
        const double threshold = 80.0;
        var bitmap = NewCanvas(width, height, out var canvas);
        var muted = F(8.5f, false, Muted);
        SkiaText.Draw(canvas, title, 18, 14, F(13, true));
        if (data.Count == 0)
        {
            SkiaText.Draw(canvas, ReportLabels.NoData, 18, 60, muted);
            return Png(bitmap, canvas);
        }

        const int left = 78, right = 60, top = 56, bottom = 86;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var paretoStep = NiceScale(data.Max(d => d.Value), 5).Step;
        var max = paretoStep * 5;

        using (var gridPen = Stroke(Grid, 1))
            for (var i = 0; i <= 5; i++)
            {
                var y = top + plotHeight - plotHeight * i / 5f;
                canvas.DrawLine(left, y, left + plotWidth, y, gridPen);
                var tickText = FormatShort(max * i / 5);
                SkiaText.Draw(canvas, tickText, left - 8 - SkiaText.Measure(tickText, muted), y - 8, muted);
                SkiaText.Draw(canvas, (i * 20).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %", left + plotWidth + 6, y - 8, muted);
            }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.24f, 22f);
        for (var i = 0; i < data.Count; i++)
        {
            var (label, value, cumulative) = data[i];
            var groupX = left + slot * i + (slot - barWidth * 3) / 2f;

            void Bar(int index, double fraction, uint color)
            {
                var barHeight = (float)(plotHeight * Math.Clamp(fraction, 0, 1));
                using var fill = Fill(color);
                var x = groupX + index * barWidth;
                var y = top + plotHeight - barHeight;
                canvas.DrawRect(new SKRect(x, y, x + barWidth - 1, y + Math.Max(barHeight, 1)), fill);
            }

            Bar(0, value / max, colorValue);
            Bar(1, Math.Clamp(cumulative, 0, 100) / 100.0, colorCumulative);
            Bar(2, threshold / 100.0, colorThreshold);

            var shortLabel = SkiaText.Truncate(label ?? "", slot - 2, muted);
            SkiaText.Draw(canvas, shortLabel, groupX + (barWidth * 3 - SkiaText.Measure(shortLabel, muted)) / 2, top + plotHeight + 8, muted);
        }

        var legendY = height - 28f;
        var legendX = (float)left;
        foreach (var (color, text) in new[]
                 {
                     (colorValue, valueLegend),
                     (colorCumulative, Tr.T("Накопленная доля", "Топтолгон үлүш", "Cumulative share", "Kümülatif pay", "Jamlangan ulush")),
                     (colorThreshold, Tr.T("Порог 80 %", "80 % босогосу", "80 % threshold", "%80 eşiği", "80 % chegarasi")),
                 })
        {
            using var swatch = Fill(color);
            canvas.DrawRect(new SKRect(legendX, legendY, legendX + 12, legendY + 12), swatch);
            SkiaText.Draw(canvas, text, legendX + 16, legendY - 2, muted);
            legendX += 20 + SkiaText.Measure(text, muted) + 18;
        }
        return Png(bitmap, canvas);
    }

    public static byte[] RenderParetoClassic(string title, IReadOnlyList<(string Label, double Share, double Cumulative, string Group)> data, int width = 980, int height = 460)
    {
        var bitmap = NewCanvas(width, height, out var canvas);
        var muted = F(8.5f, false, Muted);
        SkiaText.Draw(canvas, title, 18, 14, F(13, true));
        if (data.Count == 0)
        {
            SkiaText.Draw(canvas, ReportLabels.NoData, 18, 60, muted);
            return Png(bitmap, canvas);
        }

        const int left = 60, right = 56, top = 56, bottom = 86;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var maxShare = Math.Max(data.Max(d => d.Share), 0.0001);

        using (var gridPen = Stroke(Grid, 1))
            for (var i = 0; i <= 4; i++)
            {
                var y = top + plotHeight - plotHeight * i / 4f;
                canvas.DrawLine(left, y, left + plotWidth, y, gridPen);
                SkiaText.Draw(canvas, (i * 25).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %", left + plotWidth + 6, y - 8, muted);
            }

        foreach (var boundary in new[] { (Value: 80.0, Color: 0xFFF59E0B), (Value: 95.0, Color: 0xFF3B82F6) })
        {
            var y = top + plotHeight - (float)(plotHeight * boundary.Value / 100.0);
            using var pen = Stroke(boundary.Color, 1.5f, dashed: true);
            canvas.DrawLine(left, y, left + plotWidth, y, pen);
            SkiaText.Draw(canvas, boundary.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %", 8, y - 8, muted with { Argb = boundary.Color });
        }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.55f, 34f);
        var points = new List<SKPoint>();
        for (var i = 0; i < data.Count; i++)
        {
            var (label, share, cumulative, group) = data[i];
            var barHeight = (float)(plotHeight * (share / maxShare));
            var x = left + slot * i + (slot - barWidth) / 2f;
            using (var fill = Fill(GroupColor(group)))
                canvas.DrawRect(new SKRect(x, top + plotHeight - barHeight, x + barWidth, top + plotHeight - barHeight + Math.Max(barHeight, 1)), fill);
            var shortLabel = SkiaText.Truncate(label ?? "", slot - 2, muted);
            SkiaText.Draw(canvas, shortLabel, x + (barWidth - SkiaText.Measure(shortLabel, muted)) / 2, top + plotHeight + 8, muted);
            points.Add(new SKPoint(x + barWidth / 2f, top + plotHeight - (float)(plotHeight * Math.Clamp(cumulative, 0, 100) / 100.0)));
        }

        if (points.Count > 1)
        {
            using var linePen = Stroke(Ink, 2);
            using var path = new SKPath();
            path.MoveTo(points[0]);
            for (var i = 1; i < points.Count; i++)
                path.LineTo(points[i]);
            canvas.DrawPath(path, linePen);
        }
        using var dotFill = Fill(0xFFFFFFFF);
        using var dotPen = Stroke(Ink, 1.5f);
        foreach (var point in points)
        {
            canvas.DrawOval(point.X, point.Y, 3.5f, 3.5f, dotFill);
            canvas.DrawOval(point.X, point.Y, 3.5f, 3.5f, dotPen);
        }
        return Png(bitmap, canvas);
    }

    private static uint GroupColor(string group) => group switch
    {
        "A" => 0xFF16A34A,
        "B" => 0xFFF59E0B,
        _ => 0xFF3B82F6,
    };

    public static byte[] RenderPie(string title, IReadOnlyList<(string Label, double Value)> data, int width = 900, int height = 420)
    {
        var bitmap = NewCanvas(width, height, out var canvas);
        var legendFont = F(9);
        SkiaText.Draw(canvas, title, 18, 14, F(13, true));
        var total = data.Sum(d => d.Value);
        if (data.Count == 0 || total <= 0)
        {
            SkiaText.Draw(canvas, ReportLabels.NoData, 18, 60, legendFont with { Argb = Muted });
            return Png(bitmap, canvas);
        }

        var size = Math.Min(height - 90, 300);
        var rect = new SKRect(40, 66, 40 + size, 66 + size);
        var start = -90f;
        for (var i = 0; i < data.Count; i++)
        {
            var sweep = (float)(360.0 * data[i].Value / total);
            using var fill = Fill(Palette[i % Palette.Length]);
            canvas.DrawArc(rect, start, sweep, true, fill);
            start += sweep;
        }

        var legendX = 40 + size + 40;
        var legendY = 70f;
        for (var i = 0; i < data.Count; i++)
        {
            var share = data[i].Value / total * 100;
            using (var box = Fill(Palette[i % Palette.Length]))
                canvas.DrawRect(new SKRect(legendX, legendY + 2, legendX + 12, legendY + 14), box);
            var text = $"{SkiaText.Truncate(data[i].Label ?? "", width - legendX - 130, legendFont)} — " +
                       $"{FormatShort(data[i].Value)} ({share.ToString("0.#", ReportFormat.Culture)} %)";
            SkiaText.Draw(canvas, text, legendX + 18, legendY, legendFont);
            legendY += 22;
            if (legendY > height - 24)
                break;
        }
        return Png(bitmap, canvas);
    }
}
