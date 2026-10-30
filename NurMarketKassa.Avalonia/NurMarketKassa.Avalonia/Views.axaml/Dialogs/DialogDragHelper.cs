using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-30, баг владельца «модалка не двигается!!!»: окна без системной рамки (SystemDecorations=None — оплата, диалоги на
/// ModalShell, сообщения) нельзя было перетащить. Единая точка: нажатие левой кнопки на ШАПКЕ окна начинает перетаскивание
/// (Window.BeginMoveDrag). Шапка — элемент с <see cref="IsHandleProperty"/> (в шаблоне ModalShell), либо класс «pay-header» (оплата),
/// либо, если в окне своей шапки нет, верхние <see cref="FallbackHeaderHeight"/> точек. Кнопки (✕ и другие), поля ввода и любые
/// фокусируемые элементы перетаскивание не запускают. После отпускания окно возвращается в рабочую область своего экрана (шапка всегда
/// достижима, несколько мониторов), и вызывается необязательный обработчик (оплата запоминает положение).
/// Окна, которые уже двигаются сами (<see cref="SelfDragging"/>), не затрагиваются. На Android (слои WindowLayerHost) — пустышка.
/// Подключение: само — из ModalWindowManager.OnOpened (<see cref="AttachAuto"/>); вручную — <c>DialogDragHelper.Attach(this)</c>.</summary>
public sealed class DialogDragHelper : AvaloniaObject
{
    /// <summary>Элемент — шапка окна: нажатие на нём (и на его неинтерактивных потомках) двигает окно.</summary>
    public static readonly AttachedProperty<bool> IsHandleProperty =
        AvaloniaProperty.RegisterAttached<DialogDragHelper, Control, bool>("IsHandle");

    public static bool GetIsHandle(Control control) => control.GetValue(IsHandleProperty);

    public static void SetIsHandle(Control control, bool value) => control.SetValue(IsHandleProperty, value);

#if NURANDROID
    public static void Attach(Window window, Action<Window>? onMoved = null)
    {
        // 2026-10-30: на Android окна — слои WindowLayerHost, перетаскивания нет.
    }

    public static void AttachAuto(Window window)
    {
    }
#else
    /// <summary>Высота верхней зоны, за которую двигается окно без своей шапки (шапка модалок — 56…80).</summary>
    private const double FallbackHeaderHeight = 56;

    /// <summary>Окна, которые двигаются сами (свой BeginMoveDrag) — второй обработчик дал бы двойное перетаскивание.</summary>
    private static readonly HashSet<string> SelfDragging = new(StringComparer.Ordinal)
    {
        "CustomerDisplayEditorWindow", "ThemeEditorWindow", "ShiftsHistoryWindow", "PayDebtDialog", "ProductEditDialog",
        "ClientsWindow", "WarehouseWindow", "CalculatorWindow", "OwnerShellWindow",
        "CustomerDisplayWindow", "SplashWindow", "FrmKeyboard", "MainWindow",
    };

    /// <summary>Сколько раз начато перетаскивание (для стенда проверки).</summary>
    internal static int StartedCount;

    private sealed class State
    {
        public Action<Window>? OnMoved;
        public DispatcherTimer? Settle;
        public EventHandler? SettleTick; // 2026-10-30: прежний обработчик таймера снимается при новом перетаскивании (приёмка 1.17.67)
    }

    private static readonly ConditionalWeakTable<Window, State> Attached = new();

    /// <summary>Автоподключение к только что открытому окну: только окна без системной рамки, не главное, не двигающиеся сами.</summary>
    public static void AttachAuto(Window window)
    {
        try
        {
            if (window.SystemDecorations != SystemDecorations.None || SelfDragging.Contains(window.GetType().Name))
                return;
            // 2026-10-30 (приёмка 1.17.67, п. 7б, 7в): всплывающие плашки (SuccessToast, «Приходы в фоне», баннер удалённой помощи —
            // ShowActivated=false) тянуть не нужно: запасная зона в 56 точек — вся плашка, щелчок по ней не срабатывал.
            // Разделы, встроенные в программу владельца, тоже не двигаются сами (их место задаёт OwnerShellWindow).
            if (!window.ShowActivated || NurMarketKassa.AvaloniaHost.Services.FullscreenHelper.IsEmbedded(window))
                return;
            var main = (Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (ReferenceEquals(main, window))
                return;
            Attach(window);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"DialogDragHelper: автоподключение {window.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Подключает перетаскивание за шапку (повторный вызов безопасен; обработчик onMoved заменяется).
    /// onMoved вызывается после того, как окно отпустили на новом месте и вернули в рабочую область.</summary>
    public static void Attach(Window window, Action<Window>? onMoved = null)
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (Attached.TryGetValue(window, out var existing))
        {
            if (onMoved is not null)
                existing.OnMoved = onMoved;
            return;
        }
        Attached.Add(window, new State { OnMoved = onMoved });
        // Пузырьком и без handledEventsToo: кнопки и поля, которые забрали нажатие, перетаскивание не запускают.
        window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Window window || e.Handled || window.WindowState != WindowState.Normal)
            return;
        try
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed || !IsOnHeader(window, e))
                return;
            if (!Attached.TryGetValue(window, out var state))
                return;
            var before = window.Position;
            e.Handled = true;
            StartedCount++;
            if (OperatingSystem.IsWindows())
            {
                // Win32: BeginMoveDrag возвращается, когда окно отпустили — подгоняем сразу.
                window.BeginMoveDrag(e);
                Finish(window, state, before);
            }
            else
            {
                // X11/Wayland/macOS: перетаскивание идёт асинхронно — ждём, пока положение перестанет меняться.
                window.BeginMoveDrag(e);
                WatchUntilSettled(window, state, before);
            }
        }
        catch (Exception ex)
        {
            // Wayland и часть оболочек Linux не умеют BeginMoveDrag — окно просто остаётся на месте.
            NurMarketKassa.Services.PosLogger.Log($"DialogDragHelper: перетаскивание {window.GetType().Name} не удалось: {ex.Message}", "DEBUG");
        }
    }

    private static void WatchUntilSettled(Window window, State state, PixelPoint before)
    {
        state.Settle ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        var timer = state.Settle;
        var last = window.Position;
        timer.Stop();
        if (state.SettleTick is not null)
            timer.Tick -= state.SettleTick;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (window.Position != last)
            {
                last = window.Position; // ещё двигают
                return;
            }
            timer.Stop();
            timer.Tick -= tick;
            if (ReferenceEquals(state.SettleTick, tick))
                state.SettleTick = null;
            Finish(window, state, before);
        };
        state.SettleTick = tick;
        timer.Tick += tick;
        timer.Start();
    }

    private static void Finish(Window window, State state, PixelPoint before)
    {
        try
        {
            if (!window.IsVisible || window.Position == before)
                return;
            KeepInsideWorkingArea(window);
            state.OnMoved?.Invoke(window);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"DialogDragHelper: после перетаскивания {window.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Окно целиком в рабочей области того экрана, где его большая часть (несколько мониторов); шире экрана — по левому/верхнему краю.</summary>
    internal static void KeepInsideWorkingArea(Window window)
    {
        var screens = window.Screens;
        if (screens is null)
            return;
        var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        var size = window.FrameSize ?? window.ClientSize;
        var w = (int)Math.Round(size.Width * scale);
        var h = (int)Math.Round(size.Height * scale);
        if (w <= 0 || h <= 0)
            return;
        var pos = window.Position;
        var rect = new PixelRect(pos, new PixelSize(w, h));
        Avalonia.Platform.Screen? best = null;
        long bestArea = 0;
        foreach (var screen in screens.All)
        {
            var common = screen.WorkingArea.Intersect(rect);
            long area = (long)common.Width * common.Height;
            if (area > bestArea)
            {
                best = screen;
                bestArea = area;
            }
        }
        best ??= screens.ScreenFromPoint(rect.Center) ?? screens.Primary;
        if (best is null)
            return;
        var wa = best.WorkingArea;
        var x = w >= wa.Width ? wa.X : Math.Clamp(pos.X, wa.X, wa.Right - w);
        var y = h >= wa.Height ? wa.Y : Math.Clamp(pos.Y, wa.Y, wa.Bottom - h);
        if (x != pos.X || y != pos.Y)
            window.Position = new PixelPoint(x, y);
    }

    /// <summary>Нажатие пришлось на шапку: идём от источника вверх; встретили шапку — да, встретили интерактивный элемент раньше — нет.</summary>
    private static bool IsOnHeader(Window window, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return false;
        for (var visual = source; visual is not null && !ReferenceEquals(visual, window); visual = visual.GetVisualParent())
        {
            if (visual is not Control control)
                continue;
            if (GetIsHandle(control) || control.Classes.Contains("pay-header"))
                return true;
            if (IsInteractive(control))
                return false;
        }
        // Шапка без фона (как pay-header у оплаты) нажатие «пропускает» к фону окна — проверяем по границам шапки.
        // Своей шапки в окне нет — верхняя полоса. Интерактивных элементов в цепочке источника нет (иначе вышли бы раньше).
        var point = e.GetPosition(window);
        var hasHandle = false;
        foreach (var item in window.GetVisualDescendants())
        {
            if (item is not Control control || !(GetIsHandle(control) || control.Classes.Contains("pay-header")))
                continue;
            hasHandle = true;
            if (control.IsEffectivelyVisible && control.TranslatePoint(default, window) is { } origin
                && new Rect(origin, control.Bounds.Size).Contains(point))
                return true;
        }
        return !hasHandle && point.Y <= FallbackHeaderHeight;
    }

    private static bool IsInteractive(Control control) =>
        control is Button or ToggleButton or TextBox or ComboBox or Slider or ScrollBar or Thumb or TabItem or ListBoxItem
            or MenuItem or NumericUpDown or AutoCompleteBox or DatePicker or Calendar or DataGrid or SelectableTextBlock
        || (control.Focusable && control is not Window);
#endif
}
