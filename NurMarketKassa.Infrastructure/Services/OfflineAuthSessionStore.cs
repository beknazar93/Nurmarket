using System.IO;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Локально сохранённая сессия после успешной онлайн-авторизации.</summary>
public sealed class OfflineAuthSession
{
    public string UserId { get; set; } = "";

    public string Login { get; set; } = "";

    public string CashierName { get; set; } = "";

    public string? Role { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public string? BranchId { get; set; }

    public string? CompanyInn { get; set; }

    public string? StoreInn { get; set; }

    public string? CompanyAddress { get; set; }

    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset LastAuthAt { get; set; } = DateTimeOffset.Now;
}

public static class OfflineAuthSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object SyncRoot = new();

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_auth_session.dat");

    private static string LegacyJsonFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_auth_session.json");

    public static void SaveFromApi(IAuthApiService authApi, string loginEmail)
    {
        var session = new OfflineAuthSession
        {
            Login = loginEmail.Trim(),
            AccessToken = authApi.AccessToken,
            RefreshToken = authApi.RefreshToken,
            BranchId = authApi.ActiveBranchId,
            LastAuthAt = DateTimeOffset.Now,
        };

        ExtractUserFields(authApi.UserPayload, session);
        session.StoreInn = UserPreferences.Instance.StoreInn;
        session.CompanyInn = session.StoreInn;
        session.CompanyAddress = UserPreferences.Instance.StoreAddress;
        Write(session);
    }

    public static OfflineAuthSession? TryLoad()
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(FilePath))
                    return TryLoadFromDat();

                if (!File.Exists(LegacyJsonFilePath))
                    return null;

                // Legacy migration path: read old JSON once and immediately rewrite to encrypted .dat.
                var legacySession = TryLoadFromLegacyJson();
                if (legacySession == null)
                    return null;

                Write(legacySession);
                TryDeleteLegacyJson();
                return legacySession;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Legacy auth session load failed: {ex.GetType().Name}", "WARNING");
                return null;
            }
        }
    }

    public static void UpdateTokens(string? accessToken, string? refreshToken)
    {
        lock (SyncRoot)
        {
            var session = TryLoadFromDatInternal();
            if (session == null)
                return;

            if (!string.IsNullOrWhiteSpace(accessToken))
                session.AccessToken = accessToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
                session.RefreshToken = refreshToken;
            session.LastAuthAt = DateTimeOffset.Now;
            WriteUnlocked(session);
        }
    }

    public static bool IsUsable(OfflineAuthSession? session) =>
        session != null
        && !string.IsNullOrWhiteSpace(session.Login)
        && !string.IsNullOrWhiteSpace(session.AccessToken)
        && !string.IsNullOrWhiteSpace(session.CashierName);

    public static void Clear()
    {
        lock (SyncRoot)
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                if (File.Exists(LegacyJsonFilePath))
                    File.Delete(LegacyJsonFilePath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Legacy auth session cleanup failed: {ex.GetType().Name}", "WARNING");
            }
        }
    }

    public static void UpdateCompanyData(string? storeInn, string? companyAddress)
    {
        lock (SyncRoot)
        {
            var session = TryLoad();
            if (session == null)
                return;

            session.StoreInn = string.IsNullOrWhiteSpace(storeInn) ? null : storeInn.Trim();
            session.CompanyInn = session.StoreInn;
            session.CompanyAddress = string.IsNullOrWhiteSpace(companyAddress) ? null : companyAddress.Trim();
            Write(session);
        }
    }

    private static void ExtractUserFields(JsonElement user, OfflineAuthSession session)
    {
        var source = ResolveUserObject(user);
        if (source.ValueKind != JsonValueKind.Object)
            return;

        session.UserId = ReadScalar(source, "id", "pk", "uuid", "user_id") ?? "";
        session.Role = ReadScalar(source, "role", "user_role", "position");
        session.CashierName =
            ReadScalar(source, "full_name", "name", "username", "email") ?? session.Login;
        session.Permissions = PermissionService.ExtractPermissionNames(user).ToList();
    }

    /// <summary>
    /// Ответ login/profile может быть плоским или обёрнут в user/profile/data.
    /// </summary>
    internal static JsonElement ResolveUserObject(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return default;

        if (HasIdentityField(payload))
            return payload;

        foreach (var key in new[] { "user", "profile", "data", "cashier" })
        {
            if (!payload.TryGetProperty(key, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            if (HasIdentityField(nested) || HasNameField(nested))
                return nested;
        }

        return payload;
    }

    internal static string? TryExtractUserId(JsonElement payload)
    {
        var source = ResolveUserObject(payload);
        return source.ValueKind == JsonValueKind.Object
            ? ReadScalar(source, "id", "pk", "uuid", "user_id")
            : null;
    }

    private static bool HasIdentityField(JsonElement obj) =>
        ReadScalar(obj, "id", "pk", "uuid", "user_id") != null;

    private static bool HasNameField(JsonElement obj) =>
        ReadScalar(obj, "full_name", "name", "username", "email") != null;

    private static string? ReadScalar(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v))
                continue;

            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var s = v.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                    break;
                }
                case JsonValueKind.Number:
                    return v.GetRawText();
            }
        }

        return null;
    }

    private static void Write(OfflineAuthSession session)
    {
        lock (SyncRoot)
        {
            WriteUnlocked(session);
        }
    }

    private static void WriteUnlocked(OfflineAuthSession session)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(session, JsonOpts);
        var encrypted = WindowsDpapiHelper.ProtectToBase64(json);
        File.WriteAllText(tmp, encrypted);
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, null);
        else
            File.Move(tmp, FilePath);
    }

    private static OfflineAuthSession? TryLoadFromDatInternal()
    {
        if (File.Exists(FilePath))
            return TryLoadFromDat();

        if (File.Exists(LegacyJsonFilePath))
            return TryLoadFromLegacyJson();

        return null;
    }

    private static OfflineAuthSession? TryLoadFromDat()
    {
        var encryptedText = File.ReadAllText(FilePath);
        var json = WindowsDpapiHelper.UnprotectFromBase64(encryptedText);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<OfflineAuthSession>(json, JsonOpts);
    }

    private static OfflineAuthSession? TryLoadFromLegacyJson()
    {
        var json = File.ReadAllText(LegacyJsonFilePath);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<OfflineAuthSession>(json, JsonOpts);
    }

    private static void TryDeleteLegacyJson()
    {
        try
        {
            if (File.Exists(LegacyJsonFilePath))
                File.Delete(LegacyJsonFilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Legacy auth JSON cleanup failed: {ex.GetType().Name}", "WARNING");
        }
    }
}
