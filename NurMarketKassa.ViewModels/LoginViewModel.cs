using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels;

/// <summary>
/// Compatibility name used by the existing login views. The implementation is
/// the shared WPF/Avalonia <see cref="AuthViewModel"/>.
/// </summary>
public sealed class LoginViewModel : AuthViewModel
{
    public LoginViewModel(
        IOnlineOfflineAuthenticationService authentication,
        IAppSession appSession)
        : base(authentication, appSession)
    {
    }
}
