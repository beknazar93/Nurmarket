using System.Net.Http;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Production <see cref="IAuthService"/> backed by <see cref="AuthService"/>.</summary>
public sealed class PosAuthService : IAuthService
{
    private readonly AuthService _authService;

    public PosAuthService(AuthService authService) => _authService = authService;

    public async Task<AuthResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var email = username.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            return AuthResult.Failure("Введите логин и пароль.");

        try
        {
            await _authService.LoginOnlineAsync(email, password, cancellationToken).ConfigureAwait(false);
            await _authService.PersistOfflineSessionAsync(email, cancellationToken).ConfigureAwait(false);

            var session = _authService.TryLoadOfflineSession();
            var userId = session?.UserId;
            if (string.IsNullOrWhiteSpace(userId))
                return AuthResult.Failure("Не удалось получить идентификатор пользователя.");

            return AuthResult.Success(
                userId,
                posCashboxDisplayName: session?.CashierName);
        }
        catch (ApiException ex)
        {
            return AuthResult.Failure(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return AuthResult.Failure(
                string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Failure("Превышено время ожидания.");
        }
    }
}
