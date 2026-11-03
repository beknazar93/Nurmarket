namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-14, по просьбе владельца: встроенный (через очередь Windows) чековый
/// принтер время от времени засыпает (USB-энергосбережение) в паузах между продажами — фикс в
/// RawPrinterHelper.WakeUpIfSleepingOrPaused уже снимает паузу очереди и ждёт пробуждения
/// ПЕРЕД каждой печатью, но сам принтер всё равно успевает уснуть между чеками, если между
/// ними проходит несколько минут простоя. Этот сервис периодически шлёт минимальный безопасный
/// no-op (ESC @ — инициализация принтера, не печатает и не подаёт бумагу) на настроенный порт,
/// чтобы порт не успевал простаивать достаточно долго для перехода в сон — устраняет проблему
/// на корню, а не только смягчает её перед печатью.</summary>
public sealed class PrinterKeepAliveService
{
    private static readonly Lazy<PrinterKeepAliveService> LazyInstance = new(() => new PrinterKeepAliveService());
    public static PrinterKeepAliveService Instance => LazyInstance.Value;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);
    private static readonly byte[] NoOpPayload = { 0x1B, 0x40 }; // ESC @ — инициализация принтера

    private readonly object _lock = new();
    private Timer? _timer;
    private int _busy;

    private PrinterKeepAliveService()
    {
    }

    public void Start()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, Interval, Interval);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Тики не накладываются: пока предыдущий не закончил (порт медленный), следующий
    /// пропускается, а не встаёт в очередь отдельным потоком.</summary>
    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            SendHeartbeat();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static void SendHeartbeat()
    {
        try
        {
            if (!HardwareModeHelper.UsePhysicalPrinter())
                return;

            var prefs = UserPreferences.Instance;
            if (!prefs.ReceiptEnabled)
                return;

            var port = HardwarePortHelper.NormalizeLptPort(prefs.ReceiptDevicePath);
            if (string.IsNullOrWhiteSpace(port) || HardwareModeHelper.IsNonePort(port))
                return;

            // Только если порт свободен: keep-alive не должен ни ждать чек, ни задерживать его
            // (2026-09-26 — раньше при зависшем принтере тики копились в очереди к порту).
            if (PrinterPortService.TrySendKeepAlive(port, NoOpPayload))
                NoteRecovered();
        }
        catch (Exception ex)
        {
            NoteFailure(ex.Message);
        }
    }

    // 2026-11-02 (аудит №2, Н3-02): «Printer keep-alive skipped: Порт LPT1 не найден…» писалось каждые 90 с (2 165 строк = 15 % журнала за 2,6 суток, когда принтер
    // в настройках — несуществующий LPT1). Теперь: сразу при новой причине, потом раз в 30 минут с числом пропущенных повторов, и одна строка при восстановлении.
    // Тики не накладываются (_busy), поэтому состояние без блокировок.
    private static readonly TimeSpan RepeatLogEvery = TimeSpan.FromMinutes(30);
    private static string? _lastFailure;
    private static DateTime _lastFailureLoggedUtc = DateTime.MinValue;
    private static DateTime _failingSinceUtc = DateTime.MinValue;
    private static int _suppressed;

    private static void NoteFailure(string message)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(_lastFailure, message, StringComparison.Ordinal))
        {
            if (_lastFailure is null)
                _failingSinceUtc = now;
            _lastFailure = message;
            _lastFailureLoggedUtc = now;
            _suppressed = 0;
            PosLogger.Log($"Printer keep-alive skipped: {message}", "PRINTER");
        }
        else if (now - _lastFailureLoggedUtc >= RepeatLogEvery)
        {
            PosLogger.Log($"Printer keep-alive skipped: {message} (то же ещё {_suppressed} раз за {(now - _lastFailureLoggedUtc).TotalMinutes:0} мин, принтер недоступен с {_failingSinceUtc.ToLocalTime():HH:mm})", "PRINTER");
            _lastFailureLoggedUtc = now;
            _suppressed = 0;
        }
        else
        {
            _suppressed++;
        }
    }

    private static void NoteRecovered()
    {
        if (_lastFailure is null)
            return;
        PosLogger.Log($"Printer keep-alive: принтер снова отвечает (был недоступен с {_failingSinceUtc.ToLocalTime():HH:mm}, пропущено проверок: {_suppressed}).", "PRINTER");
        _lastFailure = null;
        _suppressed = 0;
    }
}
