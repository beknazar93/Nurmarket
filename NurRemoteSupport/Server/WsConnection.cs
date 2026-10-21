using System.Buffers;
using System.Net.WebSockets;

namespace NurRemoteSupport.Server;

/// <summary>Принятое сообщение. Buffer взят из ArrayPool — владелец обязан вернуть его (или передать дальше).</summary>
public readonly record struct Incoming(byte[] Buffer, int Length, WebSocketMessageType Type)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
}

public sealed class MessageTooBigException(int limit) : Exception($"Сообщение больше {limit} байт");
public sealed class SendStallException() : Exception("Очередь отправки не освобождается");

/// <summary>
/// Обёртка над WebSocket (2026-09-29):
///  - одна задача отправки и ограниченная по байтам очередь (сервер никогда не копит без предела;
///    если очередь полна, читающий от другой стороны ждёт — давление назад по TCP);
///  - пинг JSON каждые 15 с и разрыв после 45 с тишины;
///  - закрытие: сначала досылаем очередь (не дольше 5 с), затем close-кадр.
/// </summary>
public sealed class WsConnection
{
    private readonly record struct Outgoing(byte[] Buffer, int Length, WebSocketMessageType Type, bool Pooled);

    private static readonly byte[] PingBytes = "{\"type\":\"ping\"}"u8.ToArray();

    private readonly WebSocket _ws;
    private readonly RelayOptions _o;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Queue<Outgoing> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _queuedBytes;
    private bool _completed;
    private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
    private string _closeText = "end";
    private TaskCompletionSource? _dataWaiter;
    private TaskCompletionSource? _spaceWaiter;
    private long _lastReceived = Environment.TickCount64;
    private long _bytesSent;

