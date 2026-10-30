using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: что касса делает с сервером при отправке операций без связи — три вызова. Реализация: <see cref="SalesOfflineOpsApi"/> (боевая) и
/// подмена в мини-прогоне (без сети).</summary>
public interface IOfflineOpsApi
{
    Task<JsonElement> GetSaleAsync(string saleId, CancellationToken ct);

    /// <summary>lines = null — вернуть чек целиком. Ключ идемпотентности — всегда ключ операции.</summary>
    Task<JsonElement> ReturnSaleAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, string idempotencyKey, CancellationToken ct);

    Task<PayDebtResult> PayDebtAsync(string clientId, double amount, string method, string? shiftId, string idempotencyKey, CancellationToken ct);
}

/// <summary>Боевая реализация: возврат — тот же POST, что у окна возврата (PosReturnSaleAsync), оплата долга — clients/{id}/pay-debt/ одной суммой.</summary>
public sealed class SalesOfflineOpsApi : IOfflineOpsApi
{
    private readonly ISalesApiService _sales;

    /// <summary>Боевой клиент оплаты долга (регистрируется в DI Avalonia; в Infrastructure его отдаёт провайдер из App).</summary>
    public static Func<ClientDebtsApiService?>? DebtApiProvider { get; set; }

    public SalesOfflineOpsApi(ISalesApiService sales) => _sales = sales;

    public Task<JsonElement> GetSaleAsync(string saleId, CancellationToken ct) => _sales.PosSaleGetAsync(saleId, ct);

    public Task<JsonElement> ReturnSaleAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, string idempotencyKey, CancellationToken ct)
    {
        // 2026-10-29 (ревью, Б-1): пустой (не null) список строк — ошибка вызова: PosReturnSaleAsync без items возвращает ВЕСЬ чек. «Весь чек» — только явный null.
        if (lines is { Count: 0 })
            throw new InvalidOperationException("Возврат без строк не отправляется (без items сервер вернул бы весь чек).");
        return _sales.PosReturnSaleAsync(saleId, lines, ct, idempotencyKey);
    }

    public Task<PayDebtResult> PayDebtAsync(string clientId, double amount, string method, string? shiftId, string idempotencyKey, CancellationToken ct)
    {
        var api = DebtApiProvider?.Invoke()
                  ?? throw new HttpRequestException("Клиент оплаты долга ещё не создан (касса запускается) — повтор на следующем проходе.");
        return api.PayDebtAsync(clientId, amount, method, shiftId, idempotencyKey, ct);
    }
}

/// <summary>Состояние чека очереди для зависимой операции.</summary>
public enum OfflineSaleState
{
    /// <summary>Ещё в очереди (ждёт отправки / отправляется).</summary>
    Pending,
    /// <summary>Сервер отклонил чек или он требует проверки.</summary>
    Rejected,
    /// <summary>Записи нет и серверного id нет: чек удалён из очереди без отправки.</summary>
    Gone,
    /// <summary>Чек уже отправлен (запись осталась со статусом Synced, а карта «локальный → серверный id» не записана) — восстанавливается по SyncedSaleId.</summary>
    Synced,
}

/// <summary>2026-10-29: отличает деловой отказ сервера (лимит долга, нечего возвращать, клиент удалён) от «запрос с этим ключом ещё выполняется».
/// У обоих бывает код 409; первый — окончательный отказ (чек уходит в «Требует проверки»), второй — повтор тем же ключом.</summary>
public static class OfflineRejection
{
    private static readonly string[] InProgressMarkers =
        ["in_progress", "in progress", "idempotency", "being processed", "еще выполняется", "ещё выполняется", "уже выполняется"];

    private static readonly string[] BusinessMarkers =
        ["credit_limit", "limit", "exceed", "refund_exceeds", "no_debt", "blocked", "not_found", "not found", "deleted", "archived", "inactive",
         "лимит", "превыш", "заблокир", "не найден", "удал", "нет долга", "больше", "уже возвращ"];

    /// <summary>Коды деловых отказов сервера (одинаково для 400, 403 и 409 — по ответу Умара без заголовка клиента сервер отдаёт то 400, то 409).</summary>
    private static readonly string[] BusinessCodes =
        ["refund_exceeds_paid", "shift_closed", "shift_not_owned", "shift_required", "no_debt", "amount_exceeds_debt", "credit_limit_exceeded", "method_not_allowed"];

    private static string RawText(ApiException ex)
    {
        try
        {
            return ((ex.Payload is { } p ? p.GetRawText() : "") + " " + ex.Message).ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            return ex.Message.ToLowerInvariant();
        }
    }

    /// <summary>Код смены в ответе: shift_closed / shift_not_owned / shift_required.</summary>
    public static bool IsShiftCode(ApiException ex) => RawText(ex) is var t && (t.Contains("shift_closed") || t.Contains("shift_not_owned") || t.Contains("shift_required"));

