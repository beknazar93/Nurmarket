using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: схема этикетки весов ШТРИХ-ПРИНТ в масштабе — для «Макета этикетки» и мастера
/// «Заменить надпись на СОМ». Рисует белую этикетку 54 мм × длина бумаги, сетку через 5 мм и
/// прямоугольники включённых элементов (размер — по шрифту и длине строки, как в Приложении 3
/// протокола). Красная рамка — элемент вылезает за этикетку или накладывается на другой: весы
/// в таком случае напечатают правильно только один из них. Элементы можно перетаскивать мышью
/// (шаг 1 мм), если <see cref="AllowDrag"/>. Цвета — «бумажные» (белый/чёрный) в любой теме:
/// это картинка этикетки, а не часть интерфейса.
/// </summary>
public sealed class ShtrikhLabelCanvas : Control
{
    private const double DefaultPaper = 40;

    public ShtrikhLabelLayout? Layout { get; set; }
    public IReadOnlyList<byte>? Lengths { get; set; }
    public int NameLines { get; set; } = 2;
    public int MessageLines { get; set; } = 1;
    public bool AllowDrag { get; set; }

    /// <summary>Подпись элемента (короткое название на языке кассы).</summary>
    public Func<ShtrikhLabelElement, string>? Caption { get; set; }

    /// <summary>Текст, который реально будет напечатан (для своих текстов — их содержимое).</summary>
    public Func<ShtrikhLabelElement, string?>? SampleText { get; set; }

    public string? SelectedKey { get; set; }

    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<string>? ElementMoved;

    private ShtrikhLabelElement? _drag;
    private Point _dragStart;
    private (int X, int Y) _dragOrigin;

    public ShtrikhLabelCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double PaperMm => Layout is { PaperLength: > 0 } l ? l.PaperLength : DefaultPaper;

    private double Scale(Size available)
    {
        var width = double.IsInfinity(available.Width) || available.Width <= 0 ? 380 : available.Width;
        return Math.Clamp((width - 16) / ShtrikhLabelFormat.PrintWidthMm, 4, 9);
    }

    private double _scale = 7;

    protected override Size MeasureOverride(Size availableSize)
    {
        _scale = Scale(availableSize);
        return new Size(ShtrikhLabelFormat.PrintWidthMm * _scale + 16, PaperMm * _scale + 16);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _scale = Scale(finalSize);
        return finalSize;
    }

    /// <summary>Прямоугольник элемента в мм или null, если он не печатается.</summary>
    public static Rect? ElementRect(ShtrikhLabelLayout layout, ShtrikhLabelElement e, IReadOnlyList<byte>? lengths, int nameLines, int messageLines)
    {
        if (e.Kind == ShtrikhElementKind.Frame)
        {
            var f = layout.Frame;
            if (f.Top == 0 || f.Right <= f.Left || f.Bottom <= f.Top)
                return null;
            return new Rect(f.Left, f.Top - 1, f.Right - f.Left, f.Bottom - f.Top);
        }

        var (x, y) = layout.GetPosition(e);
        if (y == 0)
            return null;
        if (e.Key == "GoodsName" && nameLines == 0)
            return null;
        var (w, h) = layout.SizeOf(e, lengths, nameLines, messageLines);
        // Y отсчитывается от 1 (Приложение 3): элемент с Y = 1 начинается у верхнего края.
        return new Rect(x, y - 1, w, h);
    }