    public WsConnection(WebSocket ws, string role, string ip, RelayOptions o, ILogger log)
    {
        _ws = ws;
        Role = role;
        Ip = ip;
        _o = o;
        _log = log;
        Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    public string Id { get; }
    public string Role { get; }
    public string Ip { get; }
    public bool PongSeen { get; set; }
    public bool TimedOut { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public CancellationToken Token => _cts.Token;
    /// <summary>Можно ещё читать: клиент не прислал close и соединение не оборвано.</summary>
    public bool CanReceive => _ws.State is WebSocketState.Open or WebSocketState.CloseSent;
    public Task Finished => _finished.Task;

    public void Start()
    {
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(PingLoopAsync);
    }

    /// <summary>Приём одного сообщения целиком. null — сторона закрыла соединение.</summary>
    public async Task<Incoming?> ReceiveAsync(CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var count = 0;
        try
        {
            var limit = _o.MaxBinaryMessageBytes;
            while (true)
            {
                if (count == buf.Length)
                {
                    if (count >= limit) throw new MessageTooBigException(limit);
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buf.Length * 2, limit + 1));
                    Buffer.BlockCopy(buf, 0, bigger, 0, count);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }
                var r = await _ws.ReceiveAsync(buf.AsMemory(count), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    return null;
                }
                count += r.Count;
                limit = r.MessageType == WebSocketMessageType.Text ? _o.MaxTextMessageBytes : _o.MaxBinaryMessageBytes;
                if (count > limit) throw new MessageTooBigException(limit);
                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                if (r.EndOfMessage) return new Incoming(buf, count, r.MessageType);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
    }

    /// <summary>
    /// Поставить сообщение в очередь с учётом предела байт. Если места нет — ждём (давление назад),
    /// но не дольше SendStallSeconds, потом SendStallException. false — соединение уже закрывается.
    /// Буфер pooled переходит во владение очереди в любом случае.
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(byte[] buffer, int length, WebSocketMessageType type, bool pooled)
    {
        while (true)
        {
            TaskCompletionSource wait;
            lock (_gate)
            {
                if (_completed)
                {
                    if (pooled) ArrayPool<byte>.Shared.Return(buffer);
                    return false;
                }
                if (_queuedBytes == 0 || _queuedBytes + length <= _o.SendQueueBytes)
                {
                    _queue.Enqueue(new Outgoing(buffer, length, type, pooled));
                    _queuedBytes += length;
                    SignalDataLocked();
                    return true;
                }
                wait = _spaceWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                await wait.Task.WaitAsync(TimeSpan.FromSeconds(_o.SendStallSeconds), _cts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (pooled) ArrayPool<byte>.Shared.Return(buffer);
                throw new SendStallException();
            }
            catch (OperationCanceledException)
            {
                if (pooled) ArrayPool<byte>.Shared.Return(buffer);
                return false;
            }
        }
    }

    /// <summary>Служебное сообщение сервера (маленькое) — без ожидания места.</summary>
    public bool SendControl(byte[] json)
    {
        lock (_gate)
        {
            if (_completed) return false;
            _queue.Enqueue(new Outgoing(json, json.Length, WebSocketMessageType.Text, false));
            _queuedBytes += json.Length;
            SignalDataLocked();
            return true;
        }
    }

    /// <summary>Задать код закрытия заранее (например, 1009), не закрывая: сообщения ещё можно дослать.</summary>
    public void SetCloseStatus(WebSocketCloseStatus status, string text)
    {
        lock (_gate)
        {
            if (_completed) return;
            _closeStatus = status;
            _closeText = text;
        }
    }

    /// <summary>Досылаем очередь и закрываем (не дольше 5 с, потом обрыв).</summary>
    public void Close(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string text = "end")
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            if (status != WebSocketCloseStatus.NormalClosure || _closeStatus == WebSocketCloseStatus.NormalClosure)
            {
                _closeStatus = status;
                _closeText = text;
            }
            SignalDataLocked();
        }
        try { _cts.CancelAfter(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { }
    }

    /// <summary>Немедленный обрыв (таймаут тишины).</summary>
    public void Abort()
    {
        lock (_gate) _completed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _ws.Abort(); } catch { }
    }

    private void SignalDataLocked()
    {
        var d = _dataWaiter;
        _dataWaiter = null;
        d?.TrySetResult();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                Outgoing item;
                TaskCompletionSource? wait = null;
                lock (_gate)
                {
                    if (_queue.Count > 0) item = _queue.Dequeue();
                    else if (_completed) break;
                    else
                    {
                        wait = _dataWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        item = default;
                    }
                }
                if (wait is not null)
                {
                    await wait.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    await _ws.SendAsync(item.Buffer.AsMemory(0, item.Length), item.Type, true, _cts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesSent, item.Length);
                }
                finally
                {
                    if (item.Pooled) ArrayPool<byte>.Shared.Return(item.Buffer);
                    lock (_gate)
                    {
                        _queuedBytes -= item.Length;
                        var s = _spaceWaiter;
                        _spaceWaiter = null;
                        s?.TrySetResult();
                    }
                }
            }
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(_closeStatus, _closeText, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            try { _ws.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка отправки ({Role} {Conn})", Role, Id);
            try { _ws.Abort(); } catch { }
        }
        finally
        {
            lock (_gate)
            {
                _completed = true;
                while (_queue.Count > 0)
                {
                    var left = _queue.Dequeue();
                    if (left.Pooled) ArrayPool<byte>.Shared.Return(left.Buffer);
                }
                _queuedBytes = 0;
                _spaceWaiter?.TrySetResult();
                _spaceWaiter = null;
            }
            _finished.TrySetResult();
        }
    }

    private async Task PingLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_o.PingIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                lock (_gate) if (_completed) return;
                var silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastReceived);
                // Совместимость: 45-секундное правило — только для тех, кто хоть раз ответил pong (см. PROTOCOL.md).
                if (PongSeen && silentMs > _o.IdleTimeoutSeconds * 1000L)
                {
                    _log.LogInformation("Нет сообщений {Seconds} с — обрываем ({Role} {Conn} {Ip})", silentMs / 1000, Role, Id, Ip);
                    TimedOut = true;
                    Abort();
                    return;
                }
                SendControl(PingBytes);
            }
        }
        catch (OperationCanceledException) { }
    }
}
