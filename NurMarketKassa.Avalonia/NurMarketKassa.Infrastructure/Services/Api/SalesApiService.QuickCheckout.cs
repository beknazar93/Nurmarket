using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-09-28, BE-11: продажа одним запросом. Отдельный интерфейс, а не новый метод
/// в <see cref="ISalesApiService"/>: общий интерфейс правят и другие части кассы, а заглушки
/// в тестах, которые его реализуют, этого метода не знают — касса тогда просто идёт старым путём.</summary>
public interface IPosQuickCheckoutApi
{
    /// <summary>POST api/main/pos/checkout/ с заголовком Idempotency-Key. Повтор с тем же ключом
    /// сервер не проводит второй раз: отвечает 200 и той же продажей с "replayed": true.
    /// ВНИМАНИЕ (проверено 2026-09-28): тело при повторе сервер НЕ сравнивает — тот же ключ с
    /// другим составом вернёт прежнюю продажу. Ключ обязан меняться вместе с составом чека.</summary>
    Task<JsonElement> PosQuickCheckoutAsync(
        JsonObject body,
        string idempotencyKey,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public sealed partial class SalesApiService : IPosQuickCheckoutApi
{
    public async Task<JsonElement> PosQuickCheckoutAsync(
        JsonObject body,
        string idempotencyKey,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));

        var (response, _) = await _client.RequestWithHeadersAsync(
                HttpMethod.Post,
                "api/main/pos/checkout/",
                body,
                new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() },
                ct,
                timeout)
            .ConfigureAwait(false);
        return response;
    }
}
