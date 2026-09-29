using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Состояние связи с сервером NurCRM (см. <see cref="ServerOutageMonitor"/>).</summary>
public enum ServerLinkState
{
    /// <summary>Сервер отвечает.</summary>
    Online,

    /// <summary>Были сбои подряд, но меньше порога — касса ещё работает с сервером.</summary>
    Degraded,

    /// <summary>Сервер не отвечает — касса работает автономно, пока проверка связи не пройдёт.</summary>
    Outage,
}

/// <summary>
/// 2026-09-29, требование владельца: «при сбое бэка кассы не выводи ошибку, а просто предупреди
/// об ошибке и продолжай работу автономно до исправления бэка».
///
/// Раньше офлайн-режим включался только при входе без интернета (PosApp.IsOfflineBootstrap), а
/// сбой самого сервера — 502/503/504 от Cloudflare, 500, 520–524, обрыв TLS, «чёрная дыра»,
/// HTML вместо JSON, поток 429 — каждая операция встречала заново: оплата ждала таймаутов и
/// показывала «Оплата не прошла», фоновые синхронизации долбили сервер и писали ошибки.
///
/// Машина состояний (одна на процесс, все сетевые запросы кассы проходят через неё):
/// <code>
///   Online ──сбой──▶ Degraded ──ещё сбои (всего 3 подряд)──▶ Outage
///      ▲                │ успех                                  │
///      └────────────────┘                                        │ проверка связи с паузой
///      ▲                                                         │ 10 → 20 → 40 → 60 → 60 … с
///      └──────── успешная проверка или любой успешный ответ ◀─────┘
/// </code>
/// • «Жёсткий» сбой (оплата не прошла из-за сервера после своих повторов) переводит в Outage сразу.
/// • Сбой — ответ 5xx (включая 520–524 Cloudflare), 408, 429, обрыв/отказ соединения, DNS, TLS,
///   таймаут, ответ 2xx, который не разбирается как JSON. 4xx — сервер жив (отказ по существу).
/// • В Outage касса не ходит на сервер: продажи сразу в офлайн-очередь с ключом идемпотентности,
///   каталог и остатки — из локальной базы и соседних касс, фоновые синхронизации молчат.
///   Проверку связи ведёт только этот класс (<see cref="HealthProbe"/>), с нарастающей паузой.
/// • Возврат в Online — событие <see cref="Recovered"/>: SyncService сразу досылает очередь и
///   сообщает итог через <see cref="NotifyQueueFlushed"/> (кассиру — «отправлено N продаж»).
/// Каждый переход и каждый проглоченный сбой пишутся в журнал с категорией «OUTAGE».
/// </summary>
public static class ServerOutageMonitor
{
    /// <summary>Сколько сбоев подряд (без единого успеха между ними) означает «сервер лёг».</summary>
    public const int FailuresToOutage = 3;

