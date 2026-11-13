using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Одна проданная единица с серийным номером / IMEI.</summary>
public sealed class SerialRecord
{
    /// <summary>Id продажи на сервере (у офлайн-чека — местный id записи очереди).</summary>
    public string SaleId { get; set; } = "";
    public string ReceiptNo { get; set; } = "";
    public DateTime SoldAt { get; set; }
    public string ProductId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>Срок гарантии в месяцах на момент продажи (0 — гарантии нет).</summary>
    public int WarrantyMonths { get; set; }
    /// <summary>Гарантия до (включительно); null — без гарантии.</summary>
    public DateTime? WarrantyUntil { get; set; }
}

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Электроника», ЭЛ2–ЭЛ4): местный журнал «чек ↔ серийник ↔ дата ↔ гарантия до» этой кассы.
/// До пунктов Б-ЭЛ1…Б-ЭЛ4 сервера серийников на сервере нет, поэтому они хранятся файлом serial-journal.json в папке данных кассы
/// (схема pos_local.db не меняется; запись — AtomicFile.Update под межпроцессным замком). Поиск — только по чекам ЭТОЙ кассы.
/// Запись делается после УСПЕШНОЙ продажи, а не при добавлении товара в чек. Хранится 3 года (гарантия).
/// </summary>
public static class SerialJournal
{
    private static readonly object Gate = new();

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.KassaLocal, "serial-journal.json");

    public static event Action? Changed;

    /// <summary>Читает серийники строк корзины (поле <see cref="ElectronicsProfile.SerialsField"/>) в записи журнала. Строки без номеров пропускаются.</summary>
    public static List<SerialRecord> ExtractFromCart(string? cartJson, string? saleId, string? receiptNo, DateTime soldAt)
    {
        var result = new List<SerialRecord>();
        if (string.IsNullOrWhiteSpace(cartJson))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(cartJson);
            foreach (var item in CartDisplayHelper.EnumerateItems(doc.RootElement))
            {
                var serials = ReadSerials(item);
                if (serials.Count == 0)
                    continue;
                var months = ReadMonths(item);
                foreach (var serial in serials)
                    result.Add(new SerialRecord
                    {
                        SaleId = saleId ?? "",
                        ReceiptNo = receiptNo ?? "",
                        SoldAt = soldAt,
                        ProductId = CartDisplayHelper.TryProductId(item) ?? "",
                        Title = CartDisplayHelper.ItemName(item).Trim(),
                        Serial = serial,
                        WarrantyMonths = months,
                        WarrantyUntil = months > 0 ? WarrantyCard.WarrantyUntil(soldAt, months) : null,
                    });
            }
        }
        catch (JsonException ex)
        {
            PosLogger.Log($"Серийники чека не прочитаны: {ex.Message}", "WARNING");
        }
        return result;
    }

    /// <summary>Номера строки корзины (из JSON строки). 2026-11-13 (сервер sn1311): если у строки нет своего поля serials (корзина пришла с сервера), берётся serial_numbers сервера.</summary>
    public static List<string> ReadSerials(JsonElement item)
    {
        var list = new List<string>();
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(ElectronicsProfile.SerialsField, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } text)
                    list.Add(text);
        return list.Count > 0 ? list : ServerSerials.LineSerials(item);
    }

    /// <summary>Срок гарантии строки корзины в месяцах (0 — нет).</summary>
    public static int ReadMonths(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(ElectronicsProfile.WarrantyMonthsField, out var m)
        && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var months) && months > 0
            ? Math.Min(months, 120)
            : 0;

    /// <summary>Записать серийники успешной продажи. Только в сфере «Электроника»; ошибка записи чек не ломает.</summary>
    public static void RecordSale(string? saleId, string? receiptNo, DateTime soldAt, string? cartJson)
    {
        if (!ElectronicsProfile.IsActive)
            return;
        try
        {
            var records = ExtractFromCart(cartJson, saleId, receiptNo, soldAt);
            if (records.Count == 0)
                return;
            Append(records);
            PosLogger.Log($"Серийники продажи записаны в журнал этой кассы: {records.Count}.", "CART");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Серийники продажи не записаны в журнал: {ex.Message}", "WARNING");
        }
    }

    public static void Append(IReadOnlyCollection<SerialRecord> records)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.Update(FilePath, text => AppendTo(text, records));
        }
        try { Changed?.Invoke(); } catch { /* журнал — не повод ломать продажу */ }
    }

    /// <summary>Новый текст файла: прежние записи + новые; записи этой же продажи и номера не дублируются; старше 3 лет убраны.</summary>
    internal static string AppendTo(string? currentText, IReadOnlyCollection<SerialRecord> records)
    {
        var all = new List<SerialRecord>();
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            try { all = JsonSerializer.Deserialize<List<SerialRecord>>(currentText) ?? new(); }
            catch (JsonException ex) { PosLogger.Log($"Журнал серийников не прочитан: {ex.Message}", "WARNING"); }
        }
        foreach (var r in records)
            if (!all.Any(x => x.SaleId.Length > 0 && x.SaleId == r.SaleId && x.Serial == r.Serial && x.ProductId == r.ProductId))
                all.Add(r);
        var cutoff = DateTime.Now.AddYears(-3);
        all.RemoveAll(r => r.SoldAt < cutoff);
        return JsonSerializer.Serialize(all);
    }

    public static IReadOnlyList<SerialRecord> ReadAll()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<SerialRecord>>(AtomicFile.ReadWithRetry(FilePath)) ?? new();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал серийников не прочитан: {ex.Message}", "WARNING");
            }
            return new List<SerialRecord>();
        }
    }

    /// <summary>Минимум знаков запроса: меньше — почти все чеки подойдут.</summary>
    public const int MinQueryLength = 3;

    /// <summary>Поиск по серийнику / IMEI: целиком, по окончанию («последние 6 цифр») или по части. Новые продажи — первыми.</summary>
    public static List<SerialRecord> Find(string? query) => FindIn(ReadAll(), query);

    internal static List<SerialRecord> FindIn(IEnumerable<SerialRecord> all, string? query)
    {
        var q = ImeiValidator.Normalize(query);
        if (q.Length < MinQueryLength)
            return new List<SerialRecord>();
        return all.Where(r => ImeiValidator.Normalize(r.Serial).Contains(q, StringComparison.Ordinal))
            .OrderByDescending(r => r.SoldAt)
            .ToList();
    }

    /// <summary>Записи одной продажи (для повторной печати чека и талона).</summary>
    public static List<SerialRecord> ForSale(string? saleId) =>
        string.IsNullOrWhiteSpace(saleId)
            ? new List<SerialRecord>()
            : ReadAll().Where(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Номер уже продан этой кассой (для предупреждения «уже продан в чеке №…»).</summary>
    public static SerialRecord? FindSold(string? serial)
    {
        var s = ImeiValidator.Normalize(serial);
        return s.Length == 0
            ? null
            : ReadAll().Where(r => ImeiValidator.Normalize(r.Serial) == s).OrderByDescending(r => r.SoldAt).FirstOrDefault();
    }

    /// <summary>Дата для вывода.</summary>
    public static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
