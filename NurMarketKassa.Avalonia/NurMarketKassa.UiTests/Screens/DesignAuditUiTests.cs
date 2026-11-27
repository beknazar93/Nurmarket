using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 («дизайн и удобство 10/10»): общий проход по всем окнам и диалогам кассы и программы владельца — то, что не ловят TextNotClipped / FormFieldsOverlap / N7.
/// Известный дефект: на 1024×600 в виде «список» мелкий остаток печатался поверх цены — две надписи друг на друге, а проверки ловили только «текст шире поля» и «поле на поле».
/// Правила по визуальному дереву (скрытое, Opacity=0 и содержимое вне прокрутки не считаются):
/// (1) «надпись-на-надписи»: две видимые надписи (TextBlock) не печатаются друг поверх друга (реальные границы текста, пересечение больше 3 px по обеим осям;
///     родитель и потомок — разные случаи, не считаются);
/// (2) «цель-мала»: кнопка / переключатель / выпадающий список меньше 40 px по высоте (для сенсорного экрана);
/// (3) «контраст»: цвет надписи против фона, который реально под ней (ближайшие предки с кистью, смешение с прозрачностью), не ниже 3:1 (крупный/жирный текст) и 4,5:1 (мелкий) — порог
///     держим 3:1 для всех (пары 3–4,5 идут в отчёт для глаз, но не валят тест).
/// Размеры: окна — 1024×600 и 1366×768, диалоги — по содержимому; светлая тема ru/ky/uz и тёмная ru/uz. Список уже известных нарушений — Snapshots/known-design-audit.txt
/// («храповик»: новое нарушение — провал; исправил — удали строку; обновить сознательно: NURMARKET_UI_UPDATE=1). Подробный отчёт для глаз (все нарушения, в т.ч. известные) —
/// %TEMP%\nurmarket-tests\design-audit\*.txt.</summary>
[Collection("UiScenarios")]
public sealed class DesignAuditUiTests
{
    private const double TextOverlap = 3;
    private const double MinTarget = 40;
    private const double MinContrast = 3.0;

    public static IEnumerable<object[]> Matrix()
    {
        var names = ScreenCatalog.All.Where(s => s.SkipReason is null).Select(s => "c-" + s.Name).Concat(TextNotClippedTests.ExtraWindows.Keys);
        foreach (var name in names)
        {
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
                yield return [name, UiTheme.Light, lang];
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Uzbek })
                yield return [name, UiTheme.Dark, lang];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Window_DesignAudit(string name, UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        Window window;
        bool sized;
        if (name.StartsWith("c-", StringComparison.Ordinal))
        {
            var screen = ScreenCatalog.Get(name[2..]);
            window = screen.Create();
            sized = screen.Kind == ScreenKind.Window;
        }
        else
        {
            window = TextNotClippedTests.ExtraWindows[name]();
            sized = true;
        }
        var code = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };
        var found = new Dictionary<string, string>();   // ключ → подробность (для храповика)
        var info = new List<string>();                  // что только для глаз
        try
        {
            foreach (var size in sized ? new[] { new UiSize(1024, 600), new UiSize(1366, 768) } : [default(UiSize)])
            {
                using (var shot = UiShot.Render(window, sized ? size : null))
                {
                    // Снимки для глаз: NURMARKET_DESIGN_SHOTS=<папка> (в отчёт «Дизайн и удобство»).
                    if (Environment.GetEnvironmentVariable("NURMARKET_DESIGN_SHOTS") is { Length: > 0 } shotsDir && lang == AppLanguage.Russian)
                    {
                        Directory.CreateDirectory(shotsDir);
                        using var image = SkiaSharp.SKImage.FromBitmap(shot);
                        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                        File.WriteAllBytes(Path.Combine(shotsDir, $"{name}-{theme}-{(sized ? size.ToString() : "auto")}.png"), data.ToArray());
                    }
                }
                UiShot.Settle(3);
                Audit(window, name, theme, found, info, sized ? size.ToString() : "по содержимому");
            }
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }

