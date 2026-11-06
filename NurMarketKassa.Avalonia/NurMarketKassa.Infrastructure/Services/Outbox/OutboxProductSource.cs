using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06: новые товары, заведённые без связи (PendingProductQueue, pending-products.json). «Отправить сейчас» = ForceNow: тот же воркер, тот же ключ повтора запроса
/// (IdempotencyKey) и та же проверка «не создан ли уже» — второго товара не будет. «Убрать» — товар остаётся в этой кассе, на сервер не пойдёт.</summary>
public sealed class ProductOutboxSource : IOutboxSource
{
    public const string SourceName = "product";
    private readonly PendingProductQueue _queue;

    /// <summary>queue — очередь (по умолчанию общая; тесты подставляют свою).</summary>
    public ProductOutboxSource(PendingProductQueue? queue = null) => _queue = queue ?? PendingProductQueue.Instance;
    public string Name => SourceName;

    /// <summary>Сколько ждём создания товара после «Отправить сейчас».</summary>
    public TimeSpan WaitFor { get; set; } = TimeSpan.FromSeconds(20);

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        IReadOnlyList<PendingProductJob> jobs;
        try { jobs = _queue.Snapshot(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь новых товаров не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        return jobs.Where(j => j.IsActive || j.Status == PendingProductStatus.Failed).Select(Map).ToList();
    }

    private static OutboxItem Map(PendingProductJob j)
    {
        var failed = j.Status == PendingProductStatus.Failed;
        var r = j.Request;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Barcode))
            details.Add(Tr.T("Штрихкод: ", "Штрихкод: ", "Barcode: ", "Barkod: ", "Shtrixkod: ") + r.Barcode);
        if (r.Quantity > 0)
            details.Add(Tr.T("Остаток: ", "Калдык: ", "Stock: ", "Stok: ", "Qoldiq: ") + OutboxFormat.Qty(r.Quantity) + " " + r.Unit);
        return new OutboxItem
        {
            Source = SourceName,
            Id = j.TempId,
            Kind = OutboxKind.NewProduct,
            Title = string.IsNullOrWhiteSpace(r.Name) ? "—" : r.Name,
            Amount = r.Price is { } p ? (decimal)p : null,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Local)),
            Status = failed ? OutboxStatus.Error : OutboxStatus.Waiting,
            Reason = j.LastError,
            Attempts = j.Attempts + j.HardAttempts,
            Details = string.Join(" · ", details),
            Hint = failed
                ? Tr.T("Сервер не принял товар. «Повторить» — после исправления данных на сайте; «Убрать» — товар останется только в этой кассе.", "Сервер товарды кабыл албады. «Кайталоо» — сайтта маалыматтарды оңдоп алгандан кийин; «Алып салуу» — товар ушул кассада гана калат.",
                    "The server did not accept the product. “Retry” — after fixing the data on the website; “Remove” — the product stays only in this till.", "Sunucu ürünü kabul etmedi. «Tekrar dene» — verileri sitede düzelttikten sonra; «Kaldır» — ürün yalnızca bu kasada kalır.", "Server tovarni qabul qilmadi. «Qayta urinish» — saytda ma'lumotlarni tuzatgach; «Olib tashlash» — tovar faqat shu kassada qoladi.")
                : null,
            CanSend = !failed,
            CanRetry = failed,
            CanRemove = failed,
        };
    }

    public async Task SendAsync(string id, CancellationToken ct)
    {
        var queue = _queue;
        if (!queue.ForceNow(id))
            return;
        var until = DateTime.UtcNow + WaitFor;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct).ConfigureAwait(false);
            var job = queue.Snapshot().FirstOrDefault(j => string.Equals(j.TempId, id, StringComparison.OrdinalIgnoreCase));
            if (job is null || !job.IsActive)
                return;
        }
    }

    public bool Retry(string id) => _queue.Retry(id);

    public bool Remove(string id, string who) => _queue.Dismiss(id);

    public string? ExportText(string id)
    {
        var j = _queue.Snapshot().FirstOrDefault(x => string.Equals(x.TempId, id, StringComparison.OrdinalIgnoreCase));
        if (j is null)
            return null;
        var sb = new StringBuilder();
        sb.AppendLine($"{OutboxText.KindLabel(OutboxKind.NewProduct)}: {j.Request.Name}");
        sb.AppendLine("Временный id: " + j.TempId);
        sb.AppendLine("Создан: " + OutboxFormat.Time(new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Local))));
        sb.AppendLine("Штрихкод: " + (j.Request.Barcode ?? "—") + "; цена: " + (j.Request.Price?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "—"));
        sb.AppendLine("Статус: " + OutboxText.RawStatus(j.Status.ToString()) + (string.IsNullOrWhiteSpace(j.LastError) ? "" : "; ошибка: " + j.LastError));
        return sb.ToString();
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler() => changed();
        _queue.Changed += Handler;
        return new ActionSubscription(() => _queue.Changed -= Handler);
    }
}
