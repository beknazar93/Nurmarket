using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>Прямоугольник элемента в точках окна (с учётом масштаба обёртки раздела).</summary>
public readonly record struct LayoutBox(double X, double Y, double W, double H)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"x={X:0.#} y={Y:0.#} w={W:0.#} h={H:0.#}");
}

/// <summary>2026-11-11 (владелец: «чтобы не было как с пульсацией», видео «у.mp4»): «отпечаток» раскладки окна — прямоугольники всех видимых элементов ВНЕ прокручиваемого
/// содержимого (корень, рабочая область, панели, кнопки, таблицы, сами ScrollViewer-ы и их видимая область), ширина содержимого прокрутки, число колонок плиток
/// (WrapPanel/UniformGrid) и «режимы» окон (закрытые bool-поля вида _compact/_narrow…/_phone… у элементов приложения). Положения внутри прокрутки не входят — они законно
/// меняются при прокрутке; тексты не входят (часы, счётчики) — их смена видна по размерам кнопок.</summary>
public sealed class LayoutFingerprint
{
    public Dictionary<string, LayoutBox> Boxes { get; } = new(StringComparer.Ordinal);
    /// <summary>2026-11-12 (аудит Fable, В-14): положения ВНУТРИ прокрутки — прямоугольники элементов относительно содержимого прокрутки (при прокрутке не меняются; меняются, только если
    /// раскладка внутри сдвинулась сама) и ширины колонок таблиц. Сравниваются только общие ключи (ленивые/виртуальные элементы появляются и исчезают законно).</summary>
    public Dictionary<string, LayoutBox> Inner { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Modes { get; } = new(StringComparer.Ordinal);
    public double ClientWidth { get; set; }
    public double ClientHeight { get; set; }
    /// <summary>Ширина корня содержимого окна (у раздела владельца — обёртка масштаба), в точках макета.</summary>
    public double RootWidth { get; set; }
    /// <summary>Ширина содержимого внутри обёртки масштаба (без обёртки — то же, что RootWidth), в точках макета.</summary>
    public double InnerWidth { get; set; }
    public List<ScrollViewer> ScrollViewers { get; } = new();
    public List<DataGrid> DataGrids { get; } = new();
    public List<TabControl> TabControls { get; } = new();

    public string ColsText => string.Join(",", Modes.Where(m => m.Key.Contains("cols", StringComparison.Ordinal)).Select(m => m.Value));
}

/// <summary>Одно расхождение двух отпечатков.</summary>
public sealed record LayoutDiff(string Key, string Before, string After, double Weight)
{
    public override string ToString() => $"{LayoutStabilityProbe.ShortKey(Key)}: {Before} → {After}";
}

/// <summary>2026-11-11 (владелец: «чтобы не было как с пульсацией»): общая защита от пульсации и дрожания раскладки. Снимает отпечаток раскладки окна (<see cref="LayoutFingerprint"/>)
/// на каждом шаге сценария и сравнивает с исходным (допуск ±2 px). Сценарии: покой (окно само не меняется), прокрутка колесом/смещением вниз и вверх, плавный ресайз
/// 1000→1900→1000 (режимы и колонки переключаются не чаще 2 раз на монотонном отрезке и без «пинг-понга» A→B→A на трёх соседних шагах, ширина корня идёт за шириной окна),
/// смена вкладки туда-обратно, произвольное «туда-обратно» (масштаб 100→90→100), модалка над окном, серия сворачиваний. Провалы копятся в <see cref="Failures"/> с понятным
/// текстом (экран, сценарий, шаг, до/после, график ширины по шагам) и снимками до/после: %TEMP%\nurmarket-tests\ui-actual\layout-stability-*.png.
/// Время в Headless детерминировано: между шагами — явный прогон очереди и кадра (UiShot.Pump), таймеры — коротким циклом диспетчера (<see cref="RunLoopFor"/>), не Thread.Sleep.</summary>
public sealed class LayoutStabilityProbe
{
    public const double Tolerance = 2.0;

    private static readonly Regex ModeField = new("^_(compact|narrow|wide|phone|tight|dense|small|mini|stacked|twoRow)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Assembly AppAssembly = typeof(NurMarketKassa.AvaloniaHost.App).Assembly;
    private static readonly Dictionary<Type, FieldInfo[]> ModeFieldsCache = new();

    private readonly Func<Window> _window;

    public string Screen { get; }
    public List<string> Failures { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>Сколько сценариев прогнано (для отчёта).</summary>
    public int Scenarios { get; private set; }

    public LayoutStabilityProbe(string screen, Window window) : this(screen, () => window) { }

    /// <summary>Окно можно отдавать функцией: у раздела владельца после масштаба/сворачивания окно то же, но так проще передать «текущее».</summary>
    public LayoutStabilityProbe(string screen, Func<Window> window)
    {
        Screen = screen;
        _window = window;
    }

    public Window Window => _window();

    // ------------------------------------------------------------------ отпечаток

    public LayoutFingerprint Capture()
    {
        var window = Window;
        var fp = new LayoutFingerprint { ClientWidth = window.ClientSize.Width, ClientHeight = window.ClientSize.Height };
        if (window.Content is not Control root)
            return fp;
        fp.RootWidth = root.Bounds.Width;
        fp.InnerWidth = root is LayoutTransformControl { Child: Control child } ? child.Bounds.Width : root.Bounds.Width;
        AddModes(window, "window", fp);
        Walk(window, root, "root", fp, 0);
        return fp;
    }

    private static bool IsShown(Control c) => c.IsVisible && c.Bounds.Width >= 0.5 && c.Bounds.Height >= 0.5;

    private static LayoutBox? BoxOf(Control c, Visual window)
    {
        var p1 = c.TranslatePoint(default, window);
        var p2 = c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), window);
        if (p1 is not { } a || p2 is not { } b)
            return null;
        return new LayoutBox(Math.Round(a.X, 1), Math.Round(a.Y, 1), Math.Round(b.X - a.X, 1), Math.Round(b.Y - a.Y, 1));
    }

