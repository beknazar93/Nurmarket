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

    /// <summary>Погашение долга клиентом наличными (в ящик). Записи до 2026-10-18 — все этого вида (касса принимала только наличные).</summary>
    public const string KindDebtPayment = "debt_payment";

    /// <summary>2026-10-18, ревью: погашение долга безналом (карта, перевод) — отдельным видом, чтобы в Z-отчёте, отчёте
    /// Telegram и плитке смены оно не выглядело деньгами в ящике. Схема таблицы та же (kind — текст).</summary>
    public const string KindDebtPaymentNonCash = "debt_payment_noncash";

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

    public static void Record(string kind, string? shiftId, string? sourceId, double amount, string? note = null) =>
        TryRecord(kind, shiftId, sourceId, amount, note);

    /// <summary>2026-10-18: то же, что Record, но сообщает, записалось ли событие (оплата долга предупреждает кассира, если
    /// в смену не попало). Нулевая сумма — записывать нечего, true.</summary>
    public static bool TryRecord(string kind, string? shiftId, string? sourceId, double amount, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(kind) || Math.Abs(amount) < 1e-9)
            return true;

        try
        {
            Db.RecordShiftEvent(kind, shiftId, sourceId, amount, note);
            // Возврат, списание, изъятие и оплата долга меняют Z-отчёт и аналитику.
            PosDataEvents.RaiseSalesChanged();
            return true;
        }
        catch (Exception ex)
        {
            // Журнал итогов не должен ронять саму операцию — возврат или списание уже сделаны.
            PosLogger.Log($"Событие смены не записано ({kind}): {ex.Message}", "WARNING");
            return false;
        }
    }

    /// <summary>2026-11-12 (аудит К-1): возврат от клиента изменили или аннулировали на сервере — в смене записывается сторно (отрицательное событие «return» с той же разбивкой
    /// cash/noncash/debt в примечании; положительное — если возврат увеличили). Суммы смены (X/Z, окно закрытия, «в ящике должно быть») складывают события, поэтому
    /// сходятся с сервером. Нет смены (программа владельца) или нулевая разница — не пишем. true — записано.
    /// Источник события — «id чека:время» (как у обычного возврата), чтобы отсечение по отменённым чекам работало одинаково.</summary>
    /// <param name="previousShift">2026-11-13 (проверка денег 1.17.86, п. 7): возврат был оформлен в ПРОШЛОЙ смене — деньги движутся сегодня, поэтому сторно пишется в текущую смену,
    /// но с пометкой <c>prev=1</c>: окно закрытия, X/Z и раскладка ящика показывают его отдельной строкой «Отмена возврата прошлой смены», а не прячут в минус «Возвратов».</param>
    public static bool RecordReturnDelta(string? shiftId, string? saleId, decimal amount, decimal cash, decimal nonCash, decimal debt, bool previousShift = false)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || Math.Abs(amount) < 0.005m)
            return false;
        var note = $"cash={SaleReturnLedger.Inv(cash)};noncash={SaleReturnLedger.Inv(nonCash)};debt={SaleReturnLedger.Inv(debt)};storno=1" + (previousShift ? ";prev=1" : "");
        return TryRecord(KindReturn, shiftId, OperationKey(saleId), (double)amount, note);
    }

    /// <summary>2026-11-13: значение «ключ=число» из примечания события (так же, как CloseShiftDialog.ParseNote, но доступно ядру и тестам).</summary>
    public static double? NoteNumber(string? note, string key)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        foreach (var part in note.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0].Trim(), key, StringComparison.Ordinal)
                && double.TryParse(kv[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    /// <summary>2026-11-13 (п. 7): запись возврата — сторно возврата прошлой смены (prev=1).</summary>
    public static bool IsPreviousShiftNote(string? note) => NoteNumber(note, "prev") is { } v && v > 0.5;

    /// <summary>2026-11-13 (п. 7): возвраты смены двумя частями — обычные (этой смены и сторно её же возвратов) и «отмена возврата прошлой смены» (prev=1). Суммы со знаком: у отмены
    /// возврата прошлой смены сумма отрицательная (деньги вернулись в ящик). Записи до 20.10 без разбивки — вся сумма «из ящика». Итог по ящику = наличные обеих частей.</summary>
    public sealed record ShiftReturnTotals(int Count, decimal Amount, decimal Cash, decimal NonCash, decimal Debt, int PrevCount, decimal PrevAmount, decimal PrevCash, decimal PrevNonCash, decimal PrevDebt)
    {
        public decimal TotalCash => Cash + PrevCash;
        public decimal TotalAmount => Amount + PrevAmount;
    }

    public static ShiftReturnTotals ReturnTotalsForShift(string? shiftId)
    {
        int n = 0, pn = 0;
        decimal amount = 0, cash = 0, non = 0, debt = 0, pAmount = 0, pCash = 0, pNon = 0, pDebt = 0;
        foreach (var (_, a, note) in ListForShift(shiftId, KindReturn))
        {
            var amt = (decimal)a;
            var hasSplit = NoteNumber(note, "cash") is not null;
            var c = (decimal)(NoteNumber(note, "cash") ?? a);
            var nc = hasSplit ? (decimal)(NoteNumber(note, "noncash") ?? 0) : 0m;
            var d = hasSplit ? (decimal)(NoteNumber(note, "debt") ?? 0) : 0m;
            if (IsPreviousShiftNote(note))
            {
                pn++; pAmount += amt; pCash += c; pNon += nc; pDebt += d;
            }
            else
            {
                n++; amount += amt; cash += c; non += nc; debt += d;
            }
        }
        return new ShiftReturnTotals(n, amount, cash, non, debt, pn, pAmount, pCash, pNon, pDebt);
    }

    /// <summary>Суммы по видам событий за смену. Ключ — вид (KindReturn и т.д.).</summary>
    public static Dictionary<string, double> TotalsForShift(string? shiftId) =>
        string.IsNullOrWhiteSpace(shiftId)
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : Db.GetShiftEventTotals(shiftId!);

    /// <summary>2026-11-01 (code-review, Ж-01): сколько оплат долга (наличные + безнал) этого клиента записано на этой кассе за всё время (примечание события — имя клиента).
    /// Пустое имя — 0 (оплаты не подтверждены).</summary>
    public static double DebtPaidByClientName(string? clientName, bool cashOnly = false, DateTime? sinceUtc = null) =>
        string.IsNullOrWhiteSpace(clientName)
            ? 0
            : Db.SumShiftEventsByNote(cashOnly ? new[] { KindDebtPayment } : new[] { KindDebtPayment, KindDebtPaymentNonCash }, clientName.Trim(), sinceUtc);

    /// <summary>Записи одного вида за смену — подробный отчёт по плитке смены.</summary>
    public static List<(DateTime CreatedAt, double Amount, string? Note)> ListForShift(string? shiftId, string kind) =>
        string.IsNullOrWhiteSpace(shiftId)
            ? new List<(DateTime, double, string?)>()
            : Db.ListShiftEvents(shiftId!, kind);

    /// <summary>Суммы по видам событий за период (UTC) — для аналитики продаж.</summary>
    /// <summary>Возвраты за период без возвратов по отменённым на сервере чекам: такой чек целиком
    /// выпадает из выручки сам, и его возврат иначе вычелся бы второй раз.</summary>
    public static double ReturnsBetweenExcluding(DateTime fromUtc, DateTime toUtc, IReadOnlySet<string> canceledSaleIds) =>
        Db.SumShiftEventsBetweenExcluding(KindReturn, fromUtc, toUtc, canceledSaleIds);

    public static Dictionary<string, double> TotalsBetween(DateTime fromUtc, DateTime toUtc) =>
        Db.GetShiftEventTotalsBetween(fromUtc, toUtc);
}
