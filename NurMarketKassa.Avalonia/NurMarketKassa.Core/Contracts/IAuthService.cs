namespace NurMarketKassa.Core.Contracts;

/// <summary>Cross-platform cashier authentication.</summary>
public interface IAuthService
{
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
}