    /// <summary>Деловая причина простыми словами для известных кодов; иначе — текст сервера.</summary>
    public static string Describe(ApiException ex)
    {
        var t = RawText(ex);
        if (t.Contains("shift_closed"))
            return Tr.T("Смена, в которую принята операция, уже закрыта на сервере. Операцию не переносим в другую смену автоматически: проверьте на сайте и оформите вручную (деньги кассир уже принял или выдал).",
                "Операция кабыл алынган смена серверде жабылган. Операцияны башка сменага автоматтык которбойбуз: сайтта текшерип, кол менен жасаңыз (акчаны кассир алып койгон же берип койгон).",
                "The shift the operation was taken in is already closed on the server. The operation is not moved to another shift automatically: check the website and do it by hand (the cashier has already taken or paid out the money).",
                "İşlemin alındığı vardiya sunucuda zaten kapalı. İşlem otomatik olarak başka vardiyaya taşınmaz: siteden kontrol edip elle yapın (para kasiyer tarafından alındı veya verildi).",
                "Operatsiya qabul qilingan smena serverda allaqachon yopilgan. Operatsiya boshqa smenaga avtomatik ko'chirilmaydi: saytda tekshirib, qo'lda bajaring (pulni kassir olgan yoki bergan).");
        if (t.Contains("shift_not_owned"))
            return Tr.T("Смена принадлежит другому кассиру на сервере — операция не принята. Проверьте на сайте и оформите вручную.",
                "Смена серверде башка кассирге таандык — операция кабыл алынган жок. Сайтта текшерип, кол менен жасаңыз.",
                "The shift belongs to another cashier on the server — the operation was not accepted. Check the website and do it by hand.",
                "Vardiya sunucuda başka bir kasiyere ait — işlem kabul edilmedi. Siteden kontrol edip elle yapın.",
                "Smena serverda boshqa kassirga tegishli — operatsiya qabul qilinmadi. Saytda tekshirib, qo'lda bajaring.");
        if (t.Contains("shift_required"))
            return Tr.T("Серверу нужна открытая смена для этой операции. Откройте смену на сайте/в кассе и повторите отправку.",
                "Бул операция үчүн серверге ачык смена керек. Сменаны ачып, жөнөтүүнү кайталаңыз.",
                "The server needs an open shift for this operation. Open a shift and retry sending.",
                "Sunucu bu işlem için açık vardiya istiyor. Vardiyayı açıp göndermeyi tekrarlayın.",
                "Server bu operatsiya uchun ochiq smena talab qiladi. Smenani ochib, yuborishni takrorlang.");
        if (t.Contains("refund_exceeds_paid"))
            return Tr.T("Возврат больше, чем оплачено по чеку (часть уже возвращена на сайте или другой кассе). Сверьте чек.",
                "Кайтаруу чек боюнча төлөнгөндөн көп (бир бөлүгү сайтта же башка кассада кайтарылган). Чекти салыштырыңыз.",
                "The return is more than was paid on the receipt (part was already returned on the website or another till). Check the receipt.",
                "İade fişte ödenenden fazla (bir kısmı sitede veya başka kasada iade edilmiş). Fişi kontrol edin.",
                "Qaytarish chek bo'yicha to'langandan ko'p (bir qismi saytda yoki boshqa kassada qaytarilgan). Chekni solishtiring.");
        if (t.Contains("no_debt") || t.Contains("amount_exceeds_debt"))
            return Tr.T("У клиента нет такого долга (уже оплачен или сумма больше долга). Проверьте долг клиента на сайте.",
                "Кардардын мындай карызы жок (төлөнүп калган же сумма карыздан көп). Кардардын карызын сайтта текшериңиз.",
                "The client has no such debt (already paid, or the amount is above the debt). Check the client's debt on the website.",
                "Müşterinin böyle bir borcu yok (ödenmiş veya tutar borçtan fazla). Müşterinin borcunu sitede kontrol edin.",
                "Mijozda bunday qarz yo'q (to'langan yoki summa qarzdan ko'p). Mijoz qarzini saytda tekshiring.");
        return ex.Message;
    }

    /// <summary>Деловой отказ сервера (лимит долга, нечего возвращать, смена закрыта, клиент удалён) — окончательный: «Требует проверки». Обычное 409 без понятной причины
    /// («запрос с этим ключом ещё выполняется») — НЕ деловой отказ: повтор тем же ключом.</summary>
    public static bool IsBusinessConflict(ApiException ex)
    {
        if (ex.StatusCode is not (409 or 400 or 422 or 403))
            return false;
        var text = RawText(ex);
        if (BusinessCodes.Any(text.Contains))
            return true; // известный код — одинаково для 400, 403 и 409
        if (ex.StatusCode == 403)
            return false;
        if (ex.StatusCode == 409 && InProgressMarkers.Any(text.Contains))
            return false;
        if (ex.StatusCode == 409)
            return BusinessMarkers.Any(text.Contains);
        return true; // 400/422 с JSON-телом — отказ по существу (Classify уже проверил, что тело — JSON)
    }
}

/// <summary>2026-10-29: отправка операций без связи (возврат, оплата долга) после чеков очереди. Правила:
/// • порядок — как поставлены; операция ждёт своих зависимостей (чек очереди → возврат/оплата по нему; прошлая операция по тому же чеку);
/// • один ключ идемпотентности на все повторы (id операции), отметка «запрос мог уйти» пишется ДО запроса — повтор после обрыва/перезапуска безопасен;
/// • связь/сервер (5xx, 429, 408, 401, ключ ещё выполняется) — операция остаётся в очереди, проход прекращается;
/// • отказ по существу (лимит, уже возвращено, клиент удалён, чек не принят) — «Требует проверки» с причиной, владельцу оповещение; операция не теряется.</summary>
public static class OfflineOperationsReplayer
{
    /// <summary>Как узнать состояние чека очереди по его локальному id (в мини-прогоне подменяется).</summary>
    public static Func<string, (OfflineSaleState State, string? ServerId)> SaleStateProvider { get; set; } = id =>
    {
        var entry = OfflinePendingSalesStore.TryGetById(id);
        if (entry is null)
            return (OfflineSaleState.Gone, null);
        // 2026-10-29 (ревью, В-1): запись осталась «Synced» (касса упала до записи карты/снятия записи) — чек УЖЕ на сервере, берём его id отсюда.
        if (string.Equals(entry.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase))
            return (OfflineSaleState.Synced, entry.SyncedSaleId);
        return string.Equals(entry.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase)
            ? (OfflineSaleState.Rejected, null)
            : (OfflineSaleState.Pending, null);
    };

