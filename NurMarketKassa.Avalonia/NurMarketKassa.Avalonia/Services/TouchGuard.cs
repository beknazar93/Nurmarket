using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, владелец: «если 2 пальца касаются — появляется какая-то ошибка; сделать так, чтобы
/// двумя пальцами включался скролл». На сенсорной кассе второй палец нажимал вторую кнопку одновременно
/// с первой: два действия сразу (два окна, две оплаты) — отсюда сбой. Теперь во всех окнах программы:
/// • второй палец кнопки не нажимает, а нажатие первого отменяется, как только к нему добавился второй;
/// • движение двумя пальцами прокручивает список под ними (каталог, чек, таблицы) — колесом мыши, которое
///   понимает и ScrollViewer, и DataGrid;
/// • если сбой всё же случится во время касания двумя пальцами — он записывается в журнал, но окно
///   «Произошла ошибка» не показывается (см. App.RegisterGlobalExceptionHandlers).
/// Одним пальцем всё работает как раньше; мышь, клавиатура и сканер не затрагиваются.</summary>
internal static class TouchGuard
{
    private sealed class Touch
    {
        public required IPointer Pointer { get; init; }
        public Point Position { get; set; }
        public DateTime SeenUtc { get; set; } = DateTime.UtcNow;
    }

    private static readonly Dictionary<int, Touch> Active = new();
    private static bool _twoFinger;
    private static Point _lastCenter;
    private static TopLevel? _root;
    private static DateTime _lastMultiTouchUtc;

    /// <summary>Касание двумя пальцами было в последние 3 секунды.</summary>
    public static bool MultiTouchRecently => DateTime.UtcNow - _lastMultiTouchUtc < TimeSpan.FromSeconds(3);

    public static void Register()
    {
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>(OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPressed(TopLevel top, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
            return;
        try
        {
            // Палец, чьё отпускание система потеряла (ушёл за край, окно закрылось), иначе навсегда
            // превращал бы следующее одиночное касание во «второй палец» — кнопки перестали бы нажиматься.
            foreach (var stale in Active.Where(p => DateTime.UtcNow - p.Value.SeenUtc > TimeSpan.FromSeconds(5)).Select(p => p.Key).ToList())
                Active.Remove(stale);
            if (Active.Count == 0)
            {
                _twoFinger = false;
                _root = null;
            }
            Active[e.Pointer.Id] = new Touch { Pointer = e.Pointer, Position = e.GetPosition(top) };
            if (Active.Count < 2)
                return;

            // Второй (и следующий) палец: ничего не нажимаем, первый палец своё нажатие отменяет.
            e.Handled = true;
            if (!_twoFinger)
            {
                _twoFinger = true;
                _root = top;
                _lastCenter = Center();
                foreach (var t in Active.Values)
                    if (!ReferenceEquals(t.Pointer, e.Pointer))
                        t.Pointer.Capture(null);
                PosLogger.Log("Сенсор: касание двумя пальцами — прокрутка, нажатия отменены.", "UI");
            }
            _lastMultiTouchUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сенсор: касание не обработано: {ex.Message}", "WARNING");
        }
    }

    private static void OnMoved(TopLevel top, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch || !Active.TryGetValue(e.Pointer.Id, out var touch))
            return;
        try
        {
            touch.Position = e.GetPosition(top);
            touch.SeenUtc = DateTime.UtcNow;
            if (!_twoFinger || !ReferenceEquals(top, _root))
                return;
            e.Handled = true;
            _lastMultiTouchUtc = DateTime.UtcNow;

            var center = Center();
            var delta = center - _lastCenter;
            if (Math.Abs(delta.X) < 2 && Math.Abs(delta.Y) < 2)
                return;
            _lastCenter = center;

            // Прокрутка «колесом» элемента под пальцами: 1 шаг колеса = 50 px у ScrollViewer.
            if (top.InputHitTest(center) is not InputElement target)
                return;
            var wheel = new PointerWheelEventArgs(target, e.Pointer, top, center, e.Timestamp,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None,
                new Vector(delta.X / 50.0, delta.Y / 50.0))
            {
                RoutedEvent = InputElement.PointerWheelChangedEvent,
            };
            target.RaiseEvent(wheel);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сенсор: прокрутка двумя пальцами не удалась: {ex.Message}", "WARNING");
        }
    }

    private static void OnReleased(TopLevel top, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
            return;
        Release(e.Pointer.Id, e);
    }

    private static void Release(int id, RoutedEventArgs? e)
    {
        try
        {
            Active.Remove(id);
            if (_twoFinger)
            {
                // Отпускание после жеста двумя пальцами — не клик.
                if (e is not null)
                    e.Handled = true;
                _lastMultiTouchUtc = DateTime.UtcNow;
                if (Active.Count == 0)
                {
                    _twoFinger = false;
                    _root = null;
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сенсор: отпускание не обработано: {ex.Message}", "WARNING");
        }
    }

    private static Point Center()
    {
        double x = 0, y = 0;
        foreach (var t in Active.Values)
        {
            x += t.Position.X;
            y += t.Position.Y;
        }
        var n = Math.Max(1, Active.Count);
        return new Point(x / n, y / n);
    }
}
