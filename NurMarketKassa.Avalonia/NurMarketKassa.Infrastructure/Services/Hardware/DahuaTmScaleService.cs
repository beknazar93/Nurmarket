using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Как слать строки PLU.</summary>
public enum DahuaTmSendMode
{
    /// <summary>По одной строке с ожиданием ответа — так делает их datatransters.dll
    /// (0x1000daa0: отправить элемент списка → ждать ответ → следующий). По умолчанию.</summary>
    LineByLine,

    /// <summary>Все строки одной записью в сокет, потом собрать ответы. Запасной режим — если
    /// весы отвечают не так, как ждёт модель, и «по строке» упирается в таймауты.</summary>
    Batch,
}

public enum DahuaTmError
{
    None,
    BadAddress,
    ConnectFailed,
    NoReply,
    ConnectionLost,
    SendFailed,
    Cancelled,
}

public sealed record DahuaTmCheckResult(
    bool Connected,
    bool Replied,
    DahuaTmProtocol.ReplyKind ReplyKind,
    string ReplyText,
    long ConnectMs,
    DahuaTmError Error,
    string Detail);

public sealed record DahuaTmUploadProgress(int Done, int Total, int PluNumber);

public sealed class DahuaTmUploadResult
{
    public int Total { get; init; }

    /// <summary>Строк записано в сокет.</summary>
    public int Sent { get; set; }

    /// <summary>Строк, на которые пришёл полный ответ (по правилам их DLL).</summary>
    public int Acknowledged { get; set; }

    /// <summary>Строк, после которых пришли байты без маркера конца ответа — приняли как ответ,
    /// но это значит, что модель ответа неточна (смотрите журнал обмена).</summary>
    public int UnframedReplies { get; set; }

    /// <summary>Сколько раз строку пришлось повторить из-за тишины.</summary>
    public int Retries { get; set; }

    public DahuaTmError Error { get; set; }

    /// <summary>PLU, на котором остановились (0 — нет).</summary>
    public int FailedPlu { get; set; }

    public string Detail { get; set; } = "";

    public bool Ok => Error == DahuaTmError.None && Acknowledged + UnframedReplies >= Total;
}

/// <summary>
/// 2026-09-28: драйвер весов Dahua TM (TM-30F «BARCODE PRINTING SCALE» у владельца) по сети —
/// просьба владельца «чтобы наша программа могла напрямую отправлять на весы TM-30F». Протокол и
/// его источники — в <see cref="DahuaTmProtocol"/>. Здесь только транспорт: TCP-клиент к IP весов
/// (порт 4001), строка → ответ → следующая строка, таймаут 2500 мс и 4 повтора, как у их DLL.
///
/// Шлёт ТОЛЬКО строки PLU (!0V) и безопасное чтение (!0J). Очистку PLU, инициализацию,
/// этикетки, горячие клавиши и системные параметры не отправляет никогда.
///
/// Весь обмен пишется в журнал <see cref="ExchangeLogPath"/> (рядом с nurmarket-kassa.log):
/// если на живых весах что-то пойдёт не так, по нему сразу видно, что весы ответили.
/// НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО — проверено на эмуляторе с моделью ответа из их DLL.
/// </summary>
public sealed class DahuaTmScaleService
{
    private readonly IPEndPoint _endPoint;
    private readonly int _replyTimeoutMs;
    private readonly int _retries;
    private readonly int _connectTimeoutMs;

    public DahuaTmScaleService(IPAddress address, int port = DahuaTmProtocol.DefaultPort,
        int replyTimeoutMs = DahuaTmProtocol.DefaultReplyTimeoutMs, int retries = DahuaTmProtocol.DefaultRetries,
        int connectTimeoutMs = 3000)
    {
        _endPoint = new IPEndPoint(address, port is > 0 and <= 65535 ? port : DahuaTmProtocol.DefaultPort);
        _replyTimeoutMs = Math.Max(300, replyTimeoutMs);
        _retries = Math.Clamp(retries, 0, 10);
        _connectTimeoutMs = Math.Max(500, connectTimeoutMs);
    }

