using System.Globalization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-30, доработка мастера «Массовая загрузка товаров»: загрузка карточек товаров БЕЗ прихода (файл без количества — «только карточки и цены»)
// и запись доп. штрихкодов. Остаток не трогается: новый товар создаётся с остатком 0, у товара склада PATCH-ом уходят только изменившиеся поля
// (закупка, цена продажи + наценка, доп. штрихкоды). Пакетами по 100 строк, внутри пакета до 4 запросов сразу; 429 и обрывы связи — повтор с паузой;
// создание после обрыва повторяется только если товара с этим штрихкодом на сервере ещё нет. Загрузку можно отменить — уже записанное остаётся,
// а повторная загрузка того же файла пропустит его (журнал BulkImportJournal + каталог «без изменений»). 2026-10-30 (приёмка 1.17.65): журнал, повтор 429/5xx/обрывов
// до 5 раз с растущей паузой, 401 останавливает загрузку, доп. штрихкоды объединяются со списком с сервера перед записью.
// Строки, которые сервер не принял, помечаются <see cref="BulkImportRow.LoadError"/> и попадают в «Скачать строки с ошибками».

public sealed class BulkCardsResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    /// <summary>Товары склада, у которых в файле нет ничего нового.</summary>
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    /// <summary>2026-10-30: сервер ответил 401 даже после обновления токена — загрузка остановлена, а не «упала» пачкой ошибок; войти и «Продолжить».</summary>
    public bool AuthExpired { get; set; }
    /// <summary>2026-10-30: сколько строк уже записано (в этом запуске и в прошлых по журналу) — для «Остановлено, загружено N из M».</summary>
    public int LoadedTotal { get; set; }
    public int PlannedTotal { get; set; }

    public string SummaryText => Tr.T($"Создано {Created}, обновлено {Updated}, пропущено {Skipped}, ошибок {Failed}" + (AuthExpired ? " (сессия истекла — войдите и продолжите)" : Cancelled ? " (загрузка остановлена)" : ""),
        $"Түзүлдү {Created}, жаңыртылды {Updated}, өткөрүлдү {Skipped}, ката {Failed}" + (AuthExpired ? " (сессия бүттү — кирип улантыңыз)" : Cancelled ? " (жүктөө токтотулду)" : ""),
        $"Created {Created}, updated {Updated}, skipped {Skipped}, errors {Failed}" + (AuthExpired ? " (session expired — sign in and continue)" : Cancelled ? " (loading stopped)" : ""),
        $"Oluşturulan {Created}, güncellenen {Updated}, atlanan {Skipped}, hata {Failed}" + (AuthExpired ? " (oturum süresi doldu — giriş yapıp devam edin)" : Cancelled ? " (yükleme durduruldu)" : ""),
        $"Yaratildi {Created}, yangilandi {Updated}, o'tkazib yuborildi {Skipped}, xato {Failed}" + (AuthExpired ? " (sessiya tugadi — kirib davom eting)" : Cancelled ? " (yuklash to'xtatildi)" : ""));

    /// <summary>«Остановлено, загружено N из M».</summary>
    public string StoppedText => Tr.T($"Остановлено, загружено {LoadedTotal} из {PlannedTotal}", $"Токтотулду, жүктөлдү {LoadedTotal} / {PlannedTotal}",
        $"Stopped, loaded {LoadedTotal} of {PlannedTotal}", $"Durduruldu, yüklenen {LoadedTotal} / {PlannedTotal}", $"To'xtatildi, yuklandi {LoadedTotal} / {PlannedTotal}");
}

/// <summary>2026-10-30 (скорость загрузки): пауза/«продолжить» для идущей загрузки карточек. Строки в работе дописываются, новые не берутся.</summary>
public sealed class BulkLoadControl
{
    public volatile bool Paused;
}

public static class BulkCardsLoader
{
    /// <summary>Строк между записями журнала на диск (раньше — пакет по 100 строк с остановкой пула на хвосте пакета).</summary>
    public const int BatchSize = 250;

