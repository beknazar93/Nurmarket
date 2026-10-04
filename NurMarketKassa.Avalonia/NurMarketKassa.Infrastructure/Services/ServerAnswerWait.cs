using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-04, отчёт «офлайн и сбои сервера», раздел «Не сделано»: окна возврата и оплаты
/// долга, открытые в первые секунды после того, как сервер замолчал (касса ещё «Онлайн»), грузили
/// список до 55 с (таймаут HttpClient) — кассир смотрел на пустое окно почти минуту. Сервер не ответил
/// на первую порцию данных за <see cref="FirstPortionBudget"/> — это сбой сервера, а не «медленный
/// список»: объявляется авария (дальше касса не ждёт сеть ни в оплате, ни в других окнах), окно сразу
/// пишет, что делать.
///
/// Окно отсчитывается заново после КАЖДОГО ответа сервера (как у оплаты и смены — см.
/// ServerOutageMonitor.BeginResponseWatch): живой медленный сервер, отвечающий за 3–4 с на запрос,
/// список загрузит; ложных аварий от него нет. Продолжение длинных списков (следующие страницы)
/// окна грузят фоном — его этот класс не ограничивает.</summary>
public static class ServerAnswerWait
{
    /// <summary>Сколько окно ждёт ответа сервера на первую порцию данных (с последнего ответа).
    /// Живой NurCRM отвечает на список продаж/клиентов за 0,2–1 с.</summary>
    public static readonly TimeSpan FirstPortionBudget = TimeSpan.FromSeconds(5);

    /// <summary>Выполняет <paramref name="operation"/>, ожидая сервер не дольше <paramref name="budget"/>
    /// с последнего его ответа. Не дождались — <see cref="ServerNotAnsweringException"/>.</summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        var startedUtc = DateTime.UtcNow;
        using var serverWait = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverWait.Token);
        var watch = ServerOutageMonitor.BeginResponseWatch(() => serverWait.CancelAfter(budget));
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (serverWait.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                                   && ex is not ServerNotAnsweringException)
        {
            // Во время ожидания сервер ответил 429 (а запрос ждал паузу) — он жив, просто просит подождать.
            throw new ServerNotAnsweringException(budget, throttled: ThrottledSince(startedUtc), ex);
        }
        finally
        {
            watch.Stop();
        }
    }

    /// <summary>Первая порция данных окна (возврат, оплата долга): <see cref="RunAsync{T}"/> с
    /// <see cref="FirstPortionBudget"/>. Сервер не ответил вовремя или ответил сбоем (5xx, обрыв,
    /// HTML вместо JSON) — авария объявляется сразу (<paramref name="context"/> — для журнала) и
    /// бросается <see cref="ServerNotAnsweringException"/>. Отказ по существу (4xx) пробрасывается
    /// как есть. 429 («слишком часто») — сервер жив: аварии нет, но и ждать окно не будет.</summary>
    public static async Task<T> FirstPortionAsync<T>(
        string context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var startedUtc = DateTime.UtcNow;
        try
        {
            return await RunAsync(operation, FirstPortionBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerNotAnsweringException ex)
        {
            if (!ex.Throttled)
                ServerOutageMonitor.ReportFailure(context, ex.InnerException ?? ex, hard: true);
            PosLogger.Log(
                $"{context}: сервер не ответил за {FirstPortionBudget.TotalSeconds:0} с{(ex.Throttled ? " (просит паузу, 429)" : " — объявлена авария")}.",
                "OUTAGE");
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is not ServerNotAnsweringException
                                   && ServerOutageMonitor.IsServerFailure(ex))
        {
            var throttled = ex is ApiException { StatusCode: 429 } || ThrottledSince(startedUtc);
            if (!throttled)
                ServerOutageMonitor.ReportFailure(context, ex, hard: true);
            PosLogger.Log($"{context}: сбой сервера ({ServerOutageMonitor.Describe(ex)}){(throttled ? "" : " — объявлена авария")}.", "OUTAGE");
            throw new ServerNotAnsweringException(FirstPortionBudget, throttled, ex);
        }
    }

    /// <summary>Сервер ответил 429 после <paramref name="startedUtc"/> или ещё просит паузу.</summary>
    private static bool ThrottledSince(DateTime startedUtc) =>
        ApiThrottle.LastThrottledUtc >= startedUtc || ApiThrottle.RemainingBlock > TimeSpan.Zero;

    /// <summary>Сервер просит паузу (429) — текст для окна вместо «сервер не отвечает».</summary>
    public static string ThrottledMessage => Tr.T(
        "Сервер NurCRM просит подождать несколько секунд. Повторите чуть позже.",
        "NurCRM сервери бир нече секунд күтүүнү суранып жатат. Бир аздан кийин кайталаңыз.",
        "The NurCRM server asks to wait a few seconds. Try again shortly.",
        "NurCRM sunucusu birkaç saniye beklemenizi istiyor. Biraz sonra tekrar deneyin.",
        "NurCRM serveri bir necha soniya kutishni so'rayapti. Birozdan keyin qayta urinib ko'ring.");
}

/// <summary>2026-10-04: сервер не ответил за отведённое окну время или ответил сбоем (см.
/// <see cref="ServerAnswerWait"/>). <see cref="Throttled"/> — сервер жив, но просит паузу (429).</summary>
public sealed class ServerNotAnsweringException : Exception
{
    public ServerNotAnsweringException(TimeSpan budget, bool throttled, Exception? inner)
        : base($"Сервер NurCRM не ответил за {budget.TotalSeconds:0} с.", inner)
    {
        Budget = budget;
        Throttled = throttled;
    }

    public TimeSpan Budget { get; }

    public bool Throttled { get; }
}
