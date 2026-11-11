using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Ошибка ответа API (аналог api_client.ApiError).</summary>
public class ApiException : Exception // 2026-11-11: не sealed — AmbiguousBarcodeException наследует (сохраняет все прежние проверки по ApiException/StatusCode)
{
    public int? StatusCode { get; }
    public JsonElement? Payload { get; }

    public ApiException(string message, int? statusCode = null, JsonElement? payload = null)
        : base(message)
    {
        StatusCode = statusCode;
        Payload = payload;
    }
}
