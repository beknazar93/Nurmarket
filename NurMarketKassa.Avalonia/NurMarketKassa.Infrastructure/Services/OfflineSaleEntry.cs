namespace NurMarketKassa.Services;

/// <summary>Оффлайн-продажа для последующей выгрузки на сервер (как очередь в PRINTER_EXE_CHAIN).</summary>
public sealed class OfflineSaleEntry
{
    public const string PendingSync = "pending_sync";
    public const string Syncing = "syncing";
    public const string Synced = "synced";
    public const string Failed = "failed";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>pending_sync | syncing | synced | failed.</summary>
    public string Status { get; set; } = PendingSync;

    public string PaymentMethod { get; set; } = "";

    public string? CashReceived { get; set; }

    public string? CartId { get; set; }

    public string? ShiftId { get; set; }

    public string? BranchId { get; set; }

    public string? CashboxId { get; set; }

    /// <summary>Консультант продажи (см. PosCheckoutRequest.ConsultantId) — чтобы продажа,
    /// пробитая без связи, не потеряла его при выгрузке.</summary>
    public string? ConsultantId { get; set; }

    public bool ConsultantCommissionEnabled { get; set; }

    public string? ConsultantCommissionPercent { get; set; }

    /// <summary>2026-10-27 (аудит кассы, Н-6): клиент, выбранный в окне оплаты, — чтобы чек, пробитый без связи, не потерял его
    /// при досылке (бонусы клиенту касса начисляет сразу, а на сервере продажа раньше уходила «без клиента»). Поле лежит в JSON
    /// записи (схема таблицы не меняется); у старых записей null — досылка как раньше.</summary>
    public string? ClientId { get; set; }

    public string CartJson { get; set; } = "{}";

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }

    public string? LastError { get; set; }

    public string? SyncedSaleId { get; set; }

    /// <summary>Server cart reused across retries so item upload can resume.</summary>
    public string? SyncCartId { get; set; }

    public int UploadedItemCount { get; set; }

    /// <summary>
    /// Set durably before checkout. If no response arrives, automatic replay is
    /// stopped because the server may already have accepted the payment.
    /// </summary>
    public DateTimeOffset? CheckoutSubmittedAt { get; set; }

    public bool CheckoutCompleted { get; set; }

    /// <summary>2026-09-28, BE-11: чек уже отправлялся одним запросом (POST pos/checkout/) с
    /// Idempotency-Key = <see cref="Id"/> — ответ мог не дойти. Такой чек досылается только тем
    /// же адресом и тем же ключом (даже если быстрый путь потом выключили в настройках): сервер
    /// вернёт уже проведённую продажу вместо второй.</summary>
    public bool QuickCheckoutAttempted { get; set; }

    /// <summary>2026-10-06: чек уходил пакетом (pos/checkout/batch/, SyncService.BatchReplay.cs). Пока сервер не дал
    /// по нему ясного ответа, чек досылается только пакетом и тем же ключом: ключи пакета и одиночной продажи
    /// сервер может хранить раздельно, и одиночная досылка такого чека могла бы создать вторую продажу.</summary>
    public bool BatchCheckoutAttempted { get; set; }

    /// <summary>2026-09-10: продажа сделана в автономном (офлайн, без NurCRM) режиме — для такого
    /// аккаунта сервера, на который можно выгрузить, не существует вообще, и запись никогда не
    /// должна попасть в очередь синхронизации (см. OfflinePendingSalesStore.IsPendingLike). Без
    /// этого флага запись выглядела бы как обычная "ждёт связи с интернетом" и, если этот же ПК
    /// когда-нибудь потом войдёт в реальный NurCRM-аккаунт, SyncService попытался бы выгрузить туда
    /// чужую (тестовую/локальную) продажу.</summary>
    public bool IsAutonomous { get; set; }

    /// <summary>2026-10-29 (аварийный аудит кассы, п. 4): сколько проходов досылки подряд чек отложили, потому что сервер не смог
    /// ответить, прошла ли его оплата. После предела чек уходит в «Требует проверки» (failed), а не держит всю очередь. Лежит в
    /// JSON записи (схема таблицы не меняется).</summary>
    public int ReplayDeferrals { get; set; }

    /// <summary>2026-10-30 («Некорректные чеки»: отклонённый чек висел вечно): чек закрыт вручную БЕЗ отправки на сервер (причина — в <see cref="ClosedReason"/>).
    /// Статус при этом Synced — очередь и счётчики его больше не считают; запись остаётся (не удаляется), в журнале кассы — строка о закрытии.
    /// Поля лежат в JSON записи (схема таблицы не меняется).</summary>
    public DateTimeOffset? ClosedWithoutSendAt { get; set; }

    public string? ClosedReason { get; set; }

    /// <summary>2026-10-29 (п. 8): владельцу уже отправлено оповещение, что этот чек отклонён/требует проверки (одно на чек).</summary>
    public bool OwnerNotified { get; set; }

    /// <summary>2026-10-29: сколько раз касса сама вернула этот чек из «Ошибки отправки» в очередь (до 3, с паузами 3 мин / 15 мин / 1 час,
    /// см. ReplayErrorPolicy). Кнопка «Повторить отправку» начинает счёт заново. Лежит в JSON записи (схема таблицы не меняется).</summary>
    public int AutoRetries { get; set; }

    // ── 2026-10-29: «в долг» без связи (владелец разрешил). Все поля лежат в JSON записи (схема таблицы не меняется); у старых записей — значения по умолчанию. ──

    /// <summary>Продажа «в долг», оформленная без связи: клиент и лимит проверены по кэшу кассы, на сервере долга ещё нет.</summary>
    public bool OfflineDebt { get; set; }

    /// <summary>Сколько из чека ушло в долг (итог минус предоплата) — для долга клиента в кэше, пока чек в очереди.</summary>
    public decimal DebtAmount { get; set; }

    public string? DebtClientName { get; set; }
    public string? DebtClientPhone { get; set; }

    /// <summary>Когда касса в последний раз получала данные этого клиента с сервера (метка «данные клиента от ЧЧ:ММ»).</summary>
    public DateTimeOffset? ClientDataAt { get; set; }

    /// <summary>Кассир подтвердил продажу при неизвестном лимите/сверх осторожности (см. OfflineDebtRules).</summary>
    public bool DebtConfirmedByCashier { get; set; }

    /// <summary>График долга (DebtSchedulePlan в JSON): после отправки чека применяется к сделке долга (DebtDueDateSync).</summary>
    public string? DebtScheduleJson { get; set; }

    /// <summary>«Требует проверки» (а не обычная «Ошибка отправки»): сервер отказал по существу — лимит, клиент удалён и т. п. Чек не потерян,
    /// решает человек; автоповтор для таких чеков не включается.</summary>
    public bool ReviewNeeded { get; set; }
}
