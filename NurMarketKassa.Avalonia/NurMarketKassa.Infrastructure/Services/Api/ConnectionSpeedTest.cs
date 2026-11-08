using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

public enum SpeedTestStatus { Ok, RateLimited, NotSignedIn, Failed }

/// <summary>Вывод по замеру: хватит ли связи кассе (продажа — пара килобайт, каталог и фото — единицы мегабайт).</summary>
public enum SpeedVerdict { Unknown, Good, Weak }

/// <summary>2026-11-08 (ТЗ п. 34, Б-51): итог замера. Любое поле замера может быть null — замер остановился раньше (429, обрыв), тогда видно, что успели.
/// <see cref="FailedStep"/> — на каком шаге остановились (ping / down / up / ip), <see cref="HttpStatus"/> — ответ сервера на нём.</summary>
public sealed record SpeedTestResult(
    SpeedTestStatus Status,
    double? LatencyMs = null,
    double? DownMbps = null,
    double? UpMbps = null,
    string? Ip = null,
    int RetryAfterSeconds = 0,
    string? FailedStep = null,
    int? HttpStatus = null)
{
    /// <summary>Порог «хватит для кассы»: задержка не больше 800 мс, скачивание от 1 Мбит/с, отдача от 0,5 Мбит/с.
    /// Чек — килобайты, каталог и картинки — мегабайты раз в сессию; ниже порога касса работает, но тормозит.</summary>
    public const double MaxGoodLatencyMs = 800, MinGoodDownMbps = 1.0, MinGoodUpMbps = 0.5;

    public SpeedVerdict Verdict
    {
        get
        {
            if (LatencyMs is null && DownMbps is null && UpMbps is null)
                return SpeedVerdict.Unknown;
            if (LatencyMs is > MaxGoodLatencyMs || DownMbps is < MinGoodDownMbps || UpMbps is < MinGoodUpMbps)
                return SpeedVerdict.Weak;
            // Хорошо — только когда есть все три цифры; частичный замер (429 на скачивании) вывода «хватит» не даёт.
            return LatencyMs is not null && DownMbps is not null && UpMbps is not null ? SpeedVerdict.Good : SpeedVerdict.Unknown;
        }
    }
}

