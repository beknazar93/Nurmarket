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
///      ▲                                                         │ 10 → 20 → 20 … с (2026-10-04)
///      └──────── успешная проверка или любой успешный ответ ◀─────┘
/// </code>
/// • «Жёсткий» сбой (оплата не прошла из-за сервера после своих повторов) переводит в Outage сразу.
/// • Сбой — ответ 5xx (включая 520–524 Cloudflare), 408, обрыв/отказ соединения, DNS, TLS,
///   таймаут, ответ 2xx, который не разбирается как JSON. 4xx — сервер жив (отказ по существу).
///   429 («слишком часто») сам по себе аварией не считается — паузу соблюдает ApiThrottle; аварию
///   объявит оплата, если и после своих повторов упрётся в 429.
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

    /// <summary>Паузы между проверками связи в аварии: 10 с, дальше — 20 с.
    /// 2026-10-04, живой тест (брандмауэр отрезал кассу на 190 с): при паузах 10 → 20 → 40 → 60 с касса
    /// после возврата сети становилась «Онлайн» только через 44 с (после сбоя на 70 с — через 18 с). Теперь
    /// пауза не больше 20 с: проверка — один лёгкий запрос профиля и идёт только пока объявлена авария;
    /// в «Онлайн» связь проверяет общая проверка (SharedConnectivityCheck), не чаще прежнего.</summary>
    private static readonly TimeSpan[] ProbeDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
    ];

    private static readonly object Sync = new();

    /// <summary>2026-09-29: ответы сервера на запросы ТЕКУЩЕЙ операции (оплаты). Видна только в её
    /// асинхронном потоке — фоновые запросы кассы (каталог, остатки) ответы оплаты не подменяют.</summary>
    private static readonly AsyncLocal<ResponseWatch?> CurrentWatch = new();

    /// <summary>Следит, отвечает ли сервер на запросы одной операции (см. PosCheckoutService: не
    /// больше ~2 с ожидания без ответа сервера). Успешный ответ или отказ 4xx вызывает
    /// <paramref name="onAnswer"/>. Действует до конца асинхронного метода, который его начал.</summary>
    public static ResponseWatch BeginResponseWatch(Action? onAnswer, Action<TimeSpan>? onThrottled = null)
    {
        var watch = new ResponseWatch(onAnswer, onThrottled);
        CurrentWatch.Value = watch;
        return watch;
    }

    /// <summary>2026-10-30 (приёмка 1.17.66, «Важно» 1): сервер ответил 429 на запрос текущей операции — операция узнаёт, сколько ждать (Retry-After),
    /// и продлевает свой срок на эту паузу (оплата, смена): 429 — не «сервер молчит».</summary>
    internal static void NoteThrottled(TimeSpan wait) => CurrentWatch.Value?.Throttled(wait);

    public sealed class ResponseWatch
    {
        private Action? _onAnswer;
        private Action<TimeSpan>? _onThrottled;
        private int _answers;

        internal ResponseWatch(Action? onAnswer, Action<TimeSpan>? onThrottled = null)
        {
            _onAnswer = onAnswer;
            _onThrottled = onThrottled;
        }

        internal void Throttled(TimeSpan wait)
        {
            try
            {
                Volatile.Read(ref _onThrottled)?.Invoke(wait);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Сколько раз сервер ответил в этой операции.</summary>
        public int Answers => Volatile.Read(ref _answers);

        /// <summary>Больше не следить (операция завершена): фоновые задачи, начатые внутри неё,
        /// не должны трогать её таймер.</summary>
        public void Stop()
        {
            Volatile.Write(ref _onAnswer, null);
            Volatile.Write(ref _onThrottled, null);
        }

        internal void Answered()
        {
            Interlocked.Increment(ref _answers);
            try
            {
                Volatile.Read(ref _onAnswer)?.Invoke();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
    private static ServerLinkState _state = ServerLinkState.Online;
    private static int _consecutiveFailures;
    private static DateTimeOffset? _outageSince;
    private static DateTimeOffset? _lastErrorAt;
    private static string? _lastError;
    private static DateTimeOffset? _lastSuccessAt;
    private static CancellationTokenSource? _probeCts;
    private static int _probeAttempt;
    private static DateTimeOffset? _lastRecoveredAt;

    /// <summary>2026-10-04: авария в пределах этого окна после восстановления считается «дребезгом»
    /// (см. StartProbeLoopLocked).</summary>
    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(2);

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
        // 2026-10-30 (приёмка кассы 1.17.65, «Важно» 2): 429 («слишком часто», Request was throttled) — сервер ЖИВ и ответил, просто просит паузу. Это не авария:
        // раньше поток 429 при массовой загрузке владельца включал «Автономно», касса показывала смену закрытой и «Касса: 0,00». Пауза уже запомнена
        // (ApiThrottle), шапка покажет «Сервер занят»; сама операция повторит или поставит чек в очередь без смены состояния связи.
        if (Api.ApiThrottle.LooksThrottled(error))
        {
            CurrentWatch.Value?.Answered();
            return;
        }

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

    /// <summary>2026-10-04, стенд «сбои сервера»: обрыв или таймаут запроса, который УШЁЛ ДО последнего
    /// успешного ответа сервера, о текущем состоянии сервера ничего не говорит. Живой случай стенда:
    /// при «чёрной дыре» фоновые загрузки (каталог, история) висят до таймаута HttpClient (55 с); сервер
    /// за это время ожил, касса вернулась в «Онлайн» и дослала очередь — а потом эти старые запросы
    /// разом отваливались по таймауту и снова объявляли аварию: оплаты на ровном месте уходили в
    /// очередь ещё минимум на 10 с. Такие сбои только пишутся в журнал.</summary>
    public static void ReportTransportFailure(string context, Exception ex, DateTimeOffset requestStartedAt)
    {
        DateTimeOffset? lastSuccess;
        lock (Sync)
            lastSuccess = _lastSuccessAt;
        if (lastSuccess is { } success && requestStartedAt < success)
        {
            PosLogger.Log(
                $"Сбой старого запроса NurCRM ({context}, начат {requestStartedAt:HH:mm:ss}, сервер с тех пор отвечал в {success:HH:mm:ss}): {Describe(ex)} — не считается.",
                "OUTAGE");
            return;
        }

        ReportFailure(context, Describe(ex));
    }

    /// <summary>Успешный ответ сервера (разобранный JSON или отказ 4xx — сервер жив).</summary>
    public static void ReportSuccess(string context)
    {
        CurrentWatch.Value?.Answered();

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
            {
                _outageSince = null;
                _lastRecoveredAt = DateTimeOffset.Now;
            }
        }
        else
        {
            PosLogger.Log($"Сервер NurCRM снова отвечает ({context}) после единичных сбоев.", "OUTAGE");
        }

        RaiseStateChanged(from, ServerLinkState.Online);
        if (from == ServerLinkState.Outage)
            SafeInvoke(() => Recovered?.Invoke(), "Recovered");
    }

    // ── 2026-11-03 (С-02, аудит «зависание при медленном сервере»): «Сервер медлит» — НЕ новое состояние связи ──────────────────────────────────
    // Сервер отвечает, но долго (5–55 с): аварию не объявляем (иначе оплаты уйдут в очередь при живом сервере), только журнал и значок в шапке.
    private static bool _slow;
    private static int _fastAnswers;
    private static DateTime _lastSlowLogUtc;
    private static DateTime _slowSinceUtc;

    /// <summary>Сервер сейчас «медлит» (ответ дольше срока переднего плана). Снимается после <see cref="FastAnswersToClearSlow"/> быстрых ответов подряд.</summary>
    public static bool IsSlow
    {
        get { lock (Sync) return _slow; }
    }

    /// <summary>Сколько быстрых (&lt; 2 с) ответов подряд снимают «Сервер медлит».</summary>
    public const int FastAnswersToClearSlow = 3;

    /// <summary>Сервер медлит — значок шапки меняется ("Сервер медлит"), событие <see cref="SlowChanged"/>.</summary>
    public static event Action<bool>? SlowChanged;

    /// <summary>Передний план не дождался ответа за срок <see cref="ServerAnswerWait.ForegroundBudget"/> (или ответ пришёл дольше 5 с).
    /// Журнал «OUTAGE SLOW» — не чаще раза в 30 с. Аварию НЕ объявляет.</summary>
    public static void ReportSlow(string context)
    {
        bool raise;
        bool log;
        lock (Sync)
        {
            _fastAnswers = 0;
            raise = !_slow;
            if (raise)
            {
                _slow = true;
                _slowSinceUtc = DateTime.UtcNow;
            }

            log = DateTime.UtcNow - _lastSlowLogUtc >= TimeSpan.FromSeconds(30);
            if (log)
                _lastSlowLogUtc = DateTime.UtcNow;
        }

        if (log)
            // 2026-11-03 (аудит №3, Д3-08): раньше строка писала «ответа нет дольше 8 с» и при ответе за 5–7 с — вводило в заблуждение при разборе жалоб.
            PosLogger.Log($"Сервер NurCRM медлит ({context}): ответ медленный (больше 5 с; окна ждут до {ServerAnswerWait.ForegroundBudget.TotalSeconds:0} с) — аварии нет, касса ждёт.", "OUTAGE SLOW");
        if (raise)
            SafeInvoke(() => SlowChanged?.Invoke(true), "SlowChanged");
    }

    /// <summary>Время, за которое пришёл ответ на запрос (вызывает общий обработчик запросов): быстрые ответы подряд снимают «медлит».</summary>
    internal static void NoteResponseTime(TimeSpan elapsed)
    {
        bool clear = false;
        lock (Sync)
        {
            if (!_slow)
                return;
            if (elapsed < TimeSpan.FromSeconds(2))
            {
                if (++_fastAnswers >= FastAnswersToClearSlow)
                {
                    _slow = false;
                    _fastAnswers = 0;
                    clear = true;
                }
            }
            else
                _fastAnswers = 0;
        }

        if (clear)
        {
            PosLogger.Log("Сервер NurCRM снова отвечает быстро.", "OUTAGE SLOW");
            SafeInvoke(() => SlowChanged?.Invoke(false), "SlowChanged");
        }
    }

    /// <summary>Нейтральная надпись для шапки: «Сервер медлит».</summary>
    public static string SlowHeaderText => Tr.T("Сервер медлит", "Сервер жай иштеп жатат", "Server is slow", "Sunucu yavaş", "Server sekin");

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
            ? Tr.T($"Данные на {time:HH:mm}, сервер недоступен", $"Маалымат {time:HH:mm} боюнча, сервер жеткиликсиз",
                $"Data as of {time:HH:mm}, the server is unavailable", $"Veriler {time:HH:mm} itibarıyla, sunucu kullanılamıyor",
                $"Ma'lumotlar {time:HH:mm} holatiga ko'ra, server mavjud emas")
            : Tr.T("Сервер недоступен — показаны данные этой кассы", "Сервер жеткиликсиз — ушул кассанын маалыматтары көрсөтүлдү",
                "The server is unavailable — showing this till's data", "Sunucu kullanılamıyor — bu kasanın verileri gösteriliyor",
                "Server mavjud emas — shu kassaning ma'lumotlari ko'rsatildi");
    }

    /// <summary>2026-11-01 (В-08, аудит «10 из 10»): ОДНА фраза для сбоя сервера во всей программе владельца вместо трёх
    /// («Нет связи с сервером», «Сервер не отвечает», «сервер недоступен»). «Нет связи» при ответе 5xx на работающем интернете
    /// заставляло владельца проверять свой роутер.</summary>
    public static string UnavailableText => Tr.T("Сервер NurCRM недоступен", "NurCRM сервери жеткиликсиз", "The NurCRM server is unavailable",
        "NurCRM sunucusu kullanılamıyor", "NurCRM serveri mavjud emas");

    /// <summary>2026-11-01 (В-28, аудит «10 из 10»): понятный текст ошибки для экрана вместо сырого <c>ex.Message</c>
    /// («An error occurred while sending the request.», «mock 500»). Сбой сервера или сети — <see cref="UnavailableText"/> и совет;
    /// отказ сервера 4xx — его текст (он по-русски и по делу); прочее — общая фраза (подробности остаются в журнале).</summary>
    public static string FriendlyError(Exception? ex, bool showServerText = false)
    {
        // 2026-11-03 (С-01/С-15): сервер не ответил за срок переднего плана — «отвечает медленно» (а не «недоступен»), совет — повторить позже.
        if (ex is ServerNotAnsweringException { Throttled: false })
            return ServerAnswerWait.SlowMessage;
        if (ex is ServerNotAnsweringException)
            return ServerAnswerWait.ThrottledMessage;
        // 2026-11-05 (сервер ceba67c): известные коды ошибок (duplicate_line, has_returns, read_only, pin_locked…) — понятный текст на языке кассы вместо слов сервера.
        if (ex is ApiException && ServerErrorCodes.Friendly(ex) is { } known)
            return known;
        if (ex is null || IsNetworkOrServer(ex))
            return UnavailableText + ". " + Tr.T("Проверьте интернет и нажмите «Обновить».", "Интернетти текшериңиз да «Жаңыртуу» басыңыз.",
                "Check the internet and press “Refresh”.", "İnterneti kontrol edip «Yenile»ye basın.", "Internetni tekshirib, «Yangilash»ni bosing.");
        // 2026-11-02 (аудит №2, В3-03): застрявший принтер, диск и отмена операции — не «сервер недоступен».
        if (ex is OperationCanceledException)
            return Tr.T("Операция прервана.", "Операция токтотулду.", "The operation was interrupted.", "İşlem kesildi.", "Amal to'xtatildi.");
        if (ex is System.IO.IOException or UnauthorizedAccessException)
            return Tr.T("Не удалось выполнить операцию с устройством или файлом. Проверьте принтер, соединение с ним и свободное место на диске.",
                "Түзмөк же файл менен иштөө мүмкүн болгон жок. Принтерди, анын байланышын жана дисктеги бош ордун текшериңиз.",
                "The operation with the device or file failed. Check the printer, its connection and the free disk space.",
                "Cihaz veya dosya işlemi başarısız oldu. Yazıcıyı, bağlantısını ve boş disk alanını kontrol edin.",
                "Qurilma yoki fayl bilan amal bajarilmadi. Printerni, ulanishni va diskdagi bo'sh joyni tekshiring.");
        if (ex is ApiException { StatusCode: 401 })
            return Tr.T("Сессия истекла — войдите заново.", "Сессия бүттү — кайра кириңиз.", "The session expired — sign in again.",
                "Oturum süresi doldu — yeniden giriş yapın.", "Sessiya tugadi — qayta kiring.");
        if (ex is ApiException { StatusCode: 403 })
            return Tr.T("Нет права на это действие. Обратитесь к владельцу.", "Бул аракетке укук жок. Ээсине кайрылыңыз.",
                "You have no permission for this action. Ask the owner.", "Bu işlem için yetkiniz yok. Sahibine başvurun.", "Bu amalga ruxsat yo'q. Egasiga murojaat qiling.");
        if (ex is ApiException api && !string.IsNullOrWhiteSpace(api.Message) && api.Message.Length <= 200 && (showServerText || !LooksTechnical(api.Message)))
            return api.Message.Trim();
        return Tr.T("Не удалось выполнить запрос. Повторите позже.", "Суроону аткаруу мүмкүн болгон жок. Кийинчерээк кайталаңыз.",
            "The request failed. Try again later.", "İstek gerçekleştirilemedi. Daha sonra tekrar deneyin.", "So'rovni bajarib bo'lmadi. Keyinroq qayta urinib ko'ring.");
    }

    /// <summary>2026-11-12 (оценка UX/UI, Д-2): сбой именно «сервер/сеть недоступны» (а не отказ 4xx, не медленный ответ, не принтер и не отмена) — для блока состояния:
    /// такой сбой показывается карточкой «Сервер NurCRM недоступен», остальные — карточкой «Не удалось …» с понятной причиной.</summary>
    public static bool IsServerUnavailable(Exception? ex) =>
        ex is not ServerNotAnsweringException
        && !(ex is ApiException && ServerErrorCodes.Friendly(ex) is not null)
        && (ex is null || IsNetworkOrServer(ex));

    /// <summary>2026-11-02 (В3-03): сбой именно сети/сервера (а не принтера, диска или отмены пользователем). IOException считается сетевым только внутри HttpRequestException.</summary>
    private static bool IsNetworkOrServer(Exception ex) => ex switch
    {
        ApiException api => api.StatusCode is null || IsServerFailureStatus(api.StatusCode),
        HttpRequestException or TimeoutException or System.Net.Sockets.SocketException or JsonException => true,
        OperationCanceledException oce => oce is TaskCanceledException || oce.InnerException is TimeoutException,
        _ => ex.InnerException is not null && IsNetworkOrServer(ex.InnerException),
    };

    /// <summary>Сырой технический текст (английский .NET, имена исключений, «mock 500») на экран не выводим.</summary>
    private static bool LooksTechnical(string message) =>
        message.Contains("An error occurred", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Exception", StringComparison.Ordinal)
        || message.Contains("mock ", StringComparison.OrdinalIgnoreCase)
        || (message.All(c => c < 128) && message.Any(char.IsLetter)); // чисто английский текст — тоже сырой

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
            _lastRecoveredAt = null;
            _probeAttempt = 0;
            _slow = false;
            _fastAnswers = 0;
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
        // 2026-10-04: авария снова через минуты после восстановления («дребезг»: проверка профиля
        // проходит, а продажа/досылка снова падает) — паузы проверки продолжают расти (10 → 20 с;
        // 2026-10-04: потолок 20 с, см. ProbeDelays), а не начинаются заново с 10 с: иначе касса каждые
        // ~10 с на миг становилась «Онлайн» и очередной чек снова ждал сервер.
        var flapping = _lastRecoveredAt is { } recovered && DateTimeOffset.Now - recovered < FlapWindow;
        if (!flapping)
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
