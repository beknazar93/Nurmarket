namespace NurMarketKassa.Services;

/// <summary>2026-11-06: что отправить «сейчас» из окна «Неотправленное» (OutboxService): id чеков очереди, операций без связи и смен с отложенным закрытием.</summary>
public sealed record OutboxScope(ISet<string> SaleIds, ISet<string> OperationIds, ISet<string> ShiftIds)
{
    public static OutboxScope Sales(params string[] ids) => new(new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed partial class SyncService
{
    /// <summary>Работающая служба синхронизации (ставится в <see cref="Start"/>, снимается в Dispose). «Неотправленное» отправляет через неё.
    /// internal set — тесты подставляют свою службу.</summary>
    public static SyncService? Current { get; internal set; }

    /// <summary>2026-11-06, «Неотправленное → Отправить сейчас»: ТОТ ЖЕ путь, что у фонового прохода (SyncBatchAsync / OfflineOperationsReplayer / закрытие смен), но только для выбранных записей.
    /// Замок прохода (<c>_syncGate</c>) общий с автоматикой: пока она шлёт, ждём её конец — запись, которую она отправила, из очереди уже пропала и второй раз не уйдёт;
    /// ключи повтора (Idempotency-Key = id записи, PendingIdempotencyKey операций) те же, поэтому даже потерянный ответ не создаёт дубль. Не бросает наружу ошибки сети: итог видно по записи.</summary>
    public async Task SendScopedAsync(OutboxScope scope, CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested || SessionExpiryMonitor.IsExpired)
            return;

        await ProbeNowAsync(ct).ConfigureAwait(false);
        if (!IsOnline)
            return;

        // Ждём конец идущего прохода (он может сам отправить эту запись), но не бесконечно.
        using var gateWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateWait.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await _syncGate.WaitAsync(gateWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return; // проход всё ещё идёт — запись остаётся в очереди, его итог покажет окно
        }

        try
        {
            IsSyncInProgress = true;
            UpdateStatusText();
            RaiseStateChanged();

            if (scope.SaleIds.Count > 0)
            {
                var pending = OfflinePendingSalesStore.LoadPendingForSync().Where(e => scope.SaleIds.Contains(e.Id)).ToList();
                if (pending.Count > 0 && IsOnline && !ServerOutageMonitor.IsOutage)
                    await SyncBatchAsync(pending, ct).ConfigureAwait(false);
            }

            if (scope.OperationIds.Count > 0 && IsOnline && !ServerOutageMonitor.IsOutage)
                await ReplayOfflineOperationsAsync(ct, scope.OperationIds).ConfigureAwait(false);

            if (scope.ShiftIds.Count > 0 && IsOnline && !ServerOutageMonitor.IsOutage)
                await FlushPendingShiftClosesAsync(ct, scope.ShiftIds).ConfigureAwait(false);
        }
        finally
        {
            IsSyncInProgress = false;
            UpdateStatusText();
            RaiseStateChanged();
            _syncGate.Release();
        }
    }
}
