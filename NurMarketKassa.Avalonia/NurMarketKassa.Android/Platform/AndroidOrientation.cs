using Android.App;
using Android.Content;
using Android.Content.PM;

namespace NurMarketKassa.Droid;

/// <summary>2026-11-06: В4 А4-20 — ориентацию экрана Android-кассы можно закрепить: авто (как раньше) / только альбомная / только книжная.
/// Планшет на подставке, повёрнутый при оплате, перестраивал главное окно и окно оплаты. Выбор хранится в SharedPreferences Android
/// (настройка именно этого аппарата, в облако не уходит) и применяется сразу, без перезапуска.</summary>
internal static class AndroidOrientation
{
    public const string Auto = "auto";
    public const string Landscape = "landscape";
    public const string Portrait = "portrait";

    private const string PrefsName = "nurmarket_device";
    private const string Key = "orientation";

    public static string Current
    {
        get
        {
            try
            {
                var value = AndroidBootstrap.AppContext.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.GetString(Key, Auto);
                return value is Landscape or Portrait ? value : Auto;
            }
            catch
            {
                return Auto;
            }
        }
    }

    /// <summary>Запомнить выбор и применить к открытому экрану.</summary>
    public static void Set(string mode)
    {
        mode = mode is Landscape or Portrait ? mode : Auto;
        try { AndroidBootstrap.AppContext.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.Edit()?.PutString(Key, mode)?.Apply(); }
        catch { /* выбор не сохранился — применим хотя бы сейчас */ }
        if (AndroidBootstrap.CurrentActivity is { } activity)
            activity.RunOnUiThread(() => Apply(activity));
    }

    public static void Apply(Activity activity)
    {
        try
        {
            // Sensor-варианты: экран разворачивается на 180° (подставка вверх ногами), но не меняет вид «лёжа / стоя».
            activity.RequestedOrientation = Current switch
            {
                Landscape => ScreenOrientation.SensorLandscape,
                Portrait => ScreenOrientation.SensorPortrait,
                _ => ScreenOrientation.Unspecified,
            };
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: ориентация не применена — {ex.Message}", "WARNING");
        }
    }
}
