using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (живой тест Этапа 1, Б-2): снимок чека с сервера ДО отправки возврата. Сервер ceba67c на частичный возврат долгового чека отвечает 400
/// «Долг нельзя указывать в строках оплаты», но возврат проводит: чек становится «partially_returned», количество строки уменьшается (2 → 1), итог 14 → 7.
/// Прежняя проверка сравнивала «можно вернуть» с количеством, которое кассир хотел вернуть (1), а не с тем, что было к возврату (2), и говорила «не проведён».
/// Теперь проведённость определяется сравнением ЭТОГО снимка с чеком после ответа: изменился статус, итоги возвратов или количества по строкам — возврат записан.</summary>
public sealed class ReturnSnapshot
{
    /// <summary>Строка чека: количество, сколько ещё можно вернуть, сколько уже возвращено.</summary>
    public readonly record struct LineState(double Quantity, double Refundable, double Returned);

    private static readonly string[] ReturnedKeys = ["returned_qty", "quantity_refunded", "returned_quantity", "qty_returned", "refunded_quantity"];
    private static readonly string[] RefundedTotalKeys = ["refunded_total", "returned_total", "return_total", "refund_total", "refunded_amount"];

    public string Status { get; private init; } = "";
    public string ReturnStatus { get; private init; } = "";
    public decimal? Total { get; private init; }
    public decimal? RefundableTotal { get; private init; }
    public decimal? RefundedTotal { get; private init; }
    public decimal? DebtRemaining { get; private init; }
    public bool FullyReturned { get; private init; }
    public IReadOnlyDictionary<string, LineState> Lines { get; private init; } = new Dictionary<string, LineState>();

    public static ReturnSnapshot From(JsonElement sale)
    {
        var lines = new Dictionary<string, LineState>(StringComparer.OrdinalIgnoreCase);
        if (sale.ValueKind != JsonValueKind.Object)
            return new ReturnSnapshot { Lines = lines };
        foreach (var it in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            if (CartDisplayHelper.TryRefundLineId(it) is not { Length: > 0 } id)
                continue;
            double returned = 0;
            foreach (var key in ReturnedKeys)
                if (it.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d))
                {
                    returned = d;
                    break;
                }
            lines[id] = new LineState(CartDisplayHelper.LineQuantity(it), CartDisplayHelper.RefundableQuantity(it), returned);
        }
        return new ReturnSnapshot
        {
            Status = Text(sale, "status"),
            ReturnStatus = Text(sale, "return_status"),
            Total = Money(sale, "total"),
            RefundableTotal = Money(sale, "refundable_total"),
            RefundedTotal = RefundedTotalKeys.Select(k => Money(sale, k)).FirstOrDefault(v => v is not null),
            DebtRemaining = Money(sale, "debt_remaining") ?? Money(sale, "remaining_debt"),
            FullyReturned = CartDisplayHelper.IsSaleFullyReturned(sale),
            Lines = lines,
        };
    }

    private static string Text(JsonElement sale, string key) =>
        sale.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim().ToLowerInvariant() : "";

    private static decimal? Money(JsonElement sale, string key) =>
        sale.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String ? (decimal)d : null;

    private static bool Differs(decimal? a, decimal? b) => Math.Abs((a ?? 0m) - (b ?? 0m)) > 0.005m;

    /// <summary>Чек после запроса отличается от снимка до него: статус, статус возврата, итоги возвратов или количества по строкам. Остаток долга не смотрим —
    /// сервер при ошибке «Долг нельзя указывать…» возврат проводит, а долг не уменьшает (это проверяет <see cref="DebtRecalcSuspicious"/>).</summary>
    public bool ChangedSince(ReturnSnapshot before)
    {
        if (!string.Equals(Status, before.Status, StringComparison.Ordinal) || !string.Equals(ReturnStatus, before.ReturnStatus, StringComparison.Ordinal))
            return true;
        if (Differs(Total, before.Total) || Differs(RefundableTotal, before.RefundableTotal) || Differs(RefundedTotal, before.RefundedTotal))
            return true;
        foreach (var (id, was) in before.Lines)
        {
            if (!Lines.TryGetValue(id, out var now))
                return true; // строка исчезла из чека
            if (Math.Abs(now.Quantity - was.Quantity) > 1e-6 || Math.Abs(now.Refundable - was.Refundable) > 1e-6 || Math.Abs(now.Returned - was.Returned) > 1e-6)
                return true;
        }
        return false;
    }

    /// <summary>Сервер пересчитал долг клиента иначе, чем ждала касса. 1) Частичный возврат, а чек стал «возвращён целиком/отменён» (№2061: canceled, долг 0 вместо 7).
    /// 2) Частичный возврат долгового чека: остаток долга после не равен «до − на сколько уменьшила касса» (№2060: осталось 14 вместо 7).
    /// Полный возврат не проверяется: у возвращённого целиком чека сервер оставляет старый остаток, фактически долг по нему 0.</summary>
    public static bool DebtRecalcSuspicious(ReturnSnapshot? before, ReturnSnapshot after, bool partialReturn, decimal debtReduced)
    {
        if (!partialReturn)
            return false;
        if (after.FullyReturned && before is not { FullyReturned: true })
            return true;
        if (before?.DebtRemaining is { } was && was > 0.005m && after.DebtRemaining is { } now && debtReduced > 0.005m)
            return Math.Abs(now - Math.Max(0m, was - debtReduced)) > 0.01m;
        return false;
    }

    /// <summary>Честное предупреждение кассиру: сервер пересчитал долг клиента иначе, чем рассчитала касса.</summary>
    public static string DebtRecalcWarningText => Tr.T(
        "Сервер пересчитал долг клиента иначе, чем рассчитала касса (например, частичный возврат записан как полный или долг не уменьшился). Проверьте долг клиента в «Долгах» и на сайте NurCRM.",
        "Сервер кардардын карызын касса эсептегенден башкача кайра эсептеди (мисалы, жарым-жартылай кайтаруу толук катары жазылды же карыз азайган жок). Кардардын карызын «Карыздар» бөлүмүнөн жана NurCRM сайтынан текшериңиз.",
        "The server recalculated the client's debt differently from the till (for example, a partial return was recorded as a full one, or the debt did not decrease). Check the client's debt in “Debts” and on the NurCRM website.",
        "Sunucu müşteri borcunu kasadan farklı hesapladı (örneğin kısmi iade tam iade olarak kaydedildi veya borç azalmadı). Müşterinin borcunu «Borçlar»da ve NurCRM sitesinde kontrol edin.",
        "Server mijoz qarzini kassadan boshqacha qayta hisobladi (masalan, qisman qaytarish to'liq deb yozildi yoki qarz kamaymadi). Mijoz qarzini «Qarzlar»da va NurCRM saytida tekshiring.");
}

