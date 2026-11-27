using System.Drawing;
using System.Drawing.Drawing2D;
using SkiaSharp;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (владелец: «в ценники добавь возможность добавить рамку», фото листа A4 3×2 без рамок): рамка вокруг этикетки/ценника —
/// толщина 0,2–2 мм (по умолчанию 0,3), чёрная или серая (термопринтер и ч/б лазер — только они), скругление углов 0–5 мм, отступ рамки от края 0–5 мм,
/// пунктир. Одна геометрия для всех путей: этикетка GDI+ (печать Windows и предпросмотр), сцена (Linux/Android, PDF, TSPL через Skia), лист A4 (GDI+ и Skia).
/// Координаты — точки 203 dpi от левого верхнего угла этикетки/ячейки. Рамка целиком внутри (не обрезается краем и не двоится на стыке ячеек без зазора).</summary>
public sealed record LabelFrameSpec(double ThicknessMm, double RadiusMm, double InsetMm, bool Gray, bool Dashed)
{
    public const double MinThicknessMm = 0.2, MaxThicknessMm = 2, DefaultThicknessMm = 0.3, MaxRadiusMm = 5, MaxInsetMm = 5;
    private const float Dpi = 203f;
    public const uint BlackArgb = 0xFF000000, GrayArgb = 0xFF808080;

    public static LabelFrameSpec From(LabelTemplate t) => new(t.FrameThicknessMm, t.FrameRadiusMm, t.FrameInsetMm, t.FrameGray, t.FrameDashed);

    public static LabelFrameSpec From(A4SheetOptions o) => new(o.FrameThicknessMm, o.FrameRadiusMm, 0, o.FrameGray, o.FrameDashed);

    public double Thickness => Math.Clamp(double.IsFinite(ThicknessMm) && ThicknessMm > 0 ? ThicknessMm : DefaultThicknessMm, MinThicknessMm, MaxThicknessMm);
    public double Radius => Math.Clamp(double.IsFinite(RadiusMm) ? RadiusMm : 0, 0, MaxRadiusMm);
    public double Inset => Math.Clamp(double.IsFinite(InsetMm) ? InsetMm : 0, 0, MaxInsetMm);
    public uint Argb => Gray ? GrayArgb : BlackArgb;

    /// <summary>Сколько миллиметров от края занимает рамка вместе с зазором до текста (0,5 мм): содержимое — не ближе, чтобы рамка не резала штрихкод и текст.</summary>
    public double KeepOutMm => Inset + Thickness + 0.5;

    private static float Px(double mm) => (float)(mm / 25.4 * Dpi);

    /// <summary>Толщина линии в точках 203 dpi — не меньше одной точки.</summary>
    public float StrokePx => Math.Max(1f, (float)Math.Round(Px(Thickness)));

    /// <summary>Сплошная прямоугольная рамка рисуется четырьмя полосами пиксель-в-пиксель; скруглённая или пунктирная — линией по средней линии.</summary>
    public bool IsPlainRect => Radius <= 0.001 && !Dashed;

    /// <summary>Средняя линия рамки (для пера/кисти толщиной <see cref="StrokePx"/>) в ячейке w×h точек.</summary>
    public RectangleF CenterLine(float w, float h)
    {
        var inset = Px(Inset) + StrokePx / 2f;
        return new RectangleF(inset, inset, Math.Max(w - 2 * inset, 1), Math.Max(h - 2 * inset, 1));
    }

    public float RadiusPx(float w, float h) => Math.Min(Px(Radius), Math.Min(w, h) / 2f);

    /// <summary>Пунктир: штрих 3 толщины, промежуток 2 толщины (в долях толщины пера, как у GDI+ DashPattern).</summary>
    public static readonly float[] DashPattern = { 3f, 2f };

    /// <summary>Четыре полосы рамки (сплошной прямоугольной) в ячейке w×h точек.</summary>
    public IEnumerable<RectangleF> Bars(float w, float h)
    {
        var i = Px(Inset);
        var s = StrokePx;
        var iw = Math.Max(w - 2 * i, s);
        var ih = Math.Max(h - 2 * i, s);
        yield return new RectangleF(i, i, iw, s);
        yield return new RectangleF(i, i + ih - s, iw, s);
        yield return new RectangleF(i, i, s, ih);
        yield return new RectangleF(i + iw - s, i, s, ih);
    }

    /// <summary>GDI+: рамка в мировых координатах g (точки 203 dpi; масштаб принтера уже в ScaleTransform).</summary>
    public void DrawGdi(Graphics g, float w, float h)
    {
        var color = Color.FromArgb(unchecked((int)Argb));
        if (IsPlainRect)
        {
            using var brush = new SolidBrush(color);
            foreach (var r in Bars(w, h))
                g.FillRectangle(brush, r);
            return;
        }
        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, StrokePx);
            if (Dashed)
                pen.DashPattern = DashPattern;
            var c = CenterLine(w, h);
            var r = RadiusPx(c.Width, c.Height);
            if (r <= 0.5f)
                g.DrawRectangle(pen, c.X, c.Y, c.Width, c.Height);
            else
            {
                using var path = RoundedPath(c, r);
                g.DrawPath(pen, path);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static GraphicsPath RoundedPath(RectangleF c, float r)
    {
        var d = 2 * r;
        var p = new GraphicsPath();
        p.AddArc(c.X, c.Y, d, d, 180, 90);
        p.AddArc(c.Right - d, c.Y, d, d, 270, 90);
        p.AddArc(c.Right - d, c.Bottom - d, d, d, 0, 90);
        p.AddArc(c.X, c.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Сцена: сплошная прямоугольная — четыре <see cref="LabelSceneRect"/> (как прежняя рамка в 1 точку), иначе <see cref="LabelSceneFrame"/>.</summary>
    public void AddScene(List<LabelSceneItem> items, float w, float h)
    {
        if (IsPlainRect)
        {
            foreach (var r in Bars(w, h))
                items.Add(new LabelSceneRect(r.X, r.Y, r.Width, r.Height, Argb));
            return;
        }
        var c = CenterLine(w, h);
        items.Add(new LabelSceneFrame(c.X, c.Y, c.Width, c.Height, StrokePx, RadiusPx(c.Width, c.Height), Argb, Dashed));
    }

    /// <summary>Skia: рамка в координатах canvas (точки 203 dpi).</summary>
    public void DrawSkia(SKCanvas canvas, float w, float h)
    {
        var items = new List<LabelSceneItem>();
        AddScene(items, w, h);
        foreach (var item in items)
            DrawSkiaItem(canvas, item);
    }

    internal static void DrawSkiaItem(SKCanvas canvas, LabelSceneItem item)
    {
        switch (item)
        {
            case LabelSceneRect r:
                using (var paint = new SKPaint { Color = new SKColor(r.Argb), IsAntialias = false })
                    canvas.DrawRect(new SKRect(r.X, r.Y, r.X + r.W, r.Y + r.H), paint);
                break;
            case LabelSceneFrame f:
                using (var paint = new SKPaint { Color = new SKColor(f.Argb), Style = SKPaintStyle.Stroke, StrokeWidth = f.StrokePx, IsAntialias = true })
                {
                    if (f.Dashed)
                        paint.PathEffect = SKPathEffect.CreateDash(new[] { DashPattern[0] * f.StrokePx, DashPattern[1] * f.StrokePx }, 0);
                    canvas.DrawRoundRect(new SKRect(f.X, f.Y, f.X + f.W, f.Y + f.H), f.RadiusPx, f.RadiusPx, paint);
                }
                break;
        }
    }
}
