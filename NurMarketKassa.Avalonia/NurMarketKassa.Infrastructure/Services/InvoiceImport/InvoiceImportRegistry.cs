using System.Security.Cryptography;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: свой JSON рядом с памятью сопоставлений «Массового добавления» (%APPDATA%\NurMarketKassa\purchases\invoice-imports.json):
// какие накладные уже загружались (хэш файла, поставщик + номер) и какие столбцы выбирали для таких заголовков. База pos_local.db не трогается.
// Запись — атомарно (временный файл + подмена). Хранится не больше 500 записей.
//
// 2026-10-27 (ревью Ск-2): ФОРМУЛА КЛЮЧЕЙ ДУБЛЯ — не зависит от поставщика, выбранного в окне (раньше смена поставщика «Без поставщика» → «создан ＋» меняла ключ,
// и та же накладная проходила второй раз: приход и долг поставщику удваивались). Мобилка (NurMarketMobile/lib/core/invoice/invoice_match.dart, InvoiceGuard.keys)
// обязана считать ТЕ ЖЕ строки. Все значения — нижний регистр, обрезаны по краям; sha256 hex строчными (UTF-8):
//   num = номер накладной (как в окне, кассир мог поправить), d = «Г-М-Д» без нулей или пусто, inn = цифры ИНН поставщика ИЗ ФАЙЛА (9–14 цифр) или пусто,
//   tot = итог из файла в тыйынах: round(100 × (всего с НДС, иначе без НДС)) или пусто, если итога нет или он ≤ 0.
//   K1 = sha256("file|" + sha256-файла)                      — всегда: тот же файл, кто бы ни был поставщиком;
//   K2 = sha256("doc|" + num + "|" + d + "|" + inn)           — если num не пусто и (inn не пусто или tot пусто): та же накладная в другом формате;
//   K3 = sha256("sum|" + num + "|" + d + "|" + tot)           — если num не пусто и tot не пусто: то же, когда в файле нет ИНН.
// Накладная считается загруженной, если совпал ЛЮБОЙ ключ. Запись ставится при переносе в приёмку (Posted=false) и помечается Posted=true, когда приёмку
// провели («Оприходовать»); повторная сверка с проведёнными — перед «Оприходовать» (см. FindPostedDuplicate).

public sealed class InvoiceImportEntry
{
    public string Hash { get; set; } = "";
    public string SupplierKey { get; set; } = "";

    /// <summary>2026-10-27 (Ск-2): название поставщика для предупреждения «уже загружалась (поставщик …)».</summary>
    public string SupplierName { get; set; } = "";

    /// <summary>2026-10-27 (Ск-2): код записи — чтобы отличить её от другой записи с теми же ключами.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>2026-10-27 (Ск-2): приёмка с этой накладной проведена («Оприходовать»). Пока false — накладная только лежит в списке приёмки.</summary>
    public bool Posted { get; set; }

    public string Number { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime LoadedAt { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public int Items { get; set; }
    public double Total { get; set; }

    /// <summary>Ключи защиты от дубля (см. InvoiceImportRegistry.Keys).</summary>
    public List<string> Keys { get; set; } = new();
}

public static class InvoiceImportRegistry
{
    private const int MaxEntries = 500;
    private const int MaxMappings = 50;

    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases");

    private static string FilePath => Path.Combine(DataDir, "invoice-imports.json");
    private static readonly object Sync = new();

    public sealed class MappingEntry
    {
        public DateTime SavedAt { get; set; }
        public Dictionary<string, int> Columns { get; set; } = new();
    }

    public sealed class Store
    {
        public List<InvoiceImportEntry> Invoices { get; set; } = new();
        public Dictionary<string, MappingEntry> Mappings { get; set; } = new();
    }

    private static Store Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: журнал загруженных накладных не прочитан ({ex.Message}) — начинаем с чистого.", "WARNING");
        }
        return new Store();
    }