    /// <summary>null — адрес не IP (окно показывает понятную ошибку само).</summary>
    public static DahuaTmScaleService? TryCreate(string? host, int port)
    {
        if (!IPAddress.TryParse((host ?? "").Trim(), out var address))
            return null;
        return new DahuaTmScaleService(address, port);
    }

    public string Host => _endPoint.Address.ToString();
    public int Port => _endPoint.Port;

    /// <summary>%LOCALAPPDATA%\NurMarketKassa\Logs\tm30f-exchange.log (у программы владельца — своя папка).</summary>
    public static string ExchangeLogPath => ExchangeLogPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppMode.DataFolderName, "Logs", "tm30f-exchange.log");

    /// <summary>Для стенда: писать журнал в другое место, не в папку установленной кассы.</summary>
    public static string? ExchangeLogPathOverride { get; set; }

    private static readonly object LogLock = new();

    private void Log(string direction, string text)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Host}:{Port} {direction} {DahuaTmProtocol.Visible(text)}{Environment.NewLine}";
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
            // Журнал обмена — вспомогательный: из-за него выгрузка падать не должна.
        }
    }

    // ------------------------------------------------------------------ соединение

    private sealed class Connection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public List<byte> Pending { get; } = new();

        public Connection(TcpClient client)
        {
            Client = client;
            Stream = client.GetStream();
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch (Exception) { }
            try { Client.Dispose(); } catch (Exception) { }
        }
    }

    private async Task<(Connection? Connection, long Ms, string Detail)> ConnectAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_connectTimeoutMs);
        try
        {
            await client.ConnectAsync(_endPoint.Address, _endPoint.Port, timeout.Token).ConfigureAwait(false);
            Log("--", $"подключено за {watch.ElapsedMilliseconds} мс");
            return (new Connection(client), watch.ElapsedMilliseconds, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            Log("--", $"нет подключения за {_connectTimeoutMs} мс");
            return (null, watch.ElapsedMilliseconds, $"timeout {_connectTimeoutMs} ms");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            Log("--", $"нет подключения: {ex.SocketErrorCode}");
            return (null, watch.ElapsedMilliseconds, ex.SocketErrorCode.ToString());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private enum ReadOutcome { Reply, Unframed, Timeout, Closed }

    /// <summary>Ждёт один полный ответ (правила — <see cref="DahuaTmProtocol.ClassifyReply"/>).
    /// Короткие (&lt; 5 знаков) ответы пропускает, как их DLL. Если к таймауту пришли байты без
    /// маркера — возвращает их как Unframed.</summary>
    private async Task<(ReadOutcome Outcome, string Text, DahuaTmProtocol.ReplyKind Kind)> ReadReplyAsync(
        Connection connection, int timeoutMs, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var buffer = new byte[1024]; // их DLL тоже читает кусками по 1024
        while (true)
        {
            var end = DahuaTmProtocol.FindReplyEnd(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(connection.Pending));
            if (end > 0)
            {
                var bytes = connection.Pending.GetRange(0, end).ToArray();
                connection.Pending.RemoveRange(0, end);
                var text = Encoding.Latin1.GetString(bytes);
                Log("<<", text);
                var kind = DahuaTmProtocol.ClassifyReply(text);
                if (kind == DahuaTmProtocol.ReplyKind.TooShort)
                    continue;
                return (ReadOutcome.Reply, text, kind);
            }

            var left = timeoutMs - (int)deadline.ElapsedMilliseconds;
            if (left <= 0)
            {
                if (connection.Pending.Count > 0)
                {
                    var text = Encoding.Latin1.GetString(connection.Pending.ToArray());
                    connection.Pending.Clear();
                    Log("<<", text + "   [без маркера конца ответа]");
                    return (ReadOutcome.Unframed, text, DahuaTmProtocol.ReplyKind.Incomplete);
                }
                return (ReadOutcome.Timeout, "", DahuaTmProtocol.ReplyKind.Incomplete);
            }

            // Пришла строка с переводом строки (LF), но без маркера их DLL: ждём ещё 400 мс тишины и принимаем её,
            // чтобы при неточной модели ответа не стоять по 2,5 с на каждой строке.
            var endsWithNewLine = connection.Pending.Count > 0 && connection.Pending[^1] == 0x0A;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(endsWithNewLine ? Math.Min(left, 400) : left);
            int read;
            try
            {
                read = await connection.Stream.ReadAsync(buffer.AsMemory(0, buffer.Length), wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (endsWithNewLine)
                {
                    var text = Encoding.Latin1.GetString(connection.Pending.ToArray());
                    connection.Pending.Clear();
                    Log("<<", text + "   [без маркера конца ответа]");
                    return (ReadOutcome.Unframed, text, DahuaTmProtocol.ReplyKind.Incomplete);
                }
                continue; // таймаут — обработается в начале цикла
            }
            catch (IOException)
            {
                return (ReadOutcome.Closed, "", DahuaTmProtocol.ReplyKind.Incomplete);
            }
            if (read <= 0)
                return (ReadOutcome.Closed, "", DahuaTmProtocol.ReplyKind.Incomplete);
            connection.Pending.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    private async Task<bool> WriteAsync(Connection connection, string text, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await connection.Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await connection.Stream.FlushAsync(ct).ConfigureAwait(false);
            Log(">>", text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log("--", "ошибка записи: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ проверка связи

    /// <summary>TCP-подключение + чтение PLU №1 («!0J0001A») — только чтение, весы ничего не меняют.</summary>
    public async Task<DahuaTmCheckResult> TestConnectionAsync(CancellationToken ct)
    {
        Log("--", "проверка связи");
        var (connection, ms, detail) = await ConnectAsync(ct).ConfigureAwait(false);
        if (connection is null)
            return new DahuaTmCheckResult(false, false, DahuaTmProtocol.ReplyKind.Incomplete, "", ms, DahuaTmError.ConnectFailed, detail);

        using (connection)
        {
            if (!await WriteAsync(connection, DahuaTmProtocol.BuildReadPluCommand(1), ct).ConfigureAwait(false))
                return new DahuaTmCheckResult(true, false, DahuaTmProtocol.ReplyKind.Incomplete, "", ms, DahuaTmError.SendFailed, "");

            var (outcome, text, kind) = await ReadReplyAsync(connection, _replyTimeoutMs, ct).ConfigureAwait(false);
            return outcome switch
            {
                ReadOutcome.Reply => new DahuaTmCheckResult(true, true, kind, text, ms, DahuaTmError.None, ""),
                ReadOutcome.Unframed => new DahuaTmCheckResult(true, true, DahuaTmProtocol.ReplyKind.Incomplete, text, ms, DahuaTmError.None, "unframed"),
                ReadOutcome.Closed => new DahuaTmCheckResult(true, false, kind, "", ms, DahuaTmError.ConnectionLost, ""),
                _ => new DahuaTmCheckResult(true, false, kind, "", ms, DahuaTmError.NoReply, ""),
            };
        }
    }

    // ------------------------------------------------------------------ выгрузка PLU

    /// <summary>Отправляет записи PLU. Записи должны пройти <see cref="DahuaTmProtocol.Validate"/>
    /// (иначе — ArgumentException до подключения).</summary>
    public async Task<DahuaTmUploadResult> UploadPlusAsync(IReadOnlyList<DahuaTmPlu> plus, int priceDecimals,
        DahuaTmSendMode mode, IProgress<DahuaTmUploadProgress>? progress, CancellationToken ct)
    {
        var lines = plus.Select(p => DahuaTmProtocol.BuildPluCommand(p, priceDecimals)).ToList();
        var result = new DahuaTmUploadResult { Total = lines.Count };
        if (lines.Count == 0)
            return result;

        Log("--", $"выгрузка PLU: {lines.Count} шт., режим {mode}, знаков цены {priceDecimals}");
        Connection? connection;
        string detail;
        try
        {
            (connection, _, detail) = await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Error = DahuaTmError.Cancelled;
            Log("--", "отменено");
            return result;
        }
        if (connection is null)
        {
            result.Error = DahuaTmError.ConnectFailed;
            result.Detail = detail;
            return result;
        }

        try
        {
            if (mode == DahuaTmSendMode.Batch)
                await SendBatchAsync(connection, lines, plus, result, progress, ct).ConfigureAwait(false);
            else
                connection = await SendLineByLineAsync(connection, lines, plus, result, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Error = DahuaTmError.Cancelled;
            Log("--", "отменено");
        }
        finally
        {
            connection?.Dispose();
        }

        Log("--", $"итог: отправлено {result.Sent}, ответов {result.Acknowledged}, без маркера {result.UnframedReplies}, повторов {result.Retries}, ошибка {result.Error}");
        return result;
    }

    private async Task<Connection?> SendLineByLineAsync(Connection connection, List<string> lines, IReadOnlyList<DahuaTmPlu> plus,
        DahuaTmUploadResult result, IProgress<DahuaTmUploadProgress>? progress, CancellationToken ct)
    {
        var reconnected = false;
        for (var i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var done = false;
            for (var attempt = 0; attempt <= _retries && !done; attempt++)
            {
                if (attempt > 0)
                    result.Retries++;
                if (!await WriteAsync(connection, lines[i], ct).ConfigureAwait(false))
                {
                    // Соединение оборвалось — один раз переподключаемся (строка PLU идемпотентна).
                    if (reconnected)
                        return Fail(DahuaTmError.SendFailed);
                    reconnected = true;
                    connection.Dispose();
                    var (again, _, _) = await ConnectAsync(ct).ConfigureAwait(false);
                    if (again is null)
                        return Fail(DahuaTmError.ConnectionLost);
                    connection = again;
                    continue;
                }
                if (attempt == 0)
                    result.Sent++;

                var (outcome, _, _) = await ReadReplyAsync(connection, _replyTimeoutMs, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case ReadOutcome.Reply:
                        result.Acknowledged++;
                        done = true;
                        break;
                    case ReadOutcome.Unframed:
                        result.UnframedReplies++;
                        done = true;
                        break;
                    case ReadOutcome.Closed:
                        if (reconnected)
                            return Fail(DahuaTmError.ConnectionLost);
                        reconnected = true;
                        connection.Dispose();
                        var (again, _, _) = await ConnectAsync(ct).ConfigureAwait(false);
                        if (again is null)
                            return Fail(DahuaTmError.ConnectionLost);
                        connection = again;
                        break;
                    default:
                        Log("--", $"нет ответа за {_replyTimeoutMs} мс (попытка {attempt + 1})");
                        break;
                }
            }

            if (!done)
                return Fail(DahuaTmError.NoReply);
            progress?.Report(new DahuaTmUploadProgress(i + 1, lines.Count, plus[i].PluNumber));

            Connection Fail(DahuaTmError error)
            {
                result.Error = error;
                result.FailedPlu = plus[i].PluNumber;
                return connection;
            }
        }
        return connection;
    }

    private async Task SendBatchAsync(Connection connection, List<string> lines, IReadOnlyList<DahuaTmPlu> plus,
        DahuaTmUploadResult result, IProgress<DahuaTmUploadProgress>? progress, CancellationToken ct)
    {
        if (!await WriteAsync(connection, string.Concat(lines), ct).ConfigureAwait(false))
        {
            result.Error = DahuaTmError.SendFailed;
            return;
        }
        result.Sent = lines.Count;

        // Ответы собираем, пока идут: каждый полный ответ — одна строка; тишина дольше таймаута — конец.
        while (result.Acknowledged + result.UnframedReplies < lines.Count)
        {
            var (outcome, _, _) = await ReadReplyAsync(connection, _replyTimeoutMs, ct).ConfigureAwait(false);
            if (outcome == ReadOutcome.Reply)
                result.Acknowledged++;
            else if (outcome == ReadOutcome.Unframed)
                result.UnframedReplies++;
            else
                break;
            var done = result.Acknowledged + result.UnframedReplies;
            progress?.Report(new DahuaTmUploadProgress(done, lines.Count, plus[Math.Min(done, plus.Count) - 1].PluNumber));
        }

        if (result.Acknowledged + result.UnframedReplies < lines.Count)
        {
            result.Error = DahuaTmError.NoReply;
            result.Detail = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", result.Acknowledged + result.UnframedReplies, lines.Count);
        }
    }
}
