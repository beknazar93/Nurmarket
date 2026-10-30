using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.PhoneLink;

/// <summary>2026-10-29 (отчёт «Скорость синхронизации», п. 2): касса — центр событий магазина. Протокол PhoneLink v1 расширен двумя
/// необязательными операциями (старые телефоны их не вызывают, старые кассы отвечают bad_request — телефон тогда живёт по-старому):
/// • <c>events</c> — «что изменилось после seq». Тело {"since":N,"wait":мс}. Есть события после N — ответ сразу; нет — касса держит запрос до
///   <c>wait</c> мс (не больше 12 000, у сервера на операцию 15 с) и отвечает сразу, как только что-то случилось. Ответ: seq (число) и
///   kinds (типы через запятую: sale, product, stock, price; пусто — ничего нового). since = 0 — «запомнить текущий seq» (ответ сразу).
///   since больше текущего seq (касса перезапущена) — kinds = «product,sale»: перечитать всё. На один телефон — один ожидающий запрос
///   (новый заменяет прежний).
/// • <c>hint</c> — «я записал изменение». Тело {"kind":"price"}. Касса сразу читает ленту каталога с NurCRM, передаёт подсказку другим кассам
///   и программе владельца по локальной сети и отдаёт её остальным телефонам.
/// В событиях только тип, без идентификаторов и данных: получатель сам берёт данные с NurCRM под своими правами. Подпись и проверки — те же,
/// что у scan (см. <see cref="PhoneLinkServer"/>).</summary>
public static class PhoneLinkEvents
{
    public const string OpEvents = "events";
    public const string OpHint = "hint";
    public const int MaxWaitMs = 12_000;
    private const int RingSize = 64;
    private static readonly TimeSpan HintMinGap = TimeSpan.FromMilliseconds(500);

    private static readonly object Gate = new();
    private static readonly List<(long Seq, string Kind)> Ring = new();
    private static long _seq;
    private static TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Waiters = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> LastHint = new(StringComparer.Ordinal);

    public static long Seq
    {
        get
        {
            lock (Gate)
                return _seq;
        }
    }

    /// <summary>Записать событие и разбудить ждущие телефоны. Не бросает.</summary>
    public static void Publish(string kind)
    {
        if (!SyncCenter.IsKnownHint(kind))
            return;
        TaskCompletionSource<bool> old;
        lock (Gate)
        {
            _seq++;
            Ring.Add((_seq, kind));
            if (Ring.Count > RingSize)
                Ring.RemoveRange(0, Ring.Count - RingSize);
            old = _wake;
            _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult(true);
    }

    private static (long Seq, string Kinds, Task Wake) Snapshot(long since)
    {
        lock (Gate)
        {
            string kinds;
            if (since > _seq)
                kinds = "product,sale"; // касса перезапущена: счётчик начался заново
            else
                kinds = string.Join(',', Ring.Where(e => e.Seq > since).Select(e => e.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal));
            return (_seq, kinds, _wake.Task);
        }
    }

    private static PhoneLinkReply Reply(long seq, string kinds) =>
        PhoneLinkReply.Success(PhoneLinkCodes.Ok, null, new JsonObject { ["seq"] = seq, ["kinds"] = kinds });

    private static long ReadLong(JsonObject body, string name, long fallback) =>
        body[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : fallback;

    /// <summary>Обработчик операции events (см. описание класса).</summary>
    public static async Task<PhoneLinkReply> HandleEventsAsync(PhoneLinkCall call, CancellationToken ct)
    {
        var since = ReadLong(call.Body, "since", 0);
        var wait = (int)Math.Clamp(ReadLong(call.Body, "wait", 0), 0, MaxWaitMs);
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Waiters.AddOrUpdate(call.Phone, mine, (_, previous) =>
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { /* уже завершён */ }
            return mine;
        });
        try
        {
            var deadline = Environment.TickCount64 + wait;
            while (true)
            {
                var (seq, kinds, wake) = Snapshot(since);
                if (since <= 0 || kinds.Length > 0 || wait <= 0)
                    return Reply(seq, kinds);
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return Reply(seq, "");
                using var step = CancellationTokenSource.CreateLinkedTokenSource(mine.Token);
                await Task.WhenAny(wake, Task.Delay((int)left, step.Token)).ConfigureAwait(false);
                step.Cancel(); // таймер ожидания больше не нужен
                if (mine.IsCancellationRequested)
                    return Reply(seq, ""); // заменён новым запросом этого телефона или касса остановлена
            }
        }
        finally
        {
            Waiters.TryRemove(new KeyValuePair<string, CancellationTokenSource>(call.Phone, mine));
        }
    }

    /// <summary>Обработчик операции hint (см. описание класса).</summary>
    public static Task<PhoneLinkReply> HandleHintAsync(PhoneLinkCall call, CancellationToken ct)
    {
        var kind = call.Body["kind"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        if (!SyncCenter.IsKnownHint(kind))
            return Task.FromResult(PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Неизвестный тип подсказки."));
        // Не чаще двух подсказок в секунду с одного телефона: лишние молча принимаем (успех), чтобы телефон не повторял.
        var now = Environment.TickCount64;
        var previous = LastHint.GetOrAdd(call.Phone, 0);
        if (previous != 0 && now - previous < (long)HintMinGap.TotalMilliseconds)
            return Task.FromResult(PhoneLinkReply.Success());
        LastHint[call.Phone] = now;
        SyncCenter.OnPhoneHint(kind!);
        return Task.FromResult(PhoneLinkReply.Success());
    }
}
