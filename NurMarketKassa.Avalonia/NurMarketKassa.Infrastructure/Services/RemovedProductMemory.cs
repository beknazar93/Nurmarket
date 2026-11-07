using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (просьба владельца «удаление товара… как это отразится в аналитике»): память о товарах, которые касса удалила или отправила в архив.
/// Зачем: после удаления карточка исчезает из каталога, а старые чеки остаются. Местная аналитика (прибыль и ABC по категориям/брендам, «Топ товаров», маржа в «Продажах»,
/// «Аналитика товара») брала закупочную цену, категорию и бренд из каталога — у удалённого товара их не было, и он выпадал из прошлых периодов (прибыль занижалась, а выручка нет).
/// Теперь при удалении/архиве запоминаем название, закупку, цену, категорию и бренд; аналитика берёт их отсюда, если товара уже нет в каталоге.
/// Файл removed-products.json в %LOCALAPPDATA%\NurMarketKassa — общий для кассы и программы владельца, данные компании (CompanyStoreRegistry.CompanyLocalItems), хранится не более 5000 записей.
/// Историю продаж и приходов файл не заменяет — она на сервере и в строках чеков.</summary>
public sealed class RemovedProductInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public string? Brand { get; set; }
    public double PurchasePrice { get; set; }
    public double SalePrice { get; set; }
    /// <summary>true — отправлен в архив (карточка жива на сервере); false — удалён.</summary>
    public bool Archived { get; set; }
    public DateTimeOffset At { get; set; }
}

public static class RemovedProductMemory
{
    public const int MaxEntries = 5000;

    /// <summary>Только для тестов: другой файл.</summary>
    public static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(AppPaths.KassaLocal, "removed-products.json");
    private static readonly object Gate = new();
    private static (string Path, DateTime Stamp, long Length, Dictionary<string, RemovedProductInfo> Map)? _cache;

    /// <summary>Запомнить товар перед удалением/архивом. Любая ошибка записи — только в журнал: память нужна аналитике, а не операции.</summary>
    public static void Remember(CatalogProductTileVm tile, bool archived)
    {
        if (tile is null || string.IsNullOrWhiteSpace(tile.Id))
            return;
        var info = new RemovedProductInfo
        {
            Id = tile.Id.Trim(),
            Title = tile.Title ?? "",
            Category = string.IsNullOrWhiteSpace(tile.Category) ? null : tile.Category!.Trim(),
            Brand = string.IsNullOrWhiteSpace(tile.Brand) ? null : tile.Brand!.Trim(),
            PurchasePrice = tile.PurchasePrice,
            SalePrice = tile.PriceValue,
            Archived = archived,
            At = DateTimeOffset.Now,
        };
        Mutate(list =>
        {
            list.RemoveAll(x => string.Equals(x.Id, info.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(info);
            if (list.Count > MaxEntries)
                list.RemoveRange(0, list.Count - MaxEntries);
        });
    }

    /// <summary>Товар вернули из архива (или создали заново) — забыть.</summary>
    public static void Forget(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        Mutate(list => list.RemoveAll(x => string.Equals(x.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public static RemovedProductInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return Load().TryGetValue(id.Trim(), out var info) ? info : null;
    }

    /// <summary>Поиск по названию (окно «Аналитика товара» знает только название строки чека).</summary>
    public static RemovedProductInfo? FindByTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var t = title.Trim();
        return Load().Values.OrderByDescending(x => x.At).FirstOrDefault(x => string.Equals(x.Title?.Trim(), t, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Contains(string? id) => Find(id) is not null;

    public static IReadOnlyCollection<RemovedProductInfo> All() => Load().Values.ToList();

    /// <summary>Облегчённая «карточка» для расчётов, которым нужны закупка, категория и бренд (остаток 0, в каталог не попадает).</summary>
    public static CatalogProductTileVm ToTile(RemovedProductInfo info) =>
        new(info.Id, info.Title, info.SalePrice.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сом", false)
        {
            PurchasePrice = info.PurchasePrice,
            Category = info.Category,
            Brand = info.Brand,
        };

    private static Dictionary<string, RemovedProductInfo> Load()
    {
        var path = FilePath;
        lock (Gate)
        {
            try
            {
                if (!File.Exists(path))
                    return new(StringComparer.OrdinalIgnoreCase);
                var fi = new FileInfo(path);
                if (_cache is { } c && c.Path == path && c.Stamp == fi.LastWriteTimeUtc && c.Length == fi.Length)
                    return c.Map;
                var list = JsonSerializer.Deserialize<List<RemovedProductInfo>>(AtomicFile.ReadWithRetry(path)) ?? new();
                var map = new Dictionary<string, RemovedProductInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in list.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
                    map[x.Id] = x;
                _cache = (path, fi.LastWriteTimeUtc, fi.Length, map);
                return map;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Память об удалённых товарах не прочитана: {ex.Message}", "WARNING");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void Mutate(Action<List<RemovedProductInfo>> change)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Update(path, text =>
            {
                var list = new List<RemovedProductInfo>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { list = JsonSerializer.Deserialize<List<RemovedProductInfo>>(text) ?? new(); }
                    catch (JsonException ex) { PosLogger.Log($"Память об удалённых товарах нечитаема, начата заново: {ex.Message}", "WARNING"); }
                }
                change(list);
                return JsonSerializer.Serialize(list);
            });
            lock (Gate)
                _cache = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Память об удалённых товарах не записана (файл занят: {ex.Message}).", "WARNING");
        }
    }
}
