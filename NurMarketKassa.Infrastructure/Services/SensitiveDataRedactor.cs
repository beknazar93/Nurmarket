using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>Last-line protection for data that must never be written to logs.</summary>
public static partial class SensitiveDataRedactor
{
    private const string Mask = "***";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var result = QuotedSecret().Replace(value, match =>
            $"{match.Groups["prefix"].Value}{Mask}{match.Groups["suffix"].Value}");
        result = PlainSecret().Replace(result, match =>
            $"{match.Groups["prefix"].Value}{Mask}");
        result = BearerToken().Replace(result, "Bearer ***");
        result = EmailAddress().Replace(result, match => MaskEmail(match.Value));
        result = ConnectionStringSecret().Replace(result, match =>
            $"{match.Groups["key"].Value}={Mask}");
        return result;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
            return Mask;
        return $"{email[0]}***{email[at..]}";
    }

    [GeneratedRegex(
        "(?<prefix>[\\\"']?(?:password|cashier_password|pin|token|access_token|refresh_token|authorization|email|phone|whatsapp_phone|inn|address)[\\\"']?\\s*[:=]\\s*[\\\"'])(?:[^\\\"']*)(?<suffix>[\\\"'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedSecret();

    [GeneratedRegex(
        "(?<prefix>(?:password|cashier_password|pin|token|access_token|refresh_token|authorization|email|phone|whatsapp_phone|inn|address)\\s*[:=]\\s*)(?:[^;}\\]\\r\\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainSecret();

    [GeneratedRegex("Bearer\\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex("[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    [GeneratedRegex("(?<key>Password|Pwd)\\s*=\\s*[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();
}
