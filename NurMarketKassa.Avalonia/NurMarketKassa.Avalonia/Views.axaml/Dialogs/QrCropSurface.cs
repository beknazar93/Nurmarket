using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (запрос владельца, п. 2): картинка QR целиком и поверх неё рамка обрезки, которую можно двигать и менять мышкой или пальцем:
/// тянуть за углы (меняет две стороны), за середины сторон (одну) и за саму рамку (сдвиг). Вне рамки картинка затемнена. Значения — проценты от краёв
/// (слева, сверху, справа, снизу), как у ползунков окна <see cref="QrCropDialog"/>: ползунки остаются запасным путём и синхронизируются с рамкой.</summary>
public sealed class QrCropSurface : Control
{
    /// <summary>Не дальше этой доли края (как максимум у ползунков).</summary>
    public const double MaxEdgePercent = 45;
    /// <summary>Минимум, который остаётся от картинки по каждой оси, проценты.</summary>
    public const double MinSizePercent = 10;

    private enum Hit { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private Bitmap? _bitmap;
    private double _l, _t, _r, _b;
    private Hit _drag = Hit.None;
    private Point _dragStart;
    private (double L, double T, double R, double B) _startCrop;

    /// <summary>Рамку изменил пользователь мышкой/пальцем (значения: слева, сверху, справа, снизу, проценты).</summary>
    public event Action<double, double, double, double>? CropChanged;

    public double LeftPercent => _l;
    public double TopPercent => _t;
    public double RightPercent => _r;
    public double BottomPercent => _b;

    public QrCropSurface()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public void SetBitmap(Bitmap? bitmap)
    {
        _bitmap = bitmap;
        InvalidateVisual();
    }

    /// <summary>Выставить рамку из ползунков (событие <see cref="CropChanged"/> не вызывается).</summary>
    public void SetCrop(double left, double top, double right, double bottom)
    {
        (_l, _t, _r, _b) = (left, top, right, bottom);
        InvalidateVisual();
    }

    /// <summary>Прямоугольник картинки внутри элемента (Uniform, по центру).</summary>
    private Rect ImageRect()
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return new Rect(Bounds.Size);
        // поля 10 px по краям, чтобы ручки рамки у самого края картинки не обрезались
        const double pad = 10;
        var scale = Math.Max(0.01, Math.Min((Bounds.Width - 2 * pad) / _bitmap.Size.Width, (Bounds.Height - 2 * pad) / _bitmap.Size.Height));
        var w = _bitmap.Size.Width * scale;
        var h = _bitmap.Size.Height * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private Rect FrameRect(Rect img) => new(
        img.X + img.Width * _l / 100, img.Y + img.Height * _t / 100,
        Math.Max(1, img.Width * (100 - _l - _r) / 100), Math.Max(1, img.Height * (100 - _t - _b) / 100));

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // чтобы касание по пустому месту тоже попадало в элемент
        if (_bitmap is null)
            return;
        var img = ImageRect();
        ctx.DrawImage(_bitmap, new Rect(_bitmap.Size), img);

        var frame = FrameRect(img);
        var dim = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        ctx.FillRectangle(dim, new Rect(img.X, img.Y, img.Width, frame.Y - img.Y));
        ctx.FillRectangle(dim, new Rect(img.X, frame.Bottom, img.Width, img.Bottom - frame.Bottom));
        ctx.FillRectangle(dim, new Rect(img.X, frame.Y, frame.X - img.X, frame.Height));
        ctx.FillRectangle(dim, new Rect(frame.Right, frame.Y, img.Right - frame.Right, frame.Height));

