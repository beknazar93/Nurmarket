using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Авто-refresh JWT при 401: POST api/users/auth/refresh/, сохранение в DPAPI, повтор запроса.
/// </summary>
public sealed class JwtBearerRefreshHandler : DelegatingHandler
{
    private readonly NurMarketApiClient _api;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _refreshSlowUntilTicks;

    public JwtBearerRefreshHandler(NurMarketApiClient api) => _api = api;

    /// <summary>2026-09-29: каждый запрос кассы к NurCRM проходит здесь — поэтому здесь же
    /// ServerOutageMonitor узнаёт о сбоях сервера: 5xx (в том числе 520–524 Cloudflare) и 429,
    /// обрыв соединения/DNS/TLS и долгие таймауты. Ответ 4xx — сервер жив (отказ по существу).
    /// Успешный 2xx отмечает не здесь, а тот, кто разобрал JSON (NurMarketApiClient): ответ 200
    /// с HTML или обрывком JSON — тоже сбой, и отметка «успех» здесь его бы маскировала.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 2026-10-30 (приёмка 1.17.65, «Важно» 2): общий щит от 429 — пока сервер просит паузу, запросы кассы её ждут (фоновые — дольше и по одному,
        // критичные: оплата, смена — первыми). Ожидание до отсчёта времени запроса: отмена здесь сбоем сервера не считается.
        await ApiThrottle.WaitShieldAsync(cancellationToken).ConfigureAwait(false);

