using System.Globalization;

namespace NurMarketKassa.Services;

public static class OrderDiscountHelper
{
    public static string NormalizeDecimal(string raw) => (raw ?? "").Trim().Replace(',', '.');

    /// <summary>Пустая строка или число ≈0 — поле считается «не задано».</summary>
    public static bool IsEmptyOrZeroLike(string? raw)
    {
        var n = NormalizeDecimal(raw ?? "");
        if (n.Length == 0)
            return true;
        if (!double.TryParse(n, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) ||
            double.IsInfinity(v))
            return false;
        return Math.Abs(v) < 1e-9;
    }

    /// <summary>Тело patch: только одно поле скидки или пустой словарь, если скидка не задана.</summary>
    public static Dictionary<string, string> BuildPatchBody(bool isPercent, string? rawInput)
    {
        var normalized = NormalizeDecimal(rawInput ?? "");
        if (IsEmptyOrZeroLike(normalized))
            return new Dictionary<string, string>();

        return isPercent
            ? new Dictionary<string, string> { ["order_discount_percent"] = normalized }
            : new Dictionary<string, string> { ["order_discount_total"] = normalized };
    }

    /// <summary>Сброс скидки: только ранее использованное поле, никогда оба сразу.</summary>
    public static Dictionary<string, string> BuildClearPatchBody(string? existingPercent, string? existingSum)
    {
        if (!IsEmptyOrZeroLike(existingPercent))
            return new Dictionary<string, string> { ["order_discount_percent"] = "0" };
        if (!IsEmptyOrZeroLike(existingSum))
            return new Dictionary<string, string> { ["order_discount_total"] = "0" };
        return new Dictionary<string, string>();
    }

    /// <summary>Оставляет не более одного поля скидки.</summary>
    public static Dictionary<string, string> SanitizePatchBody(Dictionary<string, string> body, bool isClear = false)
    {
        if (body.TryGetValue("order_discount_percent", out var pct)
            && (isClear || !IsEmptyOrZeroLike(pct)))
            return new Dictionary<string, string> { ["order_discount_percent"] = NormalizeDecimal(pct) };

        if (body.TryGetValue("order_discount_total", out var total)
            && (isClear || !IsEmptyOrZeroLike(total)))
            return new Dictionary<string, string> { ["order_discount_total"] = NormalizeDecimal(total) };

        return new Dictionary<string, string>();
    }

    public static string? ValidatePercent(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return "Введите процент";
        if (!TryParseNonNegative(s, out var v))
            return "Некорректный процент";
        if (v > 100)
            return "Процент не может быть больше 100";
        return null;
    }

    public static string? ValidateSum(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return "Введите сумму скидки";
        if (!TryParseNonNegative(s, out _))
            return "Некорректная сумма скидки";
        return null;
    }

    public static string? ValidateQuantity(string raw)
    {
        if (!TryParseNonNegative(NormalizeDecimal(raw), out var v))
            return "Некорректное количество";
        if (v <= 0)
            return "Количество должно быть больше нуля";
        if (v > 1_000_000)
            return "Слишком большое количество";
        return null;
    }

    private static bool TryParseNonNegative(string s, out double v)
    {
        v = 0;
        s = NormalizeDecimal(s);
        if (s.Length == 0)
            return false;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) && v >= 0 && !double.IsNaN(v) &&
               !double.IsInfinity(v);
    }
}
