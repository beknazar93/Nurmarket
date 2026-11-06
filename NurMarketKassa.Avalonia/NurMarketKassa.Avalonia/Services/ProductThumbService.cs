using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Avalonia port: disk cache + download for product thumbnails (path-based, no WPF ImageSource).
/// 2026-11-05 (аудит скорости, п. 15 а, б): в кэше теперь не оригиналы с сайта, а миниатюры 320 px WebP (<c>&lt;sha&gt;.t320.webp</c>,
/// см. <see cref="ThumbImageCompressor"/>); старые тяжёлые файлы пересжимаются лениво в фоне при первом обращении;
/// всё скачивание, сжатие и расшифровка — вне потока интерфейса. Для карточки товара (просмотр фото на весь экран) есть отдельный
/// больший размер — <see cref="GetFullPathAsync"/>.</summary>
internal sealed class ProductThumbService
{
    // 2026-11-06 (аудит скорости, 25.2): общая фабрика внешних клиентов (смена DNS через 15 мин, автораспаковка).
    private static readonly HttpClient PublicHttp = ExternalHttp.Create(TimeSpan.FromSeconds(25));
    private const int ThumbQuality = 80;
    private const int FullQuality = 85;
    private const long MaxDownloadBytes = 30L * 1024 * 1024;
    private const string FullFolder = "full";
    private static readonly string[] ThumbExtensions = { ".webp", ".jpg", ".png" };

    // Ленивое пересжатие старых файлов: по одному за раз (слабый ПК), каждый файл — один раз за запуск программы.
    private static readonly SemaphoreSlim RecompressGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> RecompressSeen = new(StringComparer.OrdinalIgnoreCase);
    private static int _recompressDone;
    private static long _recompressSavedBytes;

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<string?>> _downloadTasks = new(StringComparer.OrdinalIgnoreCase);

