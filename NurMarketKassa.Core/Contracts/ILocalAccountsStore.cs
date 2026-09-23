namespace NurMarketKassa.Core.Contracts;

public sealed class LocalAccountRecord
{
    public string Email { get; init; } = "";

    public string? DisplayName { get; init; }

    public DateTimeOffset LastLoginDate { get; init; }
}

/// <summary>SQLite-backed saved cashier accounts for offline login.</summary>
public interface ILocalAccountsStore
{
    void EnsureSchema();

    /// <summary>Deletes password verifiers created by legacy offline-login builds.</summary>
    void PurgeLegacyCredentials();

    IReadOnlyList<string> GetSavedEmails();

    LocalAccountRecord? FindByEmail(string email);

    void Upsert(string email, string plainPassword, string? displayName);

    bool ValidatePassword(string email, string plainPassword);
}
