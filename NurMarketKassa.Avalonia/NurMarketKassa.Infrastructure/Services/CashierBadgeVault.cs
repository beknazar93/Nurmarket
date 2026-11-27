using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27: вход по QR-бейджу без метода сервера. Сервер выдаёт токен кассира только по паролю, поэтому касса
/// хранит вход (токены сервера) кассиров, у которых есть бейдж, — зашифрованно (DPAPI на Windows, AES-GCM на Linux/Android,
/// как auth.dat). Бейдж лишь открывает уже сохранённый вход этого кассира на ЭТОЙ кассе; сервер при входе проверяет токен
/// (обновление refresh-токена). Нет сохранённого входа или он истёк (срок refresh-токена задаёт сервер) — один раз
/// пароль. Вход сохраняется только после входа этого кассира паролем на этой кассе. Обновлённые посреди работы токены
/// подхватываются (<see cref="NurMarketApiClient.SessionTokensRefreshed"/>): сервер отзывает прежний refresh при обмене.</summary>
public sealed class CashierBadgeVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NurMarketKassa:CashierBadgeVault:v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Потолок числа сохранённых входов на кассе.</summary>
    public const int MaxEntries = 50;

    private readonly string _path;
    private readonly object _gate = new();

    public CashierBadgeVault(string path) => _path = path;

    private static CashierBadgeVault? _instance;
    private static bool _attached;

    public static CashierBadgeVault Instance =>
        _instance ??= new CashierBadgeVault(Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "cashier_badge_sessions.dat"));

    /// <summary>Подписка на обновление токенов посреди работы (один раз на процесс).</summary>
    public static void EnsureAttached()
    {
        if (_attached)
            return;
        _attached = true;
        NurMarketApiClient.SessionTokensRefreshed += (used, access, refresh) =>
        {
            try { Instance.OnTokensRefreshed(used, access, refresh); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Бейдж: обновлённые токены не сохранены ({ex.GetType().Name}).", "AUTH"); }
        };
    }

    private static bool _autoRemember;

    /// <summary>2026-11-27: касса вошла автовходом (без окна входа) или список бейджей пришёл позже входа — сохранённый вход
    /// (auth.dat) кассира с действующим бейджем кладётся сюда: сразу и при каждом получении общих настроек.</summary>
    public static void StartAutoRemember(IAuthSessionManager storage)
    {
        EnsureAttached();
        if (_autoRemember)
            return;
        _autoRemember = true;
        SettingsCloudSync.Applied += () => _ = RememberSavedAsync(storage);
        _ = RememberSavedAsync(storage);
    }

    private static async Task RememberSavedAsync(IAuthSessionManager storage)
    {
        try
        {
            PruneRevoked();
            var session = await storage.LoadSessionAsync().ConfigureAwait(false);
            CashierBadgeLogin.RememberAfterPasswordLogin(session, null, UserPreferences.Instance.CashierBadgesJson, Instance, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Бейдж: сохранённый вход не прочитан ({ex.GetType().Name}).", "AUTH");
        }
    }

    /// <summary>Отозванные/истёкшие бейджи — их сохранённые входы стираются (только если список бейджей уже приходил на кассу).</summary>
    public static void PruneRevoked()
    {
        try
        {
            var json = UserPreferences.Instance.CashierBadgesJson;
            if (json is null)
                return;
            var now = DateTimeOffset.UtcNow;
            var dropped = Instance.RetainOnly(CashierBadgeRegistry.Parse(json).Where(r => r.IsActive(now)).Select(r => r.EmployeeId));
            if (dropped > 0)
                PosLogger.Log($"Вход по бейджу: стёрто сохранённых входов без действующего бейджа: {dropped}.", "AUTH");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Вход по бейджу: чистка сохранённых входов не удалась ({ex.GetType().Name}).", "AUTH");
        }
    }

    public bool Has(string? userId) => !string.IsNullOrWhiteSpace(userId) && TryGet(userId) is not null;

    public UserSession? TryGet(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;
        lock (_gate)
            return Read().TryGetValue(userId.Trim(), out var s) ? s : null;
    }

    public void Save(UserSession session)
    {
        if (string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.RefreshToken) || string.IsNullOrWhiteSpace(session.AccessToken))
            return;
        lock (_gate)
        {
            var all = Read();
            all[session.UserId.Trim()] = session;
            if (all.Count > MaxEntries)
                foreach (var old in all.OrderBy(kv => kv.Value.LastOnlineContactAt ?? DateTimeOffset.MinValue).Take(all.Count - MaxEntries).Select(kv => kv.Key).ToList())
                    all.Remove(old);
            Write(all);
        }
    }

    public bool Remove(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;
        lock (_gate)
        {
            var all = Read();
            if (!all.Remove(userId.Trim()))
                return false;
            Write(all);
            return true;
        }
    }

    /// <summary>Оставить входы только тех, у кого есть действующий бейдж (отзыв владельцем стирает и сохранённый вход).</summary>
    public int RetainOnly(IEnumerable<string> employeeIds)
    {
        var keep = new HashSet<string>(employeeIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var all = Read();
            var drop = all.Keys.Where(k => !keep.Contains(k)).ToList();
            if (drop.Count == 0)
                return 0;
            foreach (var k in drop)
                all.Remove(k);
            Write(all);
            return drop.Count;
        }
    }

    public int Count
    {
        get { lock (_gate) return Read().Count; }
    }

    internal void OnTokensRefreshed(string? usedRefresh, string? access, string? refresh)
    {
        if (string.IsNullOrWhiteSpace(usedRefresh) || string.IsNullOrWhiteSpace(access))
            return;
        lock (_gate)
        {
            var all = Read();
            var hit = all.FirstOrDefault(kv => string.Equals(kv.Value.RefreshToken, usedRefresh, StringComparison.Ordinal));
            if (hit.Value is null)
                return;
            var s = hit.Value;
            all[hit.Key] = new UserSession
            {
                AccessToken = access!,
                RefreshToken = string.IsNullOrWhiteSpace(refresh) ? s.RefreshToken : refresh!,
                // Срок доступа — прежний (не позже настоящего): при входе по бейджу касса обновит токен сама.
                ExpiresAt = s.ExpiresAt,
                UserId = s.UserId,
                Login = s.Login,
                DisplayName = s.DisplayName,
                Role = s.Role,
                BranchId = s.BranchId,
                Permissions = s.Permissions,
                LastOnlineContactAt = DateTimeOffset.UtcNow,
                PasswordLoginAt = s.PasswordLoginAt,
            };
            Write(all);
        }
    }

    private Dictionary<string, UserSession> Read()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, UserSession>(StringComparer.OrdinalIgnoreCase);
        byte[] plain = [];
        try
        {
            plain = PortableSecret.Unprotect(File.ReadAllBytes(_path), Entropy);
            var map = JsonSerializer.Deserialize<Dictionary<string, UserSession>>(plain, JsonOptions);
            return new Dictionary<string, UserSession>(map ?? new Dictionary<string, UserSession>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Файл с другого ПК/пользователя или испорчен — как «входов нет» (бейджу понадобится пароль один раз).
            PosLogger.Log($"Бейдж: сохранённые входы кассиров не прочитаны ({ex.GetType().Name}) — сброшены.", "AUTH");
            TryDelete();
            return new Dictionary<string, UserSession>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (plain.Length > 0)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(Dictionary<string, UserSession> all)
    {
        if (all.Count == 0)
        {
            TryDelete();
            return;
        }
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var plain = JsonSerializer.SerializeToUtf8Bytes(all, JsonOptions);
        byte[] secret = [];
        try
        {
            secret = PortableSecret.Protect(plain, Entropy);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, secret);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (secret.Length > 0)
                CryptographicOperations.ZeroMemory(secret);
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Бейдж: файл входов не удалён ({ex.GetType().Name}).", "AUTH");
        }
    }
}
