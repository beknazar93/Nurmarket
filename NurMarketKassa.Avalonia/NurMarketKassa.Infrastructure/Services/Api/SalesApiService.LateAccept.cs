using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-01: проведение офлайн-чека старше 7 суток по PIN владельца (ответ Умара 31.10, сервер c8958b4, раздел 2): сервер принимает продажу
/// от 7 до 90 суток, если в теле "accept_late": true и подтвердил владелец/админ — запросом от него ИЛИ заголовком X-Confirm-Token, полученным из
/// POST /api/users/auth/pin/verify/ {pin} (токен живёт 5 минут). Отдельный интерфейс, как IPosQuickCheckoutApi: заглушки не обязаны его знать.</summary>
public interface IPosLateAcceptApi
{
    /// <summary>POST users/auth/pin/verify/ {pin}. Возвращает токен подтверждения. Неверный PIN — <see cref="OwnerPinResult.Rejected"/>.</summary>
    Task<OwnerPinResult> VerifyOwnerPinAsync(string pin, CancellationToken ct = default);

    /// <summary>POST pos/checkout/ с Idempotency-Key и (если есть) X-Confirm-Token; accept_late уже должен стоять в теле.</summary>
    Task<JsonElement> PosLateCheckoutAsync(JsonObject body, string idempotencyKey, string? confirmToken, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Итог проверки PIN владельца. Token — значение для X-Confirm-Token (значение в журнал не пишется). Rejected — сервер ЯВНО сказал «PIN неверен»; Unauthorized — 401/403 (сессия истекла/нет прав, попытка PIN не тратится).</summary>
public readonly record struct OwnerPinResult(bool Accepted, bool Rejected, string? Token, string? Error, bool Unauthorized = false);

public sealed partial class SalesApiService : IPosLateAcceptApi
{
    /// <summary>Заголовок подтверждения владельца (ответ Умара 31.10, п. 2).</summary>
    internal const string ConfirmTokenHeader = "X-Confirm-Token";

    // TODO(Умар): в ответе POST users/auth/pin/verify/ НЕ описано, в каком поле лежит токен. Здесь только явные имена подтверждения —
    // 2026-11-01 (ревью): "token"/"access" убраны намеренно, чтобы не принять JWT сессии за X-Confirm-Token. После ответа Умара оставить одно настоящее имя.
    private static readonly string[] ConfirmTokenFieldCandidates = ["confirm_token", "confirmation_token", "x_confirm_token"];

    public async Task<OwnerPinResult> VerifyOwnerPinAsync(string pin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return new OwnerPinResult(false, true, null, null);
        try
        {
            // TODO(Умар): адрес и тело {pin} — по ответу 31.10 (раздел 2); формат ошибки неверного PIN (код 400/403?) не описан.
            var response = await _client.RequestAsync(HttpMethod.Post, "api/users/auth/pin/verify/",
                new JsonObject { ["pin"] = pin.Trim() }, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (response.ValueKind == JsonValueKind.Object)
            {
                // 2026-11-01 (ревью): явное «PIN неверен» в ответе 200 ({"valid": false}).
                if (response.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
                    return new OwnerPinResult(false, true, null, null);
                foreach (var name in ConfirmTokenFieldCandidates)
                    if (response.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        return new OwnerPinResult(true, false, t.GetString(), null);
                // Токена не нашли: в сообщение — только имена полей ответа (значения могут быть секретом).
                var keys = string.Join(", ", response.EnumerateObject().Select(p => p.Name));
                return new OwnerPinResult(false, false, null, "no_token_field: " + keys);
            }
            return new OwnerPinResult(false, false, null, "no_token_field: ответ не объект");
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 403)
        {
            // Сессия истекла или нет прав — это не «неверный PIN», попытка не тратится.
            return new OwnerPinResult(false, false, null, null, Unauthorized: true);
        }
        catch (ApiException ex) when (ex.StatusCode == 400 && IsExplicitWrongPin(ex))
        {
            return new OwnerPinResult(false, true, null, null);
        }
        // 400 без явного указания на PIN и прочие ошибки уходят вызывающему как ApiException (общая ошибка сервера).
    }

    /// <summary>400 считается «PIN неверен» только если сервер сам назвал PIN (поле pin или код вида invalid_pin / wrong_pin / pin_invalid).</summary>
    private static bool IsExplicitWrongPin(ApiException ex)
    {
        try
        {
            if (ex.Payload is not { ValueKind: JsonValueKind.Object } p)
                return false;
            if (p.TryGetProperty("pin", out _))
                return true;
            if (p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var code = (c.GetString() ?? "").ToLowerInvariant();
                return code.Contains("pin");
            }
        }
        catch (InvalidOperationException)
        {
        }
        return false;
    }

    public async Task<JsonElement> PosLateCheckoutAsync(JsonObject body, string idempotencyKey, string? confirmToken, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        if (!string.IsNullOrWhiteSpace(confirmToken))
            headers[ConfirmTokenHeader] = confirmToken.Trim();
        var (response, _) = await _client.RequestWithHeadersAsync(HttpMethod.Post, "api/main/pos/checkout/", body, headers, ct, timeout).ConfigureAwait(false);
        return response;
    }
}