/// <summary>Итог сверки возврата по чеку после ошибки сервера.</summary>
public enum ReturnProbeResult
{
    /// <summary>Чек изменился относительно снимка до запроса — возврат проведён.</summary>
    Applied,
    /// <summary>Чек прочитан (последняя попытка) и не изменился за всё время ожидания — возврат не проведён.</summary>
    NotApplied,
    /// <summary>Чек перечитать не удалось (последняя попытка не прошла) — исход неизвестен: деньги не выдавать, возврат не повторять.</summary>
    Unknown,
}

/// <summary>2026-11-06 (Б-2): «проведён ли возврат» после ответа сервера ошибкой. Критерий — сравнение со снимком ДО запроса (<see cref="ReturnSnapshot"/>) плюс прежняя проверка
/// «можно вернуть стало меньше, чем было» (по количеству ДО возврата, а не по количеству к возврату). Сервер может записывать с задержкой — чек перечитывается с нарастающей паузой.</summary>
public static class ReturnProbe
{
    /// <summary>Паузы между перечитываниями: 1+2+3+4+5 = 15 с.</summary>
    public static readonly TimeSpan[] RefusalDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)];

    /// <summary>Чек после запроса: статус «отменён/возвращён» или по какой-то из отправленных строк к возврату осталось меньше, чем было перед отправкой.</summary>
    public static bool LinesReturnedSince(JsonElement sale, IReadOnlyDictionary<string, double> refundableBefore)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return false;
        if (sale.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            && status.GetString()?.Trim().ToLowerInvariant() is "canceled" or "cancelled" or "returned" or "refunded")
            return true;
        return CartDisplayHelper.EnumerateSaleLineItems(sale).Any(item =>
            CartDisplayHelper.TryRefundLineId(item) is { } lineId
            && refundableBefore.TryGetValue(lineId, out var before) && before > 0
            && (CartDisplayHelper.LineLooksFullyReturned(item) || CartDisplayHelper.RefundableQuantity(item) < before - 1e-6));
    }

    /// <summary>Возврат записан на сервере: чек изменился относительно снимка либо «можно вернуть» стало меньше, чем было.</summary>
    public static bool Applied(JsonElement sale, ReturnSnapshot? before, IReadOnlyDictionary<string, double> refundableBefore) =>
        LinesReturnedSince(sale, refundableBefore) || (before is not null && ReturnSnapshot.From(sale).ChangedSince(before));

    /// <param name="getSale">Чтение чека с сервера (одна попытка).</param>
    /// <param name="delays">Паузы между попытками; null — <see cref="RefusalDelays"/>.</param>
    /// <param name="wait">Ожидание (в тестах подменяется); null — Task.Delay.</param>
    public static async Task<ReturnProbeResult> RunAsync(Func<CancellationToken, Task<JsonElement>> getSale, ReturnSnapshot? before,
        IReadOnlyDictionary<string, double> refundableBefore, IReadOnlyList<TimeSpan>? delays = null, Func<TimeSpan, Task>? wait = null)
    {
        delays ??= RefusalDelays;
        wait ??= d => Task.Delay(d);
        var lastRead = false;
        for (var attempt = 0; attempt <= delays.Count; attempt++)
        {
            if (attempt > 0)
                await wait(delays[attempt - 1]).ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var sale = await getSale(cts.Token).ConfigureAwait(false);
                lastRead = true;
                if (Applied(sale, before, refundableBefore))
                    return ReturnProbeResult.Applied;
            }
            catch (Exception ex)
            {
                lastRead = false;
                PosLogger.Log($"Возврат: чек для сверки не перечитан (попытка {attempt + 1}/{delays.Count + 1}): {ex.Message}", "WARNING");
            }
        }
        return lastRead ? ReturnProbeResult.NotApplied : ReturnProbeResult.Unknown;
    }
}
