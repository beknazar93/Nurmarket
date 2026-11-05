using System.Net;
using System.Net.Http;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит скорости, Н-19): общая фабрика HTTP-клиентов для ВНЕШНИХ сервисов (Gemini, Groq, GitHub, Open Food Facts,
/// DuckDuckGo, Telegram). Раньше каждый сервис делал <c>new HttpClient()</c> (иногда на каждый вызов — новое соединение и TLS) без сжатия.
/// Здесь: автораспаковка gzip/deflate/br (в запрос сам идёт Accept-Encoding), соединение живёт не дольше 15 мин
/// (<c>PooledConnectionLifetime</c> — долгоживущий статический клиент замечает смену DNS) и срок, заданный по типу запроса.
/// Клиент NurCRM (NurMarketApiClient, Polly, TransportTuningHandler) сюда НЕ относится — у него своя настройка.</summary>
public static class ExternalHttp
{
    /// <summary>Клиент для внешнего сервиса. Хранить в static readonly поле (не создавать на каждый вызов).</summary>
    public static HttpClient Create(TimeSpan timeout, string? userAgent = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(userAgent))
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    /// <summary>Срок вышел сам (а не отменил вызывающий): HttpClient.Timeout бросает TaskCanceledException/OperationCanceledException
    /// при ещё не отменённом токене. Нужен фильтрам <c>catch ... when</c>: срок источника — это «источник не ответил», а не отмена всего.</summary>
    public static bool IsTimeout(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>Понятная надпись при сроке ожидания внешнего сервиса (вместо голого «The request was canceled…»).</summary>
    public static string TimeoutMessage(string service, int seconds) => Tr.T(
        $"{service} не ответил за {seconds} с — проверьте интернет и повторите позже.",
        $"{service} {seconds} сек ичинде жооп берген жок — интернетти текшерип, кийинчерээк кайталаңыз.",
        $"{service} did not respond within {seconds} s — check your internet and try again later.",
        $"{service} {seconds} sn içinde yanıt vermedi — internetinizi kontrol edip daha sonra tekrar deneyin.",
        $"{service} {seconds} soniyada javob bermadi — internetni tekshirib, keyinroq qayta urinib ko‘ring.");
}
