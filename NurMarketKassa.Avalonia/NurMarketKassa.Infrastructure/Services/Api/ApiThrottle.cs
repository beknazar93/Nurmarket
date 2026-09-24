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

    /// <summary>Запоминает паузу из ответа 429 и возвращает её.</summary>
    internal static TimeSpan ReportThrottled(HttpResponseMessage response, string? body)
    {
        var wait = response.Headers.RetryAfter?.Delta ?? ParseWait(body) ?? TimeSpan.FromSeconds(5);
        if (wait < TimeSpan.FromSeconds(1))
            wait = TimeSpan.FromSeconds(1);
        if (wait > MaxWait)
            wait = MaxWait;

        lock (Sync)
        {
            var until = DateTime.UtcNow + wait;
            if (until > _blockedUntilUtc)
                _blockedUntilUtc = until;
        }

        PosLogger.Log($"Сервер попросил паузу {wait.TotalSeconds:0} с (слишком частые запросы): {response.RequestMessage?.RequestUri?.AbsolutePath}", "API");
        return wait;
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
