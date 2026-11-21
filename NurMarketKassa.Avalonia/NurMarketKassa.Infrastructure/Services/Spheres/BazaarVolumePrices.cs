using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Ступень цены по количеству: «от <see cref="MinQty"/> штук — цена <see cref="Price"/> за единицу».</summary>
public sealed record VolumeTier(decimal MinQty, decimal Price);

/// <summary>Почему набор ступеней не принят редактором.</summary>
public enum VolumeTiersError
{
    None,
    /// <summary>Количество «от» меньше или равно нулю.</summary>
    BadQuantity,
    /// <summary>Цена ступени меньше или равна нулю.</summary>
    BadPrice,
    /// <summary>Два одинаковых «от».</summary>
    DuplicateQuantity,
    /// <summary>Цена ступени не ниже цены розницы (ступень не имеет смысла) — только при известной цене розницы.</summary>
    NotCheaperThanRetail,
    /// <summary>Чем больше количество, тем цена должна быть ниже (или равна).</summary>
    NotDescending,
    /// <summary>Больше <see cref="BazaarVolumePrices.MaxTiers"/> ступеней.</summary>
    TooMany,
}

/// <summary>
/// 2026-11-21 (сфера «Базар», модуль <c>volume_price</c>, ТЗ Контейнер-базар 2.4 / 3.2): цена по количеству — ступени «от N шт — цена».
/// Источники: 1) сервер — <c>GET main/products/&lt;id&gt;/prices/</c> → <c>volume_prices:[{min_qty, price, level:null|код}]</c> (ответ Умара 13.11 вечер; берём ступени без уровня);
/// 2) местные ступени, которые владелец задал в карточке товара кассы — файл <c>volume-prices.json</c> в папке данных (схема pos_local.db не меняется). Запись ступеней на сервер
/// в ответах Умара не описана (адрес и поля не придуманы) — вопрос Умару в отчёте; пока местные ступени живут на этой кассе. Если сервер прислал ступени товара — они главные.
/// Чистая логика (выбор ступени, проверка набора, подпись) — на decimal, проверяется тестами.
/// </summary>
public static class BazaarVolumePrices
{
    public const int MaxTiers = 8;

