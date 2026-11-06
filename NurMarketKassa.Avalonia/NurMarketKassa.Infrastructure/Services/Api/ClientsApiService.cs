using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Реализация клиентской базы поверх NurCRM (/api/main/clients/).</summary>
public sealed class ClientsApiService : IClientsApiService
{
    // 2026-10-31 (стресс-тест 1.17.68, В-2): раньше было MaxPages = 40 без page_size — на 5 000 клиентов список обрезался на 4 000.
    // Теперь страница 500 (как у списка поставщиков того же адреса), число страниц берётся из count / следования next до конца;
    // HardMaxPages — только защита от зацикленного next (500 × 1000 = полмиллиона клиентов).
    private const int HardMaxPages = 1000;
    private const int PageSize = 500;

    private readonly NurMarketApiClient _client;

    public ClientsApiService(NurMarketApiClient client) => _client = client;

    // 2026-11-06 (план «десктоп 10 из 10», В9, шаг 4 / Н-2): полный список клиентов качался из 10 мест без общего кэша (до 6000 клиентов при каждом
    // открытии окна оплаты). Теперь запрос полного списка без поиска и без индикатора идёт через общий кэш на минуту, а одинаковые запросы «в полёте»
    // сливаются в один. Сброс: свои POST/PATCH/DELETE на clients/, смена компании (CompanyInfoService.CompanyChanged), другой токен входа.
    // Запрос с индикатором «загружено N» (окно «Клиенты», кнопка «Обновить») всегда свежий, а его результат кладётся в кэш. Поиск (search) не кэшируется.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static long _invalidations;

    static ClientsApiService()
    {
        CompanyInfoService.CompanyChanged += _ => InvalidateCache();
    }

    /// <summary>Сбросить общий кэш списка клиентов (запись клиента, смена компании, «Обновить»). Загрузка, начатая до сброса, в кэш не попадёт.</summary>
    public static void InvalidateCache() => Interlocked.Increment(ref _invalidations);

    private readonly object _cacheSync = new();
    private List<JsonElement>? _cache;
    private DateTime _cacheAtUtc;
    private long _cacheGeneration;
    private string? _cacheKey;
    private Task<List<JsonElement>>? _inFlight;
    private long _inFlightGeneration;
    private string? _inFlightKey;

