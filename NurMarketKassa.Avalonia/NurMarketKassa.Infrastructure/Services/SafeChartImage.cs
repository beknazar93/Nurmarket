namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В4 АЛ-1 — графики Word-отчётов рисует System.Drawing (AnalyticsChartRenderer), которого на Linux и Android нет: любой график
/// ронял всю выгрузку («Word-отчёт аналитики не создаётся»). Теперь на этих системах график пропускается (таблицы и текст остаются), а на Windows
/// сбой одного графика тоже не ломает отчёт. Полноценные графики на Linux/Android — перевод на SkiaSharp в волне В18.</summary>
public static class SafeChartImage
{
    private static bool _loggedSkip;

    /// <summary>Нарисовать график и вставить в отчёт; на системе без System.Drawing или при сбое — пропустить.</summary>
    public static void TryChart(this WordReportBuilder doc, Func<byte[]> render, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!_loggedSkip)
            {
                _loggedSkip = true;
                PosLogger.Log("Отчёт Word: графики на этой системе пока не рисуются (нужен System.Drawing, только Windows) — выгружены таблицы без графиков.", "INFO");
            }
            return;
        }
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
