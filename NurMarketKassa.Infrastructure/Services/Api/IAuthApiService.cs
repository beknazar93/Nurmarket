using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис авторизации: вход/выход кассиров, токены сессии, профиль и компания.
/// </summary>
public interface IAuthApiService
{
    /// <summary>Текущий access-токен сессии (Bearer).</summary>
    string? AccessToken { get; }

    /// <summary>Текущий refresh-токен сессии.</summary>
    string? RefreshToken { get; }

    /// <summary>Полезная нагрузка пользователя из ответа логина/профиля.</summary>
    JsonElement UserPayload { get; }

    /// <summary>Активный филиал (для query branch=…).</summary>
    string? ActiveBranchId { get; }

    /// <summary>Проверка доступности API.</summary>
    Task<bool> CanReachApiAsync(CancellationToken ct = default);

    /// <summary>Вход кассира (POST /api/users/auth/login/).</summary>
    Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Ручное обновление access-токена по refresh-токену.</summary>
    Task<bool> RefreshAccessAsync(CancellationToken ct = default);

    /// <summary>Быстрый вход по refresh-токену из DPAPI (без полного POST login).</summary>
    Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default);

    /// <summary>Выход: очистка токенов и данных сессии.</summary>
    void ClearSession();

    /// <summary>Восстановление сессии из локального кэша (офлайн-вход).</summary>
    void RestoreOfflineSession(OfflineAuthSession session);

    /// <summary>GET /api/users/profile/</summary>
    Task<JsonElement> GetProfileAsync(CancellationToken ct = default);

    /// <summary>GET /api/users/company/</summary>
    Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default);

    /// <summary>Применить филиал из профиля (если в JWT его нет).</summary>
    void ApplyBranchFromProfile(JsonElement profile);

    /// <summary>Подставить пользователя из профиля (если login вернул только токены).</summary>
    void ApplyUserFromProfile(JsonElement profile);

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default);
}
