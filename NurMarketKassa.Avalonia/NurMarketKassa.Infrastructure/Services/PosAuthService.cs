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
            return AuthResult.Failure(Tr.T("Введите логин и пароль.", "Логин менен сырсөздү киргизиңиз.", "Enter your login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting."));

        try
        {
            await _authService.LoginOnlineAsync(email, password, cancellationToken).ConfigureAwait(false);
            await _authService.PersistOfflineSessionAsync(email, cancellationToken).ConfigureAwait(false);

            var session = _authService.TryLoadOfflineSession();
            var userId = session?.UserId;
            if (string.IsNullOrWhiteSpace(userId))
                return AuthResult.Failure(Tr.T("Не удалось получить идентификатор пользователя.",
                    "Колдонуучунун идентификаторун алуу мүмкүн болгон жок.", "Could not get the user ID.",
                    "Kullanıcı kimliği alınamadı.", "Foydalanuvchi identifikatorini olib bo'lmadi."));

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
                string.IsNullOrWhiteSpace(ex.Message)
                    ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.")
                    : ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Failure(Tr.T("Превышено время ожидания.", "Күтүү убактысы бүттү.",
                "The request timed out.", "Bekleme süresi aşıldı.", "Kutish vaqti tugadi."));
        }
    }
}
