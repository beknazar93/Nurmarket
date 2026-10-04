using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using NurMarketKassa.Services;
using ZXing;
using ZXing.Common;
using Camera = Android.Hardware.Camera;

#pragma warning disable CS0618, CA1422 // android.hardware.Camera (Camera1) устарел, но есть на всех аппаратах и не требует AndroidX

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, владелец: «доступ к камере добавь в Android, чтобы его как сканер тоже можно было
/// использовать в кассе, и ещё при создании товаров, приёмке товаров», «и в админке тоже».
/// Камера аппарата как сканер штрихкодов: отдельный экран Android (камера + рамка + фонарик), код
/// распознаёт ZXing (без интернета и без сервисов Google — их нет на многих кассовых терминалах).
/// Считанный код касса получает как скан USB-сканера (CameraScan → AvaloniaKeyboardWedgeBarcodeService).</summary>
internal static class AndroidCameraScanner
{
    internal const int PermissionRequestCode = 4711;
    private static TaskCompletionSource<string?>? _pending;
    private static TaskCompletionSource<bool>? _permission;

    public static void Install() => NurMarketKassa.AvaloniaHost.Services.CameraScan.Scanner = ScanAsync;

    private static async Task<string?> ScanAsync()
    {
        var activity = AndroidBootstrap.CurrentActivity;
        if (activity is null)
            return null;
        if (activity.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraAny) != true)
        {
            Toast.MakeText(activity, AndroidCrashReport.T("На этом аппарате нет камеры.", "Бул аппаратта камера жок.",
                "This device has no camera.", "Bu cihazda kamera yok.", "Bu qurilmada kamera yo'q."), ToastLength.Long)?.Show();
            return null;
        }

        // 2026-10-04: разрешения во время работы — с Android 6.0; на 5.x камера разрешена при установке.
        if (OperatingSystem.IsAndroidVersionAtLeast(23)
            && activity.CheckSelfPermission(Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            _permission = new TaskCompletionSource<bool>();
            activity.RequestPermissions(new[] { Android.Manifest.Permission.Camera }, PermissionRequestCode);
            if (!await _permission.Task.ConfigureAwait(true))
            {
                Toast.MakeText(activity, AndroidCrashReport.T(
                    "Нет доступа к камере. Разрешите его: Настройки Android → Приложения → NurMarket → Разрешения.",
                    "Камерага уруксат жок. Android жөндөөлөрү → Колдонмолор → NurMarket → Уруксаттар.",
                    "No camera access. Allow it in Android Settings → Apps → NurMarket → Permissions.",
                    "Kamera izni yok. Android Ayarlar → Uygulamalar → NurMarket → İzinler.",
                    "Kameraga ruxsat yo'q. Android sozlamalari → Ilovalar → NurMarket → Ruxsatlar."), ToastLength.Long)?.Show();
                return null;
            }
        }

        _pending?.TrySetResult(null);
        var pending = new TaskCompletionSource<string?>();
        _pending = pending;
        // Программа владельца работает в своём процессе (:owner) — экран камеры нужен в нём же.
        var type = NurMarketKassa.Services.AppMode.IsOwner ? typeof(OwnerScannerActivity) : typeof(ScannerActivity);
        activity.StartActivity(new Intent(activity, type));
        return await pending.Task.ConfigureAwait(true);
    }

    internal static void OnPermissionResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode == PermissionRequestCode)
            _permission?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }

    internal static void Deliver(string? code)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(code);
    }
}

