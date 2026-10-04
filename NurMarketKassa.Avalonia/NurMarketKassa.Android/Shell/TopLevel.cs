using Avalonia;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvTopLevel = Avalonia.Controls.TopLevel;

namespace NurMarketKassa;

// 2026-10-04, Android-касса. ВАЖНО: эти типы лежат в пространстве имён NurMarketKassa намеренно.
// C# ищет имя типа сначала в объемлющих пространствах имён (NurMarketKassa.AvaloniaHost.Views… →
// NurMarketKassa.AvaloniaHost → NurMarketKassa) и только потом в using-директивах. Поэтому в коде
// кассы «Window», «TopLevel», «Screens», «WindowClosingEventArgs» и «IClassicDesktopStyleApplicationLifetime»
// без полного имени в Android-сборке указывают сюда, а не в Avalonia — код окон не меняется.
// На Android у Avalonia одно окно (один TopLevel); «окна» кассы — слои внутри него (WindowLayerHost).

/// <summary>Замена Avalonia.Controls.TopLevel для кода кассы: «верхний уровень» здесь — окно-слой
/// (<see cref="Window"/>). Всё, что в Avalonia живёт у настоящего TopLevel (буфер обмена, файлы,
/// фокус, экраны), берётся у единственного настоящего TopLevel Android-вида.</summary>
public class TopLevel : ContentControl
{
    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>Как Avalonia.Controls.TopLevel.GetTopLevel: окно-слой, в котором лежит элемент.
    /// Для элементов во всплывающих панелях (Popup/Flyout) — окно по логическому дереву;
    /// для прочих присоединённых элементов — верхнее открытое окно.</summary>
    public static TopLevel? GetTopLevel(Visual? visual)
    {
        if (visual is null)
            return null;
        if (visual is TopLevel self)
            return self;

        foreach (var v in visual.GetVisualAncestors())
        {
            if (v is TopLevel tl)
                return tl;
        }

        if (visual is Avalonia.LogicalTree.ILogical logical)
        {
            foreach (var l in Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(logical))
            {
                if (l is TopLevel tl)
                    return tl;
            }
        }

        // Присоединён к виду, но не внутри окна (оверлей/всплывающая панель без логического
        // родителя) — отдаём верхнее окно: так код кассы получает владельца для диалога.
        return visual.GetVisualRoot() is not null ? WindowLayerHost.Current?.TopWindow : null;
    }

    /// <summary>Настоящий TopLevel Android-вида (один на программу).</summary>
    public static AvTopLevel? RealTopLevel =>
        WindowLayerHost.Current is { } host ? AvTopLevel.GetTopLevel(host) : null;

    public IClipboard? Clipboard => RealTopLevel?.Clipboard;
    public IStorageProvider StorageProvider => RealTopLevel?.StorageProvider ?? throw new InvalidOperationException("Нет вида Android.");
    public IFocusManager? FocusManager => RealTopLevel?.FocusManager;
    public IPlatformSettings? PlatformSettings => RealTopLevel?.PlatformSettings;
    public IInsetsManager? InsetsManager => RealTopLevel?.InsetsManager;
    public IInputPane? InputPane => RealTopLevel?.InputPane;
    public ILauncher Launcher => RealTopLevel?.Launcher ?? throw new InvalidOperationException("Нет вида Android.");

    private Screens? _screens;
    public Screens Screens => _screens ??= new Screens();

    public double RenderScaling => RealTopLevel?.RenderScaling ?? 1.0;
    public double DesktopScaling => RenderScaling;
    public Size ClientSize => Bounds.Size;
    public Size? FrameSize => Bounds.Size;
    public object? PlatformImpl => null;