    /// <summary>Запросов одновременно — верхняя граница (2026-10-30: было 4 внутри пакета, 25 строк/с при задержке 150 мс). Общая очередь запросов клиента — 12.</summary>
    public static int MaxParallel { get; set; } = 8;

    /// <summary>С скольких запросов начинаем; растёт до <see cref="MaxParallel"/> при успехах, при 429 уменьшается на четверть (но не ниже <see cref="MinParallel"/>).</summary>
    public static int StartParallel { get; set; } = 6;

    public const int MinParallel = 2;

    /// <summary>Журнал на диск — не реже, чем раз в столько секунд (и каждые <see cref="BatchSize"/> строк): запись всего журнала на каждую строку не нужна.</summary>
    public static TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Продолжение после обрыва: «есть ли такой товар на сервере» спрашиваем только у первых строк очереди — незаписанными в журнал могли остаться
    /// лишь строки, бывшие в работе при обрыве (журнал пишется каждые 250 строк / 3 с). Остальные строки такой проверки не требуют (было — запрос на каждую строку).</summary>
    public static int ResumeCheckWindow { get; set; } = 1000;

    /// <summary>2026-10-30: один запрос не ждём дольше (общий таймаут клиента — 55 с): зависший запрос повторяется, а не держит поток минуту.</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Пауза между повторами (стенд может уменьшить).</summary>
    public static TimeSpan RetryUnit { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Какие строки плана пишет загрузчик: в режиме «только карточки» — все годные, кроме «без изменений»; в режиме прихода — только
    /// строки с новыми доп. штрихкодами (сам приход ведёт очередь фоновых приходов, штрихкоды ей писать нечем).</summary>
    public static List<BulkImportRow> Select(BulkImportPlan plan) =>
        plan.Rows.Where(r => r.Kind is BulkImportRowKind.Receipt or BulkImportRowKind.New && r.Line is not null && !r.Unchanged
                             && (plan.CardsOnly || r.AltAdded > 0)).ToList();

    /// <summary>Сколько раз повторяем запрос при 5xx / обрыве связи (пауза растёт вдвое: 2, 4, 8, 16, 32 с).</summary>
    public const int MaxRetries = 5;

    /// <summary>2026-10-30: 429 — запрос не выполнен, ждём ровно сколько просит сервер (Retry-After) и повторяем; таких повторов можно больше.</summary>
    public const int MaxRetries429 = 12;

    /// <param name="journal">2026-10-30: журнал загрузки — уже записанные штрихкоды пропускаются, новые добавляются каждые <see cref="BatchSize"/> строк / <see cref="FlushInterval"/> (null — без журнала).</param>
    /// <param name="control">2026-10-30: пауза/«продолжить» (null — без паузы).</param>
    public static async Task<BulkCardsResult> RunAsync(BulkImportPlan plan, ICatalogApiService api, IProgress<int>? progress, CancellationToken ct, BulkImportJournal? journal = null,
        BulkLoadControl? control = null)
    {
        // 2026-10-30 (15 тыс. товаров): пока идёт загрузка, программа не качает весь каталог (AvaloniaCatalogCacheService) — только лента изменений.
        Interlocked.Increment(ref _activeRuns);
        try
        {
            return await RunCoreAsync(plan, api, progress, ct, journal, control).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRuns);
        }
    }

    private static int _activeRuns;

    /// <summary>2026-10-30: идёт массовая загрузка карточек (в этой программе).</summary>
    public static bool IsActive => Volatile.Read(ref _activeRuns) > 0;

