using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация работы со сменами поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// </summary>
public sealed class ShiftApiService : IShiftApiService
{
    private readonly NurMarketApiClient _client;

    public ShiftApiService(NurMarketApiClient client) => _client = client;

    public async Task<JsonElement> ConstructionCashboxesListAsync(CancellationToken ct = default)
    {
        try
        {
            return await _client.RequestAsync(HttpMethod.Get, "api/construction/cashboxes/", null, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            using var d = JsonDocument.Parse("[]");
            return d.RootElement.Clone();
        }
    }

    public Task<JsonElement> ConstructionShiftsListAsync(bool openOnly = false, CancellationToken ct = default)
    {
        if (!openOnly)
            return FetchFullShiftsListAsync(ct);

        return FetchOpenShiftsListAsync(ct);
    }

    // ── 2026-10-04, отчёт о производительности (п. 8): один запрос открытой смены после продажи ──────
    // После оплаты список открытых смен спрашивали двое: проверка смены кассы (ShiftStateService, сразу) и
    // шапка «Касса: N сом» (MainWindow, через 2 с) — два одинаковых запроса на каждую продажу. Теперь
    // запоминается последний ответ ?status=open и момент, когда запрос ушёл; шапка берёт его, если он ушёл
    // уже после продажи (ответ содержит её), или дожидается такого идущего запроса.
    private readonly object _openListSync = new();
    private JsonElement _openList;
    private DateTime _openListRequestedUtc = DateTime.MinValue;
    private string? _openListToken;
    private Task<JsonElement>? _openListInFlight;
    private DateTime _openListInFlightStartedUtc = DateTime.MinValue;
    private DateTime _saleRecordedUtc = DateTime.MinValue;

    private async Task<JsonElement> FetchOpenShiftsListAsync(CancellationToken ct)
    {
        var requestedUtc = DateTime.UtcNow;
        var query = new Dictionary<string, string> { ["status"] = "open" };
        var task = _client.RequestAsync(HttpMethod.Get, "api/construction/shifts/", null, query, ct);
        lock (_openListSync)
        {
            _openListInFlight = task;
            _openListInFlightStartedUtc = requestedUtc;
        }

        var payload = await task.ConfigureAwait(false);
        lock (_openListSync)
        {
            if (requestedUtc >= _openListRequestedUtc)
            {
                _openList = payload;
                _openListRequestedUtc = requestedUtc;
                _openListToken = _client.AccessToken;
            }
        }

        return payload;
    }

    public void NoteSaleRecorded()
    {
        lock (_openListSync)
            _saleRecordedUtc = DateTime.UtcNow;
    }

    /// <summary>Остаток смены изменился не продажей (внесение/изъятие, открытие/закрытие смены) —
    /// запомненный список больше не годится.</summary>
    private void ForgetOpenShiftsList()
    {
        lock (_openListSync)
            _openListRequestedUtc = DateTime.MinValue;
    }

    public async Task<JsonElement?> OpenShiftsListAfterSaleAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        Task<JsonElement>? inFlight = null;
        lock (_openListSync)
        {
            if (_saleRecordedUtc == DateTime.MinValue)
                return null;

            var sameLogin = !string.IsNullOrEmpty(_openListToken)
                && string.Equals(_openListToken, _client.AccessToken, StringComparison.Ordinal);
            if (_openListRequestedUtc >= _saleRecordedUtc
                && sameLogin
                && DateTime.UtcNow - _openListRequestedUtc <= maxAge)
                return _openList;

            if (_openListInFlight is { IsCompleted: false } running && _openListInFlightStartedUtc >= _saleRecordedUtc)
                inFlight = running;
        }

        if (inFlight is null)
            return null;

        try
        {
            return await inFlight.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Тот запрос не удался — вызывающий спросит сам.
            return null;
        }
    }

    // Снимок последнего полного списка смен (см. TryGetRecentShiftsList). Сам список каждый раз
    // по-прежнему идёт с сервера — снимок только запоминается.
    private readonly object _recentListSync = new();
    private JsonElement _recentList;
    private DateTime _recentListAtUtc = DateTime.MinValue;
    private string? _recentListToken;

    /// <summary>Сколько страниц списка смен читаем максимум (по 100 смен — 3000 смен).</summary>
    private const int MaxShiftPages = 30;

