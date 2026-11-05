using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-01 (контрольный аудит, С2-07): перемещение между складами, отправка которого закончилась неизвестным исходом (таймаут, обрыв), запоминается НА ДИСКЕ:
// ключ повтора, дата (она входит в ключ), склады и состав. Повторное «Отправить» — даже после перезапуска кассы, на следующий день или после повторного открытия окна —
// сначала ищет это перемещение на сервере по метке ключа и только потом, если его там нет, шлёт тот же документ ТЕМ ЖЕ ключом. Запись снимается при успехе, при найденном
// на сервере документе и при явном отказе сервера. Файл: purchases\pending-transfers.json (AtomicFile, схема pos_local.db не меняется).

public sealed record PendingTransferLine(string ProductId, double Quantity);

public sealed record PendingTransfer(string Key, string Date, string? FromId, string? ToId, List<PendingTransferLine> Lines, DateTime AtUtc);

public static class PendingTransferKeys
{
    private static readonly object Sync = new();
    private static Dictionary<string, PendingTransfer>? _data;
    private static bool _readFailed; // файл не прочитался — не кэшируем пустое и не перезаписываем файл (иначе записи с неизвестным исходом пропали бы)

    /// <summary>2026-11-02 (аудит №2, Н-6): запись живёт ограниченно — через 14 дней неизвестный исход уже не проверить поиском «за период», запись снимается.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private static string? FilePath => PurchasesStore.StorageDir is { Length: > 0 } d ? Path.Combine(d, "pending-transfers.json") : null;

    private static Dictionary<string, PendingTransfer> Data()
    {
        if (_data is not null)
            return _data;
        var loaded = new Dictionary<string, PendingTransfer>(StringComparer.Ordinal);
        try
        {
            if (FilePath is { } path && AtomicFile.ReadAllText(path) is { } text)
                loaded = JsonSerializer.Deserialize<Dictionary<string, PendingTransfer>>(text) ?? loaded;
            foreach (var old in loaded.Where(kv => DateTime.UtcNow - kv.Value.AtUtc > MaxAge).Select(kv => kv.Key).ToList())
                loaded.Remove(old);
            _readFailed = false;
            _data = loaded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // ошибка чтения не кэшируется: следующий вызов прочитает файл заново, а запись на диск до этого запрещена (Save)
            _readFailed = true;
            PosLogger.Log($"Перемещения с неизвестным исходом не прочитаны ({ex.Message}).", "WARNING");
            return loaded;
        }
        return _data;
    }

    private static void Save()
    {
        if (PurchasesStore.ReadOnly || _readFailed || FilePath is not { } path)
            return;
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Data()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Перемещения с неизвестным исходом не записаны ({ex.Message}).", "WARNING");
        }
    }

    public static PendingTransfer? Get(string scope)
    {
        lock (Sync)
            return Data().TryGetValue(scope, out var p) ? p : null;
    }

    public static void Set(string scope, PendingTransfer pending)
    {
        lock (Sync)
        {
            // первая отправка с неизвестным исходом задаёт время и ключ; повторная неизвестность ключ не меняет, но продлевает паузу 90 с (AtUtc = сейчас) — 2026-11-05 (аудит №3, Т3-14): комментарий приведён к коду
            if (!Data().ContainsKey(scope))
                Data()[scope] = pending;
            else
                Data()[scope] = Data()[scope] with { AtUtc = DateTime.UtcNow };
            Save();
        }
    }

    public static void Clear(string scope)
    {
        lock (Sync)
            if (Data().Remove(scope))
                Save();
    }
}
