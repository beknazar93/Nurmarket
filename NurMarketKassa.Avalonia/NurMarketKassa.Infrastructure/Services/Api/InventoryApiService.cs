using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Реализация ревизии/списания поверх настроенного транспорта <see cref="NurMarketApiClient"/>.</summary>
public sealed class InventoryApiService : IInventoryApiService
{
    private readonly NurMarketApiClient _client;

    public InventoryApiService(NurMarketApiClient client) => _client = client;

    public async Task<string?> CreateSessionAsync(
        string note, IReadOnlyList<InventorySessionItem> items, CancellationToken ct = default)
    {
        var body = new
        {
            note = note ?? "",
            items = items.Select(i => new
            {
                product_id = i.ProductId,
                // 2026-09-12: было "0.###" (до 3 знаков) — сервер отвечал "Убедитесь, что вы
                // ввели не более 2 цифры после запятой" и вся ревизия падала, стоило только
                // взвешенному товару дать вес с 3 знаками (например 0.711 кг).
                quantity_fact = i.QuantityFact.ToString("0.##", CultureInfo.InvariantCulture),
            }).ToList(),
        };

        var response = await _client
            .RequestAsync(HttpMethod.Post, "api/main/inventory/sessions/", body, null, ct)
            .ConfigureAwait(false);

        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("id", out var idEl) &&
            idEl.ValueKind == JsonValueKind.String)
            return idEl.GetString();

        return null;
    }

    public Task ApplySessionAsync(string sessionId, bool allowNegative, CancellationToken ct = default)
    {
        var escaped = Uri.EscapeDataString(sessionId.Trim());
        var body = new { allow_negative = allowNegative };
        return _client.RequestAsync(HttpMethod.Post, $"api/main/inventory/sessions/{escaped}/apply/", body, null, ct);
    }

    // 2026-10-24 (ТЗ «Склад», Р1): состояние акта — как у мобилки (_sessionApplied): проведён, если is_applied / applied_at / статус «проведён».
    public async Task<InventorySessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var escaped = Uri.EscapeDataString(sessionId.Trim());
        JsonElement s;
        try
        {
            s = await _client.RequestAsync(HttpMethod.Get, $"api/main/inventory/sessions/{escaped}/", null, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }

        if (s.ValueKind != JsonValueKind.Object)
            return null;
        var status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
        var applied = (s.TryGetProperty("is_applied", out var ia) && ia.ValueKind == JsonValueKind.True)
            || (s.TryGetProperty("applied_at", out var at) && at.ValueKind != JsonValueKind.Null && at.ValueKind != JsonValueKind.Undefined)
            || status.ToLowerInvariant() is "applied" or "done" or "completed" or "closed" or "posted";
        return new InventorySessionInfo(sessionId, status, applied);
    }

    // 2026-10-27, аудит владельца В-2: акты всех касс (раньше «История списаний» и «Прошлые ревизии» владельца показывали только этот ПК).
    // Ответ — массив актов или {results:[…], next}; кэш 60 с, чтобы вкладки не дёргали сервер при каждом открытии.
    private static readonly SemaphoreSlim ActsGate = new(1, 1);
    private static IReadOnlyList<InventoryAct>? _actsCache;
    private static DateTime _actsCacheUtc = DateTime.MinValue;
    private static string? _actsCacheKey;

    /// <summary>Сбросить кэш актов (после своего списания/ревизии — чтобы список показал новый акт сразу).</summary>
    public static void InvalidateActsCache() => _actsCacheUtc = DateTime.MinValue;

    public async Task<IReadOnlyList<InventoryAct>?> ListActsAsync(CancellationToken ct = default)
    {
        await ActsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = _client.AccessToken ?? "";
            if (_actsCache is not null && _actsCacheKey == key && DateTime.UtcNow - _actsCacheUtc < TimeSpan.FromSeconds(60))
                return _actsCache;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in await _client.GetEmployeesAsync(ct).ConfigureAwait(false) ?? new List<NurMarketKassa.Models.EmployeeInfoDto>())
                    if (!string.IsNullOrWhiteSpace(e.Id))
                        names[e.Id!] = !string.IsNullOrWhiteSpace(e.FullName) ? e.FullName! : e.Email ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Акты склада: имена сотрудников не получены ({ex.Message}).", "WARNING");
            }

            var acts = new List<InventoryAct>();
            string? path = "api/main/inventory/sessions/?page_size=200";
            for (var page = 0; page < 6 && path is not null; page++)
            {
                JsonElement data;
                try
                {
                    data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Акты склада: список не получен ({ex.Message}).", "WARNING");
                    return acts.Count > 0 ? acts : null;
                }

                JsonElement list;
                path = null;
                if (data.ValueKind == JsonValueKind.Array)
                    list = data;
                else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
                {
                    list = r;
                    if (data.TryGetProperty("next", out var nx) && nx.ValueKind == JsonValueKind.String && Uri.TryCreate(nx.GetString(), UriKind.Absolute, out var nu))
                        path = nu.PathAndQuery.TrimStart('/');
                }
                else
                    return null;

                foreach (var s in list.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object)
                        continue;
                    var status = Str(s, "status");
                    var appliedAt = Str(s, "applied_at");
                    var isApplied = appliedAt.Length > 0 || status.ToLowerInvariant() is "applied" or "done" or "completed" or "closed" or "posted";
                    var whenText = appliedAt.Length > 0 ? appliedAt : Str(s, "created_at");
                    var when = DateTimeOffset.TryParse(whenText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var w) ? w.UtcDateTime : DateTime.MinValue;
                    var by = Str(s, "created_by");
                    var lines = new List<InventoryActLine>();
                    if (s.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
                        foreach (var l in ls.EnumerateArray())
                            if (l.ValueKind == JsonValueKind.Object)
                                lines.Add(new InventoryActLine(Str(l, "product"), Str(l, "product_name"), Num(l, "quantity_before"), Num(l, "quantity_fact"), Num(l, "quantity_delta")));
                    acts.Add(new InventoryAct(Str(s, "id"), Str(s, "note"), status, isApplied, when, by,
                        names.TryGetValue(by, out var nm) ? nm : "", lines));
                }
            }

            _actsCache = acts.OrderByDescending(a => a.CreatedUtc).ToList();
            _actsCacheKey = key;
            _actsCacheUtc = DateTime.UtcNow;
            return _actsCache;
        }
        finally
        {
            ActsGate.Release();
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
