using System.IO;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>
/// Manages saved cashier profiles in <c>accounts/accounts.db</c> for offline login.
/// </summary>
public sealed class LocalAccountsManager : ILocalAccountsStore
{
    private static readonly string AccountsDirectory =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts");

    private static readonly string DbPath = Path.Combine(AccountsDirectory, "accounts.db");

    private readonly object _initLock = new();
    private bool _initialized;

    public void EnsureSchema()
    {
        lock (_initLock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(AccountsDirectory);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS accounts (
                    email TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    password_hash TEXT NOT NULL,
                    display_name TEXT,
                    last_login_date TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public IReadOnlyList<string> GetSavedEmails()
    {
        EnsureSchema();
        var emails = new List<string>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT email
            FROM accounts
            ORDER BY last_login_date DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            emails.Add(reader.GetString(0));
        return emails;
    }

    public void PurgeLegacyCredentials()
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts;";
        command.ExecuteNonQuery();
    }

    public LocalAccountRecord? FindByEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        EnsureSchema();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT email, display_name, last_login_date
            FROM accounts
            WHERE email = $email
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$email", email.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new LocalAccountRecord
        {
            Email = reader.GetString(0),
            DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
            LastLoginDate = DateTimeOffset.Parse(reader.GetString(2)),
        };
    }

    public void Upsert(string email, string plainPassword, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        // Compatibility entry point: never persist a password verifier.
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM accounts WHERE email = $email;";
        command.Parameters.AddWithValue("$email", email.Trim());
        command.ExecuteNonQuery();
    }

    public bool ValidatePassword(string email, string plainPassword) =>
        false; // token expiration controls offline access

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }
}
