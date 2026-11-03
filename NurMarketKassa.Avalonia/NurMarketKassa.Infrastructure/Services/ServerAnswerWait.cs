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

    // ── 2026-11-03 (С-01, аудит «зависание при медленном сервере», жалоба владельца «если сервер зависает, то зависает и наша программа») ──────────────
    // Раньше окно ждало медленный сервер до 55 с (срок HttpClient), а цепочка из 2–3 вызовов — до 165 с, без надписи и без «Отмены». Теперь любой серверный вызов
    // переднего плана — через ForegroundAsync (чтение) или ForegroundWriteAsync (запись, деньги): не дольше 8 с с последнего ответа сервера.

    /// <summary>Сколько передний план (окно, кнопка) ждёт ответа сервера с последнего его ответа (аудит 03.11.2026: «не дольше 8 с без ответа»).</summary>
    public static readonly TimeSpan ForegroundBudget = TimeSpan.FromSeconds(8);

    /// <summary>Через сколько без ответа окну показывается «Ждём ответ сервера NurCRM… N с» и «Отменить ожидание».</summary>
    public static readonly TimeSpan SlowNoticeAfter = TimeSpan.FromSeconds(3);

    /// <summary>Серверный вызов переднего плана (чтение): <paramref name="operation"/> с запросами в «быстрой полосе» очереди
    /// (<see cref="NurMarketApiClient.Foreground"/>), срок <see cref="ForegroundBudget"/> с последнего ответа сервера. Не дождались — <see cref="ServerNotAnsweringException"/>
    /// (аварию НЕ объявляем: «медленно», а не «недоступен»; только <see cref="ServerOutageMonitor.ReportSlow"/>). Отмена пользователем — <see cref="OperationCanceledException"/>,
    /// сбоем сервера не считается. <paramref name="onSlow"/>(true) — через <see cref="SlowNoticeAfter"/> без ответа, (false) — ответ пришёл/вызов закончен; вызывается НЕ из UI-потока.
    /// Для денег и записей — <see cref="ForegroundWriteAsync{T}"/> (запрос по сроку не отменяется).</summary>
    public static async Task<T> ForegroundAsync<T>(
        string context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken userCancel = default,
        Action<bool>? onSlow = null)
    {
        var startedUtc = DateTime.UtcNow;
        using var serverWait = new CancellationTokenSource(ForegroundBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancel, serverWait.Token);
        using var notice = new SlowNotice(onSlow);
        using var scope = NurMarketApiClient.Foreground();
        var watch = ServerOutageMonitor.BeginResponseWatch(
            () =>
            {
                serverWait.CancelAfter(ForegroundBudget);
                notice.Answered();
            },
            wait => serverWait.CancelAfter(ForegroundBudget + wait));
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (serverWait.IsCancellationRequested && !userCancel.IsCancellationRequested
                                   && ex is not ServerNotAnsweringException)
        {
            var throttled = ThrottledSince(startedUtc);
            if (!throttled)
                ServerOutageMonitor.ReportSlow(context);
            PosLogger.Log($"{context}: сервер не ответил за {ForegroundBudget.TotalSeconds:0} с — окно освобождено{(throttled ? " (просит паузу, 429)" : "")}.", "OUTAGE SLOW");
            throw new ServerNotAnsweringException(ForegroundBudget, throttled, ex);
        }
        finally
        {
            watch.Stop();
        }
    }

    /// <summary>Версия без значения.</summary>
    public static Task ForegroundAsync(
        string context,
        Func<CancellationToken, Task> operation,
        CancellationToken userCancel = default,
        Action<bool>? onSlow = null) =>
        ForegroundAsync<bool>(context, async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }, userCancel, onSlow);

    /// <summary>Серверный вызов переднего плана для ЗАПИСИ и ДЕНЕГ (возврат, долг, обмен, зарплата, прокат, сохранение карточки). Запрос по сроку НЕ отменяется
    /// (сервер мог его провести): через <see cref="ForegroundBudget"/> без ответа окно получает <see cref="ForegroundResult{T}.Pending"/> = true и
    /// <see cref="ForegroundResult{T}.Completion"/> — ту же задачу; окно пишет «операция продолжается» и разрешает закрыться, а итог (плашка, обновление шапки)
    /// выводит продолжение этой задачи. Запрос уходит с тем же ключом идемпотентности, что и всегда — второй отправки нет. Ошибка быстрого запроса пробрасывается как обычно.</summary>
    public static async Task<ForegroundResult<T>> ForegroundWriteAsync<T>(
        string context,
        Func<CancellationToken, Task<T>> operation,
        Action<bool>? onSlow = null)
    {
        var slowSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new Timer(_ => slowSignal.TrySetResult(true), null, ForegroundBudget, Timeout.InfiniteTimeSpan);
        using var notice = new SlowNotice(onSlow);
        var startedUtc = DateTime.UtcNow;
        var watch = ServerOutageMonitor.BeginResponseWatch(
            () =>
            {
                try { timer.Change(ForegroundBudget, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                notice.Answered();
            },
            wait =>
            {
                try { timer.Change(ForegroundBudget + wait, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            });
        try
        {
            Task<T> task;
            using (NurMarketApiClient.Foreground())
                task = operation(CancellationToken.None);

            var first = await Task.WhenAny(task, slowSignal.Task).ConfigureAwait(false);
            if (ReferenceEquals(first, task))
                return new ForegroundResult<T>(false, await task.ConfigureAwait(false), task);

            if (!ThrottledSince(startedUtc))
                ServerOutageMonitor.ReportSlow(context);
            PosLogger.Log($"{context}: сервер не ответил за {ForegroundBudget.TotalSeconds:0} с — запись не отменена, окно освобождено, итог придёт фоном.", "OUTAGE SLOW");
            // Исключение продолжения наблюдает вызывающий окна; здесь только не даём ему стать «необработанным».
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new ForegroundResult<T>(true, default, task);
        }
        finally
        {
            watch.Stop();
        }
    }

    /// <summary>Таймер надписи «Ждём ответ сервера…»: срабатывает через <see cref="SlowNoticeAfter"/> без ответа, сбрасывается каждым ответом сервера.</summary>
    private sealed class SlowNotice : IDisposable
    {
        private readonly Action<bool>? _callback;
        private readonly Timer? _timer;
        private readonly object _sync = new();
        private bool _shown;
        private bool _disposed;

        public SlowNotice(Action<bool>? callback)
        {
            _callback = callback;
            if (callback is not null)
                _timer = new Timer(_ => Fire(), null, SlowNoticeAfter, Timeout.InfiniteTimeSpan);
        }

        private void Fire()
        {
            lock (_sync)
            {
                if (_disposed || _shown)
                    return;
                _shown = true;
            }

            Invoke(true);
        }

        public void Answered()
        {
            bool hide;
            lock (_sync)
            {
                if (_disposed)
                    return;
                hide = _shown;
                _shown = false;
                try { _timer?.Change(SlowNoticeAfter, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }

            if (hide)
                Invoke(false);
        }

        public void Dispose()
        {
            bool hide;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                hide = _shown;
                _shown = false;
            }

            _timer?.Dispose();
            if (hide)
                Invoke(false);
        }

        private void Invoke(bool slow)
        {
            try
            {
                _callback?.Invoke(slow);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ServerAnswerWait: обработчик «ждём сервер» упал: {ex.GetType().Name}", "WARNING");
            }
        }
    }

    /// <summary>Сервер ответил 429 после <paramref name="startedUtc"/> или ещё просит паузу.</summary>
    private static bool ThrottledSince(DateTime startedUtc) =>
        ApiThrottle.LastThrottledUtc >= startedUtc || ApiThrottle.RemainingBlock > TimeSpan.Zero;

    /// <summary>2026-11-03 (С-01): текст окна, когда сервер не ответил за <see cref="ForegroundBudget"/> («медленно», а не «недоступен»).</summary>
    public static string SlowMessage => Tr.T(
        "Сервер NurCRM отвечает медленно. Попробуйте ещё раз чуть позже.",
        "NurCRM сервери жай жооп берип жатат. Бир аздан кийин кайра аракет кылыңыз.",
        "The NurCRM server is responding slowly. Please try again shortly.",
        "NurCRM sunucusu yavaş yanıt veriyor. Biraz sonra tekrar deneyin.",
        "NurCRM serveri sekin javob bermoqda. Birozdan keyin qayta urinib ko'ring.");

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

    /// <summary>2026-11-03 (ревью Н-4): текст на языке интерфейса (раньше был зашит по-русски и попадал в окна через ex.Message).</summary>
    public override string Message => Throttled ? ServerAnswerWait.ThrottledMessage : ServerAnswerWait.SlowMessage;
}

/// <summary>2026-11-03 (С-01): итог <see cref="ServerAnswerWait.ForegroundWriteAsync{T}"/>. <see cref="Pending"/> = false — ответ получен вовремя (<see cref="Value"/>).
/// <see cref="Pending"/> = true — за срок ответа нет, запрос НЕ отменён и продолжается: итог даст <see cref="Completion"/> (ошибку она отдаст исключением).</summary>
public sealed class ForegroundResult<T>
{
    public ForegroundResult(bool pending, T? value, Task<T> completion)
    {
        Pending = pending;
        Value = value;
        Completion = completion;
    }

    public bool Pending { get; }

    public T? Value { get; }

    public Task<T> Completion { get; }
}
