using System.Net.Sockets;
using System.Text.RegularExpressions;

#nullable enable

namespace NurMarketKassa.Services;

// 2026-11-01 (просьба владельца: «принтеры и по LAN соединение тоже добавь»): сетевой принтер (LAN/Wi-Fi, «RAW»/JetDirect, TCP 9100).
// Адрес порта — «TCP:192.168.1.50:9100» (также «NET:адрес», «IP:адрес» и просто «192.168.1.50[:порт]», порт по умолчанию 9100).
// Один разбор адреса для всех платформ: Windows (PrinterPortService), Linux/macOS (PosixPrinterTransport) и Android (AndroidPrinterTransport).
// Соединение открывается на время одного задания и закрывается (постоянных соединений нет); проверка связи — только подключение, без отправки данных.

/// <summary>Адрес сетевого принтера: разбор, проверка, запись сырых байтов и проверка доступности.</summary>
public static class NetworkPrinterAddress
{
    public const int DefaultPort = 9100;

    /// <summary>Сколько ждать соединения при печати.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Сколько ждать соединения при проверке связи (настройки, статус, keep-alive).</summary>
    public const int ProbeTimeoutMs = 2000;

    private const int ChunkBytes = 16 * 1024;

    private static readonly Regex Ipv4Rx = new(@"^(?:\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);
    private static readonly Regex BarePortRx = new(@"^\s*\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?\s*$", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,251}[A-Za-z0-9])?$", RegexOptions.Compiled);

    /// <summary>Порт — это сетевой принтер (TCP:, NET:, IP: или голый IPv4 с необязательным портом).</summary>
    public static bool IsNetworkPort(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
            return false;
        var p = port.TrimStart();
        return p.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("NET:", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("IP:", StringComparison.OrdinalIgnoreCase) || BarePortRx.IsMatch(p);
    }

    /// <summary>Проверка имени или IPv4-адреса: четыре числа 0–255 либо имя хоста (буквы, цифры, «-», «.»).</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var h = host.Trim();
        if (Ipv4Rx.IsMatch(h))
            return h.Split('.').All(o => int.TryParse(o, out var n) && n is >= 0 and <= 255);
        return HostRx.IsMatch(h) && !h.All(c => char.IsDigit(c) || c == '.');
    }

