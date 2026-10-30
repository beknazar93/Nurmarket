namespace NurMarketKassa.Services;

public static class OfflinePendingSalesStore
{
    private static readonly object UpdateGate = new();
    public static List<OfflineSaleEntry> LoadAll() => OfflineDatabase.LoadAll();

    public static void SaveAll(List<OfflineSaleEntry> items) => OfflineDatabase.SaveAll(items);

    /// <summary>2026-10-29: чек поставлен в очередь — SyncService досылает его почти сразу (если есть связь), а не ждёт очередного такта.</summary>
    public static event Action? Appended;

    public static void Append(OfflineSaleEntry entry)
    {
        OfflineDatabase.Append(entry);
        PosLogger.Log($"OFFLINE queue append: id={entry.Id}, receipt={entry.CreatedAt:yyyyMMdd-HHmmss}", "OFFLINE");
        try { Appended?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"OFFLINE: обработчик постановки в очередь не отработал: {ex.Message}", "WARNING"); }
    }

    /// <summary>Статусы, которые вообще могут оказаться "ждущими" — ими же фильтруется выборка в
    /// SQL, чтобы не разбирать JSON уже выгруженных продаж. Признак автономности живёт внутри
    /// json_data (колонки под него нет), поэтому его по-прежнему проверяет <see cref="IsPendingLike"/>
    /// уже в памяти — но только на этой короткой выборке, а не на всей таблице.</summary>
    private static readonly string[] PendingStatuses = [OfflineSaleEntry.PendingSync, OfflineSaleEntry.Syncing];

    // 2026-10-29: в счётчик «В очереди» входят и операции без связи (возврат, оплата долга) — владелец видит всё, что ещё не дошло до сервера.
    public static int PendingCount => OfflineDatabase.LoadByStatus(PendingStatuses).Count(IsPendingLike) + SafeOpsCount(() => OfflineOperationsStore.PendingCount);

    public static int SyncedCount => LoadAll().Count(s =>
        string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase));

    // 2026-10-29 (аварийный аудит кассы, п. 8): считается и показывается в шапке («Отклонено: M»), поэтому выборка по статусу, а не вся таблица.
    public static int FailedCount => OfflineDatabase.LoadByStatus([OfflineSaleEntry.Failed]).Count(s => !s.IsAutonomous) + SafeOpsCount(() => OfflineOperationsStore.FailedCount);

    private static int SafeOpsCount(Func<int> count)
    {
        try { return count(); }
        catch (Exception ex) { PosLogger.Log($"OFFLINE: счётчик операций без связи не прочитан: {ex.Message}", "WARNING"); return 0; }
    }

    /// <summary>2026-10-29: чеки «в долг», оформленные без связи, по клиенту — для долга клиента в кэше, пока они не дошли до сервера.
    /// includeRejected — считать и отклонённые («Требует проверки»): они могут вернуться в очередь.</summary>
    public static IReadOnlyList<OfflineSaleEntry> LoadDebtSalesForClient(string clientId, bool includeRejected)
    {
        var statuses = includeRejected
            ? new[] { OfflineSaleEntry.PendingSync, OfflineSaleEntry.Syncing, OfflineSaleEntry.Failed }
            : PendingStatuses;
        return OfflineDatabase.LoadByStatus(statuses)
            .Where(e => e.OfflineDebt && !e.IsAutonomous && string.Equals(e.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<OfflineSaleEntry> LoadPendingForSync() =>
        OfflineDatabase.LoadByStatus(PendingStatuses)
            .Where(IsPendingLike)
            .OrderBy(x => x.CreatedAt)
            .ToList();

    public static OfflineSaleEntry? TryGetById(string id) => OfflineDatabase.TryGetById(id);

    public static void Update(string id, Action<OfflineSaleEntry> update)
    {
        lock (UpdateGate)
        {
            var entry = TryGetById(id);
            if (entry == null)
                return;
            update(entry);
            OfflineDatabase.UpdateEntry(entry);
        }
    }

    public static void MarkSyncing(string id)
    {
        Update(id, entry =>
        {
            entry.Status = OfflineSaleEntry.Syncing;
            entry.LastAttemptAt = DateTimeOffset.Now;
            entry.LastError = null;
        });
    }

    public static void MarkSynced(string id, string? saleId = null)
    {
        // 2026-10-29 (ревью, В-1): серверный id чека записывается в карту ДО отметки «Synced» и ДО снятия записи из очереди; ошибка записи карты не глотается —
        // чек остаётся в очереди (повтор вернёт «replayed»), а зависимый возврат не зависает навсегда.
        var knownServerId = !string.IsNullOrWhiteSpace(saleId) ? saleId : TryGetById(id)?.SyncedSaleId;
        OfflineOperationsStore.NoteSaleSynced(id, knownServerId);
        OfflineSaleEntry? snapshot = null;
        Update(id, entry =>
        {
            entry.Status = OfflineSaleEntry.Synced;
            entry.SyncedAt = DateTimeOffset.Now;
            entry.LastAttemptAt = entry.SyncedAt;
            entry.LastError = null;
            if (!string.IsNullOrWhiteSpace(saleId))
                entry.SyncedSaleId = saleId.Trim();
            snapshot = entry;
        });

        // 2026-10-29 (возврат / оплата долга без связи): чек очереди ушёл — запоминаем его серверный id (по нему отправятся возврат и оплата долга),
        // долг клиента в кэше становится подтверждённым, график долга уходит на сервер. Любой сбой здесь — только в журнал: чек уже на сервере.
        try
        {
            if (snapshot is { IsAutonomous: false } sold)
                RememberSyncedSaleForReturns(sold, saleId ?? sold.SyncedSaleId);
            if (snapshot is { OfflineDebt: true } debt)
            {
                OfflineClientCache.ApplyConfirmed(debt.ClientId, debt.Id, debt.DebtAmount);
                ApplyDebtScheduleAfterSync(debt, saleId ?? debt.SyncedSaleId);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: после отправки чека {id} не обновлены связанные данные (карта чеков/долг клиента): {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-29: чек очереди ушёл на сервер — касса запоминает его (итог, оплата, строки) в журнале возвратов под серверным id, как для чека, проведённого
    /// со связью: возврат по нему можно оформить и без связи (запись очереди после отправки удаляется, а строк чека больше нигде нет).</summary>
    private static void RememberSyncedSaleForReturns(OfflineSaleEntry entry, string? serverSaleId)
    {
        if (string.IsNullOrWhiteSpace(serverSaleId))
            return;
        var snap = OfflineRefundPlanner.FromQueueEntry(entry);
        if (snap is null)
            return;
        var id = serverSaleId!.Trim();
        SaleReturnLedger.RememberPayment(id, snap.Total, snap.Method, snap.PaidCash, snap.PaidNonCash, null, null, snap.LinesTotal, entry.CreatedAt,
            clientName: entry.DebtClientName, clientPhone: entry.DebtClientPhone, shiftId: entry.ShiftId); // 2026-10-30: смена — для Z-отчёта без связи
        SaleReturnLedger.RememberLinesIfMissing(id,
            snap.Lines.Select(l => new SaleLedgerLine { LineId = l.Key, Name = l.Name, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Total = l.LineTotal }).ToList(),
            snap.LinesTotal, entry.CreatedAt);
    }

    private static void ApplyDebtScheduleAfterSync(OfflineSaleEntry entry, string? saleId)
    {
        if (string.IsNullOrWhiteSpace(entry.DebtScheduleJson) || string.IsNullOrWhiteSpace(saleId) || string.IsNullOrWhiteSpace(entry.ClientId))
            return;
        try
        {
            var plan = System.Text.Json.JsonSerializer.Deserialize<NurMarketKassa.Core.Contracts.DebtSchedulePlan>(entry.DebtScheduleJson);
            if (plan is null)
                return;
            var clientId = entry.ClientId!;
            var sale = saleId!;
            // Как и после обычной продажи в долг: график ставится отдельным запросом, ошибки — только в журнал (DebtDueDateSync).
            _ = Task.Run(() => DebtDueDateSync.ApplyAsync(clientId, sale, plan));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: график долга чека {entry.Id} не разобран: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Возвращает отклонённые чеки в очередь на выгрузку.
    ///
    /// До 2026-09-23 выхода из статуса failed не было вообще: MarkFailed(retryable: false)
    /// ставил его, LoadPendingForSync такие записи не выбирал, и ни одна строка кода их оттуда
    /// не доставала. Отказ сервера один раз — и деньги взяты, а продажи в NurCRM нет никогда,
    /// причём молча. Отказ бывает и временным по сути (товар удалили на сайте, а потом вернули),
    /// поэтому решение о повторе принимает владелец кнопкой, а не автоматика: молча повторять
    /// то, что сервер уже отверг, — прямой путь к дублям.</summary>
    /// <returns>Сколько чеков вернулось в очередь.</returns>
    public static int RequeueFailed()
    {
        var failed = OfflineDatabase.LoadByStatus([OfflineSaleEntry.Failed]);
        var count = 0;
        foreach (var entry in failed)
        {
            // Уже проведённые на сервере не трогаем: их повтор и есть дубль.
            if (entry.CheckoutCompleted)
                continue;

            Update(entry.Id, e =>
            {
                e.Status = OfflineSaleEntry.PendingSync;
                e.LastError = null;
                e.ReviewNeeded = false;
                // 2026-10-29 (аварийный аудит кассы, п. 4 и 8): новый круг — счётчик отложений и признак оповещения владельца заново.
                e.ReplayDeferrals = 0;
                e.OwnerNotified = false;
                e.AutoRetries = 0;
            });
            count++;
        }

        if (count > 0)
            PosLogger.Log($"OFFLINE: {count} отклонённых чек(ов) возвращены в очередь вручную.", "OFFLINE");

        return count;
    }

    /// <summary>2026-10-29: кнопка «Повторить отправку» в «Истории чеков» — один чек из «Ошибки отправки» обратно в очередь (то же, что
    /// <see cref="RequeueFailed"/>, но по одному). false — чека нет, он не в «Ошибке отправки» или уже проведён на сервере.</summary>
    public static bool RequeueFailedById(string id)
    {
        var entry = TryGetById(id);
        if (entry == null || entry.IsAutonomous || entry.CheckoutCompleted
            || !string.Equals(entry.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase))
            return false;

        Update(id, e =>
        {
            e.Status = OfflineSaleEntry.PendingSync;
            e.ReplayDeferrals = 0;
            e.OwnerNotified = false;
            e.AutoRetries = 0;
            e.ReviewNeeded = false;
        });
        PosLogger.Log($"OFFLINE: чек {id} возвращён в очередь кнопкой «Повторить отправку».", "OFFLINE");
        return true;
    }

    /// <summary>2026-10-29 (жалоба «чеки сами не уходят при возвращении связи»): «Ошибка отправки» раньше была тупиком — только руками из
    /// «Некорректных чеков». Теперь касса сама возвращает такой чек в очередь до трёх раз (паузы 3 мин / 15 мин / 1 час, см.
    /// <see cref="ReplayErrorPolicy"/>): повтор безопасен — досылка идёт тем же ключом, проведённую продажу сервер вернёт как повтор, а не
    /// создаст вторую. «Требует проверки» (неясно, прошла ли оплата) и проведённые чеки не трогаем. Причина прошлого отказа остаётся в записи
    /// до следующей попытки.</summary>
    /// <returns>Сколько чеков возвращено в очередь.</returns>
    public static int AutoRequeueFailed(int needsReviewDeferrals)
    {
        var count = 0;
        var now = DateTimeOffset.Now;
        foreach (var entry in OfflineDatabase.LoadByStatus([OfflineSaleEntry.Failed]))
        {
            if (entry.IsAutonomous || entry.CheckoutCompleted)
                continue;

            // 2026-10-29: «Требует проверки» (долг без связи: лимит, клиент удалён…) сам не повторяется — решает человек кнопкой.
            if (entry.ReviewNeeded)
                continue;

            // 2026-10-29 (приёмка 1.17.65, Б-1): старый чек (старше суток или из закрытой смены) молча не повторяем — остаётся
            // «Ошибкой отправки»; один раз дописываем пояснение, дальше решает человек кнопкой «Повторить отправку».
            if (IsStaleForAutoRetry(entry, now))
            {
                if (entry.AutoRetries < ReplayErrorPolicy.AutoRetryDelays.Length
                    && entry.LastError?.Contains(StaleNoteMarker, StringComparison.Ordinal) != true)
                {
                    var note = StaleNote;
                    Update(entry.Id, e => e.LastError = string.IsNullOrWhiteSpace(e.LastError) ? note : e.LastError.Trim() + " " + note);
                }
                continue;
            }

            if (!ReplayErrorPolicy.ShouldAutoRequeue(entry.AutoRetries, entry.LastAttemptAt ?? entry.CreatedAt, now,
                    entry.CheckoutCompleted, entry.ReplayDeferrals, needsReviewDeferrals,
                    entry.CreatedAt, entry.CheckoutSubmittedAt, IsShiftClosed(entry)))
                continue;

            // Не больше одного чека за проход: пачка отклонённых чеков не занимает очередь разом.
            if (count >= ReplayErrorPolicy.AutoRetryMaxPerPass)
                break;

            Update(entry.Id, e =>
            {
                e.Status = OfflineSaleEntry.PendingSync;
                e.AutoRetries++;
            });
            count++;
        }

        if (count > 0)
            PosLogger.Log($"OFFLINE: {count} чек(ов) из «Ошибки отправки» возвращены в очередь автоматически.", "OFFLINE");

        return count;
    }

    private const string StaleNoteMarker = "[авто-стоп]";

    private static string StaleNote => StaleNoteMarker + " " + Tr.T(
        "Автоповтор не выполнен: чек старше суток или из закрытой смены. Проверьте, нет ли такой продажи на сервере, и нажмите «Повторить отправку».",
        "Автоматтык кайталоо жасалган жок: чек бир суткадан эски же жабылган сменадан. Серверде мындай сатуу жок экенин текшерип, «Жөнөтүүнү кайталоо» баскычын басыңыз.",
        "Automatic retry skipped: the receipt is over a day old or from a closed shift. Check that the sale is not on the server, then press “Retry sending”.",
        "Otomatik tekrar yapılmadı: fiş bir günden eski veya kapalı vardiyadan. Satışın sunucuda olmadığını kontrol edip «Göndermeyi tekrarla» düğmesine basın.",
        "Avtomatik qayta urinish bajarilmadi: chek bir kundan eski yoki yopilgan smenadan. Sotuv serverda yo'qligini tekshirib, «Yuborishni takrorlash» tugmasini bosing.");

    /// <summary>2026-10-29 (Б-1): смена чека известна и уже не текущая (закрыта). Неизвестная смена (нет id или нет открытой смены) — не «закрыта».</summary>
    private static bool IsShiftClosed(OfflineSaleEntry entry)
    {
        var shiftId = entry.ShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            return false;
        if (shiftId.StartsWith("offline-closed-", StringComparison.OrdinalIgnoreCase))
            return true;
        var active = PosApp.ActiveShiftId;
        return !string.IsNullOrWhiteSpace(active) && !string.Equals(active, shiftId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>2026-10-29 (Б-1): чек не годится для автоповтора (старше суток или из закрытой смены) — окно «История чеков» по этому признаку
    /// пишет «автоповтора не будет», а не «касса попробует ещё раз».</summary>
    public static bool IsStaleForAutoRetry(OfflineSaleEntry entry, DateTimeOffset? now = null) =>
        !ReplayErrorPolicy.IsFreshForAutoRetry(entry.CreatedAt, entry.CheckoutSubmittedAt, now ?? DateTimeOffset.Now, IsShiftClosed(entry));

    public static void MarkFailed(string id, string error, bool retryable)
    {
        Update(id, entry =>
        {
            entry.Status = retryable ? OfflineSaleEntry.PendingSync : OfflineSaleEntry.Failed;
            // 2026-10-29: «в долг» без связи, отклонённый сервером, — «Требует проверки» (не «Ошибка отправки»): автоповтора нет, решает человек.
            if (!retryable && entry.OfflineDebt)
                entry.ReviewNeeded = true;
            entry.LastAttemptAt = DateTimeOffset.Now;
            entry.LastError = string.IsNullOrWhiteSpace(error) ? "Неизвестная ошибка синхронизации." : error.Trim();
        });
    }

    /// <summary>2026-10-30: закрыть отклонённый чек без отправки: статус Synced (очередь и счётчики его не считают), запись и причина остаются.
    /// false — чека нет или он не «Ошибка отправки» (ждущие и проведённые не закрываем).</summary>
    public static bool CloseWithoutSending(string id, string reason)
    {
        var entry = TryGetById(id);
        if (entry == null || entry.IsAutonomous || !string.Equals(entry.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase))
            return false;
        Update(id, e =>
        {
            e.Status = OfflineSaleEntry.Synced;
            e.ClosedWithoutSendAt = DateTimeOffset.Now;
            e.ClosedReason = reason;
            e.ReviewNeeded = false;
        });
        PosLogger.Log($"OFFLINE: чек {id} закрыт без отправки на сервер; причина: {reason}; последняя ошибка: {entry.LastError}", "OFFLINE");
        return true;
    }

    public static void RemoveSynced(string id) => OfflineDatabase.RemoveIds(new[] { id });

    // 2026-09-10: автономные продажи (см. OfflineSaleEntry.IsAutonomous) никогда не считаются
    // "ждущими" — им некуда синхронизироваться, и они не должны ни попадать в PendingCount
    // (баннер "В очереди" вводит в заблуждение), ни подхватываться SyncService.LoadPendingForSync
    // (иначе они могли бы однажды выгрузиться в чужой NurCRM-аккаунт, если тот же ПК потом
    // войдёт онлайн). Они остаются доступны для повторной печати через LoadAll().
    private static bool IsPendingLike(OfflineSaleEntry sale) =>
        !sale.IsAutonomous
        && (string.Equals(sale.Status, OfflineSaleEntry.PendingSync, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sale.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase));
}
