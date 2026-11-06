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

    /// <param name="previousPath">Прежний выбранный файл этой настройки. 2026-11-06: В4 А5-14 — если он лежит в папке копий и выбран новый, прежняя копия
    /// удаляется (раньше копии QR, фона и рекламы копились при каждом выборе).</param>
    public static async Task<string?> PermanentPathAsync(IStorageFile? file, string? previousPath = null)
    {
        if (file is null)
            return null;
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
            return local;
        var copy = await CopyAsync(file, PermanentFolder).ConfigureAwait(true);
        if (copy is not null)
            DeleteImported(previousPath, copy);
        return copy;
    }

    /// <summary>2026-11-06: В4 А5-14 — предел размера копии: видео рекламы на сотни МБ заполняло флеш-память терминала.</summary>
    internal const long MaxCopyBytes = 50L * 1024 * 1024;

    /// <summary>Удалить прежнюю копию, но только из нашей папки копий и только если это не только что созданный файл.</summary>
    private static void DeleteImported(string? previousPath, string keep)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previousPath) || string.Equals(previousPath, keep, StringComparison.OrdinalIgnoreCase))
                return;
            var folder = Path.GetFullPath(PermanentFolder) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(previousPath).StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(previousPath))
                File.Delete(previousPath);
        }
        catch
        {
            // старая копия не удалилась — не страшно
        }
    }

    private static async Task<string?> CopyAsync(IStorageFile file, string folder)
    {
        string? target = null;
        try
        {
            // 2026-11-06: В4 А5-14 — слишком большой файл не копируем, а говорим об этом (раньше копировался любой размер).
            ulong? declared = null;
            try { declared = (await file.GetBasicPropertiesAsync().ConfigureAwait(true)).Size; }
            catch { /* размер не сообщили — проверим по ходу копирования */ }
            if (declared is { } size && size > (ulong)MaxCopyBytes)
            {
                TooLarge(file.Name);
                return null;
            }
            Directory.CreateDirectory(folder);
            var ext = Path.GetExtension(file.Name);
            target = Path.Combine(folder, Guid.NewGuid().ToString("N") + (string.IsNullOrEmpty(ext) ? ".bin" : ext));
            await using (var input = await file.OpenReadAsync().ConfigureAwait(true))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(true)) > 0)
                {
                    total += read;
                    if (total > MaxCopyBytes)
                    {
                        output.Close();
                        TryDelete(target);
                        TooLarge(file.Name);
                        return null;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
                }
            }
            return target;
        }
        catch (Exception ex)
        {
            // 2026-11-06: В4 А5-14 — недописанная копия при сбое удаляется (раньше оставалась в папке).
            TryDelete(target);
            PosLogger.Log($"Выбор файла: не удалось прочитать «{file.Name}»: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // занят — удалим при очистке
        }
    }

    private static void TooLarge(string name)
    {
        PosLogger.Log($"Выбор файла: «{name}» больше {MaxCopyBytes / 1048576} МБ — не скопирован.", "WARNING");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => PosDialogs.Warning(null,
            Tr.T($"Файл «{name}» больше {MaxCopyBytes / 1048576} МБ. Выберите файл поменьше (для рекламы — короткое видео или картинку).",
                $"«{name}» файлы {MaxCopyBytes / 1048576} МБ чоң. Кичирээк файл тандаңыз (жарнама үчүн кыска видео же сүрөт).",
                $"The file “{name}” is larger than {MaxCopyBytes / 1048576} MB. Choose a smaller file (a short video or a picture for ads).",
                $"«{name}» dosyası {MaxCopyBytes / 1048576} MB'tan büyük. Daha küçük bir dosya seçin (reklam için kısa video veya görsel).",
                $"«{name}» fayli {MaxCopyBytes / 1048576} MB dan katta. Kichikroq fayl tanlang (reklama uchun qisqa video yoki rasm)."),
            Tr.T("Файл слишком большой", "Файл өтө чоң", "File is too large", "Dosya çok büyük", "Fayl juda katta")));
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
