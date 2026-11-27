using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-27 (конструктор акций Ф1): набор действующих акций для кассы — GET api/main/pos/promotions/active/?branch= с ETag/If-None-Match (304 — ничего не изменилось),
/// кэш на диске в папке данных компании (<c>promotions-v2.json</c>, переносится CompanyStoreRegistry), касса считает акции и без сети.
/// Обновление — в фоне при старте (признаки сервера получены) и не чаще раза в <see cref="RefreshEvery"/> при изменении чека; без признака promotions_v2 запросов нет.
/// Сервер отдаёт scope категорий и брендов по UUID, а товар каталога — названием категории и бренда, поэтому вместе с акциями (при изменении набора
/// или раз в 6 ч) читаются справочники api/main/categories/ (с parent — «категория включает подкатегории») и api/main/brands/.
/// </summary>
public static class PromoV2Store
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefsEvery = TimeSpan.FromHours(6);
    private static readonly object Gate = new();
    private static State? _state;
    private static string? _loadedFor;
    private static DateTime _lastTry = DateTime.MinValue;
    private static int _running;

    /// <summary>Только для проверок: свой файл и свой набор вместо диска/сервера.</summary>
    public static string? FileOverride { get; set; }

    public sealed class State
    {
        public string? ETag { get; set; }
        public string? Version { get; set; }
        public string? Branch { get; set; }
        public DateTime SavedAt { get; set; }
        public DateTime RefsAt { get; set; }
        public List<PromoV2Engine.Rule> Rules { get; set; } = new();
        public JsonArray RawPromotions { get; set; } = new();
        public Dictionary<string, (string Name, string? Parent)> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Brands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "promotions-v2.json");

    /// <summary>Текущий набор (с диска при первом обращении); пусто — акций нет или ещё не загружены.</summary>
    public static State Current
    {
        get
        {
            lock (Gate)
            {
                var key = FilePath + "|" + ServerCapabilities.AccountKey();
                if (_state is null || _loadedFor != key)
                {
                    _loadedFor = key;
                    _state = LoadFromDisk() ?? new State();
                }
                return _state;
            }
        }
    }

    /// <summary>Только для проверок: подставить набор акций и справочники.</summary>
    public static void SetForTests(State? state)
    {
        lock (Gate)
        {
            _state = state;
            _loadedFor = state is null ? null : FilePath + "|" + ServerCapabilities.AccountKey();
            _lastTry = state is null ? DateTime.MinValue : DateTime.UtcNow;
        }
    }

    /// <summary>Применить ответ pos/promotions/active/ (200). Возвращает true — набор изменился.</summary>
    public static bool ApplyResponse(JsonElement body, string? etag, string? branch)
    {
        var raw = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("promotions", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? (JsonNode.Parse(arr.GetRawText()) as JsonArray ?? new JsonArray())
            : new JsonArray();
        var version = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        lock (Gate)
        {
            var cur = Current;
            var changed = !string.Equals(cur.RawPromotions.ToJsonString(), raw.ToJsonString(), StringComparison.Ordinal);
            cur.RawPromotions = raw;
            cur.Rules = ParseRules(raw);
            cur.ETag = etag;
            cur.Version = version;
            cur.Branch = branch;
            cur.SavedAt = DateTime.UtcNow;
            if (changed)
                cur.RefsAt = DateTime.MinValue; // новый набор — справочники перечитать (могли появиться новые категории)
            return changed;
        }
    }

    public static void ApplyRefs(IEnumerable<(string Id, string Name, string? Parent)> categories, IEnumerable<(string Id, string Name)> brands)
    {
        lock (Gate)
        {
            var cur = Current;
            cur.Categories = categories.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (g.First().Name, g.First().Parent), StringComparer.OrdinalIgnoreCase);
            cur.Brands = brands.GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            cur.RefsAt = DateTime.UtcNow;
        }
    }

    private static List<PromoV2Engine.Rule> ParseRules(JsonArray raw)
    {
        var list = new List<PromoV2Engine.Rule>();
        using var doc = JsonDocument.Parse(raw.ToJsonString());
        foreach (var p in doc.RootElement.EnumerateArray())
            if (PromoV2Engine.Rule.Parse(p) is { } r)
                list.Add(r);
        return list;
    }

    /// <summary>В фоне обновить набор, если признак promotions_v2 есть и прошло <see cref="RefreshEvery"/> (или <paramref name="force"/>). Без сети — ничего (работает кэш).</summary>
    public static void RefreshInBackground(bool force = false)
    {
        if (!ServerCapabilities.Has(ServerCapabilities.PromotionsV2))
            return;
        if (!force && DateTime.UtcNow - _lastTry < RefreshEvery)
            return;
        if (ServerLoyalty.ApiProvider?.Invoke() is not { } api || string.IsNullOrEmpty(api.AccessToken))
            return;
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        _lastTry = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(api).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Акции v2: набор не обновлён ({ex.Message}) — касса считает по сохранённому.", "WARNING");
            }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    /// <summary>Событие «набор акций изменился» (из фонового потока) — касса пересчитывает открытый чек.</summary>
    public static event Action? Changed;

    public static async Task RefreshAsync(NurMarketApiClient api, CancellationToken ct = default)
    {
        var branch = DeviceIdentity.BranchProvider();
        var cur = Current;
        // Другой филиал — другой набор: условный запрос только для того же филиала.
        var etag = string.Equals(cur.Branch ?? "", branch ?? "", StringComparison.OrdinalIgnoreCase) ? cur.ETag : null;
        var (body, status, newTag) = await api.GetConditionalAsync("api/main/pos/promotions/active/", null, etag, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var changed = false;
        if (status == 304)
        {
            lock (Gate)
                cur.SavedAt = DateTime.UtcNow;
        }
        else
        {
            changed = ApplyResponse(body, newTag, branch);
            PosLogger.Log($"Акции v2: набор с сервера — {Current.Rules.Count} шт. (версия {Current.Version ?? "—"}){(changed ? ", изменился" : "")}.", "INFO");
        }

        var needRefs = Current.Rules.Any(r => r.Categories.Count > 0 || r.Brands.Count > 0) && DateTime.UtcNow - Current.RefsAt > RefsEvery;
        if (needRefs)
        {
            var cats = await ReadRefsAsync(api, "api/main/categories/", ct).ConfigureAwait(false);
            var brands = await ReadRefsAsync(api, "api/main/brands/", ct).ConfigureAwait(false);
            ApplyRefs(cats, brands.Select(b => (b.Id, b.Name)));
            changed = true;
        }

        Save();
        if (changed)
            try { Changed?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Акции v2: обработчик изменения: {ex.Message}", "WARNING"); }
    }

    private static async Task<List<(string Id, string Name, string? Parent)>> ReadRefsAsync(NurMarketApiClient api, string path, CancellationToken ct)
    {
        var list = new List<(string, string, string?)>();
        for (var page = 1; page <= 30; page++)
        {
            var d = await api.RequestAsync(HttpMethod.Get, path, null, new Dictionary<string, string> { ["page"] = page.ToString(), ["page_size"] = "300" }, ct).ConfigureAwait(false);
            var results = d.ValueKind == JsonValueKind.Array ? d
                : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
            if (results.ValueKind != JsonValueKind.Array)
                break;
            foreach (var x in results.EnumerateArray())
            {
                var id = x.TryGetProperty("id", out var i) ? (i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText()) : null;
                var name = x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var parent = x.TryGetProperty("parent", out var p) ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Number ? p.GetRawText() : null) : null;
                if (!string.IsNullOrWhiteSpace(id) && name is not null)
                    list.Add((id!, name, parent));
            }
            if (!(d.ValueKind == JsonValueKind.Object && d.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                break;
        }
        return list;
    }

    private static void Save()
    {
        try
        {
            State cur;
            lock (Gate)
                cur = Current;
            var o = new JsonObject
            {
                ["account"] = ServerCapabilities.AccountKey(),
                ["etag"] = cur.ETag,
                ["version"] = cur.Version,
                ["branch"] = cur.Branch,
                ["saved_at"] = cur.SavedAt.ToString("O"),
                ["refs_at"] = cur.RefsAt.ToString("O"),
                ["promotions"] = cur.RawPromotions.DeepClone(),
                ["categories"] = new JsonArray(cur.Categories.Select(kv => (JsonNode)new JsonObject { ["id"] = kv.Key, ["name"] = kv.Value.Name, ["parent"] = kv.Value.Parent }).ToArray()),
                ["brands"] = new JsonArray(cur.Brands.Select(kv => (JsonNode)new JsonObject { ["id"] = kv.Key, ["name"] = kv.Value }).ToArray()),
            };
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, o.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Акции v2: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    private static State? LoadFromDisk()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path) || AtomicFile.ReadAllText(path) is not { Length: > 0 } text || JsonNode.Parse(text) is not JsonObject o)
                return null;
            // Файл другого аккаунта (компания переключена без переноса папки) — не используем.
            if (o["account"]?.GetValue<string>() is { Length: > 0 } acc && acc != ServerCapabilities.AccountKey() && ServerCapabilities.AccountKey().Trim('|').Length > 0)
                return null;
            var raw = o["promotions"] as JsonArray ?? new JsonArray();
            var st = new State
            {
                ETag = o["etag"]?.GetValue<string>(),
                Version = o["version"]?.GetValue<string>(),
                Branch = o["branch"]?.GetValue<string>(),
                SavedAt = DateTime.TryParse(o["saved_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var s) ? s : DateTime.MinValue,
                RefsAt = DateTime.TryParse(o["refs_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var r) ? r : DateTime.MinValue,
                RawPromotions = (JsonArray)raw.DeepClone(),
            };
            st.Rules = ParseRules(st.RawPromotions);
            if (o["categories"] is JsonArray cats)
                foreach (var c in cats.OfType<JsonObject>())
                    if (c["id"]?.ToString() is { Length: > 0 } id)
                        st.Categories[id] = (c["name"]?.ToString() ?? "", c["parent"]?.ToString());
            if (o["brands"] is JsonArray brands)
                foreach (var b in brands.OfType<JsonObject>())
                    if (b["id"]?.ToString() is { Length: > 0 } id)
                        st.Brands[id] = b["name"]?.ToString() ?? "";
            return st;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            PosLogger.Log($"Акции v2: файл не прочитан ({ex.Message}) — ждём набор с сервера.", "WARNING");
            return null;
        }
    }
}

/// <summary>2026-11-27 (акции v2): категория, бренд и закупочная цена товара из ответа каталога — для scope акций (категории/бренды) и защиты И-1 (не ниже закупки).
/// Колонок под это в pos_local.db нет (схему не меняем) — файл <c>promo-products.json</c> в папке данных компании, как <see cref="PartMetaStore"/>.</summary>
public static class PromoProductInfo
{
    public sealed record Info(string? Category, string? Brand, decimal Purchase);

    private static readonly ConcurrentDictionary<string, Info> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static volatile bool _loaded;
    private static volatile bool _dirty;

    public static string? FileOverride { get; set; }
    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "promo-products.json");

    public static void Remember(string? productId, JsonElement p)
    {
        if (string.IsNullOrWhiteSpace(productId) || p.ValueKind != JsonValueKind.Object)
            return;
        if (!p.TryGetProperty("category", out _) && !p.TryGetProperty("brand", out _) && !p.TryGetProperty("purchase_price", out _))
            return;
        EnsureLoaded();
        var info = new Info(Name(p, "category", "category_name"), Name(p, "brand", "brand_name"),
            p.TryGetProperty("purchase_price", out var pp) && JsonNumericReader.TryToDouble(pp, out var d) && double.IsFinite(d) && d > 0 ? (decimal)d : 0m);
        var id = productId.Trim();
        if (Known.TryGetValue(id, out var old) && old == info)
            return;
        Known[id] = info;
        _dirty = true;
    }

    public static void Set(string productId, Info info)
    {
        EnsureLoaded();
        Known[productId.Trim()] = info;
    }

    public static Info? Get(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        EnsureLoaded();
        return Known.TryGetValue(productId.Trim(), out var i) ? i : null;
    }

    public static void ResetForTests()
    {
        Known.Clear();
        _loaded = true;
        _dirty = false;
    }

    private static string? Name(JsonElement p, string key, string altKey)
    {
        foreach (var k in new[] { altKey, key })
            if (p.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!.Trim();
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    return n.GetString()?.Trim();
            }
        return null;
    }

    public static void SaveIfDirty()
    {
        if (!_dirty)
            return;
        try
        {
            lock (FileGate)
            {
                _dirty = false;
                var snapshot = Known.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Category ?? "", kv.Value.Brand ?? "", kv.Value.Purchase.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Акции v2: данные товаров не сохранены: {ex.Message}", "WARNING");
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (FileGate)
        {
            if (_loaded)
                return;
            try
            {
                var path = FilePath;
                if (File.Exists(path) && AtomicFile.ReadAllText(path) is { Length: > 0 } text
                    && JsonSerializer.Deserialize<Dictionary<string, string[]>>(text) is { } stored)
                    foreach (var (id, v) in stored)
                        if (v is { Length: >= 3 })
                            Known.TryAdd(id, new Info(v[0].Length > 0 ? v[0] : null, v[1].Length > 0 ? v[1] : null,
                                decimal.TryParse(v[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pp) ? pp : 0m));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                PosLogger.Log($"Акции v2: файл данных товаров не прочитан ({ex.Message}).", "WARNING");
            }
            _loaded = true;
        }
    }
}
