using System.Security.Cryptography;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-09-09: реализация автономного (офлайн) режима — обёртка над
/// GithubLicenseRegistryClient (активация ключа, единственное онлайн-действие) и
/// DatabaseService.LocalOwnerAccount (локальный логин/пароль, PBKDF2, без интернета).</summary>
public sealed class AutonomousAuthService : IAutonomousAuthService
{
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>2026-09-12: реальный баг с живого теста — после автономной сессии кассир снова
    /// входит в тот же реальный NurCRM-аккаунт, что и раньше, и AccountCatalogIsolation видит
    /// "userId не изменился" → каталог НЕ чистится, хотя в локальной БД уже могли накопиться
    /// товары, созданные только офлайн (сервер о них не знает). Автономный режим сам никогда не
    /// вызывает AccountCatalogIsolation (это оставляет каталог целым при ВХОДЕ в офлайн — так и
    /// задумано), поэтому единственный способ гарантировать чистый пересинк при ВОЗВРАТЕ к
    /// реальному аккаунту — "испортить" сохранённый ключ изоляции прямо здесь, при входе в
    /// автономный режим. Тогда следующий реальный вход (тем же или другим аккаунтом) всегда
    /// увидит несовпадение и обязательно пересинхронизируется с сервера.</summary>
    private const string AutonomousCatalogTaintKey = "__autonomous__";

    /// <summary>2026-09-12: тестовый мастер-ключ для автономного режима — по тому же принципу,
    /// что LicenseKeys.MasterTestSerial для остальных платных фич (голос/лояльность/табель и
    /// т.д.): не трогает реестр GitHub вообще (там реальные одноразовые ключи покупателей),
    /// просто активирует режим локально на этой машине. Нужен, потому что у автономного режима
    /// нет отдельного сервера — единственный "настоящий" способ получить ключ — вручную
    /// добавить запись в licenses/standalone-keys.json в репозитории, а это прямая запись в
    /// прод-реестр лицензий и должно делаться владельцем сознательно, а не в фоне агентом.</summary>
    public const string MasterTestActivationKey = "NMK-AUTO-TEST-MASTER";

    private readonly GithubLicenseRegistryClient _registry = new();
    private DatabaseService Db => DatabaseService.Instance;

    public bool IsActivated => UserPreferences.Instance.AutonomousModeActivated;

    public bool HasLocalAccount => Db.FindLocalOwnerAccount() is not null;

    public bool IsCurrentSessionAutonomous { get; private set; }

    public async Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default)
    {
        var trimmedKey = activationKey.Trim();
        var isMasterTestKey = string.Equals(trimmedKey, MasterTestActivationKey, StringComparison.OrdinalIgnoreCase);

        if (!isMasterTestKey)
        {
            var (success, error) = await _registry.TryActivateAsync(trimmedKey, ct).ConfigureAwait(false);
            if (!success)
                return (false, error);
        }

        var prefs = UserPreferences.Instance;
        prefs.AutonomousModeActivated = true;
        prefs.AutonomousModeActivationKey = trimmedKey;
        prefs.SaveToDisk();
        return (true, null);
    }

    public (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName)
    {
        email = email.Trim();
        if (email.Length == 0 || !email.Contains('@'))
            return (false, "Введите корректный email — он будет вашим логином.");
        if (password.Length < 4)
            return (false, "Пароль должен быть не короче 4 символов.");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Hash(password, salt);
        Db.UpsertLocalOwnerAccount(email, Convert.ToBase64String(salt), Convert.ToBase64String(hash), displayName);
        return (true, null);
    }

    public (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password)
    {
        var account = Db.FindLocalOwnerAccount(email.Trim());
        if (account is null)
            return (false, "Неверный логин или пароль.", null);

        var salt = Convert.FromBase64String(account.Value.PasswordSalt);
        var expectedHash = Convert.FromBase64String(account.Value.PasswordHash);
        var actualHash = Hash(password, salt);

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            return (false, "Неверный логин или пароль.", null);

        IsCurrentSessionAutonomous = true;
        var prefs = UserPreferences.Instance;
        prefs.AutonomousAutoResume = true;
        prefs.LastCatalogUserKey = AutonomousCatalogTaintKey;

        // 2026-09-12, по прямой просьбе пользователя: "тем более когда офлайн режим включается
        // очищай каталог" — каждый раз, когда кассир ЯВНО входит в автономный режим (не при
        // тихом продолжении уже идущей сессии через TryAutoResume после простого перезапуска
        // кассы — там это стёрло бы его же собственные, ещё не сохранённые нигде больше
        // локальные товары), локальный каталог начинается с чистого листа, а не с того, что
        // случайно осталось закэшировано от последнего реального NurCRM-аккаунта. Реальный
        // аккаунт при этом ничего не теряет — его данные живут на сервере NurCRM, а не только
        // в этом локальном кэше, и при следующем реальном входе подтянутся заново (см.
        // AccountCatalogIsolation, срабатывает по LastCatalogUserKey выше).
        //
        // 2026-09-13, по просьбе владельца ("с онлайна на офлайн переходил с базой") —
        // исключение из правила выше: если каталог был явно подготовлен для переноса (см.
        // PreserveCatalogOnNextAutonomousLogin/MigrateToOfflineDialog), не чистим его. Флаг
        // одноразовый — сбрасывается сразу после использования, обычный следующий вход снова
        // чистит каталог как раньше.
        if (prefs.PreserveCatalogOnNextAutonomousLogin)
        {
            prefs.PreserveCatalogOnNextAutonomousLogin = false;
            prefs.SaveToDisk();
        }
        else
        {
            prefs.SaveToDisk();
            LocalProductRepository.Instance.ClearAll();
        }

        return (true, null, account.Value.DisplayName ?? account.Value.Email);
    }

    public (bool Success, string? Email, string? DisplayName) TryAutoResume()
    {
        if (!UserPreferences.Instance.AutonomousAutoResume)
            return (false, null, null);

        var account = Db.FindLocalOwnerAccount();
        if (account is null)
            return (false, null, null);

        IsCurrentSessionAutonomous = true;
        var prefs = UserPreferences.Instance;
        if (prefs.LastCatalogUserKey != AutonomousCatalogTaintKey)
        {
            prefs.LastCatalogUserKey = AutonomousCatalogTaintKey;
            prefs.SaveToDisk();
        }
        return (true, account.Value.Email, account.Value.DisplayName ?? account.Value.Email);
    }

    public void EndAutonomousSession()
    {
        IsCurrentSessionAutonomous = false;
        var prefs = UserPreferences.Instance;
        if (!prefs.AutonomousAutoResume)
            return;
        prefs.AutonomousAutoResume = false;
        prefs.SaveToDisk();
    }

    public void MarkCatalogPreservedForNextLogin()
    {
        var prefs = UserPreferences.Instance;
        prefs.PreserveCatalogOnNextAutonomousLogin = true;
        prefs.SaveToDisk();
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
}