    private static readonly object Gate = new();
    private static Dictionary<string, List<VolumeTier>>? _local;
    private static readonly Dictionary<string, (DateTime At, List<VolumeTier> Tiers)> Server = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ServerTtl = TimeSpan.FromMinutes(10);

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    /// <summary>Подмена для тестов: клиент API вместо <see cref="ServerLoyalty.ApiProvider"/>.</summary>
    public static Func<NurMarketApiClient?>? ApiOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "volume-prices.json");

    // ───────────── чистая логика ─────────────

    /// <summary>Ступень для количества: с наибольшим «от», не превышающим количество. Нет ступени — null.</summary>
    public static VolumeTier? Pick(IReadOnlyList<VolumeTier>? tiers, decimal qty)
    {
        if (tiers is null || tiers.Count == 0 || qty <= 0m)
            return null;
        VolumeTier? best = null;
        foreach (var t in tiers)
            if (t.MinQty <= qty && t.Price > 0m && (best is null || t.MinQty > best.MinQty))
                best = t;
        return best;
    }

    /// <summary>Подпись под строкой чека: «Опт от 10 — 450».</summary>
    public static string Caption(VolumeTier tier) =>
        $"{Tr.T("Опт от", "Дүң, мин.", "Bulk from", "Toptan, en az", "Ulgurji, kamida")} {tier.MinQty.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"))}"
        + $" — {MoneyText.Amount((double)tier.Price)}";

    /// <summary>Проверка набора ступеней редактором (до записи). <paramref name="retailPrice"/> — цена розницы товара, если известна: ступень не ниже неё не принимается.</summary>
    public static VolumeTiersError Validate(IReadOnlyList<VolumeTier> tiers, decimal? retailPrice = null)
    {
        if (tiers.Count > MaxTiers)
            return VolumeTiersError.TooMany;
        foreach (var t in tiers)
        {
            if (t.MinQty <= 0m)
                return VolumeTiersError.BadQuantity;
            if (t.Price <= 0m)
                return VolumeTiersError.BadPrice;
            if (retailPrice is > 0m && t.Price >= retailPrice.Value)
                return VolumeTiersError.NotCheaperThanRetail;
        }
        if (tiers.Select(t => t.MinQty).Distinct().Count() != tiers.Count)
            return VolumeTiersError.DuplicateQuantity;
        var ordered = tiers.OrderBy(t => t.MinQty).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Price > ordered[i - 1].Price)
                return VolumeTiersError.NotDescending;
        return VolumeTiersError.None;
    }

    public static string ErrorText(VolumeTiersError e) => e switch
    {
        VolumeTiersError.BadQuantity => Tr.T("Количество «от» должно быть больше нуля.", "«Дан» саны нөлдөн чоң болушу керек.", "The “from” quantity must be above zero.", "«En az» miktarı sıfırdan büyük olmalı.", "«Dan» miqdori noldan katta bo'lishi kerak."),
        VolumeTiersError.BadPrice => Tr.T("Цена ступени должна быть больше нуля.", "Баскычтын баасы нөлдөн чоң болушу керек.", "A tier price must be above zero.", "Kademe fiyatı sıfırdan büyük olmalı.", "Bosqich narxi noldan katta bo'lishi kerak."),
        VolumeTiersError.DuplicateQuantity => Tr.T("Два одинаковых «от» — оставьте одну ступень.", "Эки бирдей «дан» — бир баскыч калтырыңыз.", "Two identical “from” values — keep one tier.", "İki aynı «en az» değeri — bir kademe bırakın.", "Ikkita bir xil «dan» — bitta bosqich qoldiring."),
        VolumeTiersError.NotCheaperThanRetail => Tr.T("Цена ступени должна быть ниже розничной цены.", "Баскычтын баасы чекене баадан төмөн болушу керек.", "A tier price must be below the retail price.", "Kademe fiyatı perakende fiyattan düşük olmalı.", "Bosqich narxi chakana narxdan past bo'lishi kerak."),
        VolumeTiersError.NotDescending => Tr.T("Чем больше количество, тем цена ниже: проверьте порядок.", "Саны көп болгон сайын баа төмөн: тартибин текшериңиз.", "The more units, the lower the price: check the order.", "Miktar arttıkça fiyat düşmeli: sırayı kontrol edin.", "Miqdor ko'paygan sari narx past: tartibni tekshiring."),
        VolumeTiersError.TooMany => Tr.T($"Не больше {MaxTiers} ступеней.", $"{MaxTiers} баскычтан ашык болбосун.", $"No more than {MaxTiers} tiers.", $"En fazla {MaxTiers} kademe.", $"{MaxTiers} tadan ko'p bo'lmasin."),
        _ => "",
    };

    /// <summary>Разбор ступеней из ответа <c>products/&lt;id&gt;/prices/</c>: только <c>volume_prices</c> без уровня (<c>level</c> = null). Терпимый к форме: <c>min_qty</c> / <c>min_quantity</c>, цена числом или строкой.</summary>
    public static List<VolumeTier> ParseServer(JsonElement data)
    {
        var list = new List<VolumeTier>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("volume_prices", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var r in arr.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
                continue;
            if (r.TryGetProperty("level", out var level) && level.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(level.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(level.GetString())))
                continue; // ступень для уровня цен (опт клиента) — не «розничная по количеству»
            var min = Dec(r, "min_qty") ?? Dec(r, "min_quantity");
            var price = Dec(r, "price");
            if (min is > 0m && price is > 0m)
                list.Add(new VolumeTier(min.Value, price.Value));
        }
        return list.OrderBy(t => t.MinQty).ToList();
    }

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    // ───────────── хранение ─────────────

    private static Dictionary<string, List<VolumeTier>> LoadLocalNoLock()
    {
        if (_local is not null)
            return _local;
        var dict = new Dictionary<string, List<VolumeTier>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && AtomicFile.ReadAllText(FilePath) is { Length: > 0 } text && JsonNode.Parse(text) is JsonObject root)
                foreach (var (productId, node) in root)
                    if (node is JsonArray arr)
                    {
                        var tiers = new List<VolumeTier>();
                        foreach (var row in arr)
                            if (row is JsonArray pair && pair.Count == 2 && pair[0] is JsonValue a && pair[1] is JsonValue b
                                && a.TryGetValue<decimal>(out var min) && b.TryGetValue<decimal>(out var price) && min > 0m && price > 0m)
                                tiers.Add(new VolumeTier(min, price));
                        if (tiers.Count > 0)
                            dict[productId] = tiers.OrderBy(t => t.MinQty).ToList();
                    }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            PosLogger.Log($"Цена по количеству: файл не прочитан ({ex.Message}).", "WARNING");
        }
        return _local = dict;
    }

    /// <summary>Местные ступени товара (по возрастанию «от»).</summary>
    public static IReadOnlyList<VolumeTier> LocalTiers(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return Array.Empty<VolumeTier>();
        lock (Gate)
            return LoadLocalNoLock().TryGetValue(productId.Trim(), out var t) ? t.ToList() : Array.Empty<VolumeTier>();
    }

    /// <summary>Записать местные ступени товара (пустой список — убрать). false — набор не принят или не записался на диск.</summary>
    public static bool SetLocalTiers(string productId, IReadOnlyList<VolumeTier> tiers, decimal? retailPrice = null)
    {
        if (string.IsNullOrWhiteSpace(productId) || Validate(tiers, retailPrice) != VolumeTiersError.None)
            return false;
        try
        {
            lock (Gate)
            {
                var all = LoadLocalNoLock();
                if (tiers.Count == 0)
                    all.Remove(productId.Trim());
                else
                    all[productId.Trim()] = tiers.OrderBy(t => t.MinQty).ToList();
                var root = new JsonObject();
                foreach (var (id, list) in all)
                {
                    var arr = new JsonArray();
                    foreach (var t in list)
                        arr.Add(new JsonArray(JsonValue.Create(t.MinQty), JsonValue.Create(t.Price)));
                    root[id] = arr;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, root.ToJsonString());
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Цена по количеству: не удалось сохранить: {ex.Message}", "WARNING");
            return false;
        }
    }

    /// <summary>Ступени с сервера, полученные раньше (в памяти кассы, не старше 10 минут); null — не загружались.</summary>
    public static IReadOnlyList<VolumeTier>? ServerTiers(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        lock (Gate)
            return Server.TryGetValue(productId.Trim(), out var s) && DateTime.UtcNow - s.At < ServerTtl ? s.Tiers : null;
    }

    /// <summary>Запомнить ступени с сервера (в том числе пустой список — «у товара ступеней нет»).</summary>
    public static void RememberServer(string productId, IEnumerable<VolumeTier> tiers)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        lock (Gate)
            Server[productId.Trim()] = (DateTime.UtcNow, tiers.OrderBy(t => t.MinQty).ToList());
    }

    /// <summary>Действующие ступени товара: сервер главный, если прислал ступени; иначе местные.</summary>
    public static IReadOnlyList<VolumeTier> Effective(string? productId)
    {
        if (ServerTiers(productId) is { Count: > 0 } s)
            return s;
        return LocalTiers(productId);
    }

    /// <summary>Только для проверок: забыть память (файлы не трогает).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            _local = null;
            Server.Clear();
            Loading.Clear();
        }
    }

    // ───────────── сервер ─────────────

    /// <summary>Товары, чьих серверных ступеней ещё нет в памяти (и не грузятся сейчас). Возвращённые считаются «грузятся», пока их не обработает <see cref="LoadServerAsync"/>.</summary>
    public static List<string> NeedServer(IEnumerable<string> productIds)
    {
        var result = new List<string>();
        lock (Gate)
            foreach (var id in productIds.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Loading.Contains(id) && !(Server.TryGetValue(id, out var s) && DateTime.UtcNow - s.At < ServerTtl))
                {
                    Loading.Add(id);
                    result.Add(id);
                }
        return result;
    }

    /// <summary>Загрузить ступени товаров с сервера (GET products/&lt;id&gt;/prices/) в память. Нет связи или сервер не ответил — тихо (местные ступени остаются). true — что-то изменилось.</summary>
    public static async Task<bool> LoadServerAsync(IReadOnlyList<string> productIds, CancellationToken ct = default)
    {
        var api = (ApiOverride ?? ServerLoyalty.ApiProvider)?.Invoke();
        var canAsk = api is not null && !string.IsNullOrEmpty(api.AccessToken);
        var changed = false;
        foreach (var id in productIds.Take(30))
        {
            try
            {
                if (!canAsk)
                {
                    RememberServer(id, Array.Empty<VolumeTier>()); // нет входа / связи — на десять минут не спрашиваем снова
                    continue;
                }
                var data = await api!.RequestAsync(HttpMethod.Get, $"api/main/products/{Uri.EscapeDataString(id)}/prices/", null, null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var tiers = ParseServer(data);
                var before = ServerTiers(id);
                RememberServer(id, tiers);
                if (before is null ? tiers.Count > 0 : !before.SequenceEqual(tiers))
                    changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // нет связи / сервер без этого адреса — ступени с сервера не берём; на десять минут не спрашиваем снова
                RememberServer(id, Array.Empty<VolumeTier>());
                PosLogger.Log($"Цена по количеству: ступени товара {id} с сервера не получены ({ex.Message}).", "WARNING");
            }
            finally
            {
                lock (Gate)
                    Loading.Remove(id);
            }
        }
        foreach (var id in productIds.Skip(30))
            lock (Gate)
                Loading.Remove(id);
        return changed;
    }
}
