using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-10-22, брендбук NUR CRM (разд. 2–3, 6): знак «восход солнца над горой» и фирменный паттерн лучей —
/// чистая геометрия без картинок. Квадрат 100×100: жёлтое солнце с лучами (веер сверху и по бокам), чёрная гора —
/// заполненный треугольник с веером линий до нижней кромки. Из этих отрезков рисуют знак Avalonia (BrandMark),
/// PDF-бланки (SVG) и по тому же описанию — мобильное приложение (Flutter, brand_mark.dart). Менять пропорции,
/// число лучей и цвета нельзя (брендбук 3.7): цвета вариантов — в <see cref="BrandColors"/>.</summary>
public static class BrandGeometry
{
    public const double Size = 100;
    public const double Cx = 50, Cy = 47;
    public const double SunRadius = 22;
    public const double RayWidth = 3.2;
    public const double MountainWidth = 3.4;

    public readonly record struct Seg(double X1, double Y1, double X2, double Y2);

    /// <summary>Лучи солнца: от кромки круга до границы квадрата, угол от −12° до 192° через 12°.</summary>
    public static IReadOnlyList<Seg> Rays()
    {
        var list = new List<Seg>();
        for (var deg = -12; deg <= 192; deg += 12)
        {
            var a = deg * Math.PI / 180;
            double dx = Math.Cos(a), dy = -Math.Sin(a);
            var t = double.MaxValue;
            if (dx > 1e-9) t = Math.Min(t, (Size - Cx) / dx);
            else if (dx < -1e-9) t = Math.Min(t, -Cx / dx);
            if (dy > 1e-9) t = Math.Min(t, (Size - Cy) / dy);
            else if (dy < -1e-9) t = Math.Min(t, -Cy / dy);
            var r0 = SunRadius + 3.5;
            list.Add(new Seg(Cx + dx * r0, Cy + dy * r0, Cx + dx * t, Cy + dy * t));
        }
        return list;
    }

    /// <summary>Веер горы: от вершины к нижней кромке; крайние линии — склоны до углов квадрата.</summary>
    public static IReadOnlyList<Seg> Fan()
    {
        var xs = new[] { 0.0, 14, 27, 39, 50, 61, 73, 86, 100 };
        var list = new List<Seg>();
        foreach (var x in xs)
        {
            const double k = 0.3;
            list.Add(new Seg(Cx + (x - Cx) * k, Cy + (Size - Cy) * k, x, Size));
        }
        return list;
    }

    /// <summary>Вершина горы — залитый треугольник, в который сходится веер.</summary>
    public static (double X, double Y)[] Peak() =>
    [
        (Cx, Cy - 1.2), (Cx - 0.34 * Cx - 1.5, Cy + 0.34 * (Size - Cy)), (Cx + 0.34 * Cx + 1.5, Cy + 0.34 * (Size - Cy)),
    ];

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>SVG знака (viewBox 0 0 100 100) для PDF и файлов; цвета — #RRGGBB.</summary>
    public static string Svg(string rays, string sun, string mountain)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">");
        sb.Append($"<g stroke=\"{rays}\" stroke-width=\"{N(RayWidth)}\" fill=\"none\">");
        foreach (var s in Rays())
            sb.Append($"<line x1=\"{N(s.X1)}\" y1=\"{N(s.Y1)}\" x2=\"{N(s.X2)}\" y2=\"{N(s.Y2)}\"/>");
        sb.Append("</g>");
        sb.Append($"<circle cx=\"{N(Cx)}\" cy=\"{N(Cy)}\" r=\"{N(SunRadius)}\" fill=\"{sun}\"/>");
        sb.Append($"<g stroke=\"{mountain}\" stroke-width=\"{N(MountainWidth)}\" fill=\"none\">");
        foreach (var s in Fan())
            sb.Append($"<line x1=\"{N(s.X1)}\" y1=\"{N(s.Y1)}\" x2=\"{N(s.X2)}\" y2=\"{N(s.Y2)}\"/>");
        sb.Append("</g>");
        var peak = Peak();
        sb.Append($"<polygon fill=\"{mountain}\" points=\"{string.Join(' ', peak.Select(p => N(p.X) + "," + N(p.Y)))}\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Сегменты одной «звезды» паттерна лучей (брендбук 6.1): кольцо коротких лучей вокруг точки,
    /// размер ячейки <paramref name="cell"/>. Центр — (cell/2, cell/2).</summary>
    public static IReadOnlyList<Seg> PatternCell(double cell, int rays = 16)
    {
        var list = new List<Seg>();
        double c = cell / 2, r0 = cell * 0.16, r1 = cell * 0.42;
        for (var i = 0; i < rays; i++)
        {
            var a = i * 2 * Math.PI / rays;
            list.Add(new Seg(c + Math.Cos(a) * r0, c + Math.Sin(a) * r0, c + Math.Cos(a) * r1, c + Math.Sin(a) * r1));
        }
        return list;
    }

    /// <summary>SVG-полоса паттерна: <paramref name="cols"/>×<paramref name="rows"/> звёзд, цвет и прозрачность задаёт вызывающий.</summary>
    public static string PatternSvg(string color, double opacity, int cols, int rows, double cell = 20)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(cols * cell)} {N(rows * cell)}\">");
        sb.Append($"<g stroke=\"{color}\" stroke-opacity=\"{N(opacity)}\" stroke-width=\"{N(cell * 0.06)}\" fill=\"none\">");
        var cellSegs = PatternCell(cell);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
                foreach (var s in cellSegs)
                    sb.Append($"<line x1=\"{N(s.X1 + x * cell)}\" y1=\"{N(s.Y1 + y * cell)}\" x2=\"{N(s.X2 + x * cell)}\" y2=\"{N(s.Y2 + y * cell)}\"/>");
        sb.Append("</g></svg>");
        return sb.ToString();
    }
}

