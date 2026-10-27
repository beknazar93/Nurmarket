using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurRemoteSupport.Server;

/// <summary>Оператор (сотрудник NurMarket). Пароль хранится только как хэш PBKDF2-SHA256.</summary>
public sealed class OperatorRecord
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>pbkdf2-sha256$итерации$соль(base64)$хэш(base64)</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; }

    /// <summary>Отпечаток пароля для токена: смена пароля делает старые токены недействительными.</summary>
    [JsonIgnore]
    public string Stamp
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Hash));
            return Convert.ToHexString(bytes, 0, 6);
        }
    }
}

/// <summary>
/// Файл операторов (operators.json). Перечитывается при изменении — добавить оператора можно
/// командой add-operator без перезапуска сервера (2026-09-29).
/// </summary>
public sealed class OperatorStore
{
    // 2026-10-27: новые хэши — 600 тыс. итераций (рекомендация OWASP); старые (210 тыс.) проверяются как раньше и обновятся при сбросе/смене пароля.
    public const int Iterations = 600_000;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Пустой хэш для сравнения, когда логина нет: время ответа одинаковое, логины не угадать по задержке.
    private static readonly string DummyHash = HashPassword("dummy-password-for-timing");

    private readonly string _path;
    private readonly object _gate = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private long _loadedLength = -1;
    private Dictionary<string, OperatorRecord> _byLogin = new(StringComparer.OrdinalIgnoreCase);

    public OperatorStore(string path) => _path = path;

    public string FilePath => _path;

    public static string NormalizeLogin(string login) => login.Trim().ToLowerInvariant();

    public static bool IsValidLogin(string login) =>
        login.Length is >= 2 and <= 32 && login.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');

    public IReadOnlyList<OperatorRecord> All()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return _byLogin.Values.OrderBy(o => o.Login).ToList();
        }
    }

    public OperatorRecord? Find(string login)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return _byLogin.TryGetValue(NormalizeLogin(login), out var op) ? op : null;
        }
    }

    /// <summary>Проверка логина и пароля. null — не подошло (или оператор отключён).</summary>
    public OperatorRecord? Verify(string login, string password)
    {
        var op = Find(login);
        var ok = VerifyPassword(password, op?.Hash ?? DummyHash);
        return ok && op is { Disabled: false } ? op : null;
    }

    /// <summary>Добавить оператора или сбросить пароль (если логин уже есть).</summary>
    public (OperatorRecord op, bool created) Upsert(string login, string name, string password)
    {
        login = NormalizeLogin(login);
        lock (_gate)
        {
            ReloadIfChanged(force: true);
            var created = !_byLogin.TryGetValue(login, out var op);
            op ??= new OperatorRecord { Login = login };
            if (!string.IsNullOrWhiteSpace(name)) op.Name = name.Trim();
            if (string.IsNullOrWhiteSpace(op.Name)) op.Name = login;
            op.Hash = HashPassword(password);
            op.Disabled = false;
            op.Updated = DateTimeOffset.UtcNow;
            _byLogin[login] = op;
            Save();
            return (op, created);
        }
    }

    public bool Remove(string login)
    {
        login = NormalizeLogin(login);
        lock (_gate)
        {
            ReloadIfChanged(force: true);
            if (!_byLogin.Remove(login)) return false;
            Save();
            return true;
        }
    }

    private void ReloadIfChanged(bool force = false)
    {
        var fi = new FileInfo(_path);
        if (!fi.Exists)
        {
            _byLogin = new(StringComparer.OrdinalIgnoreCase);
            _loadedStamp = DateTime.MinValue;
            _loadedLength = -1;
            return;
        }
        if (!force && fi.LastWriteTimeUtc == _loadedStamp && fi.Length == _loadedLength) return;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<OperatorRecord>>(json, JsonOpts) ?? new();
            var map = new Dictionary<string, OperatorRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in list.Where(o => !string.IsNullOrWhiteSpace(o.Login)))
                map[NormalizeLogin(op.Login)] = op;
            _byLogin = map;
            _loadedStamp = fi.LastWriteTimeUtc;
            _loadedLength = fi.Length;
        }
        catch (IOException)
        {
            // Файл пишется прямо сейчас — оставим прежний список до следующего обращения.
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_byLogin.Values.OrderBy(o => o.Login).ToList(), JsonOpts), new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
        TryRestrictPermissions(_path);
        var fi = new FileInfo(_path);
        _loadedStamp = fi.LastWriteTimeUtc;
        _loadedLength = fi.Length;
    }

    internal static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* не критично */ }
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
            var iterations = int.Parse(parts[1]);
            if (iterations is < 10_000 or > 5_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
