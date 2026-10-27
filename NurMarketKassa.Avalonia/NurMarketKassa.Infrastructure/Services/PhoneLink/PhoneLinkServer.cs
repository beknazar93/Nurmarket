using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.PhoneLink;

/// <summary>2026-10-27, пакет К-П2 (ТЗ «Мобилка и касса», путь B): мини-сервер связи кассы с телефонами в сети магазина.
/// Одно соединение = один запрос: телефон шлёт ОДНУ строку JSON (UTF-8, до перевода строки, ≤ 64 КБ), касса отвечает ОДНОЙ
/// строкой JSON и закрывает соединение. Порт 47830, занят — 47831…47839. Слушает все интерфейсы, пока запущен
/// (<see cref="Start"/>); остановлен — порт закрыт. Отдельный от обмена между кассами (LanSyncService).
///
/// ТОЧКА РАСШИРЕНИЯ: операции подключаются через <see cref="Register"/> (op → обработчик). Сейчас есть hello (внутри) и scan
/// (PhoneScanHandler); печать чека (print), корзина (cart), «выгружено» (uploaded) добавятся тем же способом — подпись,
/// время, повтор, права и ограничения частоты проверяются здесь, обработчику приходит уже проверенный <see cref="PhoneLinkCall"/>.</summary>
public sealed class PhoneLinkServer : IDisposable
{
    public delegate Task<PhoneLinkReply> OpHandler(PhoneLinkCall call, CancellationToken ct);

    private const int RatePerSecondLimit = 20;
    private const int MaxParallel = 16;
    // 2026-10-27, ревью Ф-6: порт слушает все интерфейсы — один адрес не должен занять все 16 мест (16 × 5 с чтения = отказ в обслуживании).
    private const int MaxParallelPerAddress = 4;
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(15);

