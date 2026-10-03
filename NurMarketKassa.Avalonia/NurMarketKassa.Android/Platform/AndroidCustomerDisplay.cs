using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, Android-касса: экран покупателя на втором дисплее кассового аппарата.
///
/// В Windows экран покупателя — отдельное окно (CustomerDisplayWindow) на втором мониторе.
/// На Android второй дисплей (задний экран CaravPOS и подобных) доступен через Presentation —
/// окно Android на другом дисплее. Здесь CustomerDisplayWindow кассы (он у нас обычный элемент,
/// см. Shell\Window.cs) кладётся в отдельный AvaloniaView внутри Presentation.
/// Если второго дисплея нет — окно показывается слоем на основном экране (как «предпросмотр»).
/// Второй дисплей сообщается коду кассы как второй монитор (Screens.All), поэтому настройки
/// «Экран покупателя» работают как в Windows. НЕ ПРОВЕРЕНО на устройстве.</summary>
internal static class AndroidCustomerDisplay
{
    private static Activity? _activity;
    private static DisplayManager? _displays;
    private static CustomerPresentation? _presentation;
    private static Window? _shownWindow;

    public static void Initialize(Activity activity)
    {
        _activity = activity;
        _displays = activity.GetSystemService(Context.DisplayService) as DisplayManager;
        Window.ExternalPresenter = TryPresent;
        Window.ExternalCloser = TryDismiss;
        Screens.SecondaryScreenProvider = SecondaryScreen;
        try
        {
            _displays?.RegisterDisplayListener(new Listener(), new Handler(Looper.MainLooper!));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: слежение за дисплеями недоступно — {ex.Message}", "CUSTOMER_DISPLAY");
        }
    }

    private static Display? SecondaryDisplay =>
        _displays?.GetDisplays(DisplayManager.DisplayCategoryPresentation)?.FirstOrDefault();

    /// <summary>Второй дисплей как «второй монитор» для кода кассы (справа от основного).</summary>
    private static Avalonia.Platform.Screen? SecondaryScreen()
    {
        var display = SecondaryDisplay;
        if (display is null)
            return null;
        var metrics = new Android.Util.DisplayMetrics();
#pragma warning disable CA1422, CS0618 // GetRealMetrics устарел на API 31, но есть на всех аппаратах
        display.GetRealMetrics(metrics);
#pragma warning restore CA1422, CS0618
        var scaling = metrics.Density > 0 ? metrics.Density : 1.0;
        var primaryWidth = (int)((WindowLayerHost.Current?.Bounds.Width ?? 1280) * (TopLevel.RealTopLevel?.RenderScaling ?? 1.0));
        var rect = new PixelRect(primaryWidth, 0, metrics.WidthPixels, metrics.HeightPixels);
#pragma warning disable CS0618
        return new Avalonia.Platform.Screen(scaling, rect, rect, false);
#pragma warning restore CS0618
    }

    private static bool IsCustomerWindow(Window window) =>
        window.GetType().Name.Contains("CustomerDisplay", StringComparison.Ordinal)
        && !window.GetType().Name.Contains("Editor", StringComparison.Ordinal);

    private static bool TryPresent(Window window)
    {
        if (!IsCustomerWindow(window) || _activity is null || SecondaryDisplay is not { } display)
            return false;
        try
        {
            TryDismiss(_shownWindow ?? window);
            window.Width = double.NaN;
            window.Height = double.NaN;
            window.HorizontalAlignment = HorizontalAlignment.Stretch;
            window.VerticalAlignment = VerticalAlignment.Stretch;
            _presentation = new CustomerPresentation(_activity, display, window);
            _presentation.Show();
            _shownWindow = window;
            PosLogger.Log($"Android: экран покупателя открыт на дисплее «{display.Name}».", "CUSTOMER_DISPLAY");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: экран покупателя на втором дисплее не открылся — {ex.Message}", "CUSTOMER_DISPLAY");
            _presentation = null;
            _shownWindow = null;
            return false;
        }
    }

    private static bool TryDismiss(Window window)
    {
        if (_presentation is null || !ReferenceEquals(_shownWindow, window))
            return false;
        var presentation = _presentation;
        _presentation = null;
        _shownWindow = null;
        try
        {
            presentation.DetachContent();
            presentation.Dismiss();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: закрытие экрана покупателя — {ex.Message}", "CUSTOMER_DISPLAY");
        }
        return true;
    }

    private sealed class CustomerPresentation : Presentation
    {
        private readonly Window _window;
        private AvaloniaView? _view;

        public CustomerPresentation(Context outerContext, Display display, Window window)
            : base(outerContext, display)
        {
            _window = window;
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _view = new AvaloniaView(Context) { Content = _window };
            SetContentView(_view);
        }

        public void DetachContent()
        {
            if (_view is null)
                return;
            _view.Content = null;
            _view.Dispose();
            _view = null;
        }
    }

    private sealed class Listener : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) => Notify();
        public void OnDisplayChanged(int displayId) { }

        public void OnDisplayRemoved(int displayId)
        {
            if (_shownWindow is { } w && SecondaryDisplay is null)
                TryDismiss(w);
            Notify();
        }

        private static void Notify()
        {
            foreach (var w in WindowLayerHost.Current?.Windows ?? Array.Empty<Window>())
                w.Screens.RaiseChanged();
            _shownWindow?.Screens.RaiseChanged();
        }
    }
}
