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

        var query = new Dictionary<string, string> { ["status"] = "open" };
        return _client.RequestAsync(HttpMethod.Get, "api/construction/shifts/", null, query, ct);
    }

    // Снимок последнего полного списка смен (см. TryGetRecentShiftsList). Сам список каждый раз
    // по-прежнему идёт с сервера — снимок только запоминается.
    private readonly object _recentListSync = new();
    private JsonElement _recentList;
    private DateTime _recentListAtUtc = DateTime.MinValue;
    private string? _recentListToken;

    private async Task<JsonElement> FetchFullShiftsListAsync(CancellationToken ct)
    {
        var payload = await _client.RequestAsync(HttpMethod.Get, "api/construction/shifts/", null, null, ct).ConfigureAwait(false);
        lock (_recentListSync)
        {
            _recentList = payload;
            _recentListAtUtc = DateTime.UtcNow;
            // Вход, под которым список получен (после возможного обновления токена в самом запросе).
            _recentListToken = _client.AccessToken;
        }

        return payload;
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

    public Task<JsonElement> ConstructionCashFlowCreateAsync(IReadOnlyDictionary<string, string> body, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Post, "api/construction/cashflows/", body, null, ct);

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