    private readonly PhoneLinkPairing _pairing;
    private readonly Func<PhoneLinkHostInfo> _hostInfo;
    private readonly Func<DateTime> _utcNow;
    private readonly int _firstPort;
    private readonly int _lastPort;
    private readonly PhoneLinkNonceCache _nonces = new();
    private readonly Dictionary<string, OpHandler> _handlers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _parallel = new(MaxParallel, MaxParallel);
    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);

    // Ограничение частоты: времена последних запросов с каждого адреса.
    private readonly Dictionary<string, Queue<long>> _rate = new();
    private long _lastRatePrune;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PhoneLinkServer(PhoneLinkPairing pairing, Func<PhoneLinkHostInfo> hostInfo, Func<DateTime>? utcNow = null,
        int firstPort = PhoneLinkProtocol.FirstPort, int lastPort = PhoneLinkProtocol.LastPort)
    {
        _pairing = pairing;
        _hostInfo = hostInfo;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _firstPort = firstPort;
        _lastPort = lastPort;
    }

    /// <summary>Порт, на котором слушаем (0 — остановлен).</summary>
    public int Port { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _listener is not null;
        }
    }

    /// <summary>Подключить обработчик операции (op). Повторная регистрация заменяет прежний обработчик. hello занят самим сервером.</summary>
    public void Register(string op, OpHandler handler)
    {
        if (op == "hello")
            throw new ArgumentException("hello обслуживает сам сервер", nameof(op));
        lock (_gate)
            _handlers[op] = handler;
    }

    // ------------------------------------------------------------------ запуск и остановка

    /// <summary>Открыть порт. false — свободного порта нет (error — причина).</summary>
    public bool Start(out string? error)
    {
        lock (_gate)
        {
            error = null;
            if (_listener is not null)
                return true;

            SocketException? last = null;
            for (var port = _firstPort; port <= _lastPort; port++)
            {
                TcpListener? candidate = null;
                try
                {
                    candidate = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                    candidate.Start(32);
                    _listener = candidate;
                    Port = port;
                    break;
                }
                catch (SocketException ex)
                {
                    last = ex;
                    try { candidate?.Stop(); } catch { /* уже закрыт */ }
                    // Порт занят или закрыт системой (исключённый диапазон) — пробуем следующий.
                }
            }

            if (_listener is null)
            {
                error = $"порты {_firstPort}–{_lastPort} заняты ({last?.SocketErrorCode})";
                PosLogger.Log($"PHONELINK: приём с телефонов не запущен — {error}.", "WARNING");
                return false;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var listener = _listener;
            _loop = Task.Run(() => AcceptLoopAsync(listener, token));
            PosLogger.Log($"PHONELINK: приём с телефонов включён, порт {Port}.", "INFO");
            return true;
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            Port = 0;
        }
        if (listener is null)
            return;
        try { cts?.Cancel(); } catch { /* уже отменён */ }
        try { listener.Stop(); } catch { /* уже закрыт */ }
        PosLogger.Log("PHONELINK: приём с телефонов выключен, порт закрыт.", "INFO");
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ приём соединений

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                    return;
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private bool RateExceeded(string address)
    {
        var nowMs = Environment.TickCount64;
        lock (_rate)
        {
            if (nowMs - _lastRatePrune > 10_000)
            {
                _lastRatePrune = nowMs;
                foreach (var key in _rate.Where(kv => kv.Value.Count == 0 || nowMs - kv.Value.Peek() > 1000).Select(kv => kv.Key).ToList())
                    _rate.Remove(key);
            }
            if (!_rate.TryGetValue(address, out var queue))
                _rate[address] = queue = new Queue<long>();
            while (queue.Count > 0 && nowMs - queue.Peek() >= 1000)
                queue.Dequeue();
            if (queue.Count >= RatePerSecondLimit)
                return true;
            queue.Enqueue(nowMs);
            return false;
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        var address = "?";
        var parallelTaken = false;
        var addressTaken = false;
        try
        {
            using (client)
            {
                address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
                client.NoDelay = true;
                var stream = client.GetStream();

                if (RateExceeded(address))
                {
                    await WriteReplyAsync(stream, PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Слишком много запросов, повторите через секунду."), serverToken).ConfigureAwait(false);
                    await DrainAsync(stream, TimeSpan.FromMilliseconds(300), 128 * 1024).ConfigureAwait(false); // иначе закрытие сбросит ответ (RST)
                    return;
                }
                if (!await _parallel.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
                {
                    await WriteReplyAsync(stream, PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Касса занята, повторите."), serverToken).ConfigureAwait(false);
                    await DrainAsync(stream, TimeSpan.FromMilliseconds(300), 128 * 1024).ConfigureAwait(false);
                    return;
                }
                parallelTaken = true;
                // 2026-10-27, ревью Ф-6: больше 4 одновременных соединений с одного адреса (кроме самой кассы) — «занято».
                if (!TryEnterAddress(address))
                {
                    await WriteReplyAsync(stream, PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Слишком много соединений, повторите."), serverToken).ConfigureAwait(false);
                    await DrainAsync(stream, TimeSpan.FromMilliseconds(300), 128 * 1024).ConfigureAwait(false);
                    return;
                }
                addressTaken = true;

                // Чтение строки: не дольше 5 секунд и не больше 64 КБ.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                readCts.CancelAfter(PhoneLinkProtocol.ReadTimeout);
                var (line, tooBig, timedOut) = await ReadLineAsync(stream, readCts.Token).ConfigureAwait(false);
                if (timedOut)
                {
                    PosLogger.Log($"PHONELINK: {address}: запрос не получен за {PhoneLinkProtocol.ReadTimeout.TotalSeconds:0} с — соединение закрыто.", "INFO");
                    return;
                }

                PhoneLinkReply reply;
                if (tooBig)
                    reply = PhoneLinkReply.Fail(PhoneLinkCodes.TooBig, "Запрос больше 64 КБ.");
                else
                    reply = await ProcessAsync(line!, address, serverToken).ConfigureAwait(false);

                await WriteReplyAsync(stream, reply, serverToken).ConfigureAwait(false);

                if (tooBig)
                    await DrainAsync(stream, TimeSpan.FromSeconds(2), 4 * 1024 * 1024).ConfigureAwait(false); // дочитываем остаток, иначе закрытие сбросит ответ (RST)
            }
        }
        catch (OperationCanceledException)
        {
            // касса остановлена
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // телефон оборвал связь
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: {address}: ошибка обработки запроса: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
        finally
        {
            if (addressTaken)
                LeaveAddress(address);
            if (parallelTaken)
                _parallel.Release();
        }
    }

    private bool TryEnterAddress(string address)
    {
        if (address is "127.0.0.1" or "?")
            return true;
        lock (_perAddress)
        {
            _perAddress.TryGetValue(address, out var count);
            if (count >= MaxParallelPerAddress)
                return false;
            _perAddress[address] = count + 1;
            return true;
        }
    }

    private void LeaveAddress(string address)
    {
        if (address is "127.0.0.1" or "?")
            return;
        lock (_perAddress)
        {
            if (!_perAddress.TryGetValue(address, out var count))
                return;
            if (count <= 1)
                _perAddress.Remove(address);
            else
                _perAddress[address] = count - 1;
        }
    }

    private static async Task<(string? Line, bool TooBig, bool TimedOut)> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[PhoneLinkProtocol.MaxLineBytes + 1];
        var used = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), ct).ConfigureAwait(false);
                if (read == 0)
                    break; // телефон закончил отправку без перевода строки — берём что есть
                var start = used;
                used += read;
                var nl = Array.IndexOf(buffer, (byte)'\n', start, read);
                if (nl >= 0)
                {
                    if (nl > PhoneLinkProtocol.MaxLineBytes)
                        return (null, true, false);
                    return (DecodeLine(buffer, nl), false, false);
                }
                if (used > PhoneLinkProtocol.MaxLineBytes)
                    return (null, true, false);
            }
        }
        catch (OperationCanceledException)
        {
            return (null, false, true);
        }
        return (DecodeLine(buffer, used), false, false);
    }

    private static string DecodeLine(byte[] buffer, int length)
    {
        if (length > 0 && buffer[length - 1] == '\r')
            length--;
        return new UTF8Encoding(false, false).GetString(buffer, 0, length);
    }

    private static async Task DrainAsync(NetworkStream stream, TimeSpan limit, int maxBytes)
    {
        try
        {
            using var cts = new CancellationTokenSource(limit);
            var sink = new byte[16 * 1024];
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(sink, cts.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
        }
        catch
        {
            // не получилось дочитать — закрываем как есть
        }
    }

    private static async Task WriteReplyAsync(NetworkStream stream, PhoneLinkReply reply, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(PhoneLinkProtocol.BuildReply(reply) + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        try { stream.Socket.Shutdown(SocketShutdown.Send); } catch { /* уже закрыт */ }
    }

    // ------------------------------------------------------------------ проверки и разбор операции

    private static string Short(string phone) => phone.Length <= 6 ? phone : phone[..6] + "…";

    /// <summary>2026-10-27, ревью Ф-4: ключ, которым проверена подпись запроса, — им подписывается и ответ (необязательное поле sig).
    /// Пока запрос не прошёл проверку подписи, ключа нет и ответ остаётся без подписи (parse/unknown_phone/revoked/bad_sig).</summary>
    private sealed class SignContext
    {
        public byte[]? Key;
        public PhoneLinkRequest? Request;
    }

    private async Task<PhoneLinkReply> ProcessAsync(string line, string address, CancellationToken serverToken)
    {
        var ctx = new SignContext();
        var reply = await ProcessCoreAsync(line, address, serverToken, ctx).ConfigureAwait(false);
        if (ctx.Key is not null && ctx.Request is not null)
            reply = reply.WithSig(PhoneLinkProtocol.SignReply(ctx.Key, ctx.Request.Op, ctx.Request.Phone, ctx.Request.Nonce, reply));
        return reply;
    }

    private async Task<PhoneLinkReply> ProcessCoreAsync(string line, string address, CancellationToken serverToken, SignContext ctx)
    {
        if (!PhoneLinkProtocol.TryParseRequest(line, out var request, out var parseError) || request is null)
            return PhoneLinkReply.Fail(parseError ?? PhoneLinkCodes.BadRequest, "Неверный формат запроса.");

        // 1. Чей это телефон и каким ключом проверять подпись.
        var standing = _pairing.GetStanding(request.Phone, out var key);
        var pairingKey = request.Op == "hello" ? _pairing.PendingKeyBytes() : null;
        var viaPairingKey = false;

        if (standing == PhoneLinkPairing.Standing.Paired && key is not null && PhoneLinkProtocol.VerifySignature(key, request))
        {
            // обычный случай
            ctx.Key = key;
            ctx.Request = request;
        }
        else if (pairingKey is not null && PhoneLinkProtocol.VerifySignature(pairingKey, request))
        {
            // первое сопряжение (или повторное: телефон заново отсканировал QR)
            viaPairingKey = true;
            ctx.Key = pairingKey;
            ctx.Request = request;
        }
        else if (standing == PhoneLinkPairing.Standing.Revoked)
        {
            Log(address, request, PhoneLinkCodes.Revoked);
            return PhoneLinkReply.Fail(PhoneLinkCodes.Revoked, "Телефон отключён на кассе. Подключите его заново: «Подключить телефон».");
        }
        else if (standing == PhoneLinkPairing.Standing.Unknown && pairingKey is null)
        {
            Log(address, request, PhoneLinkCodes.UnknownPhone);
            return PhoneLinkReply.Fail(PhoneLinkCodes.UnknownPhone, "Касса не знает этот телефон. Подключите его: «Подключить телефон».");
        }
        else
        {
            Log(address, request, PhoneLinkCodes.BadSig);
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadSig, "Неверная подпись.");
        }

        // 2. Время телефона (±10 минут).
        var now = _utcNow();
        var phoneTime = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(request.Ts, 0, 253402300799L)).UtcDateTime;
        if ((now - phoneTime).Duration() > PhoneLinkProtocol.MaxClockSkew)
        {
            Log(address, request, PhoneLinkCodes.BadTime);
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadTime, "Время на телефоне и на кассе различается больше чем на 10 минут. Проверьте время на телефоне.");
        }

        // 3. Повтор nonce.
        if (!_nonces.TryRegister(request.Phone, request.Nonce, now))
        {
            Log(address, request, PhoneLinkCodes.Replay);
            return PhoneLinkReply.Fail(PhoneLinkCodes.Replay, "Этот запрос уже был.");
        }

        // 4. Тело — JSON-объект.
        var body = PhoneLinkProtocol.ParseBody(request.Body);
        if (body is null)
        {
            Log(address, request, PhoneLinkCodes.BadRequest);
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Тело запроса — не JSON-объект.");
        }

        // 5. Сопряжение: ключ ожидающего сопряжения привязывается к телефону (одноразово).
        if (viaPairingKey)
        {
            var name = BodyString(body, "name") ?? "";
            if (!_pairing.CompletePairing(request.Phone, name))
                return PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Не удалось запомнить телефон, нажмите «Подключить телефон» ещё раз.");
            PosLogger.Log($"PHONELINK: подключён телефон {Short(request.Phone)}.", "INFO");
        }
        else
        {
            _pairing.Touch(request.Phone, request.Op == "hello" ? BodyString(body, "name") : null);
        }

        // 6. Операция.
        if (request.Op == "hello")
        {
            var info = _hostInfo();
            return PhoneLinkReply.Success(PhoneLinkCodes.Ok, null, new JsonObject
            {
                ["kassa"] = info.Kassa,
                ["company"] = info.Company8,
                ["shift_open"] = info.ShiftOpen,
            });
        }

        OpHandler? handler;
        lock (_gate)
            _handlers.TryGetValue(request.Op, out handler);
        if (handler is null)
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Операция не поддерживается этой кассой.");

        var call = new PhoneLinkCall
        {
            Op = request.Op,
            Phone = request.Phone,
            PhoneName = _pairing.NameOf(request.Phone),
            BodyText = request.Body,
            Body = body,
            RemoteAddress = address,
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            cts.CancelAfter(OpTimeout);
            return await handler(call, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Касса не успела ответить, повторите.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: {address}: операция {request.Op} не выполнена: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Касса не смогла выполнить операцию.");
        }
    }

    private static string? BodyString(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static void Log(string address, PhoneLinkRequest request, string code) =>
        PosLogger.Log($"PHONELINK: {address}: телефон {Short(request.Phone)}, операция {request.Op} отклонена — {code}.", "INFO");
}
