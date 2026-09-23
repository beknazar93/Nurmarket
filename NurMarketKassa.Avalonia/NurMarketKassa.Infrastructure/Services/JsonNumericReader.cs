using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Безопасное чтение чисел из JSON (число или строка).</summary>
public static class JsonNumericReader
{
    public const decimal WeightStepKg = 0.05m;
    public const int WeightDecimalPlaces = 3;

    public static bool TryToDouble(JsonElement element, out double value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var i))
                {
                    value = i;
                    return true;
                }

                if (element.TryGetDecimal(out var dec))
                {
                    value = (double)dec;
                    return true;
                }

                return element.TryGetDouble(out value);

            case JsonValueKind.String:
                return TryParseString(element.GetString(), out value);

            case JsonValueKind.True:
                value = 1;
                return true;

            case JsonValueKind.False:
                value = 0;
                return true;

            default:
                return false;
        }
    }

    public static bool TryToDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node == null)
            return false;

        switch (node.GetValueKind())
        {
            case JsonValueKind.Number:
                if (node is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out var l))
                    {
                        value = l;
                        return true;
                    }

                    if (jv.TryGetValue<decimal>(out var dec))
                    {
                        value = (double)dec;
                        return true;
                    }

                    if (jv.TryGetValue<double>(out var d))
                    {
                        value = d;
                        return true;
                    }
                }

                return TryParseString(node.ToJsonString().Trim('"'), out value);

            case JsonValueKind.String:
                return TryParseString(node.GetValue<string>(), out value);

            case JsonValueKind.True:
                value = 1;
                return true;

            case JsonValueKind.False:
                value = 0;
                return true;

            default:
                return false;
        }
    }

    public static double ToDouble(JsonElement element, double defaultValue = 0) =>
        TryToDouble(element, out var v) ? v : defaultValue;

    public static double ToDouble(JsonNode? node, double defaultValue = 0) =>
        TryToDouble(node, out var v) ? v : defaultValue;

    public static double ReadProperty(JsonElement obj, string prop, double defaultValue = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return defaultValue;

        return ToDouble(el, defaultValue);
    }

    public static double RoundWeight(double kg) =>
        (double)Math.Round((decimal)kg, WeightDecimalPlaces, MidpointRounding.AwayFromZero);

    public static double AdjustWeighedQuantity(double current, int direction)
    {
        var next = (decimal)current + direction * WeightStepKg;
        next = Math.Round(next, WeightDecimalPlaces, MidpointRounding.AwayFromZero);
        return (double)Math.Max(0, next);
    }

    public static string FormatWeightDisplay(double kg) =>
        RoundWeight(kg).ToString("0.000", CultureInfo.InvariantCulture);

    public static string FormatWeightForApi(double kg) =>
        FormatWeightDisplay(kg).TrimEnd('0').TrimEnd('.');

    private static bool TryParseString(string? raw, out double value)
    {
        value = 0;
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw))
            return false;

        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(raw.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
