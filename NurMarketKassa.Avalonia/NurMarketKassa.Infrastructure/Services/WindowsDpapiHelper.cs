using System.Security.Cryptography;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>
/// Windows DPAPI helper for short machine-local secrets. Passwords must not be
/// passed here; authentication persistence stores session tokens only.
/// Output format is Base64 over ProtectedData payload.
/// </summary>
public static class WindowsDpapiHelper
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NurMarketKassa:UserSecrets:v1");

    public static string ProtectToBase64(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;
        // 2026-10-04: Linux/Android — без DPAPI, через PortableSecret (на Windows — тот же DPAPI).
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = PortableSecret.Protect(plainBytes, Entropy);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectFromBase64(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return string.Empty;
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var plainBytes = PortableSecret.Unprotect(protectedBytes, Entropy);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }
}
