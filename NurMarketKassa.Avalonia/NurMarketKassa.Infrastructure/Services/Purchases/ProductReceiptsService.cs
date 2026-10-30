using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-30, просьба владельца: «на складе при нажатии на товар — какой партии, какого числа и от кого пришла, и ссылку на накладную».
// Поступления ОДНОГО товара: приходы от поставщиков с сервера (suppliers/receipts/, строки товара отбираются у себя — сервер фильтра по товару
// пока не знает) плюс приёмки этой кассы (ProductPurchaseHistory: там ещё цена продажи и приёмки без поставщика). Новых таблиц нет:
// последний ответ лежит копией в %APPDATA%\NurMarketKassa\purchases\product-receipts\<компания>_<товар>.json (для работы без связи).
// «Остаток партии» — оценка по FIFO: раньше пришедшее продаётся первым, поэтому текущий остаток «лежит» в самых свежих приходах.

/// <summary>Одно поступление товара (строка прихода). Remaining — оценка остатка этой партии (заполняет <see cref="ProductReceiptsService.ApplyFifo"/>).</summary>
public sealed class ProductReceiptRow
{
    public string? ReceiptId { get; set; }
    public DateTime At { get; set; }
    public string SupplierId { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string Employee { get; set; } = "";
    public double Quantity { get; set; }
    public double ReturnedQty { get; set; }
    public string Unit { get; set; } = "";
    public double PurchasePrice { get; set; }
    public double? SalePrice { get; set; }
    /// <summary>Сумма прихода целиком (для сверки с накладной).</summary>
    public double ReceiptTotal { get; set; }
    /// <summary>true — строка с сервера (сайт или касса), false — только в журнале этой кассы (приёмка без поставщика или сервер не ответил).</summary>
    public bool FromServer { get; set; }
    public double Remaining { get; set; }

    /// <summary>Принято за вычетом возвращённого поставщику.</summary>
    public double Net => Math.Max(0, Quantity - ReturnedQty);

    public double Sum => Math.Round(Quantity * PurchasePrice, 2, MidpointRounding.AwayFromZero);

    /// <summary>Короткий «номер» прихода — первые 8 знаков кода, как в окне «Позиции прихода» и на накладной PDF.</summary>
    public string Number => string.IsNullOrEmpty(ReceiptId) ? "" : (ReceiptId!.Length >= 8 ? ReceiptId[..8] : ReceiptId).ToUpperInvariant();
}

/// <summary>Результат загрузки: строки (новые сверху), сколько приходов просмотрено из скольких, офлайн ли, когда получено.</summary>
public sealed class ProductReceiptsResult
{
    public List<ProductReceiptRow> Rows { get; set; } = new();
    public int Scanned { get; set; }
    public int TotalReceipts { get; set; }
    public bool Done { get; set; }
    /// <summary>Сервер не ответил: показаны копия прошлой загрузки и журнал кассы.</summary>
    public bool Offline { get; set; }
    public DateTime LoadedAt { get; set; }
    /// <summary>Просмотрено не всё: упёрлись в предел страниц (<see cref="ProductReceiptsService.MaxPages"/>).</summary>
    public bool Truncated { get; set; }
}

public static class ProductReceiptsService
{
    private const int PageSize = 100;
    /// <summary>Не больше 100 страниц по 100 приходов (10 000) за один просмотр — защита от бесконечной загрузки.</summary>
    public const int MaxPages = 100;

    /// <summary>2026-10-30: просмотренные приходы компании (все, без отбора по товару/дате) — в памяти на <see cref="ScanTtl"/>.</summary>
    private sealed class ScanCache
    {
        public string Company { get; init; } = "";
        public DateTime At { get; init; }
        public List<SupplierReceipt> Receipts { get; init; } = new();
        public int Total { get; init; }
        public bool Truncated { get; init; }
        public bool Fresh => DateTime.UtcNow - At < ScanTtl;
    }

    private static volatile ScanCache? _scan;

    /// <summary>2026-10-30: сколько приходы живут в памяти, прежде чем их придётся просматривать заново.</summary>
    public static TimeSpan ScanTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Сбросить кэш просмотренных приходов (смена компании, «Обновить», стенд проверки).</summary>
    public static void ResetScanCache() => _scan = null;

    /// <summary>Только для стенда проверки: другая папка копий (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string CacheDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases", "product-receipts");

