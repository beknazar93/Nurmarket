using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvTopLevel = Avalonia.Controls.TopLevel;

namespace NurMarketKassa;

/// <summary>2026-10-04, Android-касса: единственный вид программы — стопка «окон» кассы.
/// Нижние слои — главные окна (заставка, вход, касса, программа владельца) на весь экран;
/// сверху — окна и диалоги. Модальный диалог (ShowDialog) кладётся с затемнением, и всё, что под
/// ним, перестаёт принимать касания — как модальное окно в Windows.
/// Размеры: окно, развёрнутое в Windows (Maximized/FullScreen), главное окно и окно, которое почти
/// не помещается на экран, растягиваются на весь вид; остальные — по центру, не больше вида.</summary>
public sealed class WindowLayerHost : Panel
{
    public static WindowLayerHost? Current { get; private set; }

    private const double DialogMargin = 8;
    private readonly List<Window> _windows = new();
    private readonly Dictionary<Window, Layer> _layers = new();
    private readonly Dictionary<Window, IInputElement?> _lastFocus = new();

    public WindowLayerHost()
    {
        Current = this;
        Background = Brushes.Black;
        this[!BackgroundProperty] = this.GetResourceObservable("BrushWindow").ToBinding();
    }

    /// <summary>Окна снизу вверх.</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>Верхнее видимое окно (активное).</summary>
    public Window? TopWindow => _windows.LastOrDefault(w => w.IsVisible);

    /// <summary>Главное окно задаёт AndroidDesktopLifetime (desktop.MainWindow = …).</summary>
    internal Func<Window, bool>? IsMainWindow { get; set; }

    // ---- Стек окон ----

    internal void Add(Window window, bool modal)
    {
        if (_layers.ContainsKey(window))
            return;

        RememberFocusOfTop();

        var layer = new Layer(window, modal);
        _layers[window] = layer;

        // Окна «поверх всех» (Topmost) остаются наверху; обычные кладутся под них.
        var index = _windows.Count;
        if (!window.Topmost)
        {
            while (index > 0 && _windows[index - 1].Topmost)
                index--;
        }
        _windows.Insert(index, window);
        Children.Insert(index, layer);

        ApplySizing(window);
        UpdateInteractivity();
    }

    internal void Remove(Window window)
    {
        if (!_layers.TryGetValue(window, out var layer))
            return;
        _layers.Remove(window);
        _windows.Remove(window);
        _lastFocus.Remove(window);
        Children.Remove(layer);
        layer.Detach();
        UpdateInteractivity();
        RestoreFocusOfTop();
    }

    internal void BringToFront(Window window)
    {
        if (!_layers.TryGetValue(window, out var layer))
            return;
        window.IsVisible = true;
        if (!ReferenceEquals(_windows.LastOrDefault(), window))
        {
            RememberFocusOfTop();
            _windows.Remove(window);
            Children.Remove(layer);
            var index = _windows.Count;
            if (!window.Topmost)
            {
                while (index > 0 && _windows[index - 1].Topmost)
                    index--;
            }
            _windows.Insert(index, window);
            Children.Insert(index, layer);
        }
        UpdateInteractivity();
        FocusWindow(window);
    }

    internal void OnWindowHidden(Window window)
    {
        UpdateInteractivity();
        RestoreFocusOfTop();
    }

    internal void Relayout(Window window)
    {
        if (_layers.ContainsKey(window))
            ApplySizing(window);
    }

    // ---- Активность, касания, фокус ----

    private void UpdateInteractivity()
    {
        // Ниже верхнего модального окна ничего не принимает касания (как в Windows).
        var topModal = -1;
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            if (_windows[i].IsVisible && _layers[_windows[i]].IsModal)
            {
                topModal = i;
                break;
            }
        }

        for (var i = 0; i < _windows.Count; i++)
        {
            var layer = _layers[_windows[i]];
            layer.IsHitTestVisible = i >= topModal;
            layer.IsVisible = _windows[i].IsVisible;
        }

