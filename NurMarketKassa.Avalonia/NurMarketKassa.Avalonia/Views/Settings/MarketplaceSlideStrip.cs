using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-3, 3.7 п.3 и 3.8): лента слайдов на странице «подробно» и полноэкранный просмотр.
// Лента: слайды 16:9 одной высоты, круглые ‹ › поверх краёв (видны, если есть куда листать), точки, колесо и перетаскивание,
// клик — просмотр на весь экран через ShowOverlay страницы. Картинки грузятся в фоне и освобождаются при уходе со страницы.

/// <summary>Лента слайдов. Слайды, которых нет в ресурсах, сюда не попадают (их отсеивает страница); битый слайд пропускается.</summary>
internal sealed class MarketplaceSlideStrip : Grid
{
    private sealed class Cell
    {
        public required string Uri;
        public required string Caption;
        public Bitmap? Bitmap;
        public required Border Frame;
        public required Image Image;
        public required StackPanel Host;
        public bool Failed;
    }

    private const double Gap = 12;
    private readonly string _itemTitle;
    private readonly Action<Control> _showOverlay;
    private readonly Action _hideOverlay;
    private readonly double _slideHeight;
    private readonly double _slideWidth;
    private readonly List<Cell> _cells = [];
    private readonly StackPanel _track = new() { Orientation = Orientation.Horizontal, Spacing = Gap, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _translate = new();
    private readonly Border _clip = new() { ClipToBounds = true };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Control _prev;
    private readonly Control _next;
    private double _offset;
    private double _wheel;
    private CancellationTokenSource? _cts;
    private MarketplaceSlideViewer? _viewer;
    private Point? _downPoint;
    private double _downOffset;
    private bool _dragging;

    /// <summary>Ни одна картинка не загрузилась — страница заменит ленту обложкой.</summary>
    public event Action? AllFailed;

    public MarketplaceSlideStrip(string itemTitle, IReadOnlyList<(string Uri, string Caption)> slides, double slideHeight,
        Action<Control> showOverlay, Action hideOverlay)
    {
        _itemTitle = itemTitle;
        _showOverlay = showOverlay;
        _hideOverlay = hideOverlay;
        _slideHeight = slideHeight;
        _slideWidth = Math.Round(slideHeight * 16 / 9);
        RowDefinitions = new RowDefinitions("Auto,Auto");

        _track.RenderTransform = _translate;
        _clip.Child = _track;
        foreach (var (uri, caption) in slides)
            _cells.Add(MakeCell(uri, caption));

        _prev = MarketplaceDetailUi.RoundButton("chevron-left", 40, 22, MarketplaceDetailUi.Word("prev"), Prev, onDark: false);
        _next = MarketplaceDetailUi.RoundButton("chevron-right", 40, 22, MarketplaceDetailUi.Word("next"), Next, onDark: false);
        _prev.HorizontalAlignment = HorizontalAlignment.Left;
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _prev.VerticalAlignment = _next.VerticalAlignment = VerticalAlignment.Top;
        _prev.Margin = new Thickness(8, _slideHeight / 2 - 20, 0, 0);
        _next.Margin = new Thickness(0, _slideHeight / 2 - 20, 8, 0);

        RebuildTrack();
        var viewport = new Grid { Children = { _clip, _prev, _next } };
        Children.Add(viewport);
        SetRow(_dots, 1);
        Children.Add(_dots);

        _clip.SizeChanged += (_, _) => { SetOffset(_offset, animate: false); };
        // Перетаскивание (мышь и касание) и клик — в одном обработчике: клик без сдвига открывает просмотр.
        _clip.AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: false);
        _clip.AddHandler(PointerMovedEvent, OnPointerMoved, handledEventsToo: false);
        _clip.AddHandler(PointerReleasedEvent, OnPointerReleased, handledEventsToo: false);
        _clip.AddHandler(PointerCaptureLostEvent, (_, _) => { _downPoint = null; _dragging = false; EnableTransition(true); });
        PointerWheelChanged += OnWheel;

        AttachedToVisualTree += (_, _) => LoadAll();
        DetachedFromVisualTree += (_, _) => Release();
    }

    private Cell MakeCell(string uri, string caption)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        var frame = new Border
        {
            Width = _slideWidth,
            Height = _slideHeight,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Child = image,
        };
        MarketplaceDetailUi.Bind(frame, Border.BackgroundProperty, "BrushPanelSoft");
        MarketplaceDetailUi.Bind(frame, Border.BorderBrushProperty, "BrushBorder");

