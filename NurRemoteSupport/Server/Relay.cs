using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurRemoteSupport.Server;

/// <summary>2026-10-16: состояние кода кассы. Waiting — ждём оператора; Asking — кассира спросили; Cooldown — кассир
/// разрешил, но оператор ушёл раньше (касса сама снимет согласие через 30 с); InSession — сеанс; Closed — код закрыт.</summary>
public enum HostState { Waiting, Asking, Cooldown, InSession, Closed }

/// <summary>2026-10-16: одна касса с выданным кодом. Всё изменяемое — только под Gate.</summary>
public sealed class HostSession(string code, string pin, Peer host)
{
    public readonly object Gate = new();
    public string Code { get; } = code;
    public string Pin { get; } = pin;
    public Peer Host { get; } = host;
    public string Company = "", Machine = "", Version = "";
    public int ScreenW, ScreenH;
    public long ExpiresAt, Deadline, StartedAt, LastActivity, BytesToOperator;
    public HostState State = HostState.Waiting;
    public Peer? Operator;
    public string OperatorName = "";
    public int PinFailures;
    public bool CashierViewOnly, OperatorViewOnly;
    public bool ViewOnly => CashierViewOnly || OperatorViewOnly;
}

/// <summary>2026-10-16: ретранслятор удалённой помощи (протокол v1, см. PROTOCOL.md). Касса (/ws/host) получает код и PIN,
/// оператор (/ws/operator) входит по ним, касса спрашивает кассира, после согласия сервер пересылает экран кассы оператору
/// и ввод оператора кассе. Сервер не смотрит в кадры и не пишет в журнал PIN, кадры, текст и клавиши.</summary>
public sealed class Relay
{
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public const int CodeLength = 9;
    private const int CooldownMs = 35_000; // касса снимает согласие через 30 с без «started»
    private static readonly byte[] PingMessage = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");

    private readonly RelayOptions _o;
    private readonly ILogger<Relay> _log;
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _connections = new();
    private readonly IpLimiter _joins, _fails, _hosts;
    private readonly byte[]? _keyHash;
    private long _nextPing, _nextCleanup;

    public Relay(RelayOptions o, ILogger<Relay> log)
    {
        _o = o;
        _log = log;
        var window = TimeSpan.FromSeconds(o.RateWindowSeconds);
        _joins = new IpLimiter(o.JoinLimit, window);
        _fails = new IpLimiter(o.JoinFailLimit, window);
        _hosts = new IpLimiter(o.HostLimit, window);
        _keyHash = o.OperatorKey is { } key ? SHA256.HashData(Encoding.UTF8.GetBytes(key)) : null;
    }

    public bool KeyRequired => _keyHash is not null;
    public int PinLength => _o.PinLength;
    public int HostCount => _sessions.Count;
    public int SessionCount => _sessions.Values.Count(s => s.State == HostState.InSession);
    private static long Now => Environment.TickCount64;

    // ---------- приём соединений ----------

    public Task HandleHostAsync(HttpContext ctx) => AcceptAsync(ctx, false, RunHostAsync);

    public Task HandleOperatorAsync(HttpContext ctx) => AcceptAsync(ctx, true, RunOperatorAsync);

