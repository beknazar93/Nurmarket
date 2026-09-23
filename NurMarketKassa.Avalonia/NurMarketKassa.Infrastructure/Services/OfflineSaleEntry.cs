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

    /// <summary>2026-09-10: продажа сделана в автономном (офлайн, без NurCRM) режиме — для такого
    /// аккаунта сервера, на который можно выгрузить, не существует вообще, и запись никогда не
    /// должна попасть в очередь синхронизации (см. OfflinePendingSalesStore.IsPendingLike). Без
    /// этого флага запись выглядела бы как обычная "ждёт связи с интернетом" и, если этот же ПК
    /// когда-нибудь потом войдёт в реальный NurCRM-аккаунт, SyncService попытался бы выгрузить туда
    /// чужую (тестовую/локальную) продажу.</summary>
    public bool IsAutonomous { get; set; }
}
