using NurMarketKassa.Models.Local;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Авторизация, «Запомнить меня» и офлайн-вход.</summary>
public sealed class AuthService
{
    private readonly DatabaseService _database;
    private readonly IAuthApiService _authApi;

    public AuthService(DatabaseService database, IAuthApiService authApi)
    {
        _database = database;
        _authApi = authApi;
    }

    public LocalUserRecord? TryGetLastRememberedUser() => _database.TryGetLastRememberedUser();

    public void SaveRememberedCredentials(string email, string password, bool rememberMe, string? userId = null, string? cashierName = null)
    {
        _database.SaveRememberedUser(email, password, rememberMe, userId, cashierName);
    }

    public bool ValidateOfflineCredentials(string email, string password) =>
        _database.ValidateOfflineCredentials(email, password);

    public Task<bool> CheckInternetAsync(CancellationToken ct = default) =>
        _authApi.CanReachApiAsync(ct);

    public async Task<bool> LoginOnlineAsync(string email, string password, CancellationToken ct = default)
    {
        var sessionRestored = await _authApi.TryRestoreSessionViaRefreshAsync(email, ct).ConfigureAwait(false);
        if (!sessionRestored)
            await _authApi.LoginAsync(email, password, ct).ConfigureAwait(false);

        await EnsureUserPayloadAsync(ct).ConfigureAwait(false);
        return true;
    }

    public void RestoreOfflineSession(OfflineAuthSession session)
    {
        _authApi.RestoreOfflineSession(session);
    }

    public OfflineAuthSession? TryLoadOfflineSession() => OfflineAuthSessionStore.TryLoad();

    public bool IsOfflineSessionUsable(OfflineAuthSession? session) =>
        OfflineAuthSessionStore.IsUsable(session);

    public void PersistOfflineSession(string email)
    {
        OfflineAuthSessionStore.SaveFromApi(_authApi, email);
    }

    /// <summary>
    /// Сохраняет офлайн-сессию; при отсутствии user id догружает GET /api/users/profile/.
    /// </summary>
    public async Task PersistOfflineSessionAsync(string email, CancellationToken ct = default)
    {
        await EnsureUserPayloadAsync(ct).ConfigureAwait(false);
        OfflineAuthSessionStore.SaveFromApi(_authApi, email);
    }

    private async Task EnsureUserPayloadAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(OfflineAuthSessionStore.TryExtractUserId(_authApi.UserPayload)))
            return;

        var profile = await _authApi.GetProfileAsync(ct).ConfigureAwait(false);
        _authApi.ApplyUserFromProfile(profile);
        _authApi.ApplyBranchFromProfile(profile);
    }
}
