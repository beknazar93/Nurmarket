using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-20, владелец: «в маркете при нажатии на фото товара сделай, чтобы он увеличивался для просмотра».
/// Одно фото товара для просмотрщика: полноразмерная ссылка, ссылка на миниатюру и путь к уже лежащей в кэше миниатюре (плитка каталога).</summary>
public sealed record ViewerPhoto(string? FullUrl, string? ThumbUrl, string? LocalThumbPath);

/// <summary>Загрузка файла с картинкой (путь на диске) для просмотрщика; null — не получилось. В тестах подменяется.</summary>
public delegate Task<string?> PhotoPathLoader(ViewerPhoto photo, CancellationToken ct);

/// <summary>Круглая кнопка поверх фото (✕, стрелки, лупа): тёмная полупрозрачная подложка, белый значок. Свой шаблон — чтобы при наведении
/// тема Button не заливала её светлым и белый значок не пропадал.</summary>
public class PhotoOverlayButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public PhotoOverlayButton(double size = 48)
    {
        Width = size;
        Height = size;
        MinWidth = size;
        MinHeight = size;
        Padding = new Thickness(0);
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        Foreground = Brushes.White;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Template = new FuncControlTemplate<Button>((owner, _) => new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x14, 0x14)),
            CornerRadius = new CornerRadius(size / 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            [!ContentPresenter.ContentProperty] = owner[!ContentControl.ContentProperty],
        });
        PointerEntered += (_, _) => Opacity = 1;
        PointerExited += (_, _) => Opacity = 0.85;
        Opacity = 0.85;
    }

    /// <summary>Значок-путь (Material) нужного размера, белый.</summary>
    public static PathIcon Icon(string data, double size = 22) => new()
    {
        Width = size,
        Height = size,
        Foreground = Brushes.White,
        Data = Geometry.Parse(data),
    };

    public const string ZoomInPath = "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14zm2.5-4h-2v2H9v-2H7V9h2V7h1v2h2v1z";
    public const string ClosePath = "M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";
    public const string ChevronLeftPath = "M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z";
    public const string ChevronRightPath = "M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z";
}

/// <summary>Общий просмотрщик фото товара (владелец 20.11.2026): фото на максимальный размер (Uniform) внутри рамки-карточки, закрытие кликом
/// по затемнённому полю вне рамки, кнопкой ✕ и Esc;
/// 2026-11-21, владелец: «при нажатии на фото в товаре не на всю страницу! сделай рамку и хотя бы 70% экрана» — окно по-прежнему на весь экран, но
/// прозрачное и затемнённое, а видимый просмотр — скруглённая карточка с тенью (80 % × 85 % экрана, по центру; заголовок с названием и ✕ сверху,
/// счётчик/точки/подсказка снизу). Размер задают доли строк/столбцов сетки, поэтому он один для всех мест входа и сам следует за размером экрана; колесо мыши / щипок — масштаб, перетаскивание при увеличении; двойное нажатие — 1× ↔ 2,5×; у товара несколько фото —
/// стрелки ←/→, свайп и точки; под фото — название. Пока грузится полноразмерное (ProductThumbService.GetFullPathAsync, кэш product_thumbs/full),
/// показывается миниатюра; ошибка загрузки — понятная надпись, окно не падает без сети. Окно собрано в коде и на Android-оболочке работает слоем
/// (только API, которые есть в Window оболочки: WindowState, SystemDecorations, Opened, ShowDialog).</summary>
public sealed class ProductPhotoViewerWindow : Window
{
    private const double MaxScale = 8;
    private const double DoubleTapScale = 2.5;
    private const double ClickSlop = 6;
    private const double SwipeDistance = 70;

    private sealed class Slot
    {
        public ViewerPhoto Photo = null!;
        public Bitmap? Thumb;
        public Bitmap? Full;
        public bool FullFailed;
        public bool FullLoading;
        public bool ThumbLoading;
    }

