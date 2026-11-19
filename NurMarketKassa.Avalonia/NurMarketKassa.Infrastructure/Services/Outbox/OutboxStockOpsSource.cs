using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-14 (пакет Л-6): складские операции без связи в «Неотправленном» — списания и перемещения из StockOpsQueue и посчитанная, но не проведённая ревизия (черновик RevisionDraftStore).

/// <summary>Окружение очереди складских операций в работающей кассе (тесты подставляют свои <see cref="StockOpsDeps"/>).</summary>
public static class StockOpsRuntime
{
    /// <summary>Откуда взять клиент NurCRM; не задан — общий резолвер очереди приходов (его выставляет приложение при входе).</summary>
    public static Func<NurMarketApiClient?>? ApiResolver { get; set; }

    public static StockOpsDeps? Create()
    {
        var inventory = ProductActions.InventoryApi;
        var catalog = PosApp.CatalogApi;
        var client = (ApiResolver ?? BackgroundReceivingQueue.ApiResolver)?.Invoke();
        if (inventory is null || catalog is null || client is null)
            return null;
        return new StockOpsDeps
        {
            Inventory = inventory,
            ReadStock = async (productId, ct) =>
            {
                var detail = await catalog.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
                if (detail is not { } el)
                    return null;
                var mustWeigh = ProductActions.Find(productId)?.MustWeigh ?? false;
                return StockSyncService.ResolveStockQuantity(el, mustWeigh);
            },
            PostTransfer = async (bodyJson, key, ct) =>
            {
                using var doc = JsonDocument.Parse(bodyJson);
                return await client.PostWithIdempotencyKeyAsync("api/main/branch-transfers/", doc.RootElement.Clone(), key, ct, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            },
            OnWriteOffPosted = (job, serverStock) =>
            {
                try
                {
                    if (job.ProductId is { } id && ProductActions.Find(id) is { } product)
                    {
                        StockSyncService.ApplyQuantityToTileOnUi(product, serverStock, product.MustWeigh);
                        CatalogCacheService.PersistProductStock(id, serverStock, product.MustWeigh);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Склад без связи: остаток в каталоге не обновлён ({ex.Message}).", "WARNING");
                }
            },
        };
    }
}

/// <summary>Списание, которое нельзя провести сейчас из-за связи: остаток в кассе уменьшается сразу, операция уходит в очередь (<see cref="StockOpsQueue"/>).
/// Вызывается из ProductActions.ChangeStockAsync только для чистого списания («минус N»): «точное количество» и приход считаются от серверного остатка — без связи они опасны (затёрли бы чужие продажи).</summary>
public static class OfflineWriteOff
{
    public static ProductActions.Result TryQueue(CatalogProductTileVm product, double quantity, string reason, string? historyReason, string? actor)
    {
        var cached = product.Quantity;
        if (quantity > cached + 0.0005)
            return new ProductActions.Result(false, Tr.T(
                $"«{product.Title}»: связи с сервером нет, а в кассе по последним данным осталось {ProductActions.Qty(cached)} — списать {ProductActions.Qty(quantity)} нельзя. Обновите остаток, когда появится связь.",
                $"«{product.Title}»: сервер менен байланыш жок, кассада акыркы маалымат боюнча {ProductActions.Qty(cached)} калды — {ProductActions.Qty(quantity)} чыгарууга болбойт. Байланыш пайда болгондо калдыкты жаңыртыңыз.",
                $"“{product.Title}”: there is no connection to the server and by the last data the till has {ProductActions.Qty(cached)} — cannot write off {ProductActions.Qty(quantity)}. Refresh the stock when the connection returns.",
                $"«{product.Title}»: sunucuyla bağlantı yok ve son verilere göre kasada {ProductActions.Qty(cached)} kaldı — {ProductActions.Qty(quantity)} düşülemez. Bağlantı gelince stoğu güncelleyin.",
                $"«{product.Title}»: server bilan aloqa yo'q, kassada oxirgi ma'lumot bo'yicha {ProductActions.Qty(cached)} qoldi — {ProductActions.Qty(quantity)} chiqarib bo'lmaydi. Aloqa paydo bo'lganda qoldiqni yangilang."));

        var note = string.IsNullOrWhiteSpace(reason) ? "Списание" : reason.Trim();
        StockOpsQueue.Instance.EnqueueWriteOff(product.Id, product.Title, quantity, note, actor ?? PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId);

        // Остаток в кассе уменьшаем сразу (иначе то же количество продадут ещё раз); когда сервер проведёт — OnWriteOffPosted выставит серверное значение.
        var left = Math.Max(0, Math.Round(cached - quantity, 3));
        try
        {
            StockSyncService.ApplyQuantityToTileOnUi(product, left, product.MustWeigh);
            CatalogCacheService.PersistProductStock(product.Id, left, product.MustWeigh);
            // Журнал списаний и строка «Списания» смены — как у онлайн-списания.
            var unitCost = product.PurchasePrice > 0 ? product.PurchasePrice : LocalCartService.ParsePrice(product.PriceLine);
            WriteOffHistoryStore.Append(product.Id, product.Title, quantity, historyReason ?? reason, actor ?? PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId, unitCost > 0 ? unitCost : null);
            if (unitCost > 0)
                ShiftEventsStore.Record(ShiftEventsStore.KindWriteOff, PosApp.ActiveShiftId, ShiftEventsStore.OperationKey(product.Id + ":" + DateTime.UtcNow.Ticks), -quantity * unitCost, product.Title);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад без связи: локальные записи списания «{product.Title}» не сделаны полностью ({ex.Message}).", "WARNING");
        }

        return new ProductActions.Result(true, Tr.T(
            $"«{product.Title}»: списание {ProductActions.Qty(quantity)} принято без связи. Остаток в кассе уменьшен; на сервер оно уйдёт само, когда появится связь (см. «Неотправленное»).",
            $"«{product.Title}»: {ProductActions.Qty(quantity)} эсептен чыгаруу байланышсыз кабыл алынды. Кассадагы калдык азайды; байланыш пайда болгондо серверге өзү кетет («Жөнөтүлбөгөндөр» бөлүмүн караңыз).",
            $"“{product.Title}”: the write-off of {ProductActions.Qty(quantity)} was accepted offline. The till stock was reduced; it will go to the server by itself when the connection returns (see “Unsent”).",
            $"«{product.Title}»: {ProductActions.Qty(quantity)} düşüm bağlantısız kabul edildi. Kasadaki stok azaltıldı; bağlantı gelince sunucuya kendiliğinden gider («Gönderilmeyenler»e bakın).",
            $"«{product.Title}»: {ProductActions.Qty(quantity)} hisobdan chiqarish aloqasiz qabul qilindi. Kassadagi qoldiq kamaytirildi; aloqa paydo bo'lganda serverga o'zi ketadi («Yuborilmaganlar»ga qarang)."), Queued: true);
    }
}

/// <summary>Списания и перемещения без связи. «Отправить сейчас» = тот же проход очереди, что и автоматика (под её замком, с тем же ключом и номером документа).</summary>
public sealed class StockOpsOutboxSource : IOutboxSource
{
    public const string SourceName = "stockop";
    private readonly StockOpsQueue _queue;
    private readonly Func<StockOpsDeps?> _deps;

    public StockOpsOutboxSource(StockOpsQueue? queue = null, Func<StockOpsDeps?>? deps = null)
    {
        _queue = queue ?? StockOpsQueue.Instance;
        _deps = deps ?? StockOpsRuntime.Create;
    }

    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        try { return _queue.Snapshot().Select(Map).ToList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь складских операций не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
    }

    private static OutboxItem Map(StockOpJob j)
    {
        var failed = j.Status == StockOpStatus.Failed;
        var write = j.Kind == StockOpKind.WriteOff;
        return new OutboxItem
        {
            Source = SourceName,
            Id = j.Id,
            Kind = write ? OutboxKind.StockWriteOff : OutboxKind.StockTransfer,
            Title = write ? (j.ProductName ?? "—") + " × " + ProductActions.Qty(j.Quantity) : j.Title ?? "—",
            Subtitle = j.Actor,
            LinesCount = null,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Local)),
            Status = failed ? OutboxStatus.Error : j.Status == StockOpStatus.Sending ? OutboxStatus.Sending : OutboxStatus.Waiting,
            Reason = j.LastError,
            Attempts = j.Attempts,
            LastAttemptAt = j.LastAttemptAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Local)) : null,
            Owner = j.Actor,
            Details = write ? Tr.T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sebep: ") + (j.Reason ?? "—") : null,
            Hint = failed && j.StockConflict
                ? Tr.T("Конфликт остатка: на сервере товара меньше, чем списано. «Повторить» — после проверки остатка; «Убрать» — если списание уже не нужно (остаток в кассе поправьте вручную).",
                    "Калдык кармашуусу: серверде товар чыгарылгандан аз. «Кайра аракет» — калдыкты текшергенден кийин; «Алып салуу» — эсептен чыгаруу керек болбосо (кассадагы калдыкты кол менен оңдоңуз).",
                    "Stock conflict: the server has less than was written off. “Retry” — after checking the stock; “Remove” — if the write-off is no longer needed (fix the till stock by hand).",
                    "Stok çakışması: sunucuda düşülenden az var. «Tekrar dene» — stoğu kontrol ettikten sonra; «Kaldır» — düşüm artık gerekmiyorsa (kasadaki stoğu elle düzeltin).",
                    "Qoldiq to'qnashuvi: serverda chiqarilganidan kam. «Qayta urinish» — qoldiqni tekshirgach; «Olib tashlash» — hisobdan chiqarish kerak bo'lmasa (kassadagi qoldiqni qo'lda to'g'rilang).")
                : null,
            CanSend = !failed && j.Status != StockOpStatus.Sending,
            CanRetry = failed,
            CanRemove = failed,
        };
    }

    public async Task SendAsync(string id, CancellationToken ct)
    {
        var deps = _deps() ?? throw new InvalidOperationException(OutboxFormat.ServiceMissing);
        await _queue.ProcessOneAsync(id, deps, ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => _queue.Retry(id);

    public bool Remove(string id, string who) => _queue.Dismiss(id);

    public string? ExportText(string id)
    {
        var j = _queue.Get(id);
        if (j is null)
            return null;
        var sb = new StringBuilder();
        sb.AppendLine($"{OutboxText.KindLabel(j.Kind == StockOpKind.WriteOff ? OutboxKind.StockWriteOff : OutboxKind.StockTransfer)}: {Map(j).Title}");
        sb.AppendLine("ID записи: " + j.Id);
        sb.AppendLine("Принята: " + OutboxFormat.Time(new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Local))));
        if (j.Kind == StockOpKind.WriteOff)
            sb.AppendLine($"Товар: {j.ProductName} ({j.ProductId}); количество: {j.Quantity.ToString("0.###", CultureInfo.InvariantCulture)}; причина: {j.Reason}; документ ревизии на сервере: {j.SessionId ?? "—"}");
        else
            sb.AppendLine($"Ключ повтора: {j.IdempotencyKey}; тело: {j.BodyJson}");
        sb.AppendLine("Статус: " + j.Status + (string.IsNullOrWhiteSpace(j.LastError) ? "" : "; ошибка: " + j.LastError));
        return sb.ToString();
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler() => changed();
        _queue.Changed += Handler;
        return new ActionSubscription(() => _queue.Changed -= Handler);
    }
}

