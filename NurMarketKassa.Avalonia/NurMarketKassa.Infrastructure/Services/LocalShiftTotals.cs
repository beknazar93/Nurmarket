using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-30 (приёмка кассы 1.17.65, «Важно» 1): итоги смены ПО ДАННЫМ КАССЫ, без ответа сервера. Z-отчёт после закрытия смены без связи
/// строился из ответа сервера на закрытие, которого нет, и показывал выручку 0 и пустые продажи (были видны только возврат и оплата долга).
/// Здесь — продажи смены из журнала оплат кассы (<see cref="SaleReturnLedger"/>: чеки, проведённые на сервере со связью, и ушедшие из очереди) и чеки
/// очереди без связи, ещё не отправленные (<see cref="OfflinePendingSalesStore"/>). Возвраты, оплаты долга, внесения и изъятия окно отчёта берёт из своих
/// журналов (<see cref="ShiftEventsStore"/>, ShiftCashOperationsStore). Продажи — до возвратов (возвраты отдельной строкой), как у сервера.
/// Чистое чтение файлов и базы кассы, сети нет.</summary>
public static class LocalShiftTotals
{
    public sealed record Result(decimal TotalSales, decimal CashSales, decimal NonCashSales, decimal DebtSales, int SalesCount);

    /// <summary>null — по этой смене касса продаж не помнит (журнал пуст или не прочитан).</summary>
    public static Result? Compute(string? shiftId, DateTime? openedAt)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;

        IReadOnlyList<OfflineSaleEntry> queue = [];
        IReadOnlyList<SaleLedgerEntry> ledger = [];
        try
        {
            queue = OfflineDatabase.LoadByStatus([OfflineSaleEntry.PendingSync, OfflineSaleEntry.Syncing]);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Итоги смены по данным кассы: очередь не прочитана: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }

        try
        {
            ledger = SaleReturnLedger.ForShift(shiftId, openedAt);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Итоги смены по данным кассы: журнал оплат не прочитан: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }

        return Aggregate(shiftId, queue, ledger);
    }

    /// <summary>Сумма по уже выбранным источникам (чистый расчёт, без файлов и базы): чеки очереди этой смены, ещё не отправленные (и автономные —
    /// их некуда отправлять), плюс проведённые на сервере по журналу оплат. Запись журнала под локальным id чека очереди (возврат чека без связи) пропускается.</summary>
    public static Result? Aggregate(string shiftId, IEnumerable<OfflineSaleEntry> queue, IEnumerable<SaleLedgerEntry> ledger)
    {
        decimal total = 0m, cash = 0m, nonCash = 0m, debt = 0m;
        var count = 0;
        var queuedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in queue)
        {
            if (!string.Equals(entry.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase) || entry.ClosedWithoutSendAt is not null)
                continue;
            queuedIds.Add(entry.Id);
            var sum = TotalOf(entry);
            if (entry.OfflineDebt || string.Equals(entry.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            {
                decimal.TryParse(entry.CashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var prepaid);
                prepaid = Math.Min(Math.Max(0m, prepaid), sum);
                cash += prepaid;
                debt += sum - prepaid;
            }
            else if (string.Equals(entry.PaymentMethod, "cash", StringComparison.OrdinalIgnoreCase))
                cash += sum;
            else
                nonCash += sum;
            total += sum;
            count++;
        }

        foreach (var sale in ledger)
        {
            if (queuedIds.Contains(sale.SaleId))
                continue;
            total += sale.Total;
            cash += sale.PaidCash;
            nonCash += sale.PaidNonCash;
            debt += sale.Debt;
            count++;
        }

        return count == 0 ? null : new Result(total, cash, nonCash, debt, count);
    }

    private static decimal TotalOf(OfflineSaleEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
            return Math.Round((decimal)CartTotalsCalculator.Calculate(doc.RootElement).TotalDue, 2);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        {
            PosLogger.Log($"Итоги смены по данным кассы: сумма чека {entry.Id} не прочитана: {ex.GetType().Name}", "WARNING");
            return 0m;
        }
    }
}
