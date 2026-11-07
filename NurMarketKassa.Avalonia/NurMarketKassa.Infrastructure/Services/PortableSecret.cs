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
            var dir = NurMarketKassa.Services.AppPaths.KassaLocal;
            Directory.CreateDirectory(dir);
            // 2026-11-06: В3 А-15 — на Linux/Android папка с ключом доступна только владельцу (700), а не всем пользователям ПК. Сбой прав не мешает работе.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (Exception) { /* файловая система без прав Unix — ключ всё равно в файле с правами 600 */ }
            }
            var path = Path.Combine(dir, ".secret-key");
            if (File.Exists(path) && File.ReadAllBytes(path) is { Length: 32 } existing)
                return _key = existing;

            var key = RandomNumberGenerator.GetBytes(32);
            // 2026-11-05 (аудит №4, А4-25): касса и процесс владельца (:owner) делят этот файл. Раньше при одновременном первом
            // запуске оба писали свой ключ — токены одного не расшифровывались (повторный вход). Теперь файл создаётся
            // только если его ещё нет (CreateNew), проигравший перечитывает ключ победителя.
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(key, 0, key.Length);
                    stream.Flush(true);
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return _key = key;
            }
            catch (IOException) when (File.Exists(path))
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        if (File.ReadAllBytes(path) is { Length: 32 } winner)
                            return _key = winner;
                    }
                    catch (IOException)
                    {
                        // победитель ещё пишет файл — подождём
                    }
                    System.Threading.Thread.Sleep(50);
                }
                // файл остался битым (не 32 байта) — перезаписываем, но СНАЧАЛА сохраняем его копию рядом: токены и ключи, зашифрованные прежним ключом, иначе пропали бы без возврата.
                // 2026-11-06: В3 А-15 — раньше битый файл ключа затирался молча.
                try
                {
                    var backup = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Copy(path, backup, overwrite: false);
                    PosLogger.Log($"Ключ защиты данных повреждён (не 32 байта): создан новый, копия повреждённого файла — {Path.GetFileName(backup)}. Токены и ключи, записанные прежним ключом, потребуется ввести заново.", "ERROR");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PosLogger.Log($"Ключ защиты данных повреждён и будет создан заново; копия не сохранена ({ex.GetType().Name}).", "ERROR");
                }
                File.WriteAllBytes(path, key);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return _key = key;
            }
        }
    }
}
