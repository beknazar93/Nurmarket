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

    /// <summary>Атомарность транзакции «прочитать-изменить-сохранить» (FileLock защищает только сам I/O).</summary>
    private static readonly object UpdateGate = new();

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            NurMarketKassa.Services.AppMode.DataFolderName,
            "deferred_carts.json");

    // 2026-11-02 (аудит №2 кассы, К3-01; приёмка круга 3): «файл занят» ≠ «нет отложенных чеков». Файл не прочитан или записать не вышло — запись откладывается
    // (AtomicFile.DeferWrite): свежее состояние читается из памяти первым, таймер дописывает его в файл (с объединением по Id), при закрытии — запасной файл *.pending.
    private static bool _blocked;

    /// <summary>2026-11-05 (Д3-06): id отложенных чеков, удалённых в этом сеансе — при занятом файле объединение с файлом иначе вернуло бы их.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RemovedIds = new(StringComparer.Ordinal);

    private static string MergeEntries(string? fileText, string memoryText)
    {
        List<DeferredCartEntry> Parse(string? text)
        {
            try
            {
                return text is null ? new() : JsonSerializer.Deserialize<List<DeferredCartEntry>>(text, JsonOpts) ?? new();
            }
            catch (JsonException)
            {
                return new();
            }
        }
        var memory = Parse(memoryText);
        foreach (var e in Parse(fileText))
            if (!memory.Any(x => string.Equals(x.Id, e.Id, StringComparison.Ordinal)) && !RemovedIds.ContainsKey(e.Id)) // 2026-11-05 (аудит №3, Д3-06): удалённый в этом сеансе чек из файла не возвращается
                memory.Add(e);
        return JsonSerializer.Serialize(memory, JsonOpts);
    }

    public static List<DeferredCartEntry> LoadAll()
    {
        FileLock.EnterReadLock();
        try
        {
            var read = AtomicFile.TryRead(FilePath, attempts: 3, pauseMs: 100);
            List<DeferredCartEntry> list;
            try
            {
                list = (read.Text is { } text ? JsonSerializer.Deserialize<List<DeferredCartEntry>>(text, JsonOpts) : null) ?? new List<DeferredCartEntry>();
            }
            catch (JsonException)
            {
                list = new List<DeferredCartEntry>();
            }
            _blocked = read.Status == AtomicFile.ReadStatus.Unreadable || read.FromMemory;
            return list;
        }
        finally
        {
            FileLock.ExitReadLock();
        }
    }

    public static void SaveAll(List<DeferredCartEntry> items)
    {
        var json = JsonSerializer.Serialize(items, JsonOpts);
        if (_blocked)
        {
            AtomicFile.DeferWrite(FilePath, "deferred_carts.json", json, MergeEntries);
            return;
        }
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        FileLock.EnterWriteLock();
        try
        {
            try
            {
                AtomicFile.WriteAllTextWithRetry(path, json); // 2026-11-01 (Н2-01): сброс на диск + .bak
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AtomicFile.DeferWrite(path, "deferred_carts.json", json, MergeEntries); // 2026-11-02 (К3-01): файл занят при записи — не теряем
            }
        }
        finally
        {
            FileLock.ExitWriteLock();
        }
    }

    public static void Add(DeferredCartEntry entry)
    {
        lock (UpdateGate)
        {
            var all = LoadAll();
            all.Add(entry);
            SaveAll(all);
        }
    }

    public static void UpdateEntry(DeferredCartEntry entry)
    {
        lock (UpdateGate)
        {
            var all = LoadAll();
            var index = all.FindIndex(x => string.Equals(x.Id, entry.Id, StringComparison.Ordinal));
            if (index < 0)
                return;
            all[index] = entry;
            SaveAll(all);
        }
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
        foreach (var id in set)
            RemovedIds[id] = 0;
        lock (UpdateGate)
            SaveAll(LoadAll().Where(x => !set.Contains(x.Id)).ToList());
    }

    /// <summary>Серверная корзина занята другим чеком — отложенный остаётся только в локальном снимке.</summary>
    public static void ReleaseServerCartId(string serverCartId)
    {
        if (string.IsNullOrWhiteSpace(serverCartId))
            return;

        lock (UpdateGate)
        {
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
}
