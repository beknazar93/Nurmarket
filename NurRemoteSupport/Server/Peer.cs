using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace NurRemoteSupport.Server;

/// <summary>2026-10-16: одно WebSocket-соединение (касса или оператор). Отправка — через свою очередь и один цикл
/// (WebSocket не допускает параллельных SendAsync), приём — из обработчика запроса. Очередь больше MaxQueueBytes
/// (клиент не успевает принимать) — соединение обрывается, память сервера не растёт.</summary>
public sealed class Peer
{
    public readonly record struct Msg(byte[] Data, bool Binary);

    private readonly WebSocket _ws;
    private readonly Channel<Msg> _out = Channel.CreateUnbounded<Msg>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _buf = new byte[16 * 1024];
    private readonly long _maxQueue;
    private readonly Task _sendLoop;
    private long _queued;
    private int _closeStatus; // 0 — ещё не задан (тогда 1000)

    public string Ip { get; }

    /// <summary>Когда от клиента последний раз что-то пришло (Environment.TickCount64).</summary>
    public long LastSeen { get; private set; } = Environment.TickCount64;

    public Peer(WebSocket ws, string ip, long maxQueue)
    {
        _ws = ws;
        Ip = ip;
        _maxQueue = maxQueue;
        _sendLoop = Task.Run(SendLoopAsync);
    }

    public bool Send(byte[] data, bool binary = false)
    {
        if (Interlocked.Add(ref _queued, data.Length) > _maxQueue)
        {
            Kill(); // 2026-10-16: клиент не успевает — обрыв, а не бесконечная очередь в памяти
            return false;
        }
        if (_out.Writer.TryWrite(new Msg(data, binary)))
            return true;
        Interlocked.Add(ref _queued, -data.Length);
        return false;
    }

    public bool SendJson(object message) => Send(JsonSerializer.SerializeToUtf8Bytes(message, Relay.Json));

    /// <summary>Отправить всё из очереди и закрыть соединение (без ожидания). 2026-10-16: код закрытия задаёт первый вызов —
    /// иначе Shutdown после «слишком большого сообщения» (1009) подменял его на 1000.</summary>
    public void Close(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        Interlocked.CompareExchange(ref _closeStatus, (int)status, 0);
        _out.Writer.TryComplete();
    }

    private WebSocketCloseStatus CloseStatus => Volatile.Read(ref _closeStatus) is var s and not 0 ? (WebSocketCloseStatus)s : WebSocketCloseStatus.NormalClosure;

    /// <summary>Оборвать сразу (мёртвый или слишком медленный клиент).</summary>
    public void Kill()
    {
        _out.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _ws.Abort();
        }
        catch
        {
            // уже закрыто
        }
    }

    /// <summary>Следующее сообщение целиком; null — соединение закрыто, оборвано, истёк timeout или сообщение больше
    /// лимита (тогда закрытие с кодом 1009).</summary>
    public async Task<Msg?> ReceiveAsync(int maxText, int maxBinary, TimeSpan? timeout = null)
    {
        using var limit = timeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token) : null;
        limit?.CancelAfter(timeout.GetValueOrDefault());
        var ct = limit?.Token ?? _cts.Token;
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _ws.ReceiveAsync(_buf, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    return null;
                var max = r.MessageType == WebSocketMessageType.Text ? maxText : maxBinary;
                if (ms.Length + r.Count > max)
                {
                    Close(WebSocketCloseStatus.MessageTooBig);
                    return null;
                }
                ms.Write(_buf, 0, r.Count);
                if (r.EndOfMessage)
                {
                    LastSeen = Environment.TickCount64;
                    return new Msg(ms.ToArray(), r.MessageType == WebSocketMessageType.Binary);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            return null;
        }
    }

    /// <summary>Конец обработчика: дождаться отправки очереди и закрытия (не дольше 6 с).</summary>
    public async Task FinishAsync()
    {
        Close();
        try
        {
            await _sendLoop.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill();
            return;
        }
        // 2026-10-16: закрыли мы (конец сеанса, слишком большое сообщение) — дочитать до ответного «закрыть» клиента
        // (не дольше 3 с). Иначе непрочитанные байты заставят ОС оборвать соединение (RST) и клиент не увидит код закрытия.
        if (_ws.State != WebSocketState.CloseSent)
            return;
        using var drain = new CancellationTokenSource(3000);
        try
        {
            while ((await _ws.ReceiveAsync(_buf, drain.Token).ConfigureAwait(false)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // клиент не ответил — соединение закроет сервер
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _ws.SendAsync(m.Data, m.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref _queued, -m.Data.Length);
            }
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                t.CancelAfter(5000);
                await _ws.CloseOutputAsync(CloseStatus, null, t.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // соединение уже оборвано
        }
        finally
        {
            // 2026-10-16: после своего «закрыть» ждём ответ клиента не дольше 5 с — потом приём прерывается.
            try
            {
                _cts.CancelAfter(5000);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
