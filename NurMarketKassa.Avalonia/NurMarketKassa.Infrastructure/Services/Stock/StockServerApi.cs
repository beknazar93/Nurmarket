using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-19 (склад «10 из 10», К-4/К-5/К-8): запросы к серверу — партии, резервы, срок годности, настройки склада. Клиент — тот же, что у бонусов (ServerLoyalty.ApiProvider);
/// в проверках можно передать свой. Запись настроек (PUT settings/stock/) здесь НЕТ намеренно: включает склад владелец на сервере.
/// </summary>
public static class StockServerApi
{
    private static NurMarketApiClient Client(NurMarketApiClient? api) =>
        api ?? (ServerLoyalty.ApiProvider?.Invoke() is { } a && !string.IsNullOrEmpty(a.AccessToken) ? a : throw new ApiException("Нет входа в NurCRM.", 401));

    /// <summary>GET api/main/products/{id}/batches/ (с kind=label — этикеточные партии).</summary>
    public static async Task<List<StockBatch>> BatchesAsync(string productId, bool labels = false, NurMarketApiClient? api = null, CancellationToken ct = default)
    {
        var query = labels ? new Dictionary<string, string> { ["kind"] = "label" } : null;
        var d = await Client(api).RequestAsync(HttpMethod.Get, $"api/main/products/{Uri.EscapeDataString(productId.Trim())}/batches/", null, query, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return ServerStock.ParseBatches(d);
    }

    /// <summary>GET api/main/stock/expiring/ — «Скоро истекает» по партиям. null — форма ответа не распознана.</summary>
    public static async Task<List<StockExpiringRow>?> ExpiringAsync(NurMarketApiClient? api = null, CancellationToken ct = default, int? days = null, string? productId = null)
    {
        // 2026-11-19 (ответ Умара 19.11, К-4/К-5/К-8 п. 1, 4): настройки «скоро истекает» на сервере нет — days по умолчанию 30 (0–365), касса передаёт своё; ответ постраничный {days, today, count, page, page_size, results[]}.
        var all =new List<StockExpiringRow>();
        for (var page = 1; page <= 20; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["days"] = Math.Clamp(days ?? ServerStock.ExpiringDays, 0, 365).ToString(CultureInfo.InvariantCulture),
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "200",
            };
            if (!string.IsNullOrWhiteSpace(productId))
                query["product"] = productId.Trim();
            var d = await Client(api).RequestAsync(HttpMethod.Get, "api/main/stock/expiring/", null, query, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (ServerStock.ParseExpiringRaw(d, DateTime.Today) is not { } part)
                return page == 1 ? null : ServerStock.GroupExpiring(all);
            all.AddRange(part);
            var count = ServerStock.Num(d, "count");
            var pageSize = ServerStock.Num(d, "page_size") ?? 200;
            // последняя страница: набралось count или пришло меньше страницы (нет count — обычный массив, одной страницы хватит)
            if (d.ValueKind != JsonValueKind.Object || count is not { } total || all.Count >= total || part.Count == 0 || part.Count < pageSize)
                break;
        }
        return ServerStock.GroupExpiring(all);
    }

    /// <summary>GET api/main/settings/stock/ — настройки склада (только чтение).</summary>
    public static Task<JsonElement> SettingsAsync(NurMarketApiClient? api = null, CancellationToken ct = default) =>
        Client(api).RequestAsync(HttpMethod.Get, "api/main/settings/stock/", null, null, ct, TimeSpan.FromSeconds(12));

    /// <summary>GET api/main/reservations/ — список резервов. 2026-11-19 (ответ Умара 19.11, п. 2): выбор резерва для чека — ?product=&amp;source=&amp;source_id=&amp;status=active (по клиенту фильтра нет — по source_id заказа/отложенного чека).</summary>
    public static async Task<List<StockReservation>> ReservationsAsync(NurMarketApiClient? api = null, CancellationToken ct = default, string? product = null, string? source = null, string? sourceId = null, string? status = null)
    {
        var query = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(product)) query["product"] = product.Trim();
        if (!string.IsNullOrWhiteSpace(source)) query["source"] = source.Trim();
        if (!string.IsNullOrWhiteSpace(sourceId)) query["source_id"] = sourceId.Trim();
        if (!string.IsNullOrWhiteSpace(status)) query["status"] = status.Trim();
        var d = await Client(api).RequestAsync(HttpMethod.Get, "api/main/reservations/", null, query.Count > 0 ? query : null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return ServerStock.ParseReservations(d);
    }

    /// <summary>POST api/main/reservations/ {product, qty, comment, expires_at?, allow_over?} с Idempotency-Key. Свободного не хватило — ApiException 409
    /// (<see cref="ServerStock.TryParseInsufficient"/>); «Всё равно зарезервировать» — повтор с <paramref name="allowOver"/> и НОВЫМ ключом.</summary>
    public static async Task<JsonElement> ReserveAsync(string productId, double qty, string comment, DateTime? expiresAt, bool allowOver, string idempotencyKey, NurMarketApiClient? api = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["product"] = productId.Trim(),
            ["qty"] = Math.Round(qty, 6, MidpointRounding.AwayFromZero),
            ["comment"] = comment ?? "",
        };
        if (expiresAt is { } e)
            body["expires_at"] = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (allowOver)
            body["allow_over"] = true;
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        var (data, _) = await Client(api).RequestWithHeadersAsync(HttpMethod.Post, "api/main/reservations/", body, headers, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return data;
    }

    /// <summary>DELETE api/main/reservations/{id}/ — снять резерв.</summary>
    public static Task<JsonElement> ReleaseAsync(string reservationId, NurMarketApiClient? api = null, CancellationToken ct = default) =>
        Client(api).RequestAsync(HttpMethod.Delete, $"api/main/reservations/{Uri.EscapeDataString(reservationId.Trim())}/", null, null, ct, TimeSpan.FromSeconds(20));
}
