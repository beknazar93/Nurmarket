namespace NurMarketKassa.Services;

internal static class GraphicReceiptLayout
{
    private const float BoldTotalLineStepPx = 32f;

    public static bool IsBoldTotalLine(string line) =>
        line.TrimStart().StartsWith("ИТОГО:", StringComparison.OrdinalIgnoreCase);

    public static float GetLineStepPx(string line, float lineHeightPx)
    {
        if (lineHeightPx <= 0)
            lineHeightPx = TestReceiptLineBuilder.LineStepPx;

        if (string.IsNullOrWhiteSpace(line))
            return Math.Max(4f, lineHeightPx * 0.4f);

        if (IsBoldTotalLine(line))
            return BoldTotalLineStepPx;

        if (IsSparseAmountLine(line))
            return Math.Max(TestReceiptLineBuilder.CompactLineStepPx, lineHeightPx * 0.85f);

        return lineHeightPx;
    }

    public static float GetLineStepMm(string line, float lineHeightPx) =>
        GetLineStepPx(line, lineHeightPx) * 0.264583f;

    public static float ComputeContentHeightPx(IReadOnlyList<string> lines, float lineHeightPx)
    {
        var height = TestReceiptLineBuilder.StartOffsetPx + TestReceiptLineBuilder.BottomPaddingPx;
        foreach (var line in lines)
            height += GetLineStepPx(line, lineHeightPx);
        return height;
    }

    /// <summary>Строка суммы под товаром: много пробелов слева и короткое значение справа.</summary>
    private static bool IsSparseAmountLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        var leadingSpaces = line.Length - line.TrimStart().Length;
        return leadingSpaces >= ReceiptLayout.CharWidth / 2 && trimmed.Length <= 14;
    }
}