    public Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default) => GetClientsAsync(search, null, ct);

    /// <summary>2026-10-31: то же с индикатором «загружено N» — progress получает число клиентов, пришедших к этому моменту (с любого потока).</summary>
    public async Task<List<JsonElement>> GetClientsAsync(string? search, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(search) && progress is null)
            return await GetFullListCachedAsync(ct).ConfigureAwait(false);

        var generation = Interlocked.Read(ref _invalidations);
        var result = await FetchAndRememberAsync(search, progress, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(search))
            StoreCache(result, _client.AccessToken ?? "", generation);
        return result;
    }

    private async Task<List<JsonElement>> FetchAndRememberAsync(string? search, IProgress<int>? progress, CancellationToken ct)
    {
        var result = await GetClientsCoreAsync(search, progress, ct).ConfigureAwait(false);
        // 2026-10-29: список клиентов запоминается в кэше кассы — «в долг» и оплата долга без связи (OfflineClientCache).
        OfflineClientCache.RememberClients(result);
        return result;
    }

    private void StoreCache(List<JsonElement> list, string key, long generation)
    {
        lock (_cacheSync)
        {
            if (generation != Interlocked.Read(ref _invalidations))
                return; // за время загрузки список изменили или сменилась компания — это уже не свежее
            _cache = list;
            _cacheAtUtc = DateTime.UtcNow;
            _cacheGeneration = generation;
            _cacheKey = key;
        }
    }

    private async Task<List<JsonElement>> GetFullListCachedAsync(CancellationToken ct)
    {
        var key = _client.AccessToken ?? "";
        Task<List<JsonElement>> task;
        lock (_cacheSync)
        {
            var generation = Interlocked.Read(ref _invalidations);
            if (_cache is { } cached && _cacheGeneration == generation && _cacheKey == key && DateTime.UtcNow - _cacheAtUtc < CacheTtl)
                return new List<JsonElement>(cached);

            if (_inFlight is { } running && _inFlightGeneration == generation && _inFlightKey == key)
            {
                task = running;
            }
            else
            {
                // Общая загрузка не зависит от отмены первого вызывающего (его отмена только перестаёт ждать): её результат нужен и остальным.
                // Task.Run — чтобы загрузка не прошла синхронно внутри замка и не оставила в _inFlight уже завершённую задачу.
                Task<List<JsonElement>>? self = null;
                self = Task.Run(async () =>
                {
                    try
                    {
                        var list = await FetchAndRememberAsync(null, null, CancellationToken.None).ConfigureAwait(false);
                        StoreCache(list, key, generation);
                        return list;
                    }
                    finally
                    {
                        lock (_cacheSync)
                        {
                            if (ReferenceEquals(_inFlight, self))
                                _inFlight = null;
                        }
                    }
                });
                _inFlight = self;
                _inFlightGeneration = generation;
                _inFlightKey = key;
                task = self;
            }
        }

        var loaded = await task.WaitAsync(ct).ConfigureAwait(false);
        return new List<JsonElement>(loaded);
    }

    private async Task<List<JsonElement>> GetClientsCoreAsync(string? search, IProgress<int>? progress, CancellationToken ct)
    {
        var result = new List<JsonElement>();

        // 2026-10-23 (аудит, круг 6: «Клиенты» у владельца 3,1 с): страницы шли строго по очереди. Первая страница даёт
        // count и размер страницы — остальные берём параллельно (по 4), те же запросы, без новых. Нет count — как раньше.
        var (first, firstNext, count) = await GetClientsPageCoreAsync(1, search, ct).ConfigureAwait(false);
        if (first.Count == 0)
            return result;
        result.AddRange(first);
        progress?.Report(result.Count);
        if (!firstNext)
            return result;

        // 2026-10-31: id первого клиента каждой страницы — если страница повторилась (зациклившийся next), загрузка останавливается.
        var seenFirst = new HashSet<string> { FirstId(first) };
        var nextPage = 2;
        var lastHasNext = true;
        if (count > first.Count)
        {
            var pages = Math.Min(HardMaxPages, (int)Math.Ceiling(count / (double)first.Count));
            if (pages >= 3)
            {
                var gate = new SemaphoreSlim(4);
                var loaded = result.Count;
                var tasks = Enumerable.Range(2, pages - 1).Select(async n =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // 2026-10-31: при 429 страница повторяется после паузы сервера (ApiThrottle), а не теряется.
                        var page = await ApiThrottle.RunWithRetryAsync(() => GetClientsPageCoreAsync(n, search, ct), 4, null, ct).ConfigureAwait(false);
                        progress?.Report(Interlocked.Add(ref loaded, page.Items.Count));
                        return page;
                    }
                    catch (ApiException ex) when (ex.StatusCode == 404)
                    {
                        // 2026-10-31 (приёмка 1.17.68, С-5): клиентов за время загрузки стало меньше — хвостовая страница уже не существует (404).
                        // Это не сбой: оставляем уже загруженное, страницу пропускаем (в журнал).
                        PosLogger.Log($"Клиенты: страница {n} не найдена (404) — список стал короче за время загрузки, страница пропущена.", "WARNING");
                        return (Items: new List<JsonElement>(), HasNext: false, Count: 0);
                    }
                    finally { gate.Release(); }
                }).ToList();
                var all = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var page in all)
                    result.AddRange(page.Items);
                nextPage = pages + 1;
                lastHasNext = all.Length > 0 && all[^1].HasNext; // count устарел (клиенты добавились) — докачиваем по next
            }
        }

        for (var page = nextPage; lastHasNext && page <= HardMaxPages; page++)
        {
            var n = page;
            List<JsonElement> pageItems;
            bool hasNext;
            try
            {
                (pageItems, hasNext, _) = await ApiThrottle.RunWithRetryAsync(() => GetClientsPageCoreAsync(n, search, ct), 4, null, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                PosLogger.Log($"Клиенты: страница {n} не найдена (404) — конец списка, уже загруженное оставлено.", "WARNING"); // 2026-10-31 (С-5)
                break;
            }
            if (pageItems.Count == 0 || !seenFirst.Add(FirstId(pageItems)))
                break;

            result.AddRange(pageItems);
            progress?.Report(result.Count);
            lastHasNext = hasNext;
        }

        // 2026-10-31 (приёмка 1.17.68, С-5): при сдвиге списка во время загрузки клиент на стыке страниц приходит дважды — оставляем один (по id).
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<JsonElement>(result.Count);
        foreach (var item in result)
        {
            var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            if (id.Length == 0 || seenIds.Add(id))
                unique.Add(item);
        }
        if (unique.Count != result.Count)
            PosLogger.Log($"Клиенты: убрано повторов по id: {result.Count - unique.Count}.", "WARNING");
        return unique;
    }

    private static string FirstId(List<JsonElement> items) =>
        items.Count > 0 && items[0].ValueKind == JsonValueKind.Object && items[0].TryGetProperty("id", out var id) ? id.ToString() : "";

    /// <summary>2026-10-04: одна страница списка — тот же запрос, что был телом цикла
    /// <see cref="GetClientsAsync"/>. Окно оплаты долга показывает первую страницу сразу, остальные
    /// догружает фоном (раньше ждало все страницы, при молчащем сервере — до 55 с).</summary>
    public async Task<(List<JsonElement> Items, bool HasNext)> GetClientsPageAsync(
        int page, string? search, CancellationToken ct = default)
    {
        var (items, hasNext, _) = await GetClientsPageCoreAsync(page, search, ct).ConfigureAwait(false);
        OfflineClientCache.RememberClients(items); // 2026-10-29: кэш клиентов для работы без связи
        return (items, hasNext);
    }

    private async Task<(List<JsonElement> Items, bool HasNext, int Count)> GetClientsPageCoreAsync(
        int page, string? search, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture), // 2026-10-31: крупная страница — меньше запросов
        };
        if (!string.IsNullOrWhiteSpace(search))
            query["search"] = search.Trim();

        var data = await _client
            .RequestAsync(HttpMethod.Get, "api/main/clients/", null, query, ct)
            .ConfigureAwait(false);

        var pageItems = NurMarketApiClient.UnwrapList(data);
        var hasNext = page < HardMaxPages
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("next", out var next)
            && next.ValueKind == JsonValueKind.String;
        var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;
        return (pageItems, hasNext && pageItems.Count > 0, count);
    }

    public Task<JsonElement> CreateClientAsync(
        string fullName,
        string phone,
        string? email,
        string? address = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["full_name"] = fullName.Trim(),
            ["phone"] = phone.Trim(),
            ["type"] = "client",
        };
        if (!string.IsNullOrWhiteSpace(email))
            body["email"] = email.Trim();
        if (!string.IsNullOrWhiteSpace(address))
            body["address"] = address.Trim();

        return WriteAsync(() => _client.RequestAsync(HttpMethod.Post, "api/main/clients/", body, null, ct));
    }

    public Task<JsonElement> UpdateClientAsync(
        string id,
        string fullName,
        string phone,
        string? email,
        string? address = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["full_name"] = fullName.Trim(),
            ["phone"] = phone.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(email))
            body["email"] = email.Trim();
        // Пустую строку отправляем намеренно: так адрес можно и стереть, а не только задать.
        if (address is not null)
            body["address"] = address.Trim();

        return WriteAsync(() => _client.RequestAsync(HttpMethod.Patch, $"api/main/clients/{id}/", body, null, ct));
    }

    public Task DeleteClientAsync(string id, CancellationToken ct = default) =>
        WriteAsync(() => _client.RequestAsync(HttpMethod.Delete, $"api/main/clients/{id}/", null, null, ct));

    public Task<JsonElement> ResolveQrAsync(string qrText, CancellationToken ct = default) =>
        WriteAsync(() => _client.RequestAsync(HttpMethod.Post, "api/main/clients/resolve-qr/", new Dictionary<string, string> { ["token"] = qrText.Trim() }, null, ct));

    // 2026-11-06 (шаг 4): запись на clients/ меняет список — общий кэш сбрасывается и при успехе, и при ошибке (таймаут мог записать клиента на сервере).
    private static async Task<T> WriteAsync<T>(Func<Task<T>> request)
    {
        try { return await request().ConfigureAwait(false); }
        finally { InvalidateCache(); }
    }
}
