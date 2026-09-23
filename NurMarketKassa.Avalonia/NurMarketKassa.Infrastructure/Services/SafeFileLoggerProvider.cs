using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Thread-safe rolling text logger with mandatory redaction.</summary>
public sealed class SafeFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileLength = 5 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, SafeFileLogger> _loggers = new();
    private readonly object _writeGate = new();
    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;

    public SafeFileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SafeFileLogger(this, name));

    internal bool IsEnabled(LogLevel level) => !_disposed && level >= _minimumLevel;

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (!IsEnabled(level))
            return;

        var safeMessage = SensitiveDataRedactor.Redact(message);
        var safeException = exception is null ? null : SensitiveDataRedactor.Redact(exception.ToString());
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}]" +
                   (eventId.Id == 0 ? string.Empty : $" [{eventId.Id}]") +
                   $" {safeMessage}" +
                   (safeException is null ? string.Empty : Environment.NewLine + safeException) +
                   Environment.NewLine;

        try
        {
            lock (_writeGate)
            {
                var directory = Path.GetDirectoryName(_path)
                    ?? throw new InvalidOperationException("Log path has no directory.");
                Directory.CreateDirectory(directory);
                RotateIfNeeded();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
#if DEBUG
            Debug.WriteLine($"Log write failed: {writeError.GetType().Name}");
#endif
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaxFileLength)
            return;
        var archive = _path + ".1";
        if (File.Exists(archive))
            File.Delete(archive);
        File.Move(_path, archive);
    }

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }

    private sealed class SafeFileLogger : ILogger
    {
        private readonly SafeFileLoggerProvider _provider;
        private readonly string _category;

        public SafeFileLogger(SafeFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _provider.Write(logLevel, _category, eventId, formatter(state, exception), exception);
        }
    }
}