    /// <summary>Паузы между проверками связи в аварии: быстро в начале, дальше не чаще раза в минуту.</summary>
    private static readonly TimeSpan[] ProbeDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(60),
    ];

    private static readonly object Sync = new();
    private static ServerLinkState _state = ServerLinkState.Online;
    private static int _consecutiveFailures;
    private static DateTimeOffset? _outageSince;
    private static DateTimeOffset? _lastErrorAt;
    private static string? _lastError;
    private static DateTimeOffset? _lastSuccessAt;
    private static CancellationTokenSource? _probeCts;
    private static int _probeAttempt;

    /// <summary>Проверка «сервер снова отвечает» — задаёт NurMarketApiClient (лёгкий запрос API с
    /// авторизацией). true — сервер жив. Исключения считаются неудачей.</summary>
    public static Func<CancellationToken, Task<bool>>? HealthProbe { get; set; }

    /// <summary>Переход состояния: (было, стало). Вызывается не из UI-потока.</summary>
    public static event Action<ServerLinkState, ServerLinkState>? StateChanged;

    /// <summary>Сервер снова отвечает после аварии — пора досылать очередь.</summary>
    public static event Action? Recovered;

    /// <summary>Очередь досылки разобрана после восстановления: (отправлено, осталось).</summary>
    public static event Action<int, int>? QueueFlushed;

    public static ServerLinkState State
    {
        get { lock (Sync) return _state; }
    }

    public static bool IsOutage => State == ServerLinkState.Outage;

    /// <summary>С какого момента сервер не отвечает (null — не в аварии).</summary>
    public static DateTimeOffset? OutageSince
    {
        get { lock (Sync) return _outageSince; }
    }

    /// <summary>Последний сбой (текст для подсказки в шапке) и когда он был.</summary>
    public static (string? Error, DateTimeOffset? At) LastError
    {
        get { lock (Sync) return (_lastError, _lastErrorAt); }
    }

    /// <summary>Последний успешный ответ сервера в этом запуске (для «данные на HH:mm»).</summary>
    public static DateTimeOffset? LastSuccessAt
    {
        get { lock (Sync) return _lastSuccessAt; }
    }

    /// <summary>Сбой ли это сервера/связи (а не отказ по существу). Такие операции касса повторяет
    /// позже или ставит в очередь; 4xx — нет.</summary>
    public static bool IsServerFailure(Exception? ex) => ex switch
    {
        null => false,
        ApiException api => IsServerFailureStatus(api.StatusCode),
        HttpRequestException => true,
        OperationCanceledException => true,
        JsonException => true,
        System.IO.IOException => true,
        _ => ex.InnerException != null && IsServerFailure(ex.InnerException),
    };

    public static bool IsServerFailureStatus(int? statusCode) =>
        statusCode is >= 500 and <= 599 or 408 or 429;

    /// <summary>Сбой запроса к серверу. <paramref name="hard"/> — операция, от которой зависит
    /// работа кассы (оплата), уже исчерпала свои повторы: авария объявляется сразу.</summary>
    public static void ReportFailure(string context, string error, bool hard = false)
    {
        ServerLinkState from, to;
        int failures;
        lock (Sync)
        {
            from = _state;
            _lastError = Shorten(error);
            _lastErrorAt = DateTimeOffset.Now;
            failures = ++_consecutiveFailures;
            if (_state == ServerLinkState.Outage)
                return; // В аварии сбои ждали — проверку ведёт ProbeLoopAsync, журнал не засоряем.

            to = hard || failures >= FailuresToOutage ? ServerLinkState.Outage : ServerLinkState.Degraded;
            _state = to;
            if (to == ServerLinkState.Outage)
            {
                _outageSince = DateTimeOffset.Now;
                StartProbeLoopLocked();
            }
        }

        PosLogger.Log(
            $"Сбой сервера NurCRM ({context}): {Shorten(error)} — подряд {failures}{(hard ? ", критичная операция" : "")}.",
            to == ServerLinkState.Outage ? "OUTAGE WARNING" : "OUTAGE");
        if (from != to)
            RaiseStateChanged(from, to);
    }

    public static void ReportFailure(string context, Exception ex, bool hard = false) =>
        ReportFailure(context, Describe(ex), hard);

    /// <summary>Успешный ответ сервера (разобранный JSON или отказ 4xx — сервер жив).</summary>
    public static void ReportSuccess(string context)
    {
        ServerLinkState from;
        lock (Sync)
        {
            from = _state;
            _consecutiveFailures = 0;
            _lastSuccessAt = DateTimeOffset.Now;
            if (from == ServerLinkState.Online)
                return;
            _state = ServerLinkState.Online;
            StopProbeLoopLocked();
        }

        if (from == ServerLinkState.Outage)
        {
            var since = OutageSinceForLog();
            PosLogger.Log($"Связь с сервером NurCRM восстановлена ({context}){since}.", "OUTAGE");
            lock (Sync)
                _outageSince = null;
        }
        else
        {
            PosLogger.Log($"Сервер NurCRM снова отвечает ({context}) после единичных сбоев.", "OUTAGE");
        }

        RaiseStateChanged(from, ServerLinkState.Online);
        if (from == ServerLinkState.Outage)
            SafeInvoke(() => Recovered?.Invoke(), "Recovered");
    }

    /// <summary>Итог проверки связи (опрос шапки, SyncService): false — считается сбоем.
    /// Успех этой проверки успехом НЕ считается: она смотрит корень сайта, а его веб-сервер отдаёт
    /// и тогда, когда сам API лежит (бэк упал, страница сайта жива). «Сервер отвечает» — только
    /// разобранный ответ API (NurMarketApiClient) или проверка монитора (профиль с авторизацией).</summary>
    public static void ReportProbeResult(bool reachable, string context)
    {
        if (!reachable)
            ReportFailure(context, Tr.T("сервер не ответил на проверку связи", "сервер байланышты текшерүүгө жооп берген жок",
                "the server did not answer the connection check", "sunucu bağlantı kontrolüne yanıt vermedi",
                "server aloqa tekshiruviga javob bermadi"));
    }

    /// <summary>SyncService разобрал очередь после восстановления связи.</summary>
    public static void NotifyQueueFlushed(int sent, int left)
    {
        PosLogger.Log($"После восстановления связи отправлено продаж: {sent}, осталось в очереди: {left}.", "OUTAGE");
        SafeInvoke(() => QueueFlushed?.Invoke(sent, left), "QueueFlushed");
    }

    /// <summary>«Данные на 14:05, сервер не отвечает» — для разделов владельца, показывающих
    /// сохранённые данные вместо ошибки. <paramref name="dataAt"/> — когда данные получены.</summary>
    public static string StaleDataNote(DateTime? dataAt)
    {
        var at = dataAt ?? LastSuccessAt?.LocalDateTime;
        return at is { } time
            ? Tr.T($"Данные на {time:HH:mm}, сервер не отвечает", $"Маалымат {time:HH:mm} боюнча, сервер жооп бербей жатат",
                $"Data as of {time:HH:mm}, the server is not responding", $"Veriler {time:HH:mm} itibarıyla, sunucu yanıt vermiyor",
                $"Ma'lumotlar {time:HH:mm} holatiga ko'ra, server javob bermayapti")
            : Tr.T("Сервер не отвечает — показаны данные этой кассы", "Сервер жооп бербей жатат — ушул кассанын маалыматтары көрсөтүлдү",
                "The server is not responding — showing this till's data", "Sunucu yanıt vermiyor — bu kasanın verileri gösteriliyor",
                "Server javob bermayapti — shu kassaning ma'lumotlari ko'rsatildi");
    }

    /// <summary>Хвост к <see cref="StaleDataNote"/> в разделах, где вместо данных сервера
    /// показана локальная история этой кассы.</summary>
    public static string LocalDataSuffix => Tr.T(
        " — показаны данные этой кассы за выбранный период.",
        " — тандалган мезгил үчүн ушул кассанын маалыматтары көрсөтүлдү.",
        " — showing this till's data for the selected period.",
        " — seçilen dönem için bu kasanın verileri gösteriliyor.",
        " — tanlangan davr uchun shu kassaning ma'lumotlari ko'rsatildi.");

    /// <summary>Только для проверочного стенда: сбросить состояние между сценариями.</summary>
    public static void ResetForTests()
    {
        lock (Sync)
        {
            StopProbeLoopLocked();
            _state = ServerLinkState.Online;
            _consecutiveFailures = 0;
            _outageSince = null;
            _lastError = null;
            _lastErrorAt = null;
        }
    }

    /// <summary>Короткое описание сбоя для журнала и подсказки (без тел ответов и токенов).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ApiException api => $"HTTP {api.StatusCode?.ToString() ?? "?"}: {Shorten(api.Message)}",
        HttpRequestException http => $"{Tr.T("нет соединения", "байланыш жок", "no connection", "bağlantı yok", "aloqa yo'q")}: {Shorten(http.Message)}",
        OperationCanceledException => Tr.T("сервер не ответил вовремя (таймаут)", "сервер убагында жооп берген жок (таймаут)",
            "the server did not respond in time (timeout)", "sunucu zamanında yanıt vermedi (zaman aşımı)",
            "server o'z vaqtida javob bermadi (taymaut)"),
        JsonException => Tr.T("сервер прислал повреждённый ответ", "сервер бузулган жооп жөнөттү",
            "the server sent a corrupted response", "sunucu bozuk bir yanıt gönderdi", "server buzilgan javob yubordi"),
        _ => $"{ex.GetType().Name}: {Shorten(ex.Message)}",
    };

    // ── Проверка связи в аварии ─────────────────────────────────────────────────────

    private static void StartProbeLoopLocked()
    {
        StopProbeLoopLocked();
        _probeAttempt = 0;
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _ = Task.Run(() => ProbeLoopAsync(cts.Token));
    }

    private static void StopProbeLoopLocked()
    {
        try { _probeCts?.Cancel(); } catch (ObjectDisposedException) { }
        _probeCts = null;
    }

    private static async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            lock (Sync)
            {
                delay = ProbeDelays[Math.Min(_probeAttempt, ProbeDelays.Length - 1)];
                _probeAttempt++;
            }

            // Сервер сам попросил паузу (429) — раньше неё не стучимся.
            var throttle = Api.ApiThrottle.RemainingBlock;
            if (throttle > delay)
                delay = throttle;

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var probe = HealthProbe;
            if (probe == null)
                continue;

            bool ok;
            string? error = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                ok = await probe(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ok = false;
                error = Describe(ex);
            }

            if (ct.IsCancellationRequested)
                return;

            if (ok)
            {
                ReportSuccess(Tr.T("проверка связи", "байланышты текшерүү", "connection check", "bağlantı kontrolü", "aloqa tekshiruvi"));
                return;
            }

            lock (Sync)
            {
                if (error != null)
                {
                    _lastError = error;
                    _lastErrorAt = DateTimeOffset.Now;
                }
            }

            PosLogger.Log(
                $"Проверка связи с сервером NurCRM: не отвечает{(error != null ? $" ({error})" : "")}, следующая через " +
                $"{ProbeDelays[Math.Min(_probeAttempt, ProbeDelays.Length - 1)].TotalSeconds:0} с.",
                "OUTAGE");
        }
    }

    private static string OutageSinceForLog()
    {
        var since = OutageSince;
        return since is { } s ? $", сервер не отвечал с {s:HH:mm:ss} ({(DateTimeOffset.Now - s).TotalSeconds:0} с)" : "";
    }

    private static void RaiseStateChanged(ServerLinkState from, ServerLinkState to) =>
        SafeInvoke(() => StateChanged?.Invoke(from, to), "StateChanged");

    private static void SafeInvoke(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Подписчик (окно, SyncService) не должен ломать сетевой слой.
            PosLogger.Log($"ServerOutageMonitor.{what}: подписчик упал: {ex.Message}", "OUTAGE WARNING");
        }
    }

    private static string Shorten(string? text)
    {
        text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}