    /// <summary>Проблемы макета: вылет за край и наложения. Возвращает ключи «плохих» элементов
    /// и пары наложений (для текста предупреждения).</summary>
    public static (HashSet<string> Bad, List<(ShtrikhLabelElement A, ShtrikhLabelElement B)> Overlaps, List<ShtrikhLabelElement> Outside)
        FindProblems(ShtrikhLabelLayout layout, IReadOnlyList<byte>? lengths, int nameLines, int messageLines)
    {
        var bad = new HashSet<string>();
        var overlaps = new List<(ShtrikhLabelElement, ShtrikhLabelElement)>();
        var outside = new List<ShtrikhLabelElement>();
        var paper = layout.PaperLength > 0 ? layout.PaperLength : DefaultPaper;
        var rects = new List<(ShtrikhLabelElement E, Rect R)>();
        foreach (var e in ShtrikhLabelFormat.Elements)
        {
            if (!layout.HasEx && e.Block == 1)
                continue;
            if (ElementRect(layout, e, lengths, nameLines, messageLines) is not { } r)
                continue;
            // Рисунки и рамка — «прозрачные» (белое не печатается), их с текстом не сверяем.
            if (r.Right > ShtrikhLabelFormat.PrintWidthMm + 0.01 || r.Bottom > paper + 0.01)
            {
                outside.Add(e);
                bad.Add(e.Key);
            }
            rects.Add((e, r));
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (a, ra) = rects[i];
                var (b, rb) = rects[j];
                if (a.Kind is ShtrikhElementKind.Picture or ShtrikhElementKind.Frame
                    || b.Kind is ShtrikhElementKind.Picture or ShtrikhElementKind.Frame)
                    continue;
                var inter = ra.Intersect(rb);
                if (inter.Width > 0.01 && inter.Height > 0.01)
                {
                    overlaps.Add((a, b));
                    bad.Add(a.Key);
                    bad.Add(b.Key);
                }
            }
        }
        return (bad, overlaps, outside);
    }

    private static readonly IBrush Paper = Brushes.White;
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#1F2937"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E5E7EB")), 1);
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#9CA3AF")), 1);
    private static readonly IPen NormalPen = new Pen(new SolidColorBrush(Color.Parse("#2563EB")), 1);
    private static readonly IPen BadPen = new Pen(new SolidColorBrush(Color.Parse("#DC2626")), 2);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#16A34A")), 2.5);
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#1A2563EB"));
    private static readonly IBrush InscriptionFill = new SolidColorBrush(Color.Parse("#1AF59E0B"));
    private static readonly IBrush TextFill = new SolidColorBrush(Color.Parse("#2616A34A"));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double pad = 8;
        var s = _scale;
        var paper = PaperMm;
        var labelRect = new Rect(pad, pad, ShtrikhLabelFormat.PrintWidthMm * s, paper * s);
        context.FillRectangle(Paper, labelRect);
        for (var mm = 5; mm < ShtrikhLabelFormat.PrintWidthMm; mm += 5)
            context.DrawLine(GridPen, new Point(pad + mm * s, pad), new Point(pad + mm * s, pad + paper * s));
        for (var mm = 5; mm < paper; mm += 5)
            context.DrawLine(GridPen, new Point(pad, pad + mm * s), new Point(pad + ShtrikhLabelFormat.PrintWidthMm * s, pad + mm * s));
        context.DrawRectangle(null, EdgePen, labelRect);

        if (Layout is not { } layout)
            return;

        var (bad, _, _) = FindProblems(layout, Lengths, NameLines, MessageLines);
        var typeface = new Typeface("Segoe UI");
        foreach (var e in ShtrikhLabelFormat.Elements)
        {
            if (!layout.HasEx && e.Block == 1)
                continue;
            if (ElementRect(layout, e, Lengths, NameLines, MessageLines) is not { } r)
                continue;
            var rect = new Rect(pad + r.X * s, pad + r.Y * s, Math.Max(2, r.Width * s), Math.Max(2, r.Height * s));
            var fill = e.Kind switch
            {
                ShtrikhElementKind.Inscription => InscriptionFill,
                ShtrikhElementKind.UserText => TextFill,
                ShtrikhElementKind.Frame => null,
                _ => Fill,
            };
            if (fill is not null)
                context.FillRectangle(fill, rect);
            var pen = e.Key == SelectedKey ? SelectedPen : bad.Contains(e.Key) ? BadPen : NormalPen;
            context.DrawRectangle(null, pen, rect);

            var caption = SampleText?.Invoke(e);
            if (string.IsNullOrWhiteSpace(caption))
                caption = Caption?.Invoke(e) ?? e.Key;
            var fontSize = Math.Clamp(Math.Min(rect.Height * 0.62, 12), 7, 12);
            var text = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Ink)
            {
                MaxTextWidth = Math.Max(10, rect.Width - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            using (context.PushClip(rect))
                context.DrawText(text, new Point(rect.X + 2, rect.Y + Math.Max(0, (Math.Min(rect.Height, 24) - text.Height) / 2)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Layout is not { } layout)
            return;
        var p = e.GetPosition(this);
        var hit = HitTest(layout, p);
        SelectedKey = hit?.Key;
        SelectionChanged?.Invoke(this, SelectedKey);
        if (hit is not null && AllowDrag)
        {
            _drag = hit;
            _dragStart = p;
            _dragOrigin = hit.Kind == ShtrikhElementKind.Frame ? (layout.Frame.Left, layout.Frame.Top) : layout.GetPosition(hit);
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null || Layout is not { } layout)
            return;
        var p = e.GetPosition(this);
        var dx = (int)Math.Round((p.X - _dragStart.X) / _scale);
        var dy = (int)Math.Round((p.Y - _dragStart.Y) / _scale);
        var x = Math.Clamp(_dragOrigin.X + dx, 0, ShtrikhLabelFormat.MaxX);
        var y = Math.Clamp(_dragOrigin.Y + dy, 1, ShtrikhLabelFormat.MaxY);
        var current = _drag.Kind == ShtrikhElementKind.Frame ? (layout.Frame.Left, layout.Frame.Top) : layout.GetPosition(_drag);
        if (current.Item1 == x && current.Item2 == y)
            return;
        layout.SetPosition(_drag, x, y);
        ElementMoved?.Invoke(this, _drag.Key);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
    }

    private ShtrikhLabelElement? HitTest(ShtrikhLabelLayout layout, Point p)
    {
        const double pad = 8;
        ShtrikhLabelElement? found = null;
        foreach (var e in ShtrikhLabelFormat.Elements)
        {
            if (!layout.HasEx && e.Block == 1)
                continue;
            if (ElementRect(layout, e, Lengths, NameLines, MessageLines) is not { } r)
                continue;
            var rect = new Rect(pad + r.X * _scale, pad + r.Y * _scale, Math.Max(4, r.Width * _scale), Math.Max(4, r.Height * _scale));
            if (rect.Contains(p))
                found = e; // последний в порядке обработки — «верхний», как печатают весы
        }
        return found;
    }
}
