using Android.App;
using Android.Content;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-11-06: В4 А4-28 — «Слабое устройство» (без размытия под окнами, реже опрос сервера) на Android включалось только вручную.
/// Теперь на аппарате с малой памятью (признак системы IsLowRamDevice или не больше ~2,2 ГБ) при первом запуске оно включается само,
/// ОДИН раз: если кассир потом выключит, вернуть его выбор не пытаемся (флаг в SharedPreferences Android).</summary>
internal static class AndroidLowRam
{
    private const string PrefsName = "nurmarket_device";
    private const string AppliedKey = "lowram_applied";
    private const long LowRamBytes = 2_300L * 1024 * 1024;

    public static void ApplyOnce(Context context)
    {
        try
        {
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs is null || prefs.GetBoolean(AppliedKey, false))
                return;
            var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
            long total = 0;
            if (manager is not null)
            {
                var info = new ActivityManager.MemoryInfo();
                manager.GetMemoryInfo(info);
                total = info.TotalMem;
            }
            var lowFlag = manager?.IsLowRamDevice ?? false;
            var weak = lowFlag || (total > 0 && total <= LowRamBytes);
            prefs.Edit()?.PutBoolean(AppliedKey, true)?.Apply();
            if (!weak)
                return;
            var preferences = UserPreferences.Instance;
            if (preferences.LowPerformanceMode)
                return;
            preferences.LowPerformanceMode = true;
            preferences.SaveToDisk();
            PosLogger.Log($"Android: слабое устройство (память {total / 1048576} МБ, IsLowRamDevice={lowFlag}) — режим «Слабое устройство» включён сам.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: проверка слабого устройства — {ex.Message}", "WARNING");
        }
    }
}