    private static async Task<BulkCardsResult> RunCoreAsync(BulkImportPlan plan, ICatalogApiService api, IProgress<int>? progress, CancellationToken ct, BulkImportJournal? journal,
        BulkLoadControl? control)
    {
        var all = Select(plan);
        // 2026-10-30: строки, которые журнал уже знает (прошлый запуск этого файла), пропускаются — повтор не создаёт дублей.
        var todo = journal is null ? all : all.Where(r => r.Line is { } l && !journal.IsDone(l.Barcode)).ToList();
        var resumed = journal is { DoneCount: > 0 };
        var result = new BulkCardsResult { Skipped = (plan.CardsOnly ? plan.UnchangedCount : 0), PlannedTotal = all.Count };
        var alreadyDone = all.Count - todo.Count;
        var done = alreadyDone;
        var created = 0;
        var updated = 0;
        var failed = 0;
        var skippedExisting = 0;
        var authExpired = 0;
        var succeeded = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var batchNo = journal?.BatchesDone ?? 0;
        var gate = new AdaptiveGate(Math.Clamp(MaxParallel, MinParallel, 12), StartParallel);
        var flushLock = new object();
        var sinceFlush = 0;
        var lastFlushTicks = Environment.TickCount64;
        var lastReportTicks = 0L;

        void Flush()
        {
            if (journal is null)
                return;
            lock (flushLock)
            {
                var codes = new List<string>();
                while (succeeded.TryDequeue(out var code))
                    codes.Add(code);
                Volatile.Write(ref sinceFlush, 0);
                Volatile.Write(ref lastFlushTicks, Environment.TickCount64);
                if (codes.Count == 0)
                    return;
                journal.MarkDone(codes);
                journal.BatchesDone = ++batchNo;
                journal.Total = all.Count;
                journal.Save();
            }
        }

        void MaybeFlush()
        {
            if (journal is null)
                return;
            if (Interlocked.Increment(ref sinceFlush) < BatchSize && Environment.TickCount64 - Volatile.Read(ref lastFlushTicks) < (long)FlushInterval.TotalMilliseconds)
                return;
            if (Monitor.TryEnter(flushLock))
            {
                try { Flush(); }
                finally { Monitor.Exit(flushLock); }
            }
        }

        // 2026-10-30: ход показываем не на каждую строку (при 40 строк/с это 40 перерисовок окна в секунду), а раз в 120 мс.
        void Report(bool force)
        {
            if (progress is null)
                return;
            var now = Environment.TickCount64;
            var prev = Interlocked.Read(ref lastReportTicks);
            if (!force && (now - prev < 120 || Interlocked.CompareExchange(ref lastReportTicks, now, prev) != prev))
                return;
            progress.Report(Volatile.Read(ref done));
        }

        var next = -1;
        async Task WorkerAsync(int slot)
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref next) + 1 >= todo.Count)
                    return;
                // Пул «дышит»: рабочий с номером выше текущего предела отдыхает (предел растёт при успехах и падает при 429); на паузе не берёт новых строк.
                if (slot >= gate.Limit || control is { Paused: true })
                {
                    await Task.Delay(control is { Paused: true } ? 250 : 100, stop.Token).ConfigureAwait(false);
                    continue;
                }
                // Сервер просил паузу (429, Retry-After) — не шлём ничего, пока она не кончится: иначе каждый поток зря получает новый отказ.
                var block = ApiThrottle.RemainingBlock;
                if (block > TimeSpan.Zero)
                {
                    await Task.Delay(block, stop.Token).ConfigureAwait(false);
                    continue;
                }
                var index = Interlocked.Increment(ref next);
                if (index >= todo.Count)
                    return;
                var row = todo[index];
                var token = stop.Token;
                try
                {
                    var outcome = await ProcessAsync(row, plan.CardsOnly, resumed && index < ResumeCheckWindow, api, gate, token).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case Outcome.Created: Interlocked.Increment(ref created); break;
                        case Outcome.Updated: Interlocked.Increment(ref updated); break;
                        default: Interlocked.Increment(ref skippedExisting); break;
                    }
                    succeeded.Enqueue(row.Line!.Barcode);
                    gate.OnSuccess();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (ApiException ex) when (ex.StatusCode == 401)
                {
                    // 2026-10-30: 401 после обновления токена — сессия кончилась. Остальные строки не «падают» пачкой ошибок: загрузку останавливаем,
                    // строка остаётся не записанной (журнал её не знает) — после входа «Продолжить загрузку» её допишет.
                    Interlocked.Exchange(ref authExpired, 1);
                    PosLogger.Log("Массовая загрузка: сервер ответил 401 — загрузка остановлена, нужен повторный вход.", "WAREHOUSE");
                    stop.Cancel();
                    throw new OperationCanceledException(stop.Token);
                }
                catch (Exception ex)
                {
                    row.LoadError = Tr.T($"не загружено: {ex.Message}", $"жүктөлгөн жок: {ex.Message}", $"not loaded: {ex.Message}", $"yüklenmedi: {ex.Message}", $"yuklanmadi: {ex.Message}");
                    Interlocked.Increment(ref failed);
                    PosLogger.Log($"Массовая загрузка: строка {row.FileRow} (штрихкод {row.Barcode}) не записана: {ex.Message}", "WAREHOUSE");
                }
                Interlocked.Increment(ref done);
                Report(false);
                MaybeFlush();
            }
        }

        // 2026-10-30: на время загрузки подсказки «товар изменён» (опрос ленты, соседи, телефоны) молчат — одна общая в конце.
        using var quiet = SyncCenter.QuietLocalChanges();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, gate.Max).Select(slot => Task.Run(() => WorkerAsync(slot)))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        finally
        {
            Flush(); // записанное попадает в журнал даже при остановке посреди загрузки
            Report(true);
        }

        result.AuthExpired = authExpired == 1;
        result.Created = created;
        result.Updated = updated;
        result.Failed = failed;
        result.Skipped += skippedExisting;
        result.LoadedTotal = alreadyDone + created + updated + skippedExisting;
        PosLogger.Log($"Массовая загрузка карточек: {result.SummaryText}. Предел запросов в конце {gate.Limit} из {gate.Max}, отказов 429: {gate.Throttled}.", "WAREHOUSE");
        return result;
    }

    /// <summary>2026-10-30: предел одновременных запросов. Растёт на 1 после 5 × предел удачных запросов подряд, при 429 уменьшается на четверть
    /// (не чаще раза в 2 с — пачка 429 от запросов, уже бывших в пути, не должна сбивать предел до минимума).</summary>
    internal sealed class AdaptiveGate
    {
        private readonly object _g = new();
        private int _limit;
        private int _okRun;
        private long _lastCutTicks = long.MinValue / 2;

        public AdaptiveGate(int max, int start)
        {
            Max = Math.Max(MinParallel, max);
            _limit = Math.Clamp(start, MinParallel, Max);
        }

        public int Max { get; }
        public int Limit { get { lock (_g) return _limit; } }
        public int Throttled { get { lock (_g) return _throttled; } }
        private int _throttled;

        public void OnSuccess()
        {
            lock (_g)
            {
                if (++_okRun < _limit * 5 || _limit >= Max)
                    return;
                _limit++;
                _okRun = 0;
            }
        }

        public void OnThrottle()
        {
            lock (_g)
            {
                _throttled++;
                _okRun = 0;
                var now = Environment.TickCount64;
                if (now - _lastCutTicks < 2000)
                    return;
                _lastCutTicks = now;
                _limit = Math.Max(MinParallel, _limit * 3 / 4);
            }
        }
    }

    private enum Outcome { Created, Updated, AlreadyThere }

    private static async Task<Outcome> ProcessAsync(BulkImportRow row, bool cardsOnly, bool checkExists, ICatalogApiService api, AdaptiveGate? gate, CancellationToken ct)
    {
        var line = row.Line!;
        if (row.Kind == BulkImportRowKind.New)
        {
            // 2026-10-30: продолжение после обрыва — товар мог быть создан до обрыва, но не попасть в журнал: сначала смотрим, есть ли он на сервере
            // (только у первых строк очереди, см. ResumeCheckWindow).
            if (checkExists && await ExistsAsync(line.Barcode, api, gate, ct).ConfigureAwait(false))
                return Outcome.AlreadyThere;
            var request = new ProductEditRequest
            {
                Name = line.ProductName.Trim(),
                Barcode = line.Barcode,
                Unit = line.Unit,
                CategoryName = line.CategoryName,
                IsWeight = line.IsWeight,
                Quantity = 0,
                SendQuantity = false,
                PurchasePrice = line.PurchasePrice,
                Price = line.SalePrice,
                MarkupPercent = ReceivingPricing.MarkupPercent(line.PurchasePrice, line.SalePrice),
                AlternateBarcodes = row.AltFull.Count > 0 ? string.Join('\n', row.AltFull.Select(v => v.Barcode)) : null,
                IsNew = true,
            };
            await WithRetryAsync(async t => { await api.CreateProductAsync(request, t).ConfigureAwait(false); }, createdBarcode: line.Barcode, api, gate, ct).ConfigureAwait(false);
            row.LoadOutcome = "created";
            return Outcome.Created;
        }

        var fields = new Dictionary<string, object?>();
        var inv = CultureInfo.InvariantCulture;
        if (cardsOnly && row.ChPurchase)
            fields["purchase_price"] = line.PurchasePrice.ToString("0.##", inv);
        if (cardsOnly && row.ChSale)
            fields["price"] = line.SalePrice.ToString("0.##", inv);
        if (cardsOnly && (row.ChPurchase || row.ChSale) && ReceivingPricing.MarkupPercent(line.PurchasePrice, line.SalePrice) is { } markup)
            fields["markup_percent"] = markup.ToString("0.##", inv);
        if (row.AltAdded > 0)
        {
            // 2026-10-30 (приёмка 1.17.65): список из кэша каталога мог устареть — доп. штрихкод, добавленный на сайте позже, стирался. Перед записью
            // читаем карточку с сервера и шлём объединённый список: то, что уже на сервере, + новые из файла. (Только строки с доп. штрихкодами.)
            var merged = await MergeAlternatesAsync(row, api, ct, gate).ConfigureAwait(false);
            fields["alternate_barcodes"] = merged.Select(v => (object)new Dictionary<string, object?>
            {
                ["barcode"] = v.Barcode,
                ["name"] = v.Name ?? "",
                ["quantity"] = v.Quantity,
            }).ToArray();
        }
        if (fields.Count == 0 || string.IsNullOrWhiteSpace(line.ProductId))
            return Outcome.Updated;

        await WithRetryAsync(async t => { ServerPriceContract.Observe(await api.PatchProductFieldsAsync(line.ProductId!, fields, t).ConfigureAwait(false)); },
            createdBarcode: null, api, gate, ct).ConfigureAwait(false);
        row.LoadOutcome = "updated";
        return Outcome.Updated;
    }

    private static async Task<bool> ExistsAsync(string barcode, ICatalogApiService api, AdaptiveGate? gate, CancellationToken ct)
    {
        System.Text.Json.JsonElement? found = null;
        await WithRetryAsync(async t => { found = await api.FindWarehouseProductByBarcodeAsync(barcode, t).ConfigureAwait(false); }, createdBarcode: null, api, gate, ct).ConfigureAwait(false);
        return found is not null;
    }

    /// <summary>Объединённый список доп. штрихкодов: сначала то, что сейчас на сервере (с названиями и количествами упаковок), потом новые из файла, которых там нет.
    /// Карточку не нашли или в ней нет списка — остаётся список плана (он уже собран с учётом кэша каталога).</summary>
    internal static async Task<List<AlternateBarcodeVariant>> MergeAlternatesAsync(BulkImportRow row, ICatalogApiService api, CancellationToken ct, AdaptiveGate? gate = null)
    {
        var line = row.Line!;
        System.Text.Json.JsonElement? card = null;
        await WithRetryAsync(async t => { card = await api.FindWarehouseProductByBarcodeAsync(row.Tile?.Barcode ?? line.Barcode, t).ConfigureAwait(false); },
            createdBarcode: null, api, gate, ct).ConfigureAwait(false);
        return MergeAlternates(card, row.AltFull);
    }

    /// <summary>Чистое слияние (для проверки без сети): <paramref name="card"/> — карточка товара с сервера, <paramref name="planned"/> — список плана.</summary>
    public static List<AlternateBarcodeVariant> MergeAlternates(System.Text.Json.JsonElement? card, IReadOnlyList<AlternateBarcodeVariant> planned)
    {
        var merged = new List<AlternateBarcodeVariant>();
        if (card is { ValueKind: System.Text.Json.JsonValueKind.Object } c
            && c.TryGetProperty("alternate_barcodes", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                    merged.Add(new AlternateBarcodeVariant { Barcode = s.Trim() });
                else if (el.ValueKind == System.Text.Json.JsonValueKind.Object && el.TryGetProperty("barcode", out var bc))
                {
                    var code = bc.ValueKind == System.Text.Json.JsonValueKind.Number ? bc.GetRawText() : bc.GetString();
                    if (string.IsNullOrWhiteSpace(code))
                        continue;
                    var name = el.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
                    var qty = el.TryGetProperty("quantity", out var q) && JsonNumericReader.TryToDouble(q, out var qv) ? qv : 0;
                    merged.Add(new AlternateBarcodeVariant { Barcode = code.Trim(), Name = name, Quantity = qty });
                }
            }
        }
        foreach (var v in planned)
            if (!string.IsNullOrWhiteSpace(v.Barcode) && !merged.Any(m => SameCode(m.Barcode, v.Barcode)))
                merged.Add(v);
        return merged;
    }

    private static bool SameCode(string a, string b) => a.Trim().TrimStart('0') == b.Trim().TrimStart('0');

    /// <summary>2026-10-30: ответы 5xx (и 408) и обрывы связи повторяем до <see cref="MaxRetries"/> раз с растущей паузой (2, 4, 8, 16, 32 с; если сервер просит
    /// паузу больше — ждём её, ApiThrottle.RemainingBlock). Изменение товара повторять безопасно (поля абсолютные); создание — только убедившись, что товара с этим
    /// штрихкодом ещё нет (запрос мог дойти до записи, а ответ потеряться). 429 — запрос не выполнен: проверка не нужна, ждём ровно столько, сколько просит сервер
    /// (Retry-After), а не растущие 2–32 с, и пул запросов уменьшается (<see cref="AdaptiveGate.OnThrottle"/>); таких повторов до <see cref="MaxRetries429"/>.</summary>
    private static async Task WithRetryAsync(Func<CancellationToken, Task> send, string? createdBarcode, ICatalogApiService api, AdaptiveGate? gate, CancellationToken ct)
    {
        var failures = 0;
        var throttles = 0;
        while (true)
        {
            try
            {
                // 2026-10-30: у каждой попытки свой предел времени (RequestTimeout); отмена загрузки (ct) его перекрывает.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(RequestTimeout);
                await send(attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested
                                       && (ex is ApiException { StatusCode: 429 } ? ++throttles <= MaxRetries429 : ++failures <= MaxRetries))
            {
                var is429 = ex is ApiException { StatusCode: 429 };
                if (is429)
                    gate?.OnThrottle();
                var asked = ApiThrottle.RemainingBlock;
                var pause = is429
                    ? (asked > TimeSpan.Zero ? asked : RetryUnit)
                    : TimeSpan.FromTicks(RetryUnit.Ticks * (1L << (failures - 1)));
                if (!is429 && asked > pause)
                    pause = asked;
                await Task.Delay(pause, ct).ConfigureAwait(false);
                if (createdBarcode is not null && !is429)
                {
                    try
                    {
                        if (await api.FindWarehouseProductByBarcodeAsync(createdBarcode, ct).ConfigureAwait(false) is not null)
                            return; // товар уже создан до обрыва
                    }
                    catch (Exception checkEx) when (IsTransient(checkEx))
                    {
                        // связь всё ещё рвётся — следующая попытка разберётся
                    }
                }
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is ApiException { StatusCode: 429 or 408 or >= 500 } || ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException; // отмену самой загрузки отсекает фильтр !ct.IsCancellationRequested в WithRetryAsync
}
