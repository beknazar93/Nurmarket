using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-06: источники «Неотправленного» из Infrastructure. Каждый только ЧИТАЕТ свою очередь (форматы файлов и таблиц не менялись) и для отправки зовёт тот же код, что автоматика.
// Источники приложения (внесения/изъятия — файл cash_history.json лежит в проекте кассы) подключаются в OutboxSetup проекта кассы.

internal sealed class ActionSubscription : IDisposable
{
    private Action? _undo;
    public ActionSubscription(Action undo) => _undo = undo;
    public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
}

internal static class OutboxFormat
{
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Qty(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string ServiceMissing => Tr.T("Служба отправки сейчас не работает — запись уйдёт сама, когда касса подключится к серверу.",
        "Жөнөтүү кызматы азыр иштебей жатат — касса серверге туташканда жазуу өзү кетет.", "The sending service is not running now — the item will go by itself once the till connects to the server.",
        "Gönderme hizmeti şu an çalışmıyor — kasa sunucuya bağlandığında kayıt kendiliğinden gider.", "Yuborish xizmati hozir ishlamayapti — kassa serverga ulanganda yozuv o'zi ketadi.");
}

/// <summary>Чеки продаж из офлайн-очереди (таблица офлайн-продаж pos_local.db). Отправка — SyncService.SendScopedAsync: тот же ReplayOfflineSaleAsync и тот же ключ идемпотентности (id записи).</summary>
public sealed class SalesQueueOutboxSource : IOutboxBatchSource
{
    public const string SourceName = "sale";
    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        List<OfflineSaleEntry> rows;
        try { rows = OfflinePendingSalesStore.LoadAllUnsynced(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь чеков не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        return rows.Where(e => !e.IsAutonomous).Select(Map).ToList();
    }

    private static OutboxItem Map(OfflineSaleEntry e)
    {
        var failed = string.Equals(e.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase);
        var syncing = string.Equals(e.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase);
        var lines = new List<OutboxLine>();
        decimal total = 0;
        string? cashier = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(e.CartJson) ? "{}" : e.CartJson);
            var root = doc.RootElement;
            var totals = CartTotalsCalculator.Calculate(root);
            total = totals.LineCount > 0 ? (decimal)totals.TotalDue : 0m;
            foreach (var it in CartDisplayHelper.EnumerateItems(root))
                lines.Add(new OutboxLine(CartDisplayHelper.ItemName(it), OutboxFormat.Qty(CartDisplayHelper.LineQuantity(it)), CartDisplayHelper.LineTotal(it)));
            cashier = CartDisplayHelper.TryCashierName(root);
        }
        catch (JsonException)
        {
            // сумма и позиции — только для показа; чек от этого не страдает
        }

        string? hint = null;
        if (failed && e.TooOldForServer)
            hint = Tr.T("Чек старше 7 суток: сервер принимает такой только по PIN владельца — откройте «Некорректные чеки» и нажмите «Провести по PIN владельца».",
                "Чек 7 суткадан эски: сервер аны ээсинин PIN-коду менен гана кабыл алат — «Туура эмес чектер» бөлүмүн ачып, «Ээсинин PIN-коду менен өткөрүү» баскычын басыңыз.",
                "The receipt is older than 7 days: the server accepts it only with the owner's PIN — open “Irregular receipts” and press “Process with owner PIN”.",
                "Fiş 7 günden eski: sunucu bunu yalnızca sahibin PIN'iyle kabul eder — «Hatalı fişler»i açıp «Sahip PIN'iyle işle»ye basın.",
                "Chek 7 kundan eski: server uni faqat egasining PIN-kodi bilan qabul qiladi — «Noto'g'ri cheklar»ni ochib, «Egasining PIN-kodi bilan o'tkazish»ni bosing.");
        else if (failed && e.ReviewNeeded)
            hint = Tr.T("Чек не потерян: сверьте его с продажами на сервере. Если его там нет — «Повторить».", "Чек жоголгон жок: аны сервердеги сатуулар менен салыштырыңыз. Ал жерде жок болсо — «Кайталоо».",
                "The receipt is not lost: compare it with the sales on the server. If it is not there — “Retry”.", "Fiş kaybolmadı: sunucudaki satışlarla karşılaştırın. Orada yoksa — «Tekrar dene».", "Chek yo'qolmagan: uni serverdagi sotuvlar bilan solishtiring. U yerda yo'q bo'lsa — «Qayta urinish».");

