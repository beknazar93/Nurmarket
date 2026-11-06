using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Итог картинок одной выгрузки: «Картинки: M из K, без фото: L».</summary>
/// <param name="Total">Товаров в выгрузке.</param>
/// <param name="Written">Картинок записано в папку весов заново.</param>
/// <param name="Unchanged">Картинка уже лежит на весах и не менялась — не перекачивалась и не пересоздавалась.</param>
/// <param name="NoPhoto">У товара нет фото (ссылки на картинку нет).</param>
/// <param name="Failed">Картинки нет совсем: фото не получилось, а заглушка выключена или не нарисовалась; либо код ресурса не помещается.</param>
/// <param name="TimedOut">Не хватило времени (лимит на выгрузку) — доедут в следующий раз.</param>
/// <param name="WithPhoto">У скольких товаров на весах настоящее фото.</param>
/// <param name="Placeholders">У скольких — нарисованная кассой плитка-заглушка (товар без фото или фото не получилось).</param>
public sealed record ShtrikhPcPictureStats(int Total, int Written, int Unchanged, int NoPhoto, int Failed, int TimedOut, int WithPhoto = 0, int Placeholders = 0)
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

    /// <summary>2026-11-06: ключ состояния заглушки — название, код и (если показывается на плитке) цена: сменились — плитку рисуем заново. Префикс «ph1:» отделяет её от хэша ссылки фото.</summary>
    public static string PlaceholderKey(ShtrikhPcItem item, bool showPrice) =>
        "ph1:" + SourceHash($"{item.Name}|{item.Code}|{(showPrice ? item.Price.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "")}");

    /// <summary>Шаг записи картинок. <paramref name="loadBmp"/> отдаёт готовый BMP (скачать/взять из кэша и перевести) или null — фото не получилось.
    /// <paramref name="budget"/> — предел времени на все картинки выгрузки (не успели — «TimedOut», доедут в следующий раз); <paramref name="perImage"/> — на одну.
    /// <paramref name="placeholder"/> — рисование плитки-заглушки для товара без фото (или если фото не получилось); null — заглушек нет, товар без фото остаётся без картинки, как раньше.
    /// Если фото появилось позже, ключ состояния другой (хэш ссылки) — плитка заменяется фото при следующей выгрузке.</summary>
    public static ShtrikhPcPictureStep CreateStep(
        Func<ShtrikhPcItem, CancellationToken, Task<byte[]?>> loadBmp,
        ShtrikhPcPictureState state,
        TimeSpan budget,
        TimeSpan perImage,
        IProgress<(int Done, int Total)>? progress = null,
        int parallel = 3,
        ShtrikhPcPlaceholder? placeholder = null) =>
        async (items, settings, ct) =>
        {
            var total = items.Count;
            var results = new ShtrikhPcItem[total];
            var written = 0;
            var unchanged = 0;
            var noPhoto = 0;
            var failed = 0;
            var timedOut = 0;
            var withPhoto = 0;
            var placeholders = 0;
            var done = 0;
            var picturesDir = ShtrikhPcImportWriter.ResolvePath(settings.Folder, FolderName);
            var dirReady = false;
            var dirLock = new object();

            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            using var gate = new SemaphoreSlim(Math.Max(1, parallel));

            async Task WriteBmp(string target, byte[] bmp)
            {
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
            }

            async Task Work(int index)
            {
                var item = items[index];
                results[index] = item;
                try
                {
                    var hasSource = !string.IsNullOrWhiteSpace(item.PictureSource);
                    if (!hasSource)
                        Interlocked.Increment(ref noPhoto);
                    if (!hasSource && placeholder is null)
                        return;
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
                        var target = Path.Combine(picturesDir, item.Code + ".bmp");
                        ShtrikhPcItem WithResource() => item with { PictureResource = resource, PictureFile = ResourcePath(item.Code) };

                        // 1. Настоящее фото.
                        if (hasSource)
                        {
                            var hash = SourceHash(item.PictureSource!);
                            if (state.Get(settings.Folder, item.Code) == hash && File.Exists(target))
                            {
                                results[index] = WithResource();
                                Interlocked.Increment(ref unchanged);
                                Interlocked.Increment(ref withPhoto);
                                return;
                            }
                            if (budgetCts.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref timedOut);
                                return;
                            }

                            byte[]? bmp = null;
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
                                    {
                                        Interlocked.Increment(ref timedOut);
                                        return;
                                    }
                                    // фото слишком долго грузится — как «не получилось»: ниже нарисуем заглушку, если она включена
                                }
                            }
                            if (bmp is not null && bmp.Length >= ShtrikhPcBmp.HeaderSize)
                            {
                                await WriteBmp(target, bmp).ConfigureAwait(false);
                                state.Set(settings.Folder, item.Code, hash);
                                results[index] = WithResource();
                                Interlocked.Increment(ref written);
                                Interlocked.Increment(ref withPhoto);
                                return;
                            }
                            if (placeholder is null)
                            {
                                Interlocked.Increment(ref failed);
                                return;
                            }
                        }

                        // 2. Заглушка: плитка с названием (товар без фото либо фото не получилось). Заново — при смене названия или цены.
                        var key = PlaceholderKey(item, placeholder!.ShowPrice);
                        if (state.Get(settings.Folder, item.Code) == key && File.Exists(target))
                        {
                            results[index] = WithResource();
                            Interlocked.Increment(ref unchanged);
                            Interlocked.Increment(ref placeholders);
                            return;
                        }
                        byte[]? tile = null;
                        try
                        {
                            tile = await Task.Run(() => placeholder.Render(item), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Весы Штрих РС: заглушка товара с кодом {item.Code} не нарисована: {ex.GetType().Name}", "SCALES");
                        }
                        if (tile is null || tile.Length < ShtrikhPcBmp.HeaderSize)
                        {
                            Interlocked.Increment(ref failed);
                            return;
                        }
                        await WriteBmp(target, tile).ConfigureAwait(false);
                        state.Set(settings.Folder, item.Code, key);
                        results[index] = WithResource();
                        Interlocked.Increment(ref written);
                        Interlocked.Increment(ref placeholders);
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
            return new ShtrikhPcPictureOutcome(results, new ShtrikhPcPictureStats(total, written, unchanged, noPhoto, failed, timedOut, withPhoto, placeholders));
        };
}