    /// <summary>Оповещение владельцу об операции «Требует проверки» (в боевой кассе — Telegram; в мини-прогоне — запись).</summary>
    public static Action<OfflineOperationEntry, string>? OwnerNotifier { get; set; } = NotifyOwnerViaTelegram;

    /// <summary>Сервер/связь недоступны — SyncService останавливает проход.</summary>
    public sealed class Outcome
    {
        public int Sent { get; set; }
        public int Waiting { get; set; }
        public int Review { get; set; }
        public bool StopPass { get; set; }
        /// <summary>Что оборвало проход (связь/сервер) — SyncService объявляет аварию сервера так же, как при досылке чеков.</summary>
        public Exception? Failure { get; set; }
    }

    public static async Task<Outcome> RunAsync(IOfflineOpsApi api, CancellationToken ct)
    {
        var outcome = new Outcome();
        List<OfflineOperationEntry> ops;
        try
        {
            ops = OfflineOperationsStore.LoadAll().Where(o => o.IsPendingLike).OrderBy(o => o.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE ops: очередь не прочитана: {ex.Message}", "WARNING");
            return outcome;
        }

        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            if (outcome.StopPass)
                break;

            var block = CheckDependencies(op);
            if (block.Wait)
            {
                outcome.Waiting++;
                continue;
            }
            if (block.Reason != null)
            {
                MarkReview(op, block.Reason);
                outcome.Review++;
                continue;
            }

            try
            {
                // Отметка ДО запроса и переживает перезапуск: дальше — только тот же ключ.
                OfflineOperationsStore.Update(op.Id, o =>
                {
                    o.Status = OfflineOperationEntry.Syncing;
                    o.RequestSent = true;
                    o.Attempts++;
                    o.LastAttemptAt = DateTimeOffset.Now;
                });
                var fresh = OfflineOperationsStore.Get(op.Id) ?? op;
                if (fresh.Kind == OfflineOperationEntry.KindRefund)
                    await SendRefundAsync(api, fresh, ct).ConfigureAwait(false);
                else
                    await SendDebtPaymentAsync(api, fresh, ct).ConfigureAwait(false);
                outcome.Sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                OfflineOperationsStore.Update(op.Id, o => { if (o.Status == OfflineOperationEntry.Syncing) o.Status = OfflineOperationEntry.PendingSync; });
                throw;
            }
            catch (ReviewException ex)
            {
                // Отказ до запроса (нет строки на сервере, чек не найден…): запрос не уходил — ключ не «в полёте», но решает человек.
                MarkReview(op, ex.Message);
                outcome.Review++;
            }
            catch (InvalidOperationException ex)
            {
                // Защита вызова (например, возврат без строк) — на сервер ничего не ушло; решает человек.
                MarkReview(op, ex.Message);
                outcome.Review++;
            }
            catch (ApiException ex)
            {
                HandleApiError(op, ex, outcome);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                // Связь/таймаут/повреждённый ответ: запрос мог дойти — остаётся в очереди, повтор тем же ключом.
                OfflineOperationsStore.Update(op.Id, o =>
                {
                    o.Status = OfflineOperationEntry.PendingSync;
                    o.LastError = ServerOutageMonitor.Describe(ex);
                });
                PosLogger.Log($"OFFLINE ops: {op.Kind} {op.Id} — нет ответа ({ServerOutageMonitor.Describe(ex)}), повтор тем же ключом.", "OFFLINE");
                outcome.StopPass = true;
                outcome.Failure = ex;
            }
        }

        return outcome;
    }

    private readonly record struct Block(bool Wait, string? Reason);

