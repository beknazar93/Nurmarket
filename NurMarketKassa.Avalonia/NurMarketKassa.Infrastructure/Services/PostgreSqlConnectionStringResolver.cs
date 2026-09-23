using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public static class PostgreSqlConnectionStringResolver
{
    private const string PasswordPlaceholder = "YOUR_SECURE_PASSWORD";
    private static readonly object SyncRoot = new();

    public static PostgreSqlSettings ResolveRuntimeSettings(PostgreSqlSettings configured, UserPreferences preferences)
    {
        var encrypted = preferences.PostgreSqlConnectionStringEncrypted;
        if (!string.IsNullOrWhiteSpace(encrypted))
        {
            var decrypted = WindowsDpapiHelper.UnprotectFromBase64(encrypted);
            if (!string.IsNullOrWhiteSpace(decrypted))
            {
                return new PostgreSqlSettings
                {
                    Enabled = configured.Enabled,
                    ConnectionString = decrypted,
                    CommandTimeoutSeconds = configured.CommandTimeoutSeconds,
                };
            }
        }

        return configured;
    }

    public static void PersistIfNeeded(string? runtimeConnectionString, UserPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(runtimeConnectionString))
            return;

        if (ContainsPasswordPlaceholder(runtimeConnectionString))
            return;

        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(preferences.PostgreSqlConnectionStringEncrypted))
                return;

            preferences.PostgreSqlConnectionStringEncrypted =
                WindowsDpapiHelper.ProtectToBase64(runtimeConnectionString);
            preferences.SaveToDisk();
        }
    }

    private static bool ContainsPasswordPlaceholder(string connectionString) =>
        connectionString.Contains(PasswordPlaceholder, StringComparison.OrdinalIgnoreCase);
}
