namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Persisted authentication state. Deliberately contains no password field.
/// All timestamps are UTC to avoid daylight-saving and time-zone errors.
/// </summary>
public sealed class UserSession
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string UserId { get; init; } = "";
    public string Login { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "";
    public string? BranchId { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public interface IAuthSessionManager
{
    Task SaveSessionAsync(UserSession session);
    Task<UserSession?> LoadSessionAsync();
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
}

public enum AuthenticationMode
{
    Online,
    Offline,
}

public enum AuthenticationFailure
{
    None,
    InvalidCredentials,
    SessionExpired,
    NetworkUnavailable,
    ServerError,
}

public sealed class AuthenticationResult
{
    public bool IsSuccess { get; init; }
    public AuthenticationMode Mode { get; init; }
    public AuthenticationFailure Failure { get; init; }
    public UserSession? Session { get; init; }
    public string? ErrorMessage { get; init; }

    public static AuthenticationResult Success(UserSession session, AuthenticationMode mode) =>
        new() { IsSuccess = true, Session = session, Mode = mode };

    public static AuthenticationResult Failed(AuthenticationFailure failure, string message) =>
        new() { Failure = failure, ErrorMessage = message };
}

/// <summary>Application-level online/offline authentication workflow.</summary>
public interface IOnlineOfflineAuthenticationService
{
    Task<AuthenticationResult> LoginAsync(
        string username,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken = default);

    Task<AuthenticationResult> AutoLoginAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
