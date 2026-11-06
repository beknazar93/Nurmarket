using Android.Content;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-11-06: В4 А5-05 — открыть ссылку или файл из общего кода кассы. В Windows это Process.Start(UseShellExecute), на Android
/// там исключение — WhatsApp, Telegram, сайт поддержки, заказы сайта, BotFather, документы не открывались. Здесь — Intent ACTION_VIEW;
/// файл отдаётся приложению-просмотрщику через FileProvider (прямой file:// с Android 7 запрещён). Подключается в AndroidBootstrap.</summary>
internal static class AndroidLauncher
{
    /// <summary>Должно совпадать с authorities у provider в AndroidManifest.xml.</summary>
    private const string Authority = "kg.nurmarket.kassa.fileprovider";

    public static void Install()
    {
        PlatformLauncher.AndroidOpenUrl = OpenUrl;
        PlatformLauncher.AndroidOpenFile = OpenFile;
        SaleDocumentHtmlBuilder.PlatformOpenFile = OpenFile;
    }

    private static Context Ctx => AndroidBootstrap.CurrentActivity ?? (Context)AndroidBootstrap.AppContext;

    private static bool Start(Intent intent)
    {
        try
        {
            var ctx = Ctx;
            // Из контекста приложения (не активности) запуск требует NewTask.
            if (ctx is not Android.App.Activity)
                intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
            return true;
        }
        catch (Android.Content.ActivityNotFoundException)
        {
            PosLogger.Log("Android: нет программы, которая откроет ссылку или файл.", "WARNING");
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: запуск программы не удался — {ex.Message}", "WARNING");
            return false;
        }
    }

    private static bool OpenUrl(string url)
    {
        try
        {
            var uri = Android.Net.Uri.Parse(url);
            var action = string.Equals(uri?.Scheme, "tel", StringComparison.OrdinalIgnoreCase) ? Intent.ActionDial : Intent.ActionView;
            var intent = new Intent(action, uri);
            intent.AddCategory(Intent.CategoryBrowsable);
            return Start(intent);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: ссылка не открылась — {ex.Message}", "WARNING");
            return false;
        }
    }

    private static bool OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            // FileProvider отдаёт только папки, перечисленные в res/xml/nur_file_paths.xml (кэш программы): копия во временную папку.
            var folder = Path.Combine(Path.GetTempPath(), "NurCRM-open");
            Directory.CreateDirectory(folder);
            CleanOld(folder);
            var shared = path.StartsWith(folder, StringComparison.Ordinal) ? path : Path.Combine(folder, Path.GetFileName(path));
            if (!string.Equals(shared, path, StringComparison.Ordinal))
                File.Copy(path, shared, overwrite: true);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(AndroidBootstrap.AppContext, Authority, new Java.IO.File(shared));
            var ext = Path.GetExtension(shared).TrimStart('.').ToLowerInvariant();
            var mime = (ext.Length > 0 ? Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) : null) ?? "*/*";
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mime);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            return Start(intent);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: файл не открылся — {ex.Message}", "WARNING");
            return false;
        }
    }

    /// <summary>Копии для просмотрщиков старше суток не нужны.</summary>
    private static void CleanOld(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                {
                    try { File.Delete(file); }
                    catch { /* занят просмотрщиком — удалим в следующий раз */ }
                }
            }
        }
        catch
        {
            // чистка не должна мешать открытию
        }
    }
}