    private static Block CheckDependencies(OfflineOperationEntry op)
    {
        foreach (var dep in op.DependsOn)
        {
            var other = OfflineOperationsStore.Get(dep);
            if (other is not null)
            {
                // Прошлая операция: ждём, пока уйдёт; отклонена — эта тоже ждёт решения человека.
                if (other.Status == OfflineOperationEntry.Synced)
                    continue;
                if (other.Status == OfflineOperationEntry.Failed && other.LastError == OfflineOperationsStore.CanceledMarker)
                    continue; // 2026-10-30: прошлая операция закрыта вручную без отправки — не держим эту
                if (other.Status == OfflineOperationEntry.Failed)
                    return new(false, Tr.T("Предыдущая операция по этому чеку/клиенту не отправлена (требует проверки). Сначала разберите её.",
                        "Бул чек/кардар боюнча мурунку операция жөнөтүлгөн жок (текшерүү керек). Алгач аны иретке келтириңиз.",
                        "The previous operation for this receipt/client was not sent (needs review). Sort it out first.",
                        "Bu fiş/müşteri için önceki işlem gönderilmedi (kontrol gerekli). Önce onu çözün.",
                        "Bu chek/mijoz bo'yicha oldingi operatsiya yuborilmadi (tekshirish kerak). Avval uni hal qiling."));
                return new(true, null);
            }

            // Чек очереди: отправлен (есть серверный id) / ждёт / отклонён.
            var server = OfflineOperationsStore.ServerIdOf(dep);
            if (server is not null)
            {
                // Оплате долга номер чека не нужен (она по клиенту) — пустой серверный id мешает только возврату по этому чеку.
                if (server.Length == 0 && op.Kind == OfflineOperationEntry.KindDebtPayment)
                    continue;
                if (server.Length == 0)
                    return new(false, Tr.T("Чек ушёл на сервер, но сервер не вернул его номер — оформите операцию вручную на сайте.",
                        "Чек серверге жөнөтүлдү, бирок сервер анын номерин кайтарган жок — операцияны сайтта кол менен жасаңыз.",
                        "The receipt was sent but the server did not return its number — do the operation by hand on the website.",
                        "Fiş gönderildi ancak sunucu numarasını döndürmedi — işlemi sitede elle yapın.",
                        "Chek yuborildi, lekin server uning raqamini qaytarmadi — operatsiyani saytda qo'lda bajaring."));
                continue;
            }

            var (depState, depServerId) = SaleStateProvider(dep);
            switch (depState)
            {
                case OfflineSaleState.Synced:
                    if (string.IsNullOrWhiteSpace(depServerId))
                    {
                        if (op.Kind == OfflineOperationEntry.KindDebtPayment)
                            continue;
                        return new(false, Tr.T("Чек ушёл на сервер, но его номер неизвестен — оформите операцию вручную на сайте.", "Чек серверге жөнөдү, бирок анын номери белгисиз — операцияны сайтта кол менен жасаңыз.",
                            "The receipt was sent but its number is unknown — do the operation by hand on the website.", "Fiş gönderildi ancak numarası bilinmiyor — işlemi sitede elle yapın.", "Chek yuborildi, lekin uning raqami noma'lum — operatsiyani saytda qo'lda bajaring."));
                    }
                    try { OfflineOperationsStore.NoteSaleSynced(dep, depServerId); }
                    catch (IOException) { return new(true, null); } // карта не записалась — на следующем проходе
                    continue;
                case OfflineSaleState.Pending:
                    return new(true, null);
                case OfflineSaleState.Rejected:
                    return new(false, Tr.T("Чек, к которому относится операция, сервер не принял («Требует проверки»). Разберите чек — операция уйдёт после него.",
                        "Операция тиешелүү чекти сервер кабыл алган жок («Текшерүү керек»). Чекти иретке келтириңиз — операция андан кийин кетет.",
                        "The receipt this operation belongs to was not accepted by the server (“Needs review”). Sort the receipt out — the operation goes after it.",
                        "İşlemin ait olduğu fiş sunucu tarafından kabul edilmedi («Kontrol gerekli»). Fişi çözün — işlem ondan sonra gider.",
                        "Operatsiya tegishli chekni server qabul qilmadi («Tekshirish kerak»). Chekni hal qiling — operatsiya undan keyin ketadi."));
                default:
                    return new(false, Tr.T("Чек, к которому относится операция, удалён из очереди без отправки. Проверьте чек на сервере.",
                        "Операция тиешелүү чек жөнөтүлбөй кезектен өчүрүлгөн. Чекти серверден текшериңиз.",
                        "The receipt this operation belongs to was removed from the queue without being sent. Check the receipt on the server.",
                        "İşlemin ait olduğu fiş gönderilmeden kuyruktan silindi. Fişi sunucuda kontrol edin.",
                        "Operatsiya tegishli chek yuborilmasdan navbatdan o'chirilgan. Chekni serverdan tekshiring."));
            }
        }

        return new(false, null);
    }

    private sealed class ReviewException : Exception
    {
        public ReviewException(string message) : base(message) { }
    }

    /// <summary>2026-10-29 (ревью, В-3): чтение остатка ящика до/после возврата — для поправки, если сервер не вычел наличные (возврат долгового чека).
    /// Боевая реализация ставится приложением (ExchangeCashCorrections). null — не знаем, ничего не делаем.</summary>
    public static Func<string?, Task<decimal?>>? DrawerReader { get; set; }

    /// <summary>Поправка ящика, если сервер не вычел выданные наличные (shiftId, ключ, ошибка сервера, выдано наличными, подпись).</summary>
    public static Action<string?, string, decimal, decimal, string>? DrawerCorrector { get; set; }

