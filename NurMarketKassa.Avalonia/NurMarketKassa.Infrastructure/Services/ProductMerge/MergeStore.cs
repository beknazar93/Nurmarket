using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.ProductMerge;

/// <summary>Карточка товара ТАК, КАК её отдал сервер перед слиянием: всё, что нужно, чтобы вернуть её обратно (код, доп. коды, цены, остаток, категория, фото).</summary>
public sealed record MergeCard(
    string Id,
    string Name,
    string? Barcode,
    List<MergeAltCode> Alt,
    double Stock,
    double? Price,
    double? PurchasePrice,
    string? Category,
    string? Brand,
    string? Unit,
    List<string> Images,
    bool Archived);

/// <summary>Файл-бэкап слияния: исходные карточки главного и источников + что решил владелец. Пишется ДО первой правки на сервере.</summary>
public sealed record MergeBackup(
    string MergeId,
    DateTime CreatedUtc,
    string Account,
    string Who,
    string MainId,
    string? NewMainTitle,
    List<MergeCard> Cards,
    Dictionary<string, string> VariantNames);

public sealed class MergeJournalSource
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Barcode { get; set; }
    public string VariantName { get; set; } = "";

    /// <summary>Сколько единиц остатка перенесено главному (по факту, с сервера).</summary>
    public double MovedStock { get; set; }

    /// <summary>pending — ещё не обработан; done — слит; failed — не вышло (частичные шаги отменены); rolled_back — возвращён откатом; check — исход неизвестен, проверить вручную.</summary>
    public string State { get; set; } = "pending";

    public bool Archived { get; set; }
    public List<string> AddedCodes { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>Запись журнала слияний: дата, кто, что с чем, чем кончилось и откатывали ли.</summary>
public sealed class MergeJournalEntry
{
    public string Id { get; set; } = "";
    public DateTime WhenUtc { get; set; }
    public string Account { get; set; } = "";
    public string Who { get; set; } = "";
    public string MainId { get; set; } = "";
    public string MainTitle { get; set; } = "";
    public string? NewMainTitle { get; set; }
    public string BackupFile { get; set; } = "";

    /// <summary>running — идёт; done — все источники слиты; partial — часть слита, часть нет; failed — ничего не слито; rolled_back — откат выполнен; check — проверить вручную.</summary>
    public string Status { get; set; } = "running";

    public List<MergeJournalSource> Sources { get; set; } = [];
    public double? OldPurchase { get; set; }
    public double? NewPurchase { get; set; }
    public DateTime? RolledBackUtc { get; set; }
    public string? RolledBackBy { get; set; }
    public string? Note { get; set; }

    /// <summary>2026-11-21: «server» — слияние выполнил сервер (POST main/products/merge/); «manual» или пусто (старые записи) — запасной ручной путь через карточки.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Номер слияния на сервере (merge_id): по нему откат — POST main/products/merge/&lt;id&gt;/undo/ (30 суток).</summary>
    public string? ServerMergeId { get; set; }

    public int ServerMovedBarcodes { get; set; }

    public double ServerMovedQty { get; set; }

    /// <summary>Документ склада count_fix (причина regrade «Пересорт»), которым сервер перенёс остаток.</summary>
    public string? ServerStockDocument { get; set; }

    /// <summary>Ключ повтора (Idempotency-Key) серверного слияния — для разбора, если связь оборвалась.</summary>
    public string? IdempotencyKey { get; set; }

    public bool IsServer => string.Equals(Mode, "server", StringComparison.Ordinal);

    /// <summary>Серверное слияние откатывается только при известном merge_id; ручное — как раньше.</summary>
    public bool CanRollback => Status is "done" or "partial" or "check" && Sources.Any(s => s.State is "done" or "check") && (!IsServer || !string.IsNullOrWhiteSpace(ServerMergeId));
}

/// <summary>Файлы слияний в папке данных программы: бэкапы (JSON + CSV), журнал, запомненные отказы «объединять не надо». Всё лежит в <c>…\NurMarketKassa|NurMarketOwner\product-merge</c>.</summary>
public static class MergeStore
{
    /// <summary>Только для тестов: другая папка.</summary>
    internal static string? DirOverride { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Dir => DirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "product-merge");

    private static string JournalPath => Path.Combine(Dir, "merge-journal.json");

    private static string DeclinedPath => Path.Combine(Dir, "merge-declined.json");

    public static string BackupPath(string mergeId) => Path.Combine(Dir, $"merge-{mergeId}.json");

    public static string NewMergeId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    // ───────────── бэкап ─────────────

    /// <summary>Пишет бэкап (JSON — для отката, CSV — чтобы глазами). Любой сбой — исключение: слияние без бэкапа не начинается.</summary>
    public static string SaveBackup(MergeBackup backup)
    {
        Directory.CreateDirectory(Dir);
        var path = BackupPath(backup.MergeId);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(backup, Json));
        var check = File.Exists(path) ? File.ReadAllText(path) : "";
        if (check.Length < 10 || JsonSerializer.Deserialize<MergeBackup>(check, Json) is not { Cards.Count: > 0 })
            throw new IOException("Бэкап слияния не записался или не читается: " + path);
        File.WriteAllText(Path.ChangeExtension(path, ".csv"), BackupCsv(backup), new UTF8Encoding(true));
        return path;
    }

    public static MergeBackup? LoadBackup(string mergeId)
    {
        try
        {
            var path = BackupPath(mergeId);
            return File.Exists(path) ? JsonSerializer.Deserialize<MergeBackup>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Слияние товаров: бэкап {mergeId} не прочитан ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Num(double? v) => v is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";

    internal static string BackupCsv(MergeBackup b)
    {
        var sb = new StringBuilder();
        sb.Append("id;роль;название;штрихкод;доп. штрихкоды;цена;закупка;остаток;категория;бренд;единица;фото\r\n");
        foreach (var c in b.Cards)
            sb.Append(string.Join(';', Csv(c.Id), c.Id == b.MainId ? "главный" : "источник", Csv(c.Name), Csv(c.Barcode),
                Csv(string.Join(" | ", c.Alt.Select(a => a.Barcode + (string.IsNullOrWhiteSpace(a.Name) ? "" : " (" + a.Name + ")")))),
                Num(c.Price), Num(c.PurchasePrice), Num(c.Stock), Csv(c.Category), Csv(c.Brand), Csv(c.Unit), Csv(string.Join(" | ", c.Images)))).Append("\r\n");
        return sb.ToString();
    }

    // ───────────── журнал ─────────────

    public static List<MergeJournalEntry> LoadJournal(string? account = null)
    {
        try
        {
            if (!File.Exists(JournalPath))
                return [];
            var text = AtomicFile.ReadAllText(JournalPath);
            var all = string.IsNullOrWhiteSpace(text) ? [] : JsonSerializer.Deserialize<List<MergeJournalEntry>>(text, Json) ?? [];
            return (account is null ? all : all.Where(e => string.Equals(e.Account, account, StringComparison.OrdinalIgnoreCase)).ToList())
                .OrderByDescending(e => e.WhenUtc).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Слияние товаров: журнал не прочитан ({ex.GetType().Name}).", "WARNING");
            return [];
        }
    }

    /// <summary>Добавляет запись или заменяет по Id.</summary>
    public static void SaveJournal(MergeJournalEntry entry)
    {
        Directory.CreateDirectory(Dir);
        AtomicFile.Update(JournalPath, old =>
        {
            var list = string.IsNullOrWhiteSpace(old) ? [] : JsonSerializer.Deserialize<List<MergeJournalEntry>>(old, Json) ?? [];
            list.RemoveAll(e => e.Id == entry.Id);
            list.Add(entry);
            return JsonSerializer.Serialize(list.OrderByDescending(e => e.WhenUtc).Take(500).ToList(), Json);
        });
    }

    // ───────────── «объединять не надо» ─────────────

    public static HashSet<string> Declined()
    {
        try
        {
            if (!File.Exists(DeclinedPath))
                return new HashSet<string>(StringComparer.Ordinal);
            var text = AtomicFile.ReadAllText(DeclinedPath);
            return string.IsNullOrWhiteSpace(text) ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(JsonSerializer.Deserialize<List<string>>(text, Json) ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static void SetDeclined(string groupId, bool declined)
    {
        Directory.CreateDirectory(Dir);
        AtomicFile.Update(DeclinedPath, old =>
        {
            var set = string.IsNullOrWhiteSpace(old) ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(JsonSerializer.Deserialize<List<string>>(old, Json) ?? [], StringComparer.Ordinal);
            if (declined)
                set.Add(groupId);
            else
                set.Remove(groupId);
            return JsonSerializer.Serialize(set.OrderBy(x => x, StringComparer.Ordinal).ToList(), Json);
        });
    }

    // ───────────── план в CSV (режим «только подготовка») ─────────────

    /// <summary>Выгрузка плана слияния в CSV (UTF-8 с BOM, «;»): по строке на каждый товар-источник. Ничего не меняет на сервере.</summary>
    public static void ExportPlanCsv(IEnumerable<MergePlan> plans, string path)
    {
        var sb = new StringBuilder();
        sb.Append("группа;уверенность;главный id;главный;главный штрихкод;источник id;источник;источник штрихкод;название варианта;остаток источника;цена источника;цена главного;предупреждения\r\n");
        foreach (var p in plans.Where(p => p.Main is not null))
            foreach (var v in p.Variants)
                sb.Append(string.Join(';', Csv(p.Group.Id), p.Group.Score.ToString(CultureInfo.InvariantCulture), Csv(p.Main!.Id), Csv(p.NewMainTitle ?? p.Main.Title), Csv(p.Main.Barcode),
                    Csv(v.Source.Id), Csv(v.Source.Title), Csv(v.Source.Barcode), Csv(v.Name), Num(v.Stock), Num(v.Source.Price), Num(p.Main.Price),
                    Csv(string.Join(" | ", p.Warnings.Select(MergeTexts.Warning))))).Append("\r\n");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