    /// <summary>Сколько страниц качаем одновременно: каждая отвечает 2–3 с, подряд это было бы
    /// слишком долго, а заваливать сервер ради истории смен незачем.</summary>
    private const int ShiftPagesParallelism = 3;

    private async Task<JsonElement> FetchFullShiftsListAsync(CancellationToken ct)
    {
        var payload = await _client.RequestAsync(HttpMethod.Get, "api/construction/shifts/", null, null, ct).ConfigureAwait(false);
        payload = await AppendRemainingShiftPagesAsync(payload, ct).ConfigureAwait(false);
        lock (_recentListSync)
        {
            _recentList = payload;
            _recentListAtUtc = DateTime.UtcNow;
            // Вход, под которым список получен (после возможного обновления токена в самом запросе).
            _recentListToken = _client.AccessToken;
        }

        return payload;
    }

    /// <summary>2026-09-28, сверка с сайтом: сервер отдаёт смены страницами по 100 ({count, next,
    /// results}), а касса читала только первую — «Финансы → Смены» показывали 99 закрытых смен
    /// из 201 (всего 202 на тестовой компании), история смен и табель теряли всё старше двух
    /// недель. Теперь дочитываем остальные страницы (?page=2, 3, …; страница за последней
    /// отвечает 404 «Неправильная страница») и отдаём тот же объект, но со всеми сменами в
    /// results — разбор у всех вызывающих прежний. Не пришла какая-то страница — отдаём то, что
    /// есть: неполный список лучше пустого.</summary>
    private async Task<JsonElement> AppendRemainingShiftPagesAsync(JsonElement first, CancellationToken ct)
    {
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("results", out var firstRows)
            || firstRows.ValueKind != JsonValueKind.Array
            || !first.TryGetProperty("next", out var next)
            || next.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(next.GetString()))
            return first;

        var perPage = firstRows.GetArrayLength();
        var total = first.TryGetProperty("count", out var countProp) && countProp.ValueKind == JsonValueKind.Number
            && countProp.TryGetInt32(out var count) ? count : 0;
        if (perPage <= 0)
            return first;

