using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Итог картинок одной выгрузки: «Картинки: M из K, без фото: L».</summary>
/// <param name="Total">Товаров в выгрузке.</param>
/// <param name="Written">Картинок записано в папку весов заново.</param>
/// <param name="Unchanged">Картинка уже лежит на весах и не менялась — не перекачивалась и не пересоздавалась.</param>
/// <param name="NoPhoto">У товара нет фото (ссылки на картинку нет).</param>
/// <param name="Failed">Фото есть, но не скачалось/не открылось/не записалось.</param>
/// <param name="TimedOut">Не хватило времени (лимит на выгрузку) — доедут в следующий раз.</param>
public sealed record ShtrikhPcPictureStats(int Total, int Written, int Unchanged, int NoPhoto, int Failed, int TimedOut)
{
    /// <summary>У скольких товаров на весах будет картинка (записанные + не менявшиеся).</summary>
    public int WithPicture => Written + Unchanged;

    public static readonly ShtrikhPcPictureStats None = new(0, 0, 0, 0, 0, 0);
}

public sealed record ShtrikhPcPictureOutcome(IReadOnlyList<ShtrikhPcItem> Items, ShtrikhPcPictureStats Stats);

/// <summary>Шаг «картинки» внутри записи файла (пока подключена папка весов): принимает товары и возвращает их с кодами ресурсов.</summary>
public delegate Task<ShtrikhPcPictureOutcome> ShtrikhPcPictureStep(IReadOnlyList<ShtrikhPcItem> items, ShtrikhPcSettings settings, CancellationToken ct);

/// <summary>Что уже записано в папки весов (файл состояния компании <c>shtrikhpc-pictures.json</c>): ключ «папка|код» → хэш источника картинки.
/// По нему неизменившиеся картинки не скачиваются и не пересоздаются.</summary>
public sealed class ShtrikhPcPictureState
{
    public const string FileName = "shtrikhpc-pictures.json";

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _entries;
    private readonly string? _path;

    public ShtrikhPcPictureState(string? path = null, Dictionary<string, string>? entries = null)
    {
        _path = path;
        _entries = entries ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Файл состояния этой компании (изолируется вместе с остальными данными — CompanyStoreRegistry).</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, FileName);

    public static ShtrikhPcPictureState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (entries is not null)
                    return new ShtrikhPcPictureState(path, new Dictionary<string, string>(entries, StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Весы Штрих РС: файл состояния картинок не прочитан ({ex.GetType().Name}) — картинки пересоздаются.", "WARNING");
        }
        return new ShtrikhPcPictureState(path);
    }

    private static string Key(string folder, long code) => (folder ?? "").Trim().TrimEnd('\\', '/').ToLowerInvariant() + "|" + code;

    public string? Get(string folder, long code)
    {
        lock (_lock)
            return _entries.TryGetValue(Key(folder, code), out var hash) ? hash : null;
    }

    public void Set(string folder, long code, string sourceHash)
    {
        lock (_lock)
            _entries[Key(folder, code)] = sourceHash;
    }

    public void Save()
    {
        if (_path is null)
            return;
        try
        {
            string json;
            lock (_lock)
                json = JsonSerializer.Serialize(_entries);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Весы Штрих РС: файл состояния картинок не записан ({ex.GetType().Name}).", "WARNING");
        }
    }
}

/// <summary>2026-11-06 (проверка Умара на настоящих весах): на главном экране РС-200 видны только товары с картинкой. Для каждого товара с фото касса кладёт
/// <c>Pictures\&lt;код&gt;.bmp</c> (BMP 288×288) в папку весов, добавляет строку ресурса «&lt;R» (тип 3 — картинка) и указывает её код в поле 31 строки «&lt;D».
/// Код ресурса = смещение (по умолчанию 100000) + код товара — чтобы не пересекаться с кодами прежней системы весов (6000+код) и с самими кодами товаров.</summary>
public static class ShtrikhPcPictures
{
    public const string FolderName = "Pictures";

