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

    private PrinterKeepAliveService()
    {
    }

    public void Start()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => SendHeartbeat(), null, Interval, Interval);
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

            // retries: 1 — это фоновый keep-alive, а не настоящая печать: если порт сейчас
            // занят (кассир как раз печатает чек), пробовать заново нет смысла, следующий тик
            // таймера всё равно придёт через Interval.
            PrinterPortService.SendRawBytes(port, NoOpPayload, retries: 1);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer keep-alive skipped: {ex.Message}", "PRINTER");
        }
    }
}
