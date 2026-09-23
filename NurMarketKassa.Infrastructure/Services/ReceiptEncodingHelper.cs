using System.Text;

namespace NurMarketKassa.Services;

internal static class ReceiptEncodingHelper
{
    public static string ResolveDotNetEncodingName(string userEncoding)
    {
        var u = (userEncoding ?? "cp866").Trim().ToLowerInvariant()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);

        return u switch
        {
            "utf-8" or "utf8" => "utf-8",
            "cp1251" or "windows-1251" or "wpc1251" => "windows-1251",
            "cp855" => "ibm855",
            "iso-8859-5" => "iso-8859-5",
            "koi8-r" or "koi8r" => "koi8-r",
            "cp866" or "ibm866" => "cp866",
            _ => "cp866",
        };
    }

    public static Encoding GetEncodingWithFallback(string userEncoding)
    {
        var name = ResolveDotNetEncodingName(userEncoding);
        try
        {
            return Encoding.GetEncoding(
                name,
                new EncoderReplacementFallback("?"),
                new DecoderReplacementFallback("?"));
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(
                866,
                new EncoderReplacementFallback("?"),
                new DecoderReplacementFallback("?"));
        }
    }

  /// <summary>Имитирует потерю символов при перекодировке в выбранную кодовую страницу.</summary>
    public static string ApplyEncodingPreview(string text, string userEncoding)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var encoding = GetEncodingWithFallback(userEncoding);
        var bytes = encoding.GetBytes(text);
        return encoding.GetString(bytes);
    }
}