        return new OutboxItem
        {
            Source = SourceName,
            Id = e.Id,
            Kind = OutboxKind.Sale,
            Title = Tr.T("Чек ", "Чек ", "Receipt ", "Fiş ", "Chek ") + OfflineOperationsService.ShortRef(e.Id) + " · " + e.CreatedAt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
            Subtitle = cashier,
            Amount = total,
            LinesCount = lines.Count,
            CreatedAt = e.CreatedAt,
            Status = failed ? OutboxStatus.Error : syncing ? OutboxStatus.Sending : OutboxStatus.Waiting,
            Reason = e.LastError,
            Attempts = e.AutoRetries + (e.LastAttemptAt.HasValue ? 1 : 0),
            LastAttemptAt = e.LastAttemptAt,
            Owner = cashier,
            Lines = lines,
            Details = PaymentText(e),
            Hint = hint,
            CanSend = !syncing && !failed,
            CanRetry = failed && !e.TooOldForServer && !e.CheckoutCompleted,
            CanRemove = false, // чек продажи — деньги: убрать нельзя никому
            CanSupport = true,
        };
    }

    private static string PaymentText(OfflineSaleEntry e)
    {
        var method = (e.PaymentMethod ?? "").Trim().ToLowerInvariant() switch
        {
            "cash" => Tr.T("наличные", "накталай", "cash", "nakit", "naqd"),
            "card" => Tr.T("карта", "карта", "card", "kart", "karta"),
            "transfer" or "bank" => Tr.T("перевод", "которуу", "transfer", "havale", "o'tkazma"),
            "" => "—",
            var other => other,
        };
        var text = Tr.T("Оплата: ", "Төлөм: ", "Payment: ", "Ödeme: ", "To'lov: ") + method;
        if (e.OfflineDebt)
            text += " · " + Tr.T("в долг", "карызга", "on credit", "veresiye", "qarzga") + (string.IsNullOrWhiteSpace(e.DebtClientName) ? "" : " (" + e.DebtClientName + ")");
        return text;
    }

    public Task SendAsync(string id, CancellationToken ct) => SendManyAsync(new[] { id }, ct);

