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

    public static void Log(string message, string category = "INFO")
    {
        var safeMessage = SensitiveDataRedactor.Redact(message);
        var level = ParseLevel(category);
        lock (SyncRoot)
            _logger.Log(level, "[{Category}] {Message}", category, safeMessage);
    }

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
