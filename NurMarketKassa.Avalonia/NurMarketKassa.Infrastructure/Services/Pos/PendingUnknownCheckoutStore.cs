using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (ревью денег, К-3): оплата «в долг» / смешанная на старом пути, ответ на которую не дошёл. Касса помнит её, чтобы
/// повтор «Оплатить» сначала сверил корзину с сервером, а не создал вторую продажу. Раньше запись жила только в памяти — перезапуск кассы
/// между попытками стирал защиту. Теперь запись лежит на диске рядом с остальными очередями (pending_debt_prepayments.json); таблицы
/// pos_local.db не затронуты. Запись одна (последняя неясная оплата); старше <see cref="MaxAge"/> — снимается с записью в журнал.</summary>
public static class PendingUnknownCheckoutStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Дольше этого неясная оплата уже не сверяется: чек давно ушёл дальше, кассир увидит продажу в «Продажах».</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>Путь к файлу. Стенд может подменить, чтобы не трогать настоящий профиль.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "pending_unknown_checkout.json");

    public sealed class Entry
    {
        /// <summary>Серверная корзина, на которую ушёл checkout без ответа.</summary>
        public string CartId { get; set; } = "";

        /// <summary>«Отпечаток» состава чека и его строки (товар|кол-во|цена) — для сравнения «тот же чек».</summary>
        public string Signature { get; set; } = "";

        public List<string> Lines { get; set; } = new();

        /// <summary>Жетон чека (CartJsonHelper.CheckTokenField) на момент попытки; пусто — у чека жетона не было.</summary>
        public string? CheckToken { get; set; }

        public DateTime AtUtc { get; set; } = DateTime.UtcNow;

        public string Method { get; set; } = "";

        public string? ClientId { get; set; }

        /// <summary>Наличная и безналичная части как в запросе оплаты — предоплата долга другой суммы не «тот же» платёж.</summary>
        public string? CashReceived { get; set; }

        public string? NonCashReceived { get; set; }

        /// <summary>Итог окна оплаты (со скидкой) — другая скидка при повторе означает другой платёж.</summary>
        public double? ExpectedTotal { get; set; }

        public bool HadCashPart { get; set; }

        /// <summary>Кассиру уже показано «эти позиции проведены, повторно не проводим» — второй раз тем же сообщением не пугаем.</summary>
        public bool Warned { get; set; }

        /// <summary>2026-10-29 (аварийный аудит, п. 2): «в долг»/смешанная на быстром пути (pos/checkout с Idempotency-Key), ответ не дошёл. Серверной корзины
        /// нет (CartId = «quick:ключ»), сверка — повтором ТОГО ЖЕ тела тем же ключом: сервер вернёт прежнюю продажу (replayed), а не создаст вторую.
        /// Ключ и тело лежат на диске, поэтому перезапуск кассы защиту не стирает.</summary>
        public string? QuickKey { get; set; }

        /// <summary>Тело запроса быстрого пути, для которого выдан <see cref="QuickKey"/> (сравнение «тот же платёж»).</summary>
        public string? QuickBody { get; set; }

        /// <summary>2026-10-29 (приёмка 1.17.65, В-1): запись сделана ДО отправки онлайн-оплаты («в полёте»): ключ и тело на диске, ответа ещё нет.
        /// Если касса упала посреди оплаты, после запуска исход сверяется по ключу (idempotency/{key}), а не угадывается.</summary>
        public bool InFlight { get; set; }

        /// <summary>2026-10-29 (В-1): сервер подтвердил по ключу, что продажа проведена — тот же чек больше не пробиваем, пока кассир не начнёт новый.</summary>
        public bool PaidConfirmed { get; set; }
    }

    // 2026-11-02 (аудит №2 кассы, К3-01): «файл занят» ≠ «неясной оплаты нет». Раньше ошибка чтения давала null — запись «в полёте» не восстанавливалась, и повтор «Оплатить»
    // уходил с новым ключом (вторая продажа). Теперь: 5 повторов чтения, затем последняя известная запись (память) или копия .bak, и только потом null.
    private static Entry? _lastKnown;

    public static Entry? Load()
    {
        lock (Gate)
        {
            try
            {
                var read = AtomicFile.TryRead(FilePath, attempts: 5, pauseMs: 150);
                Entry? entry = null;
                if (read.Text is { } text)
                    entry = JsonSerializer.Deserialize<Entry>(text, Json);
                if (read.Status == AtomicFile.ReadStatus.Unreadable)
                {
                    entry ??= _lastKnown;
                    PosLogger.Log(entry is null
                        ? "Запись неясной оплаты занята другой программой и не прочитана; последней известной записи нет — если оплата зависла, проверьте «Продажи» перед повтором."
                        : "Запись неясной оплаты занята другой программой — используем последнюю известную.", "WARNING");
                }
                if (entry == null || string.IsNullOrWhiteSpace(entry.CartId))
                {
                    if (read.Status != AtomicFile.ReadStatus.Unreadable)
                        _lastKnown = null;
                    return null;
                }
                if (DateTime.UtcNow - entry.AtUtc > MaxAge)
                {
                    PosLogger.Log($"Неясная оплата корзины {entry.CartId} ({entry.Method}) старше {MaxAge.TotalHours:0} ч — сверка снята, проверьте «Продажи».", "WARNING");
                    ClearNoLock();
                    return null;
                }

                _lastKnown = entry;
                return entry;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Запись неясной оплаты не прочитана: {ex.Message}", "WARNING");
                return _lastKnown;
            }
        }
    }

    public static void Save(Entry entry)
    {
        lock (Gate)
        {
            _lastKnown = entry; // 2026-11-02 (К3-01)
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                // 2026-10-29 (В-1): запись «в полёте» должна пережить мгновенный сбой процесса/питания — пишем во временный файл, сбрасываем
                // на диск (fsync) и только потом атомарно подменяем рабочий.
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = new System.Text.UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(entry, Json));
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tmp, FilePath, overwrite: true);
                AtomicFile.CancelDeferred(FilePath); // 2026-11-02: записали напрямую — отложенное состояние устарело
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Запись неясной оплаты не сохранена сразу: {ex.Message}", "WARNING");
                // 2026-11-02 (К3-01, приёмка круга 3): файл занят — запись откладывается (память читается первой, таймер допишет, при закрытии — запасной файл *.pending).
                try
                {
                    AtomicFile.DeferWrite(FilePath, "pending-unknown-checkout.json", JsonSerializer.Serialize(entry, Json));
                }
                catch (Exception deferEx)
                {
                    PosLogger.Log($"Запись неясной оплаты не отложена: {deferEx.Message}", "ERROR");
                }
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
            ClearNoLock();
    }

    private static void ClearNoLock()
    {
        _lastKnown = null;
        AtomicFile.CancelDeferred(FilePath); // 2026-11-02: и отложенная запись, и запасной файл
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Запись неясной оплаты не удалена: {ex.Message}", "WARNING");
        }
    }
}
