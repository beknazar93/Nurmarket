using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-03 (аудит №3, Д3-01; С-08/С-09/С-12): денежный запрос (возврат, обмен, оплата долга, зарплата, прокат, перемещение) на медленном сервере.
/// Правило: запрос НЕ отменяем по сроку и НЕ задваиваем (тот же Idempotency-Key, что и всегда), но окно не держим дольше
/// <see cref="ServerAnswerWait.ForegroundBudget"/> (8 с): кассир видит понятный текст, окно освобождается, ответ дожидается и обрабатывается тем же кодом
/// (успех, ошибка, чек), что и при быстром ответе. Повторить ту же операцию до ответа нельзя (<see cref="IsActive"/>).</summary>
public static class SlowMoneyCall
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Эта операция (по ключу вида «return|id чека») ещё ждёт ответа сервера — повторно не отправлять.</summary>
    public static bool IsActive(string opKey)
    {
        lock (Sync)
            return Active.Contains(opKey);
    }

    /// <summary>Текст для окна, когда операция по ключу ещё выполняется.</summary>
    public static string StillRunningText => Tr.T(
        "Операция ещё выполняется: сервер отвечает медленно. Дождитесь ответа, повторно не нажимайте.",
        "Операция дагы эле аткарылууда: сервер жай жооп берүүдө. Жоопту күтүңүз, кайра баспаңыз.",
        "The operation is still in progress: the server is responding slowly. Wait for the answer, do not press again.",
        "İşlem hâlâ sürüyor: sunucu yavaş yanıt veriyor. Yanıtı bekleyin, tekrar basmayın.",
        "Amal hali bajarilmoqda: server sekin javob bermoqda. Javobni kuting, qayta bosmang.");

    /// <summary>Плашка, когда за 8 с ответа нет, а операция отправлена.</summary>
    public static string PendingText => Tr.T(
        "Сервер отвечает медленно. Операция отправлена; результат появится в журнале и в чеке. Повторно не нажимайте.",
        "Сервер жай жооп берүүдө. Операция жөнөтүлдү; жыйынтык журналда жана чекте чыгат. Кайра баспаңыз.",
        "The server is responding slowly. The operation was sent; the result will appear in the log and on the receipt. Do not press again.",
        "Sunucu yavaş yanıt veriyor. İşlem gönderildi; sonuç günlükte ve fişte görünecek. Tekrar basmayın.",
        "Server sekin javob bermoqda. Amal yuborildi; natija jurnalda va chekda chiqadi. Qayta bosmang.");

    /// <summary>Отправляет денежный запрос. Ответ пришёл за 8 с — возвращает его. Нет — вызывает <paramref name="onReleased"/> (окно освобождается: IsBusy = false и т. п.),
    /// показывает плашку и ДОЖИДАЕТСЯ ответа (запрос не отменяется); ошибка пробрасывается как обычно — в те же catch окна. Вызывать из потока интерфейса.
    /// <paramref name="send"/> получает <see cref="CancellationToken.None"/>: срок запроса задаёт сам HttpClient (как и раньше).</summary>
    public static async Task<T> RunAsync<T>(string context, string opKey, Func<CancellationToken, Task<T>> send, Action? onReleased = null)
    {
        lock (Sync)
            Active.Add(opKey);
        try
        {
            var write = await ServerAnswerWait.ForegroundWriteAsync(context, send).ConfigureAwait(true);
            if (!write.Pending)
                return write.Value!;

            try { onReleased?.Invoke(); }
            catch (Exception ex) { PosLogger.Log($"{context}: освобождение окна не удалось: {ex.GetType().Name}", "WARNING"); }
            SuccessToast.Show(PendingText, ToastKind.Warning, TimeSpan.FromSeconds(20), null, sticky: false);
            return await write.Completion.ConfigureAwait(true);
        }
        finally
        {
            lock (Sync)
                Active.Remove(opKey);
        }
    }
}
