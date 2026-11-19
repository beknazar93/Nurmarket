using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

public enum CasError
{
    None,
    BadAddress,
    ConnectFailed,
    NoReply,
    Rejected,
    ConnectionLost,
    SendFailed,
    Cancelled,
}

public sealed record CasResult(CasError Error, int Sent, int ScaleErrorNumber, string Detail)
{
    public bool Ok => Error == CasError.None;
}

public sealed record CasStatusResult(CasError Error, CasProtocol.ScaleStatus? Status, string Detail)
{
    public bool Ok => Error == CasError.None;
}

/// <summary>
/// 2026-11-13: транспорт весов CAS (<see cref="CasProtocol"/>): TCP, кадр → ответ «G…» (принято) / «N…» (отказ, последний байт данных — номер ошибки).
/// Шлёт ТОЛЬКО «записать PLU» (WL) по одной карточке и «прочитать состояние» (RN). Стирание, этикетки, штрихкоды, отделы — никогда.
/// Весь обмен — в журнал <see cref="ExchangeLogPath"/> (hex). НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО — проверено на локальной ловушке.
/// </summary>
public sealed class CasScaleService
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _replyTimeoutMs;
    private readonly int _connectTimeoutMs;

    public CasScaleService(string host, int port = CasProtocol.DefaultPort, int replyTimeoutMs = 3000, int connectTimeoutMs = 3000)
    {
        _host = host;
        _port = port is > 0 and <= 65535 ? port : CasProtocol.DefaultPort;
        _replyTimeoutMs = Math.Max(300, replyTimeoutMs);
        _connectTimeoutMs = Math.Max(300, connectTimeoutMs);
    }

    /// <summary>null — пустой адрес.</summary>
    public static CasScaleService? TryCreate(string? host, int port = CasProtocol.DefaultPort)
    {
        var h = (host ?? "").Trim();
        return h.Length == 0 ? null : new CasScaleService(h, port);
    }

    public string Host => _host;
    public int Port => _port;

    /// <summary>%LOCALAPPDATA%\NurMarketKassa\Logs\cas-exchange.log (у программы владельца — своя папка).</summary>
    public static string ExchangeLogPath => ExchangeLogPathOverride ?? Path.Combine(
        AppPaths.Local, AppMode.DataFolderName, "Logs", "cas-exchange.log");

    /// <summary>Для тестов: журнал не в папку установленной кассы.</summary>
    public static string? ExchangeLogPathOverride { get; set; }

    private static readonly object LogLock = new();

    private void LogText(string direction, string text)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {_host}:{_port} {direction} {text}{Environment.NewLine}";
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

    /// <summary>«Проверить связь»: подключиться и прочитать состояние весов (RN). Ничего не записывает.</summary>
    public async Task<CasStatusResult> ReadStatusAsync(CancellationToken ct)
    {
        var (client, error) = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new CasStatusResult(error, null, "connect");
        using (client)
        {
            var stream = client.GetStream();
            var (reply, err) = await ExchangeAsync(stream, CasProtocol.BuildReadStatus(), ct).ConfigureAwait(false);
            if (err != CasError.None)
                return new CasStatusResult(err, null, "status");
            if (reply is null || reply.Kind != CasProtocol.ReplyKind.Status)
                return new CasStatusResult(CasError.Rejected, null, reply?.Opcode ?? "");
            // Некоторые весы (CL3000 по описанию клиента alexesDev/cas) отвечают нулями — связь всё равно есть.
            return new CasStatusResult(CasError.None, CasProtocol.ParseStatus(reply.Data), "");
        }
    }

    /// <summary>Записывает карточки по одной (WL), ждёт «G» на каждую. Останавливается на первой ошибке.</summary>
    public async Task<CasResult> SendPlusAsync(IReadOnlyList<CasPlu> plus, CancellationToken ct, IProgress<int>? progress = null)
    {
        if (plus.Count == 0)
            return new CasResult(CasError.None, 0, 0, "");
        var (client, error) = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new CasResult(error, 0, 0, "connect");
        using (client)
        {
            var stream = client.GetStream();
            var sent = 0;
            foreach (var plu in plus)
            {
                var (reply, err) = await ExchangeAsync(stream, CasProtocol.BuildDownloadPlu(plu), ct).ConfigureAwait(false);
                if (err != CasError.None)
                    return new CasResult(err, sent, 0, $"PLU {plu.PluNumber}");
                if (reply is null || reply.Kind == CasProtocol.ReplyKind.Unknown || (reply.Kind == CasProtocol.ReplyKind.Good && reply.Opcode != "GL"))
                    return new CasResult(CasError.Rejected, sent, 0, $"PLU {plu.PluNumber}: {reply?.Opcode}");
                if (reply.Kind == CasProtocol.ReplyKind.NoGood)
                    return new CasResult(CasError.Rejected, sent, reply.ErrorNumber, $"PLU {plu.PluNumber}");
                sent++;
                progress?.Report(sent);
            }
            return new CasResult(CasError.None, sent, 0, "");
        }
    }

    private async Task<(TcpClient? Client, CasError Error)> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_connectTimeoutMs);
            LogText("CONNECT", "");
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            return (client, CasError.None);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            LogText("ERR", ct.IsCancellationRequested ? "остановлено" : "тайм-аут подключения");
            return (null, ct.IsCancellationRequested ? CasError.Cancelled : CasError.ConnectFailed);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            client.Dispose();
            LogText("ERR", ex.Message);
            return (null, CasError.ConnectFailed);
        }
    }

    /// <summary>Кадр → ответ (до CR). Тишина дольше таймаута — NoReply (повтор записи PLU безопасен: карточка перезаписывается, но автоматически не повторяем).</summary>
    private async Task<(CasProtocol.Reply? Reply, CasError Error)> ExchangeAsync(NetworkStream stream, byte[] packet, CancellationToken ct)
    {
        try
        {
            LogText("TX", CasProtocol.Hex(packet));
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (null, CasError.Cancelled);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            LogText("ERR", ex.Message);
            return (null, CasError.SendFailed);
        }

        var buf = new List<byte>(32);
        var one = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_replyTimeoutMs);
        try
        {
            while (buf.Count < 1024)
            {
                var n = await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    LogText("RX", "— весы закрыли соединение");
                    return (null, CasError.ConnectionLost);
                }
                buf.Add(one[0]);
                if (one[0] == CasProtocol.Cr && CasProtocol.ParseReply(buf.ToArray()) is not null)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return (null, CasError.Cancelled);
            LogText("RX", buf.Count == 0 ? "— тишина" : "— обрыв ответа: " + CasProtocol.Hex(buf.ToArray()));
            return (null, CasError.NoReply);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            LogText("ERR", ex.Message);
            return (null, CasError.ConnectionLost);
        }
        LogText("RX", CasProtocol.Hex(buf.ToArray()));
        var reply = CasProtocol.ParseReply(buf.ToArray());
        if (reply is { ChecksumOk: false })
            LogText("WARN", "контрольная сумма ответа не сошлась");
        return (reply, CasError.None);
    }
}
