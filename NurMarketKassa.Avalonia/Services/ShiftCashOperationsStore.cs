using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

public static class ShiftCashOperationsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa",
        "cash_history.json");

    public static IReadOnlyList<CashOperationModel> LoadAll()
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
                return Array.Empty<CashOperationModel>();

            var raw = JsonSerializer.Deserialize<List<StoredCashOperation>>(File.ReadAllText(HistoryFilePath), JsonOpts)
                      ?? new List<StoredCashOperation>();

            return raw
                .OrderByDescending(x => x.CreatedAt)
                .Select(Map)
                .ToList();
        }
        catch
        {
            return Array.Empty<CashOperationModel>();
        }
    }

    public static void Append(CashOperationModel operation)
    {
        var list = LoadStored();
        list.Insert(0, new StoredCashOperation
        {
            Id = operation.Id,
            CreatedAt = operation.CreatedAt,
            Type = operation.Type,
            Amount = operation.Amount,
            Comment = operation.Note,
            UserId = operation.Cashier,
        });
        SaveStored(list);
    }

    public static void Remove(string id)
    {
        var list = LoadStored();
        list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        SaveStored(list);
    }

    private static List<StoredCashOperation> LoadStored()
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
                return new List<StoredCashOperation>();

            return JsonSerializer.Deserialize<List<StoredCashOperation>>(File.ReadAllText(HistoryFilePath), JsonOpts)
                   ?? new List<StoredCashOperation>();
        }
        catch
        {
            return new List<StoredCashOperation>();
        }
    }

    private static void SaveStored(List<StoredCashOperation> entries)
    {
        var dir = Path.GetDirectoryName(HistoryFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(entries, JsonOpts));
    }

    private static CashOperationModel Map(StoredCashOperation entry)
    {
        var kind = CashOperationModel.ResolveKind(entry.Type);
        return new CashOperationModel
        {
            Id = entry.Id ?? Guid.NewGuid().ToString("N"),
            CreatedAt = entry.CreatedAt,
            Type = entry.Type ?? "",
            Kind = kind,
            Amount = entry.Amount,
            Cashier = string.IsNullOrWhiteSpace(entry.UserId)
                ? (NurMarketKassa.AvaloniaHost.App.CurrentUserId ?? "—")
                : entry.UserId,
            Comment = entry.Comment,
        };
    }

    private sealed class StoredCashOperation
    {
        public string? Id { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string? Type { get; set; }
        public decimal Amount { get; set; }
        public string Comment { get; set; } = "";
        public string UserId { get; set; } = "";
    }
}
