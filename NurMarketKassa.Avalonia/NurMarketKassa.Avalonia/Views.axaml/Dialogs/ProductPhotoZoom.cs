using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-20, владелец: «при нажатии на фото товара сделай, чтобы он увеличивался для просмотра». Подключение просмотрщика
/// <see cref="ProductPhotoViewer"/> к любому месту, где видно фото товара (DataContext элемента — CatalogProductTileVm):
/// <list type="bullet">
/// <item><c>OpenOnClick</c> — нажатие на фото открывает просмотр (списки склада, окна выбора). Нажатие не доходит до строки/плитки под фото.</item>
/// <item><c>LongPress</c> — для плиток каталога, где нажатие обязано добавлять товар в чек: долгое касание (≈0,45 с, палец/перо) по фото открывает
/// просмотр; обычное нажатие и мышь работают как раньше. После открытия захват касания снимается, и отпускание пальца товар в чек не кладёт.</item>
/// </list></summary>
public static class ProductPhotoZoom
{
    public static readonly AttachedProperty<bool> OpenOnClickProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("OpenOnClick");

    public static readonly AttachedProperty<bool> LongPressProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("LongPress");

    public static bool GetOpenOnClick(Control c) => c.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(Control c, bool value) => c.SetValue(OpenOnClickProperty, value);
    public static bool GetLongPress(Control c) => c.GetValue(LongPressProperty);
    public static void SetLongPress(Control c, bool value) => c.SetValue(LongPressProperty, value);

    /// <summary>Сколько держать палец (мс). Чуть меньше системного «удержания» (500 мс), чтобы просмотр успел раньше контекстного меню плитки.</summary>
    public const int LongPressMs = 450;
    private const double MoveSlop = 12;

    private static long _lastLongPressTicks;

    /// <summary>true — только что фото открыто долгим касанием: контекстное меню плитки (карточка «Подробнее») в этот момент не открываем второй раз.</summary>
    public static bool LongPressJustHandled => Environment.TickCount64 - _lastLongPressTicks < 1500;

    /// <summary>Проверка в тестах: имитировать долгое касание мышью (в программе — только палец и перо).</summary>
    internal static bool AllowMouseLongPress { get; set; }