        // двойная рамка (тёмная снаружи, белая внутри) читается на любом фоне
        ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), frame.Inflate(1));
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), frame);
        foreach (var p in HandlePoints(frame))
        {
            var box = new Rect(p.X - 6, p.Y - 6, 12, 12);
            ctx.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1.5), box, 2, 2);
        }
    }

    private static Point[] HandlePoints(Rect f) =>
    [
        f.TopLeft, new(f.Center.X, f.Top), f.TopRight, new(f.Right, f.Center.Y),
        f.BottomRight, new(f.Center.X, f.Bottom), f.BottomLeft, new(f.Left, f.Center.Y),
    ];

    private Hit HitTest(Point p)
    {
        var img = ImageRect();
        var f = FrameRect(img);
        // зона захвата 24 px (для пальца), но не больше трети рамки, чтобы середина оставалась «сдвигом»
        var gx = Math.Min(24, f.Width / 3);
        var gy = Math.Min(24, f.Height / 3);
        var nearL = Math.Abs(p.X - f.Left) <= gx;
        var nearR = Math.Abs(p.X - f.Right) <= gx;
        var nearT = Math.Abs(p.Y - f.Top) <= gy;
        var nearB = Math.Abs(p.Y - f.Bottom) <= gy;
        var inX = p.X >= f.Left - gx && p.X <= f.Right + gx;
        var inY = p.Y >= f.Top - gy && p.Y <= f.Bottom + gy;
        if (nearT && nearL) return Hit.TopLeft;
        if (nearT && nearR) return Hit.TopRight;
        if (nearB && nearL) return Hit.BottomLeft;
        if (nearB && nearR) return Hit.BottomRight;
        if (nearT && inX) return Hit.Top;
        if (nearB && inX) return Hit.Bottom;
        if (nearL && inY) return Hit.Left;
        if (nearR && inY) return Hit.Right;
        return f.Contains(p) ? Hit.Move : Hit.None;
    }

    private static StandardCursorType CursorFor(Hit h) => h switch
    {
        Hit.TopLeft or Hit.BottomRight => StandardCursorType.TopLeftCorner,
        Hit.TopRight or Hit.BottomLeft => StandardCursorType.TopRightCorner,
        Hit.Left or Hit.Right => StandardCursorType.SizeWestEast,
        Hit.Top or Hit.Bottom => StandardCursorType.SizeNorthSouth,
        Hit.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Arrow,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit == Hit.None)
            return;
        _drag = hit;
        _dragStart = p;
        _startCrop = (_l, _t, _r, _b);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag == Hit.None)
        {
            Cursor = new Cursor(CursorFor(HitTest(p)));
            return;
        }
        ApplyDrag(p);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Hit.None)
            return;
        _drag = Hit.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Для тестов и клавиатуры: тот же расчёт, что при перетаскивании за угол/сторону/середину (имя: TopLeft, Left, Move…).</summary>
    public void DragForTest(string hit, double dxPercent, double dyPercent)
    {
        var img = ImageRect();
        var f = FrameRect(img);
        var start = hit switch
        {
            "TopLeft" => f.TopLeft, "TopRight" => f.TopRight, "BottomLeft" => f.BottomLeft, "BottomRight" => f.BottomRight,
            "Left" => new Point(f.Left, f.Center.Y), "Right" => new Point(f.Right, f.Center.Y),
            "Top" => new Point(f.Center.X, f.Top), "Bottom" => new Point(f.Center.X, f.Bottom), _ => f.Center,
        };
        _drag = HitTest(start);
        _dragStart = start;
        _startCrop = (_l, _t, _r, _b);
        var moved = new Point(start.X + dxPercent / 100 * img.Width, start.Y + dyPercent / 100 * img.Height);
        ApplyDrag(moved);
        _drag = Hit.None;
    }

    /// <summary>Пересчёт рамки по положению указателя при перетаскивании (мышь, палец и тест).</summary>
    private void ApplyDrag(Point p)
    {
        var img = ImageRect();
        if (img.Width <= 0 || img.Height <= 0)
            return;
        var dx = (p.X - _dragStart.X) / img.Width * 100;
        var dy = (p.Y - _dragStart.Y) / img.Height * 100;
        var (l, t, r, b) = _startCrop;
        double Clamp(double v, double other) => Math.Max(0, Math.Min(Math.Min(MaxEdgePercent, 100 - MinSizePercent - other), v));
        switch (_drag)
        {
            case Hit.Move:
                dx = Math.Max(-l, Math.Min(r, dx));
                dy = Math.Max(-t, Math.Min(b, dy));
                (l, r, t, b) = (l + dx, r - dx, t + dy, b - dy);
                break;
            case Hit.Left: l = Clamp(l + dx, r); break;
            case Hit.Right: r = Clamp(r - dx, l); break;
            case Hit.Top: t = Clamp(t + dy, b); break;
            case Hit.Bottom: b = Clamp(b - dy, t); break;
            case Hit.TopLeft: l = Clamp(l + dx, r); t = Clamp(t + dy, b); break;
            case Hit.TopRight: r = Clamp(r - dx, l); t = Clamp(t + dy, b); break;
            case Hit.BottomLeft: l = Clamp(l + dx, r); b = Clamp(b - dy, t); break;
            case Hit.BottomRight: r = Clamp(r - dx, l); b = Clamp(b - dy, t); break;
        }
        (_l, _t, _r, _b) = (l, t, r, b);
        InvalidateVisual();
        CropChanged?.Invoke(_l, _t, _r, _b);
    }
}
