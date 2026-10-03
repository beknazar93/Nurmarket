using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NurMarketKassa;

/// <summary>2026-10-04, Android-касса: замена Avalonia.Controls.Window.
/// На Android у Avalonia одно окно, а у кассы их ~65. Здесь «окно» — слой внутри
/// <see cref="WindowLayerHost"/>: Show() кладёт его поверх остальных, ShowDialog() — поверх с
/// затемнением и блокировкой нижних слоёв, Close() убирает. API повторяет Avalonia.Controls.Window
/// настолько, насколько его использует код кассы, поэтому окна кассы собираются без изменений.
/// Положение и рамка окна (Position, SystemDecorations, перетаскивание) на Android не имеют
/// смысла и только запоминаются.</summary>
public class WindowBase : TopLevel
{
    public static readonly StyledProperty<bool> TopmostProperty =
        AvaloniaProperty.Register<WindowBase, bool>(nameof(Topmost));

    public bool Topmost
    {
        get => GetValue(TopmostProperty);
        set => SetValue(TopmostProperty, value);
    }

    /// <summary>Владелец (окно, из которого открыто это).</summary>
    public WindowBase? Owner { get; protected internal set; }

    public bool IsActive { get; internal set; }

    public PixelPoint Position { get; set; }
#pragma warning disable CS0067 // окна на Android не двигаются — событие ради совместимости
    public event EventHandler<PixelPointEventArgs>? PositionChanged;
#pragma warning restore CS0067

    public event EventHandler? Activated;
    public event EventHandler? Deactivated;

    internal void SetActive(bool active)
    {
        if (IsActive == active)
            return;
        IsActive = active;
        if (active)
            Activated?.Invoke(this, EventArgs.Empty);
        else
            Deactivated?.Invoke(this, EventArgs.Empty);
    }

    public virtual void Activate() => WindowLayerHost.Current?.BringToFront((Window)this);
}