    static ProductPhotoZoom()
    {
        OpenOnClickProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.Tapped -= OnTapped;
            if (e.NewValue is true)
            {
                c.Tapped += OnTapped;
                c.Cursor = new Cursor(StandardCursorType.Hand);
            }
        });
        LongPressProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            if (e.NewValue is true)
                c.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        });
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not CatalogProductTileVm product || !ProductPhotoViewer.HasPhoto(product))
            return;
        e.Handled = true;
        ProductPhotoViewer.Open(c, product);
    }

    private sealed class Press
    {
        public DispatcherTimer Timer = null!;
        public IPointer Pointer = null!;
        public Point Start;
        public Control Host = null!;
        public Control? Root;
        public EventHandler<PointerReleasedEventArgs>? OnUp;
        public EventHandler<PointerEventArgs>? OnMove;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Press> Active = new();

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control host || host.DataContext is not CatalogProductTileVm product || !ProductPhotoViewer.HasPhoto(product))
            return;
        if (e.Pointer.Type == PointerType.Mouse && !AllowMouseLongPress)
            return; // мышь: у плитки есть кнопка-лупа; долгое нажатие мышью — просто медленный клик
        Cancel(host);
        var press = new Press { Pointer = e.Pointer, Start = e.GetPosition(host), Host = host };
        press.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
        press.Timer.Tick += (_, _) =>
        {
            Cancel(host);
            var p = host.DataContext as CatalogProductTileVm;
            if (!ProductPhotoViewer.HasPhoto(p))
                return;
            _lastLongPressTicks = Environment.TickCount64;
            // Снимаем захват касания у кнопки плитки: палец ещё на экране, и без этого его отпускание нажало бы плитку (товар ушёл бы в чек).
            try { press.Pointer.Capture(null); } catch { /* указатель уже отпущен */ }
            // Страховка: если отпускание этого же пальца всё же дойдёт до окна кассы (Android-оболочка — одно окно, просмотр лежит слоем в нём), глотаем его до того,
            // как оно нажмёт кнопку плитки. Обработчик одноразовый и живёт не дольше 15 с.
            var root = press.Root;
            if (root is not null)
            {
                var pointer = press.Pointer;
                EventHandler<PointerReleasedEventArgs>? swallow = null;
                swallow = (_, ev) =>
                {
                    if (ev.Pointer != pointer)
                        return;
                    ev.Handled = true;
                    root.RemoveHandler(InputElement.PointerReleasedEvent, swallow!);
                };
                root.AddHandler(InputElement.PointerReleasedEvent, swallow, RoutingStrategies.Tunnel, handledEventsToo: true);
                DispatcherTimer.RunOnce(() => root.RemoveHandler(InputElement.PointerReleasedEvent, swallow!), TimeSpan.FromSeconds(15));
            }
            ProductPhotoViewer.Open(host, p);
        };
        // Кнопка плитки забирает захват касания себе, поэтому отпускание и сдвиг до нас через фото не доходят — слушаем их у окна (туннель, до захвата).
        press.Root = TopLevel.GetTopLevel(host);
        press.OnUp = (_, _) => Cancel(host);
        press.OnMove = (_, ev) =>
        {
            if (ev.Pointer == press.Pointer && (ev.GetPosition(host) - press.Start) is var d && Math.Abs(d.X) + Math.Abs(d.Y) > MoveSlop)
                Cancel(host);
        };
        press.Root?.AddHandler(InputElement.PointerReleasedEvent, press.OnUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        press.Root?.AddHandler(InputElement.PointerMovedEvent, press.OnMove, RoutingStrategies.Tunnel, handledEventsToo: true);
        Active.AddOrUpdate(host, press);
        press.Timer.Start();
    }

    private static void Cancel(Control? host)
    {
        if (host is null || !Active.TryGetValue(host, out var press))
            return;
        press.Timer.Stop();
        if (press.OnUp is not null)
            press.Root?.RemoveHandler(InputElement.PointerReleasedEvent, press.OnUp);
        if (press.OnMove is not null)
            press.Root?.RemoveHandler(InputElement.PointerMovedEvent, press.OnMove);
        Active.Remove(host);
    }
}

/// <summary>Маленькая кнопка-лупа в углу фото плитки каталога: открывает просмотр фото и не добавляет товар в чек (нажатие забирает она, не плитка).
/// Видна, если у товара есть фото и фото в каталоге не скрыты настройкой. DataContext — CatalogProductTileVm плитки.</summary>
public sealed class PhotoZoomButton : PhotoOverlayButton
{
    private CatalogProductTileVm? _vm;

    public PhotoZoomButton() : base(32)
    {
        Content = Icon(ZoomInPath, 18);
        Opacity = 0.8;
        ToolTip.SetTip(this, Tr.T("Увеличить фото", "Сүрөттү чоңойтуу", "Enlarge the photo", "Fotoğrafı büyüt", "Rasmni kattalashtirish"));
        Avalonia.Automation.AutomationProperties.SetName(this, Tr.T("Увеличить фото", "Сүрөттү чоңойтуу", "Enlarge the photo", "Fotoğrafı büyüt", "Rasmni kattalashtirish"));
        IsVisible = false;
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unhook();
        AttachedToVisualTree += (_, _) => Rebind();
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (_vm is not null)
            ProductPhotoViewer.Open(this, _vm);
    }

    private void Unhook()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = null;
    }

    private void Rebind()
    {
        Unhook();
        _vm = DataContext as CatalogProductTileVm;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmChanged;
        UpdateVisible();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CatalogProductTileVm.ProductImagePath) or nameof(CatalogProductTileVm.ImageUrl) or null or "")
            UpdateVisible();
    }

    private void UpdateVisible() => IsVisible = UserPreferences.Instance.ShowCatalogPhotos && ProductPhotoViewer.HasPhoto(_vm);
}
