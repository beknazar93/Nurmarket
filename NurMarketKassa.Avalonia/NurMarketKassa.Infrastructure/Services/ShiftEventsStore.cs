using System.Globalization;
namespace NurMarketKassa.Services;

/// <summary>
/// События смены, которых нет в серверных итогах: возвраты, списания, расходы и оплата долгов.
///
/// Почему локально, а не запросом к серверу: в итогах смены (api/construction/shifts/) таких
/// полей нет вовсе — там только выручка, наличные/безналичные и расход. Считать их запросами
/// по каждому чеку смены долго и не работает без связи, а итоги смены кассир смотрит в том
/// числе тогда, когда интернет уже отвалился. Поэтому касса записывает событие в момент, когда
/// сама его выполняет, — это дёшево и всегда доступно.
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ: цифры копятся с версии, где появилась эта таблица. У смен, закрытых
/// раньше, здесь пусто — не потому, что операций не было, а потому что их никто не записывал.
/// </summary>
public static class ShiftEventsStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    /// <summary>Возврат чека или его части — сумма, возвращённая покупателю.</summary>
    public const string KindReturn = "return";

    /// <summary>Списание товара со склада — сумма по закупочной цене, если она известна.</summary>
    public const string KindWriteOff = "writeoff";

    /// <summary>Расход: изъятие из кассы или строка «Доп. услуга» с отрицательной ценой.</summary>
    public const string KindExpense = "expense";

    /// <summary>Погашение долга клиентом.</summary>
    public const string KindDebtPayment = "debt_payment";

    /// <param name="sourceId">ID продажи/операции. Повторная запись с тем же id и видом
    /// события ничего не меняет — так повтор офлайн-очереди не задваивает суммы.</param>
    /// <summary>Ключ операции для source_id.
    ///
    /// На ShiftEvents(kind, source_id) стоит уникальный индекс, а запись идёт через
    /// INSERT OR IGNORE — это защита от двойной записи одной и той же операции. Но если в
    /// source_id класть ID продажи или сделки, то ВТОРАЯ реальная операция по тому же чеку
    /// (второй частичный возврат, второй платёж по долгу) молча пропадает из отчётов.
    /// Поэтому к идентификатору добавляется момент операции: каждая операция получает свою
    /// строку, а повтор одного и того же вызова в пределах миллисекунды по-прежнему отсекается.</summary>
    public static string OperationKey(string? id) =>
        (string.IsNullOrWhiteSpace(id) ? "op" : id.Trim())
        + ":" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

    public static void Record(string kind, string? shiftId, string? sourceId, double amount, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(kind) || Math.Abs(amount) < 1e-9)
            return;

        try
        {
            Db.RecordShiftEvent(kind, shiftId, sourceId, amount, note);
            // Возврат, списание, изъятие и оплата долга меняют Z-отчёт и аналитику.
            PosDataEvents.RaiseSalesChanged();
        }
        catch (Exception ex)
        {
            // Журнал итогов не должен ронять саму операцию — возврат или списание уже сделаны.
            PosLogger.Log($"Событие смены не записано ({kind}): {ex.Message}", "WARNING");
        }
    }

    /// <summary>Суммы по видам событий за смену. Ключ — вид (KindReturn и т.д.).</summary>
    public static Dictionary<string, double> TotalsForShift(string? shiftId) =>
        string.IsNullOrWhiteSpace(shiftId)
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : Db.GetShiftEventTotals(shiftId!);

    /// <summary>Суммы по видам событий за период (UTC) — для аналитики продаж.</summary>
    public static Dictionary<string, double> TotalsBetween(DateTime fromUtc, DateTime toUtc) =>
        Db.GetShiftEventTotalsBetween(fromUtc, toUtc);
}
