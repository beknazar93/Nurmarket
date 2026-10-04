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

    /// <summary>2026-10-04: меньше этого окно не уменьшается (дальше текст не прочитать) — остальное листается вбок.</summary>
    private const double MinDialogScale = 0.75;

    /// <summary>2026-10-04: окна кассы и разделы владельца на узком экране раскладываются не уже этого (точек),
    /// лишнее листается вбок (Window.LayoutMinWidth).</summary>
    private const double ComfortWidth = 760;

    /// <summary>2026-10-04: окна, которые на узком экране сами перестраиваются в одну колонку
    /// (NarrowStack / свои правки) — их не уменьшаем, а даём ширину экрана.</summary>
    // 2026-10-05, снимки владельца с телефона: в возврате список чеков уходил под кнопки окна, в оплате долга
    // таблица сжималась до одной строки — окно листалось целиком, и таблицы получали высоту «по содержимому».
    // Эти окна перестраиваются сами (NarrowLayout) и на телефоне открываются во весь экран.
    internal static readonly HashSet<string> NarrowWindows = new(StringComparer.Ordinal)
        { "CheckoutDialog", "LoginWindow", "ProductEditDialog", "ReturnSaleDialog", "PayDebtDialog" };

    /// <summary>2026-10-04: формы, которые на телефоне открываются во весь экран.</summary>
    internal static readonly HashSet<string> FullScreenOnPhone = new(StringComparer.Ordinal)
        { "ProductEditDialog", "ReturnSaleDialog", "PayDebtDialog" };
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
        // 2026-10-05, владелец: «плавный интерфейс и приятный переход». Окна и диалоги появляются плавно; главные
        // окна (заставка, касса, программа владельца) — сразу.
        if (!(IsMainWindow?.Invoke(window) ?? false))
            AnimateIn(layer, modal);
        // 2026-10-04: журнал окон — чтобы по журналу Android было видно, какое окно висит сверху.
        NurMarketKassa.Services.PosLogger.Log($"Android: окно открыто {window.GetType().Name}{(modal ? " (модальное)" : "")}, слоёв {_windows.Count}.", "UI");
        // 2026-10-04, диагностика «модалка открыта, но её не видно»: через секунду — размеры и видимость слоёв.
        DispatcherTimer.RunOnce(DumpLayers, TimeSpan.FromSeconds(1));
    }

    private static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(170);

    /// <summary>2026-10-05: появление окна — затухание 0 → 1, модальное ещё и поднимается на 18 точек. Анимируется
    /// сам слой (его размер и уменьшение окна — в ArrangeOverride — не трогаются).</summary>
    private static void AnimateIn(Layer layer, bool modal)
    {
        try
        {
            var easing = new Avalonia.Animation.Easings.CubicEaseOut();
            var transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = AppearDuration, Easing = easing },
            };
            layer.Opacity = 0;
            if (modal)
            {
                transitions.Add(new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = AppearDuration,
                    Easing = easing,
                });
                layer.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(18px)");
            }
            layer.Transitions = transitions;
            Dispatcher.UIThread.Post(() =>
            {
                layer.Opacity = 1;
                if (modal)
                    layer.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("none");
            }, DispatcherPriority.Background);
        }
        catch
        {
            layer.Opacity = 1;
            layer.RenderTransform = null;
        }
    }

    private void DumpLayers()
    {
        try
        {
            var lines = _windows.Select(w =>
            {
                var l = _layers.TryGetValue(w, out var layer) ? layer : null;
                return $"{w.GetType().Name}: окно vis={w.IsVisible} op={w.Opacity:0.##} {w.Bounds.Width:0}×{w.Bounds.Height:0}@{w.Bounds.X:0},{w.Bounds.Y:0}"
                       + (l is null ? " (без слоя)" : $" | слой vis={l.IsVisible} hit={l.IsHitTestVisible} {l.Bounds.Width:0}×{l.Bounds.Height:0} scale={l.Scale:0.##}");
            });
            NurMarketKassa.Services.PosLogger.Log($"Android: слои {Bounds.Width:0}×{Bounds.Height:0} — " + string.Join(" ; ", lines), "UI");
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: слои — {ex.Message}", "UI");
        }
    }

    internal void Remove(Window window)
    {
        if (!_layers.TryGetValue(window, out var layer))
            return;
        NurMarketKassa.Services.PosLogger.Log($"Android: окно закрыто {window.GetType().Name}, слоёв {_windows.Count - 1}.", "UI");
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
        // 2026-10-04, стресс-тест на телефоне: экранная клавиатура меняет высоту вида → «экраны изменились» →
        // касса (MainWindow.OnCashierScreensChanged) вызывает Activate() главного окна, и оно поднималось НАД
        // открытым модальным окном: «Укажите количество» пропадало под кассой, а касса принимала касания.
        // Как в Windows: пока выше открыт модальный диалог, окно под ним не поднимается и фокус из поля
        // диалога не забирает (иначе закрылась бы клавиатура).
        var position = _windows.IndexOf(window);
        for (var i = _windows.Count - 1; i > position; i--)
        {
            if (_windows[i].IsVisible && _layers[_windows[i]].IsModal)
            {
                UpdateInteractivity();
                return;
            }
        }
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
        // 2026-10-04: диагностика (клавиатура меняет высоту вида) — слои после раскладки.
        if (_windows.Count > 1)
            DispatcherTimer.RunOnce(DumpLayers, TimeSpan.FromMilliseconds(500));
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
        // 2026-10-04: окно, которое рисует свой заголовок и «✕» (ExtendClientAreaToDecorationsHint + NoChrome,
        // например «Новый товар»), второй полосы не получает — было два заголовка подряд.
        var ownChrome = window.ExtendClientAreaToDecorationsHint
                        && window.ExtendClientAreaChromeHints == Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        window.AndroidChromeVisible = window.SystemDecorations != SystemDecorations.None
                                      && !(IsMainWindow?.Invoke(window) ?? false)
                                      && !ownChrome;

        var isMain = IsMainWindow?.Invoke(window) ?? false;
        var maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;

        // Окно, которое код сам поставил в точку экрана (разделы программы владельца поверх
        // области главного окна): размер оставляем его, только не больше вида. Главное и
        // развёрнутое окно всё равно растягиваются на весь вид.
        layer.IsFill = false;
        var narrowAware = isMain || NarrowWindows.Contains(window.GetType().Name);
        // 2026-10-04, владелец: «ты не адаптировал админку», «пройдись по всем модалкам». Узкий экран (телефон):
        // окно не уменьшается и не листается вбок, а перестраивается — ряды «блок | блок» встают столбиком
        // (AutoReflow), модалка-форма листается вверх-вниз целиком. Окна, перестроенные вручную (NarrowWindows), — как есть.
        var phone = OperatingSystem.IsAndroid() && hostW < AutoReflow.NarrowWidth;
        window.AutoReflowEnabled = phone && !narrowAware;
        // 2026-10-04, редизайн под телефон: общие стили узкого экрана (вкладки одной строкой — App.axaml).
        window.Classes.Set("android-narrow", phone);
        if (window.ExplicitPosition is not null && !isMain && !maximized)
        {
            layer.Scale = 1.0;
            layer.Applying = true;
            try
            {
                // Раздел программы владельца: на телефоне — перестройка (см. выше), на широком — раскладка не уже
                // ComfortWidth с прокруткой вбок.
                window.AndroidPageScroll = false;
                window.LayoutMinWidth = narrowAware || phone ? 0 : ComfortWidth;
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
        // 2026-10-04, владелец: «в модалке цена продажи не видно». Форма на телефоне — во весь экран (как в
        // приложениях Android): все поля быстрого добавления товара помещаются, кнопки внизу.
        var phoneFull = phone && FullScreenOnPhone.Contains(window.GetType().Name);
        var fill = maximized
                   || isMain
                   || phoneFull
                   || (!double.IsNaN(o.Width) && o.Width >= hostW * 0.9 && !double.IsNaN(o.Height) && o.Height >= hostH * 0.85)
                   || (o.MinWidth >= hostW * 0.9 && o.MinHeight >= hostH * 0.85);

        layer.IsFill = fill;
        layer.Scale = 1.0;
        layer.Applying = true;
        try
        {
            if (fill)
            {
                // 2026-10-04: окно на весь экран: на телефоне — перестройка, иначе раскладка не уже ComfortWidth.
                window.AndroidPageScroll = false;
                window.LayoutMinWidth = narrowAware || phone ? 0 : (o.Width > 0 ? Math.Min(o.Width, ComfortWidth) : ComfortWidth);
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
                // 2026-10-04, «адаптацию под все экраны»: окно шире экрана (вертикальный телефон, диалог
                // на 880 точек) раньше сжималось и обрезалось. Теперь оно уменьшается целиком; окна, которые
                // сами перестраиваются в узкую колонку (NarrowWindows), — только сжимаются, как раньше.
                var designW = !double.IsNaN(o.Width) ? o.Width : o.MinWidth;
                // Телефон: модалка во всю ширину, без уменьшения, поля столбиком, листается целиком.
                var scale = designW > maxW * 1.05 && !narrowAware && !phone
                    ? Math.Max(MinDialogScale, maxW / designW)
                    : 1.0;
                layer.Scale = scale;
                maxW /= scale;
                maxH /= scale;
                window.AndroidPageScroll = phone && !narrowAware;
                // Не поместилось и после уменьшения — содержимое не уже своей ширины (до 760), листается вбок.
                window.LayoutMinWidth = narrowAware || phone || !(designW > 0) ? 0 : Math.Min(designW, ComfortWidth);
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

        /// <summary>2026-10-04: окно шире экрана уменьшается целиком (1 — как есть).</summary>
        public double Scale { get; set; } = 1.0;

        public void Detach() => Children.Clear();

        private Size Unscaled(Size size) =>
            Scale < 0.999 ? new Size(size.Width / Scale, size.Height / Scale) : size;

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
                child.Measure(ReferenceEquals(child, Window) ? Unscaled(availableSize) : availableSize);
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
                if (ReferenceEquals(child, Window))
                {
                    if (Scale < 0.999)
                    {
                        // 2026-10-04: уменьшенное окно — своего размера, сжимается от своего левого верхнего угла,
                        // поэтому ставим этот угол так, чтобы сжатое окно оказалось по центру слоя (раньше окно
                        // раскладывалось на весь слой и после сжатия съезжало влево-вверх).
                        var size = Window.DesiredSize;
                        var w = Math.Min(size.Width, finalSize.Width / Scale);
                        var h = Math.Min(size.Height, finalSize.Height / Scale);
                        var x = Math.Max(0, (finalSize.Width - w * Scale) / 2);
                        var y = Math.Max(0, (finalSize.Height - h * Scale) / 2);
                        Window.RenderTransformOrigin = RelativePoint.TopLeft;
                        Window.RenderTransform = new ScaleTransform(Scale, Scale);
                        child.Arrange(new Rect(x, y, w, h));
                    }
                    else
                    {
                        Window.RenderTransform = null;
                        child.Arrange(new Rect(finalSize));
                    }
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
        // 2026-10-04: поля вокруг вида кассы (вырез камеры в горизонтальном положении) были белыми —
        // фон Avalonia по умолчанию. Теперь — цвет окна кассы.
        _attachedTop[!AvTopLevel.BackgroundProperty] = this.GetResourceObservable("BrushWindow").ToBinding();
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
