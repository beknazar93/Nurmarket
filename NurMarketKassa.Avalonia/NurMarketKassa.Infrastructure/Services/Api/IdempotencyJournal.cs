using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Что журнал идемпотентности сервера говорит о ключе: NotSent — ключа нет (операция не проводилась), Done — проведена, InProgress — сервер ещё выполняет (ждать, НЕ повторять),
/// FailedRetry — прошлая попытка не удалась (можно повторить ТЕМ ЖЕ ключом), Unknown — сервер не ответил по существу / старый сервер (действовать как раньше).</summary>
public enum IdempotencyVerdict { Unknown, NotSent, Done, InProgress, FailedRetry }

/// <summary>2026-11-05 (сервер ceba67c, признак idempotency_lookup_bonus_transfers): GET api/main/idempotency/{ключ}/ находит не только чеки и возвраты, но и бонусы клиента и перемещения между
/// филиалами. <c>state</c>: done / in_progress / failed. Здесь — один разбор и один запрос для кассы (бонусы ServerLoyalty, перемещения).</summary>
public static class IdempotencyJournal
{
    public static IdempotencyVerdict Decide(IdempotencyRecord? record)
    {
        if (record is null)
            return IdempotencyVerdict.Unknown;
        if (!record.Found)
            return IdempotencyVerdict.NotSent;
        var state = (record.State ?? "").Trim().ToLowerInvariant();
        if (state is "in_progress" or "processing" or "running" or "pending")
            return IdempotencyVerdict.InProgress;
        if (state == "failed")
            return IdempotencyVerdict.FailedRetry;
        if (record.IsSuccess)
            return IdempotencyVerdict.Done;
        return IdempotencyVerdict.Unknown; // done с кодом ошибки — повтор вернёт тот же отказ; решает обычная отправка
    }

    /// <summary>Запрос к журналу (не дольше 8 с); любой сбой — null (Unknown).</summary>
    public static async Task<IdempotencyRecord?> LookupAsync(NurMarketApiClient api, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        try
        {
            var response = await api.RequestAsync(HttpMethod.Get, "api/main/idempotency/" + Uri.EscapeDataString(key.Trim()) + "/", null, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            return SalesApiService.ReadIdempotencyRecord(response);
        }
        catch (ApiException ex) when (ex.StatusCode == 404 && ex.Payload is { ValueKind: JsonValueKind.Object } payload
                                      && payload.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.False)
        {
            return new IdempotencyRecord(false, null, null, "idempotency_key_not_found", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Вердикт по ключу; только при признаке сервера — иначе Unknown без запроса.</summary>
    public static async Task<IdempotencyVerdict> CheckAsync(NurMarketApiClient api, string key, CancellationToken ct = default) =>
        ServerCapabilities.Has(ServerCapabilities.IdempotencyLookupBonusTransfers)
            ? Decide(await LookupAsync(api, key, ct).ConfigureAwait(false))
            : IdempotencyVerdict.Unknown;
}
