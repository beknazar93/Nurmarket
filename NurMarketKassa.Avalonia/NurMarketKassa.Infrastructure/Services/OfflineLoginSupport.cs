using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

public sealed class OfflineLoginSupport : IOfflineLoginSupport
{
    private readonly AuthService _authService;

    public OfflineLoginSupport(AuthService authService) => _authService = authService;

    public OfflineSessionBootstrap? TryBootstrapOfflineSession(string email)
    {
        var session = _authService.TryLoadOfflineSession();
        if (!_authService.IsOfflineSessionUsable(session))
            return null;

        if (!string.Equals(session!.Login, email.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        _authService.RestoreOfflineSession(session);
        return new OfflineSessionBootstrap
        {
            UserId = session.UserId,
            DisplayName = session.CashierName,
            OfflineMessage =
                $"Оффлайн режим. Последний вход: {session.CashierName} ({session.LastAuthAt.LocalDateTime:dd.MM.yyyy HH:mm}).",
        };
    }
}
