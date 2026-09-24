using System.Net.Http;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Coordinates server authentication, token refresh and the strictly limited
/// offline fallback. Server rejection never becomes an offline login.
/// </summary>
public sealed class OnlineOfflineAuthenticationService : IOnlineOfflineAuthenticationService
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly IAuthApiService _api;
    private readonly IAuthSessionManager _storage;

    public OnlineOfflineAuthenticationService(IAuthApiService api, IAuthSessionManager storage)
    {
        _api = api;
        _storage = storage;
    }

    public async Task<AuthenticationResult> LoginAsync(
        string username,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0)
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.InvalidCredentials,
                "Введите логин и пароль.");
        }

        try
        {
            var loginPayload = await _api.LoginAsync(username, password, cancellationToken)
                .ConfigureAwait(false);
            var profile = await LoadAndApplyProfileAsync(cancellationToken).ConfigureAwait(false);
            var session = CreateSession(username, loginPayload, profile);

            // Never keep a second token copy in the legacy session file.
            OfflineAuthSessionStore.Clear();

            if (rememberMe)
                await _storage.SaveSessionAsync(session).ConfigureAwait(false);
            else
                await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            return AuthenticationResult.Success(session, AuthenticationMode.Online);
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            _api.ClearSession();
            return AuthenticationResult.Failed(
                AuthenticationFailure.InvalidCredentials,
                "Неверный логин или пароль.");
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.NetworkUnavailable,
                "Нет связи с сервером. Первый вход требует интернет-соединения.");
        }
        catch (ApiException)
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.ServerError,
                "Сервер временно недоступен. Повторите попытку позже.");
        }
    }

    public async Task<AuthenticationResult> AutoLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var saved = await _storage.LoadSessionAsync().ConfigureAwait(false);
        if (saved is null)
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.SessionExpired,
                "Сохранённая сессия отсутствует.");
        }

        _api.RestoreOfflineSession(ToLegacySession(saved));

        try
        {
            // Refresh first when the local JWT lifetime has ended. An expired
            // token is never accepted offline, even if the network is down.
            if (!IsLocallyValid(saved))
            {
                if (!await _api.RefreshAccessAsync(cancellationToken).ConfigureAwait(false))
                    return await RejectSavedSessionAsync("Сессия истекла. Войдите снова.", cancellationToken).ConfigureAwait(false);

                var refreshedProfile = await LoadAndApplyProfileAsync(cancellationToken).ConfigureAwait(false);
                var refreshed = CreateSession(saved.Login, default, refreshedProfile, saved);
                await _storage.SaveSessionAsync(refreshed).ConfigureAwait(false);
                return AuthenticationResult.Success(refreshed, AuthenticationMode.Online);
            }

            // This request is the authoritative server-side validation.
            var profile = await LoadAndApplyProfileAsync(cancellationToken).ConfigureAwait(false);
            var validated = CreateSession(saved.Login, default, profile, saved);
            await _storage.SaveSessionAsync(validated).ConfigureAwait(false);
            return AuthenticationResult.Success(validated, AuthenticationMode.Online);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            try
            {
                if (!await _api.RefreshAccessAsync(cancellationToken).ConfigureAwait(false))
                    return await RejectSavedSessionAsync("Сессия отозвана. Войдите снова.", cancellationToken).ConfigureAwait(false);

                var profile = await LoadAndApplyProfileAsync(cancellationToken).ConfigureAwait(false);
                var refreshed = CreateSession(saved.Login, default, profile, saved);
                await _storage.SaveSessionAsync(refreshed).ConfigureAwait(false);
                return AuthenticationResult.Success(refreshed, AuthenticationMode.Online);
            }
            catch (Exception refreshError) when (IsNetworkFailure(refreshError, cancellationToken))
            {
                return await OfflineOrExpiredAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException)
            {
                return await RejectSavedSessionAsync("Сессия отозвана. Войдите снова.", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            return await RejectSavedSessionAsync("Доступ к учётной записи отозван. Войдите снова.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            return await OfflineOrExpiredAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // A reachable server returning 5xx is not proof that credentials are
            // invalid, but it is also not a "no internet" condition.
            return AuthenticationResult.Failed(
                AuthenticationFailure.ServerError,
                "Сервер временно недоступен. Автономный вход разрешён только при сетевой ошибке.");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        OfflineAuthSessionStore.Clear(); // remove the pre-auth.dat session format
        _api.ClearSession();
    }

    private async Task<JsonElement> LoadAndApplyProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _api.GetProfileAsync(cancellationToken).ConfigureAwait(false);
        _api.ApplyUserFromProfile(profile);
        _api.ApplyBranchFromProfile(profile);
        return profile;
    }

    private async Task<AuthenticationResult> OfflineOrExpiredAsync(
        UserSession session,
        CancellationToken cancellationToken)
    {
        if (!IsLocallyValid(session))
            return await RejectSavedSessionAsync(
                "Сессия истекла. Для входа подключитесь к интернету.",
                cancellationToken).ConfigureAwait(false);

        // 2026-09-09: 60-часовой потолок офлайн-работы для ОБЫЧНОГО (не автономного) режима —
        // владелец явно попросил остановить офлайн-работу после 60 часов подряд без связи с
        // сервером, а не разрешать её бессрочно (как было раньше — единственная проверка тут
        // была на срок жизни JWT, который может быть куда длиннее). Автономный режим
        // (LocalAuthApiService) этой проверке не подчиняется — там офлайн-работа без лимита,
        // это его смысл.
        if (!IsWithinOfflineGracePeriod(session))
            return AuthenticationResult.Failed(
                AuthenticationFailure.NetworkUnavailable,
                "Автономная работа без интернета ограничена 60 часами. Подключитесь к интернету, чтобы продолжить.");

        _api.RestoreOfflineSession(ToLegacySession(session));
        return AuthenticationResult.Success(session, AuthenticationMode.Offline);
    }

    private static bool IsWithinOfflineGracePeriod(UserSession session) =>
        session.LastOnlineContactAt is not { } last
        || DateTimeOffset.UtcNow - last <= OfflineAuthSessionStore.MaxOfflineDuration;

    private async Task<AuthenticationResult> RejectSavedSessionAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        _api.ClearSession();
        return AuthenticationResult.Failed(AuthenticationFailure.SessionExpired, message);
    }

    private static bool IsLocallyValid(UserSession session) =>
        session.ExpiresAt > DateTimeOffset.UtcNow.Add(ClockSkew);

    private UserSession CreateSession(
        string login,
        JsonElement loginPayload,
        JsonElement profile,
        UserSession? fallback = null)
    {
        var accessToken = _api.AccessToken ?? fallback?.AccessToken ?? "";
        var refreshToken = _api.RefreshToken ?? fallback?.RefreshToken ?? "";
        var user = OfflineAuthSessionStore.ResolveUserObject(profile.ValueKind == JsonValueKind.Object
            ? profile
            : _api.UserPayload);

        var userId = ReadString(user, "id", "pk", "uuid", "user_id") ?? fallback?.UserId ?? "";
        // 2026-09-24, живой случай: в меню «Кассир —». Сервер отдаёт имя в first_name/last_name
        // («Кассир Тест»), а их здесь не читали; при неудачной загрузке профиля имя бралось из
        // прошлой сессии, и пустая строка оттуда перекрывала запасной вариант — логин.
        var firstLast = string.Join(" ", new[] { ReadString(user, "first_name"), ReadString(user, "last_name") }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var displayName = ReadString(user, "full_name", "name")
            ?? (firstLast.Length > 0 ? firstLast : null)
            ?? ReadString(user, "username", "email")
            ?? (string.IsNullOrWhiteSpace(fallback?.DisplayName) ? null : fallback!.DisplayName)
            ?? login;
        var role = ReadString(user, "role", "user_role", "position") ?? fallback?.Role ?? "";
        var expiresAt = ReadExpiration(loginPayload)
            ?? ReadJwtExpiration(accessToken)
            ?? throw new JsonException("The server did not provide a valid token expiration.");

        var permissions = PermissionService.ExtractPermissionNames(
            profile.ValueKind == JsonValueKind.Object ? profile : _api.UserPayload);
        if (permissions.Count == 0 && fallback is not null)
            permissions.UnionWith(fallback.Permissions);

        return new UserSession
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt.ToUniversalTime(),
            UserId = userId,
            Login = login,
            DisplayName = displayName,
            Role = role,
            BranchId = _api.ActiveBranchId ?? fallback?.BranchId,
            Permissions = permissions.ToList(),
            // 2026-09-09: CreateSession вызывается ТОЛЬКО после реального успешного обращения к
            // серверу (логин или онлайн-валидация в AutoLoginAsync) — значит связь только что
            // подтверждена, независимо от того, что там с ExpiresAt.
            LastOnlineContactAt = DateTimeOffset.UtcNow,
        };
    }

    private static DateTimeOffset? ReadExpiration(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "expiresAt", "expires_at", "expires" })
        {
            if (!payload.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var date))
                return date;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    private static DateTimeOffset? ReadJwtExpiration(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                return null;
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement source, params string[] names)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var result = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(result))
                    return result;
            }
        }
        return null;
    }

    private static OfflineAuthSession ToLegacySession(UserSession session) => new()
    {
        AccessToken = session.AccessToken,
        RefreshToken = session.RefreshToken,
        UserId = session.UserId,
        Login = session.Login,
        CashierName = session.DisplayName,
        Role = session.Role,
        BranchId = session.BranchId,
        Permissions = session.Permissions.ToList(),
        LastAuthAt = DateTimeOffset.UtcNow,
    };

    private static bool IsNetworkFailure(Exception exception, CancellationToken callerToken) =>
        exception is HttpRequestException ||
        exception is TaskCanceledException && !callerToken.IsCancellationRequested;
}