    private async Task AcceptAsync(HttpContext ctx, bool operatorSide, Func<Peer, Task> run)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // 2026-10-16: страницу оператора с чужого сайта к нашему WebSocket не пускаем.
        if (operatorSide && !SameOrigin(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var ip = IpOf(ctx);
        if (_connections.AddOrUpdate(ip, 1, (_, n) => n + 1) > _o.MaxConnectionsPerIp)
        {
            LeaveIp(ip);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var peer = new Peer(ws, ip, _o.MaxQueueBytes);
            try
            {
                await run(peer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Ошибка соединения {Ip}: {Error}", ip, ex.Message);
            }
            finally
            {
                await peer.FinishAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            LeaveIp(ip);
        }
    }

    private void LeaveIp(string ip)
    {
        if (_connections.AddOrUpdate(ip, 0, (_, n) => n - 1) <= 0)
            _connections.TryRemove(new KeyValuePair<string, int>(ip, 0));
    }

    // ---------- касса ----------

    private async Task RunHostAsync(Peer peer)
    {
        HostSession s;
        using (var hello = Parse(await peer.ReceiveAsync(_o.MaxTextBytes, 0, TimeSpan.FromSeconds(10)).ConfigureAwait(false)))
        {
            if (hello?.RootElement is not { } m || Str(m, "type") != "hello" || Str(m, "role") != "host")
            {
                Fail(peer, "bad-hello");
                return;
            }
            if (!Int(m, "protocol", out var protocol) || protocol != 1)
            {
                Fail(peer, "protocol");
                return;
            }
            if (!_hosts.TryHit(peer.Ip))
            {
                _log.LogWarning("Касса {Ip}: слишком много кодов подряд — отказ", peer.Ip);
                Fail(peer, "rate-limit");
                return;
            }
            if (_sessions.Count >= _o.MaxHosts)
            {
                _log.LogWarning("Касса {Ip}: достигнут предел MAX_HOSTS={Max}", peer.Ip, _o.MaxHosts);
                Fail(peer, "server-busy");
                return;
            }
            do
                s = new HostSession(Digits(CodeLength, true), Digits(_o.PinLength, false), peer);
            while (!_sessions.TryAdd(s.Code, s));
            s.Company = Clean(Str(m, "company"), 80, "");
            s.Machine = Clean(Str(m, "machine"), 64, "");
            s.Version = Clean(Str(m, "version"), 24, "");
            if (m.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Object
                && Int(sc, "w", out var w) && Int(sc, "h", out var h) && w is > 0 and <= 16384 && h is > 0 and <= 16384)
                (s.ScreenW, s.ScreenH) = ((int)w, (int)h);
            s.ExpiresAt = Now + _o.CodeTtlSeconds * 1000L;
        }
        peer.SendJson(new { type = "session", code = s.Code, pin = s.Pin, expiresIn = _o.CodeTtlSeconds });
        _log.LogInformation("Касса подключилась: код {Code}, «{Company}» / {Machine}, v{Version}, IP {Ip}",
            s.Code, s.Company, s.Machine, s.Version, peer.Ip);
        try
        {
            while (await peer.ReceiveAsync(_o.MaxTextBytes, _o.MaxFrameBytes).ConfigureAwait(false) is { } msg)
            {
                if (msg.Binary)
                    OnHostBinary(s, msg.Data);
                else
                    OnHostText(s, msg.Data);
            }
        }
        finally
        {
            lock (s.Gate)
                Shutdown(s, null, "disconnect", "касса отключилась");
        }
    }

    private void OnHostBinary(HostSession s, byte[] data)
    {
        // Кадр: [1|2][u32 номер][u16 x,y,w,h][JPEG] — 13 байт заголовка; содержимое сервер не разбирает.
        if (data.Length < 13 || data[0] is not (1 or 2))
            return;
        lock (s.Gate)
        {
            if (s.State != HostState.InSession || s.Operator is not { } op)
                return;
            s.BytesToOperator += data.Length;
            op.Send(data, binary: true);
        }
    }

    private void OnHostText(HostSession s, byte[] data)
    {
        using var doc = Parse(data);
        if (doc is null)
            return;
        var m = doc.RootElement;
        switch (Str(m, "type"))
        {
            case "consent":
                OnConsent(s, Bool(m, "accept"), Bool(m, "view_only"));
                break;
            case "end":
                lock (s.Gate)
                    Shutdown(s, null, "host", "кассир завершил");
                break;
            case "screen" when Int(m, "w", out var w) && Int(m, "h", out var h) && w is > 0 and <= 16384 && h is > 0 and <= 16384:
                Int(m, "monitor", out var monitor);
                Int(m, "monitors", out var monitors);
                lock (s.Gate)
                {
                    (s.ScreenW, s.ScreenH) = ((int)w, (int)h);
                    if (s.State == HostState.InSession)
                        s.Operator?.SendJson(new { type = "screen", w, h, monitor, monitors = Math.Max(1, monitors) });
                }
                break;
            case "cursor" when Int(m, "x", out var x) && Int(m, "y", out var y):
                lock (s.Gate)
                {
                    if (s.State == HostState.InSession)
                        s.Operator?.SendJson(new { type = "cursor", x, y });
                }
                break;
            // «pong» и неизвестное — только отметка, что касса жива (Peer.LastSeen).
        }
    }

    private void OnConsent(HostSession s, bool accept, bool viewOnly)
    {
        lock (s.Gate)
        {
            switch (s.State)
            {
                case HostState.Asking when s.Operator is { } op:
                    if (!accept)
                    {
                        // 2026-10-16: отказ — оператору конец, код кассы остаётся действительным (касса снова ждёт оператора).
                        op.SendJson(new { type = "end", reason = "declined" });
                        op.Close();
                        _log.LogInformation("Код {Code}: кассир отказал оператору «{Operator}»", s.Code, s.OperatorName);
                        s.Operator = null;
                        s.OperatorName = "";
                        s.State = HostState.Waiting;
                        return;
                    }
                    s.State = HostState.InSession;
                    s.CashierViewOnly = viewOnly;
                    s.StartedAt = s.LastActivity = Now;
                    // «started» кассе — с тем же именем, что было в join-request: касса сверяет его с одобренным.
                    s.Host.SendJson(new { type = "started", @operator = s.OperatorName });
                    op.SendJson(new
                    {
                        type = "started", @operator = s.OperatorName, viewOnly, screen = new { w = s.ScreenW, h = s.ScreenH },
                        company = s.Company, machine = s.Machine, version = s.Version,
                    });
                    _log.LogInformation("Код {Code}: сеанс начат, оператор «{Operator}» ({Ip}){Mode}", s.Code, s.OperatorName, op.Ip,
                        viewOnly ? ", только просмотр" : "");
                    return;
                case HostState.Asking:
                    // Оператор ушёл, пока кассир думал. Согласие касса снимет сама через 30 с (consent accept:false).
                    s.State = accept ? HostState.Cooldown : HostState.Waiting;
                    s.Deadline = Now + CooldownMs;
                    return;
                case HostState.Cooldown when !accept:
                    s.State = HostState.Waiting;
                    return;
                default:
                    return; // устаревший ответ
            }
        }
    }

    // ---------- оператор ----------

    private async Task RunOperatorAsync(Peer peer)
    {
        var ip = peer.Ip;
        HostSession s;
        using (var join = Parse(await peer.ReceiveAsync(_o.MaxTextBytes, 0, TimeSpan.FromSeconds(30)).ConfigureAwait(false)))
        {
            if (join?.RootElement is not { } m || Str(m, "type") != "join")
            {
                Fail(peer, "bad-request");
                return;
            }
            if (_fails.IsBlocked(ip) || !_joins.TryHit(ip))
            {
                var wait = Math.Max(1, Math.Max(_fails.RetryAfter(ip), _joins.RetryAfter(ip)));
                _log.LogWarning("Оператор {Ip}: слишком много попыток входа — отказ на {Seconds} с", ip, wait);
                Fail(peer, "rate-limit", retryAfter: wait);
                return;
            }
            if (_keyHash is not null && !KeyOk(Str(m, "key")))
            {
                _fails.Hit(ip);
                _log.LogWarning("Оператор {Ip}: неверный ключ оператора", ip);
                Fail(peer, "bad-key");
                return;
            }
            var code = OnlyDigits(StrOrNum(m, "code"));
            var pin = OnlyDigits(StrOrNum(m, "pin"));
            var name = Clean(Str(m, "name"), 40, "Оператор");
            if (code.Length != CodeLength || !_sessions.TryGetValue(code, out var found))
            {
                _fails.Hit(ip);
                _log.LogInformation("Оператор {Ip}: код не найден", ip);
                Fail(peer, "not-found");
                return;
            }
            s = found;
            lock (s.Gate)
            {
                if (s.State == HostState.Closed)
                {
                    _fails.Hit(ip);
                    Fail(peer, "not-found");
                    return;
                }
                // 2026-10-16: занятый код PIN не проверяет — чужие попытки не могут заблокировать идущий сеанс.
                if (s.State != HostState.Waiting)
                {
                    Fail(peer, "busy");
                    return;
                }
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pin), Encoding.ASCII.GetBytes(s.Pin)))
                {
                    _fails.Hit(ip);
                    s.PinFailures++;
                    var left = _o.MaxPinAttempts - s.PinFailures;
                    _log.LogWarning("Код {Code}: неверный PIN с {Ip} ({Failures}/{Max})", s.Code, ip, s.PinFailures, _o.MaxPinAttempts);
                    if (left <= 0)
                    {
                        Shutdown(s, new { type = "locked" }, null, $"заблокирован после {_o.MaxPinAttempts} неверных PIN");
                        Fail(peer, "locked");
                    }
                    else
                        Fail(peer, "wrong-pin", attemptsLeft: left);
                    return;
                }
                s.State = HostState.Asking;
                s.Operator = peer;
                s.OperatorName = name;
                s.CashierViewOnly = s.OperatorViewOnly = false;
                s.Deadline = Now + _o.ConsentTimeoutSeconds * 1000L;
                s.Host.SendJson(new { type = "join-request", @operator = name });
                peer.SendJson(new { type = "waiting", company = s.Company, machine = s.Machine, version = s.Version, timeout = _o.ConsentTimeoutSeconds });
                _log.LogInformation("Код {Code}: оператор «{Operator}» ({Ip}) просит доступ — ждём кассира", s.Code, name, ip);
            }
        }
        try
        {
            while (await peer.ReceiveAsync(_o.MaxTextBytes, 0).ConfigureAwait(false) is { } msg)
            {
                if (!msg.Binary)
                    OnOperatorText(s, peer, msg.Data);
            }
        }
        finally
        {
            lock (s.Gate)
            {
                if (ReferenceEquals(s.Operator, peer))
                {
                    if (s.State == HostState.InSession)
                        Shutdown(s, new { type = "end", reason = "disconnect" }, null, "связь с оператором прервалась");
                    else
                    {
                        s.Operator = null; // ждём ответ кассира без оператора — см. OnConsent
                        _log.LogInformation("Код {Code}: оператор ушёл до ответа кассира", s.Code);
                    }
                }
            }
        }
    }