        Finish($"{name}-{theme}-{code}", $"({name}, {theme}, {code})", found, info);
    }

    /// <summary>Отчёт для глаз в папке TEMP, подпапка nurmarket-tests/design-audit, и «храповик» по списку известных нарушений.</summary>
    internal static void Finish(string reportName, string what, Dictionary<string, string> found, List<string> info)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "design-audit");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, reportName + ".txt"),
            found.Select(kv => "FAIL " + kv.Key + " — " + kv.Value).Concat(info.Select(i => "info " + i)));

        if (Snapshots.PngSnapshot.UpdateMode)
        {
            KnownDesignAudit.Add(found.Keys);
            return;
        }
        var known = KnownDesignAudit.Load();
        var fresh = found.Where(kv => !known.Contains(kv.Key)).ToList();
        Assert.True(fresh.Count == 0, $"Дизайн-проверка: новые нарушения {what}:{Environment.NewLine}" + string.Join(Environment.NewLine, fresh.Take(25).Select(kv => $"  {kv.Key} — {kv.Value}"))
            + (fresh.Count > 25 ? $"{Environment.NewLine}  … и ещё {fresh.Count - 25}" : ""));
    }

    /// <summary>Проверка самой проверки: на искусственном окне надпись поверх надписи, мелкая кнопка и бледный текст на светлом фоне находятся; нормальная раскладка — нет.</summary>
    [AvaloniaFact]
    public async Task Checker_FindsEachKindOfViolation_InSyntheticWindow()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var canvas = new Canvas();
        void Put(Control c, double x, double y, double w = double.NaN, double h = double.NaN)
        {
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            if (!double.IsNaN(w))
                c.Width = w;
            if (!double.IsNaN(h))
                c.Height = h;
            canvas.Children.Add(c);
        }
        Put(new TextBlock { Text = "12 500,00", FontSize = 18, FontWeight = FontWeight.Bold }, 20, 20);
        Put(new TextBlock { Text = "Остаток 12 шт.", FontSize = 13 }, 20, 28);                    // поверх цены
        Put(new TextBlock { Text = "Нормально", FontSize = 13 }, 20, 120);
        Put(new TextBlock { Text = "Ниже", FontSize = 13 }, 20, 150);                              // под ним, без пересечения
        Put(new Button { Content = "Мелкая" }, 200, 20, 90, 30);                                  // 30 px < 40
        Put(new Button { Content = "Нормальная" }, 200, 80, 120, 44);
        Put(new TextBlock { Text = "Бледный текст", FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#D0D0D0")) }, 400, 20); // на белом
        var window = new Window { Content = new Border { Background = Brushes.White, Child = canvas }, Width = 600, Height = 260 };
        try
        {
            using var _ = UiShot.Render(window);
            var found = new Dictionary<string, string>();
            Audit(window, "synthetic", UiTheme.Light, found, new List<string>(), "600x260");
            var rules = found.Keys.Select(k => k.Split('|')[1]).ToList();
            Assert.Contains("надпись-на-надписи", rules);
            Assert.Contains("цель-мала", rules);
            Assert.Contains("контраст", rules);
            Assert.Single(rules, r => r == "надпись-на-надписи");   // «Нормально» / «Ниже» не пересекаются
            Assert.Single(rules, r => r == "цель-мала");             // только «Мелкая»
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ проверка

    internal static void Audit(Window window, string scope, UiTheme theme, Dictionary<string, string> found, List<string> info, string size)
    {
        var all = window.GetVisualDescendants().OfType<Control>().Where(Shown).ToList();
        var texts = all.OfType<TextBlock>().Where(t => TextOf(t).Length > 0).ToList();

        // (1) надпись на надписи
        var rects = new List<(TextBlock T, Rect R)>();
        foreach (var t in texts)
            if (TextRect(t, window) is { } r)
                rects.Add((t, r));
        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            var (a, b) = (rects[i], rects[j]);
            if (a.T.IsVisualAncestorOf(b.T) || b.T.IsVisualAncestorOf(a.T))
                continue;
            var inter = a.R.Intersect(b.R);
            if (inter.Width > TextOverlap && inter.Height > TextOverlap)
                Add(found, $"{scope}|надпись-на-надписи|{Desc(a.T)}|{Desc(b.T)}", $"{size}: {Desc(a.T)} и {Desc(b.T)} перекрываются на {inter.Width:0}×{inter.Height:0} px");
        }

        // (2) цели касания
        foreach (var c in all.Where(c => c is Button or ToggleButton or ComboBox && c.TemplatedParent is null && c.IsEffectivelyEnabled && !IsPartOfField(c)))
        {
            if (VisibleRect(c, window) is not { } vr)
                continue;
            if (c.Bounds.Height < MinTarget - 0.5 && vr.Height >= c.Bounds.Height - 0.5)
                Add(found, $"{scope}|цель-мала|{Desc(c)}", $"{size}: {Desc(c)} высотой {c.Bounds.Height:0} px (нужно ≥ {MinTarget})");
        }

        // (3) контраст
        foreach (var t in texts)
        {
            if (t.Classes.Contains("mdl2") || !t.IsEffectivelyEnabled || t.Foreground is not ISolidColorBrush fg)
                continue;
            if (!Background(t, window, out var bg))
                continue;
            var alpha = fg.Color.A / 255.0 * fg.Opacity * EffectiveOpacity(t);
            var ink = Blend(bg, fg.Color, alpha);
            var ratio = Ratio(ink, bg);
            var large = t.FontSize >= 18 || (t.FontSize >= 14 && t.FontWeight >= FontWeight.SemiBold);
            if (ratio < MinContrast)
                Add(found, $"{scope}|контраст|{Desc(t)}", $"{size}: текст {Desc(t)} {Hex(ink)} на {Hex(bg)} — {ratio:0.0}:1 (нужно ≥ {MinContrast})");
            else if (ratio < (large ? 3.0 : 4.5))
                info.Add($"{scope}/{theme} {size}: слабый контраст {Desc(t)} {ratio:0.0}:1");
        }
    }

    private static void Add(Dictionary<string, string> found, string key, string detail)
    {
        if (!found.ContainsKey(key))
            found[key] = detail;
    }

    private static bool IsPartOfField(Control c) => c.GetVisualAncestors().Any(a => a is TextBox or NumericUpDown or DatePicker or CalendarDatePicker or AutoCompleteBox or ComboBox);

    private static bool Shown(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (!cur.IsVisible || cur.Opacity <= 0.01)
                return false;
        return v.Bounds.Width > 0 && v.Bounds.Height > 0;
    }

    private static string TextOf(TextBlock tb)
    {
        if (!string.IsNullOrWhiteSpace(tb.Text))
            return tb.Text;
        return tb.Inlines is { Count: > 0 } ? string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text)).Trim() : "";
    }

    private static Rect? RectIn(Visual v, Visual root) => v.TranslatePoint(default, root) is { } p ? new Rect(p, v.Bounds.Size) : null;

    /// <summary>Видимая часть: пересечение с областями прокрутки и обрезки предков.</summary>
    private static Rect? VisibleRect(Visual v, Visual root)
    {
        if (RectIn(v, root) is not { } r)
            return null;
        foreach (var a in v.GetVisualAncestors())
        {
            if (a is not (ScrollViewer or Control { ClipToBounds: true }) || ReferenceEquals(a, root))
                continue;
            if (RectIn(a, root) is not { } ar)
                continue;
            r = r.Intersect(ar);
            if (r.Width <= 0 || r.Height <= 0)
                return null;
        }
        return r;
    }

    /// <summary>Реальные границы напечатанного текста (не всей ячейки): по выравниванию, ширине строки и числу строк; обрезка прокруткой учитывается.</summary>
    private static Rect? TextRect(TextBlock t, Window root)
    {
        if (VisibleRect(t, root) is null || RectIn(t, root) is not { } full)
            return null;
        var typeface = new Typeface(t.FontFamily ?? FontFamily.Default, t.FontStyle, t.FontWeight, t.FontStretch);
        var wraps = t.TextWrapping != TextWrapping.NoWrap;
        var ft = new FormattedText(TextOf(t), CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null)
        {
            MaxTextWidth = wraps ? Math.Max(1, t.Bounds.Width) : double.PositiveInfinity,
        };
        // Текст без переноса и без многоточия рисуется за границы своего блока (вылез на соседнюю надпись: «Оплатить 13 049,5» + «13 049,50 сом» в кнопке) — берём его настоящую ширину,
        // а не ширину блока; с переносом или многоточием текст остаётся в блоке.
        var w = !wraps && t.TextTrimming == TextTrimming.None ? ft.Width : Math.Min(ft.Width, t.Bounds.Width);
        var h = Math.Min(ft.Height, t.Bounds.Height);
        var align = t.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch
            ? t.TextAlignment switch { TextAlignment.Right => Avalonia.Layout.HorizontalAlignment.Right, TextAlignment.Center => Avalonia.Layout.HorizontalAlignment.Center, _ => Avalonia.Layout.HorizontalAlignment.Left }
            : t.HorizontalAlignment;
        var x = align switch
        {
            Avalonia.Layout.HorizontalAlignment.Right => full.Right - w,
            Avalonia.Layout.HorizontalAlignment.Center => full.Left + (full.Width - w) / 2,
            _ => full.Left,
        };
        var textRect = new Rect(x, full.Top, w, h);
        foreach (var a in t.GetVisualAncestors())   // обрезка предками (прокрутка, ClipToBounds) — самим блоком текст не обрезается
        {
            if (a is not (ScrollViewer or Control { ClipToBounds: true }) || ReferenceEquals(a, root) || RectIn(a, root) is not { } ar)
                continue;
            textRect = textRect.Intersect(ar);
        }
        return textRect.Width > 0 && textRect.Height > 0 ? textRect : null;
    }

    private static double EffectiveOpacity(Visual v)
    {
        var o = 1.0;
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            o *= cur.Opacity;
        return o;
    }

    /// <summary>Фон под надписью: смешиваем кисти ближайших предков снизу вверх, пока не станет непрозрачно. Градиент/картинка под текстом — не проверяем (false).</summary>
    private static bool Background(Visual v, Window window, out Color bg)
    {
        var layers = new List<Color>();
        for (var cur = v.GetVisualParent(); cur is not null; cur = cur.GetVisualParent())
        {
            IBrush? brush = cur switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                ContentPresenter cp => cp.Background,
                TemplatedControl tc => tc.Background,
                _ => null,
            };
            if (brush is null)
                continue;
            if (brush is not ISolidColorBrush s)
            {
                bg = default;
                return false;
            }
            var a = s.Color.A / 255.0 * s.Opacity * (cur is Visual cv ? cv.Opacity : 1);
            if (a <= 0.01)
                continue;
            layers.Add(Color.FromArgb((byte)Math.Round(a * 255), s.Color.R, s.Color.G, s.Color.B));
            if (a >= 0.99)
                break;
        }
        var baseColor = Application.Current!.TryFindResource("BrushWindow", Application.Current.ActualThemeVariant, out var w) && w is ISolidColorBrush ws ? ws.Color : Colors.White;
        var start = layers.Count;
        if (layers.Count > 0 && layers[^1].A >= 250)
        {
            baseColor = layers[^1];
            start = layers.Count - 1;
        }
        var cur0 = Color.FromRgb(baseColor.R, baseColor.G, baseColor.B);
        for (var i = start - 1; i >= 0; i--)
            cur0 = Blend(cur0, layers[i], layers[i].A / 255.0);
        bg = cur0;
        return true;
    }

    private static Color Blend(Color under, Color over, double alpha) => Color.FromRgb(
        (byte)Math.Round(under.R + (over.R - under.R) * alpha),
        (byte)Math.Round(under.G + (over.G - under.G) * alpha),
        (byte)Math.Round(under.B + (over.B - under.B) * alpha));

    private static double Lum(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Lum(a), Lum(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Desc(Control c)
    {
        var text = c switch
        {
            TextBlock t => TextOf(t),
            _ => Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } a ? a : UiDrive.LabelOf(c),
        };
        // Числа в ключе заменены на «#»: «Все 13 ›» не должно становиться новым нарушением, когда в каталоге прибавилась функция.
        text = string.IsNullOrWhiteSpace(text) ? "" : "«" + System.Text.RegularExpressions.Regex.Replace(text.Length > 28 ? text[..28] + "…" : text.Replace('\n', ' '), @"\d+", "#") + "»";
        return c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name) + text;
    }
}

/// <summary>2026-11-26: список известных нарушений дизайн-проверки (окно|правило|элементы), по одному на строку; чинить — удалять строку.</summary>
internal static class KnownDesignAudit
{
    private static readonly string FilePath = Path.Combine(Snapshots.PngSnapshot.BaselineDir, "..", "known-design-audit.txt");

    public static HashSet<string> Load() =>
        File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet() : [];

    public static void Add(IEnumerable<string> keys)
    {
        var set = Load();
        var before = set.Count;
        foreach (var k in keys)
            set.Add(k);
        if (set.Count == before && File.Exists(FilePath))
            return;
        var lines = new List<string> { "# 2026-11-26 (дизайн и удобство): известные нарушения DesignAuditUiTests (окно|правило|элементы). Чинить — удалять строку. Новые нарушения валят тест." };
        lines.AddRange(set.OrderBy(x => x, StringComparer.Ordinal));
        File.WriteAllLines(FilePath, lines);
    }
}
