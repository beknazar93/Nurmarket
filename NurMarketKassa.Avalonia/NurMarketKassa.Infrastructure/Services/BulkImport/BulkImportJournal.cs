using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

// 2026-10-30, приёмка 1.17.65 («Мастер загрузки»): журнал массовой загрузки. Повторный запуск того же файла не должен дублировать товары и приходы,
// а после обрыва загрузка должна продолжаться с места обрыва. Журнал — один файл на «отпечаток» (хэш содержимого таблицы + режим) в
// %APPDATA%\NurMarketKassa\bulk-journal-<хэш>.json, запись атомарная (AtomicFile). Ключ строки — штрихкод; ключ пакета — хэш + номер пакета
// (пакет = 100 строк, см. BulkCardsLoader.BatchSize). Записанные на сервер штрихкоды попадают в Done, следующий запуск их пропускает.
// Режим карточек: после полностью успешной загрузки журнал удаляется (повтор безопасен и так — каталог сравнивается с файлом).
// Режим прихода: журнал остаётся (Completed), иначе тот же файл прибавил бы остаток второй раз.

public sealed class BulkImportJournal
{
    /// <summary>Только для стенда: другая папка (по умолчанию — %APPDATA%\NurMarketKassa).</summary>
    public static string? DirOverride { get; set; }

    private const int KeepDays = 30;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string Hash { get; set; } = "";
    public string Mode { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Сколько строк было в последней загрузке (для сообщения «записано N из M»).</summary>
    public int Total { get; set; }
    /// <summary>Сколько пакетов по 100 уже записано (ключ пакета — хэш + этот номер).</summary>
    public int BatchesDone { get; set; }
    public bool Completed { get; set; }
    /// <summary>Штрихкоды, уже записанные на сервер (карточка записана / приход принят очередью).</summary>
    public List<string> Done { get; set; } = [];
    /// <summary>Режим прихода: пачка в фоновой очереди (BulkReceivingFeeder) и порядок штрихкодов в ней — чтобы после закрытия окна свернуть итог в Done.</summary>
    public string? ReceiptBatchId { get; set; }
    public List<string> ReceiptOrder { get; set; } = [];

    private HashSet<string>? _set;
    private readonly object _gate = new();

    private static string Dir => DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName);

    public static string PathFor(string hash) => Path.Combine(Dir, $"bulk-journal-{hash}.json");

    [JsonIgnore] public string FilePath => PathFor(Hash);

    /// <summary>Отпечаток таблицы файла (все ячейки всех строк) и режима загрузки. Тот же файл → тот же отпечаток; другой режим → другой журнал.</summary>
    public static string HashOf(ProductFileTable table, string mode)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes(mode + "\u0001"));
        foreach (var row in table.Rows)
        {
            foreach (var cell in row.Cells)
                h.AppendData(Encoding.UTF8.GetBytes((cell ?? "").Trim() + "\u001f"));
            h.AppendData([(byte)'\n']);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    /// <summary>Журнал для этого отпечатка: прочитанный или новый (чужая компания, старше 30 дней или испорченный файл — новый).</summary>
    public static BulkImportJournal Open(string hash, string mode, string fileName)
    {
        var company = CompanyInfoService.LastCompany?.Id ?? "";
        try
        {
            var text = AtomicFile.ReadAllText(PathFor(hash));
            if (text is not null && JsonSerializer.Deserialize<BulkImportJournal>(text, Json) is { } j && j.Hash == hash
                && (j.CompanyId.Length == 0 || company.Length == 0 || string.Equals(j.CompanyId, company, StringComparison.OrdinalIgnoreCase))
                && (DateTime.Now - j.UpdatedAt).TotalDays <= KeepDays)
                return j;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал массовой загрузки не прочитан ({ex.Message}) — начинаем заново.", "WARNING");
        }
        return new BulkImportJournal { Hash = hash, Mode = mode, CompanyId = company, FileName = fileName, StartedAt = DateTime.Now, UpdatedAt = DateTime.Now };
    }

    [JsonIgnore]
    public int DoneCount
    {
        get
        {
            lock (_gate)
                return Set().Count;
        }
    }

    public bool IsDone(string barcode)
    {
        lock (_gate)
            return Set().Contains(Key(barcode));
    }

    public void MarkDone(IEnumerable<string> barcodes)
    {
        lock (_gate)
        {
            var set = Set();
            foreach (var b in barcodes)
                if (set.Add(Key(b)))
                    Done.Add(Key(b));
        }
    }

    public void ClearDone()
    {
        lock (_gate)
        {
            Done.Clear();
            _set = null;
            Completed = false;
        }
    }

    /// <summary>Атомарная запись. Не получилось — в журнал кассы; загрузка не прерывается (хуже — повторная запись той же строки, а она безопасна).</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                UpdatedAt = DateTime.Now;
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал массовой загрузки не записан: {ex.Message}", "WARNING");
            }
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            try
            {
                foreach (var p in new[] { FilePath, FilePath + ".bak", FilePath + ".tmp" })
                    if (File.Exists(p))
                        File.Delete(p);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал массовой загрузки не удалён: {ex.Message}", "WARNING");
            }
        }
    }

    /// <summary>Режим прихода: пачка в очереди закончена (или её уже забыли) — штрихкоды принятых строк уходят в Done. Идёт ещё — ничего не делает.
    /// Строки «нужна проверка» тоже считаются отправленными (повторно их слать нельзя: исход неизвестен, остаток проверяют вручную).</summary>
    public void FoldReceipt(BulkBatchInfo? info)
    {
        if (ReceiptBatchId is null || (info is not null && !info.Finished))
            return;
        var sent = ReceiptOrder.Count;
        if (info is { Cancelled: true })
            sent = Math.Max(0, ReceiptOrder.Count - info.NotSent);
        var failed = info is null
            ? new HashSet<string>()
            : info.FailedRows.Where(f => !f.NeedsCheck).Select(f => Key(f.Barcode)).ToHashSet();
        MarkDone(ReceiptOrder.Take(sent).Where(b => !failed.Contains(Key(b))));
        Completed = sent == ReceiptOrder.Count && failed.Count == 0;
        ReceiptBatchId = null;
        ReceiptOrder = [];
        Save();
    }

    private HashSet<string> Set() => _set ??= new HashSet<string>(Done.Select(Key), StringComparer.Ordinal);

    private static string Key(string barcode) => (barcode ?? "").Trim();
}
