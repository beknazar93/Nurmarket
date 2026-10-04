using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Итоги допродажи по всем кассам компании (GET /api/main/recommendations/stats/).</summary>
public sealed record ServerUpsellStats(int Shown, int Accepted, int Skipped, double AcceptanceRate, double Revenue, double Profit);

/// <summary>2026-10-05, ТЗ часть 7, раздел 2 «Умная допродажа» — сервер выложил адреса 05.10 около 04:30
/// (владелец: «если бэкенд добавит — сразу применяй в нашей программе»). Подсказки по-прежнему считает сама касса;
/// сервер хранит общий журнал показов и нажатий со всех касс компании и считает по нему итоги.</summary>
public sealed class RecommendationsApi
{
    private const string Root = "api/main/recommendations/";
    private readonly NurMarketApiClient _api;

    public RecommendationsApi(NurMarketApiClient api) => _api = api;

    /// <summary>POST events/ — пачка до 200 событий; повтор с тем же client_event_id дублей не создаёт.</summary>
    public Task<JsonElement> SendEventsAsync(IReadOnlyList<Dictionary<string, object?>> events, CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Post, Root + "events/", new Dictionary<string, object?> { ["events"] = events }, null, ct, TimeSpan.FromSeconds(20));

    /// <summary>PATCH events/link-sale/ — всем событиям чека проставить номер продажи.</summary>
    public Task<JsonElement> LinkSaleAsync(string cartId, string saleId, CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Patch, Root + "events/link-sale/",
            new Dictionary<string, object?> { ["cart_id"] = cartId, ["sale_id"] = saleId }, null, ct, TimeSpan.FromSeconds(20));

    /// <summary>GET stats/ — итоги за период. null — у сервера нет адреса (404) или нет прав.</summary>
    public async Task<ServerUpsellStats?> GetStatsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, Root + "stats/", null, new Dictionary<string, string>
            {
                ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object)
                return null;
            return new ServerUpsellStats(Int(data, "shown"), Int(data, "accepted"), Int(data, "skipped"),
                Num(data, "acceptance_rate"), Num(data, "revenue"), Num(data, "profit"));
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            return null;
        }
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => 0,
            }
            : 0;
}
