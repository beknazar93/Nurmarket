namespace NurMarketKassa.Services;

// 2026-11-20 (С11 «Коллекции», волна А — только интерфейсы): места, куда позже подключится серверное хранение заказов под клиента и смет.
// Сейчас НИКТО эти интерфейсы не вызывает и не реализует — касса работает с локальными файлами (parts-orders.json, estimates.json), сервер не трогается.
// Подключать нельзя, пока нет чёткой семантики предоплаты: у customer-orders/ (Б-60, ответ «200, пусто», 01.11) формат и операции не описаны.
// Правила из ТЗ «Синхронизация»: предоплата — операция «получено/возвращено» (не значение), статусы — конечный автомат, слияние по записи (updated_at + надгробие), Idempotency-Key на каждую операцию.

/// <summary>Серверное хранилище «Заказов под клиента» (будущее customer-orders/).</summary>
public interface IPartsOrdersRemoteStore
{
    /// <summary>Заказы, изменённые после <paramref name="sinceUtc"/> (null — все); удалённые приходят отдельным списком надгробий.</summary>
    Task<(IReadOnlyList<PartsOrder> Orders, IReadOnlyList<string> DeletedIds)> ListChangedAsync(DateTime? sinceUtc, CancellationToken ct = default);

    /// <summary>Создать или изменить заказ (кроме денег: предоплата идёт только операциями ниже).</summary>
    Task<PartsOrder> UpsertAsync(PartsOrder order, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Операция «предоплата получена» на сумму <paramref name="amount"/> (дельта, не итог).</summary>
    Task<PartsOrder> ReceivePrepaymentAsync(string orderId, decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Операция «предоплата возвращена клиенту».</summary>
    Task<PartsOrder> RefundPrepaymentAsync(string orderId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Перевод статуса (<see cref="PartsOrderStatus"/>); сервер не пускает «назад» из выданного/отменённого.</summary>
    Task<PartsOrder> SetStatusAsync(string orderId, string status, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>Серверное хранилище смет строймаркета (будущая коллекция документов или app-settings/nurmarket-kassa — решит Умар).</summary>
public interface IEstimateRemoteStore
{
    Task<(IReadOnlyList<EstimateDoc> Docs, IReadOnlyList<string> DeletedIds)> ListChangedAsync(DateTime? sinceUtc, CancellationToken ct = default);

    Task<EstimateDoc> UpsertAsync(EstimateDoc doc, string idempotencyKey, CancellationToken ct = default);

    Task DeleteAsync(string docId, string idempotencyKey, CancellationToken ct = default);
}
