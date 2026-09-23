namespace NurMarketKassa.Services;

public static class HardwarePortHelper
{
    public static string NormalizeComPort(string? value, string fallback = "COM2")
    {
        var text = (value ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(text))
            return fallback;

        if (text.EndsWith(":", StringComparison.Ordinal))
            text = text[..^1];

        if (text.StartsWith(@"\\.\", StringComparison.Ordinal))
            text = text[4..];

        return text.StartsWith("COM", StringComparison.Ordinal) ? text : fallback;
    }

    public static bool LooksLikeComPort(string? value)
    {
        var text = NormalizeComPort(value, "");
        if (!text.StartsWith("COM", StringComparison.Ordinal) || text.Length <= 3)
            return false;
        return int.TryParse(text[3..], out var num) && num > 0;
    }

    public static string NormalizeLptPort(string? value, string fallback = "LPT1")
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrEmpty(text))
            return fallback;

        if (text.StartsWith(@"\\.\", StringComparison.Ordinal))
            text = text[4..];

        if (text.EndsWith(":", StringComparison.Ordinal))
            text = text[..^1];

        return text.ToUpperInvariant();
    }

    public static bool LooksLikeLptPort(string? value)
    {
        var text = NormalizeLptPort(value, "");
        if (!text.StartsWith("LPT", StringComparison.Ordinal) || text.Length <= 3)
            return false;
        return int.TryParse(text[3..], out var num) && num > 0;
    }
}
