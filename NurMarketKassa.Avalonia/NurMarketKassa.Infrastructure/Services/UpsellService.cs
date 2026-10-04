using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Подсказка допродажи: какой товар предложить к текущему чеку.</summary>
public sealed record UpsellSuggestion(
    CatalogProductTileVm Product,
    double Price,
    string TriggerProductId,
    string TriggerTitle,
    int Together,
    double Score);

/// <summary>2026-10-01, владелец: «начни реализацию доп. продажи» (разбор ChatGPT «Умная допродажа»).
///
/// По истории чеков этого аккаунта (SoldLineItems, последние 90 дней) касса находит товары, которые
/// часто покупают вместе с товарами текущего чека, и предлагает ОДИН — «С этим часто берут».
/// Оценка: совместные покупки 40 % + маржа 30 % + популярность 15 % + приоритет 15 % (пока приоритет —
/// звёздочка «избранное» в каталоге; серверное поле upsell_priority — ТЗ часть 7, раздел 2.1).
/// Только товары в наличии и с ценой, которых ещё нет в чеку и которые кассир не пропустил в этом чеке.
///
/// Масштаб (владелец: расчёт на 15 000 клиентов): всё считается на самой кассе, сервер не нагружается
/// и без интернета подсказки работают. Индекс пар строится в фоне не чаще раза в 10 минут
/// (и после новой продажи — по PosDataEvents), подбор подсказки — словари в памяти, без SQL
/// на каждое добавление товара.</summary>
public static class UpsellService
{
    private const int HistoryDays = 90;
    /// <summary>Пара должна встретиться хотя бы в двух чеках — один совместный чек может быть случайностью.</summary>
    private const int MinTogether = 2;
    /// <summary>Чеки с очень большим числом позиций (оптовые закупки) дают квадратичное число пар и шум.</summary>
    private const int MaxItemsPerSale = 40;
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromMinutes(10);

    private sealed class Index
    {
        public Dictionary<string, Dictionary<string, int>> Pairs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SalesWith { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaxSalesWith { get; set; }
    }

    private static readonly object Sync = new();
    private static Index? _index;
    private static DateTime _builtAt = DateTime.MinValue;
    private static bool _dirty = true;
    private static Task? _building;
    private static bool _subscribed;

    /// <summary>Подсказки включены в «Настройки → Экран» (по умолчанию выключены — новая функция не
    /// появляется у кассира сама после обновления).</summary>
    public static bool Enabled => UserPreferences.Instance.UpsellEnabled;

    /// <summary>Подобрать подсказку к чеку. null — подсказки нет (мало истории, всё уже в чеке и т.п.).
    /// Вызывать не в потоке интерфейса: при первом обращении строится индекс.</summary>
    public static UpsellSuggestion? Suggest(IReadOnlyCollection<(string ProductId, string Title)> cart, ISet<string> skipped)
    {
        if (cart.Count == 0)
            return null;
        var index = GetIndex();
        if (index is null || index.Pairs.Count == 0)
            return null;

        var inCart = new HashSet<string>(cart.Select(c => c.ProductId), StringComparer.OrdinalIgnoreCase);

        // Кандидаты: лучшая «совместность» (confidence = вместе / чеков с товаром чека) по любому товару чека.
        var best = new Dictionary<string, (double Confidence, int Together, string TriggerId, string TriggerTitle)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, title) in cart)
        {
            if (!index.Pairs.TryGetValue(productId, out var neighbours) || !index.SalesWith.TryGetValue(productId, out var baseCount) || baseCount == 0)
                continue;
            foreach (var (other, together) in neighbours)
            {
                if (together < MinTogether || inCart.Contains(other) || skipped.Contains(other))
                    continue;
                var confidence = Math.Min(1.0, (double)together / baseCount);
                if (!best.TryGetValue(other, out var current) || confidence > current.Confidence)
                    best[other] = (confidence, together, productId, title);
            }
        }

        if (best.Count == 0)
            return null;

