using System.Net;
using System.Net.Http;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// 2026-10-29 (замер скорости синхронизации, п. 7–8): внутренний обработчик клиента NurCRM, под JwtBearerRefreshHandler.
/// • HTTP/2 с откатом: сервер (Cloudflare) отдаёт h2, касса ходила по HTTP/1.1 — каждое параллельное обращение открывало своё TLS-соединение
///   (~170 мс до первого байта). Версия 2.0 с политикой RequestVersionOrLower: нет h2 (прокси, старая система) — тихо HTTP/1.1.
/// • 2026-11-05 (аудит №4 + «Поиск решений», Polly.Core): ТОЛЬКО для GET — срок ожидания заголовков ответа 30 с, один повтор при обрыве/502–504 и «предохранитель»:
///   5 сбоев подряд за 30 с — 15 с запросы GET сразу получают отказ (касса работает из кэша и не ждёт мёртвый сервер). POST/PATCH/PUT/DELETE
///   (деньги, склад) через Polly НЕ идут: ни повтора, ни своего срока (их срок задаёт вызывающий: SlowMoneyCall/ServerAnswerWait и ключи повтора).
///   Проверка здоровья (api/users/profile, корень, вход) мимо предохранителя, чтобы ServerOutageMonitor видел возврат сервера сразу.
/// • Время сервера: заголовок Date успешного ответа питает ServerClock (поправка часов ПК, Л4-06).
/// • Наблюдатель за своими записями: успешный POST/PUT/PATCH/DELETE по товарам (цена, приход, новый товар) — подсказка соседям
///   (SyncCenter.NoteLocalChange): другие кассы, программа владельца и телефоны сразу читают ленту. Оплату чека и прочие запросы не трогает;
///   ошибка наблюдателя запрос не ломает.
/// </summary>
internal sealed class TransportTuningHandler : DelegatingHandler
{
    private static string? _lastLoggedVersion;

    /// <summary>Ключ в Exception.Data: исключение создано отказом предохранителя, а не настоящим обрывом связи.</summary>
    internal const string BreakerMarker = "nm.breaker";

    // 2026-11-05: общий для процесса конвейер Polly для GET (предохранитель общий на всех).
    private static readonly TimeSpan GetTimeout = TimeSpan.FromSeconds(30);
    private static readonly ResiliencePipeline<HttpResponseMessage> GetPipeline = BuildGetPipeline();

    // 2026-11-05 (ревью волн 0–3, п. 2): тяжёлые отчёты (analytics/…, выгрузки) считаются на сервере дольше 30 с — для них свой срок 90 с и конвейер БЕЗ предохранителя:
    // медленный отчёт не должен ни обрываться на 30-й секунде, ни открывать предохранитель, который остановил бы чтение каталога, клиентов и долгов.
    // (Общий предел HttpClient.Timeout — 55 с — остаётся, он срабатывает раньше; здесь только снят лишний срез на 30 с.)
    internal static readonly TimeSpan HeavyGetTimeout = TimeSpan.FromSeconds(90);
    private static readonly ResiliencePipeline<HttpResponseMessage> HeavyGetPipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>().AddTimeout(HeavyGetTimeout).Build();
    private static readonly ResiliencePropertyKey<string> PathKey = new("nm.path");

    /// <summary>Тяжёлый отчёт сервера: analytics/… (P&amp;L, рынок, денежный поток) и выгрузки.</summary>
    internal static bool IsHeavyReport(string? absolutePath)
    {
        var path = absolutePath?.Trim('/') ?? "";
        return path.Contains("/analytics/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("analytics/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/export", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGatewayDown(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static ResiliencePipeline<HttpResponseMessage> BuildGetPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(400),
                BackoffType = DelayBackoffType.Constant,
                // Таймаут не повторяем: второй 30-секундный заход только удлинил бы ожидание.
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(IsGatewayDown),
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 1.0,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                // 2026-11-05 (ревью п. 2): таймаут считается сбоем сервера, только если по сроку не ответили минимум два РАЗНЫХ пути (TimeoutPathTracker);
                // один медленный путь — это медленный запрос, а не мёртвый сервер.
                ShouldHandle = args => new ValueTask<bool>(
                    args.Outcome.Exception switch
                    {
                        HttpRequestException => true,
                        TimeoutRejectedException => TimeoutPathTracker.CountsAsServerFailure(args.Context.Properties.GetValue(PathKey, ""), DateTime.UtcNow),
                        null => args.Outcome.Result is { } r && IsGatewayDown(r),
                        _ => false,
                    }),
                OnOpened = _ =>
                {
                    PosLogger.Log("SYNC: сервер не отвечает на GET 5 попыток подряд (повтор считается отдельной попыткой) — 15 с запросы чтения не отправляются (работа из кэша).", "WARNING");
                    return default;
                },
                OnClosed = _ =>
                {
                    PosLogger.Log("SYNC: чтение с сервера снова идёт.", "SYNC");
                    return default;
                },
            })
            .AddTimeout(GetTimeout)
            .Build();