/// <summary>Цвета брендбука NUR CRM (разд. 4) и варианты знака (разд. 3.1–3.4).</summary>
public static class BrandColors
{
    public const string GoldenLight = "#FFB208";
    public const string DeepNight = "#191919";
    public const string OrangeFlame = "#F77B14";
    public const string SunnyYellow = "#F9D919";
    public const string WoodBrown = "#7C460B";

    /// <summary>Цвета лучей, солнца и горы для варианта: light (на светлом), dark (на чёрном), yellow (на жёлтом).</summary>
    public static (string Rays, string Sun, string Mountain) Mark(string variant, string? mono = null) => variant switch
    {
        "dark" => ("#FFFFFF", "#FFFFFF", GoldenLight), // по PDF: на чёрном солнце с лучами белое, гора жёлтая
        "yellow" => ("#FFFFFF", "#FFFFFF", DeepNight),
        "mono" => (mono ?? DeepNight, mono ?? DeepNight, mono ?? DeepNight),
        _ => (GoldenLight, GoldenLight, DeepNight),
    };

    /// <summary>Цвета логотипа с буквами: солнце (лучи+круг — один контур), гора, текст (стр. 3.1–3.4 брендбука).</summary>
    public static (string Sun, string Mountain, string Text) Logo(string variant, string? mono = null) => variant switch
    {
        "dark" => ("#FFFFFF", GoldenLight, "#FFFFFF"),
        "yellow" => ("#FFFFFF", DeepNight, DeepNight),
        "mono" => (mono ?? DeepNight, mono ?? DeepNight, mono ?? DeepNight),
        _ => (GoldenLight, DeepNight, DeepNight),
    };
}

/// <summary>SVG точного логотипа из векторных контуров брендбука (<see cref="BrandLogoPaths"/>).</summary>
public static class BrandLogoSvg
{
    /// <summary>kind: horizontal | vertical | wordmark | mark; cyr — кириллический вариант «НУР СРМ».</summary>
    public static string Build(string kind, string variant, bool cyr = false, string? mono = null)
    {
        var lay = kind == "vertical" ? (cyr ? BrandLogoPaths.VerticalCyr : BrandLogoPaths.VerticalLat) : (cyr ? BrandLogoPaths.HorizontalCyr : BrandLogoPaths.HorizontalLat);
        var (sun, mtn, txt) = BrandColors.Logo(variant, mono);
        double x0, y0, x1, y1;
        switch (kind)
        {
            case "mark": (x0, y0, x1, y1) = (0, 0, 100, 100); break;
            case "wordmark": (x0, y0, x1, y1) = (lay.TX0, lay.TY0, lay.TX1, lay.TY1); break;
            default: (x0, y0, x1, y1) = (lay.X0, lay.Y0, lay.X1, lay.Y1); break;
        }
        var sb = new System.Text.StringBuilder();
        string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(x0)} {N(y0)} {N(x1 - x0)} {N(y1 - y0)}\">");
        if (kind != "wordmark")
        {
            sb.Append($"<path fill=\"{sun}\" d=\"{lay.Sun}\"/>");
            sb.Append($"<path fill=\"{mtn}\" d=\"{lay.Mountain}\"/>");
        }
        if (kind != "mark")
            foreach (var t in lay.Text)
                sb.Append($"<path fill=\"{txt}\" d=\"{t}\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