    private static decimal? ReadDecimal(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var n in names)
        {
            if (!root.TryGetProperty(n, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
                return p;
        }
        return null;
    }

    private static async Task SendRefundAsync(IOfflineOpsApi api, OfflineOperationEntry op, CancellationToken ct)
    {
        var saleId = op.SaleRefIsLocal ? OfflineOperationsStore.ServerIdOf(op.SaleRef ?? "") : op.SaleRef;
        if (string.IsNullOrWhiteSpace(saleId))
            throw new ReviewException(Tr.T("Серверный номер чека неизвестен.", "Чектин сервердик номери белгисиз.", "The receipt's server number is unknown.", "Fişin sunucu numarası bilinmiyor.", "Chekning server raqami noma'lum."));

        var firstAttempt = !op.RequestSent || op.Attempts <= 1;
        IReadOnlyList<PosRefundLineRequest>? lines = null;
        if (op.SentLines is { Count: > 0 } sent)
        {
            // Повтор: запрос с этим ключом мог дойти — шлём ТО ЖЕ тело, не пересчитывая по остатку на сервере (первая попытка могла его уже уменьшить).
            lines = sent.Select(l => new PosRefundLineRequest { LineId = l.LineId, ProductId = l.ProductId, Title = l.Title, Quantity = l.Quantity, OriginalQuantity = l.OriginalQuantity }).ToList();
        }
        else if (op.Whole && op.WholeSent)
        {
            // 2026-10-30: «весь чек» — повтор тем же ключом и телом (lines = null), чек на сервере не запрашиваем и не сверяем.
            PosLogger.Log($"OFFLINE ops: возврат «весь чек» {op.Id} — повтор тем же ключом и телом, сверка чека пропущена.", "OFFLINE");
        }
        else
        {
            // Чек на сервере: id строк (sale_item_id) касса знает только оттуда; заодно — сверка «сколько ещё можно вернуть» (В-3): и для возврата по строкам, и для «всего чека».
            var sale = await api.GetSaleAsync(saleId!, ct).ConfigureAwait(false);
            CheckSaleAllowsRefund(sale, op);
            if (!op.Whole)
            {
                lines = MapLines(sale, op);
                // 2026-10-29 (ревью, Б-1): пустой список строк на сервер НЕ уходит никогда — без items сервер вернул бы ВЕСЬ чек.
                if (lines.Count == 0)
                    throw new ReviewException(Tr.T("Не нашлось строк чека для возврата — на сервер ничего не отправлено. Проверьте чек.", "Кайтаруу үчүн чектин саптары табылган жок — серверге эч нерсе жөнөтүлгөн жок. Чекти текшериңиз.",
                        "No receipt lines were found to return — nothing was sent to the server. Check the receipt.", "İade edilecek fiş satırı bulunamadı — sunucuya bir şey gönderilmedi. Fişi kontrol edin.", "Qaytarish uchun chek qatorlari topilmadi — serverga hech narsa yuborilmadi. Chekni tekshiring."));
                var mapped = lines.Select(l => new OfflineSentLine { LineId = l.LineId, ProductId = l.ProductId, Title = l.Title, Quantity = l.Quantity, OriginalQuantity = l.OriginalQuantity }).ToList();
                // На диск ДО запроса: тело и ключ переживут обрыв связи и перезапуск кассы.
                OfflineOperationsStore.Update(op.Id, o => o.SentLines = mapped);
            }
        }

        // Явный режим «весь чек» — только у операции, оформленной как весь чек (сумма совпала с итогом); любой другой возврат без строк не уходит.
        if (!op.Whole && (lines is null || lines.Count == 0))
            throw new ReviewException(Tr.T("Возврат без строк не отправляется.", "Саптарсыз кайтаруу жөнөтүлбөйт.", "A return without lines is not sent.", "Satırsız iade gönderilmez.", "Qatorsiz qaytarish yuborilmaydi."));

        // Остаток ящика до возврата — для поправки (сервер не вычитает наличные при возврате долгового чека). Только на первой попытке.
        decimal? drawerBefore = null;
        if (firstAttempt && op.Cash > 0.005m && DrawerReader is { } reader && IsServerShift(op.ShiftId))
        {
            try { drawerBefore = await reader(op.ShiftId).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"OFFLINE ops: остаток ящика до возврата не прочитан: {ex.Message}", "WARNING"); }
        }

        if (op.Whole && !op.WholeSent)
            OfflineOperationsStore.Update(op.Id, o => o.WholeSent = true); // 2026-10-30: на диск ДО запроса
        var response = await api.ReturnSaleAsync(saleId!, op.Whole ? null : lines, op.Id, ct).ConfigureAwait(false);

        OfflineOperationsStore.Update(op.Id, o =>
        {
            o.Status = OfflineOperationEntry.Synced;
            o.SyncedAt = DateTimeOffset.Now;
            o.LastError = null;
            o.SaleRef = saleId; // дальше — серверный id
            o.SaleRefIsLocal = false;
        });
        PosLogger.Log($"OFFLINE ops: возврат {op.Id} проведён на сервере, чек {saleId}, {op.Amount:0.00}.", "OFFLINE");

        // Сверка суммы: сервер сообщил, сколько вернул, — расхождение больше 1 сома не молчим (лог + владельцу).
        if (ReadDecimal(response, "refund_amount", "refund_total", "returned_total", "refunded_amount") is { } serverAmount && Math.Abs(serverAmount - op.Amount) > 1m)
        {
            PosLogger.Log($"OFFLINE ops: возврат {op.Id}: сервер записал {serverAmount:0.00}, касса выдала {op.Amount:0.00} — расхождение.", "WARNING");
            OwnerNotifier?.Invoke(op, Tr.T($"Сервер записал возврат на {serverAmount:0.00} сом, а касса выдала {op.Amount:0.00} сом. Сверьте чек.", $"Сервер кайтарууну {serverAmount:0.00} сомго жазды, ал эми касса {op.Amount:0.00} сом берди. Чекти салыштырыңыз.",
                $"The server recorded the return as {serverAmount:0.00} som, the till paid out {op.Amount:0.00} som. Check the receipt.", $"Sunucu iadeyi {serverAmount:0.00} som kaydetti, kasa {op.Amount:0.00} som verdi. Fişi kontrol edin.", $"Server qaytarishni {serverAmount:0.00} so'mga yozdi, kassa {op.Amount:0.00} so'm berdi. Chekni solishtiring."));
        }

        // Поправка ящика: наличные выданы, а сервер остаток смены не уменьшил (долговой чек).
        if (drawerBefore is { } before && DrawerReader is { } reader2 && DrawerCorrector is { } corrector)
        {
            try
            {
                if (await reader2(op.ShiftId).ConfigureAwait(false) is { } after)
                    corrector(op.ShiftId, "refund:offline:" + op.Id, (after - before) + op.Cash, op.Cash, "Возврат без связи (наличные)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"OFFLINE ops: поправка ящика после возврата не сделана: {ex.Message}", "WARNING"); }
        }

        if (!string.IsNullOrWhiteSpace(op.ClientId) && op.DebtReduced > 0m)
            OfflineClientCache.ApplyConfirmed(op.ClientId, op.Id, -op.DebtReduced);
        if (op.ReturnsAll)
        {
            // Возвращён весь чек — бонусы за него отменяются (как при возврате со связью); сбой бонусов возврат не отменяет.
            try { ClientLoyaltyStore.ReverseRemainingForSale(saleId!); }
            catch (Exception ex) { PosLogger.Log($"OFFLINE ops: бонусы чека {saleId} не отменены: {ex.Message}", "WARNING"); }
        }
    }

    private static bool IsServerShift(string? shiftId) => Guid.TryParse(shiftId, out _);

    /// <summary>2026-10-29 (ревью, В-3): до отправки сверяем с чеком на сервере: чек не отменён, и «можно вернуть» не меньше, чем выдала касса. Иначе — «Требует проверки»,
    /// на сервер ничего не уходит (деньги покупателю уже выданы — решает человек).</summary>
    private static void CheckSaleAllowsRefund(JsonElement sale, OfflineOperationEntry op)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return;
        if (sale.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            && st.GetString()?.Trim().ToLowerInvariant() is "canceled" or "cancelled" or "returned" or "refunded")
            throw new ReviewException(Tr.T("Чек на сервере уже отменён или возвращён целиком (на сайте или другой кассой), а касса выдала покупателю деньги. Сверьте чек.",
                "Серверде чек мурунтан жокко чыгарылган же толугу менен кайтарылган (сайтта же башка кассада), ал эми касса сатып алуучуга акча берди. Чекти салыштырыңыз.",
                "The receipt is already canceled or fully returned on the server (on the website or another till), but the till paid the customer. Check the receipt.",
                "Fiş sunucuda zaten iptal edilmiş veya tamamen iade edilmiş (sitede veya başka kasada), ancak kasa müşteriye para verdi. Fişi kontrol edin.",
                "Chek serverda allaqachon bekor qilingan yoki to'liq qaytarilgan (saytda yoki boshqa kassada), kassa esa xaridorga pul berdi. Chekni solishtiring."));
        if (ReadDecimal(sale, "refundable_total") is { } left && left > 0m && left < op.Amount - 0.01m)
            throw new ReviewException(Tr.T($"На сервере по чеку можно вернуть только {left:0.00} сом, а касса выдала {op.Amount:0.00} сом (часть уже возвращена на сайте или другой кассой). Сверьте чек.",
                $"Серверде чек боюнча {left:0.00} сом гана кайтарууга болот, ал эми касса {op.Amount:0.00} сом берди (бир бөлүгү сайтта же башка кассада кайтарылган). Чекти салыштырыңыз.",
                $"On the server only {left:0.00} som can be returned on this receipt, but the till paid out {op.Amount:0.00} som (part was already returned on the website or another till). Check the receipt.",
                $"Sunucuda bu fiş için yalnızca {left:0.00} som iade edilebilir, kasa ise {op.Amount:0.00} som verdi (bir kısmı sitede veya başka kasada iade edilmiş). Fişi kontrol edin.",
                $"Serverda bu chek bo'yicha faqat {left:0.00} so'm qaytarish mumkin, kassa esa {op.Amount:0.00} so'm berdi (bir qismi saytda yoki boshqa kassada qaytarilgan). Chekni solishtiring."));
        if (op.Whole)
        {
            // Возврат «весь чек» без строк: на сервере не должно быть ни одной уже возвращённой строки.
            foreach (var it in CartDisplayHelper.EnumerateSaleLineItems(sale))
                if (CartDisplayHelper.RefundableQuantity(it) < CartDisplayHelper.LineQuantity(it) - 1e-6)
                    throw new ReviewException(Tr.T("Часть чека уже возвращена на сервере (сайт или другая касса), а касса оформила возврат всего чека. Сверьте чек.",
                        "Чектин бир бөлүгү серверде мурунтан кайтарылган (сайт же башка касса), ал эми касса бүт чекти кайтарды. Чекти салыштырыңыз.",
                        "Part of the receipt is already returned on the server (website or another till), but the till made a whole-receipt return. Check the receipt.",
                        "Fişin bir kısmı sunucuda zaten iade edilmiş (site veya başka kasa), kasa ise tüm fişi iade etti. Fişi kontrol edin.",
                        "Chekning bir qismi serverda allaqachon qaytarilgan (sayt yoki boshqa kassa), kassa esa butun chekni qaytardi. Chekni solishtiring."));
        }
    }

    private static List<PosRefundLineRequest> MapLines(JsonElement sale, OfflineOperationEntry op)
    {
        var server = CartDisplayHelper.EnumerateSaleLineItems(sale)
            .Select(it => (Id: CartDisplayHelper.TryRefundLineId(it), ProductId: CartDisplayHelper.TryProductId(it), Name: CartDisplayHelper.ItemName(it),
                           Left: CartDisplayHelper.RefundableQuantity(it), Total: CartDisplayHelper.LineQuantity(it)))
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .ToList();
        if (server.Count == 0)
            throw new ReviewException(Tr.T("Сервер не отдал строки чека — возврат по строкам невозможен.", "Сервер чектин саптарын берген жок — саптар боюнча кайтаруу мүмкүн эмес.",
                "The server did not return the receipt's lines — a return by lines is impossible.", "Sunucu fişin satırlarını döndürmedi — satır bazlı iade mümkün değil.",
                "Server chek satrlarini bermadi — satrlar bo'yicha qaytarish mumkin emas."));

        var left = server.Select(s => s.Left).ToArray();
        var result = new List<PosRefundLineRequest>();
        foreach (var line in op.Lines)
        {
            var need = line.Quantity;
            var matched = false;
            for (var i = 0; i < server.Count && need > 1e-6; i++)
            {
                var byProduct = !string.IsNullOrWhiteSpace(line.ProductId) && string.Equals(server[i].ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase);
                var byName = string.IsNullOrWhiteSpace(line.ProductId) && string.Equals(server[i].Name, line.Name, StringComparison.OrdinalIgnoreCase);
                if (!byProduct && !byName)
                    continue;
                matched = true;
                var take = Math.Min(left[i], need);
                if (take <= 1e-6)
                    continue;
                left[i] -= take;
                need -= take;
                result.Add(new PosRefundLineRequest
                {
                    LineId = server[i].Id!, ProductId = server[i].ProductId, Title = line.Name, Quantity = take, OriginalQuantity = server[i].Total,
                });
            }

            if (need > 1e-6)
                throw new ReviewException(matched
                    ? Tr.T($"«{line.Name}»: на сервере к возврату осталось меньше, чем нужно (уже возвращено на сайте или другой кассе). Проверьте чек.",
                        $"«{line.Name}»: серверде кайтарууга керектүүдөн аз калды (сайтта же башка кассада кайтарылган). Чекти текшериңиз.",
                        $"“{line.Name}”: the server has less left to return than needed (already returned on the website or another till). Check the receipt.",
                        $"«{line.Name}»: sunucuda iade için gerekenden az kaldı (sitede veya başka kasada iade edilmiş). Fişi kontrol edin.",
                        $"«{line.Name}»: serverda qaytarish uchun kerakdan kam qoldi (saytda yoki boshqa kassada qaytarilgan). Chekni tekshiring.")
                    : Tr.T($"«{line.Name}»: такой строки в чеке на сервере не найдено. Проверьте чек.", $"«{line.Name}»: серверде чекте мындай сап табылган жок. Чекти текшериңиз.",
                        $"“{line.Name}”: no such line in the receipt on the server. Check the receipt.", $"«{line.Name}»: sunucudaki fişte böyle bir satır bulunamadı. Fişi kontrol edin.",
                        $"«{line.Name}»: serverdagi chekda bunday satr topilmadi. Chekni tekshiring."));
        }

        return result;
    }

    private static async Task SendDebtPaymentAsync(IOfflineOpsApi api, OfflineOperationEntry op, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(op.ClientId))
            throw new ReviewException(Tr.T("В операции нет клиента.", "Операцияда кардар жок.", "The operation has no client.", "İşlemde müşteri yok.", "Operatsiyada mijoz yo'q."));

        var result = await api.PayDebtAsync(op.ClientId!, (double)op.Amount, op.Method, op.ShiftId, op.Id, ct).ConfigureAwait(false);

        OfflineOperationsStore.Update(op.Id, o =>
        {
            o.Status = OfflineOperationEntry.Synced;
            o.SyncedAt = DateTimeOffset.Now;
            o.LastError = null;
        });
        PosLogger.Log($"OFFLINE ops: оплата долга {op.Id} проведена, клиент {op.ClientId}, {op.Amount:0.00}{(result.Replayed ? " (повтор по ключу)" : "")}, остаток {(result.Left >= 0 ? result.Left.ToString("0.00") : "—")}.", "OFFLINE");
        OfflineClientCache.ApplyConfirmed(op.ClientId, op.Id, -op.Amount);
    }

