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

    // 2026-10-22 (оптимизация): запись в файл раньше шла прямо на вызывающем потоке (в том числе на UI-потоке) — на каждую строку журнала:
    // 5 регулярных выражений скрытия секретов, CreateDirectory, File.Exists + FileInfo (проверка размера) и File.AppendAllText под общей
    // блокировкой. С антивирусом (Defender проверяет каждую дозапись) это 5–50 мс на строку; пачки строк (синхронизация каталога,
    // «Долги клиентов», запросы API) подвешивали окно. Теперь UI-поток только кладёт строку в очередь, а скрытие секретов, ротацию и запись
    // делает один фоновый поток, сразу пачкой. Ошибки (Error/Critical) ждут записи до 1 с — последние строки перед падением не теряются.
    private readonly System.Collections.Concurrent.BlockingCollection<Entry> _queue = new(new System.Collections.Concurrent.ConcurrentQueue<Entry>(), 20_000);
    private readonly Thread _worker;
    private int _pending;

    private readonly record struct Entry(DateTimeOffset At, LogLevel Level, string Category, EventId EventId, string Message, Exception? Exception);

    public SafeFileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _minimumLevel = minimumLevel;
        _worker = new Thread(WriterLoop) { IsBackground = true, Name = "NurMarketKassa-LogWriter", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
        // Процесс завершается без Dispose — дописать очередь (не дольше 2 с).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(2000);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SafeFileLogger(this, name));

    internal bool IsEnabled(LogLevel level) => !_disposed && level >= _minimumLevel;

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        if (!IsEnabled(level))
            return;

        Interlocked.Increment(ref _pending);
        if (!_queue.TryAdd(new Entry(DateTimeOffset.Now, level, category, eventId, message, exception)))
        {
            // Очередь полна (диск не успевает) или журнал закрыт — строку теряем, программу не тормозим.
            Interlocked.Decrement(ref _pending);
            return;
        }

        if (level >= LogLevel.Error)
            Flush(1000);
    }

    /// <summary>Подождать, пока фоновый поток допишет очередь (не дольше <paramref name="timeoutMs"/>).</summary>
    internal void Flush(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && sw.ElapsedMilliseconds < timeoutMs && _worker.IsAlive)
            Thread.Sleep(2);
    }

    private void WriterLoop()
    {
        var batch = new List<Entry>(64);
        var sb = new StringBuilder();
        try
        {
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                batch.Clear();
                batch.Add(first);
                while (batch.Count < 256 && _queue.TryTake(out var next))
                    batch.Add(next);

                sb.Clear();
                foreach (var e in batch)
                {
                    var safeMessage = SensitiveDataRedactor.Redact(e.Message);
                    var safeException = e.Exception is null ? null : SensitiveDataRedactor.Redact(e.Exception.ToString());
                    sb.Append($"{e.At:yyyy-MM-dd HH:mm:ss.fff zzz} [{e.Level}] [{e.Category}]")
                      .Append(e.EventId.Id == 0 ? string.Empty : $" [{e.EventId.Id}]")
                      .Append(' ').Append(safeMessage)
                      .Append(safeException is null ? string.Empty : Environment.NewLine + safeException)
                      .Append(Environment.NewLine);
                }

                try
                {
                    lock (_writeGate)
                    {
                        var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("Log path has no directory.");
                        Directory.CreateDirectory(directory);
                        RotateIfNeeded();
                        File.AppendAllText(_path, sb.ToString(), Encoding.UTF8);
                    }
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
                {
#if DEBUG
                    Debug.WriteLine($"Log write failed: {writeError.GetType().Name}");
#endif
                }
                finally
                {
                    Interlocked.Add(ref _pending, -batch.Count);
                }
            }
        }
        catch (Exception)
        {
            // журнал не должен ронять программу
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
        Flush(2000);
        _disposed = true;
        _queue.CompleteAdding();
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