        var top = TopWindow;
        foreach (var w in _windows)
            w.SetActive(ReferenceEquals(w, top));
    }

    internal void FocusWindow(Window window)
    {
        if (!ReferenceEquals(TopWindow, window))
            return;
        var focused = AvTopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (focused is not null && (ReferenceEquals(focused, window) || focused.GetVisualAncestors().Contains(window)))
            return;
        window.Focus();
    }

    private void RememberFocusOfTop()
    {
        if (TopWindow is not { } top)
            return;
        var focused = AvTopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is Visual v && v.GetVisualAncestors().Contains(top))
            _lastFocus[top] = focused;
    }

    private void RestoreFocusOfTop()
    {
        if (TopWindow is not { } top)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(TopWindow, top))
                return;
            if (_lastFocus.TryGetValue(top, out var element) && element is Visual v && v.GetVisualRoot() is not null && element.Focus())
                return;
            FocusWindow(top);
        }, DispatcherPriority.Input);
    }

    // ---- Размеры окон ----

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        foreach (var w in _windows)
            ApplySizing(w);
        foreach (var w in _windows)
            w.Screens.RaiseChanged();
    }

    private void ApplySizing(Window window)
    {
        if (!_layers.TryGetValue(window, out var layer))
            return;
        var hostW = Bounds.Width;
        var hostH = Bounds.Height;
        if (hostW <= 0 || hostH <= 0)
            return; // вид ещё не разложен — пересчитаем в OnSizeChanged

        // Полоса заголовка с «✕» — у окон, которым в Windows рамку рисовала система.
        window.AndroidChromeVisible = window.SystemDecorations != SystemDecorations.None
                                      && !(IsMainWindow?.Invoke(window) ?? false);

        var isMain = IsMainWindow?.Invoke(window) ?? false;
        var maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;

        // Окно, которое код сам поставил в точку экрана (разделы программы владельца поверх
        // области главного окна): размер оставляем его, только не больше вида. Главное и
        // развёрнутое окно всё равно растягиваются на весь вид.
        layer.IsFill = false;
        if (window.ExplicitPosition is not null && !isMain && !maximized)
        {
            layer.Applying = true;
            try
            {
                window.MaxWidth = Math.Min(layer.Original.MaxWidth, hostW);
                window.MaxHeight = Math.Min(layer.Original.MaxHeight, hostH);
                window.HorizontalAlignment = HorizontalAlignment.Left;
                window.VerticalAlignment = VerticalAlignment.Top;
            }
            finally
            {
                layer.Applying = false;
            }
            layer.InvalidateArrange();
            return;
        }

        var o = layer.Original;
        var fill = maximized
                   || isMain
                   || (!double.IsNaN(o.Width) && o.Width >= hostW * 0.9 && !double.IsNaN(o.Height) && o.Height >= hostH * 0.85)
                   || (o.MinWidth >= hostW * 0.9 && o.MinHeight >= hostH * 0.85);

        layer.IsFill = fill;
        layer.Applying = true;
        try
        {
            if (fill)
            {
                window.Width = double.NaN;
                window.Height = double.NaN;
                window.MinWidth = 0;
                window.MinHeight = 0;
                window.MaxWidth = double.PositiveInfinity;
                window.MaxHeight = double.PositiveInfinity;
                window.HorizontalAlignment = HorizontalAlignment.Stretch;
                window.VerticalAlignment = VerticalAlignment.Stretch;
                window.Margin = default;
            }
            else
            {
                var maxW = Math.Max(200, hostW - 2 * DialogMargin);
                var maxH = Math.Max(160, hostH - 2 * DialogMargin);
                // SizeToContent в Avalonia главнее заданных Width/Height — окно по содержимому.
                var autoW = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
                var autoH = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
                window.Width = autoW || double.IsNaN(o.Width) ? double.NaN : Math.Min(o.Width, maxW);
                window.Height = autoH || double.IsNaN(o.Height) ? double.NaN : Math.Min(o.Height, maxH);
                window.MinWidth = Math.Min(o.MinWidth, maxW);
                window.MinHeight = Math.Min(o.MinHeight, maxH);
                window.MaxWidth = Math.Min(o.MaxWidth, maxW);
                window.MaxHeight = Math.Min(o.MaxHeight, maxH);
                window.HorizontalAlignment = HorizontalAlignment.Center;
                window.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        finally
        {
            layer.Applying = false;
        }
        layer.InvalidateArrange();
    }

    // ---- Слой одного окна ----

    private sealed class Layer : Panel
    {
        public Layer(Window window, bool modal)
        {
            Window = window;
            IsModal = modal;
            Original = new OriginalSize(window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight);
            if (modal)
            {
                // Затемнение под модальным окном; касания по нему ничего не делают (как в Windows).
                Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                    IsHitTestVisible = true,
                });
            }
            else
            {
                // Немодальное окно не мешает касаться окон под ним вне своей области.
                Background = null;
            }
            Children.Add(window);
        }

        public Window Window { get; }
        public bool IsModal { get; }
        public OriginalSize Original { get; }
        public bool Applying { get; set; }
        public bool IsFill { get; set; }

        public void Detach() => Children.Clear();

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
                child.Measure(availableSize);
            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var child in Children)
            {
                if (ReferenceEquals(child, Window) && !IsFill && Window.ExplicitPosition is { } screenPoint)
                {
                    // Точка экрана (пиксели) → точка внутри вида (как PointToScreen у кода окна).
                    var local = this.PointToClient(screenPoint);
                    var size = Window.DesiredSize;
                    var x = Math.Clamp(local.X, 0, Math.Max(0, finalSize.Width - size.Width));
                    var y = Math.Clamp(local.Y, 0, Math.Max(0, finalSize.Height - size.Height));
                    child.Arrange(new Rect(new Point(x, y), size));
                    continue;
                }
                child.Arrange(new Rect(finalSize));
            }
            return finalSize;
        }
    }

    private readonly record struct OriginalSize(double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

    // ---- Кнопка «Назад» и клавиши без фокуса ----

    private AvTopLevel? _attachedTop;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedTop = AvTopLevel.GetTopLevel(this);
        if (_attachedTop is null)
            return;
        _attachedTop.BackRequested += OnBackRequested;
        _attachedTop.AddHandler(KeyDownEvent, ForwardUnfocusedKey, RoutingStrategies.Bubble);
        _attachedTop.AddHandler(KeyUpEvent, ForwardUnfocusedKey, RoutingStrategies.Bubble);
        _attachedTop.AddHandler(TextInputEvent, ForwardUnfocusedText, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_attachedTop is not null)
        {
            _attachedTop.BackRequested -= OnBackRequested;
            _attachedTop.RemoveHandler(KeyDownEvent, ForwardUnfocusedKey);
            _attachedTop.RemoveHandler(KeyUpEvent, ForwardUnfocusedKey);
            _attachedTop.RemoveHandler(TextInputEvent, ForwardUnfocusedText);
            _attachedTop = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>«Назад» на Android = крестик верхнего окна. Главное окно так не закрывается
    /// (кассир не должен случайно выйти из кассы кнопкой телефона).</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (TopWindow is not { } top || (IsMainWindow?.Invoke(top) ?? false) || _windows.Count <= 1)
            return;
        top.RequestCloseFromUser();
    }

    /// <summary>Нажатие пришло, когда фокус не внутри окна (никто не в фокусе) — отдаём его
    /// верхнему окну: в Windows клавиши всегда получает активное окно (горячие клавиши кассы,
    /// сканер-клавиатура).</summary>
    private void ForwardUnfocusedKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || TopWindow is not { } top || !IsOutsideWindows(e.Source))
            return;
        var copy = new KeyEventArgs
        {
            RoutedEvent = e.RoutedEvent,
            Key = e.Key,
            KeyModifiers = e.KeyModifiers,
            PhysicalKey = e.PhysicalKey,
            KeySymbol = e.KeySymbol,
            KeyDeviceType = e.KeyDeviceType,
            Source = top,
        };
        top.RaiseEvent(copy);
        e.Handled = copy.Handled;
    }

    private void ForwardUnfocusedText(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || TopWindow is not { } top || !IsOutsideWindows(e.Source))
            return;
        var copy = new TextInputEventArgs { RoutedEvent = e.RoutedEvent, Text = e.Text, Source = top };
        top.RaiseEvent(copy);
        e.Handled = copy.Handled;
    }

    private bool IsOutsideWindows(object? source) =>
        source is not Visual v || !_windows.Any(w => ReferenceEquals(v, w) || v.GetVisualAncestors().Contains(w));
}