        UpsellSuggestion? winner = null;
        // Карточки читаем только для верхних кандидатов по совместности — не для всего каталога.
        foreach (var (productId, c) in best.OrderByDescending(kv => kv.Value.Confidence).ThenByDescending(kv => kv.Value.Together).Take(25))
        {
            var tile = LocalProductRepository.Instance.TryGetTileById(productId);
            if (tile is null || tile.IsUnitInvalid)
                continue;
            var price = LocalCartService.ParsePrice(tile.PriceLine);
            if (price <= 0)
                continue;
            if (!tile.IsService && tile.Quantity <= 0)
                continue;   // нет на складе — не предлагаем

            var margin = tile.PurchasePrice > 0 && price > tile.PurchasePrice ? Math.Min(1.0, (price - tile.PurchasePrice) / price) : 0;
            var popularity = index.MaxSalesWith > 0 && index.SalesWith.TryGetValue(productId, out var sw) ? (double)sw / index.MaxSalesWith : 0;
            var priority = tile.IsFavorite ? 1.0 : 0.0;
            var score = 0.40 * c.Confidence + 0.30 * margin + 0.15 * popularity + 0.15 * priority;
            if (winner is null || score > winner.Score)
                winner = new UpsellSuggestion(tile, price, c.TriggerId, c.TriggerTitle, c.Together, score);
        }

        return winner;
    }

    /// <summary>Записать показ / ответ кассира (shown, accepted, skipped).</summary>
    public static void Record(string evt, UpsellSuggestion s, string? cartKey)
    {
        try
        {
            DatabaseService.Instance.AppendUpsellEvent(evt, s.Product.Id, s.Product.Title, s.Price, s.TriggerProductId, Math.Round(s.Score, 4), cartKey);
            // 2026-10-05, ТЗ часть 7: общий журнал всех касс компании на сервере — отправка раз в 5 минут.
            UpsellServerSync.EnsureStarted();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Допродажа: событие не записано ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>После оплаты: события чека получают номер продажи (для выручки допродажи).</summary>
    public static void LinkSale(string? cartKey, string? saleId)
    {
        if (string.IsNullOrWhiteSpace(cartKey) || string.IsNullOrWhiteSpace(saleId))
            return;
        DatabaseService.Instance.LinkUpsellEventsToSale(cartKey, saleId);
        // 2026-10-05, ТЗ часть 7: и на сервере — события чека досылаются, затем link-sale.
        UpsellServerSync.LinkSaleLater(cartKey, saleId);
    }

    /// <summary>Итоги за период (для программы владельца).</summary>
    public static (int Shown, int Accepted, int Skipped, double Revenue) Stats(DateTime fromUtc, DateTime toUtc) =>
        DatabaseService.Instance.GetUpsellStats(fromUtc, toUtc);

    private static Index? GetIndex()
    {
        lock (Sync)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                PosDataEvents.SalesChanged += () => { lock (Sync) _dirty = true; };
            }

            var stale = _index is null || (_dirty && DateTime.UtcNow - _builtAt > RebuildEvery);
            if (!stale)
                return _index;
            if (_index is not null)
            {
                // Есть старый индекс — отдаём его, новый строим в фоне.
                _building ??= Task.Run(Build);
                return _index;
            }
        }

        Build();
        lock (Sync)
            return _index;
    }

    private static void Build()
    {
        try
        {
            var rows = DatabaseService.Instance.LoadSaleBaskets(DateTime.UtcNow.AddDays(-HistoryDays));
            var index = new Index();
            foreach (var sale in rows.GroupBy(r => r.SaleId, StringComparer.OrdinalIgnoreCase))
            {
                var items = sale.Select(r => r.ProductId).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var id in items)
                    index.SalesWith[id] = index.SalesWith.TryGetValue(id, out var n) ? n + 1 : 1;
                if (items.Count < 2 || items.Count > MaxItemsPerSale)
                    continue;
                foreach (var a in items)
                {
                    if (!index.Pairs.TryGetValue(a, out var row))
                        index.Pairs[a] = row = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in items)
                    {
                        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                            row[b] = row.TryGetValue(b, out var n) ? n + 1 : 1;
                    }
                }
            }

            index.MaxSalesWith = index.SalesWith.Count == 0 ? 0 : index.SalesWith.Values.Max();
            lock (Sync)
            {
                _index = index;
                _builtAt = DateTime.UtcNow;
                _dirty = false;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Допродажа: индекс пар не построен ({ex.Message}).", "WARNING");
        }
        finally
        {
            lock (Sync)
                _building = null;
        }
    }
}
