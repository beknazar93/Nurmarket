using System.Collections.Concurrent;
using System.Net.Http;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06, просьба владельца («история неотправленных документов и ручная отправка»): единое место «Неотправленное».
/// Собирает записи всех очередей кассы (чеки продаж, возвраты и оплаты долга без связи, приходы в фоне, новые товары, закрытия смен,
/// внесения/изъятия) в один список по времени СОЗДАНИЯ и даёт ручную отправку.
///
/// Чего сервис НЕ делает: не хранит записи, не меняет форматы файлов, не шлёт сам. Отправка — через источник: он вызывает тот же код, что и фоновая автоматика
/// (SyncService под своим замком прохода, воркер очереди приходов, ShiftCashFlowSync), с тем же ключом повтора (Idempotency-Key / PendingIdempotencyKey).
/// Дубль исключён так: (1) замок «отправляется» на запись здесь — двойное нажатие ничего не делает; (2) параллельная автоотправка упирается в замок самого источника,
/// а запись, уже ушедшая, при повторном проходе из очереди пропадает.</summary>
public sealed class OutboxService
{
    public static OutboxService Default { get; } = new();

    /// <summary>Запись в журнал кто/когда/что (по умолчанию — журнал кассы). Тесты подменяют.</summary>
    public static Action<string> Audit { get; set; } = message => PosLogger.Log(message, "OUTBOX");

    /// <summary>Короткая проверка связи перед ручной отправкой (приложение подключает общую проверку SharedConnectivityCheck). Не задана — только локальные признаки
    /// (авария сервера, офлайн-режим).</summary>
    public Func<CancellationToken, Task<bool>>? ReachCheck { get; set; }

    /// <summary>Сколько ждать ответа проверки связи: без зависания на таймауте сети.</summary>
    public TimeSpan ReachTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Сколько ждать завершения одной попытки (потом запись остаётся «Отправляется…» и показывает итог живое обновление окна).</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly List<IOutboxSource> _sources = new();
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, byte> _sending = new(StringComparer.OrdinalIgnoreCase);
    private int _batchRunning;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _eventSync = new();

    /// <summary>Что-то в очередях изменилось (из любого потока).</summary>
    public event Action? Changed;

