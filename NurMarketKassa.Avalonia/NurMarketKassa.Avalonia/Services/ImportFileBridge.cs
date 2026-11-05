using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №4, А4-06): на Android выбор файла (системное окно SAF) отдаёт content:// без локального пути
/// (TryGetLocalPath = null) — фото товара, QR банка, логотип, реклама экрана покупателя молча не выбирались.
/// Теперь, если локального пути нет, файл читается потоком (OpenReadAsync) и копируется в обычный файл.
/// С локальным путём (Windows/Linux) — прежнее поведение, ничего не копируется.
/// <see cref="TemporaryPathAsync"/> — для разовой загрузки (фото товара на сервер): копия во временной папке, старые копии чистятся.
/// <see cref="PermanentPathAsync"/> — для настроек, где путь хранится (QR банка, фон, реклама): копия в папке данных.</summary>
internal static class ImportFileBridge
{
    private static string TempFolder => Path.Combine(Path.GetTempPath(), "NurCRM-import");

    private static string PermanentFolder => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "imported");

    public static async Task<string?> TemporaryPathAsync(IStorageFile? file)
    {
        if (file is null)
            return null;
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;
        CleanOld(TempFolder, TimeSpan.FromDays(1));
        return await CopyAsync(file, TempFolder).ConfigureAwait(true);
    }

    public static async Task<string?> PermanentPathAsync(IStorageFile? file)
    {
        if (file is null)
            return null;
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;
        return await CopyAsync(file, PermanentFolder).ConfigureAwait(true);
    }

    private static async Task<string?> CopyAsync(IStorageFile file, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var ext = Path.GetExtension(file.Name);
            var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + (string.IsNullOrEmpty(ext) ? ".bin" : ext));
            await using var input = await file.OpenReadAsync().ConfigureAwait(true);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output).ConfigureAwait(true);
            return target;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выбор файла: не удалось прочитать «{file.Name}»: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static void CleanOld(string folder, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var path in Directory.EnumerateFiles(folder).Where(p => DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > age))
            {
                try { File.Delete(path); }
                catch { /* занят — удалим в следующий раз */ }
            }
        }
        catch
        {
            // чистка не должна мешать выбору файла
        }
    }
}
