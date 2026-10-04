using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Общая логика применения масштаба интерфейса (Настройки → Экран → "Масштаб", 50–200%,
/// см. UserPreferences.UiScalePercent) — используется и MainWindow, и PosSettingsWindow (и
/// любым другим окном, которое захочет тот же масштаб позже), чтобы не дублировать одну и ту
/// же формулу clamp+ScaleTransform в каждом окне отдельно.
///
/// 2026-09-28, баг владельца «на квадратных экранах масштаб резко увеличивается на 160%».
/// Причин было три, и все — в этом файле или в том, когда его вызывали:
///  1. С 1.17.13 (коммит 095b677, «рабочий ползунок масштаба») значение выше 100%
///     применялось КАК ЕСТЬ, в обход проверки «влезает ли на экран». До этого такие значения
///     молча превращались в «подогнано под экран», поэтому на кассах 1024×768 ползунок
///     двигали на 120–200% («ничего не меняется») — и после обновления это значение разом
///     включилось: 130% при влезающих 78% = в 1,66 раза крупнее экрана.
///  2. Подгонка считалась один раз — в самом конце MainWindow.OnLoaded, уже после модального
///     «Касса обновлена» при первом запуске после установки. Всё это время касса стояла в
///     родных 100%: на 1024×768 с масштабом Windows 125% (819 DIP в ширину) влезает только
///     62%, 100 / 62 ≈ 1,6 — те самые «160%».
///  3. PosSettingsWindow звал Apply из конструктора, когда окна у корня ещё нет:
///     TopLevel.GetTopLevel = null → подгонка = 1.0, и окно настроек не подгонялось никогда.
/// Теперь: масштаб = min(выбор кассира, сколько влезает), в любую сторону; вызов до показа
/// окна откладывается до его появления; смена экрана или DPI пересчитывает масштаб.
/// </summary>
public static class UiScaleHelper
{
    /// <summary>Мельче — текст уже не читается даже вблизи; если макет не влезает и так,
    /// остаток доступен прокруткой, а не микроскопическим шрифтом.</summary>
    public const double MinScale = 0.5;

    /// <summary>Потолок ползунка (200%).</summary>
    public const double MaxScale = 2.0;

    /// <summary>Небольшой запас на непредсказуемую высоту заголовка окна/рамки/панели задач
    /// Windows, которую WorkingArea не всегда учитывает день в день одинаково.</summary>
    private const double SafetyMarginPx = 24;

    /// <summary>Что уже знаем о каждом корне масштаба: его родной размер и окно, на экран и
    /// DPI которого уже подписались (чтобы не подписываться повторно при каждом вызове).</summary>
    private sealed class RootState
    {
        public double DesignWidth;
        public double DesignHeight;
        public Window? Window;
        public bool WaitingForAttach;
        public string? LastLoggedScreen;
        /// <summary>2026-10-04: компактный вид (см. ApplyAdaptive); null — у окна его нет.</summary>
        public CompactLayoutPolicy? Compact;
        public bool? LastCompact;
    }

    /// <summary>2026-10-04, редизайн под маленькие экраны и сенсорные моноблоки. У окна есть второй,
    /// компактный вид с меньшим «родным» размером (CompactWidth × CompactHeight). Когда обычный вид не
    /// помещается на экран в выбранном масштабе, окно переходит на компактный — и масштаб считается уже
    /// от него: на 1024×768 касса остаётся в 100%, а не уменьшается до 78% вместе с кнопками.
    /// Changed вызывается при каждом переключении вида (и при первом применении).</summary>
    public sealed record CompactLayoutPolicy(double CompactWidth, double CompactHeight, Action<bool> Changed);

    /// <summary>Обычный вид считается непомещающимся, если подгонка урезала бы его меньше, чем до
    /// 97% выбранного масштаба: на 1600×900 (не хватает 4 точек по высоте) остаётся обычный вид.</summary>
    private const double CompactTolerance = 0.97;

    private static readonly ConditionalWeakTable<LayoutTransformControl, RootState> States = new();

    /// <param name="designWidth">Ширина, на которую реально рассчитан макет ЭТОГО конкретного
    /// окна (обычно то же число, что и XAML Width) — у разных окон разный "родной" размер
    /// (MainWindow ~1280×840, PosSettingsWindow ~1000×820), поэтому общей константы на все
    /// окна быть не может.</param>
    /// <param name="designHeight">См. designWidth.</param>
    /// <returns>Применённый масштаб (1.0 = 100%).</returns>
    public static double Apply(LayoutTransformControl transformRoot, double designWidth, double designHeight)
    {
        var state = States.GetValue(transformRoot, _ => new RootState());
        // Обычный вызов — окно без компактного вида (или касса переключилась на вид без него).
        state.Compact = null;
        state.LastCompact = null;
        return ApplyCore(transformRoot, state, designWidth, designHeight);
    }

    /// <summary>2026-10-04: как Apply, но у окна есть компактный вид (см. CompactLayoutPolicy):
    /// какой из двух видов показать, решается здесь же по экрану, выбранному масштабу и настройке
    /// UserPreferences.CompactLayoutMode, и пересчитывается вместе с масштабом при смене экрана/DPI.</summary>
    public static double ApplyAdaptive(
        LayoutTransformControl transformRoot,
        double designWidth,
        double designHeight,
        CompactLayoutPolicy compact)
    {
        var state = States.GetValue(transformRoot, _ => new RootState());
        state.Compact = compact;
        return ApplyCore(transformRoot, state, designWidth, designHeight);
    }

    /// <summary>Нужен ли компактный вид — чистая формула, без Avalonia. "on"/"off" — выбор владельца
    /// (Настройки → Экран), иначе ("auto") — только если обычный вид в выбранном масштабе не помещается
    /// на экран и его пришлось бы заметно уменьшать.</summary>
    public static bool ShouldUseCompactLayout(
        string? mode,
        double preferredPercent,
        double availableWidthDip,
        double availableHeightDip,
        double designWidth,
        double designHeight)
    {
        if (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return false;

        var fit = ComputeFitScale(availableWidthDip, availableHeightDip, designWidth, designHeight);
        if (fit is null)
            return false; // экран ещё неизвестен — решим, когда окно окажется на нём
        var preferred = Math.Clamp(double.IsFinite(preferredPercent) ? preferredPercent : 100, 50, 200) / 100.0;
        return fit.Value < preferred * CompactTolerance;
    }

    private static double ApplyCore(LayoutTransformControl transformRoot, RootState state, double designWidth, double designHeight)
    {
        state.DesignWidth = designWidth;
        state.DesignHeight = designHeight;

        var window = TopLevel.GetTopLevel(transformRoot) as Window;
        if (window is null)
            ReapplyWhenAttached(transformRoot, state);
        else
            WatchWindow(transformRoot, state, window);

        var screen = MeasureScreen(window);
        var availableWidth = screen.Width - SafetyMarginPx;
        var availableHeight = screen.Height - SafetyMarginPx;

        // 2026-10-04: компактный вид — масштаб считается от его (меньшего) родного размера.
        var fitWidth = designWidth;
        var fitHeight = designHeight;
        if (state.Compact is { } compactPolicy)
        {
            var useCompact = ShouldUseCompactLayout(
                UserPreferences.Instance.CompactLayoutMode,
                UserPreferences.Instance.UiScalePercent,
                availableWidth,
                availableHeight,
                designWidth,
                designHeight);
            if (useCompact)
            {
                fitWidth = compactPolicy.CompactWidth;
                fitHeight = compactPolicy.CompactHeight;
            }

            if (state.LastCompact != useCompact)
            {
                state.LastCompact = useCompact;
                try
                {
                    compactPolicy.Changed(useCompact);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Compact layout switch failed: {ex.Message}", "WARNING");
                }
            }
        }

        var scale = ComputeScale(
            UserPreferences.Instance.UiScalePercent,
            availableWidth,
            availableHeight,
            fitWidth,
            fitHeight);

        transformRoot.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(scale, scale);

        if (window is not null && screen.IsKnown)
        {
            FitMinSizeToScreen(window, screen.Width, screen.Height);
            LogScreenOnce(state, screen, fitWidth, fitHeight, scale);
        }

        return scale;
    }

    /// <summary>Чистая формула масштаба — без Avalonia, чтобы её можно было проверить таблицей
    /// экранов. Выбор кассира (50–200%) — это желаемый масштаб, но не больше, чем реально
    /// помещается: родной размер окна × масштаб не должен выходить за доступную область.
    /// На свежей установке (100%) на обычном мониторе это ровно 100%, на маленьком экране —
    /// уменьшение «под экран»; увеличение работает, пока макет влезает (Full HD — до ~120%,
    /// 2560×1440 — до ~160%).</summary>
    /// <param name="preferredPercent">UserPreferences.UiScalePercent.</param>
    /// <param name="availableWidthDip">Доступная ширина в DIP (рабочая область экрана, делённая
    /// на масштаб Windows, минус запас); NaN — экран неизвестен.</param>
    /// <param name="availableHeightDip">Доступная высота в DIP; NaN — экран неизвестен.</param>
    /// <param name="designWidth">Родная ширина макета окна.</param>
    /// <param name="designHeight">Родная высота макета окна.</param>
    public static double ComputeScale(
        double preferredPercent,
        double availableWidthDip,
        double availableHeightDip,
        double designWidth,
        double designHeight)
    {
        var preferred = Math.Clamp(double.IsFinite(preferredPercent) ? preferredPercent : 100, 50, 200) / 100.0;

        var fit = ComputeFitScale(availableWidthDip, availableHeightDip, designWidth, designHeight);

        // Экран не определился (окно ещё не показано, сбой платформы) — вслепую не увеличиваем:
        // крупнее 100% только тогда, когда точно известно, что это влезет. Как только окно
        // появится на экране, масштаб пересчитается (см. ReapplyWhenAttached/WatchWindow).
        if (fit is null)
            return Math.Min(preferred, 1.0);

        return Math.Clamp(Math.Min(preferred, fit.Value), MinScale, MaxScale);
    }

    /// <summary>Во сколько раз родной макет можно увеличить (&gt;1) или нужно уменьшить (&lt;1),
    /// чтобы он целиком поместился в доступную область. null — размеры неизвестны.</summary>
    public static double? ComputeFitScale(
        double availableWidthDip,
        double availableHeightDip,
        double designWidth,
        double designHeight)
    {
        if (!(designWidth > 0) || !(designHeight > 0)
            || !(availableWidthDip > 0) || !(availableHeightDip > 0)
            || !double.IsFinite(availableWidthDip) || !double.IsFinite(availableHeightDip))
            return null;

        return Math.Min(availableWidthDip / designWidth, availableHeightDip / designHeight);
    }

    /// <summary>Масштаб, который сейчас реально стоит на окне (в процентах): для страницы
    /// настроек — ползунок показывает выбор кассира, а это — что вышло на этом экране.
    /// Принимает само окно (ищет в нём корень "UiScaleTransform") или сам корень.</summary>
    public static double? GetAppliedPercent(Control? scope)
    {
        var root = scope as LayoutTransformControl
                   ?? scope?.FindControl<LayoutTransformControl>("UiScaleTransform");
        if (root is null)
            return null;

        return (root.LayoutTransform is ScaleTransform scale ? scale.ScaleX : 1.0) * 100;
    }

    private readonly record struct ScreenDip(double Width, double Height, double Scaling, int PixelWidth, int PixelHeight)
    {
        public static ScreenDip Unknown => new(double.NaN, double.NaN, double.NaN, 0, 0);

        public bool IsKnown => Width > 0 && Height > 0 && double.IsFinite(Width) && double.IsFinite(Height);
    }

    /// <summary>Рабочая область экрана ЭТОГО окна в DIP. WorkingArea — в физических пикселях,
    /// а окно меряется в DIP, поэтому делим на масштаб Windows (125%/150% и т.д.).</summary>
    private static ScreenDip MeasureScreen(Window? window)
    {
        if (window is null)
            return ScreenDip.Unknown;

        try
        {
            var screens = window.Screens;
            var screen = screens?.ScreenFromWindow(window) ?? screens?.Primary;
            if (screen is null)
                return ScreenDip.Unknown;

            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            return new ScreenDip(
                workArea.Width / scaling,
                workArea.Height / scaling,
                scaling,
                workArea.Width,
                workArea.Height);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UI auto-fit scale detection failed: {ex.GetType().Name}", "WARNING");
            return ScreenDip.Unknown;
        }
    }

    /// <summary>Минимальный размер окна из XAML (у кассы 850×520) больше экрана 800×600 или
    /// 1024×768 при масштабе Windows 125% (819×582 DIP) — тогда окно всё равно шире экрана, и
    /// правый край кассы уходит за него, какой бы масштаб ни стоял. Минимум опускаем до
    /// экрана — только вниз, на обычных мониторах он не меняется.</summary>
    private static void FitMinSizeToScreen(Window window, double screenWidthDip, double screenHeightDip)
    {
        if (window.MinWidth > screenWidthDip)
            window.MinWidth = Math.Floor(screenWidthDip);
        if (window.MinHeight > screenHeightDip)
            window.MinHeight = Math.Floor(screenHeightDip);
    }

    /// <summary>Вызов до показа окна (из конструктора): экрана у корня ещё нет, считать не
    /// из чего. Повторяем, как только корень окажется в окне.</summary>
    private static void ReapplyWhenAttached(LayoutTransformControl transformRoot, RootState state)
    {
        if (state.WaitingForAttach)
            return;

        state.WaitingForAttach = true;

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            transformRoot.AttachedToVisualTree -= OnAttached;
            state.WaitingForAttach = false;
            ApplyCore(transformRoot, state, state.DesignWidth, state.DesignHeight);
        }

        transformRoot.AttachedToVisualTree += OnAttached;
    }

    /// <summary>Масштаб зависит от экрана, поэтому пересчитывается, когда окно открылось,
    /// поменялся масштаб Windows или набор/разрешение мониторов — раньше посчитанный однажды
    /// масштаб оставался от другого экрана.</summary>
    private static void WatchWindow(LayoutTransformControl transformRoot, RootState state, Window window)
    {
        if (ReferenceEquals(state.Window, window))
            return;

        state.Window = window;

        void Reapply(object? sender, EventArgs e) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(state.Window, window))
                    ApplyCore(transformRoot, state, state.DesignWidth, state.DesignHeight);
            }, DispatcherPriority.Background);

        var screens = window.Screens;
        window.Opened += Reapply;
        window.ScalingChanged += Reapply;
        if (screens is not null)
            screens.Changed += Reapply;

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            window.Opened -= Reapply;
            window.ScalingChanged -= Reapply;
            if (screens is not null)
                screens.Changed -= Reapply;
            if (ReferenceEquals(state.Window, window))
                state.Window = null;
        }

        window.Closed += OnClosed;
    }

    /// <summary>Одна строка в журнал на каждый новый экран — чтобы по журналу с кассы было
    /// видно разрешение, масштаб Windows и итоговый масштаб, не спрашивая владельца.</summary>
    private static void LogScreenOnce(RootState state, ScreenDip screen, double designWidth, double designHeight, double scale)
    {
        // 2026-10-04: в ключе и в строке журнала — ещё и вид (обычный/компактный).
        var layout = state.LastCompact switch { true => " compact", false => " normal", _ => "" };
        var key = $"{screen.PixelWidth}x{screen.PixelHeight}@{screen.Scaling:0.##}{layout}";
        if (key == state.LastLoggedScreen)
            return;

        state.LastLoggedScreen = key;
        PosLogger.Log(
            $"UI scale: work area {screen.PixelWidth}x{screen.PixelHeight} px, Windows scale {screen.Scaling * 100:0}%, " +
            $"design {designWidth:0}x{designHeight:0}{layout}, preference {UserPreferences.Instance.UiScalePercent:0}% -> applied {scale * 100:0}%",
            "UI");
    }
}