        var host = new StackPanel { Width = _slideWidth, Spacing = 6 };
        var cell = new Cell { Uri = uri, Caption = caption, Frame = frame, Image = image, Host = host };
        host.Children.Add(frame);
        if (!string.IsNullOrWhiteSpace(caption))
            host.Children.Add(MarketplaceDetailUi.Text(caption, 12.5, "BrushTextSoft"));

        // Фокус с клавиатуры: Tab к слайду, Enter/пробел — на весь экран (рамка фокуса — у самого слайда).
        frame.Focusable = true;
        frame.IsTabStop = true;
        Avalonia.Automation.AutomationProperties.SetName(frame, string.IsNullOrWhiteSpace(caption) ? _itemTitle : caption);
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(frame, Avalonia.Automation.Peers.AutomationControlType.Button);
        frame.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                OpenViewer(_cells.IndexOf(cell));
            }
        };
        frame.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            {
                frame.BorderThickness = new Thickness(2);
                MarketplaceDetailUi.Bind(frame, Border.BorderBrushProperty, "BrushFocusRing");
                EnsureVisible(_cells.IndexOf(cell));
            }
        };
        frame.LostFocus += (_, _) =>
        {
            frame.BorderThickness = new Thickness(1);
            MarketplaceDetailUi.Bind(frame, Border.BorderBrushProperty, "BrushBorder");
        };
        frame.Cursor = new Cursor(StandardCursorType.Hand);
        return cell;
    }

    private void RebuildTrack()
    {
        _track.Children.Clear();
        foreach (var cell in _cells.Where(c => !c.Failed))
            _track.Children.Add(cell.Host);
        RebuildDots();
        UpdateNav();
    }

    private int Count => _cells.Count(c => !c.Failed);
    private double Step => _slideWidth + Gap;
    private double MaxOffset => Math.Max(0, Count * _slideWidth + Math.Max(0, Count - 1) * Gap - _clip.Bounds.Width);

    // ------------------------------------------------------------------ загрузка и освобождение картинок

    private void LoadAll()
    {
        var pending = _cells.Where(c => c.Bitmap == null && !c.Failed).ToList();
        if (pending.Count == 0)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        // Декодирование — в фоне, по одной картинке: UI-поток не стоит, память не прыгает сразу на все слайды.
        Task.Run(() =>
        {
            foreach (var cell in pending)
            {
                if (token.IsCancellationRequested)
                    return;
                var bitmap = MarketplaceMedia.LoadBitmap(cell.Uri);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || !this.IsAttachedToVisualTree())
                    {
                        bitmap?.Dispose();
                        return;
                    }
                    if (bitmap == null)
                    {
                        cell.Failed = true; // пропущен, предупреждение уже в журнале (MarketplaceMedia.LoadBitmap)
                        RebuildTrack();
                        if (Count == 0)
                            AllFailed?.Invoke();
                        return;
                    }
                    cell.Bitmap = bitmap;
                    cell.Image.Source = bitmap;
                });
            }
        }, token);
    }

    private void Release()
    {
        _cts?.Cancel();
        _cts = null;
        // Просмотр держит те же картинки — закрыть его до освобождения.
        if (_viewer != null)
        {
            _viewer = null;
            _hideOverlay();
        }
        var toDispose = new List<Bitmap>();
        foreach (var cell in _cells)
        {
            cell.Image.Source = null;
            if (cell.Bitmap != null)
                toDispose.Add(cell.Bitmap);
            cell.Bitmap = null;
        }
        // Освобождаем после отрисовки: кадр мог ещё ссылаться на картинку.
        MarketplaceDetailUi.PostBackground(() =>
        {
            foreach (var bitmap in toDispose)
                bitmap.Dispose();
        });
    }

    // ------------------------------------------------------------------ прокрутка

    private void EnableTransition(bool on)
    {
        _translate.Transitions = on
            ? new Transitions { new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new CubicEaseOut() } }
            : null;
    }

    private void SetOffset(double value, bool animate)
    {
        _offset = Math.Clamp(value, 0, MaxOffset);
        if (!animate)
            _translate.Transitions = null;
        else if (_translate.Transitions == null)
            EnableTransition(true);
        _translate.X = -_offset;
        UpdateNav();
    }

    private void UpdateNav()
    {
        var max = MaxOffset;
        var scrollable = max > 1;
        _prev.IsVisible = scrollable && _offset > 1;
        _next.IsVisible = scrollable && _offset < max - 1;
        _dots.IsVisible = scrollable && Count > 1;
        var active = ActiveIndex();
        for (var i = 0; i < _dots.Children.Count; i++)
            StyleDot((Border)_dots.Children[i], i == active);
    }

    private int ActiveIndex()
    {
        var count = Count;
        if (count == 0)
            return 0;
        var max = MaxOffset;
        if (max > 1 && _offset >= max - 1)
            return count - 1;
        return Math.Clamp((int)Math.Round(_offset / Step), 0, count - 1);
    }

    private void Prev()
    {
        var index = Math.Max(0, (int)Math.Ceiling(_offset / Step - 0.01) - 1);
        SetOffset(index * Step, animate: true);
    }

    private void Next()
    {
        var index = (int)Math.Floor(_offset / Step + 0.01) + 1;
        SetOffset(Math.Min(index, Count - 1) * Step, animate: true);
    }

    private void GoTo(int index) => SetOffset(index * Step, animate: true);

    private void EnsureVisible(int index)
    {
        if (index < 0)
            return;
        var left = index * Step;
        var right = left + _slideWidth;
        if (left < _offset)
            SetOffset(left, animate: true);
        else if (right > _offset + _clip.Bounds.Width)
            SetOffset(right - _clip.Bounds.Width, animate: true);
    }

    /// <summary>Стрелки ← → (вызывает страница, когда фокус внутри неё). true — клавиша обработана.</summary>
    public bool HandleKey(Key key)
    {
        if (MaxOffset <= 1)
            return false;
        if (key == Key.Left)
            Prev();
        else if (key == Key.Right)
            Next();
        else
            return false;
        return true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (MaxOffset <= 1)
            return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
        if ((delta > 0 && _offset >= MaxOffset - 1) || (delta < 0 && _offset <= 1))
            return; // край ленты — пусть страница прокручивается дальше
        _wheel += delta;
        if (Math.Abs(_wheel) >= 1)
        {
            if (_wheel > 0)
                Next();
            else
                Prev();
            _wheel = 0;
        }
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_clip).Properties.IsLeftButtonPressed)
            return;
        _downPoint = e.GetPosition(_clip);
        _downOffset = _offset;
        _dragging = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_downPoint is not { } start)
            return;
        var dx = e.GetPosition(_clip).X - start.X;
        if (!_dragging && Math.Abs(dx) > 8 && MaxOffset > 1)
        {
            _dragging = true;
            e.Pointer.Capture(_clip);
            _translate.Transitions = null;
        }
        if (_dragging)
        {
            _translate.X = -Math.Clamp(_downOffset - dx, 0, MaxOffset);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_downPoint is not { } start)
            return;
        var wasDragging = _dragging;
        _downPoint = null;
        _dragging = false;
        if (wasDragging)
        {
            e.Pointer.Capture(null);
            var dx = e.GetPosition(_clip).X - start.X;
            var target = Math.Clamp(_downOffset - dx, 0, MaxOffset);
            EnableTransition(true);
            // Короткий щелчок-свайп листает на слайд, длинный — к ближайшему.
            var index = Math.Abs(dx) > 40 && Math.Abs(dx) < _slideWidth / 2
                ? (dx < 0 ? (int)Math.Floor(_downOffset / Step + 0.01) + 1 : (int)Math.Ceiling(_downOffset / Step - 0.01) - 1)
                : (int)Math.Round(target / Step);
            SetOffset(Math.Clamp(index, 0, Math.Max(0, Count - 1)) * Step, animate: true);
            e.Handled = true;
            return;
        }

        // Клик: слайд под указателем (в зазоре между слайдами — ничего).
        var x = e.GetPosition(_track).X;
        var slide = (int)Math.Floor(x / Step);
        if (slide >= 0 && slide < Count && x - slide * Step <= _slideWidth)
        {
            e.Handled = true;
            OpenViewer(slide);
        }
    }

    // ------------------------------------------------------------------ точки

    private void RebuildDots()
    {
        _dots.Children.Clear();
        for (var i = 0; i < Count; i++)
        {
            var index = i;
            var hit = new Border { Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = new Border() };
            ((Border)hit.Child!).HorizontalAlignment = HorizontalAlignment.Center;
            ((Border)hit.Child).VerticalAlignment = VerticalAlignment.Center;
            hit.PointerReleased += (_, e) => { GoTo(index); e.Handled = true; };
            hit.PointerPressed += (_, e) => e.Handled = true;
            Avalonia.Automation.AutomationProperties.SetName(hit, $"{index + 1} / {Count}");
            _dots.Children.Add(hit);
        }
    }

    private static void StyleDot(Border hit, bool active)
    {
        var dot = (Border)hit.Child!;
        var size = active ? 8 : 6;
        dot.Width = dot.Height = size;
        dot.CornerRadius = new CornerRadius(size / 2.0);
        MarketplaceDetailUi.Bind(dot, Border.BackgroundProperty, active ? "BrushText" : "BrushBorderStrong");
    }

    // ------------------------------------------------------------------ просмотр на весь экран

    private void OpenViewer(int index)
    {
        var shown = _cells.Where(c => !c.Failed).ToList();
        if (index < 0 || index >= shown.Count)
            return;
        var loaded = shown.Where(c => c.Bitmap != null).ToList();
        if (loaded.Count == 0)
            return;
        var start = Math.Max(0, loaded.IndexOf(shown[index]));
        var focusCell = shown[index];
        _viewer = new MarketplaceSlideViewer(loaded.Select(c => (c.Bitmap!, c.Caption)).ToList(), start, () =>
        {
            _viewer = null;
            _hideOverlay();
            // После закрытия фокус — на слайд в ленте.
            Dispatcher.UIThread.Post(() => focusCell.Frame.Focus(), DispatcherPriority.Input);
        });
        _showOverlay(_viewer);
    }
}