        // Число страниц известно из count — качаем их параллельно (по 2–3 с на страницу). Без
        // count идём по одной, пока сервер отдаёт next.
        var pageRows = new SortedDictionary<int, JsonElement>();
        if (total > perPage)
        {
            var pages = Math.Min((int)Math.Ceiling(total / (double)perPage), MaxShiftPages);
            using var gate = new SemaphoreSlim(ShiftPagesParallelism);
            var fetched = await Task.WhenAll(Enumerable.Range(2, pages - 1).Select(async page =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return (Page: page, Data: await FetchShiftPageAsync(page, ct).ConfigureAwait(false));
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            foreach (var (page, data) in fetched)
            {
                if (data is { } rows)
                    pageRows[page] = rows;
            }
        }
        else
        {
            for (var page = 2; page <= MaxShiftPages; page++)
            {
                if (await FetchShiftPageAsync(page, ct).ConfigureAwait(false) is not { } rows || rows.GetArrayLength() == 0)
                    break;
                pageRows[page] = rows;
                if (rows.GetArrayLength() < perPage)
                    break;
            }
        }

        if (pageRows.Count == 0)
            return first;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", Math.Max(total, perPage));
            writer.WriteNull("next");
            writer.WriteNull("previous");
            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var rows in new[] { firstRows }.Concat(pageRows.Values))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    // Пока листали, могла открыться новая смена и сдвинуть страницы — без
                    // повторов одной и той же смены.
                    var id = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var idProp)
                        ? idProp.ToString()
                        : null;
                    if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                        continue;
                    row.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var merged = JsonDocument.Parse(stream.ToArray());
        return merged.RootElement.Clone();
    }

    /// <summary>results одной страницы списка смен; null — страницы нет (404) или она не пришла.</summary>
    private async Task<JsonElement?> FetchShiftPageAsync(int page, CancellationToken ct)
    {
        try
        {
            var data = await _client.RequestAsync(
                HttpMethod.Get,
                "api/construction/shifts/",
                null,
                new Dictionary<string, string> { ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ct).ConfigureAwait(false);
            return data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("results", out var rows)
                   && rows.ValueKind == JsonValueKind.Array
                ? rows.Clone()
                : null;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Смены: страница {page} списка не получена: {ex.Message}", "SHIFTS");
            return null;
        }
    }

    public bool TryGetRecentShiftsList(TimeSpan maxAge, out JsonElement payload)
    {
        lock (_recentListSync)
        {
            // Снимок другого входа (смена кассира, другая компания) не годится: сервер отдаёт
            // каждому пользователю свой набор смен.
            var fresh = _recentListAtUtc != DateTime.MinValue
                && DateTime.UtcNow - _recentListAtUtc <= maxAge
                && !string.IsNullOrEmpty(_recentListToken)
                && string.Equals(_recentListToken, _client.AccessToken, StringComparison.Ordinal);
            payload = fresh ? _recentList : default;
            return fresh;
        }
    }

    private void ForgetRecentShiftsList()
    {
        lock (_recentListSync)
            _recentListAtUtc = DateTime.MinValue;
    }

    public Task<JsonElement> ConstructionShiftGetAsync(string shiftId, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Get, $"api/construction/shifts/{Uri.EscapeDataString(shiftId.Trim())}/", null, null, ct);

    public async Task<JsonElement> ConstructionShiftOpenAsync(
        string cashboxId,
        string openingCash = "0.00",
        CancellationToken ct = default)
    {
        ForgetRecentShiftsList();
        ForgetOpenShiftsList(); // 2026-10-04, п. 8
        var paths = new[] { "api/construction/shifts/open/", "api/construction/shift/open/" };
        var payloads = new[]
        {
            new Dictionary<string, string> { ["cashbox"] = cashboxId.Trim(), ["opening_cash"] = openingCash.Trim() },
            new Dictionary<string, string> { ["cashbox_id"] = cashboxId.Trim(), ["opening_cash"] = openingCash.Trim() },
        };

        ApiException? last = null;
        foreach (var path in paths)
        {
            for (var i = 0; i < payloads.Length; i++)
            {
                try
                {
                    return await _client.RequestAsync(HttpMethod.Post, path, payloads[i], null, ct).ConfigureAwait(false);
                }
                catch (ApiException e)
                {
                    last = e;
                    if (e.StatusCode == 404)
                        break;
                    if (e.StatusCode == 400 && i + 1 < payloads.Length)
                        continue;
                    throw;
                }
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Не удалось открыть смену", 404);
    }

    public async Task<JsonElement> ConstructionShiftCloseAsync(
        string shiftId,
        string? closingCash = null,
        IReadOnlyDictionary<string, string>? extraFields = null,
        CancellationToken ct = default)
    {
        ForgetRecentShiftsList();
        ForgetOpenShiftsList(); // 2026-10-04, п. 8
        var sid = Uri.EscapeDataString(shiftId.Trim());
        var paths = new[]
        {
            $"api/construction/shifts/{sid}/close/",
            $"api/construction/shift/{sid}/close/",
        };

        var body = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(closingCash))
            body["closing_cash"] = closingCash.Trim();
        if (extraFields != null)
        {
            foreach (var (key, value) in extraFields)
                body[key] = value;
        }

        ApiException? last = null;
        foreach (var path in paths)
        {
            try
            {
                return await _client.RequestAsync(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);
            }
            catch (ApiException e)
            {
                last = e;
                if (e.StatusCode == 404)
                    continue;
                throw;
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Не удалось закрыть смену", 404);
    }

    public Task<JsonElement> ConstructionCashFlowCreateAsync(IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
    {
        // 2026-10-04, п. 8: внесение/изъятие меняет остаток смены — запомненный список открытых смен устарел.
        ForgetOpenShiftsList();
        return _client.RequestAsync(HttpMethod.Post, "api/construction/cashflows/", body, null, ct);
    }

    public Task<JsonElement> ConstructionCashFlowsForShiftAsync(string shiftId, int page = 1, CancellationToken ct = default) =>
        _client.RequestAsync(
            HttpMethod.Get,
            "api/construction/cashflows/",
            null,
            new Dictionary<string, string>
            {
                ["shift"] = shiftId.Trim(),
                ["page_size"] = "500",
                ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct);
}
