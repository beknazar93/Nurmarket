using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Отдельный дисплей цены покупателя (2026-09-04) — табло на корпусе POS-моноблока или
/// отдельная коробочка, НЕ второй Windows-монитор (тот обслуживает AvaloniaCustomerDisplayService).
/// Синглтон по образцу DatabaseService.Instance — вызывается из одного места
/// (BasketPanelViewModel.PushCustomerDisplay).
///
/// <para>Два вида табло (UserPreferences.PoleDisplayProtocol):</para>
/// <para>• «led» — цифровое табло на 8 цифр (LED8N и совместимые; так устроено табло на корпусе
/// моноблоков Superwin CY25 — «0.00» зелёными цифрами). Показывает только число: ESC Q A
/// &lt;цифры&gt; CR, лампочки «цена/итого/оплата/сдача» — ESC s n. По умолчанию такие табло
/// работают на 2400 бод.</para>
/// <para>• «text» — текстовый дисплей 2×20 (CD5220): 0x0C очищает, \r\n — вторая строка.</para>
///
/// <para>2026-09-26, живой случай: на CY25 табло всегда показывало «0.00». Касса слала ему текст
/// CD5220, а COM-порт открывала на 9600 при любой настройке скорости — цифровое табло не понимало
/// ни того, ни другого. Теперь протокол выбирается, скорость берётся из настроек, а «Найти табло»
/// перебирает порты и скорости и показывает на табло номер варианта.</para></summary>
public sealed class PoleDisplayService
{
    public const string ProtocolLed = "led";
    public const string ProtocolText = "text";

    private static readonly Lazy<PoleDisplayService> LazyInstance = new(() => new PoleDisplayService());
    public static PoleDisplayService Instance => LazyInstance.Value;