    public void Register(IOutboxSource source)
    {
        lock (_sync)
        {
            if (_sources.Any(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
                return;
            _sources.Add(source);
            lock (_eventSync)
                _subscriptions.Add(source.Subscribe(RaiseChanged));
        }
    }

    /// <summary>Для тестов: убрать все источники и подписки.</summary>
    public void ClearSources()
    {
        lock (_sync)
        {
            lock (_eventSync)
            {
                foreach (var s in _subscriptions)
                    s.Dispose();
                _subscriptions.Clear();
            }
            _sources.Clear();
            _sending.Clear();
        }
    }

    public bool HasSource(string name)
    {
        lock (_sync)
            return _sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Неотправленное: обработчик изменения упал ({ex.Message}).", "WARNING"); }
    }

    private IOutboxSource[] Sources()
    {
        lock (_sync)
            return _sources.ToArray();
    }

    // ------------------------------------------------------------------ список

    /// <summary>Все видимые записи по времени создания (старые первыми — порядок отправки). Запись, которую сейчас отправляет ручная отправка, получает статус «Отправляется».</summary>
    public IReadOnlyList<OutboxItem> Snapshot(OutboxAccess who)
    {
        var all = new List<OutboxItem>();
        foreach (var source in Sources())
        {
            try
            {
                all.AddRange(source.Snapshot().Where(who.CanSee));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Неотправленное: источник «{source.Name}» не прочитан ({ex.Message}).", "WARNING");
            }
        }
        // Старые первыми, при равенстве времени — по ключу (стабильный порядок строк в окне).
        return all.OrderBy(i => i.CreatedAt).ThenBy(i => i.Key, StringComparer.Ordinal)
            .Select(i => _sending.ContainsKey(i.Key) && i.Status != OutboxStatus.Sending ? WithStatus(i, OutboxStatus.Sending) : i)
            .ToList();
    }

    /// <summary>«Ждут: N · Ошибок: M» для шапки.</summary>
    public (int Waiting, int Errors) Counts(OutboxAccess who)
    {
        var items = Snapshot(who);
        var errors = items.Count(i => i.Status == OutboxStatus.Error);
        return (items.Count - errors, errors);
    }

    private static OutboxItem WithStatus(OutboxItem i, OutboxStatus status) => new()
    {
        Source = i.Source, Id = i.Id, Kind = i.Kind, Title = i.Title, Subtitle = i.Subtitle, Amount = i.Amount, LinesCount = i.LinesCount,
        CreatedAt = i.CreatedAt, Status = status, Reason = i.Reason, Attempts = i.Attempts, LastAttemptAt = i.LastAttemptAt, Owner = i.Owner,
        Lines = i.Lines, Details = i.Details, Hint = i.Hint, CanSend = i.CanSend, CanRetry = i.CanRetry, CanRemove = i.CanRemove,
        CanTakeBack = i.CanTakeBack, CanCopy = i.CanCopy, CanSupport = i.CanSupport,
    };

    private (IOutboxSource Source, OutboxItem Item)? Find(string key, OutboxAccess who, out OutboxSendResult? refusal)
    {
        refusal = null;
        foreach (var source in Sources())
        {
            OutboxItem? item;
            try { item = source.Snapshot().FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { item = null; PosLogger.Log($"Неотправленное: источник «{source.Name}» не прочитан ({ex.Message}).", "WARNING"); }
            if (item is null)
                continue;
            if (!who.CanSee(item))
            {
                refusal = new OutboxSendResult(OutboxSendKind.Forbidden, OutboxText.Forbidden, item);
                return null;
            }
            return (source, item);
        }
        refusal = new OutboxSendResult(OutboxSendKind.NotFound, OutboxText.NotFound);
        return null;
    }

    private OutboxItem? Reload(IOutboxSource source, string key)
    {
        try { return source.Snapshot().FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // ------------------------------------------------------------------ предпроверка

    /// <summary>Сессия недействительна → «Войдите снова»; нет связи → «запись осталась в очереди». Быстро: проверка связи ограничена <see cref="ReachTimeout"/>.</summary>
    private async Task<OutboxSendResult?> PreflightAsync(CancellationToken ct)
    {
        if (SessionExpiryMonitor.IsExpired)
            return new OutboxSendResult(OutboxSendKind.SessionExpired, OutboxText.SessionExpired);
        if (ServerOutageMonitor.IsOutage || OfflineModeHelper.SellLocally)
            return new OutboxSendResult(OutboxSendKind.NoConnection, OutboxText.NoConnection);
        var check = ReachCheck;
        if (check is null)
            return null;
        bool reachable;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReachTimeout);
            var probe = check(cts.Token);
            var done = await Task.WhenAny(probe, Task.Delay(ReachTimeout, ct)).ConfigureAwait(false);
            reachable = done == probe && await probe.ConfigureAwait(false);
            if (done != probe)
                cts.Cancel();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reachable = false;
        }
        return reachable ? null : new OutboxSendResult(OutboxSendKind.NoConnection, OutboxText.NoConnection);
    }

    // ------------------------------------------------------------------ отправка

    /// <summary>«Отправить сейчас» по одной записи. Второе нажатие на то, что уже отправляется, ничего не делает.</summary>
    public async Task<OutboxSendResult> SendNowAsync(string key, OutboxAccess who, CancellationToken ct = default)
    {
        var found = Find(key, who, out var refusal);
        if (found is null)
            return refusal!;
        var (source, item) = found.Value;

        // Запись с ошибкой «отправить» не лечит — у неё «Повторить» (возврат в очередь тем же ключом, потом отправка).
        if (item.Status == OutboxStatus.Error && item.CanRetry)
            return await RetryAsync(key, who, ct).ConfigureAwait(false);
        return await SendCoreAsync(source, item, who, ct).ConfigureAwait(false);
    }

    private async Task<OutboxSendResult> SendCoreAsync(IOutboxSource source, OutboxItem item, OutboxAccess who, CancellationToken ct)
    {
        if (!_sending.TryAdd(item.Key, 0))
            return new OutboxSendResult(OutboxSendKind.AlreadySending, OutboxText.AlreadySending, item);
        RaiseChanged();
        try
        {
            if (item.Status == OutboxStatus.Sending)
                return new OutboxSendResult(OutboxSendKind.AlreadySending, OutboxText.AlreadySending, item); // автоматика прямо сейчас с ней работает

            var pre = await PreflightAsync(ct).ConfigureAwait(false);
            if (pre is not null)
                return pre with { Item = item };

            Audit($"Неотправленное: ручная отправка «{item.KindLabel}» {item.Title} ({item.Id}), создана {item.CreatedAt:dd.MM.yyyy HH:mm:ss}, кто: {who.UserName ?? "—"}.");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(SendTimeout);
                await source.SendAsync(item.Id, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Попытка не уложилась в срок: запись остаётся в очереди, итог покажет обновление списка.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var mapped = MapFailure(ex, item);
                if (mapped is not null)
                    return mapped;
            }

            return Evaluate(source, item);
        }
        finally
        {
            _sending.TryRemove(item.Key, out _);
            RaiseChanged();
        }
    }

    private static OutboxSendResult? MapFailure(Exception ex, OutboxItem item)
    {
        if (SessionExpiryMonitor.IsAuthFailure(ex) || SessionExpiryMonitor.IsExpired)
            return new OutboxSendResult(OutboxSendKind.SessionExpired, OutboxText.SessionExpired, item);
        if (ex is HttpRequestException or TimeoutException or IOException)
            return new OutboxSendResult(OutboxSendKind.NoConnection, OutboxText.NoConnection, item);
        PosLogger.Log($"Неотправленное: отправка {item.Key} не удалась: {ex.GetType().Name}: {ex.Message}", "WARNING");
        return new OutboxSendResult(OutboxSendKind.Queued, ServerOutageMonitor.FriendlyError(ex), item);
    }

    private OutboxSendResult Evaluate(IOutboxSource source, OutboxItem before)
    {
        var after = Reload(source, before.Key);
        if (after is null)
            return new OutboxSendResult(OutboxSendKind.Sent, OutboxText.SentOk, before);
        if (after.Status == OutboxStatus.Error)
            return new OutboxSendResult(OutboxSendKind.Rejected, after.Reason ?? OutboxText.StillQueued, after);
        if (SessionExpiryMonitor.IsExpired)
            return new OutboxSendResult(OutboxSendKind.SessionExpired, OutboxText.SessionExpired, after);
        var text = string.IsNullOrWhiteSpace(after.Reason) ? OutboxText.StillQueued : after.Reason + "\n" + OutboxText.StillQueued;
        return new OutboxSendResult(OutboxSendKind.Queued, text, after);
    }

    /// <summary>«Повторить» для записи с ошибкой: возвращает в очередь тем же ключом и сразу отправляет.</summary>
    public async Task<OutboxSendResult> RetryAsync(string key, OutboxAccess who, CancellationToken ct = default)
    {
        var found = Find(key, who, out var refusal);
        if (found is null)
            return refusal!;
        var (source, item) = found.Value;
        if (_sending.ContainsKey(item.Key))
            return new OutboxSendResult(OutboxSendKind.AlreadySending, OutboxText.AlreadySending, item);
        if (item.Status == OutboxStatus.Error)
        {
            if (!item.CanRetry || !source.Retry(item.Id))
                return new OutboxSendResult(OutboxSendKind.Rejected, item.Reason ?? OutboxText.StillQueued, item);
            Audit($"Неотправленное: «{item.KindLabel}» {item.Title} ({item.Id}) возвращена в очередь кнопкой «Повторить», кто: {who.UserName ?? "—"}.");
            RaiseChanged();
        }
        var fresh = Reload(source, item.Key);
        return fresh is null
            ? new OutboxSendResult(OutboxSendKind.Sent, OutboxText.SentOk, item)
            : await SendCoreAsync(source, fresh, who, ct).ConfigureAwait(false);
    }

    /// <summary>«Отправить всё»: ждущие записи по порядку времени создания (FIFO, как раньше). Записи с ошибкой не трогаем — у них своя кнопка «Повторить».
    /// Нет связи или нет сессии — останавливаемся сразу, а не серией ошибок.</summary>
    public async Task<OutboxBatchResult> SendAllAsync(OutboxAccess who, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _batchRunning, 1) != 0)
            return new OutboxBatchResult(0, 0, 0, OutboxSendKind.AlreadySending, OutboxText.AlreadySending);
        try
        {
            var pre = await PreflightAsync(ct).ConfigureAwait(false);
            if (pre is not null)
                return new OutboxBatchResult(0, 0, 0, pre.Kind, pre.Message);

            int sent = 0, queued = 0, rejected = 0;
            OutboxSendKind? stopped = null;
            string message = "";
            var items = Snapshot(who).Where(i => i.Status == OutboxStatus.Waiting && i.CanSend).ToList();
            var at = 0;
            while (at < items.Count && stopped is null)
            {
                ct.ThrowIfCancellationRequested();
                // Серия подряд идущих (по времени создания) записей одного источника: источник с пакетной отправкой шлёт её одним проходом (тысяча чеков — не тысяча проходов).
                var source = Sources().FirstOrDefault(s => string.Equals(s.Name, items[at].Source, StringComparison.OrdinalIgnoreCase));
                var run = new List<OutboxItem> { items[at] };
                while (at + run.Count < items.Count && string.Equals(items[at + run.Count].Source, items[at].Source, StringComparison.OrdinalIgnoreCase))
                    run.Add(items[at + run.Count]);
                at += run.Count;
                if (source is null)
                    continue;

                if (source is IOutboxBatchSource batch && run.Count > 1)
                {
                    var (s1, q1, r1, stop, text) = await SendRunAsync(batch, run, who, ct).ConfigureAwait(false);
                    sent += s1; queued += q1; rejected += r1;
                    if (stop is not null)
                    {
                        stopped = stop;
                        message = text;
                    }
                    continue;
                }

                foreach (var item in run)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await SendCoreAsync(source, item, who, ct).ConfigureAwait(false);
                    switch (r.Kind)
                    {
                        case OutboxSendKind.Sent: sent++; break;
                        case OutboxSendKind.Rejected: rejected++; break;
                        case OutboxSendKind.Queued: queued++; break;
                        case OutboxSendKind.NoConnection or OutboxSendKind.SessionExpired:
                            stopped = r.Kind;
                            message = r.Message;
                            break;
                    }
                    if (stopped is not null)
                        break;
                }
            }
            if (message.Length == 0)
                message = Tr.T($"Отправлено: {sent}. Осталось в очереди: {queued}. Ошибок: {rejected}.", $"Жөнөтүлдү: {sent}. Кезекте калды: {queued}. Каталар: {rejected}.",
                    $"Sent: {sent}. Left in the queue: {queued}. Errors: {rejected}.", $"Gönderildi: {sent}. Kuyrukta kaldı: {queued}. Hata: {rejected}.", $"Yuborildi: {sent}. Navbatda qoldi: {queued}. Xatolar: {rejected}.");
            return new OutboxBatchResult(sent, queued, rejected, stopped, message);
        }
        finally
        {
            Interlocked.Exchange(ref _batchRunning, 0);
        }
    }

