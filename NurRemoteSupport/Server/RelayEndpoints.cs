using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace NurRemoteSupport.Server;

/// <summary>WebSocket-адреса /ws/host и /ws/operator (2026-09-29, по PROTOCOL.md).</summary>
public static class RelayEndpoints
{
    public static string ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return "?";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    private static readonly WebSocketAcceptContext AcceptCtx = new()
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15),
        DangerousEnableCompression = false, // JPEG не сжимается, а CPU и память тратятся
    };

    // ---------- хост (касса) ----------

    public static async Task HandleHostAsync(HttpContext http, SessionRegistry registry, AttemptLimiter limiter, RelayOptions o, ILoggerFactory lf)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var log = lf.CreateLogger("Relay.Host");
        var ip = ClientIp(http);
        using var ws = await http.WebSockets.AcceptWebSocketAsync(AcceptCtx);
        var conn = new WsConnection(ws, "host", ip, o, log);
        conn.Start();

        RelaySession? session = null;
        var reason = "disconnect";
        try
        {
            if (!limiter.TryHit("hostconn:" + ip, o.HostConnectsPerMinutePerIp, TimeSpan.FromMinutes(1)))
            {
                log.LogWarning("Слишком много подключений кассы с {Ip}", ip);
                conn.SendControl(Msg.Error("rate-limited"));
                return;
            }

            // 1. hello — первым, не позже HelloTimeoutSeconds
            Incoming? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
            {
                helloCts.CancelAfter(TimeSpan.FromSeconds(o.HelloTimeoutSeconds));
                first = await conn.ReceiveAsync(helloCts.Token);
            }
            if (first is not { } hello) return;
            HostInfo? info;
            try { info = ParseHello(hello); }
            finally { hello.Return(); }
            if (info is null)
            {
                conn.SendControl(Msg.Error("protocol"));
                return;
            }

            var (s, error) = registry.CreateSession(conn, info);
            if (s is null)
            {
                log.LogWarning("Код для {Ip} не выдан: {Error}", ip, error);
                conn.SendControl(Msg.Error(error!));
                return;
            }
            session = s;

            // 2. основной цикл
            while (true)
            {
                var got = await conn.ReceiveAsync(conn.Token);
                if (got is not { } m) break;
                if (m.Type == WebSocketMessageType.Binary)
                {
                    await registry.ForwardAsync(session, toOperator: true, m);
                    continue;
                }
                var type = Msg.PeekType(m.Span);
                if (type is null || Msg.Reserved.Contains(type))
                {
                    try { HandleHostControl(type, m, conn, session, registry); }
                    catch (JsonException) { }
                    finally { m.Return(); }
                    if (session.State == SessionState.Ended) { reason = session.EndReason ?? reason; }
                    continue;
                }
                await registry.ForwardAsync(session, toOperator: true, m);
            }
        }
        catch (MessageTooBigException ex)
        {
            log.LogWarning("Касса {Ip}: {Message}", ip, ex.Message);
            reason = "error";
            conn.SetCloseStatus(WebSocketCloseStatus.MessageTooBig, "too big");
            if (session is not null) registry.Disconnected(session, "host", reason);
        }
        catch (SendStallException)
        {
            log.LogWarning("Оператор не принимает кадры 30 с — сеанс {Session} завершается", session?.Id);
            reason = "error";
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            // оборвалось
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка соединения кассы {Ip}", ip);
            reason = "error";
        }
        finally
        {
            if (conn.TimedOut) reason = "timeout";
            if (session is not null) registry.Disconnected(session, "host", reason);
            conn.Close();
            await DrainAsync(conn);
            await conn.Finished.WaitAsync(TimeSpan.FromSeconds(6)).ContinueWith(_ => { });
        }
    }

    private static void HandleHostControl(string? type, Incoming m, WsConnection conn, RelaySession s, SessionRegistry registry)
    {
        switch (type)
        {
            case "ping":
                conn.SendControl(Pong(m));
                break;
            case "pong":
                conn.PongSeen = true;
                break;
            case "consent":
            {
                using var doc = JsonDocument.Parse(m.Buffer.AsMemory(0, m.Length));
                registry.Consent(s, Msg.Bool(doc.RootElement, "accept") == true);
                break;
            }
            case "end":
            {
                using var doc = JsonDocument.Parse(m.Buffer.AsMemory(0, m.Length));
                registry.EndBy(s, "host", Msg.Str(doc.RootElement, "reason", 32));
                break;
            }
            // остальное служебное от хоста (hello повторно, подделки) — отбрасываем
        }
    }

    private static HostInfo? ParseHello(Incoming m)
    {
        if (m.Type != WebSocketMessageType.Text) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Buffer.AsMemory(0, m.Length));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Msg.Str(r, "type") != "hello" || Msg.Str(r, "role") != "host" || Msg.Int(r, "protocol") != 1) return null;
            int w = 0, h = 0;
            if (r.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                w = Math.Clamp(Msg.Int(sc, "w") ?? 0, 0, 16384);
                h = Math.Clamp(Msg.Int(sc, "h") ?? 0, 0, 16384);
            }
            return new HostInfo(
                Msg.Str(r, "app") ?? "",
                Msg.Str(r, "version") ?? "",
                Msg.Str(r, "company") ?? "",
                Msg.Str(r, "machine") ?? "",
                w, h,
                Msg.Str(r, "user") ?? "",
                Msg.Str(r, "note", 500) ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Pong(Incoming ping)
    {
        try
        {
            using var doc = JsonDocument.Parse(ping.Buffer.AsMemory(0, ping.Length));
            if (doc.RootElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number)
                return Msg.Json(new { type = "pong", t = t.GetDouble() });
        }
        catch (JsonException) { }
        return Msg.Json(new { type = "pong" });
    }

    /// <summary>
    /// После нашего закрытия дочитываем, что клиент успел прислать, до его ответного close (не дольше 5 с).
    /// Иначе непрочитанные данные превращают закрытие в TCP RST, и клиент может не получить последнее сообщение (ошибку/end).
    /// </summary>
    private static async Task DrainAsync(WsConnection conn)
    {
        try
        {
            while (conn.CanReceive)
            {
                var m = await conn.ReceiveAsync(conn.Token);
                if (m is not { } got) break;
                got.Return();
            }
        }
        catch { /* оборвалось или вышло время — нормально */ }
    }

    // ---------- оператор (браузер) ----------

    public static async Task HandleOperatorAsync(HttpContext http, SessionRegistry registry, AttemptLimiter limiter, TokenService tokens, RelayOptions o, ILoggerFactory lf)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // 2026-10-19 (проверка безопасности): программа оператора шлёт токен заголовком Authorization (адреса попадают в журналы
        // прокси); из адреса (?token=) — только для браузерного оператора, который не умеет заголовки у WebSocket.
        var auth = http.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : http.Request.Query["token"].ToString();
        var identity = tokens.Validate(token);
        if (identity is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var log = lf.CreateLogger("Relay.Operator");
        var ip = ClientIp(http);
        using var ws = await http.WebSockets.AcceptWebSocketAsync(AcceptCtx);
        var conn = new WsConnection(ws, "operator", ip, o, log);
        conn.Start();
        var ctx = new OperatorContext(identity, conn);
        var reason = "disconnect";
        log.LogInformation("Оператор {Operator} подключился с {Ip}", identity.Login, ip);

        try
        {
            while (true)
            {
                var got = await conn.ReceiveAsync(conn.Token);
                if (got is not { } m) break;
                var s = ctx.Session;
                if (m.Type == WebSocketMessageType.Binary)
                {
                    m.Return(); // в v1 двоичное от оператора не пересылается
                    continue;
                }
                var type = Msg.PeekType(m.Span);
                if (type is null || Msg.Reserved.Contains(type))
                {
                    try { HandleOperatorControl(type, m, ctx, registry, limiter, o, log); }
                    catch (JsonException) { }
                    finally { m.Return(); }
                    continue;
                }
                if (s is null)
                {
                    m.Return();
                    continue;
                }
                await registry.ForwardAsync(s, toOperator: false, m);
            }
        }
        catch (MessageTooBigException ex)
        {
            log.LogWarning("Оператор {Operator}: {Message}", identity.Login, ex.Message);
            reason = "error";
            conn.SetCloseStatus(WebSocketCloseStatus.MessageTooBig, "too big");
            if (ctx.Session is { } s) registry.Disconnected(s, "operator", reason);
        }
        catch (SendStallException)
        {
            log.LogWarning("Касса не принимает сообщения 30 с — сеанс {Session} завершается", ctx.Session?.Id);
            reason = "error";
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка соединения оператора {Operator}", identity.Login);
            reason = "error";
        }
        finally
        {
            if (conn.TimedOut) reason = "timeout";
            if (ctx.Session is { } s) registry.Disconnected(s, "operator", reason);
            conn.Close();
            await DrainAsync(conn);
            await conn.Finished.WaitAsync(TimeSpan.FromSeconds(6)).ContinueWith(_ => { });
            log.LogInformation("Оператор {Operator} отключился", identity.Login);
        }
    }

    private static void HandleOperatorControl(string? type, Incoming m, OperatorContext ctx, SessionRegistry registry, AttemptLimiter limiter, RelayOptions o, ILogger log)
    {
        switch (type)
        {
            case "ping":
                ctx.Conn.SendControl(Pong(m));
                return;
            case "pong":
                ctx.Conn.PongSeen = true;
                return;
            case "join":
            {
                string? code, pin, sessionId;
                try
                {
                    using var doc = JsonDocument.Parse(m.Buffer.AsMemory(0, m.Length));
                    code = Msg.Str(doc.RootElement, "code", 32);
                    pin = Msg.Str(doc.RootElement, "pin", 16);
                    sessionId = Msg.Str(doc.RootElement, "session", 64); // 2026-10-16, v1.1: вход из очереди
                }
                catch (JsonException) { code = pin = sessionId = null; }
                if ((code is null && sessionId is null) || pin is null)
                {
                    ctx.Conn.SendControl(Msg.Error("bad-request"));
                    return;
                }
                var window = TimeSpan.FromMinutes(1);
                if (!limiter.TryHit("join-ip:" + ctx.Conn.Ip, o.JoinPerMinute, window) ||
                    !limiter.TryHit("join-op:" + ctx.Identity.Login, o.JoinPerMinute, window))
                {
                    log.LogWarning("Слишком много попыток входа: {Operator} {Ip}", ctx.Identity.Login, ctx.Conn.Ip);
                    ctx.Conn.SendControl(Msg.Error("rate-limited"));
                    return;
                }
                if (code is not null)
                    registry.Join(ctx, code, pin);
                else
                    registry.JoinBySession(ctx, sessionId!, pin);
                return;
            }
            case "end":
            {
                if (ctx.Session is not { } s)
                {
                    ctx.Conn.SendControl(Msg.Error("not-joined"));
                    return;
                }
                string? reason = null;
                try
                {
                    using var doc = JsonDocument.Parse(m.Buffer.AsMemory(0, m.Length));
                    reason = Msg.Str(doc.RootElement, "reason", 32);
                }
                catch (JsonException) { }
                registry.EndBy(s, "operator", reason);
                return;
            }
            case null:
                ctx.Conn.SendControl(Msg.Error("bad-request"));
                return;
            // остальные служебные типы от оператора — подделка, отбрасываем
        }
    }
}