        var started = DateTime.UtcNow;
        // 2026-10-04: момент отправки — обрыв/таймаут запроса, ушедшего до последнего ответа сервера,
        // аварией не считается (см. ServerOutageMonitor.ReportTransportFailure).
        var startedAt = DateTimeOffset.Now;
        try
        {
            var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            // 2026-11-13 (ответ Умара, п. 5): 401 session_revoked — логин открыли на другом устройстве; вход этой кассы закрыт, окно «Вход выполнен на другом устройстве» вместо тихого выброса.
            if (status == 401)
                await NoteSessionRevokedAsync(request, response, cancellationToken).ConfigureAwait(false);
            // 2026-11-03 (С-18, аудит «зависание при медленном сервере»): долгий ответ — строка в журнал (метод, путь, секунды; без токенов и тел),
            // чтобы владелец и мы видели «сервер медлил». Быстрые ответы подряд снимают значок «Сервер медлит».
            var elapsed = DateTime.UtcNow - started;
            // 2026-11-05 (аудит скорости, Н-1): счётчик путей — раз в 5 минут 10 самых частых уходят в журнал (раздел API STATS), без query и значений.
            ApiPathStats.Record(request.Method, request.RequestUri?.AbsolutePath, elapsed.TotalMilliseconds);
            if (elapsed >= SlowResponse)
            {
                // 2026-11-05 (аудит скорости, 18.1): рядом с клиентским временем — серверное (x-response-time-ms) и x-request-id, если сервер их отдал.
                PosLogger.Log($"SLOW {request.Method} {request.RequestUri?.AbsolutePath} {elapsed.TotalSeconds:0.0} с{ApiPathStats.FormatServerTimingSuffix(response)}", "API SLOW");
                // 2026-11-03 (ревью Н-6): загрузка файла (фото товара) честно долгая — значок «Сервер медлит» от неё не ставим.
                if (request.Content is not MultipartContent)
                    ServerOutageMonitor.ReportSlow(DescribeRequest(request));
            }
            else
                ServerOutageMonitor.NoteResponseTime(elapsed);
            // Пауза, о которой просит сервер (Retry-After), — её соблюдают массовые загрузки и
            // проверка связи ServerOutageMonitor (раньше запоминалась только в неиспользуемом пути).
            // 429 — сервер жив и просит паузу: это не авария. Иначе поток 429 при загрузке
            // аналитики (программа владельца, тот же пользователь) переводил бы кассу в автономный
            // режим. Оплата, которая упёрлась в 429 после своих повторов, сама объявит аварию.
            // 2026-11-05 (сервер ceba67c): 429 pin_locked (проверка PIN заблокирована на 15 минут) — не «слишком частые запросы»: общую паузу кассы не ставим, продажи не страдают.
            if (status == 429 && request.RequestUri?.AbsolutePath.Contains("pin/verify", StringComparison.OrdinalIgnoreCase) == true)
                ServerOutageMonitor.ReportSuccess(DescribeRequest(request));
            else if (status == 429)
                ApiThrottle.ReportThrottled(response, null);
            else if (ServerOutageMonitor.IsServerFailureStatus(status))
                ServerOutageMonitor.ReportFailure(DescribeRequest(request), $"HTTP {status}");
            else if (status >= 400)
                ServerOutageMonitor.ReportSuccess(DescribeRequest(request));
            return response;
        }
        catch (HttpRequestException ex) when (ex.Data[TransportTuningHandler.BreakerMarker] is true)
        {
            // 2026-11-05 (ревью волн 0–3, п. 3): отказ предохранителя — не настоящий обрыв (запрос на сервер даже не уходил); монитор аварий его не получает,
            // иначе сразу после возврата сервера авария объявлялась бы снова и кнопки офлайна мигали.
            throw;
        }
        catch (HttpRequestException ex)
        {
            ServerOutageMonitor.ReportTransportFailure(DescribeRequest(request), ex, startedAt);
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                    || DateTime.UtcNow - started >= LongRequest)
        {
            // Таймаут HttpClient или вызывающего после долгого ожидания — сервер не отвечает.
            // Быстрая отмена (кассир закрыл окно) сбоем сервера не считается.
            ServerOutageMonitor.ReportTransportFailure(DescribeRequest(request), ex, startedAt);
            throw;
        }
    }

    /// <summary>2026-11-13: тело 401 с кодом session_revoked (reason device_replaced, by_device_name, at) → <see cref="SessionExpiryMonitor.NoteRevoked"/> и потеря входа (ExpireSession: токены стираются,
    /// очередь продаж, смена и корзины не трогаются — как при истёкшем входе). Тело буферизуется, вызывающие читают его как обычно. 401 самого входа (неверный пароль) не смотрим.</summary>
    private async Task NoteSessionRevokedAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase) || response.Content is null)
                return;
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length == 0 || !text.Contains("session_revoked", StringComparison.Ordinal))
                return;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || code.ValueKind != System.Text.Json.JsonValueKind.String
                || !string.Equals(code.GetString(), "session_revoked", StringComparison.Ordinal))
                return;
            static string? Str(System.Text.Json.JsonElement o, string n) =>
                o.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
            SessionExpiryMonitor.NoteRevoked(new SessionExpiryMonitor.RevokedInfo(Str(root, "by_device_name"), Str(root, "at")));
            // Запрос обновления токена: ExpireSession вызовет обычный путь (refreshed == false). Остальные запросы — объявляем потерю входа здесь.
            if (!path.Contains("/auth/refresh", StringComparison.OrdinalIgnoreCase))
                _api.ExpireSession(path);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            // Не JSON или обрыв при чтении тела — это не session_revoked; обычная обработка 401 продолжается.
        }
    }

    /// <summary>Запрос, отменённый после такого ожидания, считается таймаутом сервера.</summary>
    private static readonly TimeSpan LongRequest = TimeSpan.FromSeconds(10);

    /// <summary>2026-11-03 (С-18): ответ дольше этого срока пишется в журнал строкой SLOW.</summary>
    private static readonly TimeSpan SlowResponse = TimeSpan.FromSeconds(5);

    private static string DescribeRequest(HttpRequestMessage request) =>
        $"{request.Method} {request.RequestUri?.AbsolutePath}";

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApiPathNormalizer.ApplyToRequest(request);

        // login / refresh — только транспорт, без Bearer и без повторного refresh при 401
        if (IsAuthEndpoint(request))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        _api.ApplyBearerAuthorization(request);
        // Токен, с которым ушёл запрос: если к моменту входа в гейт он уже другой —
        // обновление сделал параллельный запрос, повторяем сразу с новым токеном.
        var sentWithToken = _api.AccessToken;

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrEmpty(_api.RefreshToken))
            return response;

        response.Dispose();

        // Параллельные запросы (до HttpSlots штук) ДОЖИДАЮТСЯ идущего обновления,
        // а не получают мгновенный синтетический 401.
        // 2026-11-03 (С-04, Сис-4): у ворот обновления токена ждём не дольше RefreshBudget (10 с) — раньше до 55 с, пока медлит сервер. Не дождались —
        // ApiException 408 «отвечает медленно»; сессию не сбрасываем (ExpireSession не зовём).
        using (var gateBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            gateBudget.CancelAfter(NurMarketApiClient.RefreshBudget);
            try
            {
                await _refreshGate.WaitAsync(gateBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw NurMarketApiClient.RefreshSlowException();
            }
        }

        bool refreshed;
        try
        {
            if (!string.IsNullOrEmpty(_api.AccessToken)
                && !string.Equals(_api.AccessToken, sentWithToken, StringComparison.Ordinal))
            {
                // Токен уже обновлён другим потоком — переиспользуем результат.
                refreshed = true;
            }
            else if (string.IsNullOrEmpty(_api.RefreshToken))
            {
                // Сессия уже сброшена неудачным обновлением в другом потоке.
                refreshed = false;
            }
            else
            {
                // 2026-11-03 (С-04): обновление токена только что не уложилось в срок — ждавшие у ворот запросы не начинают своё обновление (иначе 10 с × число очередников),
                // а сразу получают тот же ответ «сервер медленно». Через 10 с пробуем снова.
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _refreshSlowUntilTicks))
                    throw NurMarketApiClient.RefreshSlowException();
                try
                {
                    refreshed = await _api.RefreshAccessAndPersistAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == 408)
                {
                    Interlocked.Exchange(ref _refreshSlowUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
                    throw;
                }

                if (!refreshed)
                    _api.ExpireSession(request.RequestUri?.AbsolutePath ?? "refresh"); // 2026-10-30: не стираем профиль/права, сообщаем один раз
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        if (!refreshed)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };

        // Повтор выполняется уже вне гейта — он не блокирует другие ожидающие запросы.
        using var retryRequest = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ApiPathNormalizer.ApplyToRequest(retryRequest);
        _api.ApplyBearerAuthorization(retryRequest);
        return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAuthEndpoint(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/auth/refresh", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
            clone.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshGate.Dispose();
        base.Dispose(disposing);
    }
}
