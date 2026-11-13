using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Уровень цен компании (GET main/price-levels/).</summary>
public sealed record PriceLevelInfo(string Id, string Name);

/// <summary>Цена товара на одном уровне (GET main/products/{id}/prices/).</summary>
public sealed record ProductLevelPrice(string LevelId, string? LevelName, decimal Price, decimal? MinQty);

/// <summary>Объект клиента-прораба (GET/POST main/clients/{id}/objects/): стройка, адрес.</summary>
public sealed record ClientObjectInfo(string Id, string Name, string? Address);

/// <summary>
/// 2026-11-13 (ответ Умара 13.11, п. 4, «Строймаркет»): «цена прораба» — серверный уровень цен. У клиента поле <c>price_level</c>; уровни — <c>GET main/price-levels/</c>;
/// цены товара по уровням — <c>GET main/products/&lt;id&gt;/prices/</c>; расчёт цен для клиента — <c>POST main/price-levels/quote/ {client, items: [{product, qty}]}</c>;
/// объекты прораба — <c>GET/POST main/clients/&lt;id&gt;/objects/</c>. Разбор терпимый к форме ответа (массив или <c>results</c>, <c>unit_price</c> или <c>price</c>):
/// точные образцы ответов в ответе Умара не приведены. Деньги — decimal.
/// </summary>
public sealed class PriceLevelsApi
{
    private readonly NurMarketApiClient _api;

    public PriceLevelsApi(NurMarketApiClient api) => _api = api;

    public async Task<IReadOnlyList<PriceLevelInfo>> GetLevelsAsync(CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, "api/main/price-levels/", null, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return Rows(data, "results", "levels").Select(r => new PriceLevelInfo(Text(r, "id") ?? "", Text(r, "name") ?? Text(r, "title") ?? ""))
            .Where(l => l.Id.Length > 0).ToList();
    }

    public async Task<IReadOnlyList<ProductLevelPrice>> GetProductPricesAsync(string productId, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/products/{Uri.EscapeDataString(productId)}/prices/", null, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var list = new List<ProductLevelPrice>();
        foreach (var r in Rows(data, "results", "prices"))
        {
            var level = Text(r, "level") ?? Text(r, "price_level") ?? Text(r, "level_id");
            if (r.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.Object)
                level = Text(lvl, "id") ?? level;
            if (string.IsNullOrEmpty(level) || Money(r, "price") is not { } price)
                continue;
            list.Add(new ProductLevelPrice(level, Text(r, "level_name"), price, Money(r, "min_qty") ?? Money(r, "min_quantity")));
        }
        return list;
    }

    /// <summary>POST price-levels/quote/ — цены строк для клиента (по его <c>price_level</c>). Ключ результата — id товара; цена — за единицу при этом количестве.
    /// Ответ без разобранных цен — пустой словарь (вызывающий решает, что делать).</summary>
    public async Task<Dictionary<string, decimal>> QuoteAsync(string clientId, IEnumerable<(string ProductId, double Qty)> items, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["client"] = clientId,
            ["items"] = items.Select(i => new Dictionary<string, object?>
            {
                ["product"] = i.ProductId,
                ["qty"] = i.Qty.ToString("0.###", CultureInfo.InvariantCulture),
            }).ToList(),
        };
        var data = await _api.RequestAsync(HttpMethod.Post, "api/main/price-levels/quote/", body, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows(data, "items", "lines", "results"))
        {
            var product = Text(r, "product") ?? Text(r, "product_id") ?? Text(r, "id");
            if (r.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object)
                product = Text(p, "id") ?? product;
            var price = Money(r, "unit_price") ?? Money(r, "price");
            if (!string.IsNullOrEmpty(product) && price is > 0m)
                prices.TryAdd(product, price.Value);
        }
        return prices;
    }

    public async Task<IReadOnlyList<ClientObjectInfo>> GetObjectsAsync(string clientId, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(clientId)}/objects/", null, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return Rows(data, "results", "objects").Select(r => new ClientObjectInfo(Text(r, "id") ?? "", Text(r, "name") ?? Text(r, "title") ?? "", Text(r, "address")))
            .Where(o => o.Id.Length > 0).ToList();
    }

    public async Task<ClientObjectInfo?> CreateObjectAsync(string clientId, string name, string? address, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name.Trim() };
        if (!string.IsNullOrWhiteSpace(address))
            body["address"] = address.Trim();
        var data = await _api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Uri.EscapeDataString(clientId)}/objects/", body, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && Text(data, "id") is { Length: > 0 } id
            ? new ClientObjectInfo(id, Text(data, "name") ?? name.Trim(), Text(data, "address") ?? address)
            : null;
    }

    private static IEnumerable<JsonElement> Rows(JsonElement data, params string[] containers)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var name in containers)
                if (data.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        return Array.Empty<JsonElement>();
    }

    private static string? Text(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static decimal? Money(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }
}