    /// <summary>Какие GET идут через конвейер Polly. Проверка здоровья и вход — мимо: ServerOutageMonitor должен видеть живой сервер сразу.</summary>
    private static bool UsesGetPipeline(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get)
            return false;
        var path = request.RequestUri?.AbsolutePath?.Trim('/') ?? "";
        return path.Length > 0
               && !path.StartsWith("api/users/profile", StringComparison.OrdinalIgnoreCase)
               && !path.Contains("/auth/", StringComparison.OrdinalIgnoreCase);
    }

    // 2026-10-29 (жалоба «связь периодически отваливается»): на слабом интернете, за «умным» роутером/прокси и в старых Windows одно
    // оборванное HTTP/2-соединение роняет сразу все запросы на нём (а у HTTP/1.1 — только один). Серия обрывов транспорта подряд — касса
    // до перезапуска переходит на HTTP/1.1 (он работает везде); чеки при этом не теряются — очередь досылает их тем же ключом.
    private const int Http2FailureLimit = 3;
    private static readonly TimeSpan Http2FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly object Http2Gate = new();
    private static readonly Queue<DateTime> Http2Failures = new();
    private static volatile bool _http2Disabled;

    /// <summary>true — серия обрывов HTTP/2 уже была, касса работает по HTTP/1.1 до перезапуска.</summary>
    internal static bool Http2Disabled => _http2Disabled;

    /// <summary>Обрыв именно транспорта (протокол, закрытое соединение, ответ оборван), а не отмена или «нет такого имени» (сменой версии не лечится).</summary>
    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: not HttpRequestError.NameResolutionError and not HttpRequestError.UserAuthenticationError };

    private static void NoteHttp2Failure(Exception ex)
    {
        if (_http2Disabled)
            return;
        bool trip;
        lock (Http2Gate)
        {
            var now = DateTime.UtcNow;
            Http2Failures.Enqueue(now);
            while (Http2Failures.Count > 0 && now - Http2Failures.Peek() > Http2FailureWindow)
                Http2Failures.Dequeue();
            trip = Http2Failures.Count >= Http2FailureLimit;
        }

        if (trip)
        {
            _http2Disabled = true;
            PosLogger.Log($"SYNC: {Http2FailureLimit} обрыва HTTP/2 за {Http2FailureWindow.TotalMinutes:0} мин ({ex.GetType().Name}: {ex.Message}) — касса переходит на HTTP/1.1 до перезапуска.", "WARNING");
        }
    }

    private async Task<HttpResponseMessage> SendOnceViaTransportAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 2026-11-05 (ревью волн 0–3, п. 21): на повторе после сбоя версия запроса уже 2.0 — раньше второй обрыв HTTP/2 не считался; теперь считаем по версии 2.0,
        // а после отката на HTTP/1.1 возвращаем повтору версию 1.1.
        var viaHttp2 = !_http2Disabled && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower
                       && (request.Version == HttpVersion.Version11 || request.Version == HttpVersion.Version20);
        if (viaHttp2)
            request.Version = HttpVersion.Version20;
        else if (_http2Disabled && request.Version == HttpVersion.Version20 && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower)
            request.Version = HttpVersion.Version11;

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (viaHttp2 && !cancellationToken.IsCancellationRequested && IsTransportFailure(ex))
        {
            NoteHttp2Failure(ex);
            throw;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SyncCenter.CountRequest(); // 2026-10-30: счётчик «запросов в минуту» для журнала (раз в 5 мин)
        var sentUtc = DateTime.UtcNow;

        HttpResponseMessage response;
        if (UsesGetPipeline(request))
        {
            var heavy = IsHeavyReport(request.RequestUri?.AbsolutePath);
            var timeoutShown = heavy ? HeavyGetTimeout : GetTimeout;
            var context = ResilienceContextPool.Shared.Get(cancellationToken);
            try
            {
                context.Properties.Set(PathKey, request.RequestUri?.AbsolutePath ?? "");
                response = await (heavy ? HeavyGetPipeline : GetPipeline).ExecuteAsync(
                    async ctx => await SendOnceViaTransportAsync(request, ctx.CancellationToken).ConfigureAwait(false), context).ConfigureAwait(false);
            }
            catch (BrokenCircuitException ex)
            {
                // Предохранитель открыт: для вызывающего это обычный обрыв связи (HttpRequestException), а не неизвестное исключение.
                var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Сервер недоступен: чтение временно приостановлено (предохранитель).", ex);
                refused.Data[BreakerMarker] = true; // 2026-11-05 (ревью п. 3): JwtBearerRefreshHandler не сообщает такой отказ монитору аварий
                throw refused;
            }
            catch (TimeoutRejectedException ex)
            {
                // Как таймаут HttpClient: TaskCanceledException без отмены вызывающего.
                throw new TaskCanceledException($"Сервер не ответил за {timeoutShown.TotalSeconds:0} с.", new TimeoutException(ex.Message, ex));
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }
        else
        {
            response = await SendOnceViaTransportAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (response.IsSuccessStatusCode)
            ServerClock.Observe(response.Headers.Date, sentUtc, DateTime.UtcNow); // 2026-11-05 (Л4-06)

        try
        {
            // Один раз (и при смене) в журнал: по какому протоколу сервер ответил — видно, работает ли HTTP/2.
            var version = response.Version.ToString();
            if (!string.Equals(Interlocked.Exchange(ref _lastLoggedVersion, version), version, StringComparison.Ordinal))
                PosLogger.Log($"SYNC: транспорт API — HTTP/{version}.", "SYNC");
            if (response.IsSuccessStatusCode && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
                && request.RequestUri?.AbsolutePath is { } path
                && (path.Contains("/products", StringComparison.OrdinalIgnoreCase) || path.Contains("mass-incoming", StringComparison.OrdinalIgnoreCase)))
                SyncCenter.NoteLocalChange("product");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"SYNC: наблюдатель записей не отработал: {ex.Message}", "WARNING");
        }

        return response;
    }
}