    private static void Write(Store store)
    {
        try
        {
            if (store.Invoices.Count > MaxEntries)
                store.Invoices = store.Invoices.OrderByDescending(e => e.LoadedAt).Take(MaxEntries).ToList();
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: журнал загруженных накладных не сохранён ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ хэш и ключи

    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Поставщик для ключей дубля — как в мобилке (InvoiceGuard.keys): id из списка NurCRM, иначе ИНН (цифры), иначе название; нижний регистр.</summary>
    public static string SupplierKey(string? inn, string? name, string? id = null)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return id.Trim().ToLowerInvariant();
        var digits = InvoiceText.CleanInn(inn);
        return digits.Length > 0 ? digits : (name ?? "").Trim().ToLowerInvariant();
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>Ключи накладной — формула в начале файла (K1/K2/K3), от поставщика в окне не зависят. <paramref name="fileInn"/> и <paramref name="fileTotal"/> — из файла.</summary>
    public static List<string> Keys(string hash, string number, DateTime? date, string? fileInn, double? fileTotal)
    {
        var num = (number ?? "").Trim().ToLowerInvariant();
        var d = date is null ? "" : $"{date.Value.Year}-{date.Value.Month}-{date.Value.Day}";
        var inn = InvoiceText.CleanInn(fileInn);
        var tot = fileTotal is > 0 ? Math.Round(fileTotal.Value * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "";
        var keys = new List<string> { Sha($"file|{(hash ?? "").ToLowerInvariant()}") };
        if (num.Length > 0 && (inn.Length > 0 || tot.Length == 0))
            keys.Add(Sha($"doc|{num}|{d}|{inn}"));
        if (num.Length > 0 && tot.Length > 0)
            keys.Add(Sha($"sum|{num}|{d}|{tot}"));
        return keys;
    }

    /// <summary>Итог из файла для ключа K3: «всего», иначе «без НДС».</summary>
    public static double? FileTotalOf(InvoiceHeader header) => header.TotalFromFile is > 0 ? header.TotalFromFile : header.NetTotalFromFile;

    // ------------------------------------------------------------------ дубли

    /// <summary>Накладная уже переносилась в приёмку (или проведена): совпал любой из ключей <see cref="Keys"/>. Возвращает самую свежую запись.</summary>
    public static InvoiceImportEntry? FindDuplicate(string hash, string number, DateTime? date, string? fileInn, double? fileTotal)
    {
        var keys = Keys(hash, number, date, fileInn, fileTotal);
        lock (Sync)
        {
            foreach (var e in Read().Invoices.OrderByDescending(x => x.LoadedAt))
            {
                if (e.Keys.Any(keys.Contains))
                    return e;
            }
        }
        return null;
    }

    /// <summary>2026-10-27 (Ск-2): перед «Оприходовать» — эта накладная (запись <paramref name="pending"/>) уже была ПРОВЕДЕНА раньше другой записью с теми же ключами
    /// (например, из другого окна или программы владельца на этом ПК).</summary>
    public static InvoiceImportEntry? FindPostedDuplicate(InvoiceImportEntry pending)
    {
        lock (Sync)
        {
            foreach (var e in Read().Invoices.OrderByDescending(x => x.LoadedAt))
            {
                if (e.Posted && e.Id != pending.Id && e.Keys.Any(pending.Keys.Contains))
                    return e;
            }
        }
        return null;
    }

    /// <summary>2026-10-27 (Ск-2): перед «Оприходовать» — накладные из этой приёмки, которые уже проведены ранее ИЛИ лежат в списке дважды. Пусто — можно проводить.</summary>
    public static List<InvoiceImportEntry> FindRepeats(IReadOnlyList<InvoiceImportEntry> pending)
    {
        var result = new List<InvoiceImportEntry>();
        for (var i = 0; i < pending.Count; i++)
        {
            if (FindPostedDuplicate(pending[i]) is { } posted)
                result.Add(posted);
            else if (pending.Where((p, j) => j != i && p.Id != pending[i].Id && p.Keys.Any(pending[i].Keys.Contains)).Any())
                result.Add(pending[i]);
        }
        return result;
    }

    /// <summary>2026-10-27 (Ск-2): приёмка проведена — записи накладных помечаются проведёнными.</summary>
    public static void MarkPosted(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0)
            return;
        lock (Sync)
        {
            var store = Read();
            var any = false;
            foreach (var e in store.Invoices.Where(x => set.Contains(x.Id) && !x.Posted))
            {
                e.Posted = true;
                any = true;
            }
            if (any)
                Write(store);
        }
    }

    /// <summary>Запомнить: накладная перенесена в приёмку.</summary>
    public static void Record(InvoiceImportEntry entry)
    {
        lock (Sync)
        {
            var store = Read();
            // 2026-10-27 (Ск-2): непроведённую запись с теми же ключами заменяем, а проведённую оставляем — иначе повторная загрузка «по подтверждению»
            // стёрла бы след того, что накладная уже оприходована.
            store.Invoices.RemoveAll(e => !e.Posted && e.Keys.Any(entry.Keys.Contains));
            store.Invoices.Add(entry);
            Write(store);
        }
    }

    public static IReadOnlyList<InvoiceImportEntry> All()
    {
        lock (Sync)
            return Read().Invoices.OrderByDescending(e => e.LoadedAt).ToList();
    }

    // ------------------------------------------------------------------ память сопоставления столбцов

    private static string Signature(IReadOnlyList<string> headers) => ProductImportMapping.Signature(headers);

    public static InvoiceColumnMapping? TryLoadMapping(IReadOnlyList<string> headers)
    {
        lock (Sync)
        {
            if (!Read().Mappings.TryGetValue(Signature(headers), out var entry) || entry.Columns.Count == 0)
                return null;
            var mapping = new InvoiceColumnMapping { FromMemory = true };
            foreach (var (name, column) in entry.Columns)
            {
                if (Enum.TryParse<InvoiceField>(name, out var field) && column >= 0 && column < headers.Count)
                    mapping.Set(field, column);
            }
            return mapping.Columns.Count == 0 ? null : mapping;
        }
    }

    public static void SaveMapping(IReadOnlyList<string> headers, InvoiceColumnMapping mapping)
    {
        lock (Sync)
        {
            var store = Read();
            store.Mappings[Signature(headers)] = new MappingEntry { SavedAt = DateTime.Now, Columns = mapping.Columns.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) };
            foreach (var old in store.Mappings.OrderByDescending(kv => kv.Value.SavedAt).Skip(MaxMappings).Select(kv => kv.Key).ToList())
                store.Mappings.Remove(old);
            Write(store);
        }
    }
}
