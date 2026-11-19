using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Л-12 «Скорость и надёжность»): сверка «касса ↔ сервер». Ничего не правит и не отправляет — только читает и сообщает.
/// Что сверяется: (1) чеки, проведённые этой кассой (журнал оплат SaleReturnLedger, Own) за последние 30 часов, против списка продаж сервера за те же дни
/// — «есть на кассе, нет на сервере» и «сумма разная»; (2) чеки очереди без связи — ждут отправки дольше 15 минут и чеки с ошибкой отправки;
/// (3) остатки у недавно проданных товаров (до 30 штук за 24 часа): остаток в каталоге кассы против остатка на сервере (расхождение подтверждается
/// повторным чтением через 15 секунд — иначе обычная задержка ленты каталога давала бы ложные тревоги).
/// Кассиру показывается ТОЛЬКО расхождение (короткий тост и запись WARNING в журнал — её видно в «Логи и ошибки»); результат последней сверки хранится
/// в daily-reconcile.json. Раз в сутки — <see cref="Start"/>, по кнопке в «Логи и ошибки» — <see cref="RunAsync"/>.</summary>
public static class DailyReconciliation
{
    public enum Kind { MissingOnServer, AmountDiffers, Unsent, Failed, StockDiffers }

    /// <summary>Чек, проведённый этой кассой (из журнала оплат).</summary>
    public sealed record LocalSale(string Id, string? Number, decimal Total, DateTimeOffset At, bool HasRefunds);

    /// <summary>Строка списка продаж сервера.</summary>
    public sealed record ServerSale(string Id, decimal Total, string Status);

    /// <summary>Чек очереди без связи, ещё не принятый сервером (ожидает, отправляется или отклонён).</summary>
    public sealed record QueuedSale(string Id, decimal Total, string Status, DateTimeOffset CreatedAt, string? Error);

    /// <summary>Остаток товара в каталоге кассы и на сервере.</summary>
    public sealed record StockPair(string ProductId, string Name, double Local, double Server);

    public sealed record Finding(Kind Kind, string Text);

    public sealed class Report
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
        /// <summary>Не удалось получить данные сервера (нет связи): сверка не выполнена, расхождений не заявляется.</summary>
        public string? Error { get; init; }
        public int LocalSales { get; init; }
        public int ServerSales { get; init; }
        public int StockChecked { get; init; }
        public List<Finding> Findings { get; init; } = [];
        public bool HasProblems => Findings.Count > 0;

        /// <summary>Одна строка для тоста кассира.</summary>
        public string ShortText => Tr.T(
            $"Сверка с сервером: найдено расхождений — {Findings.Count}. Подробности — «Логи и ошибки».",
            $"Сервер менен салыштыруу: айырмачылыктар — {Findings.Count}. Кеңири — «Журналдар жана каталар».",
            $"Server check: {Findings.Count} mismatch(es) found. Details — “Logs and errors”.",
            $"Sunucu karşılaştırması: {Findings.Count} fark bulundu. Ayrıntılar — «Günlükler ve hatalar».",
            $"Server bilan solishtirish: {Findings.Count} ta farq topildi. Tafsilotlar — «Jurnal va xatolar».");