/// <summary>2026-11-05 (ревью волн 0–3, п. 2): предохранитель не открываем из-за одного медленного пути. Таймаут GET считается сбоем сервера,
/// только если за окно не ответили минимум два разных пути (медленный отчёт ≠ мёртвый сервер).</summary>
internal static class TimeoutPathTracker
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastTimeoutUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Записать таймаут пути; true — считать его сбоем для предохранителя.</summary>
    internal static bool CountsAsServerFailure(string? path, DateTime nowUtc)
    {
        lock (Gate)
        {
            foreach (var old in LastTimeoutUtc.Where(kv => nowUtc - kv.Value > Window).Select(kv => kv.Key).ToList())
                LastTimeoutUtc.Remove(old);
            LastTimeoutUtc[NormalizePath(path)] = nowUtc;
            return LastTimeoutUtc.Count >= 2;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex IdSegment = new(
        @"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32}|\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>2026-11-06 (аудит №5, Л5-12): путь для учёта таймаутов — id (UUID, 32 hex, число) заменяются на {id}: «pos/sales/{id}» с разными id — ОДИН путь. Раньше при открытии экрана десятки
    /// медленных GET pos/sales/&lt;разные id&gt; считались «разными путями» и открывали предохранитель на 15 с для ВСЕХ чтений (каталог, клиенты, долги).</summary>
    internal static string NormalizePath(string? path)
    {
        var trimmed = (path ?? "").Trim('/');
        if (trimmed.Length == 0)
            return "";
        var parts = trimmed.Split('/');
        for (var i = 0; i < parts.Length; i++)
            if (IdSegment.IsMatch(parts[i]))
                parts[i] = "{id}";
        return string.Join('/', parts);
    }

    internal static void Reset()
    {
        lock (Gate)
            LastTimeoutUtc.Clear();
    }
}