    /// <summary>Сколько проходов подряд ответ «запрос с этим ключом ещё выполняется» / 409 без причины терпим, прежде чем «Требует проверки».</summary>
    private const int MaxTransientConflicts = 20;

    private static void HandleApiError(OfflineOperationEntry op, ApiException ex, Outcome outcome)
    {
        var verdict = ReplayErrorPolicy.Classify(ex.StatusCode, ex.Payload is { ValueKind: JsonValueKind.Object or JsonValueKind.Array });
        var business = OfflineRejection.IsBusinessConflict(ex);

        // 2026-10-29 (ревью, В-2): смена закрыта / чужая / нужна — операцию в другую смену и с другим телом НЕ переносим: «Требует проверки» с понятной причиной.
        // Тело запроса и ключ не меняются никогда.
        if (business || verdict == ReplayVerdict.Rejected)
        {
            MarkReview(op, OfflineRejection.Describe(ex));
            outcome.Review++;
            return;
        }

        // Ключ ещё выполняется / 409 без причины: терпим ограниченное число проходов, и этот проход продолжаем — ошибка относится к одной операции.
        var oneOperationConflict = ex.StatusCode is 409 or 423 or 425;
        if (oneOperationConflict && op.Attempts >= MaxTransientConflicts)
        {
            MarkReview(op, Tr.T($"Сервер больше {MaxTransientConflicts} проходов подряд отвечает «{ex.Message}». Проверьте операцию на сайте.", $"Сервер {MaxTransientConflicts} өтүү катары менен «{ex.Message}» деп жооп берет. Операцияны сайтта текшериңиз.",
                $"For more than {MaxTransientConflicts} passes in a row the server answers “{ex.Message}”. Check the operation on the website.", $"Sunucu {MaxTransientConflicts} geçiştir üst üste «{ex.Message}» yanıtını veriyor. İşlemi sitede kontrol edin.", $"Server {MaxTransientConflicts} o'tish ketma-ket «{ex.Message}» deb javob beryapti. Operatsiyani saytda tekshiring."));
            outcome.Review++;
            return;
        }

        // Временная причина (5xx, 429, 408, 401, ключ ещё выполняется, ответ не от NurCRM, тариф/права): остаётся в очереди.
        OfflineOperationsStore.Update(op.Id, o =>
        {
            o.Status = OfflineOperationEntry.PendingSync;
            o.LastError = verdict == ReplayVerdict.RetryNotifyOwner
                ? Tr.T($"Сервер пока не разрешает операцию (код {ex.StatusCode}: тариф, подписка или права). Она уйдёт сама, как только сервер разрешит. Ответ сервера: {ex.Message}",
                    $"Сервер операцияга азырынча уруксат бербей жатат (код {ex.StatusCode}: тариф, жазылуу же укуктар). Сервер уруксат бергенде өзү кетет. Сервердин жообу: {ex.Message}",
                    $"The server does not allow the operation yet (code {ex.StatusCode}: plan, subscription or permissions). It will go by itself once allowed. Server reply: {ex.Message}",
                    $"Sunucu işleme henüz izin vermiyor (kod {ex.StatusCode}: tarife, abonelik veya yetkiler). İzin verilince kendiliğinden gider. Sunucu yanıtı: {ex.Message}",
                    $"Server operatsiyaga hozircha ruxsat bermayapti (kod {ex.StatusCode}: tarif, obuna yoki huquqlar). Ruxsat berilishi bilan o'zi ketadi. Server javobi: {ex.Message}")
                : $"HTTP {ex.StatusCode}: {ex.Message}";
        });
        if (verdict == ReplayVerdict.RetryNotifyOwner)
            NotifyOnce(op, ex.Message);
        PosLogger.Log($"OFFLINE ops: {op.Kind} {op.Id} — временная ошибка {ex.StatusCode}, повтор тем же ключом.", "OFFLINE");
        outcome.Waiting++;
        if (oneOperationConflict)
            return; // остальные операции идут дальше
        outcome.StopPass = true;
        if (ex.StatusCode is 408 or (>= 502 and <= 599))
            outcome.Failure = ex;
    }