    private static readonly Regex ComPortPattern = new(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    private string? _devicePath;
    private int _baudRate = 2400;
    private string _protocol = ProtocolLed;
    private string _status = "выключен";

    // Корзина меняется на каждый скан — на табло уходит только последнее значение, по одному
    // письму за раз (COM на 2400 бод пишет десятки миллисекунд).
    private byte[]? _pendingPayload;
    private bool _writerRunning;

    private PoleDisplayService()
    {
    }

    public bool IsAvailable
    {
        get { lock (_lock) return _devicePath is not null; }
    }

    public string Status
    {
        get { lock (_lock) return _status; }
    }

    public static string NormalizeProtocol(string? value) =>
        string.Equals(value?.Trim(), ProtocolText, StringComparison.OrdinalIgnoreCase) ? ProtocolText : ProtocolLed;

    public void Start()
    {
        lock (_lock)
        {
            _devicePath = null;

            var prefs = UserPreferences.Instance;
            _protocol = NormalizeProtocol(prefs.PoleDisplayProtocol);
            _baudRate = prefs.PoleDisplayBaudRate > 0 ? prefs.PoleDisplayBaudRate : 2400;
            if (!prefs.PoleDisplayEnabled)
            {
                _status = "выключен в настройках";
                PosLogger.Log("Дисплей цены: выключен в настройках кассы.", "POLE_DISPLAY");
                return;
            }

            var devicePath = prefs.PoleDisplayComPort;
            if (string.IsNullOrWhiteSpace(devicePath) || HardwareModeHelper.IsNonePort(devicePath))
            {
                _status = "устройство не выбрано";
                PosLogger.Log("Дисплей цены: устройство не выбрано.", "POLE_DISPLAY");
                return;
            }

            _devicePath = devicePath;
            _status = $"выбран ({devicePath}, {_baudRate} бод)";
            PosLogger.Log($"Дисплей цены: {devicePath}, {_baudRate} бод, {(_protocol == ProtocolLed ? "цифровое табло" : "текстовый дисплей CD5220")}.", "POLE_DISPLAY");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _devicePath = null;
            _pendingPayload = null;
            _status = "выключен";
        }
    }

    /// <summary>Текущая сумма чека — из BasketPanelViewModel.PushCustomerDisplay при каждом
    /// изменении корзины (UI-поток), поэтому запись идёт в фоне.</summary>
    public void ShowTotal(double total, int itemCount)
    {
        lock (_lock)
        {
            if (_devicePath is null)
                return;

            _pendingPayload = _protocol == ProtocolLed
                ? BuildLedPayload(total, itemCount > 0 ? LedLamp.Total : LedLamp.Off)
                : BuildTextPayload(itemCount > 0 ? $"Items: {itemCount}" : "Welcome", $"Total: {total.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (_writerRunning)
                return;
            _writerRunning = true;
        }

        Task.Run(DrainPending);
    }

    private void DrainPending()
    {
        while (true)
        {
            string devicePath;
            int baud;
            byte[] payload;
            lock (_lock)
            {
                if (_pendingPayload is null || _devicePath is null)
                {
                    _writerRunning = false;
                    return;
                }

                payload = _pendingPayload;
                _pendingPayload = null;
                devicePath = _devicePath;
                baud = _baudRate;
            }

            try
            {
                Write(devicePath, baud, payload);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Дисплей цены: ошибка записи ({devicePath}): {ex.Message}", "POLE_DISPLAY");
            }
        }
    }

    /// <summary>Кнопка «Тест» в настройках: синхронно и с настоящим результатом записи.</summary>
    public (bool Ok, string Message) ShowTest()
    {
        string? devicePath;
        int baud;
        string protocol;
        lock (_lock)
        {
            devicePath = _devicePath;
            baud = _baudRate;
            protocol = _protocol;
        }

        if (devicePath is null)
            return (false, Status);

        try
        {
            Write(devicePath, baud, protocol == ProtocolLed
                ? BuildLedPayload(1234.56, LedLamp.Total)
                : BuildTextPayload("NURMARKET", "TEST 1234.56"));
            return (true, protocol == ProtocolLed
                ? $"Отправлено «1234.56» на {devicePath} ({baud} бод). Если на табло не появилось — нажмите «Найти табло»."
                : $"Отправлено «NURMARKET / TEST 1234.56» на {devicePath} ({baud} бод).");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Дисплей цены: тест не прошёл: {ex.Message}", "POLE_DISPLAY");
            return (false, ex.Message);
        }
    }

    /// <summary>«Найти табло»: на каждый COM-порт и каждую скорость отправляется число-метка
    /// (порт.скорость, например 3.2400 = COM3, 2400 бод). Табло принимает только свой вариант,
    /// поэтому после перебора на нём остаётся номер подошедшего. Порты чекового принтера и весов
    /// пропускаются.</summary>
    public static async Task<IReadOnlyList<PoleDisplayProbe>> ProbeLedAsync(
        IEnumerable<string> skipPorts,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var skip = new HashSet<string>(skipPorts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
        var ports = SerialPort.GetPortNames()
            .Where(p => ComPortPattern.IsMatch(p) && !skip.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => int.TryParse(p[3..], out var n) ? n : int.MaxValue)
            .ToList();

        var result = new List<PoleDisplayProbe>();
        foreach (var port in ports)
        {
            var number = int.Parse(port[3..], CultureInfo.InvariantCulture);
            // 2400 — последней: это заводская скорость таких табло, её метка и останется на экране.
            foreach (var baud in new[] { 9600, 2400 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = $"{number}.{baud}";
                progress?.Invoke($"{port}, {baud} бод — на табло должно появиться {label}");
                string? error = null;
                try
                {
                    await Task.Run(() => WriteSerial(port, baud, BuildLedPayload(label, LedLamp.Total)), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                }

                result.Add(new PoleDisplayProbe(port, baud, label, error));
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    private static void Write(string devicePath, int baud, byte[] payload)
    {
        if (ComPortPattern.IsMatch(devicePath.Trim()))
            WriteSerial(devicePath.Trim(), baud, payload);
        else
            PrinterPortService.SendRawBytes(devicePath, payload, retries: 1);
    }

    private static void WriteSerial(string port, int baud, byte[] payload)
    {
        using var serial = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 2000,
        };
        serial.Open();
        serial.Write(payload, 0, payload.Length);
        // Close() не ждёт, пока байты уйдут из буфера драйвера: на 2400 бод десяток байт — ~50 мс.
        var deadline = DateTime.UtcNow.AddMilliseconds(1000);
        while (serial.BytesToWrite > 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
    }

    private enum LedLamp
    {
        Off = '0',
        Price = '1',
        Total = '2',
        Collect = '3',
        Change = '4',
    }

    /// <summary>LED8N: ESC s n — лампочка под числом, ESC Q A &lt;число&gt; CR — само число (цифры,
    /// точка и минус; точка места не занимает, цифр не больше 8).</summary>
    private static byte[] BuildLedPayload(double value, LedLamp lamp) => BuildLedPayload(FormatLedNumber(value), lamp);

    private static byte[] BuildLedPayload(string number, LedLamp lamp)
    {
        var bytes = new List<byte> { 0x1B, 0x73, (byte)lamp, 0x1B, 0x51, 0x41 };
        bytes.AddRange(Encoding.ASCII.GetBytes(number));
        bytes.Add(0x0D);
        return bytes.ToArray();
    }

    internal static string FormatLedNumber(double value)
    {
        var abs = Math.Abs(value);
        var text = abs.ToString("0.00", CultureInfo.InvariantCulture);
        if (text.Count(char.IsDigit) > 8)
            text = Math.Round(abs, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        if (text.Count(char.IsDigit) > 8)
            text = "99999999";
        return value < 0 && text.Count(char.IsDigit) < 8 ? "-" + text : text;
    }

    /// <summary>CD5220: 0x0C — очистить и вернуть курсор в начало; \r\n — вторая строка; строки до
    /// 20 символов. Только латиница и цифры: кириллицу такие дисплеи без своей кодовой страницы
    /// показывают знаками вопроса.</summary>
    private static byte[] BuildTextPayload(string line1, string line2)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x0C);
        var bytes1 = Encoding.ASCII.GetBytes(Truncate(line1, 20));
        ms.Write(bytes1, 0, bytes1.Length);
        ms.WriteByte(0x0D);
        ms.WriteByte(0x0A);
        var bytes2 = Encoding.ASCII.GetBytes(Truncate(line2, 20));
        ms.Write(bytes2, 0, bytes2.Length);
        return ms.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Один вариант из «Найти табло»: что и куда отправлено, и ошибка открытия порта, если была.</summary>
public sealed record PoleDisplayProbe(string Port, int BaudRate, string Label, string? Error);