    /// <summary>Путь картинки в строке ресурса — относительно папки обмена, через «/» как в образце весов (<c>Pictures/48002.bmp</c>).</summary>
    public static string ResourcePath(long code) => $"{FolderName}/{code}.bmp";

    public static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source ?? ""))).ToLowerInvariant();

    /// <summary>Шаг записи картинок. <paramref name="loadBmp"/> отдаёт готовый BMP (скачать/взять из кэша и перевести) или null — фото не получилось.
    /// <paramref name="budget"/> — предел времени на все картинки выгрузки (не успели — «TimedOut», доедут в следующий раз); <paramref name="perImage"/> — на одну.</summary>
    public static ShtrikhPcPictureStep CreateStep(
        Func<ShtrikhPcItem, CancellationToken, Task<byte[]?>> loadBmp,
        ShtrikhPcPictureState state,
        TimeSpan budget,
        TimeSpan perImage,
        IProgress<(int Done, int Total)>? progress = null,
        int parallel = 3) =>
        async (items, settings, ct) =>
        {
            var total = items.Count;
            var results = new ShtrikhPcItem[total];
            var written = 0;
            var unchanged = 0;
            var noPhoto = 0;
            var failed = 0;
            var timedOut = 0;
            var done = 0;
            var picturesDir = ShtrikhPcImportWriter.ResolvePath(settings.Folder, FolderName);
            var dirReady = false;
            var dirLock = new object();

            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            using var gate = new SemaphoreSlim(Math.Max(1, parallel));

            async Task Work(int index)
            {
                var item = items[index];
                results[index] = item;
                try
                {
                    if (string.IsNullOrWhiteSpace(item.PictureSource))
                    {
                        Interlocked.Increment(ref noPhoto);
                        return;
                    }
                    var resource = settings.PictureOffset + item.Code;
                    if (resource is < 1 or > 999_999)
                    {
                        Interlocked.Increment(ref failed);
                        return;
                    }
                    try
                    {
                        await gate.WaitAsync(budgetCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref timedOut);
                        return;
                    }
                    try
                    {
                        var hash = SourceHash(item.PictureSource);
                        var target = Path.Combine(picturesDir, item.Code + ".bmp");
                        if (state.Get(settings.Folder, item.Code) == hash && File.Exists(target))
                        {
                            results[index] = item with { PictureResource = resource, PictureFile = ResourcePath(item.Code) };
                            Interlocked.Increment(ref unchanged);
                            return;
                        }
                        if (budgetCts.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref timedOut);
                            return;
                        }

                        byte[]? bmp;
                        using (var one = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token))
                        {
                            one.CancelAfter(perImage);
                            try
                            {
                                bmp = await loadBmp(item, one.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                if (budgetCts.IsCancellationRequested)
                                    Interlocked.Increment(ref timedOut);
                                else
                                    Interlocked.Increment(ref failed);
                                return;
                            }
                        }
                        if (bmp is null || bmp.Length < ShtrikhPcBmp.HeaderSize)
                        {
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        lock (dirLock)
                        {
                            if (!dirReady)
                            {
                                Directory.CreateDirectory(picturesDir);
                                dirReady = true;
                            }
                        }
                        await Task.Run(() =>
                        {
                            var temp = target + ".tmp";
                            File.WriteAllBytes(temp, bmp);
                            File.Move(temp, target, overwrite: true);
                        }, CancellationToken.None).ConfigureAwait(false);
                        state.Set(settings.Folder, item.Code, hash);
                        results[index] = item with { PictureResource = resource, PictureFile = ResourcePath(item.Code) };
                        Interlocked.Increment(ref written);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    PosLogger.Log($"Весы Штрих РС: картинка товара с кодом {item.Code} не записана: {ex.GetType().Name}", "SCALES");
                }
                finally
                {
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
            }

            await Task.WhenAll(Enumerable.Range(0, total).Select(Work)).ConfigureAwait(false);
            state.Save();
            return new ShtrikhPcPictureOutcome(results, new ShtrikhPcPictureStats(total, written, unchanged, noPhoto, failed, timedOut));
        };
}