    private static void AddModes(Control c, string key, LayoutFingerprint fp)
    {
        var type = c.GetType();
        if (type.Assembly != AppAssembly)
            return;
        if (!ModeFieldsCache.TryGetValue(type, out var fields))
        {
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t.Assembly == AppAssembly; t = t.BaseType)
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f.FieldType == typeof(bool) && ModeField.IsMatch(f.Name)));
            ModeFieldsCache[type] = fields = list.ToArray();
        }
        foreach (var f in fields)
            fp.Modes[$"{key}/{type.Name}.{f.Name}"] = f.GetValue(c) is true ? "True" : "False";
        // 2026-11-12 (аудит Т-20): режим «широкий/компактный» панели «Приём» (ReceivingPackBar.IsWide) лежит в обычном свойстве объекта-помощника, а не в bool-поле элемента, —
        // проба его не видела. Теперь собираются и такие свойства у объектов, на которые элемент ссылается своими полями.
        if (!ModeHelperCache.TryGetValue(type, out var helpers))
        {
            var list = new List<(FieldInfo Field, PropertyInfo Prop)>();
            for (var t = type; t != null && t.Assembly == AppAssembly; t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.FieldType.IsClass || f.FieldType == typeof(string) || f.FieldType.Assembly != AppAssembly || typeof(Visual).IsAssignableFrom(f.FieldType))
                        continue;
                    foreach (var p in f.FieldType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetMethod != null && ModeProperty.IsMatch(p.Name))
                            list.Add((f, p));
                }
            ModeHelperCache[type] = helpers = list.ToArray();
        }
        foreach (var (f, p) in helpers)
            if (f.GetValue(c) is { } helper)
                fp.Modes[$"{key}/{type.Name}.{f.Name}.{p.Name}"] = p.GetValue(helper) is true ? "True" : "False";
    }

    private static readonly Regex ModeProperty = new("^Is(Compact|Narrow|Wide|Phone|Tight|Dense|Small|Mini|Stacked|TwoRow)$", RegexOptions.CultureInvariant);
    private static readonly Dictionary<Type, (FieldInfo Field, PropertyInfo Prop)[]> ModeHelperCache = new();

    private static string Segment(Visual v, int index) =>
        v is Control { Name: { Length: > 0 } name } ? $"{v.GetType().Name}#{name}" : $"{v.GetType().Name}[{index}]";

    private void Walk(Window window, Control c, string key, LayoutFingerprint fp, int depth)
    {
        if (!IsShown(c) || depth > 60)
            return;
        // тексты — не отпечаток (часы, счётчики меняются законно); их смена видна по размерам родителя
        if (c is TextBlock or TextPresenter)
            return;
        if (BoxOf(c, window) is { } box)
            fp.Boxes[key] = box;
        AddModes(c, key, fp);

        switch (c)
        {
            case ScrollViewer sv:
                fp.ScrollViewers.Add(sv);
                fp.Boxes[key + "/viewport"] = new LayoutBox(0, 0, Math.Round(sv.Viewport.Width, 1), Math.Round(sv.Viewport.Height, 1));
                if (sv.Content is Control content && sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
                    fp.Boxes[key + "/content"] = new LayoutBox(0, 0, Math.Round(content.Bounds.Width, 1), 0);
                if (sv.Content is Control content2)
                {
                    AddColumns(content2, key, fp, 0);
                    // 2026-11-12 (В-14): положения внутри прокрутки — относительно содержимого (при прокрутке они не меняются)
                    var budget = InnerBudget;
                    WalkInner(content2, content2, key + "/in", fp, 0, ref budget);
                }
                return; // положения внутри прокрутки в окне законно меняются — они сняты относительно содержимого (Inner)
            case DataGrid grid:
                fp.DataGrids.Add(grid);
                AddGridColumns(grid, key, fp);
                return;
            case TabControl tabs:
                fp.TabControls.Add(tabs);
                break;
            case TextBox or ComboBox or NumericUpDown or DatePicker or CalendarDatePicker or ProgressBar or Slider:
                return;
        }

        var i = 0;
        foreach (var child in c.GetVisualChildren())
        {
            if (child is Control cc)
                Walk(window, cc, key + "/" + Segment(cc, i), fp, depth + 1);
            i++;
        }
    }

    /// <summary>Сколько элементов внутри одной прокрутки снимается в отпечаток (экран с тысячами элементов не должен замедлять пробу).</summary>
    private const int InnerBudget = 400;

    /// <summary>2026-11-12 (аудит Fable, В-14): элементы внутри прокрутки — прямоугольник относительно <paramref name="root"/> (содержимого прокрутки). Тексты, поля ввода и
    /// виртуальные панели (их дети создаются на лету) — без детей; вложенная прокрутка — со своим содержимым.</summary>
    private static void WalkInner(Control root, Control c, string key, LayoutFingerprint fp, int depth, ref int budget)
    {
        if (budget <= 0 || depth > 14 || !IsShown(c))
            return;
        if (c is TextBlock or TextPresenter)
            return;
        budget--;
        var p1 = c.TranslatePoint(default, root);
        var p2 = c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), root);
        if (p1 is { } a && p2 is { } b)
            fp.Inner[key] = ReferenceEquals(root, c)
                ? new LayoutBox(0, 0, Math.Round(c.Bounds.Width, 1), 0) // сам корень содержимого: его высота растёт с загрузкой, ширина — нет
                : new LayoutBox(Math.Round(a.X, 1), Math.Round(a.Y, 1), Math.Round(b.X - a.X, 1), Math.Round(b.Y - a.Y, 1));
        switch (c)
        {
            case VirtualizingPanel:
                if (p1 is { } va && p2 is { } vb)
                    fp.Inner[key] = new LayoutBox(Math.Round(va.X, 1), 0, Math.Round(vb.X - va.X, 1), 0); // ширина панели — да, высота и дети — нет
                return;
            case ScrollViewer nested when !ReferenceEquals(nested, root):
                fp.Inner[key + "/viewport"] = new LayoutBox(0, 0, Math.Round(nested.Viewport.Width, 1), Math.Round(nested.Viewport.Height, 1));
                if (nested.Content is Control nc)
                    WalkInner(nc, nc, key + "/in", fp, depth + 1, ref budget);
                return;
            case DataGrid grid:
                AddGridColumns(grid, key, fp);
                return;
            case TextBox or ComboBox or NumericUpDown or DatePicker or CalendarDatePicker or ProgressBar or Slider:
                return;
        }
        var i = 0;
        foreach (var child in c.GetVisualChildren())
        {
            if (child is Control cc)
                WalkInner(root, cc, key + "/" + Segment(cc, i), fp, depth + 1, ref budget);
            i++;
        }
    }

    /// <summary>2026-11-12 (В-14): ширины колонок таблицы — пульсация ширины колонок при прокрутке строк раньше не ловилась (таблица в отпечатке была одним прямоугольником).</summary>
    private static void AddGridColumns(DataGrid grid, string key, LayoutFingerprint fp)
    {
        var i = 0;
        foreach (var col in grid.Columns)
        {
            if (col.IsVisible)
                fp.Inner[$"{key}/col{i}"] = new LayoutBox(0, 0, Math.Round(col.ActualWidth, 1), 0);
            i++;
        }
    }

    /// <summary>Число колонок плиток в прокрутке: WrapPanel — по числу разных X у видимых детей, UniformGrid — по свойству.</summary>
    private static void AddColumns(Control c, string key, LayoutFingerprint fp, int depth)
    {
        if (depth > 4 || !c.IsVisible)
            return;
        switch (c)
        {
            case WrapPanel wrap:
            {
                var xs = wrap.Children.Where(ch => ch.IsVisible && ch.Bounds.Width > 0).Take(80).Select(ch => Math.Round(ch.Bounds.X)).Distinct().Count();
                fp.Modes[$"{key}/cols#{depth}"] = xs.ToString(CultureInfo.InvariantCulture);
                return;
            }
            case UniformGrid ug:
                fp.Modes[$"{key}/ugcols#{depth}"] = ug.Columns.ToString(CultureInfo.InvariantCulture);
                return;
        }
        foreach (var child in c.GetVisualChildren().OfType<Control>())
            AddColumns(child, key, fp, depth + 1);
    }

    public static List<LayoutDiff> Diff(LayoutFingerprint a, LayoutFingerprint b, double tol = Tolerance)
    {
        var diffs = new List<LayoutDiff>();
        if (Math.Abs(a.ClientWidth - b.ClientWidth) > tol || Math.Abs(a.ClientHeight - b.ClientHeight) > tol)
            diffs.Add(new("окно", $"{a.ClientWidth:0.#}x{a.ClientHeight:0.#}", $"{b.ClientWidth:0.#}x{b.ClientHeight:0.#}", 1e9));
        foreach (var (key, ba) in a.Boxes)
        {
            if (!b.Boxes.TryGetValue(key, out var bb))
            {
                diffs.Add(new(key, ba.ToString(), "пропал", ba.W * Math.Max(1, ba.H)));
                continue;
            }
            if (Math.Abs(ba.X - bb.X) > tol || Math.Abs(ba.Y - bb.Y) > tol || Math.Abs(ba.W - bb.W) > tol || Math.Abs(ba.H - bb.H) > tol)
                diffs.Add(new(key, ba.ToString(), bb.ToString(), Math.Max(ba.W, bb.W) * Math.Max(1, Math.Max(ba.H, bb.H))));
        }
        foreach (var (key, bb) in b.Boxes)
            if (!a.Boxes.ContainsKey(key))
                diffs.Add(new(key, "нет", bb.ToString(), bb.W * Math.Max(1, bb.H)));
        // 2026-11-12 (В-14): внутри прокрутки — только общие ключи
        foreach (var (key, ia) in a.Inner)
        {
            if (!b.Inner.TryGetValue(key, out var ib))
                continue;
            if (Math.Abs(ia.X - ib.X) > tol || Math.Abs(ia.Y - ib.Y) > tol || Math.Abs(ia.W - ib.W) > tol || Math.Abs(ia.H - ib.H) > tol)
                diffs.Add(new("в прокрутке " + key, ia.ToString(), ib.ToString(), Math.Max(ia.W, ib.W) * Math.Max(1, Math.Max(ia.H, ib.H)) / 4));
        }
        foreach (var (key, ma) in a.Modes)
        {
            var mb = b.Modes.TryGetValue(key, out var v) ? v : "нет";
            if (ma != mb)
                diffs.Add(new("режим " + key, ma, mb, 1e8));
        }
        foreach (var (key, mb) in b.Modes)
            if (!a.Modes.ContainsKey(key))
                diffs.Add(new("режим " + key, "нет", mb, 1e8));
        return diffs.OrderByDescending(d => d.Weight).ToList();
    }

    /// <summary>Хвост пути элемента (последние 3 звена) — чтобы сообщение читалось.</summary>
    public static string ShortKey(string key)
    {
        var parts = key.Split('/');
        return parts.Length <= 4 ? key : "…/" + string.Join("/", parts[^3..]);
    }

    // ------------------------------------------------------------------ прогон очереди

    /// <summary>Один шаг: очередь интерфейса + кадр, дважды (раскладка после SizeChanged/Post успевает примениться).</summary>
    public static void Step(int pumps = 2)
    {
        for (var i = 0; i < pumps; i++)
            Pump();
    }

    /// <summary>Петля «SizeChanged → Post → раскладка → SizeChanged…» не даёт очереди опустеть, и RunJobs не возвращается — тест висел бы до таймаута test-safe.
    /// Сторож: если прогон очереди длится дольше <see cref="PumpWatchdogMs"/>, в очередь с наивысшим приоритетом кладётся задание, которое бросает это исключение.</summary>
    public sealed class LayoutLoopException(string message) : Exception(message);

    /// <summary>2026-11-12 (аудит Т-21): было константой 10 000 — тест сторожа ждал 10 с настоящего времени. Теперь настраивается (тест сторожа ставит 500 мс и возвращает).</summary>
    public static int PumpWatchdogMs { get; set; } = 10000;

    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "layout-stability-trace.txt");

    // 2026-11-12 (аудит Т-21): след копится в памяти и пишется на диск пачкой — раз в секунду фоновым таймером, при провале/окончании экрана (FlushTrace) и при выходе
    // (раньше — отдельная запись в файл на каждый шаг; зависший прогон по-прежнему оставляет след, не старше секунды).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> TraceBuffer = new();
    private static readonly object TraceFlushLock = new();
    private static readonly Timer TraceTimer = new(_ => FlushTrace(), null, 1000, 1000);

    static LayoutStabilityProbe()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushTrace();
    }

    /// <summary>След прогона (какой экран/сценарий/шаг идёт) — чтобы по зависшему прогону было видно место.</summary>
    public static void Trace(string text)
    {
        GC.KeepAlive(TraceTimer);
        TraceBuffer.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
    }

    /// <summary>Записать накопленный след в файл.</summary>
    public static void FlushTrace()
    {
        if (TraceBuffer.IsEmpty)
            return;
        lock (TraceFlushLock)
        {
            try
            {
                var sb = new StringBuilder();
                while (TraceBuffer.TryDequeue(out var line))
                    sb.Append(line);
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
                File.AppendAllText(TracePath, sb.ToString());
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>UiShot.Pump со сторожем бесконечной петли очереди.</summary>
    public static void Pump()
    {
        var active = new StrongBox<bool>(true);
        using var watchdog = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (active.Value)
                FlushTrace();
            if (active.Value)
                throw new LayoutLoopException($"очередь интерфейса не опустела за {PumpWatchdogMs / 1000} с — бесконечная петля «SizeChanged → Post → раскладка» (пульсация без конца)");
        }, DispatcherPriority.Send), null, PumpWatchdogMs, Timeout.Infinite);
        try
        {
            UiShot.Pump();
        }
        finally
        {
            active.Value = false;
        }
    }

    /// <summary>Пауза для фоновых загрузок (заглушка сервера отвечает из пула потоков) + прогон очереди со сторожем.</summary>
    public static void Settle(int rounds, int pauseMs = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Thread.Sleep(pauseMs);
            Pump();
        }
    }

    /// <summary>Цикл диспетчера на <paramref name="ms"/> мс: в Headless таймеры DispatcherTimer (отложенные пересчёты, WindowRestoreGuard) срабатывают только внутри цикла.</summary>
    public static void RunLoopFor(int ms)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ms + 2000)); // страховка
        using var stop = DispatcherTimer.RunOnce(() => cts.Cancel(), TimeSpan.FromMilliseconds(ms));
        Dispatcher.UIThread.MainLoop(cts.Token);
        Pump();
    }

    /// <summary>Ждёт, пока фоновые загрузки окна (заглушка сервера «нет связи») закончатся и отпечаток перестанет меняться (два одинаковых подряд).
    /// Если окно меняется само без действий — это уже провал «покой» (петля раскладки).</summary>
    public LayoutFingerprint WaitStable(string scenario, int maxRounds = 30)
    {
        Trace($"[{Screen}] {scenario}: ждём покоя");
        var a = Capture();
        var same = 0;
        List<LayoutDiff> last = new();
        for (var i = 0; i < maxRounds; i++)
        {
            Settle(1, 20);
            var b = Capture();
            last = Diff(a, b);
            same = last.Count == 0 ? same + 1 : 0;
            a = b;
            if (same >= 2)
                return b;
        }
        Fail(scenario, "покой (окно меняется само, без действий)", maxRounds, last, null, null);
        return a;
    }

    // ------------------------------------------------------------------ снимки

    private static readonly string ShotDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");

    private byte[]? Frame()
    {
        try
        {
            using var frame = Window.CaptureRenderedFrame();
            if (frame is null)
                return null;
            using var ms = new MemoryStream();
            frame.Save(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null; // снимок — для глаз; его отсутствие не ломает проверку
        }
    }

    private static string Slug(string s) =>
        Regex.Replace(s, @"[^\p{L}\p{Nd}]+", "-").Trim('-') is { Length: > 0 } t ? (t.Length > 80 ? t[..80] : t) : "x";

    private (string Before, string After) SaveShots(string scenario, byte[]? before)
    {
        Directory.CreateDirectory(ShotDir);
        var name = $"layout-stability-{Slug(Screen)}-{Slug(scenario)}";
        var pb = Path.Combine(ShotDir, name + "-before.png");
        var pa = Path.Combine(ShotDir, name + "-after.png");
        if (before != null)
            File.WriteAllBytes(pb, before);
        if (Frame() is { } after)
            File.WriteAllBytes(pa, after);
        return (pb, pa);
    }

    private void Fail(string scenario, string what, int step, List<LayoutDiff> diffs, IReadOnlyList<string>? series, byte[]? before)
    {
        var sb = new StringBuilder();
        sb.Append($"[{Screen}] сценарий «{scenario}», шаг {step}: {what} (допуск ±{Tolerance:0} px).");
        foreach (var d in diffs.Take(12))
            sb.Append("\n    ").Append(d);
        if (diffs.Count > 12)
            sb.Append($"\n    … и ещё {diffs.Count - 12}");
        if (series is { Count: > 0 })
            sb.Append("\n    график по шагам (корень / внутри обёртки / цель / колонки): ").Append(string.Join("  ", series));
        var (pb, pa) = SaveShots(scenario, before);
        sb.Append($"\n    снимки: {(before != null ? pb + ", " : "")}{pa}");
        Failures.Add(sb.ToString());
        FlushTrace();
    }

    // ------------------------------------------------------------------ сценарии

    /// <summary>Повтор от нового исходного: пульсация повторяется при каждом проходе (тест красный), а «доехавшая» один раз раскладка (поздняя загрузка заглушки,
    /// залежавшийся расчёт, который исправил первый же пересчёт) при повторе уже не меняется — её пишем в <see cref="Notes"/> со снимками, без провала.</summary>
    /// <summary>2026-11-12 (аудит Fable, В-13): «доехала» — это ПРОВАЛ. Раньше провал первого прохода, не повторившийся на втором, уходил в заметки и тест был зелёным: разовый прыжок
    /// раскладки (поздняя загрузка, залежавшийся расчёт) прощался. Теперь он красный, кроме явного списка допущенных сценариев <see cref="AllowedLateSettle"/>
    /// (подстрока названия сценария/экрана; каждое допущение — в заметках с пометкой «ДОПУЩЕНО» и причиной у владельца списка).</summary>
    public HashSet<string> AllowedLateSettle { get; } = new(StringComparer.Ordinal);

    private void WithRetry(Action attempt)
    {
        var start = Failures.Count;
        attempt();
        if (Failures.Count == start)
            return;
        var first = Failures.GetRange(start, Failures.Count - start);
        Failures.RemoveRange(start, first.Count);
        attempt();
        if (Failures.Count == start)
        {
            foreach (var f in first)
            {
                if (AllowedLateSettle.Any(a => f.Contains(a, StringComparison.Ordinal)))
                    Notes.Add("ДОПУЩЕНО (список AllowedLateSettle): раскладка «доехала» после действия, при повторе стабильна: " + f);
                else
                    Failures.Add("РАСКЛАДКА «ДОЕХАЛА» (первый проход провален, второй прошёл — разовый прыжок раскладки; проверьте исходное состояние или внесите в AllowedLateSettle с причиной): " + f);
            }
            return;
        }
        Failures.InsertRange(start, first.Select(f => "(первый проход) " + f));
    }

    /// <summary>Покой: отпечаток не меняется за 3 прогона очереди без действий (петля раскладки «сама с собой»).</summary>
    public void Settled(string scenario = "покой")
    {
        Scenarios++;
        var a = WaitStable(scenario);
        Step(3);
        var b = Capture();
        var d = Diff(a, b);
        if (d.Count > 0)
            Fail(scenario, "окно меняется само, без действий", 1, d, null, null);
    }

    /// <summary>(а) Прокрутка: до трёх самых больших прокручиваемых областей (ScrollViewer, DataGrid) — шагами вниз и вверх (колесо мыши по центру области, если оно до неё
    /// доходит; иначе смещение Offset), после каждого шага отпечаток равен исходному; после остановки (таймеры отложенных пересчётов) — тоже.</summary>
    /// <summary>2026-11-12 (аудит Fable, В-14): сколько областей реально сдвинулось при прокрутке за все вызовы <see cref="Scroll"/> (раньше «прокручивать нечего» засчитывалось зелёным
    /// сценарием) и сколько раз прокручивать было нечего. <paramref name="mustMove"/> — экран обязан иметь что прокручивать: «нечего» и «ничего не сдвинулось» — провал.</summary>
    public int ScrolledAreas { get; private set; }
    public int NothingToScroll { get; private set; }

    public void Scroll(string scenario = "прокрутка", int steps = 12, bool mustMove = false)
    {
        Scenarios++;
        var movedBefore = ScrolledAreas;
        WithRetry(() => ScrollCore(scenario, steps));
        if (mustMove && ScrolledAreas == movedBefore)
            Failures.Add($"[{Screen}] сценарий «{scenario}»: прокрутка ничего не сдвинула (прокручивать нечего или область не отзывается) — сценарий не проверен.");
    }

    private void ScrollCore(string scenario, int steps)
    {
        var baseFp = WaitStable(scenario);
        var window = Window;
        var targets = new List<Control>();
        targets.AddRange(baseFp.ScrollViewers.Where(sv => sv.Viewport.Height > 40 && sv.Extent.Height > sv.Viewport.Height + 4));
        targets.AddRange(baseFp.DataGrids.Where(g => g.Bounds.Height > 60 && VerticalBar(g) is { Maximum: > 1 }));
        targets = targets.OrderByDescending(t => t.Bounds.Width * t.Bounds.Height).Take(3).ToList();
        if (targets.Count == 0)
        {
            NothingToScroll++;
            Notes.Add($"[{Screen}] «{scenario}»: прокручивать нечего (содержимое помещается).");
            return;
        }
        var before = Frame();
        foreach (var target in targets)
        {
            var name = target is ScrollViewer ? "ScrollViewer" : "DataGrid";
            if (!string.IsNullOrEmpty(target.Name))
                name += "#" + target.Name;
            var series = new List<string>();
            string Point(LayoutFingerprint f) => string.Create(CultureInfo.InvariantCulture,
                $"{series.Count}:{f.RootWidth:0.#}/{f.InnerWidth:0.#}/{target.Bounds.Width:0.#}/{(f.ColsText.Length > 0 ? f.ColsText : "-")}");
            series.Add(Point(baseFp));
            var failed = false;
            var offsetStart = OffsetOf(target);
            var movedAway = false;
            for (var i = 0; i < steps * 2 && !failed; i++)
            {
                var down = i < steps;
                Trace($"[{Screen}] {scenario}: {name} шаг {i + 1}");
                var how = ScrollOnce(window, target, down);
                if (Math.Abs(OffsetOf(target) - offsetStart) > 0.5)
                    movedAway = true;
                Step();
                var fp = Capture();
                series.Add(Point(fp));
                var d = Diff(baseFp, fp);
                if (d.Count > 0)
                {
                    Fail($"{scenario}: {name}", $"раскладка изменилась при прокрутке ({how} {(down ? "вниз" : "вверх")})", i + 1, d, series, before);
                    failed = true;
                }
            }
            if (movedAway)
                ScrolledAreas++;
            if (failed)
                continue;
            RunLoopFor(150); // отложенные пересчёты (DispatcherTimer) после остановки колеса
            var after = Capture();
            series.Add(Point(after));
            var d2 = Diff(baseFp, after);
            if (d2.Count > 0)
                Fail($"{scenario}: {name}", "раскладка изменилась после остановки прокрутки", steps * 2 + 1, d2, series, before);
        }
    }

    /// <summary>Текущее положение прокрутки области (ScrollViewer — Offset.Y, таблица — значение вертикальной полосы).</summary>
    private static double OffsetOf(Control target) =>
        target is ScrollViewer sv ? sv.Offset.Y : target is DataGrid g ? VerticalBar(g)?.Value ?? 0 : 0;

    private static ScrollBar? VerticalBar(DataGrid g) =>
        g.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible);

    /// <summary>Один шаг прокрутки: колесо по центру области, если элемент под курсором принадлежит этой области и это не поле ввода/список выбора; иначе Offset.</summary>
    private static string ScrollOnce(Window window, Control target, bool down)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
        if (center is { } c && window.InputHitTest(c) is Visual hit && WheelReaches(hit, target))
        {
            var sv = target as ScrollViewer;
            var y0 = sv?.Offset.Y ?? VerticalBar((DataGrid)target)?.Value ?? 0;
            window.MouseWheel(c, new Vector(0, down ? -1 : 1));
            Dispatcher.UIThread.RunJobs();
            var y1 = sv?.Offset.Y ?? VerticalBar((DataGrid)target)?.Value ?? 0;
            if (Math.Abs(y1 - y0) > 0.1 || sv is null)
                return "колесо";
        }
        if (target is ScrollViewer s)
        {
            var step = Math.Max(40, s.Viewport.Height / 5);
            var max = Math.Max(0, s.Extent.Height - s.Viewport.Height);
            s.Offset = new Vector(s.Offset.X, Math.Clamp(s.Offset.Y + (down ? step : -step), 0, max));
            return "смещение";
        }
        return "колесо (мимо)";
    }

    private static bool WheelReaches(Visual hit, Control target)
    {
        for (Visual? v = hit; v != null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, target))
                return true;
            if (v is TextBox or ComboBox or NumericUpDown or Slider or DatePicker or CalendarDatePicker)
                return false;
            if (v is ScrollViewer or DataGrid)
                return false; // колесо заберёт вложенная прокрутка
        }
        return false;
    }

    /// <summary>(б) Плавный ресайз: ширина <paramref name="setWidth"/> от <paramref name="from"/> до <paramref name="to"/> и обратно шагом <paramref name="step"/>.
    /// <paramref name="driver"/> — фактическая ширина окна (у раздела владельца — окно раздела, она может прыгнуть, когда сворачивается меню). Проверки на каждом шаге:
    /// окно успокаивается (отпечаток не меняется за лишний прогон); по проходу: режимы (bool) — не больше 2 переключений на монотонном отрезке и без «пинг-понга» A→B→A
    /// на трёх соседних шагах; колонки и ширина корня не идут против ширины окна.</summary>
    // 2026-11-12 (аудит Т-20): шаг 30 → 10 px — режим, переключающийся в узком окне порога шириной меньше 30 px (панель «Приём»: гистерезис), раньше мог проскочить между шагами.
    public void Resize(string scenario, Action<double> setWidth, Func<double> driver, double from = 1000, double to = 1900, double step = 10)
    {
        Scenarios++;
        WaitStable(scenario);
        var before = Frame();
        var up = new List<double>();
        for (var x = from; x <= to + 0.1; x += step)
            up.Add(x);
        var passes = new[] { ("вверх", up), ("вниз", Enumerable.Reverse(up).Skip(1).Append(from).ToList()) };
        var settleFailed = false;
        foreach (var (passName, widths) in passes)
        {
            var drivers = new List<double>();
            var roots = new List<double>();
            var modes = new List<Dictionary<string, string>>();
            foreach (var w in widths)
            {
                Trace($"[{Screen}] {scenario}: ширина {w}");
                setWidth(w);
                Step();
                var f1 = Capture();
                Step(1);
                var f2 = Capture();
                if (!settleFailed && Diff(f1, f2) is { Count: > 0 } d)
                {
                    settleFailed = true;
                    Fail($"{scenario} {passName}", $"после ресайза до {w:0} окно не успокаивается (меняется на следующем прогоне)", drivers.Count + 1, d, null, before);
                }
                drivers.Add(driver());
                roots.Add(f2.InnerWidth);
                modes.Add(new Dictionary<string, string>(f2.Modes));
            }
            var series = drivers.Select((dw, i) => string.Create(CultureInfo.InvariantCulture, $"{i}:{dw:0}/{roots[i]:0.#}")).ToList();
            CheckRootFollows(scenario, passName, drivers, roots, series, before);
            CheckModes(scenario, passName, drivers, modes, series, before);
        }
    }

    private static int Dir(double a, double b) => b > a + 0.5 ? 1 : b < a - 0.5 ? -1 : 0;

    private void CheckRootFollows(string scenario, string pass, List<double> drivers, List<double> roots, List<string> series, byte[]? before)
    {
        for (var i = 1; i < drivers.Count; i++)
        {
            var dd = Dir(drivers[i - 1], drivers[i]);
            var dr = roots[i] - roots[i - 1];
            if ((dd >= 0 && dr < -Tolerance && dd != 0) || (dd <= 0 && dr > Tolerance && dd != 0) || (dd == 0 && Math.Abs(dr) > Tolerance))
            {
                Fail($"{scenario} {pass}", $"ширина рабочей области пошла против ширины окна: окно {drivers[i - 1]:0}→{drivers[i]:0}, область {roots[i - 1]:0.#}→{roots[i]:0.#}",
                    i + 1, new List<LayoutDiff>(), series, before);
                return;
            }
        }
    }

    private void CheckModes(string scenario, string pass, List<double> drivers, List<Dictionary<string, string>> modes, List<string> series, byte[]? before)
    {
        var keys = modes.SelectMany(m => m.Keys).Distinct().ToList();
        foreach (var key in keys)
        {
            var vals = modes.Select(m => m.TryGetValue(key, out var v) ? v : "нет").ToList();
            var withVals = series.Select((s, i) => s + "=" + vals[i]).ToList();
            var numeric = vals.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
            // пинг-понг: A→B→A на трёх соседних шагах, пока ширина окна шла в одну сторону
            for (var i = 0; i + 2 < vals.Count; i++)
            {
                var d1 = Dir(drivers[i], drivers[i + 1]);
                var d2 = Dir(drivers[i + 1], drivers[i + 2]);
                if (vals[i] != vals[i + 1] && vals[i + 2] == vals[i] && d1 * d2 >= 0)
                {
                    Fail($"{scenario} {pass}", $"дребезг режима «{ShortKey(key)}»: {vals[i]}→{vals[i + 1]}→{vals[i + 2]} при ширине окна {drivers[i]:0}→{drivers[i + 1]:0}→{drivers[i + 2]:0}",
                        i + 1, new List<LayoutDiff>(), withVals, before);
                    return;
                }
            }
            if (numeric)
            {
                // колонки не идут против ширины окна
                for (var i = 1; i < vals.Count; i++)
                {
                    var dd = Dir(drivers[i - 1], drivers[i]);
                    var dv = int.Parse(vals[i], CultureInfo.InvariantCulture) - int.Parse(vals[i - 1], CultureInfo.InvariantCulture);
                    if (dv != 0 && (dd == 0 || Math.Sign(dv) != dd))
                    {
                        Fail($"{scenario} {pass}", $"колонки «{ShortKey(key)}» {vals[i - 1]}→{vals[i]} при ширине окна {drivers[i - 1]:0}→{drivers[i]:0}", i + 1, new List<LayoutDiff>(), withVals, before);
                        return;
                    }
                }
                continue;
            }
            // режим (bool): не больше 2 переключений на монотонном отрезке ширины окна
            var switches = 0;
            var segDir = 0;
            for (var i = 1; i < vals.Count; i++)
            {
                var dd = Dir(drivers[i - 1], drivers[i]);
                if (dd != 0 && segDir != 0 && dd != segDir)
                    switches = 0; // ширина окна повернула (свернулось меню владельца) — новый отрезок
                if (dd != 0)
                    segDir = dd;
                if (vals[i] != vals[i - 1] && ++switches > 2)
                {
                    Fail($"{scenario} {pass}", $"режим «{ShortKey(key)}» переключился больше 2 раз за монотонный проход", i + 1, new List<LayoutDiff>(), withVals, before);
                    return;
                }
            }
        }
    }

    /// <summary>(в), (г) «Туда-обратно»: действие и обратное ему (вкладка, масштаб интерфейса) — после возврата отпечаток тот же.</summary>
    public void RoundTrip(string scenario, Action go, Action back, int settleRounds = 3)
    {
        Scenarios++;
        WithRetry(() => RoundTripCore(scenario, go, back, settleRounds));
    }

    private void RoundTripCore(string scenario, Action go, Action back, int settleRounds)
    {
        var baseFp = WaitStable(scenario);
        var before = Frame();
        go();
        Settle(settleRounds, 20);
        back();
        Settle(settleRounds, 20);
        var after = WaitStable(scenario + " (после возврата)");
        var d = Diff(baseFp, after);
        if (d.Count > 0)
            Fail(scenario, "после возврата раскладка другая", 1, d, null, before);
    }

    /// <summary>(в) Вкладки: сначала прогрев (обойти все вкладки — ленивое содержимое и загрузки создаются один раз), потом каждая вкладка и назад — отпечаток исходной вкладки тот же.</summary>
    public void Tabs(string scenario = "вкладки")
    {
        var fp = WaitStable(scenario);
        foreach (var tabs in fp.TabControls.Take(2))
        {
            var items = tabs.Items.OfType<TabItem>().Where(t => t.IsVisible && t.IsEnabled).ToList();
            if (items.Count < 2 || tabs.SelectedItem is not TabItem home)
                continue;
            foreach (var t in items)
            {
                tabs.SelectedItem = t;
                Settle(2, 20);
            }
            tabs.SelectedItem = home;
            Settle(2, 20);
            foreach (var t in items.Where(t => !ReferenceEquals(t, home)))
                RoundTrip($"{scenario}: «{TabTitle(home)}» → «{TabTitle(t)}» → назад", () => tabs.SelectedItem = t, () => tabs.SelectedItem = home, 2);
        }
    }

    public static string TabTitle(TabItem t) =>
        t.Header as string ?? (t.Header is Control c ? UiDrive.TextOf(c) : null) ?? t.Name ?? "?";

    /// <summary>(д) Модалка над окном (подтверждение через PosDialogHost — с затемнением владельца): отпечаток окна под модалкой и после закрытия тот же.</summary>
    public void Modal(string scenario = "модалка")
    {
        Scenarios++;
        WithRetry(() => ModalCore(scenario));
    }

    private void ModalCore(string scenario)
    {
        var window = Window;
        var baseFp = WaitStable(scenario);
        var before = Frame();
        var dialog = new PosConfirmDialog(
            Tr.T("Проверка", "Текшерүү", "Check", "Kontrol", "Tekshiruv"),
            Tr.T("Окно поверх раздела", "Бөлүмдүн үстүндөгү терезе", "A window over the section", "Bölümün üstünde pencere", "Bo'lim ustidagi oyna"),
            Tr.T("Понятно", "Түшүнүктүү", "OK", "Tamam", "Tushunarli"), null);
        Task? task = null;
        try
        {
            Trace($"[{Screen}] {scenario}: показ модалки");
            task = PosDialogHost.ShowAsync(dialog, window);
            Step(4);
            var during = Capture();
            var d = Diff(baseFp, during);
            if (d.Count > 0)
                Fail(scenario, "раскладка окна изменилась под модалкой", 1, d, null, before);
        }
        finally
        {
            if (dialog.IsVisible)
                dialog.Close();
            Step(4);
        }
        var after = WaitStable(scenario + " (после закрытия)");
        var d2 = Diff(baseFp, after);
        if (d2.Count > 0)
            Fail(scenario, "раскладка окна изменилась после закрытия модалки", 2, d2, null, before);
        _ = task;
    }

    /// <summary>(е) Серия сворачиваний и разворачиваний окна <paramref name="toMinimize"/> (у владельца — оболочка), потом таймер WindowRestoreGuard (150 мс) — отпечаток тот же.</summary>
    public void Minimize(string scenario = "сворачивание", Window? toMinimize = null)
    {
        Scenarios++;
        WithRetry(() => MinimizeCore(scenario, toMinimize));
    }

    private void MinimizeCore(string scenario, Window? toMinimize)
    {
        var target = toMinimize ?? Window;
        var baseFp = WaitStable(scenario);
        var before = Frame();
        for (var i = 0; i < 3; i++)
        {
            target.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            target.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
        }
        Trace($"[{Screen}] {scenario}: цикл таймеров");
        RunLoopFor(400);
        Step(3);
        var after = WaitStable(scenario + " (после разворачивания)");
        var d = Diff(baseFp, after);
        if (d.Count > 0)
            Fail(scenario, "после сворачивания и разворачивания раскладка другая", 1, d, null, before);
    }

    public string Report() =>
        Failures.Count == 0 ? $"[{Screen}] сценариев {Scenarios}: без пульсации" : string.Join("\n\n", Failures);
}
