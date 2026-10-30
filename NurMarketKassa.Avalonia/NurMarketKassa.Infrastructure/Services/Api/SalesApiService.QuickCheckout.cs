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

    /// <summary>2026-10-27 (Б-03 сервера, PRICE_SYNC): то же, но с заголовком X-NurMarket-Strict-Price: 1 — чек с устаревшей ценой строки
    /// сервер не проводит (409, code "price_changed"). Реализация по умолчанию заголовок не шлёт (заглушки и старый сервер).
    /// Только для интерактивной оплаты: досылка очереди (SyncService) идёт без него — продажа уже состоялась по той цене.</summary>
    Task<JsonElement> PosQuickCheckoutAsync(
        JsonObject body,
        string idempotencyKey,
        TimeSpan timeout,
        bool strictPrice,
        CancellationToken ct = default) =>
        PosQuickCheckoutAsync(body, idempotencyKey, timeout, ct);

    /// <summary>2026-10-06, ТЗ ч.12, п. 3.6: POST api/main/pos/checkout/batch/ — до 50 продаж одним запросом,
    /// {"items": [{"idempotency_key", "body"}]} → {"results": [{"idempotency_key", "status", "sale" | "detail"}]}.
    /// Каждая продажа проводится отдельно (своя транзакция); повтор с тем же ключом — status 200 и прежняя продажа.</summary>
    Task<JsonElement> PosCheckoutBatchAsync(JsonObject body, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>2026-10-29 (приёмка 1.17.65, В-1): исход оплаты по ключу — GET api/main/idempotency/{key}/. Сервер отвечает found:true (продажа есть)
    /// или 404 JSON found:false (с таким ключом ничего не проведено). Старый сервер (404 HTML), нет связи и т.п. — <see cref="IdempotencyOutcome.Unknown"/>.
    /// Реализация по умолчанию (заглушки) — Unknown.</summary>
    Task<IdempotencyOutcome> PosIdempotencyLookupAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(IdempotencyOutcome.Unknown);
}

public sealed partial class SalesApiService : IPosQuickCheckoutApi
{
    public Task<JsonElement> PosQuickCheckoutAsync(
        JsonObject body,
        string idempotencyKey,
        TimeSpan timeout,
        CancellationToken ct = default) =>
        PosQuickCheckoutAsync(body, idempotencyKey, timeout, strictPrice: false, ct);

    public async Task<JsonElement> PosQuickCheckoutAsync(
        JsonObject body,
        string idempotencyKey,
        TimeSpan timeout,
        bool strictPrice,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));

        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        // 2026-10-27: строгая проверка цены (сервер без Б-03 заголовок просто не знает).
        if (strictPrice)
            headers[ServerPriceContract.StrictPriceHeader] = "1";
        var (response, _) = await _client.RequestWithHeadersAsync(
                HttpMethod.Post,
                "api/main/pos/checkout/",
                body,
                headers,
                ct,
                timeout)
            .ConfigureAwait(false);
        return response;
    }

    public Task<JsonElement> PosCheckoutBatchAsync(JsonObject body, TimeSpan timeout, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Post, "api/main/pos/checkout/batch/", body, null, ct, timeout);

    public async Task<IdempotencyOutcome> PosIdempotencyLookupAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return IdempotencyOutcome.Unknown;
        try
        {
            var response = await _client.RequestAsync(HttpMethod.Get, "api/main/idempotency/" + Uri.EscapeDataString(idempotencyKey.Trim()) + "/",
                null, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("found", out var found))
                return found.ValueKind == JsonValueKind.True ? IdempotencyOutcome.Found
                    : found.ValueKind == JsonValueKind.False ? IdempotencyOutcome.NotFound
                    : IdempotencyOutcome.Unknown;
            return IdempotencyOutcome.Unknown;
        }
        catch (ApiException ex) when (ex.StatusCode == 404
                                      && ex.Payload is { ValueKind: JsonValueKind.Object } payload
                                      && payload.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.False)
        {
            // 404 JSON {"found":false} — сервер понимает ключи и этого ключа не знает. 404 HTML (старый сервер) сюда не попадает.
            return IdempotencyOutcome.NotFound;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return IdempotencyOutcome.Unknown;
        }
    }
}
