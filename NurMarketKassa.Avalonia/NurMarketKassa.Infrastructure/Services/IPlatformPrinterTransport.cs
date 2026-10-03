namespace NurMarketKassa.Services;

/// <summary>2026-10-04, Android-касса: отправка сырых байтов (ESC/POS) на принтер средствами
/// платформы, где нет LPT/COM/очереди Windows. Android-программа подставляет свою реализацию в
/// <see cref="PrinterPortService.PlatformTransport"/> (USB host, Bluetooth, сеть TCP 9100).
/// В Windows-кассе не используется.</summary>
public interface IPlatformPrinterTransport
{
    /// <summary>Порт этой платформы? Порт приходит нормализованным (заглавные буквы):
    /// «USB», «USB:0FE6:811E», «BT:00:11:22:33:44:55», «TCP:192.168.1.50:9100».</summary>
    bool Handles(string port);

    /// <summary>Отправить байты; бросает исключение с понятной кассиру причиной.</summary>
    void Write(string port, byte[] payload, TimeSpan timeout);

    /// <summary>Проверка порта для окна настроек (кнопка «Проверить»).</summary>
    PrinterPortService.PortProbeResult Probe(string port);
}
