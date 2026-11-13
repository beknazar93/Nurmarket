using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

public enum RongtaUdpError
{
    None,
    BadAddress,
    NoReply,
    Rejected,
    SendFailed,
    Cancelled,
}

public sealed record RongtaUdpResult(RongtaUdpError Error, int Packets, int Records, string Detail)
{
    public bool Ok => Error == RongtaUdpError.None;
}

/// <summary>
/// 2026-11-13: транспорт «родного» протокола весов Rongta (<see cref="RongtaUdpProtocol"/>): UDP на порт 5001 весов, пакет → ответ (0x02 / 0x55),
/// при тишине — повтор (как RLS1000: ждёт около секунды, несколько повторов). Шлёт ТОЛЬКО «подключиться», «обновить PLU» (0B) и «отключиться»:
/// очистку памяти, этикетки, шрифты, системные параметры (0E/0014 — переписывает их целиком) и горячие клавиши не отправляет никогда.
/// Весь обмен — в журнал <see cref="ExchangeLogPath"/> (байты в hex). НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО — проверено на локальной ловушке.
/// </summary>
public sealed class RongtaUdpScaleService
{
    private readonly IPEndPoint _endPoint;
    private readonly int _replyTimeoutMs;
    private readonly int _retries;

    public RongtaUdpScaleService(IPAddress address, int port = RongtaUdpProtocol.DefaultPort, int replyTimeoutMs = 1500, int retries = 3)
    {
        _endPoint = new IPEndPoint(address, port is > 0 and <= 65535 ? port : RongtaUdpProtocol.DefaultPort);
        _replyTimeoutMs = Math.Max(200, replyTimeoutMs);
        _retries = Math.Clamp(retries, 0, 10);
    }

    /// <summary>null — адрес не IPv4.</summary>
    public static RongtaUdpScaleService? TryCreate(string? host, int port = RongtaUdpProtocol.DefaultPort)
    {
        if (!IPAddress.TryParse((host ?? "").Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        return new RongtaUdpScaleService(address, port);
    }

    public string Host => _endPoint.Address.ToString();
    public int Port => _endPoint.Port;

    /// <summary>%LOCALAPPDATA%\NurMarketKassa\Logs\rongta-udp-exchange.log (у программы владельца — своя папка).</summary>
    public static string ExchangeLogPath => ExchangeLogPathOverride ?? Path.Combine(
        NurMarketKassa.Services.AppPaths.Local, AppMode.DataFolderName, "Logs", "rongta-udp-exchange.log");

    /// <summary>Для тестов: журнал не в папку установленной кассы.</summary>
    public static string? ExchangeLogPathOverride { get; set; }

    private static readonly object LogLock = new();

    private void Log(string direction, ReadOnlySpan<byte> bytes) => LogText(direction, RongtaUdpProtocol.Hex(bytes));

    private void LogText(string direction, string text)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Host}:{Port} {direction} {text}{Environment.NewLine}";
            lock (LogLock)
            {
                var path = ExchangeLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2_000_000)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // журнал вспомогательный — из-за него запись на весы падать не должна
        }
    }

    /// <summary>Подключиться и отключиться. Так же, как «Проверка порта» RLS1000, с пакетом подключения уходят дата и время ПК
    /// (весы с включённым «автоматически обновлять время» ставят по ним часы — на весах владельца не проверено).</summary>
    public async Task<RongtaUdpResult> ConnectOnlyAsync(DateTime now, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var connect = await ExchangeAsync(udp, RongtaUdpProtocol.BuildConnect(_endPoint.Address, now), ct).ConfigureAwait(false);
        if (connect != RongtaUdpError.None)
            return new RongtaUdpResult(connect, 0, 0, "connect");
        var disconnect = await ExchangeAsync(udp, RongtaUdpProtocol.BuildDisconnect(_endPoint.Address), ct).ConfigureAwait(false);
        return new RongtaUdpResult(disconnect, 2, 0, disconnect == RongtaUdpError.None ? "" : "disconnect");
    }

    /// <summary>Подключиться → карточки командой «обновить PLU» (по 4 в пакете) → отключиться.</summary>
    public async Task<RongtaUdpResult> UpdatePlusAsync(IReadOnlyList<RongtaUdpPlu> plus, DateTime now, CancellationToken ct)
    {
        if (plus.Count == 0)
            return new RongtaUdpResult(RongtaUdpError.None, 0, 0, "");
        var packets = RongtaUdpProtocol.BuildUpdatePackets(plus);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var error = await ExchangeAsync(udp, RongtaUdpProtocol.BuildConnect(_endPoint.Address, now), ct).ConfigureAwait(false);
        if (error != RongtaUdpError.None)
            return new RongtaUdpResult(error, 0, 0, "connect");
        var sent = 0;
        foreach (var packet in packets)
        {
            error = await ExchangeAsync(udp, packet, ct).ConfigureAwait(false);
            if (error != RongtaUdpError.None)
                break;
            sent++;
        }
        // Отключаемся и после ошибки — весы не должны остаться «на связи с ПК».
        var disconnect = await ExchangeAsync(udp, RongtaUdpProtocol.BuildDisconnect(_endPoint.Address), CancellationToken.None).ConfigureAwait(false);
        if (error == RongtaUdpError.None && disconnect != RongtaUdpError.None)
            return new RongtaUdpResult(disconnect, sent, plus.Count, "disconnect");
        var records = Math.Min(plus.Count, sent * RongtaUdpProtocol.RecordsPerPacket);
        return new RongtaUdpResult(error, sent, records, error == RongtaUdpError.None ? "" : $"packet {sent + 1}/{packets.Count}");
    }

    /// <summary>Пакет → ответ. Тишина — повтор того же пакета; 0x55 — отказ весов.</summary>
    private async Task<RongtaUdpError> ExchangeAsync(UdpClient udp, byte[] packet, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= _retries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return RongtaUdpError.Cancelled;
            try
            {
                Log(attempt == 0 ? "TX" : $"TX(повтор {attempt})", packet);
                await udp.SendAsync(packet, packet.Length, _endPoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                LogText("ERR", ex.Message);
                return RongtaUdpError.SendFailed;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_replyTimeoutMs);
            while (true)
            {
                UdpReceiveResult reply;
                try
                {
                    reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        return RongtaUdpError.Cancelled;
                    LogText("RX", "— тишина");
                    break; // повтор
                }
                catch (SocketException ex)
                {
                    // ICMP «порт недоступен» приходит сюда (Windows) — считаем тишиной и повторяем.
                    LogText("RX", "— " + ex.SocketErrorCode);
                    break;
                }
                if (!reply.RemoteEndPoint.Address.Equals(_endPoint.Address))
                    continue; // чужой пакет
                Log("RX", reply.Buffer);
                switch (RongtaUdpProtocol.Classify(reply.Buffer))
                {
                    case RongtaUdpProtocol.ReplyKind.Ack:
                        return RongtaUdpError.None;
                    case RongtaUdpProtocol.ReplyKind.Nak:
                        return RongtaUdpError.Rejected;
                    default:
                        continue; // не подтверждение — ждём дальше
                }
            }
        }
        return RongtaUdpError.NoReply;
    }
}
