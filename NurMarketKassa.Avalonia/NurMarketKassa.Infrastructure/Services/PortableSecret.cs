using System.Security.Cryptography;

namespace NurMarketKassa.Services;

/// <summary>2026-10-04: защита секретов (токены сессии, ключи) на любой ОС. Windows — DPAPI, как и раньше
/// (байты те же, старые файлы читаются). Linux/Android — AES-GCM ключом пользователя: случайные 32 байта в
/// файле ~/.local/share/NurMarketKassa/.secret-key с правами только владельца (600) — DPAPI там нет,
/// и вход в кассу падал бы на сохранении сессии.</summary>
public static class PortableSecret
{
    private const byte Version = 1;

    public static byte[] Protect(byte[] plain, byte[] entropy)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);

        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, entropy);
        var result = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        result[0] = Version;
        nonce.CopyTo(result, 1);
        tag.CopyTo(result, 13);
        cipher.CopyTo(result, 29);
        return result;
    }

    public static byte[] Unprotect(byte[] data, byte[] entropy)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);

        if (data.Length < 29 || data[0] != Version)
            throw new CryptographicException("Неизвестный формат защищённых данных.");
        var key = LoadOrCreateKey();
        var plain = new byte[data.Length - 29];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(data.AsSpan(1, 12), data.AsSpan(29), data.AsSpan(13, 16), plain, entropy);
        return plain;
    }

    private static readonly object Gate = new();
    private static byte[]? _key;

    private static byte[] LoadOrCreateKey()
    {
        lock (Gate)
        {
            if (_key is not null)
                return _key;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".secret-key");
            if (File.Exists(path) && File.ReadAllBytes(path) is { Length: 32 } existing)
                return _key = existing;

            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return _key = key;
        }
    }
}
