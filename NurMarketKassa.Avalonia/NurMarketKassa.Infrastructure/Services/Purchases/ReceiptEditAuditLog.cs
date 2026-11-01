using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-01 (просьба владельца «правка накладной постоянно открыта, но все изменения записывай»): построчный журнал правок принятых накладных —
// %APPDATA%\NurMarketKassa\purchases\purchase-edits.jsonl (одна строка JSON = одно событие правки; схема pos_local.db не меняется, секретов нет).
// Это добавление к receipt-edits.json (ReceiptEditStore): тот файл переписывается целиком и при порче начинается заново, а эта лента только дописывается —
// история правок не теряется. События: queued (поставлена в очередь без связи), started (записана ДО отправки на сервер), done / partial / applying /
// failed / abandoned (итог). Повтор того же события для той же правки (идемпотентно, ключ — id правки + статус + число проведённых шагов) не пишется.
// Ротация: файл вырос до 5 МБ — уходит в архив purchase-edits-ГГГГММДД-ЧЧММСС.jsonl, архивы не удаляются (хранение не меньше года).

public sealed class ReceiptEditAuditLine
{
    public string Product { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Unit { get; set; } = "";
    public string LineId { get; set; } = "";
    public double QtyBefore { get; set; }
    public double QtyAfter { get; set; }
    public double Price { get; set; }
    public bool NotPosted { get; set; }
}

/// <summary>2026-11-01: одно изменённое поле «было → стало» (quantity, sale_price, markup_percent…). Status — как у шага правки: done / pending / unknown / failed / abandoned.</summary>
public sealed class ReceiptEditAuditField
{
    public string Field { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class ReceiptEditAuditEntry
{
    public DateTime Ts { get; set; }
    public string Event { get; set; } = "";
    public string EditId { get; set; } = "";
    public string ReceiptId { get; set; } = "";
    public string DocNumber { get; set; } = "";
    public string Supplier { get; set; } = "";
    public DateTime? ReceiptAt { get; set; }
    public int Version { get; set; }
    public string Kind { get; set; } = "edit";
    public string By { get; set; } = "";
    public string ById { get; set; } = "";
    public string ByRole { get; set; } = "";
    public string Device { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Compensation { get; set; } = "";
    public double TotalBefore { get; set; }
    public double TotalAfter { get; set; }
    public double? PlannedTotalAfter { get; set; }
    public int StepsDone { get; set; }
    public int StepsTotal { get; set; }
    /// <summary>Правка задним числом: с прихода прошло больше <see cref="ReceiptEditAuditLog.BackdatedDays"/> суток.</summary>
    public bool Backdated { get; set; }
    public int AgeDays { get; set; }
    public List<ReceiptEditAuditLine> Lines { get; set; } = new();
    /// <summary>Каждое изменённое поле отдельно «было → стало» (количество, цена продажи, наценка).</summary>
    public List<ReceiptEditAuditField> Fields { get; set; } = new();
}

public static class ReceiptEditAuditLog
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int BackdatedDays = 7;
    public const string FileName = "purchase-edits.jsonl";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };
    private static readonly object Sync = new();
    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> Flagged = new(StringComparer.OrdinalIgnoreCase);
    private static bool _primed;

    /// <summary>Папка и запрет записи — как у журнала закупок. Мини-прогон подменяет.</summary>
    public static Func<string?> DirProvider { get; set; } = () => PurchasesStore.StorageDir;
    public static Func<bool> ReadOnlyProvider { get; set; } = () => PurchasesStore.ReadOnly;

    private static string? FilePath => DirProvider() is { Length: > 0 } d ? Path.Combine(d, FileName) : null;

    /// <summary>Забыть прочитанное (другая папка, проверка).</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _primed = false;
            Written.Clear();
            Flagged.Clear();
        }
    }

    /// <summary>Правка старше недели от прихода?</summary>
    public static bool IsBackdated(DateTime? receiptAt, DateTime editAt) => receiptAt is { } at && (editAt - at).TotalDays > BackdatedDays;

