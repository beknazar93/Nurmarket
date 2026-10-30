using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-30, просьба владельца «пример: вкладка «Покупки» со строками по ВСЕМ документам, фильтры, итог внизу, экспорт» (макет design/Invoice-Doc, раздел «Реестр»).
// Реестр закупок: документы (приходы suppliers/receipts/) и позиции (их строки) одним набором данных. Здесь — модель, отбор, итоги, загрузка страницами
// и копия для работы без связи; окно — PurchasesRegistryView (Avalonia), выгрузки — PurchasesRegistryExport. Новых таблиц в pos_local.db нет:
// копия лежит в %APPDATA%\NurMarketKassa\purchases\registry-<компания>.json (атомарная запись, как остальные файлы закупок).

/// <summary>Статус документа в реестре (взаимоисключающие): исправлен (версия &gt; 1), с возвратом поставщику, проведён.</summary>
public enum RegistryStatus
{
    Posted,
    Returned,
    Edited,
}

/// <summary>Что известно о товаре в каталоге кассы: штрихкод, внутренний код, текущая цена продажи (для расчётной наценки).</summary>
public readonly record struct RegistryCatalogInfo(string Barcode, string Code, double Sale);

/// <summary>Откуда реестр берёт то, чего нет в самом приходе (способ оплаты, каталог, склад, версия).</summary>
public sealed class RegistryContext
{
    /// <summary>Оплата прихода: текст и вид (cash/drawer/partial/debt/"" — неизвестно).</summary>
    public Func<SupplierReceipt, (string Text, string Kind)>? Pay { get; init; }
    public Func<string, RegistryCatalogInfo?>? Catalog { get; init; }
    public IReadOnlyDictionary<string, ReceiptMarkupDoc>? Markups { get; init; }
}

/// <summary>Строка реестра «Позиции» — одна строка прихода.</summary>
public sealed class RegistryLine
{
    public RegistryDoc Doc { get; init; } = null!;
    /// <summary>Номер строки внутри документа (как «№» в примере владельца).</summary>
    public int No { get; init; }
    public string ProductId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Qty { get; init; }
    public double Purchase { get; init; }
    public double Sum { get; init; }
    public MarkupView View { get; init; }
    /// <summary>Закупка выше прошлой закупки этого товара (ReceivingPricing.IsPurchaseRaised) — значок «↑».</summary>
    public bool Raised { get; set; }
    public double PrevPurchase { get; set; }
    internal string? Key { get; set; }
}

/// <summary>Документ реестра — один приход (накладная).</summary>
public sealed class RegistryDoc
{
    public SupplierReceipt Receipt { get; set; } = null!;
    public string Id => Receipt.Id;
    /// <summary>Номер документа ПР-2026-000123 (или прежний короткий код, пока история не сверена).</summary>
    public string Number { get; set; } = "";
    public DateTime? At => Receipt.CreatedAt;
    public string SupplierId => Receipt.SupplierId;
    public string SupplierName => Receipt.SupplierName;
    public string SupplierDoc => Receipt.SupplierDocNumber;
    public DateTime? SupplierDocDate => Receipt.SupplierDocDate;
    public string CreatedBy => Receipt.CreatedBy;
    public double Total => Receipt.Total;
    public double Returned => Receipt.ReturnedAmount;
    public string Warehouse { get; set; } = "";
    public int Items { get; set; }
    public double Qty { get; set; }
    public string Pay { get; set; } = "";
    public string PayKind { get; set; } = "";
    public int Version { get; set; }
    public RegistryStatus Status { get; set; }
    public double? AvgMarkup { get; set; }
    public bool MarkupEstimated { get; set; }
    /// <summary>Строки в ответе списка не пришли (ItemsCount &gt; 0, Lines пуст) — их догружает EnsureLines.</summary>
    public bool LinesMissing { get; set; }
    public List<RegistryLine> Lines { get; set; } = new();
    internal string? Key { get; set; }
}

/// <summary>Итог по фильтру: документов, позиций, штук, сумма закупки, средняя наценка и ожидаемая прибыль.</summary>
public readonly record struct RegistryTotals(int Docs, int Lines, double Qty, double Purchase, double? AvgMarkup, double Profit, bool Estimated, int LinesMissing);

/// <summary>Отбор реестра. From/To — по дате документа; пустое — без ограничения.</summary>
public sealed class RegistryFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? SupplierId { get; set; }
    public string? Warehouse { get; set; }
    public RegistryStatus? Status { get; set; }
    public string Query { get; set; } = "";
}