    /// <summary>Несколько чеков одним проходом SyncService (тот же пакетный/одиночный путь с ключом = id записи).</summary>
    public async Task SendManyAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var sync = SyncService.Current ?? throw new InvalidOperationException(OutboxFormat.ServiceMissing);
        await sync.SendScopedAsync(OutboxScope.Sales(ids.ToArray()), ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => OfflinePendingSalesStore.RequeueFailedById(id);

    public bool Remove(string id, string who) => false;

    public string? ExportText(string id)
    {
        var e = OfflinePendingSalesStore.TryGetById(id);
        if (e is null)
            return null;
        var item = Map(e);
        var sb = new StringBuilder();
        sb.AppendLine($"{item.KindLabel}: {item.Title}");
        sb.AppendLine("ID записи (ключ повтора): " + e.Id);
        sb.AppendLine("Создан: " + OutboxFormat.Time(e.CreatedAt));
        sb.AppendLine("Сумма: " + OutboxFormat.Money(item.Amount ?? 0m));
        sb.AppendLine(item.Details ?? "");
        if (e.ClientId is not null)
            sb.AppendLine("Клиент: " + e.ClientId);
        sb.AppendLine("Смена: " + (e.ShiftId ?? "—"));
        sb.AppendLine("Статус: " + OutboxText.RawStatus(e.Status) + (string.IsNullOrWhiteSpace(e.LastError) ? "" : "; ошибка: " + e.LastError));
        foreach (var l in item.Lines)
            sb.AppendLine($"  {l.Name} × {l.Quantity} = {l.Amount}");
        sb.AppendLine("Данные корзины: " + e.CartJson);
        return sb.ToString();
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler() => changed();
        OfflinePendingSalesStore.Appended += Handler;
        return new ActionSubscription(() => OfflinePendingSalesStore.Appended -= Handler);
    }
}

/// <summary>Возвраты и оплаты долга, оформленные без связи (offline_operations.json). Отправка — SyncService.SendScopedAsync → OfflineOperationsReplayer: тот же ключ (Id операции) и то же тело.</summary>
public sealed class OfflineOperationsOutboxSource : IOutboxBatchSource
{
    public const string SourceName = "op";
    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        List<OfflineOperationEntry> ops;
        try { ops = OfflineOperationsStore.LoadAll(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь операций не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        return ops.Where(o => o.Status != OfflineOperationEntry.Synced && o.LastError != OfflineOperationsStore.CanceledMarker).Select(Map).ToList();
    }

    private static OutboxItem Map(OfflineOperationEntry o)
    {
        var failed = o.Status == OfflineOperationEntry.Failed;
        var refund = o.Kind == OfflineOperationEntry.KindRefund;
        string? reason = o.LastError;
        if (!failed && string.IsNullOrWhiteSpace(reason) && WaitsForReceipt(o))
            reason = Tr.T("Ждёт отправки чека, к которому относится.", "Тиешелүү чектин жөнөтүлүшүн күтөт.", "Waiting for the receipt it belongs to to be sent.", "Ait olduğu fişin gönderilmesini bekliyor.", "Tegishli chek yuborilishini kutmoqda.");
        return new OutboxItem
        {
            Source = SourceName,
            Id = o.Id,
            Kind = refund ? OutboxKind.Return : OutboxKind.DebtPayment,
            Title = refund
                ? Tr.T("Возврат по чеку ", "Чек боюнча кайтаруу ", "Return for receipt ", "Fiş iadesi ", "Chek bo'yicha qaytarish ") + (o.ReceiptNumber ?? OfflineOperationsService.ShortRef(o.SaleRef))
                : Tr.T("Оплата долга: ", "Карыз төлөмү: ", "Debt payment: ", "Borç ödemesi: ", "Qarz to'lovi: ") + (o.ClientName ?? "—"),
            Subtitle = o.CashierName,
            Amount = o.Amount,
            LinesCount = o.Lines.Count == 0 ? null : o.Lines.Count,
            CreatedAt = o.CreatedAt,
            Status = failed ? OutboxStatus.Error : o.Status == OfflineOperationEntry.Syncing ? OutboxStatus.Sending : OutboxStatus.Waiting,
            Reason = reason,
            Attempts = o.Attempts,
            LastAttemptAt = o.LastAttemptAt,
            Owner = o.CashierName,
            Lines = o.Lines.Select(l => new OutboxLine(l.Name, OutboxFormat.Qty(l.Quantity), OutboxFormat.Money(l.Amount))).ToList(),
            Details = refund
                ? Tr.T("Выдано наличными: ", "Накталай берилди: ", "Paid in cash: ", "Nakit verildi: ", "Naqd berildi: ") + OutboxFormat.Money(o.Cash)
                : Tr.T("Способ: ", "Ыкма: ", "Method: ", "Yöntem: ", "Usul: ") + o.Method,
            Hint = failed && o.ReviewNeeded
                ? Tr.T("Проверьте операцию на сервере. Если её там нет — «Повторить»; если уже учтена вручную — «Убрать из очереди».", "Операцияны сервер аркылуу текшериңиз. Ал жерде жок болсо — «Кайталоо»; кол менен эсепке алынган болсо — «Кезектен алып салуу».",
                    "Check the operation on the server. If it is not there — “Retry”; if it is already accounted for by hand — “Remove from queue”.", "İşlemi sunucuda kontrol edin. Orada yoksa — «Tekrar dene»; elle hesaba katıldıysa — «Kuyruktan kaldır».", "Operatsiyani serverda tekshiring. U yerda yo'q bo'lsa — «Qayta urinish»; qo'lda hisobga olingan bo'lsa — «Navbatdan olib tashlash».")
                : null,
            CanSend = !failed && o.Status != OfflineOperationEntry.Syncing,
            CanRetry = failed,
            CanRemove = failed,
            CanSupport = false,
        };
    }

    private static bool WaitsForReceipt(OfflineOperationEntry o) =>
        o.DependsOn.Any(d => OfflinePendingSalesStore.TryGetById(d) is { } s && !s.IsAutonomous && !string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase));