    /// <summary>Сколько ждём пакетный проход источника (тысячи чеков).</summary>
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromMinutes(15);

    private async Task<(int Sent, int Queued, int Rejected, OutboxSendKind? Stop, string Text)> SendRunAsync(IOutboxBatchSource source, List<OutboxItem> run, OutboxAccess who, CancellationToken ct)
    {
        // Замок «отправляется» на каждую запись серии; уже занятые (их шлёт автоматика или другое нажатие) пропускаем.
        var mine = run.Where(i => _sending.TryAdd(i.Key, 0)).ToList();
        if (mine.Count == 0)
            return (0, 0, 0, null, "");
        RaiseChanged();
        try
        {
            var pre = await PreflightAsync(ct).ConfigureAwait(false);
            if (pre is not null)
                return (0, 0, 0, pre.Kind, pre.Message);

            Audit($"Неотправленное: ручная отправка серии — {mine.Count} зап. «{mine[0].KindLabel}», от {mine[0].CreatedAt:dd.MM.yyyy HH:mm:ss} до {mine[^1].CreatedAt:dd.MM.yyyy HH:mm:ss}, кто: {who.UserName ?? "—"}.");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(BatchTimeout);
                await source.SendManyAsync(mine.Select(i => i.Id).ToList(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // не уложились в срок: недоставленные остаются в очереди
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var mapped = MapFailure(ex, mine[0]);
                if (mapped is { Kind: OutboxSendKind.NoConnection or OutboxSendKind.SessionExpired })
                    return (0, 0, 0, mapped.Kind, mapped.Message);
            }

            Dictionary<string, OutboxItem> after;
            try { after = source.Snapshot().ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { after = new(); PosLogger.Log($"Неотправленное: серия не перечитана ({ex.Message}).", "WARNING"); }
            int sent = 0, queued = 0, rejected = 0;
            foreach (var item in mine)
            {
                if (!after.TryGetValue(item.Key, out var now))
                    sent++;
                else if (now.Status == OutboxStatus.Error)
                    rejected++;
                else
                    queued++;
            }
            if (SessionExpiryMonitor.IsExpired && queued > 0)
                return (sent, queued, rejected, OutboxSendKind.SessionExpired, OutboxText.SessionExpired);
            return (sent, queued, rejected, null, "");
        }
        finally
        {
            foreach (var i in mine)
                _sending.TryRemove(i.Key, out _);
            RaiseChanged();
        }
    }

    // ------------------------------------------------------------------ убрать / копировать

    /// <summary>«Убрать из очереди»: только владелец/админ, только записи с ошибкой, НИКОГДА чек продажи (деньги). Подтверждение «Эти данные не попадут на сервер. Убрать?» берёт окно;
    /// сервис пишет в журнал кто/когда/что и его вызывают только после «да».</summary>
    public OutboxRemoveResult Remove(string key, OutboxAccess who)
    {
        var found = Find(key, who, out var refusal);
        if (found is null)
            return new OutboxRemoveResult(false, refusal!.Message);
        var (source, item) = found.Value;
        if (item.Kind == OutboxKind.Sale)
            return new OutboxRemoveResult(false, OutboxText.RemoveForbiddenSale);
        if (!who.IsAdmin)
            return new OutboxRemoveResult(false, OutboxText.RemoveForbiddenRole);
        if (!item.CanRemove || item.Status != OutboxStatus.Error || _sending.ContainsKey(item.Key))
            return new OutboxRemoveResult(false, OutboxText.RemoveFailed);
        var who2 = string.IsNullOrWhiteSpace(who.UserName) ? "—" : who.UserName!;
        var summary = $"«{item.KindLabel}» {item.Title}" + (item.Amount is { } a ? $", сумма {a:0.00}" : "") + $", создана {item.CreatedAt:dd.MM.yyyy HH:mm:ss}, попыток {item.Attempts}, последняя ошибка: {item.Reason ?? "—"}";
        if (!source.Remove(item.Id, who2))
            return new OutboxRemoveResult(false, OutboxText.RemoveFailed);
        Audit($"Неотправленное: УБРАНО из очереди без отправки — {summary}. Кто: {who2}, когда: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}.");
        RaiseChanged();
        return new OutboxRemoveResult(true, OutboxText.Removed);
    }

    /// <summary>Текст для «Скопировать данные» / «Отправить в поддержку». null — записи нет или она чужая.</summary>
    public string? Export(string key, OutboxAccess who)
    {
        var found = Find(key, who, out _);
        if (found is null)
            return null;
        try { return found.Value.Source.ExportText(found.Value.Item.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: данные записи {key} не собраны ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Источник записи — окно использует для действий вроде «Вернуть в приёмку» (их знает конкретный источник).</summary>
    public IOutboxSource? SourceOf(string key)
    {
        var colon = key.IndexOf(':');
        var name = colon > 0 ? key[..colon] : key;
        return Sources().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