/// <summary>Ревизия, посчитанная и сохранённая на этой кассе, но не проведённая на сервере (черновик на диске). Только показ: проводит её окно «Склад → Ревизия» кнопкой «Применить»
/// (оно же обновляет остатки и считает поправку на продажи за время подсчёта — слать её отсюда вслепую нельзя, это затёрло бы продажи).</summary>
public sealed class RevisionDraftOutboxSource : IOutboxSource
{
    public const string SourceName = "revision";
    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        RevisionDraftInfo? info;
        try { info = RevisionDraftStore.Peek(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: черновик ревизии не прочитан ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        // Незаконченный подсчёт при живой связи — обычная работа, а не «неотправленное»: показываем, когда проведение уже начиналось или связи нет.
        if (info is null || info.Counted == 0 || !(info.PostingStarted || ServerOutageMonitor.IsOutage || OfflineModeHelper.UseLocalOperations))
            return Array.Empty<OutboxItem>();
        return
        [
            new OutboxItem
            {
                Source = SourceName,
                Id = "draft",
                Kind = OutboxKind.Revision,
                Title = Tr.T($"Ревизия от {info.StartedAt:dd.MM HH:mm}: посчитано {info.Counted} поз.", $"{info.StartedAt:dd.MM HH:mm} ревизиясы: {info.Counted} поз. эсептелди", $"Stocktake from {info.StartedAt:dd.MM HH:mm}: {info.Counted} items counted",
                    $"{info.StartedAt:dd.MM HH:mm} sayımı: {info.Counted} kalem sayıldı", $"{info.StartedAt:dd.MM HH:mm} inventarizatsiyasi: {info.Counted} ta pozitsiya sanaldi"),
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(info.StartedAt, DateTimeKind.Local)),
                Status = OutboxStatus.Waiting,
                Reason = info.PostingStarted
                    ? Tr.T("Проведение начато, но не закончено (оборвалась связь).", "Өткөрүү башталды, бирок аякталган жок (байланыш үзүлдү).", "Posting started but not finished (the connection dropped).", "İşleme başladı ama bitmedi (bağlantı koptu).", "O'tkazish boshlandi, lekin tugamadi (aloqa uzildi).")
                    : Tr.T("Подсчёт сохранён на этой кассе; на сервере остатки не менялись.", "Эсептөө ушул кассада сакталды; серверде калдыктар өзгөргөн жок.", "The count is saved on this till; stock on the server is unchanged.", "Sayım bu kasada saklandı; sunucuda stoklar değişmedi.", "Sanoq shu kassada saqlandi; serverda qoldiqlar o'zgarmagan."),
                Hint = Tr.T("Когда появится связь: «Склад» → «Ревизия» → «Продолжить» → «Применить». Касса сама пересчитает поправку на продажи за время подсчёта.",
                    "Байланыш пайда болгондо: «Кампа» → «Ревизия» → «Улантуу» → «Колдонуу». Касса эсептөө убагындагы сатууларга оңдоону өзү кайра эсептейт.",
                    "When the connection returns: “Warehouse” → “Stocktake” → “Continue” → “Apply”. The till recalculates the correction for sales made during the count.",
                    "Bağlantı gelince: «Depo» → «Sayım» → «Devam» → «Uygula». Kasa, sayım sırasındaki satışlar için düzeltmeyi kendisi yeniden hesaplar.",
                    "Aloqa paydo bo'lganda: «Ombor» → «Inventarizatsiya» → «Davom etish» → «Qo'llash». Kassa sanoq paytidagi sotuvlar uchun tuzatishni o'zi qayta hisoblaydi."),
                CanSend = false,
                CanRetry = false,
                CanRemove = false,
                CanCopy = false,
            },
        ];
    }

    public Task SendAsync(string id, CancellationToken ct) => Task.CompletedTask;

    public bool Retry(string id) => false;

    public bool Remove(string id, string who) => false;

    public string? ExportText(string id) => null;

    public IDisposable Subscribe(Action changed) => new ActionSubscription(() => { });
}