    private static string CachePath(string productId)
    {
        var company = PurchasesStore.CurrentCompany;
        var safe = new string($"{company}_{productId}".Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(CacheDir, safe + ".json");
    }

    /// <summary>Поступления товара. <paramref name="from"/> — только приходы с этой даты (null — все); <paramref name="onProgress"/> вызывается после каждой страницы
    /// с накопленным результатом (из фонового потока — вызывающий сам переносит в окно). Нет связи — копия прошлой загрузки + журнал кассы, Offline = true.</summary>
    public static async Task<ProductReceiptsResult> LoadAsync(SupplierApiService api, string productId, DateTime? from,
        Action<ProductReceiptsResult>? onProgress, CancellationToken ct, bool keepPurchasePrices = true, IReadOnlyCollection<string>? codes = null)
    {
        var local = LoadLocal(productId);
        var server = new List<ProductReceiptRow>();
        var result = new ProductReceiptsResult { LoadedAt = DateTime.Now };
        try
        {
            // 2026-10-30 (приёмка 1.17.65, «Важно»): сервер не фильтрует приходы по товару — каждое открытие вкладки гнало до 100 запросов. Теперь просмотренные приходы
            // компании лежат в памяти 5 минут и общие для всех товаров: повторное открытие (и другой товар) — без запросов. Отмена (закрытие карточки) обрывает просмотр
            // между страницами, неполный просмотр в кэш не попадает.
            var company = PurchasesStore.CurrentCompany;
            var scanned = new List<SupplierReceipt>();
            var cache = _scan;
            var fromCache = cache is not null && cache.Fresh && string.Equals(cache.Company, company, StringComparison.OrdinalIgnoreCase);
            var page = 1;
            var done = false;
            var truncatedCache = false;
            while (!done)
            {
                List<SupplierReceipt> receipts;
                int count;
                bool hasNext;
                if (fromCache)
                {
                    receipts = cache!.Receipts;
                    count = cache.Total;
                    hasNext = false;
                    truncatedCache = cache.Truncated;
                }
                else
                {
                    (receipts, count, _, hasNext) = await api.GetReceiptsPageAsync(page, PageSize, null, from, null, ct).ConfigureAwait(false);
                    if (from is null)
                        scanned.AddRange(receipts); // в кэш — только просмотр без отбора по дате
                }

                result.TotalReceipts = Math.Max(count, result.TotalReceipts);
                var inRange = from is null ? receipts : receipts.Where(r => (r.CreatedAt ?? default) >= from).ToList();
                result.Scanned += receipts.Count;
                foreach (var receipt in inRange)
                    foreach (var line in receipt.Lines.Where(l => IsLineOfProduct(l, productId, codes)))
                        server.Add(new ProductReceiptRow
                        {
                            ReceiptId = receipt.Id, At = receipt.CreatedAt ?? default, SupplierId = receipt.SupplierId, SupplierName = receipt.SupplierName,
                            Employee = receipt.CreatedBy, Quantity = line.Qty, ReturnedQty = line.ReturnedQty, Unit = line.Unit, PurchasePrice = line.PurchasePrice,
                            ReceiptTotal = receipt.Total, FromServer = true,
                        });
                result.Rows = Merge(server, local);
                done = !hasNext || receipts.Count == 0 || page >= MaxPages;
                result.Done = !hasNext || receipts.Count == 0;
                if (fromCache && truncatedCache)
                    result.Done = false;
                onProgress?.Invoke(result);
                if (done)
                    break;
                ct.ThrowIfCancellationRequested();
                page++;
            }

            result.Truncated = !result.Done;
            if (!fromCache && from is null && scanned.Count > 0)
                _scan = new ScanCache { Company = company, At = DateTime.UtcNow, Receipts = scanned, Total = result.TotalReceipts, Truncated = result.Truncated };
            result.Done = true;
            // Копию пишем только после полной загрузки без фильтра даты — иначе «Неделя» затёрла бы полную историю.
            if (from is null && !result.Truncated)
                SaveCache(productId, result, keepPurchasePrices);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара {productId}: приходы с сервера не загружены: {ex.Message}", "WARNING");
            var cached = LoadCache(productId);
            var offline = new ProductReceiptsResult { Offline = true, Done = true, LoadedAt = cached?.LoadedAt ?? DateTime.MinValue };
            var rows = cached?.Rows.Where(r => r.FromServer).ToList() ?? new List<ProductReceiptRow>();
            offline.Rows = Merge(rows, local);
            offline.Scanned = offline.TotalReceipts = rows.Select(r => r.ReceiptId).Distinct().Count();
            return offline;
        }
    }

    /// <summary>2026-10-30: строка прихода относится к товару, если совпал id; либо id в строке пуст/другой, но код строки совпал со штрихкодом товара
    /// (товар пересоздавали, приход с сайта по коду) — иначе вкладка у такого товара пустая при «Всё».</summary>
    internal static bool IsLineOfProduct(SupplierReceiptLine l, string productId, IReadOnlyCollection<string>? codes)
        => string.Equals(l.ProductId, productId, StringComparison.OrdinalIgnoreCase)
           || (codes is { Count: > 0 } && !string.IsNullOrWhiteSpace(l.Code) && codes.Any(c => !string.IsNullOrWhiteSpace(c) && string.Equals(c.Trim(), l.Code.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>Серверные строки + приёмки кассы. Приход, который провела касса, есть и там, и там: берём серверную строку (возвраты, поставщик) и
    /// дополняем ценой продажи и именем из журнала кассы. Приёмки без прихода на сервере (без поставщика) остаются отдельными строками.</summary>
    internal static List<ProductReceiptRow> Merge(IReadOnlyList<ProductReceiptRow> server, IReadOnlyList<ProductReceiptRow> local)
    {
        var rows = new List<ProductReceiptRow>(server.Count + local.Count);
        var byReceipt = local.Where(l => !string.IsNullOrEmpty(l.ReceiptId)).GroupBy(l => l.ReceiptId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in server)
        {
            if (!string.IsNullOrEmpty(s.ReceiptId) && byReceipt.TryGetValue(s.ReceiptId!, out var mine))
            {
                covered.Add(s.ReceiptId!);
                s.SalePrice ??= mine.FirstOrDefault(m => Math.Abs(m.Quantity - s.Quantity) < 0.0005)?.SalePrice ?? mine[0].SalePrice;
                if (string.IsNullOrEmpty(s.Employee))
                    s.Employee = mine[0].Employee;
            }
            rows.Add(s);
        }
        rows.AddRange(local.Where(l => string.IsNullOrEmpty(l.ReceiptId) || !covered.Contains(l.ReceiptId!)));
        return rows.OrderByDescending(r => r.At).ToList();
    }

    private static List<ProductReceiptRow> LoadLocal(string productId)
    {
        try
        {
            return PurchaseReceivingService.Instance.LoadLocalHistory(productId).Select(h => new ProductReceiptRow
            {
                ReceiptId = h.ReceiptId, At = h.At, SupplierName = h.SupplierName ?? "", Employee = h.Employee ?? "", Quantity = h.Quantity,
                Unit = h.Unit ?? "", PurchasePrice = h.PurchasePrice, SalePrice = h.SalePrice, FromServer = false,
            }).ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара {productId}: журнал кассы не прочитан: {ex.Message}", "WARNING");
            return new List<ProductReceiptRow>();
        }
    }

    /// <summary>Оценка «остаток партии» по FIFO: текущий остаток <paramref name="stock"/> раскладывается по приходам от самого свежего к старому
    /// (раньше пришедшее продано первым). Строки должны идти от новых к старым; возвращённое поставщику в партию не входит.</summary>
    public static void ApplyFifo(IReadOnlyList<ProductReceiptRow> newestFirst, double stock)
    {
        var left = Math.Max(0, stock);
        foreach (var row in newestFirst)
        {
            var take = Math.Min(row.Net, left);
            row.Remaining = Math.Round(take, 3);
            left = Math.Max(0, left - take);
        }
    }

    // ------------------------------------------------------------------ копия для работы без связи

    private sealed class CacheFile
    {
        public DateTime LoadedAt { get; set; }
        public List<ProductReceiptRow> Rows { get; set; } = new();
    }

    private static void SaveCache(string productId, ProductReceiptsResult result, bool keepPurchasePrices = true)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = CachePath(productId);
            var tmp = path + ".tmp";
            // 2026-10-30 (приёмка 1.17.65, «Права»): ПК без права видеть закупку закупочные цены и суммы приходов в копию не пишет (в окне их и так нет).
            var rows = result.Rows.Where(r => r.FromServer).Select(r => keepPurchasePrices ? r : new ProductReceiptRow
            {
                ReceiptId = r.ReceiptId, At = r.At, SupplierId = r.SupplierId, SupplierName = r.SupplierName, Employee = r.Employee, Quantity = r.Quantity,
                ReturnedQty = r.ReturnedQty, Unit = r.Unit, PurchasePrice = 0, SalePrice = r.SalePrice, ReceiptTotal = 0, FromServer = true,
            }).ToList();
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile { LoadedAt = result.LoadedAt, Rows = rows }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара {productId}: копия не сохранена: {ex.Message}", "WARNING");
        }
    }

    private static CacheFile? LoadCache(string productId)
    {
        try
        {
            var path = CachePath(productId);
            return File.Exists(path) ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ сводка и фильтр

    public sealed record Summary(double TotalQty, double AvgPurchase, double? LastPurchase, string? LastSupplier, DateTime? LastAt, int Count);

    /// <summary>Итоги по всем поступлениям: всего получено (за вычетом возвратов), средняя закупка (взвешенная по количеству), последняя закупка и поставщик.</summary>
    public static Summary Summarize(IReadOnlyList<ProductReceiptRow> newestFirst)
    {
        var qty = newestFirst.Sum(r => r.Net);
        var cost = newestFirst.Sum(r => r.Net * r.PurchasePrice);
        var last = newestFirst.FirstOrDefault();
        var lastWithSupplier = newestFirst.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SupplierName));
        return new Summary(qty, qty > 0 ? cost / qty : 0, last?.PurchasePrice, lastWithSupplier?.SupplierName, last?.At == default ? null : last?.At, newestFirst.Count);
    }

    /// <summary>Отбор: период (null — всё) и подстрока в поставщике, номере прихода или сотруднике (без учёта регистра).</summary>
    public static List<ProductReceiptRow> Filter(IReadOnlyList<ProductReceiptRow> rows, DateTime? from, string? query)
    {
        var q = (query ?? "").Trim();
        return rows.Where(r => (from is null || r.At >= from)
                               && (q.Length == 0
                                   || r.SupplierName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                   || r.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || r.Employee.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
    }

    public static string FormatQty(double v) => v.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
}