    public Task SendAsync(string id, CancellationToken ct) => SendManyAsync(new[] { id }, ct);

    public async Task SendManyAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var sync = SyncService.Current ?? throw new InvalidOperationException(OutboxFormat.ServiceMissing);
        var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var op = OfflineOperationsStore.Get(id);
            if (op is null)
                continue;
            ops.Add(id);
            // Возврат по чеку из очереди уходит ПОСЛЕ чека: отправляем и сам чек (и более ранние операции, от которых зависит), иначе она только «ждёт».
            foreach (var dep in op.DependsOn)
            {
                if (OfflineOperationsStore.Get(dep) is { IsPendingLike: true })
                    ops.Add(dep);
                else if (OfflinePendingSalesStore.TryGetById(dep) is { IsAutonomous: false } s && string.Equals(s.Status, OfflineSaleEntry.PendingSync, StringComparison.OrdinalIgnoreCase))
                    sales.Add(dep);
            }
        }
        if (ops.Count == 0)
            return;
        await sync.SendScopedAsync(new OutboxScope(sales, ops, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => OfflineOperationsStore.Requeue(id);

    public bool Remove(string id, string who) => OfflineOperationsStore.CloseWithoutSending(id, who);

    public string? ExportText(string id)
    {
        var o = OfflineOperationsStore.Get(id);
        if (o is null)
            return null;
        var item = Map(o);
        var sb = new StringBuilder();
        sb.AppendLine($"{item.KindLabel}: {item.Title}");
        sb.AppendLine("ID операции (ключ повтора): " + o.Id);
        sb.AppendLine("Создана: " + OutboxFormat.Time(o.CreatedAt));
        sb.AppendLine("Сумма: " + OutboxFormat.Money(o.Amount));
        sb.AppendLine("Кассир: " + (o.CashierName ?? "—") + "; смена: " + (o.ShiftId ?? "—"));
        sb.AppendLine("Статус: " + OutboxText.RawStatus(o.Status) + (string.IsNullOrWhiteSpace(o.LastError) ? "" : "; ошибка: " + o.LastError));
        foreach (var l in o.Lines)
            sb.AppendLine($"  {l.Name} × {OutboxFormat.Qty(l.Quantity)} = {OutboxFormat.Money(l.Amount)}");
        return sb.ToString();
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler() => changed();
        OfflineOperationsStore.Changed += Handler;
        return new ActionSubscription(() => OfflineOperationsStore.Changed -= Handler);
    }
}

/// <summary>Смены, закрытые на кассе, но не принятые сервером (pending_shift_closes.json). Отправка — тот же FlushPendingShiftClosesAsync, только для выбранной смены.</summary>
public sealed class ShiftCloseOutboxSource : IOutboxSource
{
    public const string SourceName = "shift";
    public string Name => SourceName;

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        List<PendingShiftCloseStore.Entry> rows;
        HashSet<string> queuedShifts;
        try
        {
            rows = PendingShiftCloseStore.LoadAll();
            queuedShifts = rows.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : OfflinePendingSalesStore.LoadPendingForSync().Select(s => s.ShiftId ?? "").Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь закрытий смен не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }

        var company = CompanyInfoService.LastCompany?.Id;
        return rows.Select(e =>
        {
            var foreign = !string.IsNullOrEmpty(e.CompanyId) && !string.IsNullOrEmpty(company) && !string.Equals(e.CompanyId, company, StringComparison.OrdinalIgnoreCase);
            var reason = e.LastError;
            if (queuedShifts.Contains(e.ShiftId))
                reason = Tr.T("Ждёт отправки чеков этой смены — закрытие уйдёт после них.", "Бул сменанын чектеринин жөнөтүлүшүн күтөт — жабуу алардан кийин кетет.", "Waiting for this shift's receipts to be sent — the closing goes after them.", "Bu vardiyanın fişlerinin gönderilmesini bekliyor — kapanış onlardan sonra gider.", "Shu smena cheklari yuborilishini kutmoqda — yopish ulardan keyin ketadi.");
            return new OutboxItem
            {
                Source = SourceName,
                Id = e.ShiftId,
                Kind = OutboxKind.ShiftClose,
                Title = Tr.T("Закрытие смены ", "Сменаны жабуу ", "Closing shift ", "Vardiya kapatma ", "Smenani yopish ") + (e.ShiftId.Length > 8 ? e.ShiftId[..8] : e.ShiftId),
                Amount = decimal.TryParse(e.ClosingCash, NumberStyles.Any, CultureInfo.InvariantCulture, out var cash) ? cash : null,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(e.ClosedLocallyAtUtc, DateTimeKind.Utc)),
                Status = OutboxStatus.Waiting,
                Reason = reason,
                Attempts = e.Attempts,
                Details = Tr.T("Наличных при закрытии: ", "Жабууда накталай: ", "Cash at closing: ", "Kapanışta nakit: ", "Yopishda naqd: ") + (e.ClosingCash ?? "—"),
                CanSend = !foreign && !queuedShifts.Contains(e.ShiftId),
                CanRetry = false,
                CanRemove = false,
            };
        }).ToList();
    }

