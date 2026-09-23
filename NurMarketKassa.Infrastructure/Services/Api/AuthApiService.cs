using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация авторизации поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// Состояние токенов хранится в транспорте, так как используется всеми доменными сервисами.
/// </summary>
public sealed class AuthApiService : IAuthApiService
{
    private readonly NurMarketApiClient _client;

    public AuthApiService(NurMarketApiClient client) => _client = client;

    public string? AccessToken => _client.AccessToken;

    public string? RefreshToken => _client.RefreshToken;

    public JsonElement UserPayload => _client.UserPayload;

    public string? ActiveBranchId => _client.ActiveBranchId;

    public Task<bool> CanReachApiAsync(CancellationToken ct = default) =>
        _client.CanReachApiAsync(ct);

    public Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default) =>
        _client.LoginAsync(email, password, ct);

    public Task<bool> RefreshAccessAsync(CancellationToken ct = default) =>
        _client.RefreshAccessAsync(ct);

    public Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default) =>
        _client.TryRestoreSessionViaRefreshAsync(email, ct);

    public void ClearSession() => _client.ClearSession();

    public void RestoreOfflineSession(OfflineAuthSession session) =>
        _client.RestoreOfflineSession(session);

    public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) =>
        _client.GetProfileAsync(ct);

    public Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default) =>
        _client.GetCompanyAsync(ct);

    public void ApplyBranchFromProfile(JsonElement profile) =>
        _client.ApplyBranchFromProfile(profile);

    public void ApplyUserFromProfile(JsonElement profile) =>
        _client.ApplyUserFromProfile(profile);

    public Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default) =>
        _client.DownloadAuthorizedAsync(absoluteUrl, ct);
}
