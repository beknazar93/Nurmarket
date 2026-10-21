using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace NurRemoteSupport.Server;

public sealed record OperatorIdentity(string Login, string Name);

/// <summary>
/// Токен оператора: base64url(JSON {l,n,s,e}).base64url(HMAC-SHA256). Живёт 12 ч.
/// s — отпечаток пароля: сброс пароля/удаление оператора отзывает токены (2026-09-29).
/// </summary>
public sealed class TokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly OperatorStore _operators;

    public TokenService(byte[] key, TimeSpan lifetime, OperatorStore operators)
    {
        if (key.Length < 32) throw new ArgumentException("Ключ подписи токенов короче 32 байт.");
        _key = key;
        _lifetime = lifetime;
        _operators = operators;
    }

    private sealed record Payload(string l, string n, string s, long e);

    public (string token, DateTimeOffset expires) Issue(OperatorRecord op)
    {
        var expires = DateTimeOffset.UtcNow.Add(_lifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(op.Login, op.Name, op.Stamp, expires.ToUnixTimeSeconds()));
        var body = WebEncoders.Base64UrlEncode(payload);
        var sig = WebEncoders.Base64UrlEncode(Sign(body));
        return (body + "." + sig, expires);
    }

    public OperatorIdentity? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;
        var body = token[..dot];
        try
        {
            var sig = WebEncoders.Base64UrlDecode(token[(dot + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(sig, Sign(body))) return null;
            var p = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(body));
            if (p is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > p.e) return null;
            var op = _operators.Find(p.l);
            if (op is null || op.Disabled || op.Stamp != p.s) return null;
            return new OperatorIdentity(op.Login, op.Name);
        }
        catch
        {
            return null;
        }
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
