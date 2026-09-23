namespace NurMarketKassa.Models.Local;

/// <summary>Запись пользователя для «Запомнить меня» и офлайн-входа.</summary>
public sealed class LocalUserRecord
{
    public long Id { get; set; }

    public string Email { get; set; } = "";

    public string PasswordEncrypted { get; set; } = "";

    public bool RememberMe { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public string? UserId { get; set; }

    public string? CashierName { get; set; }
}
