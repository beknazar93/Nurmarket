using Microsoft.Extensions.Logging;

namespace NurMarketKassa.Services;

/// <summary>
/// Compatibility facade for legacy call sites. New code should inject
/// <see cref="ILogger{TCategoryName}"/>. Messages are always passed through the
/// central redactor before reaching the configured logging pipeline.
/// </summary>
public static class PosLogger
{
    private static readonly object SyncRoot = new();
    private static ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void Configure(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        lock (SyncRoot)
            _logger = loggerFactory.CreateLogger("NurMarketKassa.POS");
    }

    /// <summary>2026-10-04, Android: копия каждой строки журнала (уже без секретов) — в журнал Android (logcat),
    /// иначе с аппарата её не прочитать (файл журнала во внутренней папке программы). null — не дублировать.</summary>
    public static Action<string, string>? Mirror { get; set; }

    public static void Log(string message, string category = "INFO")
    {
        var safeMessage = SensitiveDataRedactor.Redact(message);
        var level = ParseLevel(category);
        lock (SyncRoot)
            _logger.Log(level, "[{Category}] {Message}", category, safeMessage);
        try { Mirror?.Invoke(category, safeMessage); }
        catch { /* копия журнала не должна ломать программу */ }
        // 2026-10-05, ТЗ часть 7, раздел 3: ошибки — в отчёты для поддержки (ErrorReportService), строки — в его
        // короткую память «что было перед ошибкой». Только уже очищенный от секретов текст.
        try { Observer?.Invoke(level, category, safeMessage); }
        catch { /* отчёты об ошибках не должны ломать программу */ }
    }

    /// <summary>2026-10-05: каждая строка журнала (уровень, раздел, текст без секретов) — для ErrorReportService.</summary>
    public static Action<LogLevel, string, string>? Observer { get; set; }

    private static LogLevel ParseLevel(string category)
    {
        if (category.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Critical;
        if (category.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (category.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        if (category.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("TRACE", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Debug;
        return LogLevel.Information;
    }
}
