using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

/// <summary>Синхронизация остатков каталога с API и корректировка после продаж.</summary>
public static class StockSyncService
{
    public const double LowStockThreshold = CatalogProductTileVm.LowStockQuantityThreshold;

    /// <summary>
    /// Читает остаток из JSON товара. Поддерживает плоские поля и массив stocks[] с привязкой к кассе.
    /// </summary>
    public static double ResolveStockQuantity(JsonElement product, bool mustWeigh) =>
        ResolveStockQuantity(product, mustWeigh, null);

    /// <param name="props">2026-10-04, отчёт о производительности (п. 16): поля товара, заранее собранные
    /// одним проходом (ProductCatalogMapper.TryTile, JsonProps) — те же правила, без перебора всех полей
    /// товара на каждое из ~15 имён остатка.</param>
    internal static double ResolveStockQuantity(JsonElement product, bool mustWeigh, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (product.ValueKind != JsonValueKind.Object)
            return 0;

        // 1) Остатки по кассам/складам — приоритет для POS.
        if (TryResolveFromStocksArray(product, mustWeigh, props) is { } fromStocks)
            return fromStocks;

        // 2) Весовые плоские поля.
        if (mustWeigh)
        {
            if (TryReadDouble(product, "stock_weight", props) is { } sw)
                return sw;
            if (TryReadDouble(product, "weight", props) is { } w)
                return w;
        }

        // 3) Плоские поля остатка.
        foreach (var key in new[]
                 {
                     "stock_quantity",
                     "available_quantity",
                     "available",
                     "current_stock",
                     "balance",
                     "remains",
                     "remain",
                     "stock",
                     "qty",
                     "quantity",
                 })
        {
            if (TryReadDouble(product, key, props) is { } value)
                return value;
        }

        // 4) Вложенный объект stock: { quantity / ... }
        if (JsonProps.TryGet(product, props, "stock", out var stockObj) && stockObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "stock_quantity", "quantity", "qty", "amount", "value" })
            {
                if (TryReadDouble(stockObj, key) is { } nested)
                    return nested;
            }
        }

        return 0;
    }

    /// <summary>
    /// Синхронно выставляет Quantity/StockInfo (для маппинга и записи в SQLite до привязки к UI).
    /// </summary>
    public static void ApplyQuantityToTile(CatalogProductTileVm vm, double quantity, bool mustWeigh)
    {
        ApplyQuantityCore(vm, quantity, mustWeigh);
    }

    /// <summary>
    /// Обновление уже отображаемой плитки: PropertyChanged только на UI-потоке.
    /// </summary>
    public static void ApplyQuantityToTileOnUi(CatalogProductTileVm vm, double quantity, bool mustWeigh) =>
        UiDispatcherHolder.Post(() => ApplyQuantityCore(vm, quantity, mustWeigh));

    private static void ApplyQuantityCore(CatalogProductTileVm vm, double quantity, bool mustWeigh)
    {
        vm.Quantity = quantity;
        var qtyCulture = CultureInfo.GetCultureInfo("ru-RU");
        vm.StockInfo = mustWeigh
            // 2026-10-31: «Не округляй» — остаток весового до 3 знаков без потери (12,345 кг, не «12,35»; сервер хранит до 3 знаков).
            ? $"{quantity.ToString("0.###", qtyCulture)} {Tr.T("кг", "кг", "kg", "kg", "kg")}"
            : QuantityRules.ForUnit(vm.Unit) != null
                // 2026-10-26 (ХМ2): остаток дробной единицы «Хозтоваров» (37.4 м) — не до целого и в своей единице.
                ? $"{quantity.ToString("0.###", qtyCulture)} {UnitCatalog.Display(vm.Unit)}" // 2026-11-01 (Д-5): единица по языку интерфейса
                // 2026-11-01 (расследование, П-5): штучный товар с дробным остатком (поштучная продажа из упаковки: 49,875) — без округления до «50».
                : $"{(Math.Abs(quantity - Math.Round(quantity)) < 0.0005 ? quantity.ToString("F0", qtyCulture) : quantity.ToString("0.###", qtyCulture))} {Tr.T("шт.", "даана", "pcs", "adet", "dona")}";
        vm.IsLowStock = CatalogProductTileVm.IsLow(quantity, vm.MinQuantity); // 2026-11-06 (В12, Б-8): «Минимальный остаток» карточки, иначе общий порог 10
    }

    public static async Task OverlayAgentStockAsync(
        IEnumerable<CatalogProductTileVm> products,
        CancellationToken ct = default)
    {
        List<JsonElement> agentProducts;
        try
        {
            agentProducts = await PosApp.CatalogApi.GetAgentProductsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OverlayAgentStock failed: {ex}", "STOCK");
            return;
        }

        if (agentProducts.Count == 0)
        {
            PosLogger.Log("OverlayAgentStock: empty agent stock payload", "STOCK");
            return;
        }

        var qtyMap = BuildQuantityMap(agentProducts);
        var applied = 0;
        foreach (var vm in products)
        {
            if (!qtyMap.TryGetValue(vm.Id, out var qty))
                continue;

            // Синхронно: иначе SyncReplaceAllWithDiff сохранит Quantity=0 до Post.
            ApplyQuantityToTile(vm, qty, vm.MustWeigh);
            applied++;
        }

        PosLogger.Log($"OverlayAgentStock: applied={applied}, map={qtyMap.Count}", "STOCK");
    }

    // 2026-10-29: когда остаток товара последний раз менялся на этом ПК (продажа, возврат, ревизия, приход) — лента каталога
    // не применяет к такому товару серверный остаток ещё ~90 с (FeedStockGuard): сервер мог не успеть учесть операцию.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LocalTouches = new(StringComparer.OrdinalIgnoreCase);

    internal static void MarkLocallyTouched(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        LocalTouches[productId.Trim()] = Environment.TickCount64;
        Interlocked.Exchange(ref _lastAnyTouch, Environment.TickCount64);
        if (LocalTouches.Count > 2000)
            foreach (var kv in LocalTouches.Where(kv => Environment.TickCount64 - kv.Value > 600_000).ToList())
                LocalTouches.TryRemove(kv.Key, out _);
    }

    private static long _lastAnyTouch;

    /// <summary>2026-10-29: остаток какого-нибудь товара менялся на этом ПК за последнее время (признак «своя продажа» для RemoteSalesPulse).</summary>
    public static bool LocalActivityWithin(TimeSpan window)
    {
        var at = Interlocked.Read(ref _lastAnyTouch);
        return at != 0 && Environment.TickCount64 - at < window.TotalMilliseconds;
    }

    /// <summary>2026-11-01 (расследование «остаток не меняется», П-4): остаток товара менялся на этом ПК ПОСЛЕ указанного момента (начала загрузки каталога) —
    /// страницы, скачанные до этого, несут остаток без этой операции.</summary>
    public static bool TouchedSince(string productId, DateTime sinceUtc) =>
        IsReceiptHeld(productId) // 2026-11-11 (Д-4): приход товара в пути — серверный остаток мог уже включать его
        || LocalTouches.TryGetValue(productId, out var at) && Environment.TickCount64 - at <= (DateTime.UtcNow - sinceUtc).TotalMilliseconds;

    /// <summary>2026-11-12 (Л-23): остаток менялся на этом ПК после <paramref name="sinceUtc"/> — только местные операции, без защиты «приход в пути».</summary>
    internal static bool LocalTouchedSince(string productId, DateTime sinceUtc) =>
        LocalTouches.TryGetValue(productId.Trim(), out var at) && Environment.TickCount64 - at <= (DateTime.UtcNow - sinceUtc).TotalMilliseconds;

    /// <summary>2026-11-12 (Л-23): товар продан соседней кассой по LAN, сервер ещё не знает (как в FeedStockGuard.IsHeldByLocalActivity).</summary>
    private static bool LanPending(string productId)
    {
        try
        {
            return UserPreferences.Instance.LanSyncEnabled
                && Lan.LanStockAdjuster.PendingProductIds().Any(p => string.Equals(p, productId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    public static bool WasLocallyTouched(string productId, TimeSpan window) =>
        IsReceiptHeld(productId) // 2026-11-11 (Д-4): приход товара в пути
        || LocalTouches.TryGetValue(productId, out var at) && Environment.TickCount64 - at < window.TotalMilliseconds;

    /// <summary>2026-11-13: защита остатков перед записью полной загрузки (общая для AvaloniaCatalogCacheService и запасного пути CatalogCacheService.SyncCatalogFullAsync): товар, чей остаток менялся
    /// на этом ПК за последние 90 с или после начала загрузки (<paramref name="snapshotStartedUtc"/>), или чей приход стоит в очереди («приход в пути»), сохраняет локальный остаток вместо серверного снимка;
    /// потом его перечитают с сервера.</summary>
    public static void KeepLocalStockForTouched(IEnumerable<CatalogProductTileVm> list, DateTime snapshotStartedUtc)
    {
        foreach (var vm in list)
        {
            // 2026-11-01 (П-4): и всё, что менялось ПОСЛЕ начала загрузки (долгая загрузка 2–6 мин): страницы до продажи несут остаток без неё.
            if (vm.Id is null || !(WasLocallyTouched(vm.Id, FeedStockGuard.LocalTouchWindow) || TouchedSince(vm.Id, snapshotStartedUtc)))
                continue;
            if (LocalProductRepository.Instance.LoadTileCopy(vm.Id) is { } keep)
                ApplyQuantityToTile(vm, keep.Quantity, vm.MustWeigh);
            ScheduleStockReread(vm.Id, FeedStockGuard.LocalTouchWindow + TimeSpan.FromSeconds(5)); // 2026-11-01 (С-4): оставили локальный — потом перечитаем с сервера
        }
    }

    // 2026-11-11 (Д-4, живая проверка на NBS: приход упаковками 2 × 5 — в «Склад → Товары» 20 шт вместо 10, журнал «лента: остаток … не применён»): очередь фоновых приходов
    // прибавляет принятое к ТЕКУЩЕМУ остатку плитки после ответа сервера (BackgroundReceivingQueue.ApplyStockToTileAsync). Между записью на сервере и этой прибавкой лента
    // (опрос ~3 с), точечное обновление или полная загрузка успевали положить на плитку серверный остаток, УЖЕ включающий приход (0 → 10), — очередь прибавляла 10 второй раз (20),
    // а защита «трогали недавно» (90 с) после этого не пускала верное число из ленты. Теперь товары, чей приход стоит в очереди и ещё не прибавлен к плитке, для ленты, точечного
    // обновления, полной загрузки и перечитывания считаются «трогали сейчас» (WasLocallyTouched/TouchedSince) — с постановки в очередь до прибавки или до завершения строки без прихода.
    // Наборы — по владельцу (очередь), после публикации не меняются: читаются без замка на каждом товаре ленты.
    private static readonly Dictionary<object, HashSet<string>> ReceiptHoldOwners = new(ReferenceEqualityComparer.Instance);
    private static HashSet<string>[] _receiptHolds = [];

    /// <summary>2026-11-11 (Д-4): заменить набор товаров «приход в пути» владельца <paramref name="owner"/> (пустой набор — снять все его товары).</summary>
    public static void SetReceiptHolds(object owner, IEnumerable<string?> productIds)
    {
        var set = new HashSet<string>(productIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()), StringComparer.OrdinalIgnoreCase);
        lock (ReceiptHoldOwners)
        {
            if (set.Count == 0)
                ReceiptHoldOwners.Remove(owner);
            else
                ReceiptHoldOwners[owner] = set;
            Volatile.Write(ref _receiptHolds, ReceiptHoldOwners.Values.ToArray());
        }
    }

    /// <summary>2026-11-11 (Д-4): приход этого товара стоит в очереди и ещё не прибавлен к плитке.</summary>
    public static bool IsReceiptHeld(string? productId)
    {
        var sets = Volatile.Read(ref _receiptHolds);
        if (sets.Length == 0 || string.IsNullOrWhiteSpace(productId))
            return false;
        var id = productId.Trim();
        foreach (var s in sets)
            if (s.Contains(id))
                return true;
        return false;
    }

    /// <summary>2026-11-11 (Д-4): остаток товара после ЕГО прихода — с сервера (источник правды) на плитку и в базу, минус неотправленные чеки этого ПК. Для строк, где
    /// «остаток плитки + принятое» посчитать нельзя: «упаковка с поштучной продажей» у товара склада — приход в упаковках, а плитка до перевода (convert-to-packages) в штуках.
    /// Защиту «приход в пути» не спрашивает: её держит сам этот приход, а сервер его уже учёл. true — записано.</summary>
    public static async Task<bool> ApplyServerStockAfterReceiptAsync(string productId, CancellationToken ct = default)
    {
        try
        {
            var startedUtc = DateTime.UtcNow; // 2026-11-12 (Л-23): отметка начала запроса — как в RefreshStockFromServerAsync
            var detail = await PosApp.CatalogApi.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
            if (detail is not { } el)
                return false;
            var tile = CatalogCacheService.FindProductById(productId);
            if (tile is null)
                return false;
            // 2026-11-12 (Л-23, аудит Fable): продажа/возврат этого товара на этой кассе за время запроса — ответ мог быть построен до неё, запись затёрла бы её. Не пишем, вызывающий
            // поставит перечитывание (оно ждёт окно защиты «трогали недавно»). «Приход в пути» не спрашиваем: его держит сам этот приход (TouchedSince здесь не годится).
            if (LocalTouchedSince(productId, startedUtc) || LanPending(productId))
            {
                PosLogger.Log($"STOCK: остаток товара {productId} менялся здесь во время запроса после прихода — серверное значение не записано, повтор позже.", "STOCK");
                return false;
            }
            var server = ResolveStockQuantity(el, tile.MustWeigh);
            var unsent = FeedStockGuard.UnsentQuantity(productId);
            var qty = unsent > 0 ? Math.Max(0, server - unsent) : server;
            PosLogger.Log($"STOCK: остаток товара {productId} после прихода — с сервера: {tile.Quantity:0.###} → {qty:0.###}" + (unsent > 0 ? $" (сервер {server:0.###} минус неотправленные чеки {unsent:0.###})." : "."), "STOCK");
            UiDispatcherHolder.Post(() => ApplyQuantityCore(tile, qty, tile.MustWeigh));
            CatalogCacheService.PersistProductStock(productId, qty, tile.MustWeigh);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"STOCK: остаток товара {productId} после прихода не прочитан с сервера ({ex.GetType().Name}: {ex.Message}) — перечитаем позже.", "STOCK");
            return false;
        }
    }

    public static void DecrementLocalStock(string productId, double soldQty)
    {
        if (string.IsNullOrWhiteSpace(productId) || soldQty <= 0)
            return;

        var id = productId.Trim();
        MarkLocallyTouched(id);
        foreach (var vm in CatalogCacheService.Products)
        {
            if (!string.Equals(vm.Id, id, StringComparison.OrdinalIgnoreCase))
                continue;

            // Авторитетное значение считаем и персистим синхронно; UI-плитка обновится отложенно.
            var next = Math.Max(0, vm.Quantity - soldQty);
            ApplyQuantityToTileOnUi(vm, next, vm.MustWeigh);
            CatalogCacheService.PersistProductStock(id, next, vm.MustWeigh);
            break;
        }
    }

    /// <summary>2026-10-04: сразу после оплаты — остаток проданных товаров уменьшается синхронно (и в базе, и
    /// на плитках). Строки одного товара складываются: плитка обновляется отложенно, и второе вычитание по
    /// той же vm.Quantity потеряло бы первое. Возвращает ожидаемый остаток после продажи — по нему фоновая
    /// сверка с сервером (RefreshSoldItemsStockAsync) не даст запоздавшему ответу вернуть старое число.</summary>
    public static IReadOnlyDictionary<string, double> ApplySoldItemsDecrement(JsonElement cart)
    {
        var sold = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in CartDisplayHelper.EnumerateItems(cart))
        {
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrEmpty(productId))
                continue;
            var tile = CatalogCacheService.FindProductById(productId); // 2026-11-06 (шаг 17): по индексу, а не проходом по всему каталогу
            if (tile is null)
                PosLogger.Log($"STOCK: проданный товар {productId} не найден в каталоге кассы — плитка не уменьшена (остаток придёт из ленты/сверки).", "STOCK"); // 2026-11-01 (расследование, п. 6)
            var qty = CartDisplayHelper.LineQuantityInStockUnits(line, tile);
            sold[productId] = (sold.TryGetValue(productId, out var before) ? before : 0) + qty;
        }

        var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, qty) in sold)
        {
            if (FindExpectedPostSaleQuantity(productId, qty) is { } max)
                expected[productId] = max;
            DecrementLocalStock(productId, qty);
        }
        PosLogger.Log($"Остаток после продажи уменьшен сразу: товаров {expected.Count}.", "STOCK");
        return expected;
    }

    /// <summary>2026-10-20 (живой аудит кассы, К8): после возврата остаток в каталоге не возвращался до «Обновить каталог»
    /// (продажа уменьшает сразу — ApplySoldItemsDecrement). Возвращённое количество (в единицах остатка каталога) —
    /// сразу на плитки и в базу. Возвращает ожидаемый остаток после возврата — для сверки с сервером
    /// (RefreshReturnedItemsStockAsync).</summary>
    public static IReadOnlyDictionary<string, double> ApplyReturnedItemsIncrement(IReadOnlyDictionary<string, double> returned)
    {
        var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, qty) in returned)
        {
            if (string.IsNullOrWhiteSpace(productId) || qty <= 0)
                continue;
            var id = productId.Trim();
            foreach (var vm in CatalogCacheService.Products)
            {
                if (!string.Equals(vm.Id, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                // 2026-10-31 (стресс-тест 1.17.68, В-6): у услуги остатка нет (продажа его не уменьшает) — возврат не должен «поднимать» его с 0 до 1–2.
                if (vm.IsService)
                    break;
                var next = vm.Quantity + qty;
                MarkLocallyTouched(id);
                expected[id] = next;
                ApplyQuantityToTileOnUi(vm, next, vm.MustWeigh);
                CatalogCacheService.PersistProductStock(id, next, vm.MustWeigh);
                break;
            }
        }
        PosLogger.Log($"Остаток после возврата увеличен сразу: товаров {expected.Count}.", "STOCK");
        return expected;
    }

    /// <summary>2026-10-20 (К8): сверка остатка вернувшихся товаров с сервером. Сервер мог ещё не учесть возврат и отдать
    /// старое (меньшее) число — тогда оставляем увеличенный остаток, как при продаже.</summary>
    public static async Task RefreshReturnedItemsStockAsync(IReadOnlyDictionary<string, double> expectedMin, CancellationToken ct = default)
    {
        foreach (var (productId, min) in expectedMin)
        {
            try
            {
                var detail = await PosApp.CatalogApi.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
                if (detail is not { } el)
                    continue;
                var tile = CatalogCacheService.FindProductById(productId); // 2026-11-06 (шаг 17)
                var mustWeigh = tile?.MustWeigh ?? CartDisplayHelper.ProductMustWeigh(el);
                var qty = ResolveStockQuantity(el, mustWeigh);
                if (qty < min - 1e-6)
                {
                    PosLogger.Log($"RefreshReturnedItemsStock: stale server qty for {productId} ({qty} < expected {min}) — keeping optimistic value", "STOCK");
                    continue;
                }

                if (tile != null)
                    UiDispatcherHolder.Post(() => ApplyQuantityCore(tile, qty, tile.MustWeigh));
                CatalogCacheService.PersistProductStock(productId, qty, mustWeigh);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"RefreshReturnedItemsStock failed for {productId}: {ex.Message}", "STOCK");
            }
        }
    }

    // ---- 2026-11-01 (приёмка 1.17.72, С-4/С-5): отложенное перечитывание остатка ----
    // Защита остатка (FeedStockGuard: очередь неотправленных чеков, «трогали недавно») отбрасывает изменения товара из ленты, а курсор ленты уходит вперёд —
    // пропущенное законное изменение (приход с другого ПК) больше не повторяется. Поэтому такие товары ставятся в очередь: когда защита с них
    // снята, остаток читается с сервера (ProductsDetailAsync) и кладётся на плитку и в базу. Срок жизни записи — 30 минут.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Due, long Since)> RereadQueue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RereadLoopGate = new();
    private static bool _rereadLoopRunning;
    // 2026-11-12 (Л-6): срок жизни записи, шаг цикла и пауза «защита действует» — поля для тестов (по умолчанию 30 минут, 5 с и 30 с, как раньше).
    internal static TimeSpan RereadMaxAge = TimeSpan.FromMinutes(30);
    internal static TimeSpan RereadTick = TimeSpan.FromSeconds(5);
    internal static TimeSpan RereadRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>2026-11-12 (Л-6): товар стоит в очереди перечитывания остатка (для тестов).</summary>
    internal static bool IsRereadScheduled(string productId) => RereadQueue.ContainsKey(productId.Trim());

    /// <summary>2026-11-12 (Л-6): через сколько перечитывание товара (null — не стоит в очереди); для тестов.</summary>
    internal static TimeSpan? RereadDueIn(string productId) =>
        RereadQueue.TryGetValue(productId.Trim(), out var e) ? TimeSpan.FromMilliseconds(e.Due - Environment.TickCount64) : null;

    /// <summary>Поставить товар в очередь перечитывания остатка через <paramref name="delay"/> (если он уже в очереди — срок не сдвигается).</summary>
    public static void ScheduleStockReread(string productId, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        var now = Environment.TickCount64;
        RereadQueue.AddOrUpdate(productId.Trim(), (now + (long)delay.TotalMilliseconds, now), (_, old) => (old.Due, now)); // 2026-11-12 (Л-6): срок не сдвигается, возраст считается с последнего запроса
        lock (RereadLoopGate)
        {
            if (_rereadLoopRunning)
                return;
            _rereadLoopRunning = true;
        }
        _ = Task.Run(RereadLoopAsync);
    }

    /// <summary>Товары чека (JSON корзины) — в очередь перечитывания (после решения по чеку: «Отклонить», досылка).</summary>
    public static void ScheduleStockRereadForCart(string cartJson, TimeSpan delay)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            foreach (var line in CartDisplayHelper.EnumerateItems(doc.RootElement))
                if (CartDisplayHelper.TryProductId(line) is { Length: > 0 } id)
                    ScheduleStockReread(id, delay);
        }
        catch (JsonException)
        {
            // чек с битым JSON — перечитывать нечего
        }
    }

    private static async Task RereadLoopAsync()
    {
        try
        {
            while (!RereadQueue.IsEmpty)
            {
                await Task.Delay(RereadTick).ConfigureAwait(false);
                var now = Environment.TickCount64;
                var due = RereadQueue.Where(kv => kv.Value.Due <= now).Select(kv => kv.Key).ToList();
                if (due.Count == 0)
                    continue;

                foreach (var id in due)
                {
                    if (!RereadQueue.TryGetValue(id, out var entry))
                        continue;
                    // 2026-11-12 (Л-6, аудит Fable): пока приход товара ждёт в очереди (защита «приход в пути»), запись не стареет — иначе после 30 минут ожидания перечитывание
                    // снималось, и чужие продажи за это время так и не подтягивались. Возраст отсчитывается заново с момента снятия защиты.
                    if (IsReceiptHeld(id))
                    {
                        RereadQueue[id] = (now + (long)RereadRetryDelay.TotalMilliseconds, now);
                        continue;
                    }
                    if (now - entry.Since > (long)RereadMaxAge.TotalMilliseconds)
                    {
                        RereadQueue.TryRemove(id, out _);
                        continue;
                    }
                    if (FeedStockGuard.IsHeldByLocalActivity(id))
                    {
                        RereadQueue[id] = (now + (long)RereadRetryDelay.TotalMilliseconds, entry.Since); // защита ещё действует — позже
                        continue;
                    }
                    RereadQueue.TryRemove(id, out _);
                    await RefreshStockFromServerAsync(id).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"STOCK: перечитывание остатков остановлено ({ex.GetType().Name}: {ex.Message}).", "STOCK");
        }
        finally
        {
            lock (RereadLoopGate)
                _rereadLoopRunning = false;
            if (!RereadQueue.IsEmpty)
                ScheduleStockReread(RereadQueue.Keys.First(), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Остаток товара с сервера — на плитку и в базу (без вычитания), безопасно по отношению к локальным операциям (контрольное ревью):
    /// (1) запрос стартует с отметкой времени; (2) после ответа — если за время запроса остаток товара менялся здесь (продажа/правка), ответ мог быть построен
    /// до неё: не пишем, повтор позже; то же при недавней операции (окно защиты) и продаже соседней кассы по LAN; (3) если у товара есть неотправленные чеки
    /// (очередь, свежие «Ошибка отправки»), сервер о них не знает — пишем серверный остаток минус эти количества. true — записано.</summary>
    private static async Task<bool> RefreshStockFromServerAsync(string productId)
    {
        try
        {
            var startedUtc = DateTime.UtcNow;
            var detail = await PosApp.CatalogApi.ProductsDetailAsync(productId, CancellationToken.None).ConfigureAwait(false);
            if (detail is not { } el)
                return false;
            var tile = CatalogCacheService.FindProductById(productId); // 2026-11-06 (шаг 17)
            if (tile is null)
                return false;
            if (TouchedSince(productId, startedUtc) || FeedStockGuard.IsHeldByLocalActivity(productId))
            {
                PosLogger.Log($"STOCK: остаток товара {productId} менялся здесь во время запроса — серверное значение не записано, повтор позже.", "STOCK");
                ScheduleStockReread(productId, FeedStockGuard.LocalTouchWindow + TimeSpan.FromSeconds(5));
                return false;
            }
            var server = ResolveStockQuantity(el, tile.MustWeigh);
            var unsent = FeedStockGuard.UnsentQuantity(productId);
            var qty = unsent > 0 ? Math.Max(0, server - unsent) : server;
            if (Math.Abs(qty - tile.Quantity) > 1e-6)
                PosLogger.Log($"STOCK: перечитан остаток товара {productId}: {tile.Quantity:0.###} → {qty:0.###}" + (unsent > 0 ? $" (сервер {server:0.###} минус неотправленные чеки {unsent:0.###})." : "."), "STOCK");
            UiDispatcherHolder.Post(() => ApplyQuantityCore(tile, qty, tile.MustWeigh));
            CatalogCacheService.PersistProductStock(productId, qty, tile.MustWeigh);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"STOCK: остаток товара {productId} не перечитан ({ex.GetType().Name}: {ex.Message}).", "STOCK");
            return false;
        }
    }

    /// <summary>2026-11-01 (расследование «остаток не меняется», П-5): «оплата уже прошла на сервере (ответ потерялся)» — локально остаток НЕ вычитаем
    /// (лента могла уже принести серверный, было бы двойное списание); в фоне читаем остаток проданных товаров с сервера и кладём его на плитки и в базу.
    /// Ошибки не мешают завершению чека.</summary>
    public static void ResyncCartStockFromServerInBackground(string cartJson)
    {
        if (string.IsNullOrWhiteSpace(cartJson))
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(cartJson);
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in CartDisplayHelper.EnumerateItems(doc.RootElement))
                {
                    var productId = CartDisplayHelper.TryProductId(line);
                    if (string.IsNullOrEmpty(productId) || !done.Add(productId))
                        continue;
                    await RefreshStockFromServerAsync(productId).ConfigureAwait(false); // контрольное ревью: с проверкой локальных операций и неотправленных чеков
                }
                ProductVariantCache.ApplySale(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"STOCK: остаток после «оплата уже прошла на сервере» не прочитан ({ex.GetType().Name}: {ex.Message}).", "STOCK");
            }
        });
    }

    /// <param name="alreadyDecremented">2026-10-04: остаток уже уменьшен ApplySoldItemsDecrement — здесь только
    /// сверка с сервером (значения — ожидаемый остаток после продажи).</param>
    public static async Task RefreshSoldItemsStockAsync(JsonElement cart, CancellationToken ct = default,
        IReadOnlyDictionary<string, double>? alreadyDecremented = null)
    {
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in CartDisplayHelper.EnumerateItems(cart))
        {
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrEmpty(productId))
                continue;

            // 2026-09-13: конвертация в единицы остатка каталога (см. CartDisplayHelper.
            // LineQuantityInStockUnits) — раньше здесь бралось сырое количество ШТУК строки и
            // напрямую вычиталось из остатка в УПАКОВКАХ для поштучной продажи из упаковки.
            var tile = CatalogCacheService.FindProductById(productId); // 2026-11-06 (шаг 17)
            double? expectedMax;
            if (alreadyDecremented is not null)
            {
                // Одна сверка на товар, даже если он в чеке несколькими строками.
                if (!verified.Add(productId))
                    continue;
                expectedMax = alreadyDecremented.TryGetValue(productId, out var known) ? known : null;
            }
            else
            {
                var soldQty = CartDisplayHelper.LineQuantityInStockUnits(line, tile);
                // Captured before DecrementLocalStock (whose UI-side effect is posted, not
                // synchronous) so it reflects the real pre-sale quantity, not a racy read.
                expectedMax = FindExpectedPostSaleQuantity(productId, soldQty);
                DecrementLocalStock(productId, soldQty);
            }

            try
            {
                var detail = await PosApp.CatalogApi.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
                if (detail is not { } el)
                    continue;

                // 2026-09-16, живой баг ("штучные превращаются в весовые и наоборот") — раньше
                // здесь классификация товара (весовой/штучный) пересчитывалась заново из ответа
                // /products/detail и применялась к плитке (CatalogProductClassifier.ApplyWeightFlags,
                // теперь удалён как неиспользуемый). Эта функция
                // должна только обновлять ОСТАТОК после продажи — а классификация уже верно
                // выставлена полной синхронизацией каталога. Если detail-ответ по какой-то
                // причине не содержит те же поля unit/is_weight, что и каталог (на сайте эта
                // проблема не воспроизводилась — там классификация не пересчитывается на лету),
                // товар молча переклассифицировался. Берём mustWeigh с уже загруженной плитки
                // (tile), а не заново из detail — она не трогается этим путём.
                var mustWeigh = tile?.MustWeigh ?? CartDisplayHelper.ProductMustWeigh(el);
                var qty = ResolveStockQuantity(el, mustWeigh);

                // The server sometimes still returns the pre-sale (higher) quantity here —
                // stock decrement can be processed asynchronously with a short delay. Applying
                // that stale reading would silently undo the correct optimistic decrement above,
                // which is exactly what was happening: sales succeeded but the displayed stock
                // never dropped. Skip the overwrite when the server hasn't caught up yet.
                if (expectedMax is { } max && qty > max + 1e-6)
                {
                    PosLogger.Log(
                        $"RefreshSoldItemsStock: stale server qty for {productId} ({qty} > expected {max}) — keeping optimistic value",
                        "STOCK");
                    continue;
                }

                foreach (var vm in CatalogCacheService.Products)
                {
                    if (!string.Equals(vm.Id, productId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    UiDispatcherHolder.Post(() => ApplyQuantityCore(vm, qty, vm.MustWeigh));
                    CatalogCacheService.PersistProductStock(productId, qty, mustWeigh);
                    break;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"RefreshSoldItemsStock failed for {productId}: {ex}", "STOCK");
            }
        }

        // Финальный проход по vm.Quantity убран намеренно: остаток уже персистится
        // синхронно в DecrementLocalStock (списание) и по детали товара из API.
        // Перечитывание VM после факта могло затереть корректное значение старым,
        // т.к. плитки обновляются отложенным UiDispatcherHolder.Post.
    }

    private static double? FindExpectedPostSaleQuantity(string productId, double soldQty)
    {
        foreach (var vm in CatalogCacheService.Products)
        {
            if (string.Equals(vm.Id, productId, StringComparison.OrdinalIgnoreCase))
                return Math.Max(0, vm.Quantity - soldQty);
        }

        return null;
    }

    private static double? TryResolveFromStocksArray(JsonElement product, bool mustWeigh,
        IReadOnlyDictionary<string, JsonElement>? props = null)
    {
        foreach (var arrayName in new[] { "stocks", "stock_items", "warehouses", "balances" })
        {
            if (!JsonProps.TryGet(product, props, arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            var cashboxId = (PosApp.PosCashboxId ?? "").Trim();
            double? matched = null;
            double sum = 0;
            var any = false;

            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                var qty = ResolveStockRowQuantity(row, mustWeigh);
                if (qty is null)
                    continue;

                any = true;
                sum += qty.Value;

                if (cashboxId.Length > 0 && StockRowMatchesCashbox(row, cashboxId))
                    matched = qty.Value;
            }

            if (!any)
                continue;

            if (matched is not null)
                return matched.Value;

            // Нет привязки к кассе — суммируем остатки по всем складам/точкам.
            return sum;
        }

        return null;
    }

    private static bool StockRowMatchesCashbox(JsonElement row, string cashboxId)
    {
        foreach (var key in new[] { "cashbox_id", "cashbox", "pos_cashbox_id", "warehouse_id", "store_id" })
        {
            if (!row.TryGetProperty(key, out var idEl))
                continue;

            var id = idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString()?.Trim() ?? "",
                JsonValueKind.Number => idEl.GetRawText(),
                _ => "",
            };

            if (string.Equals(id, cashboxId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static double? ResolveStockRowQuantity(JsonElement row, bool mustWeigh)
    {
        if (mustWeigh && TryReadDouble(row, "stock_weight") is { } sw)
            return sw;

        foreach (var key in new[] { "stock_quantity", "quantity", "qty", "amount", "balance", "remains" })
        {
            if (TryReadDouble(row, key) is { } q)
                return q;
        }

        return null;
    }

    private static Dictionary<string, double> BuildQuantityMap(IEnumerable<JsonElement> items)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in items)
        {
            if (!el.TryGetProperty("id", out var idEl)
                && !el.TryGetProperty("product_id", out idEl)
                && !el.TryGetProperty("product", out idEl))
                continue;

            // product: { id: ... }
            if (idEl.ValueKind == JsonValueKind.Object && idEl.TryGetProperty("id", out var nestedId))
                idEl = nestedId;

            var id = idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.GetRawText(),
                JsonValueKind.String => idEl.GetString() ?? "",
                _ => "",
            };
            if (string.IsNullOrEmpty(id))
                continue;

            var mustWeigh = CartDisplayHelper.ProductMustWeigh(el);
            map[id] = ResolveStockQuantity(el, mustWeigh);
        }

        return map;
    }

    private static double? TryReadDouble(JsonElement obj, string prop, IReadOnlyDictionary<string, JsonElement>? props = null)
    {
        if (!JsonProps.TryGet(obj, props, prop, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(
                v.GetString()?.Replace(',', '.'),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var x)
                ? x
                : null,
            _ => null,
        };
    }
}