    private void OnOperatorText(HostSession s, Peer peer, byte[] data)
    {
        using var doc = Parse(data);
        if (doc is null)
            return;
        var m = doc.RootElement;
        var type = Str(m, "type");
        lock (s.Gate)
        {
            if (!ReferenceEquals(s.Operator, peer) || type is null or "pong")
                return;
            if (type == "end")
            {
                if (s.State == HostState.InSession)
                    Shutdown(s, new { type = "end", reason = "operator" }, "operator", "оператор завершил");
                else
                {
                    s.Operator = null;
                    peer.SendJson(new { type = "end", reason = "operator" });
                    peer.Close();
                    _log.LogInformation("Код {Code}: оператор отменил запрос", s.Code);
                }
                return;
            }
            if (s.State != HostState.InSession)
                return;
            object? forward = null;
            var input = false;
            switch (type)
            {
                case "mouse":
                    input = true;
                    var action = Str(m, "action");
                    if (action is not ("move" or "down" or "up" or "wheel") || !Int(m, "x", out var x) || !Int(m, "y", out var y)
                        || x is < 0 or > 65535 || y is < 0 or > 65535)
                        return;
                    Int(m, "button", out var button);
                    Int(m, "delta", out var delta);
                    forward = new
                    {
                        type, action, x, y, button = Math.Clamp(button, 0, 2),
                        delta = action == "wheel" ? Math.Clamp(delta, -1200, 1200) : (long?)null,
                    };
                    break;
                case "key":
                    input = true;
                    var keyAction = Str(m, "action");
                    var code = Str(m, "code") ?? "";
                    var key = Str(m, "key");
                    if (keyAction is not ("down" or "up") || code.Length > 32 || !code.All(char.IsAsciiLetterOrDigit))
                        return;
                    forward = new { type, action = keyAction, code, key = key is { Length: > 0 and <= 8 } ? key : null };
                    break;
                case "text":
                    input = true;
                    var text = Str(m, "text");
                    if (string.IsNullOrEmpty(text))
                        return;
                    forward = new { type, text = text.Length > 10_000 ? text[..10_000] : text };
                    break;
                case "ack":
                    if (!Int(m, "frame", out var frame) || frame is < 0 or > uint.MaxValue)
                        return;
                    forward = new { type, frame };
                    break;
                case "quality":
                    if (!Int(m, "value", out var quality))
                        return;
                    forward = new { type, value = Math.Clamp(quality, 10, 90) };
                    break;
                case "view-only":
                    if (!m.TryGetProperty("value", out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return;
                    // 2026-10-16: оператор может сам включить «только просмотр»; снять выбор кассира — нет.
                    s.OperatorViewOnly = v.GetBoolean();
                    s.LastActivity = Now;
                    s.Host.SendJson(new { type = "view-only", value = s.ViewOnly });
                    peer.SendJson(new { type = "view-only", value = s.ViewOnly, cashier = s.CashierViewOnly });
                    return;
                default:
                    return; // остальное оператору кассе слать нельзя
            }
            // 2026-10-16: «только просмотр» — мышь, клавиши и текст на кассу не уходят (касса и сама их не исполняет).
            if (input && s.ViewOnly)
                return;
            s.LastActivity = Now;
            s.Host.SendJson(forward);
        }
    }

    // ---------- общее ----------

    /// <summary>Закрыть код: сообщение кассе (если есть), «end» оператору (если есть причина), оба соединения закрыть.
    /// Вызывать под s.Gate; повторный вызов ничего не делает.</summary>
    private void Shutdown(HostSession s, object? hostMessage, string? operatorReason, string why)
    {
        if (s.State == HostState.Closed)
            return;
        var wasSession = s.State == HostState.InSession;
        s.State = HostState.Closed;
        _sessions.TryRemove(new KeyValuePair<string, HostSession>(s.Code, s));
        if (hostMessage is not null)
            s.Host.SendJson(hostMessage);
        s.Host.Close();
        if (s.Operator is { } op)
        {
            if (operatorReason is not null)
                op.SendJson(new { type = "end", reason = operatorReason });
            op.Close();
            s.Operator = null;
        }
        if (wasSession)
            _log.LogInformation("Код {Code}: сеанс завершён — {Why}; оператор «{Operator}», {Minutes:0.#} мин, экран {Mb:0.#} МБ",
                s.Code, why, s.OperatorName, (Now - s.StartedAt) / 60000.0, s.BytesToOperator / 1048576.0);
        else
            _log.LogInformation("Код {Code}: закрыт — {Why}", s.Code, why);
    }

    /// <summary>Раз в секунду: истечение кода, ожидание кассира, простой и длительность сеанса, «ping» и мёртвые соединения.</summary>
    public void Tick()
    {
        var now = Now;
        var ping = now >= _nextPing;
        if (ping)
            _nextPing = now + _o.PingIntervalSeconds * 1000L;
        var dead = _o.DeadPeerSeconds * 1000L;
        foreach (var s in _sessions.Values)
        {
            lock (s.Gate)
            {
                switch (s.State)
                {
                    case HostState.Waiting when now >= s.ExpiresAt:
                        Shutdown(s, new { type = "expired" }, null, "код истёк");
                        continue;
                    case HostState.Asking when now >= s.Deadline:
                        Shutdown(s, new { type = "end", reason = "consent-timeout" }, "consent-timeout", "кассир не ответил");
                        continue;
                    case HostState.Cooldown when now >= s.Deadline:
                        s.State = HostState.Waiting;
                        break;
                    case HostState.InSession when now - s.LastActivity > _o.SessionIdleMinutes * 60_000L:
                        Shutdown(s, new { type = "end", reason = "timeout" }, "timeout", "нет активности оператора");
                        continue;
                    case HostState.InSession when now - s.StartedAt > _o.SessionMaxMinutes * 60_000L:
                        Shutdown(s, new { type = "end", reason = "timeout" }, "timeout", "достигнута наибольшая длительность");
                        continue;
                }
                if (now - s.Host.LastSeen > dead)
                {
                    Shutdown(s, null, "disconnect", "касса не отвечает");
                    s.Host.Kill();
                    continue;
                }
                if (s.Operator is { } op && now - op.LastSeen > dead)
                {
                    if (s.State == HostState.InSession)
                        Shutdown(s, new { type = "end", reason = "disconnect" }, null, "оператор не отвечает");
                    else
                        s.Operator = null;
                    op.Kill();
                    continue;
                }
                if (ping)
                {
                    s.Host.Send(PingMessage);
                    s.Operator?.Send(PingMessage);
                }
            }
        }
        if (now >= _nextCleanup)
        {
            _nextCleanup = now + 60_000;
            _joins.Cleanup();
            _fails.Cleanup();
            _hosts.Cleanup();
        }
    }

    /// <summary>Остановка сервера: всем «end» с причиной «server».</summary>
    public void ShutdownAll()
    {
        foreach (var s in _sessions.Values)
        {
            lock (s.Gate)
                Shutdown(s, new { type = "end", reason = "server" }, "server", "сервер остановлен");
        }
    }

    private static void Fail(Peer peer, string code, int? attemptsLeft = null, int? retryAfter = null)
    {
        peer.SendJson(new { type = "error", code, attemptsLeft, retryAfter });
        peer.Close();
    }

    private bool KeyOk(string? key) =>
        key is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), _keyHash);