    private static void MarkReview(OfflineOperationEntry op, string reason)
    {
        var text = Tr.T("Требует проверки: ", "Текшерүү керек: ", "Needs review: ", "Kontrol gerekli: ", "Tekshirish kerak: ") + reason;
        OfflineOperationsStore.Update(op.Id, o =>
        {
            o.Status = OfflineOperationEntry.Failed;
            o.ReviewNeeded = true;
            o.LastError = text;
        });
        PosLogger.Log($"OFFLINE ops: {op.Kind} {op.Id} — требует проверки: {reason}", "WARNING");
        NotifyOnce(op, text);
    }

    private static void NotifyOnce(OfflineOperationEntry op, string reason)
    {
        try
        {
            if (OfflineOperationsStore.Get(op.Id) is not { OwnerNotified: false })
                return;
            OfflineOperationsStore.Update(op.Id, o => o.OwnerNotified = true);
            OwnerNotifier?.Invoke(op, reason);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE ops: оповещение владельцу не отправлено: {ex.Message}", "WARNING");
        }
    }

    private static void NotifyOwnerViaTelegram(OfflineOperationEntry op, string reason)
    {
        if (!TelegramBotService.IsConfigured)
            return;
        var title = op.Kind == OfflineOperationEntry.KindDebtPayment
            ? Tr.T("Оплата долга не ушла на сервер", "Карыз төлөмү серверге жөнөгөн жок", "A debt payment was not sent to the server", "Borç ödemesi sunucuya gönderilemedi", "Qarz to'lovi serverga yuborilmadi")
            : Tr.T("Возврат не ушёл на сервер", "Кайтаруу серверге жөнөгөн жок", "A return was not sent to the server", "İade sunucuya gönderilemedi", "Qaytarish serverga yuborilmadi");
        var when = op.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
        var text = TelegramBotService.BuildAlert(title,
            Tr.T($"Касса: {OfflineOperationsService.Title(op)} ({when}) оформлена без связи, сервер её не принял.\nПричина: {reason}\nОперация сохранена в кассе — проверьте её и верните в очередь либо проведите вручную.",
                $"Касса: {OfflineOperationsService.Title(op)} ({when}) байланышсыз жасалды, сервер аны кабыл алган жок.\nСебеби: {reason}\nОперация кассада сакталды — текшерип, кезекке кайтарыңыз же кол менен өткөрүңүз.",
                $"Till: {OfflineOperationsService.Title(op)} ({when}) was made without a connection and the server did not accept it.\nReason: {reason}\nThe operation is kept in the till — check it and return it to the queue or process it manually.",
                $"Kasa: {OfflineOperationsService.Title(op)} ({when}) bağlantısız yapıldı, sunucu kabul etmedi.\nNeden: {reason}\nİşlem kasada saklı — kontrol edip kuyruğa geri alın veya elle işleyin.",
                $"Kassa: {OfflineOperationsService.Title(op)} ({when}) aloqasiz rasmiylashtirildi, server qabul qilmadi.\nSabab: {reason}\nOperatsiya kassada saqlangan — tekshirib navbatga qaytaring yoki qo'lda o'tkazing."));
        _ = Task.Run(async () =>
        {
            var error = await TelegramBotService.SendAsync(text).ConfigureAwait(false);
            if (error != null)
                PosLogger.Log($"OFFLINE ops: оповещение владельцу не ушло ({error}).", "WARNING");
        });
    }
}
