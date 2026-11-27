using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-26 (Д1, отчёт «Проверка сфер», autoparts): на некоторых компаниях сервер отвечает на POST pos/sales/{id}/return/ кодом 403 <c>pin_required</c> —
/// возврат нужно подтвердить PIN владельца/админа. Тот же механизм, что у позднего чека (<see cref="IPosLateAcceptApi"/>): PIN → POST users/auth/pin/verify/ {pin} → токен →
/// повтор запроса с заголовком X-Confirm-Token и ТЕМ ЖЕ Idempotency-Key. Отдельный интерфейс, как IPosExchangeApi: заглушки общего ISalesApiService его не знают.</summary>
public interface IPosReturnConfirmApi
{
    /// <summary>POST api/main/pos/sales/{id}/return/ с X-Confirm-Token. Тело — как у обычного возврата (idempotency_key, cashbox_role, reason?, is_defect?, items?);
    /// <paramref name="lines"/> пуст или null — весь чек. <paramref name="idempotencyKey"/> обязателен: тот же, что был у первой отправки.</summary>
    Task<JsonElement> PosReturnConfirmedAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, string? reason, bool isDefect, string idempotencyKey, string confirmToken, CancellationToken ct = default);
}

public sealed partial class SalesApiService : IPosReturnConfirmApi
{
    public async Task<JsonElement> PosReturnConfirmedAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, string? reason, bool isDefect, string idempotencyKey, string confirmToken, CancellationToken ct = default)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(confirmToken))
            throw new ArgumentException("Нужен токен подтверждения.", nameof(confirmToken));
        var body = new Dictionary<string, object>
        {
            ["idempotency_key"] = idempotencyKey.Trim(),
            ["cashbox_role"] = "pos_main",
        };
        if (!string.IsNullOrWhiteSpace(reason))
            body["reason"] = reason.Trim();
        if (isDefect)
            body["is_defect"] = true;
        if (lines is { Count: > 0 })
            body["items"] = lines.Select(l => RefundItem(l)).ToList();
        var headers = new Dictionary<string, string> { [ConfirmTokenHeader] = confirmToken.Trim() };
        using var critical = ApiThrottle.BeginCritical(); // возврат — деньги, быстрая полоса очереди (как обычный возврат)
        var (response, _) = await _client.RequestWithHeadersAsync(HttpMethod.Post, $"api/main/pos/sales/{Uri.EscapeDataString(id)}/return/", body, headers, ct).ConfigureAwait(false);
        return response;
    }
}

/// <summary>2026-11-26 (Д1): повтор возврата с PIN. Первая отправка — как раньше; 403 pin_required — спросить токен (<paramref name="askToken"/>: окно PIN → users/auth/pin/verify/)
/// и отправить ещё раз с ним. Отмена PIN (null/пусто) — исходная ошибка pin_required уходит окну (понятный текст, ключ забывается как у любого отказа 4xx).
/// Если и с токеном сервер снова просит PIN или отказывает — ошибка уходит как есть: PIN второй раз не спрашиваем (кассир нажмёт «Вернуть» снова).</summary>
public static class ReturnPinRetry
{
    public static async Task<T> RunAsync<T>(Func<string?, Task<T>> send, Func<Task<string?>> askToken)
    {
        try
        {
            return await send(null).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ServerErrorCodes.IsPinRequired(ex))
        {
            var token = await askToken().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(token))
                throw;
            return await send(token).ConfigureAwait(true);
        }
    }
}
