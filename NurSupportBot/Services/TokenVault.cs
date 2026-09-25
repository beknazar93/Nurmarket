using System.Security.Cryptography;
using System.Text;

namespace NurSupportBot.Services;

/// <summary>Шифрует refresh-токен NurCRM перед записью в базу (AES-GCM). Ключ — файл
/// data/token.key, создаётся при первом запуске. Пароль клиента бот не хранит вообще: только
/// токен, который NurCRM выдал при входе, и только зашифрованным.</summary>
public sealed class TokenVault
{
    private readonly byte[] _key;

    public TokenVault(string dataDir)
    {
        var path = Path.Combine(dataDir, "token.key");
        if (File.Exists(path))
        {
            _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(path, Convert.ToBase64String(_key));
        }
    }

    public byte[] Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }

    public string? Decrypt(byte[]? blob)
    {
        if (blob is not { Length: > 28 })
            return null;
        try
        {
            var nonce = blob[..12];
            var tag = blob[12..28];
            var cipher = blob[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
