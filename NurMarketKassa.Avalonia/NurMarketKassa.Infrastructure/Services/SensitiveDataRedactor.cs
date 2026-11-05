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
        // 2026-11-05 (аудит №3, Н-08): телефоны клиентов без подписи «phone=» (в тексте ошибок, названиях действий) — оставляем только последние 2 цифры.
        result = PhoneNumber().Replace(result, match => MaskPhone(match.Value));
        return result;
    }

    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 2 ? $"{Mask}{digits[^2..]}" : Mask;
    }

    /// <summary>Номер КР/РФ: +996 555 123 456, 996555123456, 0555 12 34 56, +7 900 123-45-67. Не трогает суммы, даты, UUID и длинные номера чеков.</summary>
    [GeneratedRegex(
        "(?<![\\w.,+\\-])(?:(?:\\+\\s?996|996)(?:[\\s\\-()]*\\d){9}|\\+\\s?7(?:[\\s\\-()]*\\d){10}|0(?:[\\s\\-()]*\\d){9})(?![\\w\\-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumber();

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
