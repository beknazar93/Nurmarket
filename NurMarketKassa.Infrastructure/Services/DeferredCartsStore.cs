using System.IO;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

public static class DeferredCartsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ReaderWriterLockSlim FileLock = new(LockRecursionPolicy.NoRecursion);

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "deferred_carts.json");

    public static List<DeferredCartEntry> LoadAll()
    {
        FileLock.EnterReadLock();
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new List<DeferredCartEntry>();

            return JsonSerializer.Deserialize<List<DeferredCartEntry>>(File.ReadAllText(path), JsonOpts)
                   ?? new List<DeferredCartEntry>();
        }
        catch
        {
            return new List<DeferredCartEntry>();
        }
        finally
        {
            FileLock.ExitReadLock();
        }
    }

    public static void SaveAll(List<DeferredCartEntry> items)
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(items, JsonOpts);
        var tempPath = path + ".tmp";

        FileLock.EnterWriteLock();
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            FileLock.ExitWriteLock();
        }
    }

    public static void Add(DeferredCartEntry entry)
    {
        var all = LoadAll();
        all.Add(entry);
        SaveAll(all);
    }

    public static void UpdateEntry(DeferredCartEntry entry)
    {
        var all = LoadAll();
        var index = all.FindIndex(x => string.Equals(x.Id, entry.Id, StringComparison.Ordinal));
        if (index < 0)
            return;
        all[index] = entry;
        SaveAll(all);
    }

    public static DeferredCartEntry? TryGetById(string id) =>
        LoadAll().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    public static int Count() => LoadAll().Count;

    public static DeferredCartEntry? TryGetLatest() =>
        LoadAll()
            .OrderByDescending(x => x.SavedAt)
            .FirstOrDefault();

    public static void RemoveIds(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        SaveAll(LoadAll().Where(x => !set.Contains(x.Id)).ToList());
    }

    /// <summary>Серверная корзина занята другим чеком — отложенный остаётся только в локальном снимке.</summary>
    public static void ReleaseServerCartId(string serverCartId)
    {
        if (string.IsNullOrWhiteSpace(serverCartId))
            return;

        var all = LoadAll();
        var changed = false;
        foreach (var entry in all)
        {
            if (!string.Equals(entry.ServerCartId, serverCartId, StringComparison.OrdinalIgnoreCase))
                continue;

            entry.ServerCartId = null;
            changed = true;
        }

        if (changed)
            SaveAll(all);
    }
}
