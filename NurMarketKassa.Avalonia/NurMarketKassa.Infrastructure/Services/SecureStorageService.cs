using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>
/// Stores the session in %AppData%/NurMarketKassa/auth.dat, encrypted for the
/// current Windows user. A copied or corrupt file is treated as signed out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureStorageService : IAuthSessionManager
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("NurMarketKassa:AuthSession:v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public SecureStorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "auth.dat"))
    {
    }

    // Useful for isolated tests; production DI uses the parameterless constructor.
    internal SecureStorageService(string filePath) => _filePath = filePath;

    public async Task SaveSessionAsync(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(session);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Auth storage path has no directory.");
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(session, JsonOptions);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plainBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }

            var temporaryPath = _filePath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes).ConfigureAwait(false);
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UserSession?> LoadSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
                return null;

            byte[] protectedBytes = [];
            byte[] plainBytes = [];
            try
            {
                protectedBytes = await File.ReadAllBytesAsync(_filePath).ConfigureAwait(false);
                plainBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);

                var session = JsonSerializer.Deserialize<UserSession>(plainBytes, JsonOptions);
                if (session is null)
                    throw new JsonException("The auth session is empty.");
                Validate(session);
                return session;
            }
            catch (CryptographicException)
            {
                // DPAPI cannot decrypt data copied from another user/computer.
                TryDelete(_filePath);
                return null;
            }
            catch (JsonException)
            {
                TryDelete(_filePath);
                return null;
            }
            finally
            {
                if (protectedBytes.Length > 0)
                    CryptographicOperations.ZeroMemory(protectedBytes);
                if (plainBytes.Length > 0)
                    CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TryDelete(_filePath);
            TryDelete(_filePath + ".tmp");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(UserSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(session.RefreshToken) ||
            string.IsNullOrWhiteSpace(session.UserId) ||
            string.IsNullOrWhiteSpace(session.Login) ||
            session.ExpiresAt == default)
        {
            throw new JsonException("The auth session is incomplete.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            PosLogger.Log($"Secure storage cleanup failed: {ex.GetType().Name}", "WARNING");
        }
        catch (UnauthorizedAccessException ex)
        {
            PosLogger.Log($"Secure storage cleanup denied: {ex.GetType().Name}", "WARNING");
        }
    }
}
