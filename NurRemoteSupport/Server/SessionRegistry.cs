using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace NurRemoteSupport.Server;

public enum SessionState { Pending, AwaitingConsent, Active, Ended }

/// <param name="User">2026-10-16, v1.1: кто за кассой (имя кассира), необязательно.</param>
/// <param name="Note">2026-10-16, v1.1: что случилось — пишет кассир перед «Получить код» (до 500 символов), необязательно.</param>
public sealed record HostInfo(string App, string Version, string Company, string Machine, int ScreenW, int ScreenH, string User = "", string Note = "");

/// <summary>Соединение оператора (одна вкладка браузера) и сеанс, к которому оно сейчас привязано.</summary>
public sealed class OperatorContext(OperatorIdentity identity, WsConnection conn)
{
    public OperatorIdentity Identity { get; } = identity;
    public WsConnection Conn { get; } = conn;
    public RelaySession? Session { get; set; }
}

public sealed class RelaySession
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Pin { get; init; }
    public required HostInfo Host { get; init; }
    public required WsConnection HostConn { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    private int _state;
    public SessionState State
    {
        get => (SessionState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public int PinFailures { get; set; }
    public OperatorContext? Operator { get; set; }
    /// <summary>Последний, кто пытался войти (для журнала при блокировке).</summary>
    public OperatorIdentity? LastTried { get; set; }
    public string? LastTriedIp { get; set; }
    public DateTimeOffset? ConsentDeadline { get; set; }
    public DateTimeOffset? Started { get; set; }
    public string? EndReason { get; set; }

    public long BytesToOperator;
    public long BytesToHost;
}

/// <summary>
/// Реестр сеансов и все переходы состояний (2026-09-29). Переходы — под одной блокировкой (они редкие);
/// пересылка кадров идёт мимо блокировки, только с проверкой State == Active.
/// </summary>
public sealed class SessionRegistry
{
    private readonly RelayOptions _o;
    private readonly Journal _journal;
    private readonly ILogger<SessionRegistry> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RelaySession> _byCode = new();
    // Недавно закрытые коды: чтобы оператор увидел «устарел»/«заблокирован», а не «нет такого».
    private readonly ConcurrentDictionary<string, (string Error, DateTimeOffset Until)> _tombstones = new();
    private readonly ConcurrentBag<Task> _journalWrites = new();

    public SessionRegistry(RelayOptions o, Journal journal, ILogger<SessionRegistry> log)
    {
        _o = o;
        _journal = journal;
        _log = log;
    }

    public int Count => _byCode.Count;

    public IReadOnlyList<RelaySession> Snapshot() => _byCode.Values.ToList();

    // ---------- хост ----------

    public (RelaySession? session, string? error) CreateSession(WsConnection hostConn, HostInfo info)
    {
        lock (_gate)
        {
            if (_byCode.Count >= _o.MaxSessions) return (null, "server-busy");
            var pendingFromIp = _byCode.Values.Count(s => s.State == SessionState.Pending && s.HostConn.Ip == hostConn.Ip);
            if (pendingFromIp >= _o.MaxPendingPerIp) return (null, "rate-limited");

            string code;
            do code = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
            while (_byCode.ContainsKey(code) || _tombstones.ContainsKey(code));
            var pin = RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");
            var now = DateTimeOffset.UtcNow;
            var s = new RelaySession
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                Code = code,
                Pin = pin,
                Host = info,
                HostConn = hostConn,
                Created = now,
                ExpiresAt = now.AddSeconds(_o.SessionTtlSeconds),
                State = SessionState.Pending,
            };
            _byCode[code] = s;
            hostConn.SendControl(Msg.Json(new { type = "session", code, pin, expiresIn = _o.SessionTtlSeconds }));
            _log.LogInformation("Новый код {Session}: {Company} / {Machine} / {App} {Version}, IP {Ip}",
                s.Id, info.Company, info.Machine, info.App, info.Version, hostConn.Ip);
            return (s, null);
        }
    }

    public void Consent(RelaySession s, bool accept)
    {
        lock (_gate)
        {
            if (s.State != SessionState.AwaitingConsent || s.Operator is null) return;
            var op = s.Operator;
            if (!accept)
            {
                _log.LogInformation("Кассир отклонил сеанс {Session} (оператор {Operator})", s.Id, op.Identity.Login);
                op.Conn.SendControl(Msg.Error("declined"));
                FinishLocked(s, "declined", hostMsg: Msg.End("declined"), operatorMsg: null, closeOperator: false, tombstone: "declined");
                return;
            }
            s.State = SessionState.Active;
            s.Started = DateTimeOffset.UtcNow;
            s.HostConn.SendControl(Msg.Json(new { type = "started", sessionId = s.Id, @operator = op.Identity.Name }));
            op.Conn.SendControl(Msg.Json(new
            {
                type = "started",
                sessionId = s.Id,
                @operator = op.Identity.Name,
                login = op.Identity.Login,
                host = new
                {
                    app = s.Host.App,
                    version = s.Host.Version,
                    company = s.Host.Company,
                    machine = s.Host.Machine,
                    screen = new { w = s.Host.ScreenW, h = s.Host.ScreenH },
                },
            }));
            _log.LogInformation("Сеанс {Session} начат: оператор {Operator} → {Company} / {Machine}", s.Id, op.Identity.Login, s.Host.Company, s.Host.Machine);
        }
    }

    // ---------- оператор ----------

    /// <summary>2026-10-16, v1.1: вход из очереди программы оператора — касса выбрана по номеру сеанса из /api/sessions,
    /// кассир диктует только PIN. Дальше всё как при входе по коду (PIN, блокировка, согласие кассира).</summary>
    public void JoinBySession(OperatorContext ctx, string sessionId, string pin)
    {
        string? code;
        lock (_gate)
            code = _byCode.Values.FirstOrDefault(x => x.Id == sessionId)?.Code;
        Join(ctx, code ?? "", pin);
    }

    public void Join(OperatorContext ctx, string rawCode, string pin)
    {
        var code = new string(rawCode.Where(char.IsAsciiDigit).ToArray());
        lock (_gate)
        {
            if (ctx.Session is not null)
            {
                ctx.Conn.SendControl(Msg.Error("bad-request"));
                return;
            }
            if (code.Length != 9 || !_byCode.TryGetValue(code, out var s))
            {
                var err = code.Length == 9 && _tombstones.TryGetValue(code, out var t) ? t.Error : "no-session";
                ctx.Conn.SendControl(Msg.Error(err));
                return;
            }
            if (s.State != SessionState.Pending)
            {
                ctx.Conn.SendControl(Msg.Error("busy"));
                return;
            }
            if (DateTimeOffset.UtcNow > s.ExpiresAt)
            {
                ExpireLocked(s);
                ctx.Conn.SendControl(Msg.Error("expired"));
                return;
            }
            s.LastTried = ctx.Identity;
            s.LastTriedIp = ctx.Conn.Ip;
            var pinOk = pin.Length == s.Pin.Length &&
                        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pin), Encoding.ASCII.GetBytes(s.Pin));
            if (!pinOk)
            {
                s.PinFailures++;
                var left = _o.MaxPinAttempts - s.PinFailures;
                _log.LogWarning("Неверный PIN для {Session} от {Operator} ({Ip}), осталось {Left}", s.Id, ctx.Identity.Login, ctx.Conn.Ip, left);
                if (left <= 0)
                {
                    ctx.Conn.SendControl(Msg.Error("locked"));
                    FinishLocked(s, "locked", hostMsg: Msg.Json(new { type = "locked" }), operatorMsg: null, closeOperator: false, tombstone: "locked", journal: true);
                }
                else
                {
                    ctx.Conn.SendControl(Msg.Json(new { type = "error", code = "bad-pin", left }));
                }
                return;
            }
            s.State = SessionState.AwaitingConsent;
            s.Operator = ctx;
            s.ConsentDeadline = DateTimeOffset.UtcNow.AddSeconds(_o.ConsentTimeoutSeconds);
            ctx.Session = s;
            s.HostConn.SendControl(Msg.Json(new { type = "join-request", @operator = ctx.Identity.Name, login = ctx.Identity.Login, ip = ctx.Conn.Ip }));
            ctx.Conn.SendControl(Msg.Json(new { type = "waiting-consent" }));
            _log.LogInformation("Оператор {Operator} ({Ip}) ждёт согласия кассира {Session}", ctx.Identity.Login, ctx.Conn.Ip, s.Id);
        }
    }

    // ---------- завершение ----------

    /// <summary>Сторона прислала {"type":"end"}.</summary>
    public void EndBy(RelaySession s, string side, string? requested)
    {
        var reason = requested is "error" or "timeout" ? requested : side;
        lock (_gate) EndLocked(s, reason, fromSide: side);
    }

    /// <summary>Соединение стороны закрылось/оборвалось.</summary>
    public void Disconnected(RelaySession s, string side, string reason)
    {
        lock (_gate)
        {
            if (s.State == SessionState.Ended) return;
            if (side == "operator" && s.State == SessionState.Pending) return; // оператор ещё не привязан
            EndLocked(s, reason, fromSide: side);
        }
    }

    private void EndLocked(RelaySession s, string reason, string fromSide)
    {
        switch (s.State)
        {
            case SessionState.Ended:
                return;
            case SessionState.Pending:
                // Хост ушёл до подключения оператора — просто убираем код.
                FinishLocked(s, reason, hostMsg: fromSide == "host" ? null : Msg.End(reason), operatorMsg: null, closeOperator: false, tombstone: "no-session", journal: false);
                _log.LogInformation("Код {Session} снят до подключения ({Reason})", s.Id, reason);
                return;
            case SessionState.AwaitingConsent:
                // Никто не начал: хосту — end, оператору — end, его соединение остаётся для нового ввода кода.
                FinishLocked(s, reason,
                    hostMsg: fromSide == "host" ? null : Msg.End(reason),
                    operatorMsg: fromSide == "operator" ? null : Msg.End(reason),
                    closeOperator: false, tombstone: "no-session");
                return;
            case SessionState.Active:
                FinishLocked(s, reason,
                    hostMsg: Msg.End(reason),
                    operatorMsg: Msg.End(reason),
                    closeOperator: true, tombstone: "no-session");
                return;
        }
    }

    private void ExpireLocked(RelaySession s)
    {
        _log.LogInformation("Код {Session} истёк без подключения", s.Id);
        FinishLocked(s, "expired", hostMsg: Msg.Json(new { type = "expired" }), operatorMsg: null, closeOperator: false, tombstone: "expired", journal: false);
    }

    /// <summary>Единая точка завершения: сообщения, закрытие соединений, журнал.</summary>
    private void FinishLocked(RelaySession s, string reason, byte[]? hostMsg, byte[]? operatorMsg, bool closeOperator, string tombstone, bool? journal = null)
    {
        if (s.State == SessionState.Ended) return;
        var wasStarted = s.State == SessionState.Active;
        s.State = SessionState.Ended;
        s.EndReason = reason;
        _byCode.TryRemove(s.Code, out _);
        _tombstones[s.Code] = (tombstone, DateTimeOffset.UtcNow.AddHours(2));

        if (hostMsg is not null) s.HostConn.SendControl(hostMsg);
        s.HostConn.Close();

        var op = s.Operator;
        if (op is not null)
        {
            if (operatorMsg is not null) op.Conn.SendControl(operatorMsg);
            if (op.Session == s) op.Session = null;
            if (closeOperator) op.Conn.Close();
        }

        var end = DateTimeOffset.UtcNow;
        if (wasStarted)
            _log.LogInformation("Сеанс {Session} завершён ({Reason}), {Seconds} с, к оператору {BytesOut} байт, к кассе {BytesIn} байт",
                s.Id, reason, (long)(end - s.Started!.Value).TotalSeconds, Interlocked.Read(ref s.BytesToOperator), Interlocked.Read(ref s.BytesToHost));
        else if (op is not null || reason == "locked")
            _log.LogInformation("Сеанс {Session} не начат: {Reason}", s.Id, reason);

        if (journal ?? (op is not null))
        {
            var who = op?.Identity ?? s.LastTried;
            var entry = new JournalEntry
            {
                SessionId = s.Id,
                Code = s.Code,
                Operator = who?.Login,
                OperatorName = who?.Name,
                OperatorIp = op?.Conn.Ip ?? s.LastTriedIp,
                Company = s.Host.Company,
                Machine = s.Host.Machine,
                App = s.Host.App,
                Version = s.Host.Version,
                HostIp = s.HostConn.Ip,
                User = s.Host.User.Length > 0 ? s.Host.User : null,
                Note = s.Host.Note.Length > 0 ? s.Host.Note : null,
                Created = s.Created,
                Start = s.Started,
                End = end,
                DurationSec = s.Started is { } st ? (long)(end - st).TotalSeconds : 0,
                Reason = reason,
                BytesToOperator = Interlocked.Read(ref s.BytesToOperator),
                BytesToHost = Interlocked.Read(ref s.BytesToHost),
            };
            _journalWrites.Add(_journal.AppendAsync(entry));
        }
    }

    // ---------- пересылка ----------

    /// <summary>Переслать сообщение другой стороне. Буфер in.Buffer переходит сюда.</summary>
    public async ValueTask ForwardAsync(RelaySession s, bool toOperator, Incoming m)
    {
        var target = toOperator ? s.Operator?.Conn : s.HostConn;
        if (s.State != SessionState.Active || target is null)
        {
            m.Return();
            return;
        }
        if (await target.EnqueueAsync(m.Buffer, m.Length, m.Type, pooled: true).ConfigureAwait(false))
        {
            if (toOperator) Interlocked.Add(ref s.BytesToOperator, m.Length);
            else Interlocked.Add(ref s.BytesToHost, m.Length);
        }
    }

    // ---------- обслуживание ----------

    /// <summary>Раз в секунду: истёкшие коды, нет ответа кассира, старые «надгробия».</summary>
    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var s in _byCode.Values)
            {
                if (s.State == SessionState.Pending && now > s.ExpiresAt)
                    ExpireLocked(s);
                else if (s.State == SessionState.AwaitingConsent && s.ConsentDeadline is { } d && now > d)
                {
                    _log.LogInformation("Кассир не ответил на запрос {Session}", s.Id);
                    s.Operator?.Conn.SendControl(Msg.Error("declined"));
                    FinishLocked(s, "consent-timeout", hostMsg: Msg.End("consent-timeout"), operatorMsg: null, closeOperator: false, tombstone: "declined");
                }
            }
            foreach (var (code, t) in _tombstones)
                if (now > t.Until) _tombstones.TryRemove(code, out _);
        }
        // Уборка завершённых записей журнала
        if (_journalWrites.Count > 256 && _journalWrites.All(t => t.IsCompleted)) _journalWrites.Clear();
    }

    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            foreach (var s in _byCode.Values.ToList())
                EndLocked(s, "shutdown", fromSide: "server");
        }
        await Task.WhenAll(_journalWrites.ToArray()).ConfigureAwait(false);
    }
}