/// <summary>Экран камеры-сканера (обычный Android, без Avalonia): видоискатель, рамка, «Фонарик», «✕».</summary>
[Activity(
    Theme = "@style/NurTheme.NoActionBar",
    Exported = false,
    ExcludeFromRecents = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class ScannerActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    private Camera? _camera;
    private int _cameraId = -1;
    private SurfaceView? _surface;
    private volatile bool _busy;
    private volatile bool _delivered;
    private bool _torch;
    private int _previewWidth;
    private int _previewHeight;
    private Handler? _focusHandler;
    private GradientDrawable? _frameShape;
    private int _frameStrokePx = 6;

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = new List<BarcodeFormat>
            {
                BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
                BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.ITF, BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX,
            },
        },
    };

    private static string T(string ru, string ky, string en, string tr, string uz) => AndroidCrashReport.T(ru, ky, en, tr, uz);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(float v) => (int)(v * density);

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);
        _surface = new SurfaceView(this);
        root.AddView(_surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Рамка, куда наводить штрихкод.
        var frameShape = new GradientDrawable();
        frameShape.SetColor(Color.Transparent);
        frameShape.SetStroke(Dp(3), Color.ParseColor("#3B82F6"));
        frameShape.SetCornerRadius(Dp(14));
        _frameShape = frameShape;
        _frameStrokePx = Dp(4);
        var frame = new View(this) { Background = frameShape };
        root.AddView(frame, new FrameLayout.LayoutParams(Dp(300), Dp(170), GravityFlags.Center));

        // 2026-10-04, владелец: «и анимацию сканера тоже добавь» — красная «лазерная» линия ходит по рамке.
        var laser = new View(this);
        laser.SetBackgroundColor(Color.ParseColor("#EF4444"));
        laser.Elevation = Dp(2);
        root.AddView(laser, new FrameLayout.LayoutParams(Dp(270), Dp(2), GravityFlags.Center));
        var sweep = Android.Animation.ObjectAnimator.OfFloat(laser, "translationY", -Dp(72), Dp(72))!;
        sweep.SetDuration(1400);
        sweep.RepeatCount = Android.Animation.ValueAnimator.Infinite;
        sweep.RepeatMode = Android.Animation.ValueAnimatorRepeatMode.Reverse;
        sweep.SetInterpolator(new Android.Views.Animations.AccelerateDecelerateInterpolator());
        sweep.Start();

        var hint = new TextView(this)
        {
            Text = T("Наведите камеру на штрихкод", "Камераны штрихкодго багыттаңыз", "Point the camera at a barcode",
                "Kamerayı barkoda doğrultun", "Kamerani shtrix-kodga qarating"),
            TextSize = 17,
            Gravity = GravityFlags.Center,
        };
        hint.SetTextColor(Color.White);
        hint.SetShadowLayer(6, 0, 0, Color.Black);
        root.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top)
        {
            TopMargin = Dp(56),
        });

        var close = new Android.Widget.Button(this) { Text = "✕", TextSize = 22 };
        close.Click += (_, _) => Finish();
        root.AddView(close, new FrameLayout.LayoutParams(Dp(64), Dp(64), GravityFlags.Top | GravityFlags.End) { TopMargin = Dp(8), RightMargin = Dp(8) });

        var torch = new Android.Widget.Button(this)
        {
            Text = T("Фонарик", "Чырак", "Flashlight", "Fener", "Fonar"),
            TextSize = 16,
        };
        torch.Click += (_, _) => ToggleTorch();
        root.AddView(torch, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(56), GravityFlags.Bottom | GravityFlags.CenterHorizontal)
        {
            BottomMargin = Dp(40),
        });

        SetContentView(root);
        _surface.Holder?.AddCallback(this);
    }

    public void SurfaceCreated(ISurfaceHolder holder) => StartCamera(holder);

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        if (_camera is null || _cameraId < 0)
            return;
        try
        {
            _camera.SetDisplayOrientation(DisplayOrientation(_cameraId));
        }
        catch { /* повернуть не вышло — изображение будет повёрнуто, сканирует и так */ }
    }

    public void SurfaceDestroyed(ISurfaceHolder holder) => StopCamera();

    private void StartCamera(ISurfaceHolder holder)
    {
        try
        {
            _cameraId = FindBackCamera();
            _camera = Camera.Open(_cameraId);
            var p = _camera!.GetParameters()!;
            var sizes = p.SupportedPreviewSizes?.ToList() ?? new List<Camera.Size>();
            var size = sizes.Where(s => s.Width <= 1280 && s.Height <= 720).OrderByDescending(s => s.Width * s.Height).FirstOrDefault()
                       ?? p.PreviewSize!;
            p.SetPreviewSize(size.Width, size.Height);
            _previewWidth = size.Width;
            _previewHeight = size.Height;
            var modes = p.SupportedFocusModes ?? new List<string>();
            var continuous = modes.Contains(Camera.Parameters.FocusModeContinuousPicture);
            if (continuous)
                p.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            else if (modes.Contains(Camera.Parameters.FocusModeAuto))
                p.FocusMode = Camera.Parameters.FocusModeAuto;
            _camera.SetParameters(p);
            _camera.SetDisplayOrientation(DisplayOrientation(_cameraId));
            _camera.SetPreviewDisplay(holder);
            _camera.SetPreviewCallback(this);
            _camera.StartPreview();
            if (!continuous && modes.Contains(Camera.Parameters.FocusModeAuto))
                StartAutoFocusLoop();
            PosLogger.Log($"Камера-сканер: камера {_cameraId}, кадр {_previewWidth}×{_previewHeight}, фокус {p.FocusMode}.", "CART");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Камера-сканер: камера не открылась — {ex.Message}", "WARNING");
            Toast.MakeText(this, T("Камера не открылась: ", "Камера ачылган жок: ", "The camera did not open: ",
                "Kamera açılmadı: ", "Kamera ochilmadi: ") + ex.Message, ToastLength.Long)?.Show();
            Finish();
        }
    }

    private void StartAutoFocusLoop()
    {
        _focusHandler = new Handler(Looper.MainLooper!);
        void Focus()
        {
            if (_camera is null || _delivered)
                return;
            try { _camera.AutoFocus(null); } catch { /* фокус не поддерживается в этот момент */ }
            _focusHandler?.PostDelayed(Focus, 2000);
        }
        _focusHandler.PostDelayed(Focus, 500);
    }

    private void StopCamera()
    {
        _focusHandler?.RemoveCallbacksAndMessages(null);
        var camera = _camera;
        _camera = null;
        if (camera is null)
            return;
        try
        {
            camera.SetPreviewCallback(null);
            camera.StopPreview();
        }
        catch { /* уже остановлена */ }
        camera.Release();
    }

    private void ToggleTorch()
    {
        if (_camera is null)
            return;
        try
        {
            var p = _camera.GetParameters()!;
            var modes = p.SupportedFlashModes ?? new List<string>();
            if (!modes.Contains(Camera.Parameters.FlashModeTorch))
                return;
            _torch = !_torch;
            p.FlashMode = _torch ? Camera.Parameters.FlashModeTorch : Camera.Parameters.FlashModeOff;
            _camera.SetParameters(p);
        }
        catch { /* фонарик не поддерживается */ }
    }

    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        if (data is null || _busy || _delivered)
            return;
        _busy = true;
        var width = _previewWidth;
        var height = _previewHeight;
        Task.Run(() =>
        {
            try
            {
                // Кадр камеры — «альбомный»; штрихкод, который держат вдоль телефона, в нём стоит вертикально.
                // Сначала как есть, потом повёрнутый на 90° (линейным штрихкодам ориентация важна).
                var result = _reader.Decode(new PlanarYUVLuminanceSource(data, width, height, 0, 0, width, height, false))
                             ?? _reader.Decode(new PlanarYUVLuminanceSource(RotateLuma(data, width, height), height, width, 0, 0, height, width, false));
                if (result is not null && !string.IsNullOrWhiteSpace(result.Text) && IsConfirmed(result.Text.Trim(), result.BarcodeFormat))
                    RunOnUiThread(() => Deliver(result.Text));
            }
            catch
            {
                // кадр не распознан — следующий
            }
            finally
            {
                _busy = false;
            }
        });
    }

    // 2026-10-04, владелец: «баг видел в мобилке касса и ещё в админке тоже». Один и тот же товар камера
    // прочитала как 1726000161518 (ошибка чтения), а со второго раза — 4700000161515 (настоящий). Оба кода
    // сходятся по контрольной цифре EAN-13, поэтому касса завела товар под неверным штрихкодом, а настоящий
    // потом «не находился». По одному смазанному или срезанному краем кадру линейный штрихкод иногда
    // распознаётся как соседний верный по контрольной сумме. Теперь линейный код засчитывается, только когда
    // два кадра подряд дали ОДИН И ТОТ ЖЕ результат (+0,1–0,3 с); QR и DataMatrix с коррекцией ошибок — сразу.
    // Проверка идёт по одному кадру за раз (_busy), поэтому поля без блокировок.
    private const int LinearConfirmations = 2;
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(1.5);
    private string? _candidate;
    private int _candidateHits;
    private DateTime _candidateAtUtc;

    private bool IsConfirmed(string text, BarcodeFormat format)
    {
        if (format is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX)
            return true;
        var now = DateTime.UtcNow;
        if (string.Equals(text, _candidate, StringComparison.Ordinal) && now - _candidateAtUtc < ConfirmWindow)
            _candidateHits++;
        else
        {
            if (_candidate is not null && !string.Equals(text, _candidate, StringComparison.Ordinal))
                PosLogger.Log($"Камера-сканер: кадры разошлись ({_candidate} → {text}) — ждём совпадения.", "CART");
            _candidate = text;
            _candidateHits = 1;
        }
        _candidateAtUtc = now;
        return _candidateHits >= LinearConfirmations;
    }

    /// <summary>Яркость кадра NV21 (первые width×height байт), повёрнутая на 90° по часовой.</summary>
    private static byte[] RotateLuma(byte[] data, int width, int height)
    {
        var rotated = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
                rotated[x * height + (height - 1 - y)] = data[row + x];
        }
        return rotated;
    }

    private void Deliver(string code)
    {
        if (_delivered)
            return;
        _delivered = true;
        try
        {
            using var tone = new ToneGenerator(Android.Media.Stream.Notification, 80);
            tone.StartTone(Tone.PropBeep, 120);
        }
        catch { /* без звука */ }
        AndroidCameraScanner.Deliver(code.Trim());
        // Рамка вспыхивает зелёным — кассир видит, что код считан, и экран закрывается.
        try { _frameShape?.SetStroke(_frameStrokePx, Color.ParseColor("#22C55E")); } catch { /* без вспышки */ }
        new Handler(Looper.MainLooper!).PostDelayed(Finish, 250);
    }

    protected override void OnDestroy()
    {
        StopCamera();
        if (!_delivered)
        {
            _delivered = true;
            AndroidCameraScanner.Deliver(null);
        }
        base.OnDestroy();
    }

    private static int FindBackCamera()
    {
        var info = new Camera.CameraInfo();
        for (var i = 0; i < Camera.NumberOfCameras; i++)
        {
            Camera.GetCameraInfo(i, info);
            if (info.Facing == Android.Hardware.CameraFacing.Back)
                return i;
        }
        return 0;
    }

    private int DisplayOrientation(int cameraId)
    {
        var info = new Camera.CameraInfo();
        Camera.GetCameraInfo(cameraId, info);
        var degrees = (WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0) switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0,
        };
        return info.Facing == Android.Hardware.CameraFacing.Front
            ? (360 - (info.Orientation + degrees) % 360) % 360
            : (info.Orientation - degrees + 360) % 360;
    }
}

/// <summary>Тот же экран камеры для программы владельца — в её процессе (:owner).</summary>
[Activity(
    Theme = "@style/NurTheme.NoActionBar",
    Exported = false,
    ExcludeFromRecents = true,
    Process = ":owner",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class OwnerScannerActivity : ScannerActivity
{
}
