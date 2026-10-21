using System.Diagnostics;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21: общий ограничитель темпа отправки в Telegram (бот, п. 4 и ВАЖНО 6 отчёта 20.10). Стресс-тест показал до 45–58 сообщ./с
/// на бота и до 58 сообщ./с в один чат — настоящий Telegram отвечал бы 429 и терял ответы. Пределы Telegram: около 30 сообщ./с на бота,
/// около 1 сообщ./с в один чат (короткие пачки терпит), в группу — 20 в минуту.
///
/// Алгоритм — «ведро» (GCRA): у бота и у каждого чата своё «время следующего свободного места». Отправка ждёт ТОЛЬКО свой чат и общий
/// предел; другой чат при этом не стоит (очередь ответов — по чатам, TelegramBotPollingService.Queue.cs).
/// </summary>
internal static class TelegramBotRateLimiter
{
    // С запасом от пределов Telegram: 25/с на бота (пачка до 5), 1/с в личный чат (пачка до 3 — ответ частями), в группу 1 в 3 с.
    private const double GlobalIntervalMs = 1000.0 / 25;
    private const int GlobalBurst = 5;
    private const double ChatIntervalMs = 1000;
    private const int ChatBurst = 3;
    private const double GroupIntervalMs = 3000;
    private const int GroupBurst = 3;

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<string, double> ChatTat = new(StringComparer.Ordinal);
    private static double _globalTat;

    private static double Now => Clock.Elapsed.TotalMilliseconds;

    private static bool IsGroup(string chatId) => chatId.StartsWith('-');

    /// <summary>Ждёт своего места: сначала в чате, потом в общем пределе бота.</summary>
    public static async Task WaitAsync(string chatId, CancellationToken ct)
    {
        double chatDelay;
        lock (Gate)
        {
            var group = IsGroup(chatId);
            var tat = ChatTat.TryGetValue(chatId, out var t) ? t : 0;
            chatDelay = Reserve(ref tat, group ? GroupIntervalMs : ChatIntervalMs, group ? GroupBurst : ChatBurst, Now);
            ChatTat[chatId] = tat;
            if (ChatTat.Count > 5000)
                Prune();
        }

        if (chatDelay > 1)
            await Task.Delay(TimeSpan.FromMilliseconds(chatDelay), ct).ConfigureAwait(false);

        double globalDelay;
        lock (Gate)
            globalDelay = Reserve(ref _globalTat, GlobalIntervalMs, GlobalBurst, Now);
        if (globalDelay > 1)
            await Task.Delay(TimeSpan.FromMilliseconds(globalDelay), ct).ConfigureAwait(false);
    }

    /// <summary>Telegram ответил 429 «подождите N с» — следующая отправка в этот чат не раньше, чем через N с.</summary>
    public static void Penalize(string chatId, int retryAfterSeconds)
    {
        lock (Gate)
        {
            var group = IsGroup(chatId);
            var tau = ((group ? GroupBurst : ChatBurst) - 1) * (group ? GroupIntervalMs : ChatIntervalMs);
            var until = Now + retryAfterSeconds * 1000.0 + tau;
            ChatTat[chatId] = Math.Max(ChatTat.TryGetValue(chatId, out var t) ? t : 0, until);
        }
    }

    /// <summary>GCRA: место дают не раньше tat − τ (τ — допустимая пачка); возвращает, сколько ждать, мс.</summary>
    private static double Reserve(ref double tat, double intervalMs, int burst, double now)
    {
        var tau = (burst - 1) * intervalMs;
        var start = Math.Max(now, tat - tau);
        tat = Math.Max(tat, start) + intervalMs;
        return start - now;
    }

    private static void Prune()
    {
        var old = Now - 60_000;
        foreach (var key in ChatTat.Where(kv => kv.Value < old).Select(kv => kv.Key).ToList())
            ChatTat.Remove(key);
    }
}
