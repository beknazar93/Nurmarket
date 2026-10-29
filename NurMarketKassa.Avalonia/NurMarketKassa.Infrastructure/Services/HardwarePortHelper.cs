namespace NurMarketKassa.Services;

public static class HardwarePortHelper
{
    /// <summary>2026-10-29: Linux/macOS — путь устройства (/dev/ttyUSB0, /dev/cu.usbserial-…) регистр чувствителен и
    /// не COM/LPT: раньше он превращался в «/DEV/TTYUSB0» (печать) или в COM2 (весы).</summary>
    public static bool IsUnixDevicePath(string? value) =>
        value is not null && value.AsSpan().Trim().StartsWith("/dev/", StringComparison.Ordinal);

    public static string NormalizeComPort(string? value, string fallback = "COM2")
    {
        if (IsUnixDevicePath(value))
            return value!.Trim();
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
        if (IsUnixDevicePath(value))
            return value!.Trim().StartsWith("/dev/tty", StringComparison.Ordinal) || value.Trim().StartsWith("/dev/cu.", StringComparison.Ordinal);
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

        if (IsUnixDevicePath(text))
            return text; // 2026-10-29: путь /dev/usb/lp0 — без смены регистра

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
