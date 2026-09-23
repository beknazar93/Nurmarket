using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Выравнивание строк моноширинного текстового чека.</summary>
internal static class ReceiptLineLayout
{
    /// <summary>
    /// Подпись слева, сумма/значение справа. Сумма не обрезается; при нехватке места переносится подпись.
    /// </summary>
    public static string FormatLabelAmount(string label, string amount, int width, int rightMarginChars = 1)
    {
        label = (label ?? string.Empty).TrimEnd();
        amount = (amount ?? string.Empty).Trim();
        if (width < 8)
            width = 8;

        if (amount.Length > width)
            return amount;

        int effectiveWidth = Math.Max(0, width - rightMarginChars);
        const int minGap = 1;
        var spaceForLabel = effectiveWidth - amount.Length - minGap;

        if (label.Length <= spaceForLabel)
        {
            var gap = effectiveWidth - label.Length - amount.Length;
            return label + new string(' ', Math.Max(minGap, gap)) + amount;
        }

        var labelLines = WrapLeft(label, effectiveWidth).ToList();
        var lastLine = labelLines[^1];
        if (lastLine.Length + minGap + amount.Length <= effectiveWidth)
        {
            var gap = effectiveWidth - lastLine.Length - amount.Length;
            labelLines[^1] = lastLine + new string(' ', Math.Max(minGap, gap)) + amount;
            return string.Join('\n', labelLines);
        }

        labelLines.Add(amount.PadLeft(effectiveWidth));
        return string.Join('\n', labelLines);
    }

    public static string WithSom(string amount, bool appendSom = true)
    {
        amount = (amount ?? string.Empty).Trim();
        if (!appendSom || amount.Length == 0)
            return amount;

        if (amount.EndsWith("сом", StringComparison.OrdinalIgnoreCase))
            return amount;

        return amount + " сом";
    }

    /// <summary>Подпись на одной строке, сумма на следующей — выравнивание по правому краю.</summary>
    public static IEnumerable<string> FormatStackedLabelAmount(
        string label,
        string amount,
        int width,
        bool appendSom = true,
        int rightMarginChars = 1)
    {
        label = (label ?? string.Empty).TrimEnd();
        if (label.Length > 0 && !label.EndsWith(':'))
            label += ":";

        if (label.Length > 0)
        {
            foreach (var labelLine in WrapLeft(label, width))
                yield return labelLine;
        }

        var display = WithSom(amount, appendSom);
        if (display.Length > 0)
            yield return RightAlignWithMargin(display, width, rightMarginChars);
    }

    /// <summary>Строка «кол-во x цена», затем итог позиции справа с «сом».</summary>
    public static IEnumerable<string> FormatItemBlock(string qtyUnitLine, string lineTotalAmount, int width)
    {
        qtyUnitLine = (qtyUnitLine ?? string.Empty).Trim();
        if (qtyUnitLine.Length > 0)
        {
            foreach (var part in WrapLeft(qtyUnitLine, width))
                yield return part;
        }

        int effectiveWidth = Math.Max(0, width - 2);
        yield return PadLeft(WithSom(lineTotalAmount), effectiveWidth);
    }

    public static IEnumerable<string> WrapCenter(string text, int width)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            yield break;

        foreach (var line in WrapLeft(text, width))
            yield return Center(line, width);
    }

    public static string PadLine(string text, int width)
    {
        text ??= string.Empty;
        if (text.Length <= width)
            return text;

        return WrapLeft(text, width).First();
    }

    public static string PadLeft(string text, int width)
    {
        text ??= string.Empty;
        return text.Length >= width ? text : text.PadLeft(width);
    }

    /// <summary>Выравнивание по правому краю с защитным отступом от края ленты (1–2 символа).</summary>
    public static string RightAlignWithMargin(string text, int width, int rightMarginChars = 1)
    {
        text ??= string.Empty;
        var targetWidth = Math.Max(0, width - rightMarginChars);

        if (text.Length <= targetWidth)
            return text.PadLeft(targetWidth);

        return text[..targetWidth];
    }

    public static string Center(string text, int width)
    {
        text ??= string.Empty;
        if (text.Length >= width)
            return text;

        var left = (width - text.Length) / 2;
        return new string(' ', left) + text;
    }

    public static IEnumerable<string> WrapLeft(string text, int width)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            yield break;

        if (text.Length <= width)
        {
            yield return text;
            yield break;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            for (var i = 0; i < text.Length; i += width)
                yield return text.Substring(i, Math.Min(width, text.Length - i));
            yield break;
        }

        var current = new StringBuilder(width);
        foreach (var word in words)
        {
            if (word.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                for (var i = 0; i < word.Length; i += width)
                    yield return word.Substring(i, Math.Min(width, word.Length - i));
                continue;
            }

            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }

            if (current.Length + 1 + word.Length <= width)
            {
                current.Append(' ').Append(word);
                continue;
            }

            yield return current.ToString();
            current.Clear();
            current.Append(word);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
