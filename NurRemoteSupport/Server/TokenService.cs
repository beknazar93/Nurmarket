using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace NurRemoteSupport.Server;

public sealed record OperatorIdentity(string Login, string Name);

/// <summary>
/// 2026-10-27: что известно о входе оператора, пока соединение открыто: чтобы перепроверять его (В-5) без самого токена.
/// Sig — подпись токена (ключ отзыва при выходе); у соединения по билету она та же, что у токена, которым билет получен.
/// </summary>
public sealed record OperatorCredential(string Login, string Name, string Stamp, long Exp, string Sig)
{
    public OperatorIdentity Identity => new(Login, Name);
}

/// <summary>
/// Токен оператора: base64url(JSON {l,n,s,e}).base64url(HMAC-SHA256). Живёт 12 ч.
/// s — отпечаток пароля: сброс пароля/удаление оператора отзывает токены (2026-09-29).
/// </summary>
public sealed class TokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly OperatorStore _operators;
    private readonly string? _revokedPath;
    private readonly object _revokedLock = new();
    // 2026-10-27 (С-4): отозванные при выходе токены (подпись → когда токен и так истекает). Хранятся в файле, чтобы пережить перезапуск.
    private readonly ConcurrentDictionary<string, long> _revoked = new();
    // 2026-10-27 (С-4): одноразовые билеты для WebSocket страницы (вместо токена в адресе): билет → (вход, до какого времени, мс).
    private readonly ConcurrentDictionary<string, (OperatorCredential Cred, long ExpiresAtMs)> _tickets = new();
    public const int TicketSeconds = 30;

    public TokenService(byte[] key, TimeSpan lifetime, OperatorStore operators, string? revokedPath = null)
    {
        if (key.Length < 32) throw new ArgumentException("Ключ подписи токенов короче 32 байт.");
        _key = key;
        _lifetime = lifetime;
        _operators = operators;
        _revokedPath = revokedPath;
        LoadRevoked();
    }

    // j — случайный номер токена (2026-10-27): два входа в одну секунду дают разные токены, и выход отзывает только свой.
    private sealed record Payload(string l, string n, string s, long e, string? j = null);

    public (string token, DateTimeOffset expires) Issue(OperatorRecord op)
    {
        var expires = DateTimeOffset.UtcNow.Add(_lifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(op.Login, op.Name, op.Stamp, expires.ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(8))));
        var body = WebEncoders.Base64UrlEncode(payload);
        var sig = WebEncoders.Base64UrlEncode(Sign(body));
        return (body + "." + sig, expires);
    }

    public OperatorIdentity? Validate(string? token) => Parse(token)?.Identity;

    /// <summary>Проверка подписи, срока, оператора (есть, не отключён, пароль не менялся) и отзыва. null — не подошёл.</summary>
    public OperatorCredential? Parse(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;
        var body = token[..dot];
        try
        {
            var sigPart = token[(dot + 1)..];
            var sig = WebEncoders.Base64UrlDecode(sigPart);
            if (!CryptographicOperations.FixedTimeEquals(sig, Sign(body))) return null;
            var p = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(body));
            if (p is null) return null;
            var cred = new OperatorCredential("", "", p.s, p.e, sigPart);
            var op = Current(p.l, p.s, p.e, sigPart);
            return op is null ? null : cred with { Login = op.Login, Name = op.Name };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>2026-10-27 (В-5): вход всё ещё действителен — для открытых WebSocket: токен не истёк, не отозван, оператор не удалён и пароль не менялся.</summary>
    public bool StillValid(OperatorCredential c) => Current(c.Login, c.Stamp, c.Exp, c.Sig) is not null;

    private OperatorRecord? Current(string login, string stamp, long exp, string sig)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
        if (_revoked.ContainsKey(sig)) return null;
        var op = _operators.Find(login);
        return op is null || op.Disabled || op.Stamp != stamp ? null : op;
    }

    /// <summary>Выход (POST /api/logout): токен больше не принимается до своего срока.</summary>
    public void Revoke(OperatorCredential c)
    {
        if (_revoked.Count > 100_000) return;
        _revoked[c.Sig] = c.Exp;
        if (_revokedPath is null) return;
        try
        {
            lock (_revokedLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_revokedPath))!);
                File.AppendAllText(_revokedPath, c.Sig + " " + c.Exp + "\n");
                OperatorStore.TryRestrictPermissions(_revokedPath);
            }
        }
        catch (IOException) { /* в памяти отозван; после перезапуска — до конца срока токена */ }
    }

    private void LoadRevoked()
    {
        if (_revokedPath is null || !File.Exists(_revokedPath)) return;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var keep = new List<string>();
            foreach (var line in File.ReadAllLines(_revokedPath))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && long.TryParse(parts[1], out var exp) && exp > now)
                {
                    _revoked[parts[0]] = exp;
                    keep.Add(line);
                }
            }
            File.WriteAllLines(_revokedPath, keep);
        }
        catch (IOException) { }
    }

    /// <summary>Выдать одноразовый билет на WebSocket (30 с). Токен в адрес не попадает.</summary>
    public string IssueTicket(OperatorCredential c)
    {
        var now = Environment.TickCount64;
        if (_tickets.Count > 10_000)
            foreach (var (k, v) in _tickets) if (v.ExpiresAtMs < now) _tickets.TryRemove(k, out _);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _tickets[ticket] = (c, now + TicketSeconds * 1000L);
        return ticket;
    }

    /// <summary>Обменять билет на вход: один раз; просроченный или повторный — null.</summary>
    public OperatorCredential? RedeemTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var t)) return null;
        if (t.ExpiresAtMs < Environment.TickCount64) return null;
        return StillValid(t.Cred) ? t.Cred : null;
    }

    /// <summary>Раз в минуту: убрать просроченные билеты и отзывы, срок которых вышел.</summary>
    public void Sweep()
    {
        var nowMs = Environment.TickCount64;
        foreach (var (k, v) in _tickets) if (v.ExpiresAtMs < nowMs) _tickets.TryRemove(k, out _);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (k, exp) in _revoked) if (exp < now) _revoked.TryRemove(k, out _);
    }

    private byte[] Sign(string body) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body));

    /// <summary>Ключ: из настроек, иначе из файла token-secret (создаётся при первом запуске).</summary>
    public static byte[] LoadOrCreateKey(RelayOptions o, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(o.TokenSecret))
        {
            try
            {
                var b = Convert.FromBase64String(o.TokenSecret);
                if (b.Length >= 32) return b;
            }
            catch (FormatException) { }
            var raw = Encoding.UTF8.GetBytes(o.TokenSecret);
            if (raw.Length < 32) throw new InvalidOperationException("Relay:TokenSecret должен быть не короче 32 символов.");
            return SHA256.HashData(raw);
        }
        var path = Path.Combine(o.DataDir, "token-secret");
        if (File.Exists(path))
        {
            var b = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (b.Length >= 32) return b;
        }
        Directory.CreateDirectory(o.DataDir);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        OperatorStore.TryRestrictPermissions(path);
        log.LogInformation("Создан новый ключ подписи токенов в {Path}", path);
        return key;
    }
}