    /// <summary>Разбор адреса порта. false — адрес неверный, <paramref name="error"/> — понятная причина на языке кассы.</summary>
    public static bool TryParse(string? port, out string host, out int tcpPort, out string? error)
    {
        host = "";
        tcpPort = DefaultPort;
        error = null;
        var rest = (port ?? "").Trim();
        var colon0 = rest.IndexOf(':');
        if (colon0 > 0 && (rest.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("NET:", StringComparison.OrdinalIgnoreCase)
                           || rest.StartsWith("IP:", StringComparison.OrdinalIgnoreCase)))
            rest = rest[(colon0 + 1)..];
        rest = rest.Trim().TrimStart('/');
        var h = rest;
        var colon = rest.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(rest[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                error = PortRangeText;
                return false;
            }
            h = rest[..colon];
            tcpPort = p;
        }
        if (string.IsNullOrWhiteSpace(h))
        {
            error = MissingHostText;
            return false;
        }
        if (!IsValidHost(h))
        {
            error = BadHostText;
            return false;
        }
        host = h.Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>Разбор адреса; неверный адрес — исключение с понятным текстом.</summary>
    public static (string Host, int Port) ParseOrThrow(string? port)
    {
        if (!TryParse(port, out var host, out var tcpPort, out var error))
            throw new InvalidOperationException(error);
        return (host, tcpPort);
    }

    /// <summary>Единая запись адреса: «TCP:192.168.1.50:9100» (заглавные буквы — как у всех портов после NormalizePort).</summary>
    public static string Format(string host, int port) => $"TCP:{host.Trim().ToUpperInvariant()}:{port}";

    /// <summary>Канонический вид порта для ключа очереди и сохранения; не сетевой или неверный адрес — как есть.</summary>
    public static string Canonical(string? port) =>
        IsNetworkPort(port) && TryParse(port, out var host, out var tcpPort, out _) ? Format(host, tcpPort) : (port ?? "");

    /// <summary>«192.168.1.50:9100» — для сообщений и списка.</summary>
    public static string Display(string? port) =>
        TryParse(port, out var host, out var tcpPort, out _) ? $"{host}:{tcpPort}" : (port ?? "");

    // ------------------------------------------------------------------ тексты

    private static string MissingHostText => Tr.T("Не указан адрес сетевого принтера (TCP:адрес).", "Тармактык принтердин дареги көрсөтүлгөн эмес (TCP:дарек).", "Network printer address is missing (TCP:address).", "Ağ yazıcısının adresi belirtilmedi (TCP:adres).", "Tarmoq printeri manzili ko'rsatilmagan (TCP:manzil).");
    private static string BadHostText => Tr.T("Неверный адрес принтера: нужен IP-адрес (например 192.168.1.50) или имя.", "Принтердин дареги туура эмес: IP-дарек (мисалы 192.168.1.50) же ат керек.", "Invalid printer address: an IP address (e.g. 192.168.1.50) or a host name is required.", "Geçersiz yazıcı adresi: IP adresi (ör. 192.168.1.50) veya ad gerekli.", "Printer manzili noto'g'ri: IP-manzil (masalan 192.168.1.50) yoki nom kerak.");
    private static string PortRangeText => Tr.T("Порт должен быть числом от 1 до 65535.", "Порт 1ден 65535ке чейинки сан болушу керек.", "The port must be a number from 1 to 65535.", "Port 1 ile 65535 arasında bir sayı olmalı.", "Port 1 dan 65535 gacha son bo'lishi kerak.");

    private static string NoAnswerText(string host, int port) => Tr.T($"Сетевой принтер {host}:{port} не отвечает (проверьте питание, сеть и адрес).", $"Тармактык принтер {host}:{port} жооп бербейт (кубатты, тармакты жана даректи текшериңиз).",
        $"Network printer {host}:{port} is not responding (check power, network and address).", $"Ağ yazıcısı {host}:{port} yanıt vermiyor (güç, ağ ve adresi kontrol edin).", $"Tarmoq printeri {host}:{port} javob bermayapti (quvvat, tarmoq va manzilni tekshiring).");

    private static string InterruptedText(string host, int port) => Tr.T($"Печать на {host}:{port} прервалась после отправки части данных — проверьте чек (этикетку) на ленте, повтор не делался, чтобы не напечатать дубль.", $"{host}:{port} дарегине басуу маалыматтын бир бөлүгү жөнөтүлгөндөн кийин үзгүлтүккө учурады — лентадагы чекти (этикетканы) текшериңиз, кайталоо жасалган жок.",
        $"Printing to {host}:{port} was interrupted after part of the data was sent — check the receipt (label) on the tape; no retry was made to avoid a duplicate.", $"{host}:{port} yazdırması verinin bir kısmı gönderildikten sonra kesildi — banttaki fişi (etiketi) kontrol edin; kopya olmaması için yeniden denenmedi.", $"{host}:{port} ga chop etish ma'lumotning bir qismi yuborilgandan keyin uzildi — lentadagi chek (yorliq)ni tekshiring, dublikat bo'lmasligi uchun qayta urinilmadi.");

    private static string RefusedText(string host, int port) => Tr.T($"Сетевой принтер {host}:{port} отказал в соединении (порт закрыт или занят другой программой).", $"Тармактык принтер {host}:{port} туташууну четке какты (порт жабык же башка программа ээлеген).",
        $"Network printer {host}:{port} refused the connection (the port is closed or used by another program).", $"Ağ yazıcısı {host}:{port} bağlantıyı reddetti (port kapalı veya başka program kullanıyor).", $"Tarmoq printeri {host}:{port} ulanishni rad etdi (port yopiq yoki boshqa dastur band qilgan).");

    private static string NotFoundText(string host) => Tr.T($"Имя «{host}» не найдено в сети — укажите IP-адрес принтера.", $"«{host}» аты тармакта табылган жок — принтердин IP-дарегин көрсөтүңүз.",
        $"The name “{host}” was not found on the network — enter the printer's IP address.", $"«{host}» adı ağda bulunamadı — yazıcının IP adresini girin.", $"«{host}» nomi tarmoqda topilmadi — printerning IP-manzilini kiriting.");

    // ------------------------------------------------------------------ соединение

    private enum ConnectResult { Ok, NoAnswer, Refused, NotFound }

    private static ConnectResult TryConnect(TcpClient client, string host, int port, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return client.Connected ? ConnectResult.Ok : ConnectResult.NoAnswer;
        }
        catch (OperationCanceledException)
        {
            return ConnectResult.NoAnswer;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ConnectResult.Refused,
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ConnectResult.NotFound,
                _ => ConnectResult.NoAnswer,
            };
        }
    }

    /// <summary>Проверка связи: только подключение к адресу (данные не отправляются, соединение сразу закрывается). Не вызывать с потока интерфейса.</summary>
    public static PrinterPortService.PortProbeResult Probe(string? port, int timeoutMs = ProbeTimeoutMs)
    {
        if (!TryParse(port, out var host, out var tcpPort, out var error))
            return new PrinterPortService.PortProbeResult(false, "○ " + error, "tcp");
        using var client = new TcpClient();
        var result = TryConnect(client, host, tcpPort, timeoutMs);
        return result switch
        {
            ConnectResult.Ok => new PrinterPortService.PortProbeResult(true, Tr.T($"● Доступен (сеть {host}:{tcpPort})", $"● Жеткиликтүү (тармак {host}:{tcpPort})", $"● Available (network {host}:{tcpPort})", $"● Kullanılabilir (ağ {host}:{tcpPort})", $"● Mavjud (tarmoq {host}:{tcpPort})"), "tcp"),
            ConnectResult.Refused => new PrinterPortService.PortProbeResult(false, Tr.T($"○ Отказ в соединении {host}:{tcpPort}", $"○ {host}:{tcpPort} туташууну четке какты", $"○ Connection refused {host}:{tcpPort}", $"○ Bağlantı reddedildi {host}:{tcpPort}", $"○ {host}:{tcpPort} ulanishni rad etdi"), "tcp"),
            ConnectResult.NotFound => new PrinterPortService.PortProbeResult(false, Tr.T($"○ Имя «{host}» не найдено", $"○ «{host}» аты табылган жок", $"○ Name “{host}” not found", $"○ «{host}» adı bulunamadı", $"○ «{host}» nomi topilmadi"), "tcp"),
            _ => new PrinterPortService.PortProbeResult(false, Tr.T($"○ Нет ответа от {host}:{tcpPort}", $"○ {host}:{tcpPort} жооп бербейт", $"○ No answer from {host}:{tcpPort}", $"○ {host}:{tcpPort} yanıt vermiyor", $"○ {host}:{tcpPort} javob bermayapti"), "tcp"),
        };
    }

    /// <summary>Отправка сырых байтов (ESC/POS, TSPL, ZPL…): подключение с таймаутом, запись пачками по 16 КБ с таймаутом, корректное закрытие.
    /// Нет ответа — <see cref="TimeoutException"/> (повтор не делается), отказ или обрыв — <see cref="IOException"/> (повторяет вызывающий).</summary>
    public static void Write(string? port, byte[] payload, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var (host, tcpPort) = ParseOrThrow(port);
        var writeMs = (int)Math.Clamp(timeout.TotalMilliseconds, 5_000, 10_000); // таймаут записи 5–10 с: зависшее соединение не держим
        using var client = new TcpClient { NoDelay = true, SendTimeout = writeMs, LingerState = new LingerOption(true, 3) };
        switch (TryConnect(client, host, tcpPort, (int)ConnectTimeout.TotalMilliseconds))
        {
            case ConnectResult.Refused:
                throw new IOException(RefusedText(host, tcpPort));
            case ConnectResult.NotFound:
                throw new InvalidOperationException(NotFoundText(host));
            case ConnectResult.NoAnswer:
                throw new TimeoutException(NoAnswerText(host, tcpPort));
        }
        using var stream = client.GetStream();
        stream.WriteTimeout = writeMs;
        try
        {
            for (var offset = 0; offset < payload.Length; offset += ChunkBytes)
                stream.Write(payload, offset, Math.Min(ChunkBytes, payload.Length - offset));
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 2026-11-01 (ревью): соединение уже было, часть байтов могла уйти и напечататься. Повторять нельзя — получится частичный чек и дубли
            // (SendRawBytes не повторяет PrinterStalledException). Повтор безопасен только когда подключиться не удалось.
            PosLogger.Log($"Сеть {host}:{tcpPort}: запись прервалась ({ex.GetType().Name}); повтор не делаю.", "PRINTER");
            throw new PrinterPortService.PrinterStalledException(InterruptedText(host, tcpPort), ex);
        }
        try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* принтер мог закрыть соединение сам */ }
        PosLogger.Log($"Сеть {host}:{tcpPort}: отправлено {payload.Length} байт", "PRINTER");
    }

    // ------------------------------------------------------------------ запомненные сетевые принтеры

    private const int MaxKnown = 20;

    /// <summary>Запомненные сетевые принтеры (выбранные в настройках или найденные поиском) — для списка рядом с USB и очередями Windows.</summary>
    public static IReadOnlyList<string> Known() =>
        (UserPreferences.Instance.KnownNetworkPrinters ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsNetworkPort).Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Запоминает адрес (последние 20). Неверный адрес не запоминается.</summary>
    public static void Remember(string? port)
    {
        if (!IsNetworkPort(port) || !TryParse(port, out var host, out var tcpPort, out _))
            return;
        var canon = Format(host, tcpPort);
        var list = Known().ToList();
        if (list.Any(x => string.Equals(x, canon, StringComparison.OrdinalIgnoreCase)))
            return;
        list.Insert(0, canon);
        UserPreferences.Instance.KnownNetworkPrinters = string.Join(';', list.Take(MaxKnown));
        try
        {
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Network printer list not saved: {ex.GetType().Name}", "WARNING");
        }
    }

    /// <summary>Пункт списка принтеров: «🌐 Сеть: 192.168.1.50:9100».</summary>
    public static DiscoveredPrinter ToDiscovered(string port) =>
        new(Tr.T($"🌐 Сеть: {Display(port)}", $"🌐 Тармак: {Display(port)}", $"🌐 Network: {Display(port)}", $"🌐 Ağ: {Display(port)}", $"🌐 Tarmoq: {Display(port)}"), Canonical(port));
}
