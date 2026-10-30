using System.Net;
using System.Net.Http;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// 2026-10-29 (замер скорости синхронизации, п. 7–8): внутренний обработчик клиента NurCRM, под JwtBearerRefreshHandler.
/// • HTTP/2 с откатом: сервер (Cloudflare) отдаёт h2, касса ходила по HTTP/1.1 — каждое параллельное обращение открывало своё TLS-соединение
///   (~170 мс до первого байта). Версия 2.0 с политикой RequestVersionOrLower: нет h2 (прокси, старая система) — тихо HTTP/1.1.
/// • Наблюдатель за своими записями: успешный POST/PUT/PATCH/DELETE по товарам (цена, приход, новый товар) — подсказка соседям
///   (SyncCenter.NoteLocalChange): другие кассы, программа владельца и телефоны сразу читают ленту. Оплату чека и прочие запросы не трогает;
///   ошибка наблюдателя запрос не ломает.
/// </summary>
internal sealed class TransportTuningHandler : DelegatingHandler
{
    private static string? _lastLoggedVersion;

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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SyncCenter.CountRequest(); // 2026-10-30: счётчик «запросов в минуту» для журнала (раз в 5 мин)
        var viaHttp2 = !_http2Disabled && request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower;
        if (viaHttp2)
            request.Version = HttpVersion.Version20;

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (viaHttp2 && !cancellationToken.IsCancellationRequested && IsTransportFailure(ex))
        {
            NoteHttp2Failure(ex);
            throw;
        }

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