    private readonly List<Slot> _slots = new();
    private int _index;
    private readonly string _title;
    private readonly PhotoPathLoader _fullLoader;
    private readonly PhotoPathLoader _thumbLoader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ViewerPhoto>?>>? _galleryLoader;
    private readonly CancellationTokenSource _cts = new();

    // 2026-11-21: рамка-карточка по центру затемнённого окна (доли сетки 1:8:1 по ширине и 0,75:8,5:0,75 по высоте = 80 % × 85 % экрана).
    private readonly Grid _root = new() { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), ColumnDefinitions = new ColumnDefinitions("*,8*,*"), RowDefinitions = new RowDefinitions("0.75*,8.5*,0.75*") };
    private readonly Border _card = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), ClipToBounds = true, BoxShadow = BoxShadows.Parse("0 16 48 0 #99000000") };
    private bool _pressedOutside;

    private readonly Panel _stage = new() { Background = Brushes.Transparent, ClipToBounds = true, Focusable = false };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly TextBlock _titleText = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counter = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusPill = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6), Margin = new Thickness(16, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), IsVisible = false, IsHitTestVisible = false };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    private readonly TextBlock _errorText = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), IsVisible = false, IsHitTestVisible = false };
    private readonly PhotoOverlayButton _prev = new(52);
    private readonly PhotoOverlayButton _next = new(52);
    private readonly PhotoOverlayButton _close = new(48);

    // масштаб относительно «вписано в окно» (1 = целиком), сдвиг в DIP от левого верхнего угла сцены
    private double _scale = 1, _tx, _ty;
    private readonly Dictionary<int, Point> _pointers = new();
    private Point _downPoint;
    private (double Tx, double Ty) _dragOrigin;
    private bool _moved, _pinching;
    private double _pinchDist, _pinchScale;

    // Для проверок: что сейчас на экране.
    public int Index => _index;
    public int Count => _slots.Count;
    public double Scale => _scale;
    public bool ShowsFullSize => _slots.Count > _index && _slots[_index].Full is not null;
    public bool ShowsError => _errorText.IsVisible;
    public bool IsLoadingFull => _slots.Count > _index && _slots[_index].FullLoading;
    public Image PhotoImage => _image;
    public Control Stage => _stage;
    /// <summary>Рамка-карточка просмотра (для проверок размера и места).</summary>
    public Control Card => _card;

    public ProductPhotoViewerWindow(string title, IReadOnlyList<ViewerPhoto> photos, int startIndex = 0, PhotoPathLoader? fullLoader = null,
        PhotoPathLoader? thumbLoader = null, Func<CancellationToken, Task<IReadOnlyList<ViewerPhoto>?>>? galleryLoader = null)
    {
        _title = title ?? "";
        _fullLoader = fullLoader ?? DefaultFullLoader;
        _thumbLoader = thumbLoader ?? DefaultThumbLoader;
        _galleryLoader = galleryLoader;
        foreach (var p in photos)
            _slots.Add(new Slot { Photo = p });
        if (_slots.Count == 0)
            _slots.Add(new Slot { Photo = new ViewerPhoto(null, null, null) });
        _index = Math.Clamp(startIndex, 0, _slots.Count - 1);

        Title = _title;
        WindowState = WindowState.FullScreen;
        SystemDecorations = SystemDecorations.None;
        // 2026-11-21: окно прозрачное (как остальные диалоги), затемнение и рамку рисует содержимое; без прозрачности (слабое устройство) — просто тёмный фон.
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));
        ShowInTaskbar = false;
        Focusable = true;

        BuildLayout();
        WireInput();
        ShowCurrent();

        Opened += async (_, _) =>
        {
            Focus();
            if (_galleryLoader is not null)
                await LoadGalleryAsync().ConfigureAwait(true);
        };
        Closed += (_, _) => Cleanup();
    }

    // ---------------------------------------------------------------- разметка

    private void BuildLayout()
    {
        ToolTip.SetTip(_close, Tr.T("Закрыть (Esc)", "Жабуу (Esc)", "Close (Esc)", "Kapat (Esc)", "Yopish (Esc)"));
        Avalonia.Automation.AutomationProperties.SetName(_close, Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.Content = PhotoOverlayButton.Icon(PhotoOverlayButton.ClosePath);
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.VerticalAlignment = VerticalAlignment.Center;
        _close.Margin = new Thickness(12, 0, 0, 0);
        _close.Click += (_, _) => Close();

        _prev.Content = PhotoOverlayButton.Icon(PhotoOverlayButton.ChevronLeftPath, 28);
        _prev.HorizontalAlignment = HorizontalAlignment.Left;
        _prev.VerticalAlignment = VerticalAlignment.Center;
        _prev.Margin = new Thickness(12, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(_prev, Tr.T("Предыдущее фото", "Мурунку сүрөт", "Previous photo", "Önceki fotoğraf", "Oldingi rasm"));
        _prev.Click += (_, _) => Step(-1);
        _next.Content = PhotoOverlayButton.Icon(PhotoOverlayButton.ChevronRightPath, 28);
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _next.VerticalAlignment = VerticalAlignment.Center;
        _next.Margin = new Thickness(0, 0, 12, 0);
        Avalonia.Automation.AutomationProperties.SetName(_next, Tr.T("Следующее фото", "Кийинки сүрөт", "Next photo", "Sonraki fotoğraf", "Keyingi rasm"));
        _next.Click += (_, _) => Step(1);

        _image.HorizontalAlignment = HorizontalAlignment.Stretch;
        _image.VerticalAlignment = VerticalAlignment.Stretch;
        _image.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        _statusPill.Child = _statusText;
        _stage.Children.Add(_image);
        _stage.Children.Add(_errorText);
        _stage.Children.Add(_statusPill);
        _stage.Children.Add(_prev);
        _stage.Children.Add(_next);

        // Цвета — из темы кассы (карточка светлая или тёмная вместе с программой).
        _card.Bind(Border.BackgroundProperty, _card.GetResourceObservable("BrushDialogPanel"));
        _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("BrushBorderStrong"));
        _titleText.Bind(TextBlock.ForegroundProperty, _titleText.GetResourceObservable("BrushText"));
        _errorText.Bind(TextBlock.ForegroundProperty, _errorText.GetResourceObservable("BrushText"));
        _counter.Bind(TextBlock.ForegroundProperty, _counter.GetResourceObservable("BrushTextMuted"));
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("BrushTextMuted"));
        _hint.Text = OperatingSystem.IsAndroid()
            ? Tr.T("Щипок — масштаб · двойное касание — увеличить · свайп — другое фото", "Чымчуу — масштаб · эки жолу тийүү — чоңойтуу · сүрүү — башка сүрөт",
                "Pinch — zoom · double-tap — magnify · swipe — next photo", "Çimdik — yakınlaştır · çift dokunuş — büyüt · kaydır — sonraki fotoğraf", "Chimdish — masshtab · ikki marta tegish — kattalashtirish · suring — boshqa rasm")
            : Tr.T("Колёсико мыши — масштаб · двойной клик — увеличить · Esc — закрыть", "Чычкан дөңгөлөгү — масштаб · эки жолу басуу — чоңойтуу · Esc — жабуу",
                "Mouse wheel — zoom · double-click — magnify · Esc — close", "Fare tekerleği — yakınlaştır · çift tıklama — büyüt · Esc — kapat", "Sichqoncha g'ildiragi — masshtab · ikki marta bosish — kattalashtirish · Esc — yopish");

        // Шапка карточки: название товара (до двух строк) и ✕.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 8, 8, 4) };
        header.Children.Add(_titleText);
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);

        // Подвал карточки: точки и «2 / 5» в одну строку, под ними подсказка.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        meta.Children.Add(_dots);
        meta.Children.Add(_counter);
        var footer = new StackPanel { Spacing = 2, Margin = new Thickness(20, 2, 20, 10) };
        footer.Children.Add(meta);
        footer.Children.Add(_hint);

        // Фото вписано в сцену с отступом от краёв карточки.
        var stageHost = new Border { Padding = new Thickness(12, 4, 12, 10), Child = _stage };
        ToolTip.SetTip(_titleText, _title); // очень длинное название обрезается двумя строками — целиком видно в подсказке
        var inner = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        inner.Children.Add(header);
        Grid.SetRow(stageHost, 1);
        inner.Children.Add(stageHost);
        Grid.SetRow(footer, 2);
        inner.Children.Add(footer);
        _card.Child = inner;

        Grid.SetColumn(_card, 1);
        Grid.SetRow(_card, 1);
        _root.Children.Add(_card);
        Content = _root;
    }

    // ---------------------------------------------------------------- ввод

    private void WireInput()
    {
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close();
                    break;
                case Key.Left:
                    e.Handled = true;
                    Step(-1);
                    break;
                case Key.Right:
                    e.Handled = true;
                    Step(1);
                    break;
                case Key.Add or Key.OemPlus:
                    e.Handled = true;
                    ZoomAt(StageCenter(), _scale * 1.4);
                    break;
                case Key.Subtract or Key.OemMinus:
                    e.Handled = true;
                    ZoomAt(StageCenter(), _scale / 1.4);
                    break;
                case Key.D0 or Key.NumPad0:
                    e.Handled = true;
                    ResetZoom();
                    break;
            }
        };

        // 2026-11-21: клик по затемнённому полю вне рамки закрывает просмотр (клик внутри рамки — нет, даже рядом с фото).
        _root.PointerPressed += (_, e) => _pressedOutside = !_card.Bounds.Contains(e.GetPosition(_root));
        _root.PointerReleased += (_, e) =>
        {
            var outside = _pressedOutside && !_card.Bounds.Contains(e.GetPosition(_root));
            _pressedOutside = false;
            if (outside)
                Close();
        };

        _stage.SizeChanged += (_, _) => ApplyTransform();
        _stage.PointerPressed += OnStagePressed;
        _stage.PointerMoved += OnStageMoved;
        _stage.PointerReleased += OnStageReleased;
        _stage.PointerCaptureLost += (_, e) => { _pointers.Remove(e.Pointer.Id); if (_pointers.Count < 2) _pinching = false; };
        _stage.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            ZoomAt(e.GetPosition(_stage), _scale * Math.Pow(1.2, e.Delta.Y));
        };
        _stage.DoubleTapped += (_, e) =>
        {
            var p = e.GetPosition(_stage);
            if (!ImageRect().Contains(p))
                return;
            e.Handled = true;
            if (_scale > 1.01)
                ResetZoom();
            else
                ZoomAt(p, DoubleTapScale);
        };
    }

    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_stage);
        _pointers[e.Pointer.Id] = p;
        e.Pointer.Capture(_stage);
        if (_pointers.Count == 1)
        {
            _downPoint = p;
            _dragOrigin = (_tx, _ty);
            _moved = false;
        }
        else if (_pointers.Count == 2)
        {
            var pts = _pointers.Values.ToArray();
            _pinching = true;
            _moved = true; // два пальца — это не «клик мимо фото»
            _pinchDist = Math.Max(1, Distance(pts[0], pts[1]));
            _pinchScale = _scale;
        }
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointers.ContainsKey(e.Pointer.Id))
            return;
        var p = e.GetPosition(_stage);
        _pointers[e.Pointer.Id] = p;
        if (_pinching && _pointers.Count >= 2)
        {
            var pts = _pointers.Values.ToArray();
            var mid = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
            ZoomAt(mid, _pinchScale * Distance(pts[0], pts[1]) / _pinchDist);
            return;
        }
        if (_pointers.Count != 1)
            return;
        var dx = p.X - _downPoint.X;
        var dy = p.Y - _downPoint.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) > ClickSlop)
            _moved = true;
        if (_moved && _scale > 1.01)
        {
            _tx = _dragOrigin.Tx + dx;
            _ty = _dragOrigin.Ty + dy;
            ApplyTransform();
        }
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointers.TryGetValue(e.Pointer.Id, out var last))
            return;
        var p = e.GetPosition(_stage);
        _pointers.Remove(e.Pointer.Id);
        e.Pointer.Capture(null);
        if (_pointers.Count < 2)
            _pinching = false;
        if (_pointers.Count > 0)
            return;

        if (!_moved)
            return; // 2026-11-21: простой клик внутри рамки (и рядом с фото) окно не закрывает — закрывают ✕, Esc и клик вне рамки
        // Свайп по неувеличенному фото — следующее/предыдущее (если фото несколько).
        var dx = p.X - _downPoint.X;
        var dy = p.Y - _downPoint.Y;
        if (_scale <= 1.01 && _slots.Count > 1 && Math.Abs(dx) >= SwipeDistance && Math.Abs(dy) < Math.Abs(dx) / 2)
            Step(dx < 0 ? 1 : -1);
        _ = last;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------------------------------------------------------- масштаб

    private Point StageCenter() => new(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2);

    /// <summary>Размер фото, вписанного в сцену при масштабе 1 (в DIP).</summary>
    private Size FitSize()
    {
        var w = _stage.Bounds.Width;
        var h = _stage.Bounds.Height;
        var src = _image.Source?.Size ?? default;
        if (w <= 0 || h <= 0 || src.Width <= 0 || src.Height <= 0)
            return new Size(w, h);
        var k = Math.Min(w / src.Width, h / src.Height);
        return new Size(src.Width * k, src.Height * k);
    }

    /// <summary>Где фото сейчас на сцене (с учётом масштаба и сдвига) — для «клик мимо фото».</summary>
    private Rect ImageRect()
    {
        var fit = FitSize();
        var x0 = (_stage.Bounds.Width - fit.Width) / 2;
        var y0 = (_stage.Bounds.Height - fit.Height) / 2;
        return new Rect(_scale * x0 + _tx, _scale * y0 + _ty, _scale * fit.Width, _scale * fit.Height);
    }

    private void ZoomAt(Point c, double newScale)
    {
        newScale = Math.Clamp(newScale, 1, MaxScale);
        var k = newScale / _scale;
        _tx = c.X - (c.X - _tx) * k;
        _ty = c.Y - (c.Y - _ty) * k;
        _scale = newScale;
        ApplyTransform();
    }

    private void ResetZoom()
    {
        _scale = 1;
        _tx = 0;
        _ty = 0;
        ApplyTransform();
    }

    private static double ClampAxis(double t, double s, double len, double box)
    {
        var left = s * (box - len) / 2 + t;
        var width = s * len;
        if (width <= box + 0.5)
            return t - left + (box - width) / 2;   // меньше окна — по центру
        if (left > 0)
            return t - left;                        // не отрывать фото от левого/верхнего края
        if (left + width < box)
            return t + (box - (left + width));      // и от правого/нижнего
        return t;
    }

    private void ApplyTransform()
    {
        var fit = FitSize();
        if (_scale <= 1.001)
        {
            _scale = 1;
            _tx = 0;
            _ty = 0;
        }
        else
        {
            _tx = ClampAxis(_tx, _scale, fit.Width, _stage.Bounds.Width);
            _ty = ClampAxis(_ty, _scale, fit.Height, _stage.Bounds.Height);
        }
        _image.RenderTransform = new MatrixTransform(new Matrix(_scale, 0, 0, _scale, _tx, _ty));
        _stage.Cursor = _scale > 1.01 ? new Cursor(StandardCursorType.SizeAll) : null;
    }

    // ---------------------------------------------------------------- показ

    private void Step(int delta)
    {
        if (_slots.Count < 2)
            return;
        _index = (_index + delta + _slots.Count) % _slots.Count;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var s = _slots[_index];
        ResetZoom();
        if (s.Thumb is null && !string.IsNullOrWhiteSpace(s.Photo.LocalThumbPath))
            s.Thumb = DecodeThumb(s.Photo.LocalThumbPath!);
        if (s.Thumb is null && !s.ThumbLoading && !string.IsNullOrWhiteSpace(s.Photo.ThumbUrl ?? s.Photo.FullUrl))
            _ = LoadThumbAsync(s);
        if (s.Full is null && !s.FullFailed && !s.FullLoading)
            _ = LoadFullAsync(s);
        Refresh();
    }

    /// <summary>Перерисовать экран по состоянию текущего фото: картинка, надписи, точки, стрелки.</summary>
    private void Refresh()
    {
        var s = _slots[_index];
        var shown = s.Full ?? s.Thumb;
        _image.Source = shown;
        _image.IsVisible = shown is not null;
        ApplyTransform();

        string? status = null;
        var error = false;
        if (s.Full is null)
        {
            if (s.FullLoading)
                status = Tr.T("Загрузка фото…", "Сүрөт жүктөлүүдө…", "Loading the photo…", "Fotoğraf yükleniyor…", "Rasm yuklanmoqda…");
            else if (s.FullFailed && shown is not null)
                status = Tr.T("Не удалось загрузить крупное фото — показана уменьшенная копия", "Чоң сүрөт жүктөлгөн жок — кичирейтилген түрү көрсөтүлдү",
                    "Could not load the full-size photo — showing a smaller copy", "Büyük fotoğraf yüklenemedi — küçük kopya gösteriliyor", "Katta rasm yuklanmadi — kichik nusxa ko'rsatilmoqda");
            error = s.FullFailed && shown is null;
        }
        _statusText.Text = status;
        _statusPill.IsVisible = status is not null;
        _errorText.Text = Tr.T("Не удалось загрузить фото. Проверьте интернет и откройте его ещё раз.", "Сүрөт жүктөлгөн жок. Интернетти текшерип, кайра ачыңыз.",
            "Could not load the photo. Check your internet and open it again.", "Fotoğraf yüklenemedi. İnternet bağlantınızı kontrol edip tekrar açın.", "Rasm yuklanmadi. Internetni tekshirib, qayta oching.");
        _errorText.IsVisible = error;

        _titleText.Text = _title;
        var many = _slots.Count > 1;
        _counter.IsVisible = many;
        _counter.Text = (_index + 1).ToString(CultureInfo.InvariantCulture) + " / " + _slots.Count.ToString(CultureInfo.InvariantCulture);
        _dots.IsVisible = many;
        _prev.IsVisible = many;
        _next.IsVisible = many;
        _dots.Children.Clear();
        if (many)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                var index = i;
                var dot = new Border
                {
                    Width = 24, Height = 28, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new Ellipse { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                var ellipse = (Ellipse)dot.Child;
                ellipse.Bind(Shape.FillProperty, ellipse.GetResourceObservable(i == _index ? "BrushText" : "BrushBorderStrong"));
                dot.Tapped += (_, e) => { e.Handled = true; _index = index; ShowCurrent(); };
                _dots.Children.Add(dot);
            }
        }
    }

    // ---------------------------------------------------------------- загрузка

    private static Bitmap? DecodeThumb(string path)
    {
        try
        {
            return AssetPathToBitmapConverter.Instance.Convert(path, typeof(Bitmap), "thumb", CultureInfo.InvariantCulture) as Bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadThumbAsync(Slot s)
    {
        s.ThumbLoading = true;
        try
        {
            var path = await _thumbLoader(s.Photo, _cts.Token).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(path) && s.Thumb is null && !_cts.IsCancellationRequested)
                s.Thumb = DecodeThumb(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Просмотр фото: миниатюра не загружена ({ex.GetType().Name}).", "WARNING");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            s.ThumbLoading = false;
        }
        if (!_cts.IsCancellationRequested && ReferenceEquals(_slots[_index], s))
            Refresh();
    }

    private async Task LoadFullAsync(Slot s)
    {
        s.FullLoading = true;
        if (ReferenceEquals(_slots[_index], s))
            Refresh();
        try
        {
            var path = await _fullLoader(s.Photo, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested)
                return;
            Bitmap? bmp = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                bmp = await Task.Run(() => DecodeFull(path)).ConfigureAwait(true);
            if (_cts.IsCancellationRequested)
            {
                bmp?.Dispose();
                return;
            }
            s.Full = bmp;
            s.FullFailed = bmp is null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            s.FullFailed = true;
            PosLogger.Log($"Просмотр фото: крупное фото не загружено ({ex.GetType().Name}).", "WARNING");
        }
        finally
        {
            s.FullLoading = false;
        }
        if (!_cts.IsCancellationRequested && ReferenceEquals(_slots[_index], s))
        {
            // Картинка сменилась (миниатюра → крупное): сохраняем масштаб и сдвиг, чтобы фото не «прыгало», если человек уже увеличил.
            Refresh();
        }
    }

    private static Bitmap? DecodeFull(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // Свой файл (локальное фото из склада) бывает в десятки Мп — расшифровываем уменьшенным; то, что уложил кэш (≤ 1600 px), — как есть.
            return new FileInfo(path).Length > 6L * 1024 * 1024 ? Bitmap.DecodeToWidth(fs, 2400, BitmapInterpolationMode.HighQuality) : new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ответ сервера: все фото карточки. Текущее фото остаётся на экране (масштаб не сбрасываем); новые добавляются к стрелкам и точкам.</summary>
    private async Task LoadGalleryAsync()
    {
        try
        {
            var list = await _galleryLoader!(_cts.Token).ConfigureAwait(true);
            if (list is null || list.Count == 0 || _cts.IsCancellationRequested)
                return;
            ApplyGallery(list);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Просмотр фото: список фото товара не получен ({ex.GetType().Name}).", "WARNING");
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void ApplyGallery(IReadOnlyList<ViewerPhoto> gallery)
    {
        var current = _slots[_index];
        var fresh = new List<Slot>();
        foreach (var g in gallery)
        {
            var existing = _slots.FirstOrDefault(x => !string.IsNullOrWhiteSpace(g.FullUrl)
                && string.Equals(x.Photo.FullUrl, g.FullUrl, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Photo = existing.Photo with { ThumbUrl = existing.Photo.ThumbUrl ?? g.ThumbUrl };
                fresh.Add(existing);
            }
            else
                fresh.Add(new Slot { Photo = g });
        }
        // Показанное сейчас фото не из списка сервера (например, свой файл, ещё не загруженный) — оставляем его первым, ничего не теряем.
        if (!fresh.Contains(current) && (current.Photo.FullUrl is not null || current.Photo.LocalThumbPath is not null))
            fresh.Insert(0, current);
        foreach (var gone in _slots.Where(x => !fresh.Contains(x)))
            gone.Full?.Dispose();
        _slots.Clear();
        _slots.AddRange(fresh);
        _index = _slots.IndexOf(current);
        if (_index < 0)
            _index = 0;
        Refresh();
    }

    private void Cleanup()
    {
        try { _cts.Cancel(); } catch { /* уже закрыто */ }
        _image.Source = null;
        foreach (var s in _slots)
        {
            s.Full?.Dispose();
            s.Full = null;
        }
    }

    // ---------------------------------------------------------------- загрузчики по умолчанию (кэш и сеть кассы)

    private static async Task<string?> DefaultFullLoader(ViewerPhoto p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.FullUrl))
            return p.LocalThumbPath; // своё локальное фото без ссылки — открываем сам файл
        var services = App.AppHost?.Services;
        if (services is null)
            return null;
        var thumbs = services.GetRequiredService<ProductThumbService>();
        var authApi = services.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>();
        var apiBase = services.GetRequiredService<NurMarketKassa.Configuration.AppSettings>().ApiBaseUrl;
        return await thumbs.GetFullPathAsync(authApi, apiBase, p.FullUrl!, ct).ConfigureAwait(true);
    }

    private static async Task<string?> DefaultThumbLoader(ViewerPhoto p, CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(p.ThumbUrl) ? p.ThumbUrl : p.FullUrl;
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var cached = ProductThumbService.TryGetCachedPath(url);
        if (cached is not null)
            return cached;
        var services = App.AppHost?.Services;
        if (services is null)
            return null;
        var thumbs = services.GetRequiredService<ProductThumbService>();
        var authApi = services.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>();
        var apiBase = services.GetRequiredService<NurMarketKassa.Configuration.AppSettings>().ApiBaseUrl;
        return await thumbs.GetThumbPathAsync(authApi, apiBase, url, ct).ConfigureAwait(true);
    }
}

/// <summary>Вход для всех мест, где видно фото товара: <see cref="Open(Visual?, CatalogProductTileVm)"/>. Нет фото — окно не открывается.</summary>
public static class ProductPhotoViewer
{
    /// <summary>У товара есть что показать: ссылка на фото или файл фото в кэше/на диске.</summary>
    /// <summary>Только для проверок: срабатывает, когда просмотр создан (окно ещё не показано).</summary>
    public static Action<ProductPhotoViewerWindow>? WindowCreated { get; set; }

    public static bool HasPhoto(CatalogProductTileVm? product) =>
        product is not null && (!string.IsNullOrWhiteSpace(product.ImageUrl) || !string.IsNullOrWhiteSpace(product.ProductImagePath));

    /// <summary>Открыть просмотр фото товара. Возвращает false, если фото нет (окно не открывается). Окно-владелец — окно, где лежит <paramref name="anchor"/>.</summary>
    public static bool Open(Visual? anchor, CatalogProductTileVm? product)
    {
        if (!HasPhoto(product))
            return false;
        try
        {
            var window = Create(product!);
            WindowCreated?.Invoke(window);
            var owner = anchor is null ? null : TopLevel.GetTopLevel(anchor) as Window;
            if (owner is not null)
                _ = window.ShowDialog(owner);
            else
                window.Show();
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Просмотр фото товара не открылся: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return false;
        }
    }

    /// <summary>Окно просмотра для товара (отдельно — чтобы тесты и нестандартные места могли показать его сами).</summary>
    public static ProductPhotoViewerWindow Create(CatalogProductTileVm product, PhotoPathLoader? fullLoader = null, PhotoPathLoader? thumbLoader = null)
    {
        var first = new ViewerPhoto(
            string.IsNullOrWhiteSpace(product.ImageUrl) ? null : product.ImageUrl,
            null,
            string.IsNullOrWhiteSpace(product.ProductImagePath) ? null : product.ProductImagePath);
        var id = product.Id;
        return new ProductPhotoViewerWindow(product.Title ?? "", new[] { first }, 0, fullLoader, thumbLoader,
            string.IsNullOrWhiteSpace(id) ? null : ct => LoadGalleryAsync(id, ct));
    }

    /// <summary>Все фото карточки с сервера (images[].image_url + image_thumb_url). Нет сети / нет API — null, остаётся одно фото плитки.</summary>
    private static async Task<IReadOnlyList<ViewerPhoto>?> LoadGalleryAsync(string productId, CancellationToken ct)
    {
        var api = App.CatalogApi;
        var apiBase = App.AppHost?.Services.GetService<NurMarketKassa.Configuration.AppSettings>()?.ApiBaseUrl;
        if (api is null || string.IsNullOrWhiteSpace(apiBase))
            return null;
        var detail = await api.ProductsDetailAsync(productId, ct).ConfigureAwait(true);
        if (detail is not JsonElement el)
            return null;
        var refs = ProductImageUrl.ExtractGallery(el, apiBase);
        return refs.Select(r => new ViewerPhoto(r.Url, r.ThumbUrl, null)).ToList();
    }
}
