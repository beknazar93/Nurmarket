using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-04: наличные чеков офлайн-очереди одной смены (id записи → сумма) на момент
/// запроса остатка смены — см. <see cref="OfflineQueueCash"/>.</summary>
public sealed record OfflineQueueCashSnapshot(string? ShiftId, IReadOnlyDictionary<string, decimal> Cash)
{
    public static OfflineQueueCashSnapshot Empty { get; } = new(null, new Dictionary<string, decimal>());
}

/// <summary>
/// 2026-10-04, живой тест офлайна 04.10: в шапке кассы «Касса: 10181.50 сом» не менялось, хотя без
/// сервера прошла продажа 42.50 наличными. Остаток смены касса берёт у сервера (shifts, cash_balance),
/// а в аварии сервера и в офлайн-смене не спрашивает его вовсе — чеки из офлайн-очереди в нём не видны,
/// пока не уйдут на сервер. Здесь — наличные этих чеков по смене, чтобы прибавить их к остатку.
///
/// «Не считать дважды»: остаток сервера, полученный в момент T, уже включает чеки, отправленные до T,
/// и не включает те, что в T ещё лежали в очереди. Поэтому вызывающий в момент запроса остатка
/// запоминает <see cref="Snapshot"/> (чеки, которых в остатке нет), а показывает остаток +
/// <see cref="NotInServerBalance"/>: запомненные чеки (даже если они уже ушли — сервер учтёт их
/// только к следующему запросу остатка) плюс чеки, вставшие в очередь позже. Новый остаток — новый
/// снимок: отправленные чеки перестают прибавляться ровно тогда, когда их учёл сервер.
/// </summary>
public static class OfflineQueueCash
{
    private static readonly string[] QueueStatuses = [OfflineSaleEntry.PendingSync, OfflineSaleEntry.Syncing];

    /// <summary>Наличные чеков очереди смены <paramref name="shiftId"/>, ещё не отправленных на сервер.
    /// Автономные продажи (их некуда отправлять) тоже здесь — остаток кассы автономного режима растёт
    /// так же. Безнал в остаток ящика не входит; «в долг» и смешанная в очередь не попадают.</summary>
    public static OfflineQueueCashSnapshot Snapshot(string? shiftId)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(shiftId))
            return new OfflineQueueCashSnapshot(shiftId, result);

        try
        {
            foreach (var entry in OfflineDatabase.LoadByStatus(QueueStatuses))
            {
                if (!string.Equals(entry.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.PaymentMethod, "cash", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cash = CashOf(entry);
                if (cash != 0m)
                    result[entry.Id] = cash;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Наличные офлайн-очереди для остатка смены не прочитаны: {ex.GetType().Name}", "WARNING");
        }

        return new OfflineQueueCashSnapshot(shiftId, result);
    }

    /// <summary>Наличные очереди смены <paramref name="shiftId"/>, которых нет в остатке сервера: чеки
    /// снимка, взятого при запросе остатка (<paramref name="atBalance"/>, если он той же смены), и чеки,
    /// вставшие в очередь после него.</summary>
    public static decimal NotInServerBalance(OfflineQueueCashSnapshot atBalance, string? shiftId)
    {
        var sameShift = !string.IsNullOrWhiteSpace(shiftId)
                        && string.Equals(atBalance.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase);
        var counted = sameShift ? atBalance.Cash : OfflineQueueCashSnapshot.Empty.Cash;
        var total = counted.Values.Sum();
        foreach (var (id, cash) in Snapshot(shiftId).Cash)
        {
            if (!counted.ContainsKey(id))
                total += cash;
        }

        return total;
    }

    /// <summary>Наличные одного чека — к оплате по чеку (сдача уходит из того же ящика).</summary>
    private static decimal CashOf(OfflineSaleEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
            return Math.Round((decimal)CartTotalsCalculator.Calculate(doc.RootElement).TotalDue, 2);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            PosLogger.Log($"Сумма офлайн-чека {entry.Id} не прочитана: {ex.GetType().Name}", "WARNING");
            return 0m;
        }
    }
}