/// <summary>2026-11-08 (ТЗ п. 34, ответ Умара 08.11): замер скорости связи с сервером NurCRM — GET speed/ping (204), GET speed/ip, GET speed/down?bytes=N,
/// POST speed/up. Только для вошедшего сотрудника (Bearer). Пути БЕЗ «/» на конце (сервер так требует). Лимит сервера: down и up — 1 замер в минуту и 30 в сутки с IP,
/// ping и ip — 120 в минуту; при превышении 429 + Retry-After — повторно не долбим: показываем «повторите через N с».
///
/// Свой HttpClient (не общий NurMarketApiClient): замер мерит именно канал, а не очередь общих запросов; ответ 429 здесь не должен включать общую паузу «Сервер занят»
/// всей кассы (ApiThrottle), а скачивание 5 МБ на слабой связи не должно упираться в 30-секундный срок обычных GET.</summary>
public sealed class ConnectionSpeedTest
{
    public const int DefaultDownBytes = 5 * 1024 * 1024, DefaultUpBytes = 2 * 1024 * 1024;
    private const int PingCount = 4; // первый — прогрев соединения (TLS), в итог не идёт
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = HttpTimeout };

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly Action<HttpRequestMessage> _authorize;
    private readonly object _sync = new();
    private DateTime _nextHeavyUtc = DateTime.MinValue;
    private int _running;

    public ConnectionSpeedTest(NurMarketApiClient api) : this(SharedHttp, api.BaseAddress, api.ApplyBearerAuthorization) { }

    internal ConnectionSpeedTest(HttpClient http, Uri baseAddress, Action<HttpRequestMessage> authorize)
    {
        _http = http;
        _baseAddress = baseAddress;
        _authorize = authorize;
    }

    /// <summary>Сколько ещё ждать до следующего замера (сервер пускает down/up раз в минуту) — кнопка не отправляет запрос зря.</summary>
    public int SecondsUntilNext
    {
        get
        {
            lock (_sync)
            {
                var left = _nextHeavyUtc - DateTime.UtcNow;
                return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : 0;
            }
        }
    }

    /// <summary>Не пускать down/up ещё <paramref name="span"/>. Ответ 429 заменяет прежнюю отметку (сервер сам назвал срок в Retry-After), поэтому не «максимум».</summary>
    private void HoldHeavy(TimeSpan span)
    {
        lock (_sync)
            _nextHeavyUtc = DateTime.UtcNow + span;
    }

    /// <summary>Полный замер. <paramref name="step"/> получает короткий код шага (ping / ip / down / up) — для подписи «Проверяю…».</summary>
    public async Task<SpeedTestResult> RunAsync(int downBytes = DefaultDownBytes, int upBytes = DefaultUpBytes, Action<string>? step = null, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new SpeedTestResult(SpeedTestStatus.Failed, FailedStep: "busy");
        try
        {
            var wait = SecondsUntilNext;
            if (wait > 0)
                return new SpeedTestResult(SpeedTestStatus.RateLimited, RetryAfterSeconds: wait, FailedStep: "local");

            double? latency = null, down = null, up = null;
            string? ip = null;

            SpeedTestResult Stop(SpeedTestStatus status, string failedStep, int? http = null, int retry = 0) =>
                new(status, latency, down, up, ip, retry, failedStep, http);

            // 1. Задержка.
            step?.Invoke("ping");
            var times = new List<double>();
            for (var i = 0; i < PingCount; i++)
            {
                var sw = Stopwatch.StartNew();
                var (resp, fail) = await SendAsync(HttpMethod.Get, "api/speed/ping", null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                using (resp)
                {
                    sw.Stop();
                    if (resp is null)
                        return Stop(SpeedTestStatus.Failed, "ping");
                    if (fail is not null)
                        return Stop(fail.Value.Status, "ping", (int)resp.StatusCode, fail.Value.RetryAfter);
                    if (i > 0)
                        times.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            times.Sort();
            latency = Math.Round(times[times.Count / 2], 0);

            // 2. Внешний адрес (не критично — если не ответил, замер идёт дальше).
            step?.Invoke("ip");
            {
                var (resp, fail) = await SendAsync(HttpMethod.Get, "api/speed/ip", null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                using (resp)
                {
                    if (resp is not null && fail is null)
                        ip = ReadJsonString(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "ip");
                    else if (fail is { Status: SpeedTestStatus.NotSignedIn })
                        return Stop(SpeedTestStatus.NotSignedIn, "ip", (int?)resp?.StatusCode);
                }
            }

            // 3. Скачивание. Сервер пускает один замер в минуту — отметка ставится до запроса: даже оборванный замер занимает «окно» сервера.
            step?.Invoke("down");
            HoldHeavy(TimeSpan.FromSeconds(60));
            {
                var sw = Stopwatch.StartNew();
                var (resp, fail) = await SendAsync(HttpMethod.Get, "api/speed/down?bytes=" + downBytes.ToString(CultureInfo.InvariantCulture), null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                using (resp)
                {
                    if (resp is null)
                        return Stop(SpeedTestStatus.Failed, "down");
                    if (fail is not null)
                    {
                        if (fail.Value.Status == SpeedTestStatus.RateLimited)
                            HoldHeavy(TimeSpan.FromSeconds(fail.Value.RetryAfter));
                        return Stop(fail.Value.Status, "down", (int)resp.StatusCode, fail.Value.RetryAfter);
                    }

                    long got = 0;
                    try
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        var buffer = new byte[64 * 1024];
                        int n;
                        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            got += n;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        return Stop(SpeedTestStatus.Failed, "down");
                    }

                    sw.Stop();
                    if (got <= 0)
                        return Stop(SpeedTestStatus.Failed, "down", (int)resp.StatusCode);
                    down = Mbps(got, sw.Elapsed);
                }
            }

            // 4. Отдача.
            step?.Invoke("up");
            {
                var payload = new byte[upBytes];
                RandomNumberGenerator.Fill(payload);
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var sw = Stopwatch.StartNew();
                var (resp, fail) = await SendAsync(HttpMethod.Post, "api/speed/up", content, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                using (resp)
                {
                    sw.Stop();
                    if (resp is null)
                        return Stop(SpeedTestStatus.Failed, "up");
                    if (fail is not null)
                    {
                        if (fail.Value.Status == SpeedTestStatus.RateLimited)
                            HoldHeavy(TimeSpan.FromSeconds(fail.Value.RetryAfter));
                        return Stop(fail.Value.Status, "up", (int)resp.StatusCode, fail.Value.RetryAfter);
                    }

                    // Сервер отвечает {"received": N}; меряем по тому, что он действительно принял (если не прислал — по отправленному).
                    var received = ReadJsonLong(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "received") ?? upBytes;
                    if (received <= 0)
                        return Stop(SpeedTestStatus.Failed, "up", (int)resp.StatusCode);
                    up = Mbps(received, sw.Elapsed);
                }
            }

            return new SpeedTestResult(SpeedTestStatus.Ok, latency, down, up, ip);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private readonly record struct Fail(SpeedTestStatus Status, int RetryAfter);

    /// <summary>Один запрос. resp == null — обрыв связи/срок; fail != null — ответ сервера не 2xx (401, 429 с Retry-After, остальное — Failed).</summary>
    private async Task<(HttpResponseMessage? Resp, Fail? Fail)> SendAsync(HttpMethod method, string relativePath, HttpContent? content, HttpCompletionOption option, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, new Uri(_baseAddress, relativePath)) { Content = content };
        _authorize(req);
        if (req.Headers.Authorization is null)
        {
            req.Dispose();
            // Нет входа: ответ сочиняем сами, чтобы не слать запрос без Bearer.
            return (new HttpResponseMessage(HttpStatusCode.Unauthorized), new Fail(SpeedTestStatus.NotSignedIn, 0));
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, option, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Замер скорости: {method} {relativePath} — {ex.GetType().Name}: {ex.Message}", "WARNING");
            return (null, null);
        }
        finally
        {
            req.Dispose();
        }

        if (resp.IsSuccessStatusCode)
            return (resp, null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return (resp, new Fail(SpeedTestStatus.NotSignedIn, 0));
        if ((int)resp.StatusCode == 429)
            return (resp, new Fail(SpeedTestStatus.RateLimited, RetryAfterSeconds(resp)));
        return (resp, new Fail(SpeedTestStatus.Failed, 0));
    }

    /// <summary>Retry-After — секунды или дата HTTP; нет заголовка — минута (окно сервера для down/up).</summary>
    internal static int RetryAfterSeconds(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        var span = ra?.Delta ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var seconds = span is { } s ? (int)Math.Ceiling(s.TotalSeconds) : 60;
        return Math.Clamp(seconds, 1, 24 * 3600);
    }

    internal static double Mbps(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return Math.Round(bytes * 8.0 / seconds / 1_000_000.0, 2);
    }

    private static string? ReadJsonString(string? text, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(text ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadJsonLong(string? text, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(text ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
