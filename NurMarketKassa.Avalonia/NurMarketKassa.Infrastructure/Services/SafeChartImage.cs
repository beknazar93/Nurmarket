namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В4 АЛ-1 — графики Word-отчётов рисовал System.Drawing (AnalyticsChartRenderer), которого на Linux и Android нет: любой график
/// ронял всю выгрузку. Сбой одного графика не ломает отчёт. 2026-11-06 (В18): на Linux и Android графики рисует SkiaSharp (AnalyticsChartSkia).</summary>
public static class SafeChartImage
{
    /// <summary>Нарисовать график и вставить в отчёт; при сбое — пропустить (таблицы и текст остаются).</summary>
    public static void TryChart(this WordReportBuilder doc, Func<byte[]> render, string description)
    {
        // 2026-11-06 (В18, АЛ-1): график рисует SkiaSharp (на Windows — GDI+ как раньше), поэтому на Linux и Android он больше не пропускается.
        try
        {
            doc.Image(render(), description);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт Word: график «{description}» не нарисован ({ex.Message}) — пропущен.", "WARNING");
        }
    }
}
