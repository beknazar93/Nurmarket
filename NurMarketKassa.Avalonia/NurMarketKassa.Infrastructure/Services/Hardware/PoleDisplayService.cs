using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Отдельный дисплей цены покупателя (2026-09-04) — маленькая коробочка с LED/VFD-табло,
/// NFC-считывателем и т.п., НЕ второй Windows-монитор (тот уже обслуживает
/// AvaloniaCustomerDisplayService). Синглтон по образцу DatabaseService.Instance — этому
/// устройству не нужен DI-интерфейс, оно вызывается из одного места (BasketPanelViewModel.
/// PushCustomerDisplay).
/// <para>Раньше держал постоянно открытый SerialPort (только COM-порт) — пользователь сообщил,
/// что его устройство подключается по USB и "не видит" ничего в списке COM-портов. Переведено на
/// PrinterPortService.SendRawBytes — тот же способ отправки, что уже работает для чеков/этикеток/
/// ценников: адрес устройства (COM, LPT, WinUSB VID:PID через Zadig, "сырой" \\.\USBxxx) выбирается
/// один раз в настройках из полного PrinterDiscoveryService.Discover(), а запись просто открывает
/// нужный канал на каждый вызов вместо постоянного соединения.</para>
/// <para>Протокол — CD5220 (самый распространённый среди недорогих 2-строчных LED/VFD дисплеев
/// покупателя: 0x0C очищает и переводит курсор в начало, \r\n переводит на вторую строку). Это НЕ
/// проверено на реальном устройстве пользователя — нет физического доступа к железу из этой среды.
/// Кнопка "Тест" в настройках нужна именно для того, чтобы сразу увидеть, работает ли этот протокол
/// на конкретном устройстве, и если нет — скорректировать по факту.</para></summary>
public sealed class PoleDisplayService
{
    private static readonly Lazy<PoleDisplayService> LazyInstance = new(() => new PoleDisplayService());
    public static PoleDisplayService Instance => LazyInstance.Value;

    private readonly object _lock = new();
    private string? _devicePath;
    private string _status = "выключен";

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

    public void Start()
    {
        lock (_lock)
        {
            _devicePath = null;

            var prefs = UserPreferences.Instance;
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
            _status = $"выбран ({devicePath})";
            PosLogger.Log($"Дисплей цены: выбрано устройство {devicePath}.", "POLE_DISPLAY");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _devicePath = null;
            _status = "выключен";
        }
    }

    /// <summary>Текущая сумма чека — вызывается из BasketPanelViewModel.PushCustomerDisplay при
    /// каждом изменении корзины. Отправка — в фоновом потоке (Task.Run): в отличие от прежнего
    /// постоянно открытого SerialPort, каждый вызов теперь сам открывает и закрывает канал
    /// (PrinterPortService.SendRawBytes), а это может занять заметное время на COM/LPT — не должно
    /// подвешивать интерфейс кассы на каждое изменение количества в корзине.</summary>
    public void ShowTotal(double total, int itemCount)
    {
        string? devicePath;
        lock (_lock) devicePath = _devicePath;
        if (devicePath is null)
            return;

        var line1 = itemCount > 0 ? $"Товаров: {itemCount}" : "Ожидание";
        var line2 = $"Итого: {total:0.00}";
        var payload = BuildPayload(line1, line2);
        Task.Run(() => WriteRaw(devicePath, payload));
    }

    /// <summary>Тестовая строка для кнопки "Тест" в настройках — синхронная (в отличие от
    /// ShowTotal), т.к. это осознанное разовое действие пользователя, а не фоновое обновление на
    /// каждый клик в корзине; UI-поток вправе на мгновение подождать реального результата.</summary>
    public bool ShowTest()
    {
        string? devicePath;
        lock (_lock) devicePath = _devicePath;
        if (devicePath is null)
            return false;

        try
        {
            WriteRaw(devicePath, BuildPayload("КАССА MARKET PLUS", "ТЕСТ 12.34"));
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Дисплей цены: тест не прошёл: {ex.Message}", "POLE_DISPLAY");
            return false;
        }
    }

    /// <summary>CD5220: 0x0C — очистить экран и вернуть курсор в начало первой строки; \r\n —
    /// перевести курсор на вторую строку. Строки обрезаются до 20 символов — типичная ширина таких
    /// табло; для более узких/широких дисплеев может понадобиться подстройка.</summary>
    private static byte[] BuildPayload(string line1, string line2)
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

    private static void WriteRaw(string devicePath, byte[] payload)
    {
        try
        {
            PrinterPortService.SendRawBytes(devicePath, payload, retries: 1);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Дисплей цены: ошибка записи ({devicePath}): {ex.Message}", "POLE_DISPLAY");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
