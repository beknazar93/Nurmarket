using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (Б-4, живой тест Этапа 1): программа владельца и касса на одном компьютере держат данные в РАЗНЫХ папках (%APPDATA%\NurMarketOwner и \NurMarketKassa),
/// поэтому «Требует внимания» владельца не видела очередь кассы того же ПК. Этот класс только ЧИТАЕТ папку кассы (база — в режиме «только чтение», файлы очередей — разбор JSON,
/// ничего не пишется и не блокируется) и считает записи, ещё не ушедшие на сервер: чеки, возвраты/оплаты долга без связи, приходы в фоне, новые товары.
/// Касса не запущена или папки нет — 0. Любая ошибка чтения — 0 и строка в журнал; число приблизительное (без деталей, без отправки — отправляет сама касса).</summary>
public static class SiblingTillQueue
{
    /// <summary>Имя папки данных кассы.</summary>
    public const string TillFolder = "NurMarketKassa";

    /// <summary>Для тестов: папка «кассы» вместо %APPDATA%\NurMarketKassa. Не задана — только в программе владельца, настоящая папка кассы.</summary>
    public static string? RootOverride { get; set; }

    private static string? Root()
    {
        if (!string.IsNullOrWhiteSpace(RootOverride))
            return RootOverride;
        return AppMode.IsOwner ? Path.Combine(AppPaths.Roaming, TillFolder) : null;
    }

    /// <summary>Сколько записей кассы этого компьютера не ушло на сервер.</summary>
    public static int CountUnsent()
    {
        var root = Root();
        if (root is null || !Directory.Exists(root))
            return 0;
        return Safe(() => CountSales(root), "чеки") + Safe(() => CountOperations(root), "возвраты и оплаты долга")
               + Safe(() => CountReceivings(root), "приходы") + Safe(() => CountProducts(root), "новые товары");
    }

    private static int Safe(Func<int> read, string what)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь кассы этого ПК ({what}) не прочитана: {ex.Message}", "WARNING");
            return 0;
        }
    }

    /// <summary>Чеки очереди кассы (все, кроме отправленных; автономные продажи не считаются — им некуда уходить).</summary>
    internal static int CountSales(string root)
    {
        var db = Path.Combine(root, "data", "pos_local.db");
        if (!File.Exists(db))
            return 0;
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Default Timeout=2");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM OfflineSales WHERE sync_status <> $synced AND COALESCE(json_extract(json_data, '$.isAutonomous'), 0) = 0;";
        command.Parameters.AddWithValue("$synced", OfflineSaleEntry.Synced);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Возвраты и оплаты долга без связи (offline_operations.json): не отправленные и с ошибкой, кроме снятых вручную.</summary>
    internal static int CountOperations(string root)
    {
        var file = Path.Combine(root, "offline_operations.json");
        if (!File.Exists(file))
            return 0;
        using var doc = JsonDocument.Parse(ReadShared(file));
        if (!TryProp(doc.RootElement, "Operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
            return 0;
        return ops.EnumerateArray().Count(o =>
            !string.Equals(Str(o, "Status"), OfflineOperationEntry.Synced, StringComparison.OrdinalIgnoreCase)
            && Str(o, "LastError") != OfflineOperationsStore.CanceledMarker);
    }

    /// <summary>Приходы в фоне (background-receipts\queue.json): всё, что не «принято сервером».</summary>
    internal static int CountReceivings(string root)
    {
        var file = Path.Combine(root, "background-receipts", "queue.json");
        if (!File.Exists(file))
            return 0;
        using var doc = JsonDocument.Parse(ReadShared(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return 0;
        return doc.RootElement.EnumerateArray().Count(j => j.ValueKind == JsonValueKind.Object
            && !string.Equals(Str(j, "Status"), ReceivingJobStatus.Done, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Новые товары без связи (pending-products.json): ждущие и с отказом сервера, как в «Неотправленном».</summary>
    internal static int CountProducts(string root)
    {
        var file = Path.Combine(root, "pending-products.json");
        if (!File.Exists(file))
            return 0;
        using var doc = JsonDocument.Parse(ReadShared(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return 0;
        return doc.RootElement.EnumerateArray().Count(j => j.ValueKind == JsonValueKind.Object
            && Str(j, "Status") is { } s
            && (string.Equals(s, PendingProductStatus.Queued, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, PendingProductStatus.RetryWait, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, PendingProductStatus.Failed, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Файл может быть открыт кассой на запись — читаем с общим доступом.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool TryProp(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var p in element.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string? Str(JsonElement element, string name) =>
        TryProp(element, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