    public async Task SendAsync(string id, CancellationToken ct)
    {
        var sync = SyncService.Current ?? throw new InvalidOperationException(OutboxFormat.ServiceMissing);
        await sync.SendScopedAsync(new OutboxScope(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }), ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => false;

    public bool Remove(string id, string who) => false;

    public string? ExportText(string id)
    {
        var e = PendingShiftCloseStore.TryGet(id);
        return e is null ? null : $"Закрытие смены {e.ShiftId}\nЗакрыта на кассе: {OutboxFormat.Time(new DateTimeOffset(DateTime.SpecifyKind(e.ClosedLocallyAtUtc, DateTimeKind.Utc)))}\nНаличных: {e.ClosingCash ?? "—"}\nПопыток: {e.Attempts}\nОшибка: {e.LastError ?? "—"}";
    }

    public IDisposable Subscribe(Action changed) => new ActionSubscription(() => { });
}

/// <summary>Приходы (накладные) в фоне — BackgroundReceivingQueue. «Отправить сейчас» = ForceNow: тот же воркер, по одной задаче, с прежним ключом прихода.</summary>
public sealed class ReceivingOutboxSource : IOutboxSource
{
    public const string SourceName = "recv";
    private readonly BackgroundReceivingQueue _queue;

    /// <summary>queue — очередь (по умолчанию общая; тесты подставляют свою с папкой во временном каталоге).</summary>
    public ReceivingOutboxSource(BackgroundReceivingQueue? queue = null) => _queue = queue ?? BackgroundReceivingQueue.Instance;

    public string Name => SourceName;