    public static ReceiptEditAuditEntry FromRecord(ReceiptEditRecord r, string evt) => new()
    {
        Ts = DateTime.Now, Event = evt, EditId = r.Id, ReceiptId = r.ReceiptId, DocNumber = r.DocNumber, Supplier = r.SupplierName, ReceiptAt = r.ReceiptAt,
        Version = r.Version, Kind = r.Kind, By = r.By, ById = r.ById, ByRole = r.ByRole, Device = r.Device, Reason = r.Reason, Compensation = r.Compensation,
        TotalBefore = r.TotalBefore, TotalAfter = r.TotalAfter, PlannedTotalAfter = r.PlannedTotalAfter,
        StepsDone = r.Steps.Count(s => s.Status == "done"), StepsTotal = r.Steps.Count,
        Backdated = IsBackdated(r.ReceiptAt, r.At), AgeDays = r.ReceiptAt is { } at ? (int)Math.Floor((r.At - at).TotalDays) : 0,
        Lines = r.Lines.Select(l => new ReceiptEditAuditLine { Product = l.Name, ProductId = l.ProductId, Unit = l.Unit, LineId = l.LineId, QtyBefore = l.QtyBefore, QtyAfter = l.QtyAfter, Price = l.Price, NotPosted = l.NotPosted }).ToList(),
        Fields = FieldChanges(r),
    };

    /// <summary>«Молоко: цена продажи 100 → 110» для окон истории и журнала (5 языков); не применённое поле помечено.</summary>
    public static string FieldText(ReceiptEditAuditField f)
    {
        var label = f.Field switch
        {
            "sale_price" => Tr.T("цена продажи", "сатуу баасы", "sale price", "satış fiyatı", "sotuv narxi"),
            "markup_percent" => Tr.T("наценка %", "үстөк %", "markup %", "kâr %", "ustama %"),
            "quantity" => Tr.T("количество", "саны", "quantity", "miktar", "miqdor"),
            _ => f.Field,
        };
        var pending = f.Status is "failed" or "unknown" or "pending" or "applying" or "abandoned" or "not_posted" or "partial"
            ? Tr.T(" (не применено)", " (колдонулган жок)", " (not applied)", " (uygulanmadı)", " (qo'llanmadi)") : "";
        return $"{f.Subject}: {label} {f.Before} → {f.After}{pending}";
    }

    private static string N(double? v) => v is { } x ? x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?";

    /// <summary>2026-11-01: «поле: было → стало» по правке — количество каждой строки и цена продажи / наценка товара (шаги price). Для журнала, ленты и окон истории.</summary>
    public static List<ReceiptEditAuditField> FieldChanges(ReceiptEditRecord r)
    {
        var result = new List<ReceiptEditAuditField>();
        foreach (var l in r.Lines)
            result.Add(new ReceiptEditAuditField { Field = "quantity", Subject = l.Name, ProductId = l.ProductId, Before = N(l.QtyBefore), After = N(l.QtyAfter), Status = l.NotPosted ? "not_posted" : r.Status is "failed" or "applying" ? r.Status : "done" });
        foreach (var s in r.Steps.Where(s => s.Kind == "price"))
            foreach (var i in s.Items)
            {
                result.Add(new ReceiptEditAuditField { Field = "sale_price", Subject = i.Name, ProductId = i.ProductId, Before = N(i.SaleBefore), After = N(i.SaleAfter), Status = s.Status });
                if (i.MarkupAfter is not null)
                    result.Add(new ReceiptEditAuditField { Field = "markup_percent", Subject = i.Name, ProductId = i.ProductId, Before = N(i.MarkupBefore), After = N(i.MarkupAfter), Status = s.Status });
            }
        return result;
    }

