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

    // Отметку о записи на сервер ставит фоновая задача — чтение-изменение-запись файла
    // не должно пересечься с новой операцией, добавляемой в этот момент с экрана.
    private static readonly object FileLock = new();

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName,
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
        lock (FileLock)
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
                ShiftId = NurMarketKassa.PosApp.ActiveShiftId,
            });
            SaveStored(list);
        }
        // Деньги в ящике изменились — отчёт по смене пересчитываем.
        PosDataEvents.RaiseSalesChanged();
    }

    /// <summary>
    /// Итог внесений минус изъятия по конкретной смене. Прибавляется к балансу
    /// с сервера/офлайн-состояния, чтобы касса в шапке и в X/Z-отчёте учитывала эти операции.
    ///
    /// 2026-09-25: изъятие, уже записанное на сервер (ServerFlowId), сервер сам вычитает из
    /// ожидаемого остатка смены — здесь его не считаем, иначе оно вычлось бы дважды. Внесение
    /// сервер в ожидаемый остаток смены не включает (проверено на живой смене), поэтому оно
    /// прибавляется всегда.
    /// </summary>
    public static decimal NetForShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;

        return LoadStored()
            .Where(x => string.Equals(x.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
            .Aggregate(0m, (sum, x) => CashOperationModel.ResolveKind(x.Type) switch
            {
                CashOperationKind.Deposit => sum + x.Amount,
                CashOperationKind.Withdrawal when string.IsNullOrEmpty(x.ServerFlowId) => sum - x.Amount,
                _ => sum,
            });
    }

    /// <summary>Внесения и изъятия смены, которые ещё не записаны на сервер.</summary>
    public static IReadOnlyList<(string Id, string Type, decimal Amount, string Comment)> PendingForShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return Array.Empty<(string, string, decimal, string)>();

        return LoadStored()
            .Where(x => string.Equals(x.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(x.ServerFlowId)
                        && !string.IsNullOrEmpty(x.Id)
                        && CashOperationModel.ResolveKind(x.Type) is CashOperationKind.Deposit or CashOperationKind.Withdrawal)
            .OrderBy(x => x.CreatedAt)
            .Select(x => (x.Id!, x.Type ?? "", x.Amount, x.Comment))
            .ToList();
    }

    /// <summary>Операция записана на сервер: запоминаем ID движения, чтобы не отправить её
    /// повторно и не вычесть изъятие из остатка второй раз (см. NetForShift).</summary>
    public static void MarkSynced(string id, string serverFlowId)
    {
        lock (FileLock)
        {
            var list = LoadStored();
            var entry = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !string.IsNullOrEmpty(entry.ServerFlowId))
                return;
            entry.ServerFlowId = serverFlowId;
            SaveStored(list);
        }
        PosDataEvents.RaiseSalesChanged();
    }

    /// <summary>Внесения и изъятия по смене ОТДЕЛЬНО. NetForShift даёт только их разность, а в
    /// отчёте по смене нужно показать изъятия самостоятельной строкой — кассир должен видеть,
    /// сколько денег из ящика вынули, а не только итог.</summary>
    public static (decimal Deposits, decimal Withdrawals) SumsForShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return (0m, 0m);

        var deposits = 0m;
        var withdrawals = 0m;
        foreach (var op in LoadStored())
        {
            if (!string.Equals(op.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
                continue;

            switch (CashOperationModel.ResolveKind(op.Type))
            {
                case CashOperationKind.Deposit:
                    deposits += op.Amount;
                    break;
                case CashOperationKind.Withdrawal:
                    withdrawals += op.Amount;
                    break;
            }
        }

        return (deposits, withdrawals);
    }

    public static void Remove(string id)
    {
        lock (FileLock)
        {
            var list = LoadStored();
            list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveStored(list);
        }
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
        var path = HistoryFilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Атомарная запись: сбой питания посреди WriteAllText обрезал бы файл и терял всю историю.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOpts));
        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
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
        /// <summary>Смена, в которой выполнена операция (нужна для расчёта баланса кассы).</summary>
        public string? ShiftId { get; set; }
        /// <summary>ID движения денег на сервере (api/construction/cashflows/); пусто — ещё не записана.</summary>
        public string? ServerFlowId { get; set; }
    }
}
