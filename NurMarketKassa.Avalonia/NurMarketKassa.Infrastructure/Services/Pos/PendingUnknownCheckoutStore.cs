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

    public static Entry? Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                var entry = JsonSerializer.Deserialize<Entry>(AtomicFile.ReadWithRetry(FilePath), Json); // 2026-10-30: «файл занят» — повтор 3×50 мс
                if (entry == null || string.IsNullOrWhiteSpace(entry.CartId))
                    return null;
                if (DateTime.UtcNow - entry.AtUtc > MaxAge)
                {
                    PosLogger.Log($"Неясная оплата корзины {entry.CartId} ({entry.Method}) старше {MaxAge.TotalHours:0} ч — сверка снята, проверьте «Продажи».", "WARNING");
                    ClearNoLock();
                    return null;
                }

                return entry;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Запись неясной оплаты не прочитана: {ex.Message}", "WARNING");
                return null;
            }
        }
    }

    public static void Save(Entry entry)
    {
        lock (Gate)
        {
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
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Запись неясной оплаты не сохранена (после перезапуска сверки не будет): {ex.Message}", "WARNING");
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