    /// <summary>Сколько ждём завершения задачи после «Отправить сейчас» (потом — «осталась в очереди», итог покажет окно).</summary>
    public TimeSpan WaitFor { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        IReadOnlyList<ReceivingJob> jobs;
        try { jobs = _queue.Snapshot(allCompanies: false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Неотправленное: очередь приходов не прочитана ({ex.Message}).", "WARNING");
            return Array.Empty<OutboxItem>();
        }
        return jobs.Where(j => j.Status != ReceivingJobStatus.Done).Select(Map).ToList();
    }

    private static OutboxItem Map(ReceivingJob j)
    {
        var error = j.Status is ReceivingJobStatus.Failed or ReceivingJobStatus.NeedsCheck or ReceivingJobStatus.Partial;
        var sending = j.Status is ReceivingJobStatus.Sending or ReceivingJobStatus.Finishing;
        var reasonParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(j.LastError))
            reasonParts.Add(ReceivingServerMessages.Humanize(j.LastError) ?? j.LastError!);
        foreach (var l in j.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Error)).Take(3))
            reasonParts.Add(l.ProductName + ": " + (ReceivingServerMessages.Humanize(l.Error) ?? l.Error));
        if (reasonParts.Count == 0 && !error)
            reasonParts.Add(j.StatusText);
        var rejected = error && j.IsRejectedByServer;
        var supplier = j.Supplier is { Name.Length: > 0 } s ? s.Name : null;
        return new OutboxItem
        {
            Source = SourceName,
            Id = j.Id,
            Kind = OutboxKind.Receiving,
            // 2026-11-06 (живой тест, В-3): без поставщика — так и пишем (в «Приходах в фоне» после досылки приход виден как «принят … · отправлен …», здесь — «принят …» в причине).
            Title = j.SourceText + " · " + (supplier ?? Tr.T("без поставщика", "жеткирүүчүсүз", "no supplier", "tedarikçisiz", "yetkazib beruvchisiz")),
            Subtitle = j.Employee,
            Amount = (decimal)j.Total,
            LinesCount = j.LineCount,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(j.ReceivedAt, DateTimeKind.Local)), // время ПРИЁМА (с поправкой часов), а не отправки
            Status = error ? OutboxStatus.Error : sending ? OutboxStatus.Sending : OutboxStatus.Waiting,
            Reason = string.Join("\n", reasonParts.Distinct()),
            Attempts = j.Attempts,
            LastAttemptAt = j.SentAt is { } sent ? new DateTimeOffset(DateTime.SpecifyKind(sent, DateTimeKind.Local)) : null,
            Owner = j.Employee,
            Lines = j.Lines.Select(l => new OutboxLine(l.ProductName, OutboxFormat.Qty(l.Quantity) + " " + l.Unit, OutboxFormat.Money((decimal)l.LineTotal),
                l.Outcome == ReceivingLineOutcome.Pending ? null : l.Outcome)).ToList(),
            Details = j.Supplier is not null && !j.PaidNow ? Tr.T("В долг поставщику", "Жөнөтүүчүгө карызга", "On credit from the supplier", "Tedarikçiye borç", "Yetkazib beruvchiga qarzga") : null,
            Hint = rejected
                ? Tr.T("Сервер отказал по существу — «Повторить» не поможет: верните строки в приёмку и исправьте данные.", "Сервер мазмуну боюнча баш тартты — «Кайталоо» жардам бербейт: саптарды кабыл алууга кайтарып, маалыматтарды оңдоңуз.",
                    "The server refused on the merits — “Retry” will not help: return the lines to receiving and fix the data.", "Sunucu esasa göre reddetti — «Tekrar dene» yardımcı olmaz: satırları mal kabule geri alıp verileri düzeltin.", "Server mohiyati bo'yicha rad etdi — «Qayta urinish» yordam bermaydi: qatorlarni qabulga qaytarib, ma'lumotlarni tuzating.")
                : null,
            CanSend = !error && !sending,
            CanRetry = error && !rejected,
            CanRemove = error,
            CanTakeBack = error && j.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Rejected or ReceivingLineOutcome.Pending),
        };
    }

    public async Task SendAsync(string id, CancellationToken ct)
    {
        var queue = _queue;
        if (!queue.ForceNow(id))
            return;
        await queue.WaitAsync(id, WaitFor).WaitAsync(ct).ConfigureAwait(false);
    }

    public bool Retry(string id) => _queue.Retry(id);

    public bool Remove(string id, string who) => _queue.Dismiss(id);

    public string? ExportText(string id)
    {
        var j = _queue.Get(id);
        if (j is null)
            return null;
        var sb = new StringBuilder();
        sb.AppendLine($"{OutboxText.KindLabel(OutboxKind.Receiving)}: {j.SourceText}" + (j.Supplier is { } s ? " · " + s.Name : ""));
        sb.AppendLine("ID задачи: " + j.Id);
        sb.AppendLine("Принят: " + OutboxFormat.Time(new DateTimeOffset(DateTime.SpecifyKind(j.ReceivedAt, DateTimeKind.Local))) + "; сумма: " + OutboxFormat.Money((decimal)j.Total));
        sb.AppendLine("Статус: " + OutboxText.RawStatus(j.Status) + (string.IsNullOrWhiteSpace(j.LastError) ? "" : "; ошибка: " + j.LastError));
        foreach (var l in j.Lines)
            sb.AppendLine($"  {l.ProductName} × {OutboxFormat.Qty(l.Quantity)} {l.Unit} по {OutboxFormat.Money((decimal)l.PurchasePrice)} [{OutboxText.RawStatus(l.Outcome)}]");
        return sb.ToString();
    }

    public IDisposable Subscribe(Action changed)
    {
        void Handler(ReceivingJob _) => changed();
        _queue.JobChanged += Handler;
        return new ActionSubscription(() => _queue.JobChanged -= Handler);
    }
}
