using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>
/// Рисует графики для выгрузок в Excel и Word.
///
/// Почему картинкой, а не «настоящей» диаграммой Excel: диаграмма в формате OpenXML — это
/// десятки вложенных элементов, которые невозможно проверить, не открыв Excel. Картинку же
/// видно сразу, она одинаково выглядит в Excel, Word и при печати, и не ломается, если файл
/// откроют не в Excel, а в бесплатном редакторе. Данные при этом всё равно лежат рядом
/// отдельными листами — их можно пересчитать и построить свою диаграмму.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AnalyticsChartRenderer
{
    private static readonly Color Ink = Color.FromArgb(15, 23, 42);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Grid = Color.FromArgb(226, 232, 240);

    /// <summary>Палитра берётся по кругу: цвета различимы и в цвете, и в чёрно-белой печати
    /// (разная светлота), потому что отчёты часто печатают на обычном принтере.</summary>
    private static readonly Color[] Palette =
    [
        Color.FromArgb(37, 99, 235),
        Color.FromArgb(22, 163, 74),
        Color.FromArgb(234, 88, 12),
        Color.FromArgb(124, 58, 237),
        Color.FromArgb(220, 38, 38),
        Color.FromArgb(13, 148, 136),
        Color.FromArgb(202, 138, 4),
    ];

    /// <summary>Столбчатая диаграмма. Подписи не поворачиваются: вместо этого длинные имена
    /// обрезаются — наклонный текст в отчёте читать труднее, чем усечённый.</summary>
    public static byte[] RenderBars(string title, IReadOnlyList<(string Label, double Value)> data,
        int width = 900, int height = 420)
    {
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        Prepare(g, width, height);

        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 8.5f);
        using var valueFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        using var gridPen = new Pen(Grid, 1);

        g.DrawString(title, titleFont, ink, 18, 14);

        if (data.Count == 0)
        {
            g.DrawString("Нет данных за период", labelFont, muted, 18, 60);
            return ToPng(bitmap);
        }

        const int left = 70, right = 24, top = 56, bottom = 58;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var max = Math.Max(data.Max(d => d.Value), 0.0001);

        // Сетка и подписи оси — пять линий достаточно, чтобы читать порядок величин.
        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight - plotHeight * i / 4f;
            g.DrawLine(gridPen, left, y, left + plotWidth, y);
            g.DrawString(FormatShort(max * i / 4), labelFont, muted, 8, y - 8);
        }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.62f, 90f);

        for (var i = 0; i < data.Count; i++)
        {
            var (label, value) = data[i];
            var barHeight = (float)(plotHeight * (value / max));
            var x = left + slot * i + (slot - barWidth) / 2f;
            var y = top + plotHeight - barHeight;

            using var fill = new SolidBrush(Palette[i % Palette.Length]);
            g.FillRectangle(fill, x, y, barWidth, Math.Max(barHeight, 1));

            var valueText = FormatShort(value);
            var valueSize = g.MeasureString(valueText, valueFont);
            g.DrawString(valueText, valueFont, ink, x + (barWidth - valueSize.Width) / 2, y - 16);

            var shortLabel = Ellipsize(g, label, labelFont, slot - 4);
            var labelSize = g.MeasureString(shortLabel, labelFont);
            g.DrawString(shortLabel, labelFont, muted,
                x + (barWidth - labelSize.Width) / 2, top + plotHeight + 8);
        }

        return ToPng(bitmap);
    }

    /// <summary>Диаграмма Парето для отчёта — та же, что на экране: на каждый товар три
    /// столбца, само значение по левой шкале, накопленная доля и порог 80 % по правой.
    ///
    /// Рисуется отдельно от <see cref="RenderBars"/>, потому что здесь две разные шкалы: одна
    /// в деньгах, другая в процентах. Свести их в один столбчатый график нельзя — высота
    /// перестала бы что-либо значить.</summary>
    public static byte[] RenderPareto(
        string title,
        IReadOnlyList<(string Label, double Value, double Cumulative)> data,
        string valueLegend,
        int width = 980,
        int height = 460)
    {
        var colorValue = Color.FromArgb(78, 134, 199);
        var colorCumulative = Color.FromArgb(237, 125, 49);
        var colorThreshold = Color.FromArgb(165, 165, 165);
        const double threshold = 80.0;

        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        Prepare(g, width, height);

        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 8.5f);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        using var gridPen = new Pen(Grid, 1);

        g.DrawString(title, titleFont, ink, 18, 14);

        if (data.Count == 0)
        {
            g.DrawString("Нет данных за период", labelFont, muted, 18, 60);
            return ToPng(bitmap);
        }

        const int left = 78, right = 60, top = 56, bottom = 86;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var max = Math.Max(data.Max(d => d.Value), 0.0001);

        for (var i = 0; i <= 5; i++)
        {
            var y = top + plotHeight - plotHeight * i / 5f;
            g.DrawLine(gridPen, left, y, left + plotWidth, y);
            g.DrawString(FormatShort(max * i / 5), labelFont, muted, 8, y - 8);
            g.DrawString((i * 20).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %",
                labelFont, muted, left + plotWidth + 6, y - 8);
        }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.24f, 22f);

        for (var i = 0; i < data.Count; i++)
        {
            var (label, value, cumulative) = data[i];
            var groupX = left + slot * i + (slot - barWidth * 3) / 2f;

            void Bar(int index, double fraction, Color color)
            {
                var barHeight = (float)(plotHeight * Math.Clamp(fraction, 0, 1));
                using var fill = new SolidBrush(color);
                g.FillRectangle(fill, groupX + index * barWidth, top + plotHeight - barHeight,
                    barWidth - 1, Math.Max(barHeight, 1));
            }

            Bar(0, value / max, colorValue);
            Bar(1, Math.Clamp(cumulative, 0, 100) / 100.0, colorCumulative);
            Bar(2, threshold / 100.0, colorThreshold);

            var shortLabel = Ellipsize(g, label, labelFont, slot - 2);
            var labelSize = g.MeasureString(shortLabel, labelFont);
            g.DrawString(shortLabel, labelFont, muted,
                groupX + (barWidth * 3 - labelSize.Width) / 2, top + plotHeight + 8);
        }

        // Легенда: три ряда одинаковой формы различимы только по цвету.
        var legendY = height - 28f;
        var legendX = (float)left;
        foreach (var (color, text) in new[]
                 {
                     (colorValue, valueLegend),
                     (colorCumulative, "Накопленная доля"),
                     (colorThreshold, "Порог 80 %"),
                 })
        {
            using var swatch = new SolidBrush(color);
            g.FillRectangle(swatch, legendX, legendY, 12, 12);
            g.DrawString(text, labelFont, muted, legendX + 16, legendY - 2);
            legendX += 20 + g.MeasureString(text, labelFont).Width + 18;
        }

        return ToPng(bitmap);
    }

    /// <summary>Классическая Парето для отчёта: столбцы долей, окрашенные по группе ABC, и
    /// накопительная ломаная по правой шкале с пунктирами 80 % и 95 %.
    ///
    /// Вторая, столбцовая, версия — <see cref="RenderPareto"/>. В отчёт идут обе, как и на
    /// экране: по ломаной ловят перелом, по столбцам сравнивают накопленную долю с порогом.</summary>
    public static byte[] RenderParetoClassic(
        string title,
        IReadOnlyList<(string Label, double Share, double Cumulative, string Group)> data,
        int width = 980,
        int height = 460)
    {
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        Prepare(g, width, height);

        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 8.5f);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        using var gridPen = new Pen(Grid, 1);

        g.DrawString(title, titleFont, ink, 18, 14);

        if (data.Count == 0)
        {
            g.DrawString("Нет данных за период", labelFont, muted, 18, 60);
            return ToPng(bitmap);
        }

        const int left = 60, right = 56, top = 56, bottom = 86;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var maxShare = Math.Max(data.Max(d => d.Share), 0.0001);

        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotHeight - plotHeight * i / 4f;
            g.DrawLine(gridPen, left, y, left + plotWidth, y);
            g.DrawString((i * 25).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %",
                labelFont, muted, left + plotWidth + 6, y - 8);
        }

        // Границы групп: 80 % — конец A, 95 % — конец B.
        foreach (var boundary in new[] { (Value: 80.0, Color: Color.FromArgb(245, 158, 11)), (Value: 95.0, Color: Color.FromArgb(59, 130, 246)) })
        {
            var y = top + plotHeight - (float)(plotHeight * boundary.Value / 100.0);
            using var pen = new Pen(boundary.Color, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, left, y, left + plotWidth, y);
            using var tagBrush = new SolidBrush(boundary.Color);
            g.DrawString(boundary.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %",
                labelFont, tagBrush, 8, y - 8);
        }

        var slot = plotWidth / (float)data.Count;
        var barWidth = Math.Min(slot * 0.55f, 34f);
        var points = new List<PointF>();

        for (var i = 0; i < data.Count; i++)
        {
            var (label, share, cumulative, group) = data[i];
            var barHeight = (float)(plotHeight * (share / maxShare));
            var x = left + slot * i + (slot - barWidth) / 2f;

            using var fill = new SolidBrush(GroupColor(group));
            g.FillRectangle(fill, x, top + plotHeight - barHeight, barWidth, Math.Max(barHeight, 1));

            var shortLabel = Ellipsize(g, label, labelFont, slot - 2);
            var labelSize = g.MeasureString(shortLabel, labelFont);
            g.DrawString(shortLabel, labelFont, muted,
                x + (barWidth - labelSize.Width) / 2, top + plotHeight + 8);

            points.Add(new PointF(x + barWidth / 2f,
                top + plotHeight - (float)(plotHeight * Math.Clamp(cumulative, 0, 100) / 100.0)));
        }

        if (points.Count > 1)
        {
            using var linePen = new Pen(Ink, 2);
            g.DrawLines(linePen, points.ToArray());
        }

        using var dotFill = new SolidBrush(Color.White);
        using var dotPen = new Pen(Ink, 1.5f);
        foreach (var point in points)
        {
            g.FillEllipse(dotFill, point.X - 3.5f, point.Y - 3.5f, 7, 7);
            g.DrawEllipse(dotPen, point.X - 3.5f, point.Y - 3.5f, 7, 7);
        }

        return ToPng(bitmap);
    }

    /// <summary>Цвет группы ABC — тот же, что на экране, чтобы отчёт и программа не спорили.</summary>
    private static Color GroupColor(string group) => group switch
    {
        "A" => Color.FromArgb(22, 163, 74),
        "B" => Color.FromArgb(245, 158, 11),
        _ => Color.FromArgb(59, 130, 246),
    };

    /// <summary>Круговая диаграмма с легендой справа. Доли меньше 1,5 % не подписываются на
    /// самом круге — подписи наезжали бы друг на друга; они остаются в легенде.</summary>
    public static byte[] RenderPie(string title, IReadOnlyList<(string Label, double Value)> data,
        int width = 900, int height = 420)
    {
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        Prepare(g, width, height);

        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var legendFont = new Font("Segoe UI", 9);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);

        g.DrawString(title, titleFont, ink, 18, 14);

        var total = data.Sum(d => d.Value);
        if (data.Count == 0 || total <= 0)
        {
            g.DrawString("Нет данных за период", legendFont, muted, 18, 60);
            return ToPng(bitmap);
        }

        var size = Math.Min(height - 90, 300);
        var rect = new RectangleF(40, 66, size, size);
        var start = -90f;

        for (var i = 0; i < data.Count; i++)
        {
            var sweep = (float)(360.0 * data[i].Value / total);
            using var fill = new SolidBrush(Palette[i % Palette.Length]);
            g.FillPie(fill, rect, start, sweep);
            start += sweep;
        }

        var legendX = 40 + size + 40;
        var legendY = 70f;
        for (var i = 0; i < data.Count; i++)
        {
            var share = data[i].Value / total * 100;
            using var box = new SolidBrush(Palette[i % Palette.Length]);
            g.FillRectangle(box, legendX, legendY + 2, 12, 12);

            var text = $"{Ellipsize(g, data[i].Label, legendFont, width - legendX - 130)} — " +
                       $"{FormatShort(data[i].Value)} ({share:0.#} %)";
            g.DrawString(text, legendFont, ink, legendX + 18, legendY);
            legendY += 22;

            if (legendY > height - 24)
                break;
        }

        return ToPng(bitmap);
    }

    private static void Prepare(Graphics g, int width, int height)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.White);
    }

    /// <summary>Крупные суммы сокращаем: «1 234 567» на подписи столбца не помещается и
    /// налезает на соседний.</summary>
    private static string FormatShort(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##") + " млн",
        >= 1_000 => (value / 1_000).ToString("0.#") + " тыс",
        _ => value.ToString("0.##"),
    };

    private static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || g.MeasureString(text, font).Width <= maxWidth)
            return text ?? "";

        var result = text;
        while (result.Length > 1 && g.MeasureString(result + "…", font).Width > maxWidth)
            result = result[..^1];

        return result + "…";
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