public static class PurchasesRegistry
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Склад документа: сервер пока склад в приходе не отдаёт — пока пусто; когда поле появится, подключается здесь.</summary>
    public static Func<SupplierReceipt, string>? WarehouseHook { get; set; }

    /// <summary>Версия документа (исправлен vN): поле появится с редактором накладных; 0/1 — не исправлялся.</summary>
    public static Func<SupplierReceipt, int>? VersionHook { get; set; }

    // ------------------------------------------------------------------ сборка

    /// <summary>Документы реестра из приходов (новые сверху). Строки и итоги по документу считаются сразу.</summary>
    public static List<RegistryDoc> Build(IEnumerable<SupplierReceipt> receipts, RegistryContext ctx)
    {
        var docs = new List<RegistryDoc>();
        foreach (var r in receipts)
        {
            var d = new RegistryDoc();
            Fill(d, r, ctx);
            docs.Add(d);
        }
        return docs.OrderByDescending(d => d.At ?? DateTime.MinValue).ToList();
    }

    /// <summary>Заполнить документ из прихода (повторно — когда догрузились строки).</summary>
    public static void Fill(RegistryDoc d, SupplierReceipt r, RegistryContext ctx)
    {
        d.Receipt = r;
        d.Number = DocumentNumbering.ReceiptText(r.Id);
        d.Warehouse = WarehouseHook?.Invoke(r) ?? "";
        d.Version = VersionHook?.Invoke(r) ?? 0;
        (d.Pay, d.PayKind) = ctx.Pay?.Invoke(r) ?? ("", "");
        d.Items = r.ItemsCount > 0 ? r.ItemsCount : r.Lines.Count;
        d.LinesMissing = r.Lines.Count == 0 && r.ItemsCount > 0;
        d.Status = d.Version > 1 ? RegistryStatus.Edited : r.ReturnedAmount > 0.005 ? RegistryStatus.Returned : RegistryStatus.Posted;
        d.Key = null;

        ReceiptMarkupDoc? journal = null;
        ctx.Markups?.TryGetValue(r.Id, out journal);
        var lines = new List<RegistryLine>(r.Lines.Count);
        var n = 0;
        foreach (var l in r.Lines)
        {
            ReceiptMarkupLine? saved = l.SalePrice is > 0
                ? new ReceiptMarkupLine { ProductId = l.ProductId, Purchase = l.PurchasePrice, Qty = l.Qty, MarkupPercent = l.MarkupPercent, Sale = l.SalePrice.Value, PreviousSale = l.PreviousSalePrice ?? 0 }
                : journal?.Lines.FirstOrDefault(x => string.Equals(x.ProductId, l.ProductId, StringComparison.OrdinalIgnoreCase));
            var cat = l.ProductId.Length > 0 ? ctx.Catalog?.Invoke(l.ProductId) : null;
            var view = ReceiptMarkupLog.View(l.PurchasePrice, saved, saved is null ? cat?.Sale : null);
            var barcode = cat?.Barcode is { Length: > 0 } b ? b : l.Code;
            lines.Add(new RegistryLine
            {
                Doc = d, No = ++n, ProductId = l.ProductId, Code = cat?.Code ?? "", Barcode = barcode, Name = l.ProductName, Unit = l.Unit,
                Qty = l.Qty, Purchase = l.PurchasePrice, Sum = ReceivingPricing.LineTotal(l.Qty, l.PurchasePrice), View = view,
            });
        }
        d.Lines = lines;
        d.Qty = lines.Sum(x => x.Qty);
        var totals = ReceiptMarkupLog.Totals(lines.Select(x => (x.Qty, x.Purchase, x.View)));
        d.AvgMarkup = totals.AvgPercent;
        d.MarkupEstimated = totals.AnyEstimated;
    }

    /// <summary>Номера документов обновились (индекс сверился) — переписать их и сбросить ключи поиска.</summary>
    public static void RefreshNumbers(IEnumerable<RegistryDoc> docs)
    {
        foreach (var d in docs)
        {
            d.Number = DocumentNumbering.ReceiptText(d.Id);
            d.Key = null;
            foreach (var l in d.Lines)
                l.Key = null;
        }
    }

    /// <summary>Пометка «закупка выше прошлой»: по каждому товару документы идут от старых к новым; прежняя закупка — предыдущая строка того же товара.</summary>
    public static void MarkRaised(IReadOnlyList<RegistryDoc> docs)
    {
        var last = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs.OrderBy(x => x.At ?? DateTime.MinValue))
            foreach (var l in d.Lines)
            {
                var key = l.ProductId.Length > 0 ? l.ProductId : l.Barcode;
                if (key.Length == 0)
                    continue;
                if (last.TryGetValue(key, out var prev) && prev > 0 && l.Purchase > 0)
                {
                    l.PrevPurchase = prev;
                    l.Raised = ReceivingPricing.IsPurchaseRaised(prev, l.Purchase);
                }
                else
                {
                    l.PrevPurchase = 0;
                    l.Raised = false;
                }
                if (l.Purchase > 0)
                    last[key] = l.Purchase;
            }
    }

    // ------------------------------------------------------------------ отбор

    private static bool DocPasses(RegistryDoc d, RegistryFilter f)
    {
        if (f.From is { } from && (d.At ?? DateTime.MinValue).Date < from.Date)
            return false;
        if (f.To is { } to && (d.At ?? DateTime.MinValue).Date > to.Date)
            return false;
        if (!string.IsNullOrEmpty(f.SupplierId) && d.SupplierId.Length > 0 && d.SupplierId != f.SupplierId)
            return false;
        if (!string.IsNullOrEmpty(f.Warehouse) && !string.Equals(d.Warehouse, f.Warehouse, StringComparison.CurrentCultureIgnoreCase))
            return false;
        return f.Status is null || d.Status == f.Status;
    }

    /// <summary>Поля документа для поиска (без строк): поставщик, принял, накладная поставщика, номер документа в трёх видах.</summary>
    private static string HeaderKey(RegistryDoc d) => SearchMatch.Key(d.SupplierName, d.CreatedBy, d.SupplierDoc, d.Warehouse,
        DocumentNumbering.SearchKey(DocSeries.Receipt, d.Id), DocumentNumbering.ShortCode(d.Id), d.Number);

    private static string DocKey(RegistryDoc d) => d.Key ??= HeaderKey(d) + SearchMatch.FieldSeparator + SearchMatch.Key(string.Join(" ", d.Lines.Select(l => l.Name + " " + l.Barcode + " " + l.Code)));

    private static string LineKey(RegistryLine l) => l.Key ??= HeaderKey(l.Doc) + SearchMatch.FieldSeparator + SearchMatch.Key(l.Name, l.Barcode, l.Code);

    /// <summary>Документы по отбору (порядок — как в <paramref name="docs"/>). Поиск: номер документа, накладная поставщика, поставщик, принял, любой товар документа.</summary>
    public static List<RegistryDoc> FilterDocs(IReadOnlyList<RegistryDoc> docs, RegistryFilter f)
    {
        var byFilters = docs.Where(d => DocPasses(d, f)).ToList();
        var q = (f.Query ?? "").Trim();
        if (q.Length == 0)
            return byFilters;
        // Набран номер документа целиком («ПР-2026-000007») — только он: частичное совпадение числа (как в истории) нашло бы ещё и ПР-…-000497.
        var exactDocs = byFilters.Where(d => string.Equals(d.Number, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactDocs.Count > 0)
            return exactDocs;
        var byText = SearchMatch.Filter(byFilters, q, DocKey).ToHashSet();
        return byFilters.Where(d => byText.Contains(d) || DocumentNumbering.MatchesQuery(DocSeries.Receipt, d.Id, q)).ToList();
    }

    /// <summary>Позиции документов по отбору; поиск — по товару (название, штрихкод, код) и полям документа (номер, поставщик…).</summary>
    public static List<RegistryLine> FilterLines(IReadOnlyList<RegistryDoc> docs, RegistryFilter f)
    {
        var lines = docs.Where(d => DocPasses(d, f)).SelectMany(d => d.Lines).ToList();
        var q = (f.Query ?? "").Trim();
        if (q.Length == 0)
            return lines;
        var exactLines = lines.Where(l => string.Equals(l.Doc.Number, q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactLines.Count > 0)
            return exactLines;
        var byText = SearchMatch.Filter(lines, q, LineKey).ToHashSet();
        var numberDocs = new Dictionary<RegistryDoc, bool>();
        bool NumberHit(RegistryDoc d)
        {
            if (!numberDocs.TryGetValue(d, out var hit))
                numberDocs[d] = hit = DocumentNumbering.MatchesQuery(DocSeries.Receipt, d.Id, q);
            return hit;
        }
        return lines.Where(l => byText.Contains(l) || NumberHit(l.Doc)).ToList();
    }

    // ------------------------------------------------------------------ итоги

    /// <summary>Итог по документам: сумма и возвраты — по документам, штуки и наценка — по их строкам.</summary>
    public static RegistryTotals TotalsOfDocs(IReadOnlyList<RegistryDoc> docs)
    {
        var lines = docs.SelectMany(d => d.Lines).ToList();
        var m = ReceiptMarkupLog.Totals(lines.Select(x => (x.Qty, x.Purchase, x.View)));
        return new RegistryTotals(docs.Count, docs.Sum(d => d.Items), Math.Round(docs.Sum(d => d.Qty), 3), Math.Round(docs.Sum(d => d.Total), 2), m.AvgPercent, m.Profit, m.AnyEstimated,
            docs.Count(d => d.LinesMissing));
    }

    /// <summary>Итог по позициям: сумма закупки — по строкам.</summary>
    public static RegistryTotals TotalsOfLines(IReadOnlyList<RegistryLine> lines)
    {
        var m = ReceiptMarkupLog.Totals(lines.Select(x => (x.Qty, x.Purchase, x.View)));
        var docs = lines.Select(l => l.Doc).Distinct().ToList();
        return new RegistryTotals(docs.Count, lines.Count, Math.Round(lines.Sum(x => x.Qty), 3), Math.Round(lines.Sum(x => x.Sum), 2), m.AvgPercent, m.Profit, m.AnyEstimated, 0);
    }

    // ------------------------------------------------------------------ тексты (для окна и выгрузок)

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string StatusText(RegistryDoc d) => d.Status switch
    {
        RegistryStatus.Edited => T("Исправлен", "Оңдолду", "Edited", "Düzeltildi", "Tuzatildi") + " v" + d.Version.ToString(CultureInfo.InvariantCulture),
        RegistryStatus.Returned => d.Returned >= d.Total - 0.005 && d.Total > 0
            ? T("Возвращён", "Кайтарылды", "Returned", "İade edildi", "Qaytarildi") : T("С возвратом", "Кайтаруу менен", "With return", "İadeli", "Qaytarish bilan"),
        _ => T("Проведён", "Өткөрүлдү", "Posted", "İşlendi", "O'tkazildi"),
    };

    public static string QtyText(double v) => v.ToString("#,0.###", Ru);

    public static string MarkupText(double? percent, bool estimated) =>
        percent is { } p ? (estimated ? "≈ " : "") + p.ToString("0.0", Ru) : "—";
}

// ====================================================================== загрузка и копия

/// <summary>Результат загрузки реестра.</summary>
public sealed class RegistryLoad
{
    public List<SupplierReceipt> Receipts { get; set; } = new();
    /// <summary>Приходов по фильтру на сервере (count) — для «Загружаем… N из M».</summary>
    public int Total { get; set; }
    public bool Complete { get; set; }
    /// <summary>Просмотрено не всё: упёрлись в предел страниц.</summary>
    public bool Truncated { get; set; }
    /// <summary>Сервер не ответил: показаны копия прошлой загрузки (или то, что успело прийти).</summary>
    public bool Offline { get; set; }
    public DateTime LoadedAt { get; set; }
}

public static class PurchasesRegistryLoader
{
    public const int PageSize = 100;
    /// <summary>Не больше 100 страниц по 100 приходов (10 000 документов) за один просмотр — защита от бесконечной загрузки.</summary>
    public const int MaxPages = 100;
    private const int KeepInCache = 5000;

    /// <summary>Только для стенда проверки: другая папка копии (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string CacheDir => DataDirOverride ?? PurchasesStore.StorageDir;

    private static string CachePath()
    {
        var company = PurchasesStore.CurrentCompany;
        var safe = new string(("registry-" + company).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(CacheDir, safe + ".json");
    }

    private sealed class CacheFile
    {
        public DateTime LoadedAt { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? SupplierId { get; set; }
        public int Total { get; set; }
        public List<SupplierReceipt> Receipts { get; set; } = new();
    }

    /// <summary>Загрузка страницами по <see cref="PageSize"/>; <paramref name="onPage"/> — после каждой страницы (новые приходы, всего загружено, всего на сервере),
    /// вызывается из фонового потока. Нет связи — копия прошлой загрузки, если она покрывает период, иначе то, что успело прийти.</summary>
    public static async Task<RegistryLoad> LoadAsync(SupplierApiService api, DateTime? from, DateTime? to, string? supplierId, bool keepMoney,
        Action<IReadOnlyList<SupplierReceipt>, int, int>? onPage, CancellationToken ct)
    {
        var result = new RegistryLoad { LoadedAt = DateTime.Now };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var (list, count, _, hasNext) = await api.GetReceiptsPageAsync(page, PageSize, supplierId, from, to, ct).ConfigureAwait(false);
                var fresh = list.Where(r => seen.Add(r.Id)).ToList();
                result.Receipts.AddRange(fresh);
                result.Total = Math.Max(Math.Max(count, result.Total), result.Receipts.Count);
                onPage?.Invoke(fresh, result.Receipts.Count, result.Total);
                if (!hasNext || list.Count == 0)
                {
                    result.Complete = true;
                    break;
                }
                if (page == MaxPages)
                    result.Truncated = true;
            }
            result.Complete |= result.Truncated;
            if (result.Complete && !result.Truncated && keepMoney)
                Save(result, from, to, supplierId);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: приходы не загружены полностью ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            var cached = LoadCache();
            if (cached is not null && Covers(cached, from, to, supplierId))
            {
                var offline = new RegistryLoad { Offline = true, Complete = true, LoadedAt = cached.LoadedAt, Receipts = cached.Receipts, Total = cached.Receipts.Count };
                onPage?.Invoke(offline.Receipts, offline.Receipts.Count, offline.Total);
                return offline;
            }
            result.Offline = true;
            result.LoadedAt = DateTime.Now;
            return result;
        }
    }

    private static bool Covers(CacheFile c, DateTime? from, DateTime? to, string? supplierId) =>
        (c.From is null || (from is { } f && c.From.Value.Date <= f.Date))
        && (c.To is null || (to is { } t && c.To.Value.Date >= t.Date))
        && (string.IsNullOrEmpty(c.SupplierId) || c.SupplierId == supplierId);

    /// <summary>Приходы, у которых список не отдал строки (Lines пуст при ItemsCount &gt; 0), — догрузить каждый по /{id}/ (4 запроса одновременно).
    /// Возвращает id → приход со строками; <paramref name="progress"/> — сколько готово из скольких.</summary>
    public static async Task<Dictionary<string, SupplierReceipt>> EnsureLinesAsync(SupplierApiService api, IReadOnlyList<SupplierReceipt> receipts,
        Action<int, int>? progress, CancellationToken ct)
    {
        var need = receipts.Where(r => r.Lines.Count == 0 && r.ItemsCount > 0).ToList();
        var done = new Dictionary<string, SupplierReceipt>(StringComparer.OrdinalIgnoreCase);
        var finished = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = need.Select(async r =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var detail = await api.GetReceiptAsync(r.Id, ct).ConfigureAwait(false);
                if (detail.Lines.Count > 0)
                    lock (done)
                        done[r.Id] = detail;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Реестр закупок: строки прихода {r.Id} не загружены ({ex.Message}).", "WARNING");
            }
            finally
            {
                gate.Release();
                progress?.Invoke(Interlocked.Increment(ref finished), need.Count);
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return done;
    }

    /// <summary>Копия последней полной загрузки — атомарно (AtomicFile). Не пишем на стенде (ReadOnly) и никогда не роняем окно.</summary>
    public static void Save(RegistryLoad load, DateTime? from, DateTime? to, string? supplierId) => Save(load.Receipts, load.Total, from, to, supplierId);

    public static void Save(IReadOnlyList<SupplierReceipt> receipts, int total, DateTime? from, DateTime? to, string? supplierId)
    {
        if (PurchasesStore.ReadOnly && DataDirOverride is null)
            return;
        try
        {
            var keep = receipts.OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue).Take(KeepInCache).ToList();
            var file = new CacheFile { LoadedAt = DateTime.Now, From = from, To = to, SupplierId = supplierId, Total = total, Receipts = keep };
            AtomicFile.WriteAllText(CachePath(), JsonSerializer.Serialize(file));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: копия не сохранена ({ex.Message}).", "WARNING");
        }
    }

    private static CacheFile? LoadCache()
    {
        try
        {
            return AtomicFile.ReadAllText(CachePath()) is { } text ? JsonSerializer.Deserialize<CacheFile>(text) : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: копия не прочитана ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Когда сохранена копия (для подписи «данные от ЧЧ:ММ»), null — копии нет.</summary>
    public static DateTime? CacheTime() => LoadCache()?.LoadedAt;
}