/// <summary>Последние полученные цены уровней (уровень → товар → цена за единицу) — для работы без связи (price-levels-cache.json; схема pos_local.db не меняется).</summary>
public static class PriceLevelCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, Dictionary<string, decimal>>? _data;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "price-levels-cache.json");

    private static Dictionary<string, Dictionary<string, decimal>> LoadNoLock()
    {
        if (_data is not null)
            return _data;
        var dict = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && AtomicFile.ReadAllText(FilePath) is { Length: > 0 } text
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(text) is { } stored)
                foreach (var (level, prices) in stored)
                    dict[level] = new Dictionary<string, decimal>(prices, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PosLogger.Log($"Цены уровней: файл не прочитан ({ex.Message}) — ждём ответ сервера.", "WARNING");
        }
        return _data = dict;
    }

    public static void Remember(string levelId, IReadOnlyDictionary<string, decimal> pricesByProduct)
    {
        if (string.IsNullOrWhiteSpace(levelId) || pricesByProduct.Count == 0)
            return;
        try
        {
            lock (Gate)
            {
                var all = LoadNoLock();
                if (!all.TryGetValue(levelId.Trim(), out var level))
                    all[levelId.Trim()] = level = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var (product, price) in pricesByProduct)
                    if (price > 0m)
                        level[product] = price;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Цены уровней: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    public static decimal? TryGet(string? levelId, string? productId)
    {
        if (string.IsNullOrWhiteSpace(levelId) || string.IsNullOrWhiteSpace(productId))
            return null;
        lock (Gate)
            return LoadNoLock().TryGetValue(levelId.Trim(), out var level) && level.TryGetValue(productId.Trim(), out var p) ? p : null;
    }

    /// <summary>Только для проверок: забыть память (файл не трогает).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
            _data = null;
    }
}

/// <summary>Строка чека для расчёта цены по уровню.</summary>
public sealed record PriceLevelLine(string ItemId, string ProductId, double Qty);

/// <summary>Итог расчёта: цена за единицу по id строки; <c>FromCache</c> — цены из памяти кассы (нет связи или сервер не ответил); <c>Missing</c> — строки без цены уровня (останутся по обычной цене).</summary>
public sealed record PriceLevelResult(IReadOnlyDictionary<string, decimal> PriceByItem, bool FromCache, int Missing, string? Error);

/// <summary>
/// 2026-11-13: расчёт цен по уровню клиента. Онлайн — <c>POST price-levels/quote/</c> (цены запоминаются для офлайна); нет связи или ответ не разобран —
/// последние полученные цены уровня из памяти; цены нет совсем — строка остаётся по обычной цене (вызывающий показывает предупреждение). Цены уходят в чек
/// явной ценой строки (как оптовая), потому что сервер при продаже берёт цену строки, которую прислала касса.
/// </summary>
public static class PriceLevelPricing
{
    /// <summary>Подмена для тестов: клиент API вместо <see cref="ServerLoyalty.ApiProvider"/>.</summary>
    public static Func<NurMarketApiClient?>? ApiOverride { get; set; }

    /// <summary>Подмена для тестов: «связь есть».</summary>
    public static Func<bool>? OnlineOverride { get; set; }

    private static bool Online => OnlineOverride?.Invoke() ?? !(OfflineModeHelper.UseLocalOperations || OfflineModeHelper.IsServerOutage);

    public static async Task<PriceLevelResult> ResolveAsync(string clientId, string levelId, IReadOnlyList<PriceLevelLine> lines, CancellationToken ct = default)
    {
        var byItem = new Dictionary<string, decimal>(StringComparer.Ordinal);
        string? error = null;
        var fromCache = false;
        Dictionary<string, decimal>? quoted = null;

        var api = (ApiOverride ?? ServerLoyalty.ApiProvider)?.Invoke();
        if (Online && api is not null && !string.IsNullOrEmpty(api.AccessToken) && lines.Count > 0)
        {
            try
            {
                quoted = await new PriceLevelsApi(api).QuoteAsync(clientId, lines.Select(l => (l.ProductId, l.Qty)), ct).ConfigureAwait(false);
                if (quoted.Count == 0)
                    error = "quote: ответ без цен";
                else
                    PriceLevelCache.Remember(levelId, quoted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                PosLogger.Log($"Цены уровня {levelId}: quote не удался ({ex.Message}) — берём последние полученные.", "WARNING");
            }
        }
        else
        {
            error = "нет связи";
        }

        var missing = 0;
        foreach (var line in lines)
        {
            if (quoted is { Count: > 0 })
            {
                // Сервер ответил — его цены главные; товара нет в ответе — цена неизвестна (старую из памяти при живой связи не берём).
                if (quoted.TryGetValue(line.ProductId, out var price))
                    byItem[line.ItemId] = price;
                else
                    missing++;
            }
            else if (PriceLevelCache.TryGet(levelId, line.ProductId) is { } cached)
            {
                byItem[line.ItemId] = cached;
                fromCache = true;
            }
            else
            {
                missing++;
            }
        }
        return new PriceLevelResult(byItem, fromCache, missing, byItem.Count == 0 ? error : (fromCache ? error : null));
    }

    /// <summary>Уровень цен клиента по памяти кассы (поле <c>price_level</c> списка клиентов) или null.</summary>
    public static string? LevelOf(string? clientId) =>
        string.IsNullOrWhiteSpace(clientId) ? null : OfflineClientCache.Get(clientId)?.PriceLevel;
}
