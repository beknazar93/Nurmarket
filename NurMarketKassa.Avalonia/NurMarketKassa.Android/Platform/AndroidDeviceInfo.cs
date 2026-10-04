using System.Text;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-05: сведения об Android-аппарате для кнопки «Скопировать информацию об устройстве»
/// (DeviceInfoReport). Каждая строка — отдельно: что не удалось узнать на этом аппарате, просто пропускается.</summary>
internal static class AndroidDeviceInfo
{
    public static string Collect()
    {
        var sb = new StringBuilder();
        var ctx = Android.App.Application.Context;
        var activity = AndroidBootstrap.CurrentActivity;

        Add(sb, () => $"Аппарат: {Build.Manufacturer} {Build.Model} ({Build.Device}, {Build.Product}), бренд {Build.Brand}");
        Add(sb, () => $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), сборка {Build.Display}");
        Add(sb, () => $"Процессор: {string.Join(", ", Build.SupportedAbis ?? Array.Empty<string>())}; 64-бит процесс: {System.Environment.Is64BitProcess}; " +
                      $"ядер: {System.Environment.ProcessorCount}" + (OperatingSystem.IsAndroidVersionAtLeast(31) ? $"; SoC: {Build.SocManufacturer} {Build.SocModel}" : ""));
        Add(sb, () => $"Страница памяти: {Android.Systems.Os.Sysconf(Android.Systems.OsConstants.ScPagesize)} байт");

        Add(sb, () =>
        {
            var metrics = new Android.Util.DisplayMetrics();
#pragma warning disable CA1422, CS0618
            activity?.WindowManager?.DefaultDisplay?.GetRealMetrics(metrics);
#pragma warning restore CA1422, CS0618
            var w = metrics.WidthPixels / (double)metrics.Xdpi;
            var h = metrics.HeightPixels / (double)metrics.Ydpi;
            return $"Экран: {metrics.WidthPixels}×{metrics.HeightPixels} px, {metrics.DensityDpi} dpi (×{metrics.Density:0.##}), " +
                   $"физически {metrics.Xdpi:0}×{metrics.Ydpi:0} dpi ≈ {Math.Sqrt(w * w + h * h):0.0}\", " +
                   $"{metrics.WidthPixels / metrics.Density:0}×{metrics.HeightPixels / metrics.Density:0} точек";
        });
        Add(sb, () =>
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(23))
                return null;
#pragma warning disable CA1422
            var display = activity?.WindowManager?.DefaultDisplay;
#pragma warning restore CA1422
            if (display is null)
                return null;
            var modes = display.GetSupportedModes() ?? Array.Empty<Display.Mode>();
            var list = string.Join(", ", modes.Select(m => $"{m.PhysicalWidth}×{m.PhysicalHeight}@{m.RefreshRate:0}"));
            return $"Частота экрана: сейчас {display.RefreshRate:0} Гц; режимы: {list}";
        });
        Add(sb, () =>
        {
            var peak = Android.Provider.Settings.System.GetString(ctx.ContentResolver, "peak_refresh_rate");
            var min = Android.Provider.Settings.System.GetString(ctx.ContentResolver, "min_refresh_rate");
            return peak is null && min is null ? null : $"Системный предел частоты: от {min ?? "—"} до {peak ?? "—"} Гц";
        });
        Add(sb, () =>
        {
            if (ctx.GetSystemService(Context.DisplayService) is not Android.Hardware.Display.DisplayManager dm)
                return null;
            var all = dm.GetDisplays() ?? Array.Empty<Display>();
            return all.Length <= 1 ? "Второй дисплей: нет" : $"Дисплеев: {all.Length} — " + string.Join("; ", all.Select(d => $"#{d.DisplayId} «{d.Name}»"));
        });
        Add(sb, () => $"Масштаб кассы: {(ScreenFitHost.Current?.Scale ?? 1) * 100:0}% (вид программы подогнан под экран)");

        Add(sb, () =>
        {
            if (ctx.GetSystemService(Context.ActivityService) is not ActivityManager am)
                return null;
            var info = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(info);
            return $"Память: всего {info.TotalMem / 1048576} МБ, свободно {info.AvailMem / 1048576} МБ{(info.LowMemory ? " (мало!)" : "")}; " +
                   $"лимит программы {am.MemoryClass} МБ (large {am.LargeMemoryClass} МБ); программа сейчас {System.Environment.WorkingSet / 1048576} МБ";
        });
        Add(sb, () =>
        {
            var stat = new StatFs(ctx.FilesDir?.AbsolutePath ?? "/data");
            return $"Место: свободно {stat.AvailableBytes / 1048576} МБ из {stat.TotalBytes / 1048576} МБ";
        });
        Add(sb, () =>
        {
            var pm = ctx.PackageManager;
            if (pm is null)
                return null;
            var features = new[]
            {
                ("камера", Android.Content.PM.PackageManager.FeatureCameraAny),
                ("сенсор", Android.Content.PM.PackageManager.FeatureTouchscreen),
                ("USB-хост", Android.Content.PM.PackageManager.FeatureUsbHost),
                ("Bluetooth", Android.Content.PM.PackageManager.FeatureBluetooth),
                ("NFC", Android.Content.PM.PackageManager.FeatureNfc),
            };
            return "Есть: " + string.Join(", ", features.Where(f => pm.HasSystemFeature(f.Item2)).Select(f => f.Item1));
        });
        Add(sb, () =>
        {
            var version = ctx.PackageManager?.GetPackageInfo(ctx.PackageName ?? "", 0);
#pragma warning disable CA1422, CS0618
            return version is null ? null : $"APK: {version.VersionName} (код {version.VersionCode}), установлен {DateTimeOffset.FromUnixTimeMilliseconds(version.LastUpdateTime).ToLocalTime():dd.MM.yyyy HH:mm}";
#pragma warning restore CA1422, CS0618
        });

        return sb.ToString().TrimEnd();
    }

    private static void Add(StringBuilder sb, Func<string?> part)
    {
        try
        {
            if (part() is { Length: > 0 } text)
                sb.AppendLine(text);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(не получено: {ex.GetType().Name})");
        }
    }
}