    public ProductThumbService()
    {
        _cacheDir = CacheDirectory();
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product thumbnail cache creation failed: {ex.GetType().Name}", "WARNING");
        }
        StartMaintenanceOnce(_cacheDir);
    }

    // 2026-11-06 (аудит скорости, шаг 21): кэш фото — в %LOCALAPPDATA% (это кэш, не данные: в резервную копию и в роуминг не нужен), с пределом ≈ 300 МБ.
    // Раньше лежал в Roaming без предела. Старую папку один раз переносим, потом в фоне чистим самое старое.
    private static string CacheDirectory() => Path.Combine(
        NurMarketKassa.Services.AppPaths.Local,
        NurMarketKassa.Services.AppMode.DataFolderName,
        "product_thumbs");

    private static string LegacyCacheDirectory() => Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        NurMarketKassa.Services.AppMode.DataFolderName,
        "product_thumbs");

    private static int _maintenanceStarted;
    private static int _writesSincePrune;
    private const int PruneEveryWrites = 300;

    private static void StartMaintenanceOnce(string cacheDir)
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false); // не мешаем запуску кассы
                var moved = FileCachePruner.MoveAll(LegacyCacheDirectory(), cacheDir);
                var (deleted, freed) = FileCachePruner.PruneBySize(cacheDir, FileCachePruner.ThumbCacheLimitBytes);
                if (moved > 0 || deleted > 0)
                    PosLogger.Log($"Кэш фото: перенесено из старой папки {moved}, удалено {deleted} ({freed / 1024 / 1024} МБ).", "CATALOG");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Кэш фото: обслуживание не удалось: {ex.GetType().Name}", "WARNING");
            }
        });
    }

    /// <summary>После каждой записи файла в кэш: раз в 300 записей — фоновая чистка до предела.</summary>
    private static void NoteWrite(string cacheDir)
    {
        if (Interlocked.Increment(ref _writesSincePrune) % PruneEveryWrites != 0)
            return;
        _ = Task.Run(() => FileCachePruner.PruneBySize(cacheDir, FileCachePruner.ThumbCacheLimitBytes));
    }

    public async Task SetThumbAsync(
        Dispatcher uiDispatcher,
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        Models.Pos.CatalogProductTileVm vm,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(vm.ProductImagePath))
            return;

        // 2026-11-05 (п. 15 б): вызывающие зовут нас с потока интерфейса, а проверка файла, сжатие и расшифровка картинки — работа диска и
        // процессора. Всё это — в Task.Run; на поток интерфейса возвращаем только готовый путь, а картинка к этому моменту уже лежит в кэше конвертера.
        var path = await Task.Run(async () =>
        {
            var p = await GetOrDownloadPathAsync(authApi, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(p) || !File.Exists(p))
                return null;
            PrewarmBitmap(p);
            return p;
        }, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path))
            return;

        await uiDispatcher.InvokeAsync(() => vm.ProductImagePath = path);

        // Старый тяжёлый файл: отдали сразу (плитка не ждёт), пересжимаем в фоне и подменяем путь на новый файл.
        if (!IsThumbFile(path))
            ScheduleRecompress(imageUrl, path, onDone: newPath => uiDispatcher.InvokeAsync(() =>
            {
                if (string.Equals(vm.ProductImagePath, path, StringComparison.OrdinalIgnoreCase))
                    vm.ProductImagePath = newPath;
            }));
    }

    /// <summary>Расшифровать миниатюру в фоне и положить в кэш конвертера (тот же ключ «путь|thumb»): плитка получит готовую картинку без расшифровки в потоке интерфейса.</summary>
    private static void PrewarmBitmap(string path)
    {
        try
        {
            AssetPathToBitmapConverter.Instance.Convert(path, typeof(Avalonia.Media.Imaging.Bitmap), "thumb", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            // подогрев — только ускорение; плитка загрузит сама
        }
    }

    /// <summary>true — файл уже в новом формате (<c>.t320.</c>) и пересжимать его не надо.</summary>
    internal static bool IsThumbFile(string path) =>
        Path.GetFileName(path).Contains(ThumbImageCompressor.ThumbMarker, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> GetOrDownloadPathAsync(
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        CancellationToken ct,
        bool forceDownload = false)
    {
        var key = Sha256Hex(imageUrl);
        if (!forceDownload)
        {
            var existing = FindExisting(_cacheDir, key, GuessExt(imageUrl));
            if (existing is not null)
                return existing;
        }

        // Ключ задач — без расширения: новый файл получает своё имя (.t320.webp), старого имени у него нет.
        var taskKey = Path.Combine(_cacheDir, key);
        var task = _downloadTasks.GetOrAdd(taskKey, _ => DownloadToCacheAsync(_cacheDir, key, GuessExt(imageUrl), authApi, apiBaseUrl, imageUrl, ct));
        try { return await task.ConfigureAwait(false); }
        finally { _downloadTasks.TryRemove(taskKey, out _); }
    }

    /// <summary>Что уже лежит в кэше: сначала новая миниатюра, потом старый файл-оригинал (имя по расширению ссылки).</summary>
    private static string? FindExisting(string dir, string key, string legacyExt)
    {
        foreach (var ext in ThumbExtensions)
        {
            var thumb = Path.Combine(dir, key + ThumbImageCompressor.ThumbMarker.TrimEnd('.') + ext);
            if (File.Exists(thumb))
                return thumb;
        }
        var legacy = Path.Combine(dir, key + legacyExt);
        return File.Exists(legacy) ? legacy : null;
    }

    private static async Task<string?> DownloadToCacheAsync(
        string dir,
        string key,
        string legacyExt,
        IAuthApiService authApi,
        string apiBaseUrl,
        string imageUrl,
        CancellationToken ct)
    {
        try
        {
            var bytes = await DownloadBytesAsync(authApi, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
            if (bytes is null)
                return null;

            // Сжатие — не в потоке интерфейса и не в потоке сетевого ответа.
            var packed = await Task.Run(() => ThumbImageCompressor.Compress(bytes, ThumbImageCompressor.ThumbMaxSide, ThumbQuality, out _), ct).ConfigureAwait(false);
            if (packed is not null)
            {
                var local = Path.Combine(dir, key + ThumbImageCompressor.ThumbMarker.TrimEnd('.') + ExtOf(packed));
                if (ThumbImageCompressor.TryWriteAtomic(local, packed))
                {
                    NoteWrite(dir);
                    return local;
                }
            }

            // 2026-11-06 (Л5-23 / А5-12): огромный PNG/GIF/BMP (десятки Мп) не кладём — показ расшифровал бы его в памяти целиком (145 МБ и больше). Плитка без фото.
            if (ThumbImageCompressor.IsOversized(bytes, ThumbImageCompressor.MaxNonScalablePixels))
            {
                PosLogger.Log($"Миниатюра товара: картинка слишком большая для слабого устройства ({bytes.Length / 1024} КБ) — пропущена.", "CATALOG");
                return null;
            }

            // Не картинка или не расшифровалась: как раньше — кладём байты как есть (чтобы не качать заново при каждом показе плитки).
            var raw = Path.Combine(dir, key + legacyExt);
            await File.WriteAllBytesAsync(raw, bytes, ct).ConfigureAwait(false);
            NoteWrite(dir);
            return raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Расширение результата сжатия по заголовку: RIFF…WEBP → .webp, PNG → .png, иначе .jpg.</summary>
    private static string ExtOf(byte[] packed)
    {
        if (packed.Length > 12 && packed[0] == 'R' && packed[1] == 'I' && packed[2] == 'F' && packed[3] == 'F' && packed[8] == 'W')
            return ".webp";
        if (packed.Length > 4 && packed[0] == 0x89 && packed[1] == 'P')
            return ".png";
        return ".jpg";
    }

    private static async Task<byte[]?> DownloadBytesAsync(IAuthApiService authApi, string apiBaseUrl, string imageUrl, CancellationToken ct)
    {
        var uri = BuildAbsoluteUri(apiBaseUrl, imageUrl);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        // 2026-11-05 (Н-9): токен NurCRM — только своему серверу. Раньше он уходил и на чужие хосты картинок (CDN, магазины, поисковики фото).
        var token = authApi.AccessToken;
        if (!string.IsNullOrWhiteSpace(token) && IsOwnHost(uri, apiBaseUrl))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await PublicHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        // 2026-11-06: ответ больше 30 МБ — не фото товара (чужой сайт отдал файл/страницу); не качаем целиком в память и в кэш.
        if (resp.Content.Headers.ContentLength is > MaxDownloadBytes)
            return null;

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return bytes.Length == 0 ? null : bytes;
    }

    /// <summary>2026-11-06: байты фото товара для других нужд (картинки товаров для весов РС-200): та же загрузка с ограничением размера и токеном только своему серверу,
    /// без записи в кэш миниатюр. null — не скачалось.</summary>
    internal static async Task<byte[]?> DownloadOriginalAsync(IAuthApiService authApi, string apiBaseUrl, string imageUrl, CancellationToken ct)
    {
        try
        {
            return await DownloadBytesAsync(authApi, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>true — адрес картинки на том же хосте, что и API NurCRM (тогда Bearer-токен можно отправлять).</summary>
    internal static bool IsOwnHost(Uri imageUri, string apiBaseUrl)
    {
        try
        {
            return Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var api)
                   && string.Equals(imageUri.Host, api.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>2026-11-05: фото побольше для карточки товара («Открыть на весь экран»): оригинал с сайта ужимается до 1600 px WebP и лежит в product_thumbs/full.
    /// Миниатюры 320 px для полноэкранного просмотра мало. null — не получилось (нет сети и т.п.): вызывающий оставляет то, что уже показывает.</summary>
    public async Task<string?> GetFullPathAsync(IAuthApiService authApi, string apiBaseUrl, string imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;
        try
        {
            var dir = Path.Combine(_cacheDir, FullFolder);
            var local = Path.Combine(dir, Sha256Hex(imageUrl) + ".webp");
            if (File.Exists(local))
                return local;
            return await Task.Run(async () =>
            {
                Directory.CreateDirectory(dir);
                var bytes = await DownloadBytesAsync(authApi, apiBaseUrl, imageUrl, ct).ConfigureAwait(false);
                if (bytes is null)
                    return null;
                var packed = ThumbImageCompressor.Compress(bytes, ThumbImageCompressor.FullMaxSide, FullQuality, out _);
                if (packed is null)
                    return null;
                var file = Path.Combine(dir, Sha256Hex(imageUrl) + ExtOf(packed));
                return ThumbImageCompressor.TryWriteAtomic(file, packed) ? file : null;
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ---- ленивое пересжатие старых файлов ----

    private static void ScheduleRecompress(string imageUrl, string legacyPath, Func<string, DispatcherOperation>? onDone)
    {
        if (!RecompressSeen.TryAdd(legacyPath, 0))
            return;
        _ = Task.Run(async () =>
        {
            await RecompressGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(legacyPath)!;
                var key = Path.GetFileNameWithoutExtension(legacyPath);
                var before = new FileInfo(legacyPath).Length;
                var bytes = await File.ReadAllBytesAsync(legacyPath).ConfigureAwait(false);
                var packed = ThumbImageCompressor.Compress(bytes, ThumbImageCompressor.ThumbMaxSide, ThumbQuality, out _);
                if (packed is null)
                    return; // повреждённый файл не трогаем — как был (и не падаем)
                var newPath = Path.Combine(dir, key + ThumbImageCompressor.ThumbMarker.TrimEnd('.') + ExtOf(packed));
                if (!ThumbImageCompressor.TryWriteAtomic(newPath, packed))
                    return; // не записалось — старый файл остаётся нетронутым
                // Новый файл записан целиком — только теперь старый можно убрать.
                PrewarmBitmap(newPath);
                if (onDone is not null)
                    await onDone(newPath).GetTask().ConfigureAwait(false);
                try { File.Delete(legacyPath); } catch { /* занят — удалится при следующем запуске, новый файл уже главный */ }

                Interlocked.Add(ref _recompressSavedBytes, Math.Max(0, before - packed.Length));
                var done = Interlocked.Increment(ref _recompressDone);
                if (done % 25 == 0)
                    PosLogger.Log($"Миниатюры товаров: пересжато {done} файлов, освобождено {Interlocked.Read(ref _recompressSavedBytes) / 1024 / 1024} МБ.", "CATALOG");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Миниатюра не пересжата: {ex.GetType().Name}", "WARNING");
            }
            finally
            {
                RecompressGate.Release();
            }
        });
    }

    private static Uri BuildAbsoluteUri(string apiBaseUrl, string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var abs))
            return abs;
        var baseUri = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/", UriKind.Absolute);
        return new Uri(baseUri, imageUrl.TrimStart('/'));
    }

    /// <summary>Уже скачанная миниатюра товара или null — без скачивания (2026-09-28): экран
    /// покупателя показывает фото позиций чека из того же кэша, что и плитки каталога.
    /// 2026-11-05: сначала новая миниатюра 320 px (.t320.), потом старый файл. Экран покупателя и так расшифровывает миниатюры в 320 px
    /// (ConverterParameter=thumb), поэтому размера 320 ему хватает, вид не меняется.</summary>
    public static string? TryGetCachedPath(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;
        try
        {
            return FindExisting(CacheDirectory(), Sha256Hex(imageUrl), GuessExt(imageUrl));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GuessExt(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(url).AbsolutePath
                : url;
            var ext = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
        }
        catch { return ".jpg"; }
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