    public void RequestAnimationFrame(Action<TimeSpan> action)
    {
        if (RealTopLevel is { } real)
            real.RequestAnimationFrame(action);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => action(TimeSpan.Zero));
    }

    public IPlatformHandle? TryGetPlatformHandle() => null;

    /// <summary>Прозрачность окна в Windows-кассе (TransparencyLevelHint="Transparent") — здесь
    /// окна и так рисуются поверх вида; значение хранится только ради совместимости кода.</summary>
    public static readonly StyledProperty<object?> TransparencyLevelHintProperty =
        AvaloniaProperty.Register<TopLevel, object?>(nameof(TransparencyLevelHint));

    public object? TransparencyLevelHint
    {
        get => GetValue(TransparencyLevelHintProperty);
        set => SetValue(TransparencyLevelHintProperty, value);
    }
    public WindowTransparencyLevel ActualTransparencyLevel => WindowTransparencyLevel.None;
    public IBrush? TransparencyBackgroundFallback { get; set; }

    /// <summary>Тема окна (светлая/тёмная) — как у Avalonia TopLevel (ThemeVariantScope).</summary>
    public static readonly StyledProperty<ThemeVariant?> RequestedThemeVariantProperty =
        ThemeVariantScope.RequestedThemeVariantProperty.AddOwner<TopLevel>();

    public ThemeVariant? RequestedThemeVariant
    {
        get => GetValue(RequestedThemeVariantProperty);
        set => SetValue(RequestedThemeVariantProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RequestedThemeVariantProperty)
        {
            if (change.GetNewValue<ThemeVariant?>() is { } variant && variant != ThemeVariant.Default)
                SetValue(ThemeVariantScope.ActualThemeVariantProperty, variant);
            else
                ClearValue(ThemeVariantScope.ActualThemeVariantProperty);
        }
    }

    /// <summary>Масштаб экрана изменился (на Android — плотность экрана).</summary>
    public event EventHandler? ScalingChanged
    {
        add { if (RealTopLevel is { } real) real.ScalingChanged += value; }
        remove { if (RealTopLevel is { } real) real.ScalingChanged -= value; }
    }

    public event EventHandler? Opened;
    public event EventHandler? Closed;

    protected virtual void OnOpened(EventArgs e) => Opened?.Invoke(this, e);
    protected virtual void OnClosed(EventArgs e) => Closed?.Invoke(this, e);

    internal void RaiseOpenedCore() => OnOpened(EventArgs.Empty);
    internal void RaiseClosedCore() => OnClosed(EventArgs.Empty);
}

/// <summary>Замена Avalonia.Controls.Screens: на Android экран один — сам вид программы.</summary>
public sealed class Screens
{
    public event EventHandler? Changed;

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static Avalonia.Controls.Screens? Real => TopLevel.RealTopLevel?.Screens;

    /// <summary>Второй дисплей аппарата (экран покупателя) — задаёт Android-часть.</summary>
    internal static Func<Screen?>? SecondaryScreenProvider { get; set; }

    private static Screen? Secondary()
    {
        try { return SecondaryScreenProvider?.Invoke(); }
        catch { return null; }
    }

    /// <summary>Экран из размеров вида (если Avalonia не знает экранов Android).</summary>
    private static Screen Synthesized()
    {
        var host = WindowLayerHost.Current;
        // 2026-10-04: вид кассы уменьшен под экран (ScreenFitHost) — точки вида × масштаб = пиксели экрана.
        var scaling = (TopLevel.RealTopLevel?.RenderScaling ?? 1.0) * (ScreenFitHost.Current?.Scale ?? 1.0);
        var w = host?.Bounds.Width > 0 ? host.Bounds.Width : 1280;
        var h = host?.Bounds.Height > 0 ? host.Bounds.Height : 800;
        var rect = new PixelRect(0, 0, (int)(w * scaling), (int)(h * scaling));
#pragma warning disable CS0618
        return new Screen(scaling, rect, rect, true);
#pragma warning restore CS0618
    }

    public Screen? Primary
    {
        get
        {
            // 2026-10-04: при подгонке под экран (ScreenFitHost) «экран» кассы — уменьшенный вид, а не
            // настоящий экран Android: иначе UiScaleHelper окна уменьшил бы его второй раз.
            if (ScreenFitHost.Current is null)
            {
                try { if (Real?.Primary is { } p) return p; } catch { /* нет экранов у платформы */ }
            }
            return Synthesized();
        }
    }

    public IReadOnlyList<Screen> All
    {
        get
        {
            var list = new List<Screen>();
            try
            {
                if (ScreenFitHost.Current is null && Real?.All is { Count: > 0 } all)
                    list.AddRange(all);
            }
            catch { /* нет экранов у платформы */ }
            if (list.Count == 0)
                list.Add(Synthesized());
            if (Secondary() is { } second && list.All(s => s.IsPrimary))
                list.Add(second);
            return list;
        }
    }

    public int ScreenCount => All.Count;
    public Screen? ScreenFromWindow(Window window) => Primary;
    public Screen? ScreenFromWindow(Avalonia.Controls.WindowBase window) => Primary;
    public Screen? ScreenFromTopLevel(TopLevel topLevel) => Primary;
    public Screen? ScreenFromVisual(Visual visual) => Primary;
    public Screen? ScreenFromPoint(PixelPoint point) => Primary;
    public Screen? ScreenFromBounds(PixelRect bounds) => Primary;
}
