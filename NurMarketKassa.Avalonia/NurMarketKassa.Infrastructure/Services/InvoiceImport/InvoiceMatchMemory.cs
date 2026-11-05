using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-11-03: память поставщика — «как это назвали в накладной этого поставщика → какой товар склада выбрал кассир». Нужна, чтобы тот же товар у того же
// поставщика во второй раз находился сам (Kind = Memory, подтверждение не нужно, но строку можно изменить или «забыть соответствие»).
// Пишутся только соответствия «название → существующий товар»; «это новый» не запоминается. Ключ поставщика — id/ИНН/название из шапки накладной
// (InvoiceImportRegistry.SupplierKey), нет шапки — общий ключ "". Файл рядом с журналом накладных (%APPDATA%\<папка данных>\purchases\invoice-match-memory.json):
// папка «purchases» стирается при смене компании (AccountDataIsolation), поэтому чужой компании записи не попадают. Секретов нет: только названия и id товаров.
// Не больше 5000 записей — старые вытесняются. Запись атомарная (AtomicFile), занятый файл не затирается (DeferWrite).

public static class InvoiceMatchMemory
{
    public const int MaxEntries = 5000;

    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "purchases");

    private static string FilePath => Path.Combine(DataDir, "invoice-match-memory.json");
    private static readonly object Sync = new();

    public sealed class Entry
    {
        public string S { get; set; } = "";   // ключ поставщика
        public string N { get; set; } = "";   // название у поставщика, нормализованное (InvoiceNameParser.MemoryKey)
        public string P { get; set; } = "";   // id товара склада
        public string D { get; set; } = "";   // название как в накладной (для подсказки чтения фото; не длиннее 80 знаков)
        public DateTime At { get; set; }
    }

    private sealed class Store
    {
        public List<Entry> Entries { get; set; } = new();
    }

    /// <summary>Снимок памяти одного поставщика на время построения плана: поиск без обращения к файлу.</summary>
    public sealed class Snapshot
    {
        private readonly Dictionary<string, string> _map;

        internal Snapshot(string supplierKey, Dictionary<string, string> map)
        {
            SupplierKey = supplierKey;
            _map = map;
        }

        public string SupplierKey { get; }
        public int Count => _map.Count;

        /// <summary>id товара, который кассир выбирал для этого названия у этого поставщика (null — не помним).</summary>
        public string? Find(string? invoiceName) => _map.TryGetValue(InvoiceNameParser.MemoryKey(invoiceName), out var id) ? id : null;

        internal void Remove(string? invoiceName) => _map.Remove(InvoiceNameParser.MemoryKey(invoiceName));
    }

    private static Store Read()
    {
        try
        {
            var read = AtomicFile.TryRead(FilePath);
            if (read.Text is { Length: > 0 } text)
                return JsonSerializer.Deserialize<Store>(text) ?? new Store();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: память соответствий не прочитана ({ex.Message}) — работаем без неё.", "WARNING");
        }
        return new Store();
    }

    private static bool CanWrite()
    {
        try
        {
            return AtomicFile.TryRead(FilePath).CanWrite;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Write(Store store)
    {
        try
        {
            if (store.Entries.Count > MaxEntries)
                store.Entries = store.Entries.OrderByDescending(e => e.At).Take(MaxEntries).ToList();
            var text = JsonSerializer.Serialize(store);
            if (!CanWrite())
            {
                // Файл занят (антивирус): запись откладывается, чтобы не затереть прежнее.
                AtomicFile.DeferWrite(FilePath, "Память соответствий накладной", text);
                return;
            }
            AtomicFile.WriteAllTextWithRetry(FilePath, text);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: память соответствий не сохранена ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Снимок памяти поставщика (и общей записи «без поставщика», если у поставщика ничего нет). Пустой снимок — файла нет.</summary>
    public static Snapshot Load(string? supplierKey)
    {
        var key = (supplierKey ?? "").Trim().ToLowerInvariant();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (Sync)
        {
            var entries = Read().Entries;
            // сначала общие, потом записи поставщика поверх — свои сильнее
            foreach (var e in entries.Where(e => e.S.Length == 0 && e.N.Length > 0).OrderBy(e => e.At))
                map[e.N] = e.P;
            if (key.Length > 0)
                foreach (var e in entries.Where(e => e.S == key && e.N.Length > 0).OrderBy(e => e.At))
                    map[e.N] = e.P;
        }
        return new Snapshot(key, map);
    }

    /// <summary>Запомнить соответствия «название → товар» для поставщика. Пустые названия и товары пропускаются.</summary>
    public static void Remember(string? supplierKey, IEnumerable<(string InvoiceName, string ProductId)> pairs)
    {
        var key = (supplierKey ?? "").Trim().ToLowerInvariant();
        var list = pairs.Select(p => (N: InvoiceNameParser.MemoryKey(p.InvoiceName), P: (p.ProductId ?? "").Trim(), D: (p.InvoiceName ?? "").Trim())).Where(p => p.N.Length > 0 && p.P.Length > 0).ToList();
        if (list.Count == 0)
            return;
        lock (Sync)
        {
            var store = Read();
            var now = DateTime.Now;
            foreach (var (n, p, d) in list)
            {
                store.Entries.RemoveAll(e => e.S == key && e.N == n);
                store.Entries.Add(new Entry { S = key, N = n, P = p, D = d.Length > 80 ? d[..80] : d, At = now });
            }
            Write(store);
        }
        PosLogger.Log($"INVOICE_IMPORT: запомнено соответствий поставщика: {list.Count}.", "INVOICE_IMPORT");
    }

    /// <summary>Недавние соответствия по всем поставщикам (для подсказки чтению фото накладной): самые свежие первыми.</summary>
    public static List<(string InvoiceName, string ProductId)> Recent(int max)
    {
        lock (Sync)
        {
            return Read().Entries.Where(e => e.D.Length > 0 && e.P.Length > 0).OrderByDescending(e => e.At).Take(max).Select(e => (e.D, e.P)).ToList();
        }
    }

    /// <summary>«Забыть соответствие»: убрать запись поставщика (и общую) для этого названия.</summary>
    public static void Forget(string? supplierKey, string? invoiceName)
    {
        var key = (supplierKey ?? "").Trim().ToLowerInvariant();
        var n = InvoiceNameParser.MemoryKey(invoiceName);
        if (n.Length == 0)
            return;
        lock (Sync)
        {
            var store = Read();
            if (store.Entries.RemoveAll(e => e.N == n && (e.S == key || e.S.Length == 0)) > 0)
                Write(store);
        }
    }
}
