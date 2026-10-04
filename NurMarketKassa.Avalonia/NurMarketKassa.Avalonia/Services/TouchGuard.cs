using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

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
            {
                BeginDragCandidate(top, e);
                return;
            }
            CancelDrag();

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
            if (!_twoFinger && DragMove(top, e))
                return;
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
        // Отпускание после прокрутки одним пальцем — не нажатие.
        if (_dragging && e.Pointer.Id == _dragPointerId)
            e.Handled = true;
        if (e.Pointer.Id == _dragPointerId)
            CancelDrag();
        Release(e.Pointer.Id, e);
    }

    // ---- 2026-10-04, Android: прокрутка одним пальцем ----
    // Владелец: «цена продажи не видна в модалке, вниз не идёт». Avalonia прокручивает пальцем, только если
    // палец лёг на пустое место: поле ввода и кнопка забирают касание себе, а в карточке товара, окне оплаты,
    // таблицах ими занято почти всё. Теперь палец, сдвинувшийся больше чем на DragThreshold, прокручивает
    // ближайшую область, которая может прокрутиться в эту сторону (вверх-вниз или вбок), а нажатие под ним
    // отменяется. Короткое касание — как раньше. Разделитель, ползунок и полоса прокрутки тянутся как раньше.

    private const double DragThreshold = 10;
    private static int _dragPointerId = -1;
    private static Visual? _dragSource;
    private static Point _dragStartTop;
    private static bool _dragging;
    private static ScrollViewer? _dragScroller;
    private static Point _dragStartInScroller;
    private static Vector _dragStartOffset;
    private static bool _dragVertical;

    private static void BeginDragCandidate(TopLevel top, PointerPressedEventArgs e)
    {
        CancelDrag();
        if (!OperatingSystem.IsAndroid() || e.Source is not Visual source)
            return;
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is GridSplitter or Avalonia.Controls.Primitives.Thumb or Slider or Avalonia.Controls.Primitives.ScrollBar)
                return;
        }
        _dragPointerId = e.Pointer.Id;
        _dragSource = source;
        _dragStartTop = e.GetPosition(top);
    }

    /// <summary>true — движение пальца ушло в прокрутку (дальше его не обрабатываем).</summary>
    private static bool DragMove(TopLevel top, PointerEventArgs e)
    {
        if (e.Pointer.Id != _dragPointerId || _dragSource is null)
            return false;
        try
        {
            if (!_dragging)
            {
                var d = e.GetPosition(top) - _dragStartTop;
                if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold)
                    return false;
                var vertical = Math.Abs(d.Y) >= Math.Abs(d.X);
                var scroller = FindScroller(_dragSource, vertical) ?? FindScroller(_dragSource, !vertical);
                if (scroller is null)
                {
                    CancelDrag();
                    return false;
                }
                _dragging = true;
                _dragScroller = scroller;
                _dragVertical = ReferenceEquals(scroller, FindScroller(_dragSource, vertical)) ? vertical : !vertical;
                _dragStartOffset = scroller.Offset;
                _dragStartInScroller = e.GetPosition(scroller);
                // Поле ввода / кнопка больше не держат касание — клика и выделения текста не будет.
                e.Pointer.Capture(null);
            }

            var now = e.GetPosition(_dragScroller!);
            var delta = now - _dragStartInScroller;
            var offset = _dragVertical
                ? new Vector(_dragStartOffset.X, _dragStartOffset.Y - delta.Y)
                : new Vector(_dragStartOffset.X - delta.X, _dragStartOffset.Y);
            var max = new Vector(
                Math.Max(0, _dragScroller!.Extent.Width - _dragScroller.Viewport.Width),
                Math.Max(0, _dragScroller.Extent.Height - _dragScroller.Viewport.Height));
            _dragScroller.Offset = new Vector(Math.Clamp(offset.X, 0, max.X), Math.Clamp(offset.Y, 0, max.Y));
            e.Handled = true;
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сенсор: прокрутка пальцем не удалась: {ex.Message}", "WARNING");
            CancelDrag();
            return false;
        }
    }

    private static ScrollViewer? FindScroller(Visual source, bool vertical)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is ScrollViewer sv)
            {
                var can = vertical
                    ? sv.Extent.Height > sv.Viewport.Height + 1 && sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    : sv.Extent.Width > sv.Viewport.Width + 1 && sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
                if (can)
                    return sv;
            }
        }
        return null;
    }

    private static void CancelDrag()
    {
        _dragPointerId = -1;
        _dragSource = null;
        _dragging = false;
        _dragScroller = null;
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
