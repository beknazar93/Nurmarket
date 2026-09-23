namespace NurMarketKassa.Services;

/// <summary>Убирает из текста чека служебные строки (смена/касса) и гарантирует приветствие в начале.</summary>
internal static class ReceiptSanitizer
{
    internal static string StripShiftCashboxAndEnsureWelcome(string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var list = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsShiftOrCashboxLine(line))
                continue;

            var paymentLine = TryNormalizePaymentMethodLine(line);
            list.Add(paymentLine ?? line);
        }

        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[0]))
            list.RemoveAt(0);

        var welcome = WelcomeLine();
        if (!HasWelcomeAsFirstNonEmpty(list, welcome))
            list.Insert(0, welcome);

        return string.Join("\n", list);
    }

    private static string WelcomeLine()
    {
        try
        {
            var v = Environment.GetEnvironmentVariable("DESKTOP_MARKET_RECEIPT_WELCOME");
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Receipt title environment lookup failed: {ex.GetType().Name}", "DEBUG");
        }

        return "Добро пожаловать";
    }

    private static bool HasWelcomeAsFirstNonEmpty(List<string> lines, string welcome)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var t = line.Trim();
            if (t.Equals(welcome.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (t.Contains("добро", StringComparison.OrdinalIgnoreCase) &&
                t.Contains("пожаловать", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        return false;
    }

    private static string? TryNormalizePaymentMethodLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0)
            return null;

        var upper = t.ToUpperInvariant();
        if (upper.StartsWith("СПОСОБ ОПЛАТЫ", StringComparison.Ordinal)
            && !ContainsMoneyTail(upper))
            return null;

        if (upper.StartsWith("ОПЛАТА", StringComparison.Ordinal)
            || (upper.StartsWith("НАЛИЧНЫМИ", StringComparison.Ordinal) && ContainsMoneyTail(upper))
            || (upper.StartsWith("БЕЗНАЛ", StringComparison.Ordinal) && ContainsMoneyTail(upper)))
        {
            if (upper.Contains("НАЛИЧ", StringComparison.Ordinal) || upper.Contains("CASH", StringComparison.Ordinal))
                return "Способ оплаты: Наличными";
            if (upper.Contains("БЕЗНАЛ", StringComparison.Ordinal)
                || upper.Contains("CARD", StringComparison.Ordinal)
                || upper.Contains("TRANSFER", StringComparison.Ordinal))
                return "Способ оплаты: Безналичными";
        }

        return null;
    }

    private static bool ContainsMoneyTail(string upperLine) =>
        upperLine.Contains("СОМ", StringComparison.Ordinal)
        || upperLine.Contains("COM", StringComparison.Ordinal)
        || upperLine.Any(char.IsDigit);

    private static bool IsShiftOrCashboxLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0)
            return false;
        return t.StartsWith("Смена", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Касса", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Shift", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Cashbox", StringComparison.OrdinalIgnoreCase);
    }
}