public class Window : WindowBase
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Window, string?>(nameof(Title), "Window");
    public static readonly StyledProperty<SizeToContent> SizeToContentProperty =
        AvaloniaProperty.Register<Window, SizeToContent>(nameof(SizeToContent));
    public static readonly StyledProperty<WindowState> WindowStateProperty =
        AvaloniaProperty.Register<Window, WindowState>(nameof(WindowState));
    public static readonly StyledProperty<SystemDecorations> SystemDecorationsProperty =
        AvaloniaProperty.Register<Window, SystemDecorations>(nameof(SystemDecorations), SystemDecorations.Full);
    public static readonly StyledProperty<bool> CanResizeProperty =
        AvaloniaProperty.Register<Window, bool>(nameof(CanResize), true);
    public static readonly StyledProperty<bool> ShowInTaskbarProperty =
        AvaloniaProperty.Register<Window, bool>(nameof(ShowInTaskbar), true);
    public static readonly StyledProperty<bool> ExtendClientAreaToDecorationsHintProperty =
        AvaloniaProperty.Register<Window, bool>(nameof(ExtendClientAreaToDecorationsHint));
    public static readonly StyledProperty<Avalonia.Platform.ExtendClientAreaChromeHints> ExtendClientAreaChromeHintsProperty =
        AvaloniaProperty.Register<Window, Avalonia.Platform.ExtendClientAreaChromeHints>(nameof(ExtendClientAreaChromeHints));
    public static readonly StyledProperty<double> ExtendClientAreaTitleBarHeightHintProperty =
        AvaloniaProperty.Register<Window, double>(nameof(ExtendClientAreaTitleBarHeightHint), -1);
    public static readonly StyledProperty<bool> ShowActivatedProperty =
        AvaloniaProperty.Register<Window, bool>(nameof(ShowActivated), true);

    public static readonly StyledProperty<Thickness> OffScreenMarginProperty =
        AvaloniaProperty.Register<Window, Thickness>(nameof(OffScreenMargin));
    public static readonly StyledProperty<Thickness> WindowDecorationMarginProperty =
        AvaloniaProperty.Register<Window, Thickness>(nameof(WindowDecorationMargin));
    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<Window, object?>(nameof(Icon));
    public static readonly StyledProperty<WindowStartupLocation> WindowStartupLocationProperty =
        AvaloniaProperty.Register<Window, WindowStartupLocation>(nameof(WindowStartupLocation));

    public static readonly RoutedEvent<RoutedEventArgs> WindowOpenedEvent =
        RoutedEvent.Register<Window, RoutedEventArgs>("WindowOpened", RoutingStrategies.Direct);
    public static readonly RoutedEvent<RoutedEventArgs> WindowClosedEvent =
        RoutedEvent.Register<Window, RoutedEventArgs>("WindowClosed", RoutingStrategies.Direct);

    private TaskCompletionSource<object?>? _dialogResult;
    private bool _closing;

    public Window()
    {
        // Фон, цвет текста и шрифт окна — как у стиля Selector="Window" в App.axaml Windows-кассы.
        this[!BackgroundProperty] = this.GetResourceObservable("BrushWindowBackdrop").ToBinding();
        this[!ForegroundProperty] = this.GetResourceObservable("BrushText").ToBinding();
        this[!FontFamilyProperty] = this.GetResourceObservable("AppFontFamily").ToBinding();
        this[!FontSizeProperty] = this.GetResourceObservable("AppFontSize").ToBinding();
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        ClipToBounds = true;
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SizeToContent SizeToContent
    {
        get => GetValue(SizeToContentProperty);
        set => SetValue(SizeToContentProperty, value);
    }

    public WindowState WindowState
    {
        get => GetValue(WindowStateProperty);
        set => SetValue(WindowStateProperty, value);
    }

    public SystemDecorations SystemDecorations
    {
        get => GetValue(SystemDecorationsProperty);
        set => SetValue(SystemDecorationsProperty, value);
    }

    public bool CanResize
    {
        get => GetValue(CanResizeProperty);
        set => SetValue(CanResizeProperty, value);
    }

    public bool ShowInTaskbar
    {
        get => GetValue(ShowInTaskbarProperty);
        set => SetValue(ShowInTaskbarProperty, value);
    }

    public bool ShowActivated
    {
        get => GetValue(ShowActivatedProperty);
        set => SetValue(ShowActivatedProperty, value);
    }

    public bool ExtendClientAreaToDecorationsHint
    {
        get => GetValue(ExtendClientAreaToDecorationsHintProperty);
        set => SetValue(ExtendClientAreaToDecorationsHintProperty, value);
    }

    public Avalonia.Platform.ExtendClientAreaChromeHints ExtendClientAreaChromeHints
    {
        get => GetValue(ExtendClientAreaChromeHintsProperty);
        set => SetValue(ExtendClientAreaChromeHintsProperty, value);
    }

    public double ExtendClientAreaTitleBarHeightHint
    {
        get => GetValue(ExtendClientAreaTitleBarHeightHintProperty);
        set => SetValue(ExtendClientAreaTitleBarHeightHintProperty, value);
    }

    public bool IsExtendedIntoWindowDecorations => ExtendClientAreaToDecorationsHint;
    public Thickness WindowDecorationMargin => GetValue(WindowDecorationMarginProperty);
    public Thickness OffScreenMargin => GetValue(OffScreenMarginProperty);

    public WindowStartupLocation WindowStartupLocation
    {
        get => GetValue(WindowStartupLocationProperty);
        set => SetValue(WindowStartupLocationProperty, value);
    }

    /// <summary>Значок окна (в Windows — на панели задач); на Android не используется.</summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IReadOnlyList<Window> OwnedWindows =>
        WindowLayerHost.Current?.Windows.Where(w => ReferenceEquals(w.Owner, this)).ToList() ?? new List<Window>();

    /// <summary>Открыто ли окно сейчас (лежит в стеке слоёв).</summary>
    internal bool IsShownInHost { get; set; }

    internal bool IsModalLayer { get; set; }

    /// <summary>Окно показано не слоем, а на втором дисплее (экран покупателя).</summary>
    internal bool IsShownExternally { get; private set; }

    /// <summary>Показ окна вне стопки слоёв (второй дисплей); true — показано там.</summary>
    internal static Func<Window, bool>? ExternalPresenter { get; set; }

    internal static Func<Window, bool>? ExternalCloser { get; set; }

    public event EventHandler<WindowClosingEventArgs>? Closing;
    public event EventHandler<WindowResizedEventArgs>? Resized;

    // ---- Показ ----

    public void Show() => ShowCore(null, modal: false);

    public void Show(Window owner) => ShowCore(owner, modal: false);

    public Task ShowDialog(Window owner) => ShowDialog<object?>(owner);

    public Task<TResult> ShowDialog<TResult>(Window owner)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogResult = tcs;
        ShowCore(owner, modal: true);
        return tcs.Task.ContinueWith(
            t => t.Result is TResult r ? r : default!,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ShowCore(Window? owner, bool modal)
    {
        var host = WindowLayerHost.Current
                   ?? throw new InvalidOperationException("Android: вид программы ещё не создан — окно показать некуда.");
        if (owner is not null)
            Owner = owner;
        if (IsShownInHost)
        {
            IsVisible = true;
            host.BringToFront(this);
            return;
        }

        IsModalLayer = modal;
        _closing = false;
        // Экран покупателя — на второй дисплей аппарата (Android Presentation), если он есть.
        if (!modal && ExternalPresenter?.Invoke(this) == true)
            IsShownExternally = true;
        else
            host.Add(this, modal);
        IsShownInHost = true;

        // Как в Avalonia: Opened — после того, как окно показано и разложено.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsShownInHost)
                return;
            RaiseOpenedCore();
            RaiseEvent(new RoutedEventArgs(WindowOpenedEvent, this));
            if (ShowActivated)
                host.FocusWindow(this);
        }, DispatcherPriority.Loaded);
    }

    public void Hide()
    {
        IsVisible = false;
        if (!IsShownExternally)
            WindowLayerHost.Current?.OnWindowHidden(this);
    }

    public void Close() => CloseCore(null, setResult: false, programmatic: true);

    public void Close(object? dialogResult) => CloseCore(dialogResult, setResult: true, programmatic: true);

    private void CloseCore(object? dialogResult, bool setResult, bool programmatic)
    {
        if (_closing || !IsShownInHost)
        {
            // Окно не показано — Avalonia просто ничего не делает; диалог без показа — завершаем.
            if (!IsShownInHost && _dialogResult is { } pending && !_closing)
            {
                _dialogResult = null;
                pending.TrySetResult(setResult ? dialogResult : null);
            }
            return;
        }

        var args = new WindowClosingEventArgs(WindowCloseReason.WindowClosing, programmatic);
        OnClosing(args);
        if (args.Cancel)
            return;

        _closing = true;

        // Дочерние окна закрываются вместе с владельцем (как в Avalonia).
        foreach (var child in OwnedWindows.ToList())
            child.Close();

        if (IsShownExternally)
            ExternalCloser?.Invoke(this);
        else
            WindowLayerHost.Current?.Remove(this);
        IsShownExternally = false;
        IsShownInHost = false;
        SetActive(false);

        RaiseClosedCore();
        RaiseEvent(new RoutedEventArgs(WindowClosedEvent, this));

        var tcs = _dialogResult;
        _dialogResult = null;
        tcs?.TrySetResult(setResult ? dialogResult : null);
    }

    /// <summary>Запрос закрытия с кнопки «Назад» Android: как крестик окна в Windows
    /// (Closing получает IsProgrammatic = false и может отменить закрытие).</summary>
    internal void RequestCloseFromUser() => CloseCore(null, setResult: false, programmatic: false);

    protected virtual void OnClosing(WindowClosingEventArgs e) => Closing?.Invoke(this, e);

    // ---- Остальное API Avalonia.Controls.Window — без действия на Android ----

    public void BeginMoveDrag(PointerPressedEventArgs e) { }
    public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }

    internal void RaiseResized(Size size) =>
        Resized?.Invoke(this, new WindowResizedEventArgs(size));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && IsShownInHost)
            WindowLayerHost.Current?.Relayout(this);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RaiseResized(e.NewSize);
    }
}

/// <summary>Замена Avalonia.Controls.WindowClosingEventArgs (у той конструктор закрыт).</summary>
public class WindowClosingEventArgs : CancelEventArgs
{
    public WindowClosingEventArgs(WindowCloseReason closeReason, bool isProgrammatic)
    {
        CloseReason = closeReason;
        IsProgrammatic = isProgrammatic;
    }

    public WindowCloseReason CloseReason { get; }
    public bool IsProgrammatic { get; }
}

/// <summary>Замена Avalonia.Controls.WindowResizedEventArgs.</summary>
public class WindowResizedEventArgs : EventArgs
{
    public WindowResizedEventArgs(Size clientSize) => ClientSize = clientSize;
    public Size ClientSize { get; }
    public WindowResizeReason Reason => WindowResizeReason.Unspecified;
}