/// <summary>Рисование плитки-заглушки для товара без фото. <paramref name="Render"/> возвращает готовый BMP 288×288 (Skia, в приложении) или null.
/// <paramref name="ShowPrice"/> — цена на плитке (входит в ключ состояния: сменилась цена — плитка перерисовывается).</summary>
public sealed record ShtrikhPcPlaceholder(Func<ShtrikhPcItem, byte[]?> Render, bool ShowPrice);

/// <summary>Чистая логика плитки-заглушки (без Skia — проверяется отдельно): цвет фона из хэша, цвет текста, перенос названия по словам.</summary>
public static class ShtrikhPcTile
{
    /// <summary>Мягкий цвет фона из названия и кода: оттенок по хэшу, насыщенность и яркость фиксированы (светлый пастельный фон — тёмный текст читается).</summary>
    public static (byte R, byte G, byte B) BackgroundColor(string name, long code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}|{code}"));
        var hue = (hash[0] | (hash[1] << 8)) % 360;
        var lightness = 0.80 + (hash[2] % 9) / 100.0; // 0,80…0,88
        return HslToRgb(hue, 0.50, lightness);
    }

    /// <summary>Чёрный или белый текст — что контрастнее на этом фоне (по относительной яркости WCAG).</summary>
    public static bool UseDarkText(byte r, byte g, byte b)
    {
        static double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        // контраст с чёрным (L+0,05)/0,05 против контраста с белым 1,05/(L+0,05)
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Переносит текст по словам в строки не шире <paramref name="maxWidth"/> (ширину считает <paramref name="measure"/>) и не больше <paramref name="maxLines"/> строк.
    /// Слово длиннее строки режется с «…»; не поместилось по строкам — последняя строка заканчивается «…». Пустой текст — одна пустая строка не возвращается (пустой список).</summary>
    public static List<string> WrapWords(string text, Func<string, float> measure, float maxWidth, int maxLines)
    {
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        var truncated = false;

        string Fit(string word)
        {
            if (measure(word) <= maxWidth)
                return word;
            var cut = word;
            while (cut.Length > 1 && measure(cut + "…") > maxWidth)
                cut = cut[..^1];
            return cut + "…";
        }

        foreach (var raw in words)
        {
            var word = Fit(raw);
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (measure(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }
            lines.Add(current);
            if (lines.Count >= maxLines)
            {
                current = "";
                truncated = true;
                break;
            }
            current = word;
        }
        if (!truncated && current.Length > 0)
            lines.Add(current);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            truncated = true;
        }
        if (truncated && lines.Count > 0)
        {
            var last = lines[^1].TrimEnd('…');
            while (last.Length > 0 && measure(last + "…") > maxWidth)
                last = last[..^1];
            lines[^1] = last + "…";
        }
        return lines;
    }
}
