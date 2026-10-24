using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System.Collections.Generic;
using Avalonia.Styling;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Controls;

/// <summary>Вариант цвета знака NUR CRM (брендбук 3.1–3.4).</summary>
public enum BrandMarkVariant
{
    /// <summary>По теме: светлая — цветной знак, тёмная — знак для чёрного фона.</summary>
    Auto,
    /// <summary>На светлом: жёлтые лучи и солнце, чёрная гора.</summary>
    Light,
    /// <summary>На чёрном: белые лучи, жёлтые солнце и гора.</summary>
    Dark,
    /// <summary>На жёлтом: белые лучи, чёрная гора.</summary>
    Yellow,
    /// <summary>Один цвет (<see cref="BrandMark.MonoBrush"/>).</summary>
    Mono,
}

/// <summary>Вид логотипа (брендбук 2.2).</summary>
public enum BrandLogoKind { Logomark, Horizontal, Vertical, Wordmark }

/// <summary>Точный логотип NUR CRM: контуры взяты из векторов «Brand Book.pdf» (<see cref="BrandLogoPaths"/>), а не нарисованы
/// «на глаз». Рисует ровно в своих границах без искажений (пропорции сохраняются). Минимумы брендбука: знак — 28 px, с текстом —
/// 100 px по ширине: меньшего размера контрол не рисует (берёт минимум).</summary>
public class BrandLogo : Control
{
    public const double MinMark = 28, MinText = 100;

    public static readonly StyledProperty<BrandLogoKind> KindProperty = AvaloniaProperty.Register<BrandLogo, BrandLogoKind>(nameof(Kind), BrandLogoKind.Horizontal);
    public static readonly StyledProperty<BrandMarkVariant> VariantProperty = AvaloniaProperty.Register<BrandLogo, BrandMarkVariant>(nameof(Variant), BrandMarkVariant.Auto);
    public static readonly StyledProperty<bool> CyrillicProperty = AvaloniaProperty.Register<BrandLogo, bool>(nameof(Cyrillic));
    public static readonly StyledProperty<IBrush?> MonoBrushProperty = AvaloniaProperty.Register<BrandLogo, IBrush?>(nameof(MonoBrush));

    private static readonly Dictionary<string, Geometry> Cache = new();

    static BrandLogo()
    {
        AffectsRender<BrandLogo>(KindProperty, VariantProperty, CyrillicProperty, MonoBrushProperty);
        AffectsMeasure<BrandLogo>(KindProperty);
    }

    public BrandLogoKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public BrandMarkVariant Variant { get => GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public bool Cyrillic { get => GetValue(CyrillicProperty); set => SetValue(CyrillicProperty, value); }
    public IBrush? MonoBrush { get => GetValue(MonoBrushProperty); set => SetValue(MonoBrushProperty, value); }

    private static Geometry G(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out var g))
                Cache[path] = g = Geometry.Parse(path);
            return g;
        }
    }

    private static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    private BrandLogoLayout Layout => Kind == BrandLogoKind.Vertical
        ? (Cyrillic ? BrandLogoPaths.VerticalCyr : BrandLogoPaths.VerticalLat)
        : (Cyrillic ? BrandLogoPaths.HorizontalCyr : BrandLogoPaths.HorizontalLat);

    private Rect Box
    {
        get
        {
            var l = Layout;
            return Kind switch
            {
                BrandLogoKind.Logomark => new Rect(0, 0, 100, 100),
                BrandLogoKind.Wordmark => new Rect(l.TX0, l.TY0, l.TX1 - l.TX0, l.TY1 - l.TY0),
                _ => new Rect(l.X0, l.Y0, l.X1 - l.X0, l.Y1 - l.Y0),
            };
        }
    }

    private double MinWidth0 => Kind == BrandLogoKind.Logomark ? MinMark : MinText;

    protected override Size MeasureOverride(Size availableSize)
    {
        var box = Box;
        var w = MinWidth0;
        if (!double.IsInfinity(availableSize.Width)) w = Math.Max(w, Math.Min(availableSize.Width, double.IsInfinity(availableSize.Height) ? availableSize.Width : availableSize.Height * box.Width / box.Height));
        else if (!double.IsInfinity(availableSize.Height)) w = Math.Max(w, availableSize.Height * box.Width / box.Height);
        else w = Math.Max(w, box.Width * 0.8);
        return new Size(w, w * box.Height / box.Width);
    }

    public override void Render(DrawingContext context)
    {
        var box = Box;
        var k = Math.Min(Math.Max(Bounds.Width, MinWidth0) / box.Width, Math.Max(Bounds.Height, MinWidth0 * box.Height / box.Width) / box.Height);
        var ox = (Bounds.Width - box.Width * k) / 2;
        var oy = (Bounds.Height - box.Height * k) / 2;
        var variant = Variant;
        if (variant == BrandMarkVariant.Auto)
            variant = ActualThemeVariant == ThemeVariant.Dark ? BrandMarkVariant.Dark : BrandMarkVariant.Light;
        var name = variant switch { BrandMarkVariant.Dark => "dark", BrandMarkVariant.Yellow => "yellow", BrandMarkVariant.Mono => "mono", _ => "light" };
        var (sun, mtn, txt) = BrandColors.Logo(name);
        IBrush sunB = B(sun), mtnB = B(mtn), txtB = B(txt);
        if (variant == BrandMarkVariant.Mono && MonoBrush is { } mono)
            sunB = mtnB = txtB = mono;

        var l = Layout;
        using var _ = context.PushClip(new Rect(Bounds.Size));
        using var __ = context.PushTransform(Matrix.CreateTranslation(-box.X, -box.Y) * Matrix.CreateScale(k, k) * Matrix.CreateTranslation(ox, oy));
        if (Kind != BrandLogoKind.Wordmark)
        {
            context.DrawGeometry(sunB, null, G(l.Sun));
            context.DrawGeometry(mtnB, null, G(l.Mountain));
        }
        if (Kind != BrandLogoKind.Logomark)
            foreach (var t in l.Text)
                context.DrawGeometry(txtB, null, G(t));
    }
}

/// <summary>Только знак (логомарк) — частный случай <see cref="BrandLogo"/>: квадрат, не меньше 28 px.</summary>
public sealed class BrandMark : BrandLogo
{
    public BrandMark() => Kind = BrandLogoKind.Logomark;
}

/// <summary>Фирменный паттерн лучей (брендбук 6.1) как мягкий фон пустых состояний и заставок: сетка «звёзд» из лучей,
/// рисуется кодом. Цвет — <see cref="Foreground"/>, прозрачность — обычное свойство Opacity (6–8 % — «минималистично»).</summary>
public sealed class BrandPattern : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<BrandPattern, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> CellSizeProperty =
        AvaloniaProperty.Register<BrandPattern, double>(nameof(CellSize), 56);

    static BrandPattern()
    {
        AffectsRender<BrandPattern>(ForegroundProperty, CellSizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<BrandPattern>(false);
    }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double CellSize { get => GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.Gray;
        var cell = Math.Max(24, CellSize);
        var pen = new Pen(brush, Math.Max(1, cell * 0.05));
        var segs = BrandGeometry.PatternCell(cell);
        using var _ = context.PushClip(new Rect(Bounds.Size));
        for (var y = 0.0; y < Bounds.Height; y += cell)
            for (var x = 0.0; x < Bounds.Width; x += cell)
                foreach (var s in segs)
                    context.DrawLine(pen, new Point(x + s.X1, y + s.Y1), new Point(x + s.X2, y + s.Y2));
    }
}
