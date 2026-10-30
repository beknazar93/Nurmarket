using System.Net.Http;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.Api;

/// <summary>Сервер NurCRM ограничивает частоту запросов одного пользователя и на превышение
/// отвечает 429 «Запрос был проигнорирован. Expected available in 15 seconds».
///
/// 2026-09-25, живой случай («программа зависает и глючит»): окно «Финансы» качало детали
/// чеков по 8 штук одновременно, за минуту получило 114 отказов, и на эти секунды сервер
/// отказывал уже всей кассе — оплате, каталогу, сменам. Здесь касса запоминает, сколько
/// сервер просит подождать, а массовые загрузки (детали чеков для отчётов) идут не чаще
/// одного запроса в <see cref="BulkInterval"/>, не больше трёх сразу и не идут вовсе, пока
/// сервер просит паузу. Обычные запросы кассы (оплата, смена) через этот темп не проходят.</summary>
public static class ApiThrottle
{
    public static readonly TimeSpan BulkInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);
    private static readonly Regex WaitSeconds = new(@"(\d+)\s*(?:sec|сек)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim BulkSlots = new(3, 3);
    private static DateTime _blockedUntilUtc = DateTime.MinValue;
    private static DateTime _nextBulkUtc = DateTime.MinValue;

    /// <summary>Сколько ещё сервер просит не присылать запросы.</summary>
    public static TimeSpan RemainingBlock
    {
        get
        {
            lock (Sync)
            {
                var left = _blockedUntilUtc - DateTime.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    private static DateTime _lastThrottledUtc = DateTime.MinValue;

    /// <summary>2026-10-04: когда сервер последний раз ответил 429. Окна с коротким ожиданием сервера
    /// (ServerAnswerWait) по нему отличают «сервер жив и просит паузу» от «сервер молчит»: к концу
    /// ожидания пауза из 429 уже истекает, и по одному RemainingBlock 429 принимался за аварию
    /// (стенд 04.10: окно возврата при 429 объявляло аварию на живом сервере).</summary>
    public static DateTime LastThrottledUtc
    {
        get
        {
            lock (Sync)
                return _lastThrottledUtc;
        }
    }

    // 2026-10-30 (приёмка 1.17.65, «Важно» 2): счётчик 429 для журнала — раз в 5 минут одна строка вместо строки на каждый отказ.
    private static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(5);
    private static DateTime _lastSummaryUtc = DateTime.MinValue;
    private static int _throttledSinceSummary;

    /// <summary>Запоминает паузу из ответа 429 и возвращает её.</summary>
    internal static TimeSpan ReportThrottled(HttpResponseMessage response, string? body)
    {
        // 2026-10-30 (приёмка 1.17.67, мелочь): Retry-After может быть датой HTTP — раньше такой ответ всегда давал 5 с.
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? retryAfterWait = retryAfter?.Delta ?? (retryAfter?.Date is { } retryDate ? (TimeSpan?)(retryDate - DateTimeOffset.UtcNow) : null);
        var wait = retryAfterWait ?? ParseWait(body) ?? TimeSpan.FromSeconds(5);
        if (wait < TimeSpan.FromSeconds(1))
            wait = TimeSpan.FromSeconds(1);
        if (wait > MaxWait)
            wait = MaxWait;

        int? summaryCount = null;
        lock (Sync)
        {
            var until = DateTime.UtcNow + wait;
            if (until > _blockedUntilUtc)
                _blockedUntilUtc = until;
            _lastThrottledUtc = DateTime.UtcNow;
            _throttledSinceSummary++;
            if (DateTime.UtcNow - _lastSummaryUtc >= SummaryEvery)
            {
                summaryCount = _throttledSinceSummary;
                _throttledSinceSummary = 0;
                _lastSummaryUtc = DateTime.UtcNow;
            }
        }

        if (summaryCount is { } count)
            PosLogger.Log($"Сервер занят (429, слишком частые запросы): пауза {wait.TotalSeconds:0} с; отказов с прошлой записи — {count} (запись раз в 5 минут). Последний запрос: {response.RequestMessage?.RequestUri?.AbsolutePath}", "API");
        // 2026-10-30 (приёмка 1.17.66, «Важно» 1): оплата/смена в этом потоке узнают о паузе и продлевают свой срок (Retry-After + запас) — см. ServerOutageMonitor.NoteThrottled.
        ServerOutageMonitor.NoteThrottled(wait);
        return wait;
    }

    /// <summary>2026-10-30 (приёмка 1.17.66, «Важно» 1): запас сверх Retry-After для критичных запросов (оплата, смена): пауза + время самого запроса.</summary>
    public static readonly TimeSpan CriticalReserve = TimeSpan.FromSeconds(4);

    /// <summary>Срок ожидания критичного запроса после 429: пауза сервера (до 20 с) + запас.</summary>
    public static TimeSpan CriticalBudget(TimeSpan wait) => wait + CriticalReserve;

    /// <summary>Сервер отвечал 429 после <paramref name="startedUtc"/> — отмена по сроку после этого значит «сервер занят», а не «сервер молчит».
    /// 2026-10-30 (приёмка 1.17.67, «Важно» 1): пауза 429, полученная ДО старта операции и ещё идущая, тоже считается — иначе срок, истёкший внутри чужой паузы, объявлял аварию.</summary>
    public static bool ThrottledSince(DateTime startedUtc) => LastThrottledUtc >= startedUtc || IsBusy;

    // ── 2026-10-30 (приёмка кассы 1.17.65, «Важно» 2): «Сервер занят» — отдельное состояние, не «нет связи» ──────────────────────────────────
    // Раньше 429 засчитывался как сбой сервера: касса уходила в «Автономно», смена выглядела закрытой, список клиентов не грузился без повтора.
    // Теперь 429 только ставит паузу (Retry-After) здесь; все запросы кассы проходят через общий щит (JwtBearerRefreshHandler → WaitShieldAsync):
    // пока пауза идёт, фоновые (лента, пульс, настройки, клиенты…) ждут её конца и потом выходят по одному, а критичные запросы пользователя
    // (оплата, открытие и закрытие смены, состояние смены) — сразу по окончании паузы и без очереди.

    /// <summary>Сколько ещё сервер просит подождать — для надписи «Сервер занят, повтор через N с» в шапке; 0 — не занят.</summary>
    public static TimeSpan BusyRemaining => RemainingBlock;

    /// <summary>Сервер сейчас просит паузу (429).</summary>
    public static bool IsBusy => RemainingBlock > TimeSpan.Zero;

    private static readonly System.Threading.AsyncLocal<int> CriticalDepth = new();

    /// <summary>Пока вызывающий не освободит возвращённый объект, его запросы (и вложенные await) считаются критичными: оплата, смена.</summary>
    public static IDisposable BeginCritical()
    {
        CriticalDepth.Value++;
        return new CriticalScope();
    }

    private sealed class CriticalScope : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0 && CriticalDepth.Value > 0)
                CriticalDepth.Value--;
        }
    }

    public static bool IsCritical => CriticalDepth.Value > 0;

    private static readonly TimeSpan BackgroundGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(45);

    /// <summary>Общий щит: ждёт, пока сервер просит паузу (не дольше <see cref="MaxWait"/>). Критичные запросы выходят сразу по окончании паузы,
    /// фоновые — позже на <see cref="BackgroundGrace"/> и (пока 429 был недавно) не чаще одного в <see cref="BulkInterval"/>, чтобы не штурмовать сервер разом.</summary>
    public static async Task WaitShieldAsync(CancellationToken ct)
    {
        var critical = IsCritical;
        var block = RemainingBlock;
        if (block > TimeSpan.Zero)
        {
            var wait = critical ? block : block + BackgroundGrace;
            await Task.Delay(wait > MaxWait ? MaxWait : wait, ct).ConfigureAwait(false);
        }

        if (!critical && DateTime.UtcNow - LastThrottledUtc < RecentWindow)
            await WaitBulkTurnAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Сколько секунд до повтора для надписей (не меньше 1).</summary>
    public static int RetrySeconds => Math.Max(1, (int)Math.Ceiling(RemainingBlock.TotalSeconds));

    /// <summary>Нейтральная надпись для шапки (пока <see cref="IsBusy"/>): «Сервер занят, повтор через N с».</summary>
    public static string BusyHeaderText() => Tr.T($"Сервер занят, повтор через {RetrySeconds} с", $"Сервер бош эмес, {RetrySeconds} с кийин кайталанат",
        $"Server busy, retrying in {RetrySeconds} s", $"Sunucu meşgul, {RetrySeconds} sn sonra tekrar", $"Server band, {RetrySeconds} s dan keyin qayta uriniladi");

    /// <summary>Сообщение, когда действие не удалось из-за 429: сервер жив, подождите и повторите.</summary>
    public static string BusyText() => Tr.T("Сервер занят (слишком много запросов). Подождите несколько секунд и повторите — данные не потеряны.",
        "Сервер бош эмес (өтүнмөлөр өтө көп). Бир нече секунд күтүп, кайталаңыз — маалымат жоголгон жок.",
        "The server is busy (too many requests). Wait a few seconds and retry — nothing is lost.",
        "Sunucu meşgul (çok fazla istek). Birkaç saniye bekleyip tekrar deneyin — veri kaybolmadı.",
        "Server band (so'rovlar juda ko'p). Bir necha soniya kutib, qayta urinib ko'ring — ma'lumot yo'qolmadi.");

    /// <summary>Это ответ «слишком часто» (429 / throttled), а не обрыв связи.</summary>
    public static bool IsThrottled(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is ApiException api && api.StatusCode == 429)
                return true;
            if (e is HttpRequestException http && http.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return true;
            if (LooksThrottled(e.Message))
                return true;
        }

        return false;
    }

    /// <summary>Текст ошибки говорит о 429 («HTTP 429», Too Many Requests, «Request was throttled», «слишком часто»).</summary>
    public static bool LooksThrottled(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
               || text.Contains("(429)", StringComparison.Ordinal)
               || text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
               || text.Contains("throttled", StringComparison.OrdinalIgnoreCase)
               || text.Contains("слишком часто", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Expected available in", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ждёт конец текущей паузы 429 (не дольше <see cref="MaxWait"/>) — для повтора после «Сервер занят».</summary>
    public static Task WaitUntilFreeAsync(CancellationToken ct)
    {
        var block = RemainingBlock + TimeSpan.FromMilliseconds(300);
        return Task.Delay(block > MaxWait ? MaxWait : block, ct);
    }

    /// <summary>Запрос с повтором при 429: после каждого отказа ждёт паузу сервера и пробует снова (всего <paramref name="attempts"/> попыток).
    /// Другие ошибки — наружу сразу. <paramref name="onWait"/> получает, сколько ждать, — для надписи «повтор через N с».</summary>
    public static async Task<T> RunWithRetryAsync<T>(Func<Task<T>> request, int attempts = 3, Action<TimeSpan>? onWait = null, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await request().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsThrottled(ex) && attempt < attempts && !ct.IsCancellationRequested)
            {
                var pause = RemainingBlock;
                if (pause < TimeSpan.FromSeconds(1))
                    pause = TimeSpan.FromSeconds(Math.Min(5 * attempt, 15));
                onWait?.Invoke(pause);
                await Task.Delay(pause > MaxWait ? MaxWait : pause, ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan? ParseWait(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return null;
        var match = WaitSeconds.Match(body);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>Запрос из массовой фоновой загрузки (детали чеков для отчётов и истории).</summary>
    public static async Task<T> RunBulkAsync<T>(Func<Task<T>> request, CancellationToken ct = default)
    {
        await BulkSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitBulkTurnAsync(ct).ConfigureAwait(false);
            return await request().ConfigureAwait(false);
        }
        finally
        {
            BulkSlots.Release();
        }
    }

    private static async Task WaitBulkTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                var ready = _blockedUntilUtc > _nextBulkUtc ? _blockedUntilUtc : _nextBulkUtc;
                if (ready <= now)
                {
                    _nextBulkUtc = now + BulkInterval;
                    return;
                }

                delay = ready - now;
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
