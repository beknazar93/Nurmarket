using System.Text.Json;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (проверка «Автозапчастей», дефект А): локальный «журнал импорта» прайса. Артикул и бренд свежесозданных товаров попадают в каталог кассы
/// не сразу (карточки докачиваются по одной), и повторная загрузка того же прайса без штрихкодов сразу после первой заводила дубли (102 из 277).
/// После успешного создания товара запоминаем (по компании) ключ «нормализованный номер + бренд» → id товара; при следующем разборе прайса
/// <see cref="AutoPartsPriceImport.Prepare"/> сверяется с журналом ПЕРЕД созданием: товар уже в каталоге — обновление, ещё не дошёл в каталог
/// (меньше <see cref="PendingWindow"/>) — строка пропускается, иначе (товар удалён на сервере) запись игнорируется. Файл — только кэш на этом ПК
/// (<c>%LOCALAPPDATA%\NurMarketKassa\autoparts-import-log.json</c>), как PartMetaStore; чистится при полной загрузке каталога (<see cref="Prune"/>).
/// </summary>
public static class AutoPartsImportLog
{
    /// <summary>Сколько после создания товар считается «ещё едет в каталог»: пока его нет в каталоге, строку повторного прайса не создаём заново.</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private const int MaxPerCompany = 20000;

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public DateTime At { get; set; }
        /// <summary>Бренд как в прайсе: плитка новых товаров показывает его, пока каталог не докачал карточку (<see cref="BrandOf"/>).</summary>
        public string Brand { get; set; } = "";
    }

    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, Entry>>? _data;
    private static bool _dirty;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }
    /// <summary>Только для проверок: компания вместо текущей.</summary>
    public static string? CompanyIdOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.KassaLocal, "autoparts-import-log.json");
    private static string Company => CompanyIdOverride is { Length: > 0 } over ? over : CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : "default";

    private static string KeyOf(string partNorm, string brandKey) => partNorm + "|" + brandKey;

    /// <summary>После успешного создания товара: ключ «номер + бренд» → id. Файл пишется отдельно (<see cref="SaveIfDirty"/>).</summary>
    public static void Record(string? productId, string partNorm, string brandKey, string? brand = null, DateTime? at = null)
    {
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrEmpty(partNorm))
            return;
        lock (Gate)
        {
            var map = CompanyMap(create: true)!;
            map[KeyOf(partNorm, brandKey)] = new Entry { Id = productId.Trim(), At = at ?? DateTime.UtcNow, Brand = (brand ?? "").Trim() };
            _brandById = null;
            _dirty = true;
        }
    }

    private static Dictionary<string, string>? _brandById;

    /// <summary>Бренд товара по id из журнала (пока каталог не дочитал карточку; потом запись уходит); null — нет. Для плитки: вызывается часто, поиск по словарю.</summary>
    public static string? BrandOf(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        lock (Gate)
        {
            var index = _brandById;
            if (index is null)
            {
                index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (CompanyMap(create: false) is { } map)
                    foreach (var e in map.Values)
                        if (e.Brand.Length > 0)
                            index[e.Id] = e.Brand;
                _brandById = index;
            }
            return index.TryGetValue(productId.Trim(), out var brand) ? brand : null;
        }
    }

    /// <summary>Запись о ключе: сначала с тем же брендом, затем без бренда (товар, заведённый до того, как стал известен бренд).</summary>
    public static Entry? Find(string partNorm, string brandKey)
    {
        if (string.IsNullOrEmpty(partNorm))
            return null;
        lock (Gate)
        {
            var map = CompanyMap(create: false);
            if (map is null || map.Count == 0)
                return null;
            return map.TryGetValue(KeyOf(partNorm, brandKey), out var e) ? e : map.TryGetValue(KeyOf(partNorm, ""), out var e2) ? e2 : null;
        }
    }

    /// <summary>После полной загрузки каталога: убрать записи, которые каталог уже «догнал» (у товара совпал артикул), удалённые на сервере
    /// (id нет в каталоге, прошло больше <see cref="PendingWindow"/>) и устаревшие.</summary>
    public static void Prune(IReadOnlyList<CatalogProductTileVm> catalog, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        lock (Gate)
        {
            var map = CompanyMap(create: false);
            if (map is null || map.Count == 0)
                return;
            var byId = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var tile in catalog)
                if (!string.IsNullOrWhiteSpace(tile.Id))
                    byId.TryAdd(tile.Id.Trim(), tile);
            var drop = new List<string>();
            foreach (var (key, e) in map)
            {
                var age = t - e.At;
                var partNorm = key[..Math.Max(0, key.IndexOf('|'))];
                if (age > MaxAge
                    || (!byId.TryGetValue(e.Id, out var tile) && age > PendingWindow)
                    || (tile is not null && PartNumbers.Norm(tile.Article) == partNorm))
                    drop.Add(key);
            }
            foreach (var key in drop)
                map.Remove(key);
            if (drop.Count > 0)
            {
                _dirty = true;
                _brandById = null;
            }
        }
        SaveIfDirty();
    }

    /// <summary>Только для проверок: забыть всё в памяти (файл не трогает).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            _data = null;
            _brandById = null;
            _dirty = false;
        }
    }

    public static void SaveIfDirty()
    {
        try
        {
            lock (Gate)
            {
                if (!_dirty || _data is null)
                    return;
                _dirty = false;
                foreach (var map in _data.Values)
                    if (map.Count > MaxPerCompany)
                        foreach (var old in map.OrderBy(kv => kv.Value.At).Take(map.Count - MaxPerCompany).Select(kv => kv.Key).ToList())
                            map.Remove(old);
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_data));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Журнал импорта прайса: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    private static Dictionary<string, Entry>? CompanyMap(bool create)
    {
        _data ??= Load();
        if (_data.TryGetValue(Company, out var map))
            return map;
        if (!create)
            return null;
        return _data[Company] = new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private static Dictionary<string, Dictionary<string, Entry>> Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path)
                && AtomicFile.ReadAllText(path) is { Length: > 0 } text
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(text) is { } stored)
                return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый журнал не роняет загрузку: он лишь подсказка, каталог главнее.
            PosLogger.Log($"Журнал импорта прайса: файл не прочитан ({ex.Message}) — начинаем заново.", "WARNING");
        }
        return new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
    }
}