    /// <summary>Записать событие правки. Никогда не бросает: сбой журнала не должен ломать саму правку (пишется WARNING).</summary>
    public static void Write(ReceiptEditRecord record, string evt)
    {
        try
        {
            Append(FromRecord(record, evt));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Журнал правок накладных: запись не удалась ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Правка, поставленная в очередь без связи: строки известны только по id (названия окну известны, но не очереди).</summary>
    public static void WriteQueued(QueuedEdit q, string docNumber, string supplier, DateTime? receiptAt, string byId, string byRole, IReadOnlyList<ReceiptEditAuditLine>? lines = null)
    {
        try
        {
            Append(new ReceiptEditAuditEntry
            {
                Ts = DateTime.Now, Event = "queued", EditId = q.Id, ReceiptId = q.ReceiptId, DocNumber = docNumber, Supplier = supplier, ReceiptAt = receiptAt,
                Version = q.BaseVersion + 1, Kind = q.RevertedVersion > 0 ? "revert" : "edit", By = q.By, ById = byId, ByRole = byRole, Device = Environment.MachineName,
                Reason = q.Reason, Compensation = q.Compensation, Backdated = IsBackdated(receiptAt, q.CreatedAt), AgeDays = receiptAt is { } at ? (int)Math.Floor((q.CreatedAt - at).TotalDays) : 0,
                Lines = lines?.ToList() ?? q.NewQty.Select(kv => new ReceiptEditAuditLine { LineId = kv.Key, QtyAfter = kv.Value }).ToList(),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Журнал правок накладных: запись очереди не удалась ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Поднимать ли оповещение «правка задним числом» по этому приходу: не чаще раза в сутки на накладную. Вызов засчитывает показ.</summary>
    public static bool TakeBackdatedAlert(string receiptId, DateTime now)
    {
        lock (Sync)
        {
            Prime();
            if (Flagged.TryGetValue(receiptId, out var last) && now - last < TimeSpan.FromHours(24))
                return false;
            Flagged[receiptId] = now;
            return true;
        }
    }

    private static void Append(ReceiptEditAuditEntry e)
    {
        if (ReadOnlyProvider() || FilePath is not { } path)
            return;
        lock (Sync)
        {
            Prime();
            var key = $"{e.EditId}|{e.Event}|{e.Version}|{e.StepsDone}";
            if (!Written.Add(key))
                return; // то же событие этой правки уже записано — повтор идемпотентен
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Rotate(path);
            var line = JsonSerializer.Serialize(e, Json) + "\n";
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = new UTF8Encoding(false).GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true); // на диск, а не в кэш системы
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes)
                return;
            var archive = Path.Combine(Path.GetDirectoryName(path)!, $"purchase-edits-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            File.Move(path, archive, overwrite: false);
            PosLogger.Log($"Журнал правок накладных вырос до {MaxBytes / 1024 / 1024} МБ — отправлен в архив {Path.GetFileName(archive)}.", "INFO");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Журнал правок накладных: ротация не удалась ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Один раз за запуск читает текущий файл: какие события уже записаны и по каким приходам недавно поднимали оповещение.</summary>
    private static void Prime()
    {
        if (_primed)
            return;
        _primed = true;
        foreach (var e in ReadFile(FilePath))
        {
            Written.Add($"{e.EditId}|{e.Event}|{e.Version}|{e.StepsDone}");
            if (e.Backdated && e.Event is "done" or "partial")
                Flagged[e.ReceiptId] = e.Ts;
        }
    }

    /// <summary>Все события (архивы и текущий файл), по времени. Битые строки пропускаются.</summary>
    public static IReadOnlyList<ReceiptEditAuditEntry> ReadAll()
    {
        var dir = DirProvider();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<ReceiptEditAuditEntry>();
        var files = Directory.GetFiles(dir, "purchase-edits*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
        lock (Sync)
            return files.SelectMany(ReadFile).OrderBy(e => e.Ts).ToList();
    }

    private static List<ReceiptEditAuditEntry> ReadFile(string? path)
    {
        var result = new List<ReceiptEditAuditEntry>();
        if (path is null || !File.Exists(path))
            return result;
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Журнал правок накладных не прочитан ({ex.Message}).", "WARNING");
            return result;
        }
        foreach (var line in lines)
        {
            try
            {
                if (JsonSerializer.Deserialize<ReceiptEditAuditEntry>(line, Json) is { } e)
                    result.Add(e);
            }
            catch (JsonException)
            {
                // битая строка (обрыв питания при записи) — остальные читаем
            }
        }
        return result;
    }
}
