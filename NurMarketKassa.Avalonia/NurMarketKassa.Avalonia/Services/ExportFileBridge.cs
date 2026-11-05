using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №3, А3-05): на Android выбор файла отдаёт content:// без локального пути (TryGetLocalPath = null), и выгрузки
/// Excel/Word/CSV либо молча не сохранялись, либо просили «папку на этом компьютере». Теперь: нет локального пути — пишем во временный файл,
/// затем копируем в выбранный файл через OpenWriteAsync (так же, как мастер импорта и ПЛУ весов). С локальным путём (Windows/Linux) —
/// прежнее поведение, ничего не копируется.
/// Использование: <c>var path = ExportFileBridge.PathFor(file);</c> → запись по <c>path</c> → <c>path = await ExportFileBridge.CompleteAsync(file, path);</c>
/// (возвращает подпись для сообщения «сохранён: …»: путь или имя файла).</summary>
internal static class ExportFileBridge
{
    public static string PathFor(IStorageFile file)
    {
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;
        var ext = Path.GetExtension(file.Name);
        return Path.Combine(Path.GetTempPath(), "nurcrm-export-" + Guid.NewGuid().ToString("N") + (string.IsNullOrEmpty(ext) ? ".tmp" : ext));
    }

    public static async Task<string> CompleteAsync(IStorageFile file, string path)
    {
        if (!string.IsNullOrEmpty(file.TryGetLocalPath()))
            return path;
        try
        {
            await using var src = File.OpenRead(path);
            await using var dst = await file.OpenWriteAsync().ConfigureAwait(true);
            await src.CopyToAsync(dst).ConfigureAwait(true);
            await dst.FlushAsync().ConfigureAwait(true);
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) { PosLogger.Log($"Выгрузка: временный файл не удалён: {ex.Message}", "WARNING"); }
        }
        return file.Name;
    }
}