    private static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || Uri.TryCreate(origin, UriKind.Absolute, out var u) && string.Equals(u.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static string IpOf(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null)
            return "?";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    private static string Digits(int length, bool nonZeroFirst)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append((char)('0' + (i == 0 && nonZeroFirst ? RandomNumberGenerator.GetInt32(1, 10) : RandomNumberGenerator.GetInt32(10))));
        return sb.ToString();
    }

    private static string OnlyDigits(string? s) => s is null ? "" : new string(s.Where(char.IsAsciiDigit).Take(16).ToArray());

    /// <summary>Имя/название для показа и журнала: буквы, цифры, простая пунктуация, без переводов строк.</summary>
    private static string Clean(string? s, int max, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var ch in s ?? "")
        {
            if (sb.Length >= max)
                break;
            if (char.IsLetterOrDigit(ch) || "-._()№#,'\"«»&+/:!?@".Contains(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? fallback : r;
    }

    private static JsonDocument? Parse(Peer.Msg? msg) => msg is { Binary: false } m ? Parse(m.Data) : null;

    private static JsonDocument? Parse(byte[] data)
    {
        try
        {
            var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc;
            doc.Dispose();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? StrOrNum(JsonElement m, string name) =>
        !m.TryGetProperty(name, out var e) ? null
        : e.ValueKind == JsonValueKind.String ? e.GetString()
        : e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n >= 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : null;

    private static bool Int(JsonElement m, string name, out long value)
    {
        value = 0;
        return m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }

    private static bool Bool(JsonElement m, string name) => m.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;
}