        /// <summary>Полный текст для окна и журнала: итог и каждое расхождение с новой строки.</summary>
        public string FullText
        {
            get
            {
                var sb = new StringBuilder();
                if (Error is not null)
                {
                    sb.Append(Tr.T("Сверка не выполнена: ", "Салыштыруу жасалган жок: ", "The check was not done: ", "Karşılaştırma yapılamadı: ", "Solishtirish bajarilmadi: ")).Append(Error);
                    return sb.ToString();
                }
                if (!HasProblems)
                {
                    sb.Append(Tr.T(
                        $"Сверка с сервером: расхождений нет (чеков кассы {LocalSales}, чеков на сервере {ServerSales}, остатков проверено {StockChecked}).",
                        $"Сервер менен салыштыруу: айырмачылык жок (кассанын чектери {LocalSales}, серверде {ServerSales}, калдыктар текшерилди {StockChecked}).",
                        $"Server check: no mismatches (till receipts {LocalSales}, server receipts {ServerSales}, stock items checked {StockChecked}).",
                        $"Sunucu karşılaştırması: fark yok (kasa fişleri {LocalSales}, sunucuda {ServerSales}, kontrol edilen stok {StockChecked}).",
                        $"Server bilan solishtirish: farq yo'q (kassa cheklari {LocalSales}, serverda {ServerSales}, tekshirilgan qoldiq {StockChecked})."));
                    return sb.ToString();
                }
                sb.Append(Tr.T($"Сверка с сервером: расхождений — {Findings.Count}.", $"Сервер менен салыштыруу: айырмачылыктар — {Findings.Count}.",
                    $"Server check: {Findings.Count} mismatch(es).", $"Sunucu karşılaştırması: {Findings.Count} fark.", $"Server bilan solishtirish: {Findings.Count} ta farq."));
                foreach (var f in Findings)
                    sb.Append("\n• ").Append(f.Text);
                sb.Append('\n').Append(Tr.T("Программа ничего не исправляла — проверьте чеки и остатки или напишите в поддержку.",
                    "Программа эч нерсе оңдогон жок — чектерди жана калдыктарды текшериңиз же колдоого жазыңыз.",
                    "The program fixed nothing — check the receipts and stock or write to support.",
                    "Program hiçbir şeyi düzeltmedi — fişleri ve stoğu kontrol edin veya desteğe yazın.",
                    "Dastur hech narsani to'g'rilamadi — cheklar va qoldiqni tekshiring yoki yordamga yozing."));
                return sb.ToString();
            }
        }
    }

    public static readonly TimeSpan SaleWindow = TimeSpan.FromHours(30);
    private static readonly TimeSpan GraceForNewSale = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnsentAfter = TimeSpan.FromMinutes(15);
    private const decimal MoneyTolerance = 0.01m;
    private const double StockTolerance = 0.0005;
    private const int MaxStockFindings = 10;

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    private static string Qty(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Чистое сравнение (без файлов и сети). <paramref name="serverListComplete"/> — список сервера прочитан до конца: иначе «нет на сервере» не заявляется
    /// (чек мог остаться на непрочитанной странице).</summary>
    public static Report Compare(
        IReadOnlyList<LocalSale> local,
        IReadOnlyList<ServerSale> server,
        bool serverListComplete,
        IReadOnlyList<QueuedSale> queue,
        IReadOnlyList<StockPair> stock,
        DateTimeOffset now,
        int stockChecked = -1)
    {
        var findings = new List<Finding>();
        var serverById = new Dictionary<string, ServerSale>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in server)
            serverById[s.Id] = s;
        // Чек очереди, ушедший на сервер, в журнале оплат может лежать под локальным id очереди — это не «потерянный чек».
        var queuedIds = new HashSet<string>(queue.Select(q => q.Id), StringComparer.OrdinalIgnoreCase);

        var missing = new List<LocalSale>();
        foreach (var sale in local)
        {
            if (queuedIds.Contains(sale.Id) || now - sale.At < GraceForNewSale)
                continue;
            if (!serverById.TryGetValue(sale.Id, out var onServer))
            {
                if (serverListComplete)
                    missing.Add(sale);
                continue;
            }
            // Сумма сравнивается у обычных оплаченных чеков без возвратов: после возврата сервер сам пересчитывает скидку и итог.
            var status = onServer.Status.ToLowerInvariant();
            if (!sale.HasRefunds && (status is "paid" or "debt") && Math.Abs(sale.Total - onServer.Total) > MoneyTolerance)
            {
                var number = sale.Number is { Length: > 0 } n ? "№" + n : sale.Id;
                findings.Add(new Finding(Kind.AmountDiffers, Tr.T(
                    $"Чек {number}: на кассе {Money(sale.Total)}, на сервере {Money(onServer.Total)}.",
                    $"Чек {number}: кассада {Money(sale.Total)}, серверде {Money(onServer.Total)}.",
                    $"Receipt {number}: till {Money(sale.Total)}, server {Money(onServer.Total)}.",
                    $"Fiş {number}: kasada {Money(sale.Total)}, sunucuda {Money(onServer.Total)}.",
                    $"Chek {number}: kassada {Money(sale.Total)}, serverda {Money(onServer.Total)}.")));
            }
        }
        if (missing.Count > 0)
        {
            var first = missing.OrderBy(m => m.At).First();
            var number = first.Number is { Length: > 0 } n ? "№" + n : first.Id;
            findings.Insert(0, new Finding(Kind.MissingOnServer, Tr.T(
                $"Чеков на кассе, которых нет на сервере: {missing.Count} на сумму {Money(missing.Sum(m => m.Total))} (самый ранний — {number}, {first.At.LocalDateTime:dd.MM HH:mm}).",
                $"Кассада бар, серверде жок чектер: {missing.Count}, суммасы {Money(missing.Sum(m => m.Total))} (эң алгачкы — {number}, {first.At.LocalDateTime:dd.MM HH:mm}).",
                $"Receipts on the till that are missing on the server: {missing.Count}, total {Money(missing.Sum(m => m.Total))} (earliest — {number}, {first.At.LocalDateTime:dd.MM HH:mm}).",
                $"Kasada olup sunucuda olmayan fişler: {missing.Count}, toplam {Money(missing.Sum(m => m.Total))} (en eski — {number}, {first.At.LocalDateTime:dd.MM HH:mm}).",
                $"Kassada bor, serverda yo'q cheklar: {missing.Count} ta, jami {Money(missing.Sum(m => m.Total))} (eng birinchisi — {number}, {first.At.LocalDateTime:dd.MM HH:mm}).")));
        }

        var unsent = queue.Where(q => (q.Status is OfflineSaleEntry.PendingSync or OfflineSaleEntry.Syncing) && now - q.CreatedAt >= UnsentAfter).ToList();
        if (unsent.Count > 0)
        {
            var oldest = unsent.Min(q => q.CreatedAt);
            findings.Add(new Finding(Kind.Unsent, Tr.T(
                $"Не отправлено на сервер: {unsent.Count} чеков на сумму {Money(unsent.Sum(q => q.Total))}, самый старый — с {oldest.LocalDateTime:dd.MM HH:mm}.",
                $"Серверге жөнөтүлгөн жок: {unsent.Count} чек, суммасы {Money(unsent.Sum(q => q.Total))}, эң эскиси — {oldest.LocalDateTime:dd.MM HH:mm}.",
                $"Not sent to the server: {unsent.Count} receipt(s), total {Money(unsent.Sum(q => q.Total))}, the oldest since {oldest.LocalDateTime:dd.MM HH:mm}.",
                $"Sunucuya gönderilmedi: {unsent.Count} fiş, toplam {Money(unsent.Sum(q => q.Total))}, en eskisi {oldest.LocalDateTime:dd.MM HH:mm} tarihinden.",
                $"Serverga yuborilmadi: {unsent.Count} ta chek, jami {Money(unsent.Sum(q => q.Total))}, eng eskisi {oldest.LocalDateTime:dd.MM HH:mm} dan.")));
        }
        var failed = queue.Where(q => q.Status == OfflineSaleEntry.Failed).ToList();
        if (failed.Count > 0)
        {
            var reason = failed.Select(q => q.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "";
            if (reason.Length > 120)
                reason = reason[..120] + "…";
            findings.Add(new Finding(Kind.Failed, Tr.T(
                $"Сервер не принял {failed.Count} чеков на сумму {Money(failed.Sum(q => q.Total))}" + (reason.Length > 0 ? $": {reason}" : "."),
                $"Сервер {failed.Count} чекти кабыл албады, суммасы {Money(failed.Sum(q => q.Total))}" + (reason.Length > 0 ? $": {reason}" : "."),
                $"The server rejected {failed.Count} receipt(s), total {Money(failed.Sum(q => q.Total))}" + (reason.Length > 0 ? $": {reason}" : "."),
                $"Sunucu {failed.Count} fişi kabul etmedi, toplam {Money(failed.Sum(q => q.Total))}" + (reason.Length > 0 ? $": {reason}" : "."),
                $"Server {failed.Count} ta chekni qabul qilmadi, jami {Money(failed.Sum(q => q.Total))}" + (reason.Length > 0 ? $": {reason}" : "."))));
        }

        var diffs = stock.Where(s => Math.Abs(s.Local - s.Server) > StockTolerance).ToList();
        foreach (var d in diffs.Take(MaxStockFindings))
            findings.Add(new Finding(Kind.StockDiffers, Tr.T(
                $"Остаток «{d.Name}»: на кассе {Qty(d.Local)}, на сервере {Qty(d.Server)}.",
                $"«{d.Name}» калдыгы: кассада {Qty(d.Local)}, серверде {Qty(d.Server)}.",
                $"Stock of “{d.Name}”: till {Qty(d.Local)}, server {Qty(d.Server)}.",
                $"«{d.Name}» stoğu: kasada {Qty(d.Local)}, sunucuda {Qty(d.Server)}.",
                $"«{d.Name}» qoldig'i: kassada {Qty(d.Local)}, serverda {Qty(d.Server)}.")));
        if (diffs.Count > MaxStockFindings)
            findings.Add(new Finding(Kind.StockDiffers, Tr.T(
                $"…и ещё {diffs.Count - MaxStockFindings} товаров с другим остатком.",
                $"…жана дагы {diffs.Count - MaxStockFindings} товардын калдыгы башкача.",
                $"…and {diffs.Count - MaxStockFindings} more products with a different stock.",
                $"…ve stoğu farklı {diffs.Count - MaxStockFindings} ürün daha.",
                $"…va yana {diffs.Count - MaxStockFindings} ta mahsulotning qoldig'i boshqacha.")));

        return new Report
        {
            At = now,
            LocalSales = local.Count,
            ServerSales = server.Count,
            StockChecked = stockChecked >= 0 ? stockChecked : stock.Count,
            Findings = findings,
        };
    }

    // ───────────── сбор данных кассы и сервера ─────────────

    private static decimal? ReadMoney(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        return null;
    }

    private static string? ReadString(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>Разбор страницы продаж сервера: id, итог, статус; открытые корзины («new») пропускаются.</summary>
    internal static ServerSale? ParseServerSale(JsonElement row)
    {
        var id = ReadString(row, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var status = (ReadString(row, "status") ?? "").ToLowerInvariant();
        if (status == "new")
            return null;
        var total = ReadMoney(row, "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total") ?? 0m;
        return new ServerSale(id, total, status);
    }

    private static decimal QueueTotal(OfflineSaleEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
            return Math.Round((decimal)CartTotalsCalculator.Calculate(doc.RootElement).TotalDue, 2);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            return 0m;
        }
    }

    /// <summary>Источники данных кассы и сервера — подменяются в тестах (по умолчанию настоящие).</summary>
    public sealed class Sources
    {
        public Func<IReadOnlyList<LocalSale>> Local { get; init; } = DefaultLocal;
        public Func<IReadOnlyList<QueuedSale>> Queue { get; init; } = DefaultQueue;
        /// <summary>(товар, имя, остаток в каталоге кассы) — недавно проданные, по убыванию давности.</summary>
        public Func<IReadOnlyList<(string Id, string Name, double Local)>> RecentProducts { get; init; } = DefaultRecentProducts;
        public Func<string, double?>? LocalStock { get; init; }
        public Func<DateTime, DateTime, CancellationToken, Task<(List<ServerSale> Sales, bool Complete)>>? ServerSales { get; init; }
        public Func<string, CancellationToken, Task<double?>>? ServerStock { get; init; }
        public TimeSpan RecheckDelay { get; init; } = TimeSpan.FromSeconds(15);
    }

    private static IReadOnlyList<LocalSale> DefaultLocal() =>
        SaleReturnLedger.Recent(SaleWindow, 4000)
            .Where(e => e.Own && !string.IsNullOrWhiteSpace(e.SaleId))
            .Select(e => new LocalSale(e.SaleId, e.Number, e.Total, e.SoldAt ?? e.RecordedAt, e.Refunds.Count > 0))
            .ToList();

    private static IReadOnlyList<QueuedSale> DefaultQueue() =>
        OfflinePendingSalesStore.LoadAllUnsynced()
            .Where(e => !e.IsAutonomous && e.ClosedWithoutSendAt is null)
            .Select(e => new QueuedSale(e.Id, QueueTotal(e), e.Status, e.CreatedAt, e.LastError))
            .ToList();

    private static IReadOnlyList<(string Id, string Name, double Local)> DefaultRecentProducts()
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var recentCut = DateTime.UtcNow.AddMinutes(-3); // проданное только что ещё может не дойти до ленты каталога
        return DatabaseService.Instance.LoadSoldLineItemsSince(since)
            .GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Name: g.First().ProductName, Last: g.Max(x => x.SoldAt)))
            .Where(x => x.Last <= recentCut)
            .OrderByDescending(x => x.Last)
            .Take(30)
            .Select(x => (x.Id, x.Name, Local: CatalogCacheService.FindProductById(x.Id)?.Quantity ?? double.NaN))
            .Where(x => !double.IsNaN(x.Local))
            .ToList();
    }

    private static async Task<(List<ServerSale>, bool)> ReadServerSalesAsync(ISalesApiService sales, DateTime from, DateTime toExclusive, CancellationToken ct)
    {
        const int pageSize = 80;
        const int maxPages = 25;
        var list = new List<ServerSale>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= maxPages; page++)
        {
            var rows = await sales.PosSalesListAsync(page, pageSize, null, ct, dateFrom: from, dateToExclusive: toExclusive).ConfigureAwait(false);
            if (rows.Count == 0)
                return (list, true);
            var added = 0;
            foreach (var row in rows)
            {
                if (ParseServerSale(row) is { } sale && seen.Add(sale.Id))
                {
                    list.Add(sale);
                    added++;
                }
            }
            if (rows.Count < pageSize || added == 0)
                return (list, true);
        }
        return (list, false);
    }

    private static async Task<double?> ReadServerStockAsync(ICatalogApiService catalog, string productId, bool mustWeigh, CancellationToken ct)
    {
        var detail = await catalog.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
        return detail is { ValueKind: JsonValueKind.Object } el ? StockSyncService.ResolveStockQuantity(el, mustWeigh) : null;
    }

    /// <summary>Сверка по настоящим данным кассы и сервера (или по <paramref name="sources"/> в тестах). Нет связи — отчёт с Error, расхождений нет.</summary>
    public static async Task<Report> RunAsync(ISalesApiService? sales, ICatalogApiService? catalog, CancellationToken ct, Sources? sources = null, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        sources ??= new Sources();
        try
        {
            var local = sources.Local();
            var queue = sources.Queue();
            var from = (now - SaleWindow).LocalDateTime.Date;
            var to = now.LocalDateTime.Date.AddDays(1);
            var (serverSales, complete) = sources.ServerSales is { } custom
                ? await custom(from, to, ct).ConfigureAwait(false)
                : sales is null ? throw new InvalidOperationException("sales api") : await ReadServerSalesAsync(sales, from, to, ct).ConfigureAwait(false);

            var pairs = new List<StockPair>();
            var checkedCount = 0;
            // Остатки сверяем только когда всё отправлено: чек в очереди ещё не списал остаток на сервере — расхождение было бы ожидаемым.
            var hasUnsent = queue.Any(q => q.Status is OfflineSaleEntry.PendingSync or OfflineSaleEntry.Syncing);
            if (!hasUnsent)
            {
                var recent = sources.RecentProducts();
                var firstPass = new List<(string Id, string Name, double Local, double Server)>();
                foreach (var p in recent)
                {
                    ct.ThrowIfCancellationRequested();
                    var server = sources.ServerStock is { } custStock
                        ? await custStock(p.Id, ct).ConfigureAwait(false)
                        : catalog is null ? null : await ReadServerStockAsync(catalog, p.Id, CatalogCacheService.FindProductById(p.Id)?.MustWeigh ?? false, ct).ConfigureAwait(false);
                    if (server is not { } s)
                        continue;
                    checkedCount++;
                    if (Math.Abs(p.Local - s) > StockTolerance)
                        firstPass.Add((p.Id, p.Name, p.Local, s));
                }
                if (firstPass.Count > 0)
                {
                    // Повторное чтение: лента каталога могла просто не успеть. Остаётся то, что расходится и во второй раз.
                    await Task.Delay(sources.RecheckDelay, ct).ConfigureAwait(false);
                    foreach (var p in firstPass)
                    {
                        ct.ThrowIfCancellationRequested();
                        var localQty = sources.LocalStock is { } ls ? ls(p.Id) : CatalogCacheService.FindProductById(p.Id)?.Quantity;
                        var server = sources.ServerStock is { } custStock
                            ? await custStock(p.Id, ct).ConfigureAwait(false)
                            : catalog is null ? null : await ReadServerStockAsync(catalog, p.Id, CatalogCacheService.FindProductById(p.Id)?.MustWeigh ?? false, ct).ConfigureAwait(false);
                        if (localQty is { } l && server is { } s && Math.Abs(l - s) > StockTolerance)
                            pairs.Add(new StockPair(p.Id, p.Name, l, s));
                    }
                }
            }
            return Compare(local, serverSales, complete, queue, pairs, now, checkedCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка с сервером не выполнена: {ex.GetType().Name}: {ex.Message}", "INFO");
            return new Report
            {
                At = now,
                Error = Tr.T("нет связи с сервером или он не ответил. Попробуйте позже.", "сервер менен байланыш жок же ал жооп берген жок. Кийинчерээк аракет кылыңыз.",
                    "no connection to the server or it did not answer. Try again later.", "sunucuyla bağlantı yok veya yanıt vermedi. Daha sonra deneyin.", "server bilan aloqa yo'q yoki javob bermadi. Keyinroq urinib ko'ring."),
            };
        }
    }

    // ───────────── последний результат на диске ─────────────

    private sealed class Stored
    {
        public DateTimeOffset At { get; set; }
        public bool Ok { get; set; }
        public int Problems { get; set; }
        public string Text { get; set; } = "";
    }

    private static string StatePath = Path.Combine(AppPaths.KassaLocal, "daily-reconcile.json");
    private static readonly object FileGate = new();

    /// <summary>Только для тестов: папка состояния.</summary>
    internal static void UseStatePath(string path) => StatePath = path;

    /// <summary>Когда была последняя успешная сверка (с ответом сервера) и что она нашла; null — не было.</summary>
    public static (DateTimeOffset At, int Problems, string Text)? LastResult()
    {
        try
        {
            lock (FileGate)
            {
                if (!File.Exists(StatePath))
                    return null;
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(StatePath));
                return s is null || !s.Ok ? null : (s.At, s.Problems, s.Text);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void Save(Report report)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(new Stored { At = report.At, Ok = report.Error is null, Problems = report.Findings.Count, Text = report.FullText }));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка: результат не записан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сверка с записью результата и строки WARNING в журнал (если есть расхождения). Для кнопки и для суточного запуска.</summary>
    public static async Task<Report> RunAndRecordAsync(CancellationToken ct = default)
    {
        var report = await RunAsync(PosApp.SalesApi, PosApp.CatalogApi, ct).ConfigureAwait(false);
        if (report.Error is null)
            Save(report);
        if (report.HasProblems)
            PosLogger.Log(report.FullText.Replace('\n', ' '), "WARNING");
        else if (report.Error is null)
            PosLogger.Log(report.FullText, "INFO");
        return report;
    }

    // ───────────── раз в сутки в фоне ─────────────

    private static Timer? _timer;
    private static int _running;

    /// <summary>Суточная сверка: первая проверка через 4 минуты после запуска, дальше раз в 30 минут; запускается, если с последней успешной сверки прошло 23 часа,
    /// кассир вошёл и связь есть. <paramref name="onProblems"/> вызывается ТОЛЬКО при расхождениях (из фонового потока).</summary>
    public static void Start(Action<Report> onProblems)
    {
        // 2026-11-13: суточный фон ВЫКЛЮЧЕН по умолчанию (не проверен вживую на настоящих кассах): включается файлом-признаком daily-reconcile.on в папке данных кассы
        // или переменной NURMARKET_DAILY_RECONCILE=1. Кнопка «Сверить с сервером» в «Логи и ошибки» работает всегда.
        if (!BackgroundEnabled())
            return;
        if (_timer is not null)
            return;
        _timer = new Timer(_ => _ = TickAsync(onProblems), null, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(30));
    }

    internal static bool BackgroundEnabled()
    {
        try
        {
            return Environment.GetEnvironmentVariable("NURMARKET_DAILY_RECONCILE") == "1" || File.Exists(Path.Combine(AppPaths.KassaLocal, "daily-reconcile.on"));
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsDue(DateTimeOffset now, (DateTimeOffset At, int Problems, string Text)? last) =>
        last is null || now - last.Value.At >= TimeSpan.FromHours(23);

    private static async Task TickAsync(Action<Report> onProblems)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            if (PosApp.SalesApi is null || string.IsNullOrWhiteSpace(PosApp.CurrentUserId) || PosApp.IsOfflineBootstrap || ServerOutageMonitor.IsOutage)
                return;
            if (!IsDue(DateTimeOffset.Now, LastResult()))
                return;
            var report = await RunAndRecordAsync(CancellationToken.None).ConfigureAwait(false);
            if (report.HasProblems)
                onProblems(report);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Суточная сверка: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
