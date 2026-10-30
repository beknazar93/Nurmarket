using System.Net.Http;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Models.Auth;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-30 (редактор накладных закупок, ТЗ 2.4): подтверждение правки паролем владельца. Отдельный файл — общий путь запросов не трогаем.</summary>
public sealed partial class NurMarketApiClient
{
    /// <summary>Проверить пароль аккаунта: тот же запрос входа, но ответ НЕ применяется — текущая сессия, токены и профиль не меняются.
    /// true — пароль верный; false — сервер отказал (неверный пароль); null — проверить не удалось (нет связи, сбой сервера, слишком частые попытки).</summary>
    public async Task<bool?> VerifyPasswordAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return false;
        try
        {
            var body = new LoginRequest { Email = email.Trim(), Password = password };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));
            using var resp = await _http.PostAsync("api/users/auth/login/", content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return true;
            return (int)resp.StatusCode is 400 or 401 or 403 ? false : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