/// <summary>Просмотр слайдов на весь экран (слой поверх страницы, не окно): фон 88 % чёрного, крестик, ‹ ›, подпись и «2 / 5».
/// ← → листают, Esc и клик по фону закрывают, колесо листает. Картинки принадлежат ленте — здесь их не освобождают.</summary>
internal sealed class MarketplaceSlideViewer : Grid
{
    private readonly IReadOnlyList<(Bitmap Bitmap, string Caption)> _slides;
    private readonly Action _close;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _caption = new() { FontSize = 14, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 900 };
    private readonly TextBlock _counter = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), TextAlignment = TextAlignment.Center };
    private readonly Control _prev;
    private readonly Control _next;
    private readonly Border _frame = new();
    private int _index;

    public MarketplaceSlideViewer(IReadOnlyList<(Bitmap Bitmap, string Caption)> slides, int start, Action close)
    {
        _slides = slides;
        _close = close;
        _index = Math.Clamp(start, 0, slides.Count - 1);
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0));
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this, Tr.T("Просмотр слайда", "Слайдды көрүү", "Slide viewer", "Slayt görüntüleyici", "Slaydni ko'rish"));

        _frame.Margin = new Thickness(48, 48, 48, 104);
        _frame.Child = _image;
        _image.PointerPressed += (_, e) => e.Handled = true; // клик по самому слайду не закрывает просмотр
        Children.Add(_frame);

        var close40 = MarketplaceDetailUi.RoundButton("close", 40, 20, MarketplaceDetailUi.Word("close"), _close, onDark: true);
        close40.HorizontalAlignment = HorizontalAlignment.Right;
        close40.VerticalAlignment = VerticalAlignment.Top;
        close40.Margin = new Thickness(0, 16, 16, 0);
        Children.Add(close40);

        _prev = MarketplaceDetailUi.RoundButton("chevron-left", 48, 26, MarketplaceDetailUi.Word("prev"), () => Move(-1), onDark: true);
        _next = MarketplaceDetailUi.RoundButton("chevron-right", 48, 26, MarketplaceDetailUi.Word("next"), () => Move(1), onDark: true);
        _prev.HorizontalAlignment = HorizontalAlignment.Left;
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _prev.Margin = new Thickness(16, 0, 0, 40);
        _next.Margin = new Thickness(0, 0, 16, 40);
        Children.Add(_prev);
        Children.Add(_next);

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(72, 0, 72, 24), Children = { _caption, _counter } };
        info.IsHitTestVisible = false;
        Children.Add(info);

        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _close(); // клик по фону
            }
        };
        PointerWheelChanged += (_, e) =>
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
            if (delta != 0)
                Move(delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left: Move(-1); e.Handled = true; break;
                case Key.Right: Move(1); e.Handled = true; break;
                case Key.Escape: _close(); e.Handled = true; break; // Esc закрывает только просмотр, со страницы не уводит
            }
        };
        SizeChanged += (_, e) =>
        {
            var side = e.NewSize.Width < 700 ? 24 : 48;
            _frame.Margin = new Thickness(side, side, side, side + 56);
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        Show();
    }

    private void Move(int step)
    {
        var next = _index + step;
        if (next < 0 || next >= _slides.Count)
            return;
        _index = next;
        Show();
    }

    private void Show()
    {
        var (bitmap, caption) = _slides[_index];
        _image.Source = bitmap;
        _caption.Text = caption;
        _caption.IsVisible = !string.IsNullOrWhiteSpace(caption);
        _counter.Text = $"{_index + 1} / {_slides.Count}";
        _prev.IsVisible = _index > 0;
        _next.IsVisible = _index < _slides.Count - 1;
    }
}
