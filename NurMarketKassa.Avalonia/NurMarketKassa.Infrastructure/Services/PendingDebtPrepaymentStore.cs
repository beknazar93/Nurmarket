using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (аудит кассы, С-3): предоплата продажи «в долг», которую касса довносит отдельным запросом после проведения
/// продажи. Раньше довнесение жило только в фоновой задаче (повторы 3, 8, 15, 30 с): закрыли кассу — предоплата терялась, долг
/// клиента оставался полным, хотя деньги взяты. Теперь запись ставится на диск ДО фоновой попытки, снимается после успеха, а
/// не доехавшие досылаются при следующем проходе синхронизации (в том числе после перезапуска). Отдельный файл рядом с
/// другими очередями (pending_shift_closes.json); таблицы pos_local.db не затронуты.
///
/// Повторная оплата одного взноса исключена: перед каждой попыткой пишется ключ идемпотентности, подпись тела и остаток долга
/// «до» — после перезапуска исход прошлой попытки без ответа выясняется по сделке (DebtPaymentLooksApplied), а тот же ключ
/// сервер вернёт как повтор.</summary>
public static class PendingDebtPrepaymentStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Запись старше этого срока считается потерянной и снимается (с записью в журнал): предоплату нужно вносить вручную.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Путь к файлу. Тест/стенд может подменить, чтобы не трогать настоящий профиль.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "pending_debt_prepayments.json");

    public sealed class Entry
    {
        public string SaleId { get; set; } = "";

        /// <summary>Сумма предоплаты как в запросе оплаты («0.00»).</summary>
        public string CashReceived { get; set; } = "";

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public int Attempts { get; set; }

        /// <summary>Ключ идемпотентности и подпись тела («взнос|сумма») последней начатой попытки оплаты.</summary>
        public string? PayKey { get; set; }

        public string? PaySignature { get; set; }

        /// <summary>Взнос и остаток долга ДО последней начатой попытки: по ним выясняется, прошла ли она, если ответ потерян.</summary>
        public string? InstallmentId { get; set; }

        public decimal? DebtBefore { get; set; }

        /// <summary>2026-10-27 (ревью денег, К-8): когда начата попытка оплаты с известными ключом и остатком «до» (UTC). Сверка «долг уменьшился —
        /// значит прошла» верна только вскоре после попытки: через дни остаток меняют другие платежи клиента.</summary>
        public DateTime? AttemptUtc { get; set; }

        /// <summary>2026-10-27 (К-8): исход прошлой попытки через дни установить нельзя — касса её не повторяет и не засчитывает, ждёт ручной проверки.</summary>
        public bool ManualCheck { get; set; }

        /// <summary>2026-10-27 (К-9): компания, в которой поставлена запись, — чужие записи другой компании касса не трогает.</summary>
        public string? CompanyId { get; set; }
    }

    /// <summary>2026-10-27 (К-8): пока с начала попытки с неизвестным исходом прошло не больше этого, остаток долга и тот же ключ идемпотентности
    /// надёжно показывают, прошла ли она. Позже касса сама ничего не решает: ни повтора, ни «уже прошла».</summary>
    public static readonly TimeSpan UnknownAttemptWindow = TimeSpan.FromMinutes(10);

    /// <summary>2026-10-27 (К-8): до скольки касса надеется на ключ идемпотентности сервера (повтор тем же ключом и телом вернёт прежний итог).
    /// Срок хранения ключей на сервере НЕ подтверждён (вопрос Умару); старше — только ручная проверка.</summary>
    public static readonly TimeSpan UnknownAttemptKeyLifetime = TimeSpan.FromHours(24);

    public static List<Entry> LoadAll()
    {
        lock (Gate)
            return LoadAllNoLock();
    }

    /// <summary>2026-10-30 (приёмка 1.17.65, блокер 3): последнее чтение упало из-за «файл занят» — SaveNoLock тогда не пишет (иначе «пусто» затёрло бы очередь).</summary>
    private static bool _readBusy;

    private static List<Entry> LoadAllNoLock()
    {
        _readBusy = false;
        try
        {
            if (!File.Exists(FilePath))
                return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(AtomicFile.ReadWithRetry(FilePath), Json) ?? new List<Entry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _readBusy = true;
            PosLogger.Log($"Очередь довнесения предоплат долга: файл занят ({ex.Message}) — запись отложена, прежнее не затираем.", "WARNING");
            return new List<Entry>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Очередь довнесения предоплат долга не прочитана: {ex.Message}", "WARNING");
            return new List<Entry>();
        }
    }

    private static void SaveNoLock(List<Entry> all)
    {
        if (_readBusy)
            return; // 2026-10-30: файл не прочитан («занят») — пишем только после успешного чтения
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Очередь довнесения предоплат долга не записана: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Ставит предоплату в очередь. Та же продажа повторно не дублируется.</summary>
    public static void Add(string saleId, string? cashReceived)
    {
        if (string.IsNullOrWhiteSpace(saleId) || string.IsNullOrWhiteSpace(cashReceived))
            return;
        lock (Gate)
        {
            var all = LoadAllNoLock();
            if (all.Any(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase)))
                return;
            all.Add(new Entry { SaleId = saleId.Trim(), CashReceived = cashReceived.Trim(), CompanyId = CompanyInfoService.LastCompany?.Id });
            SaveNoLock(all);
        }
    }

    public static void Remove(string saleId)
    {
        lock (Gate)
        {
            var all = LoadAllNoLock();
            if (all.RemoveAll(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveNoLock(all);
        }
    }

    public static Entry? Get(string saleId)
    {
        lock (Gate)
            return LoadAllNoLock().FirstOrDefault(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Запись перед оплатой взноса: ключ, подпись, взнос и остаток долга «до».</summary>
    public static void NoteAttempt(string saleId, string payKey, string paySignature, string? installmentId, decimal? debtBefore)
    {
        lock (Gate)
        {
            var all = LoadAllNoLock();
            var entry = all.FirstOrDefault(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;
            entry.Attempts++;
            // 2026-10-27 (К-8): тот же ключ = повтор той же операции с неизвестным исходом — остаток «до» и время берём от ПЕРВОЙ попытки
            // (остаток, прочитанный при повторе, уже мог быть уменьшен дошедшей оплатой).
            var sameOperation = !string.IsNullOrEmpty(entry.PayKey) && string.Equals(entry.PayKey, payKey, StringComparison.Ordinal) && entry.AttemptUtc != null;
            entry.PayKey = payKey;
            entry.PaySignature = paySignature;
            if (!sameOperation)
            {
                entry.InstallmentId = installmentId;
                entry.DebtBefore = debtBefore;
                entry.AttemptUtc = DateTime.UtcNow;
            }
            SaveNoLock(all);
        }
    }

    /// <summary>2026-10-27 (К-8): запись больше не повторяется автоматически — исход прошлой попытки через дни не установить, проверка вручную.</summary>
    public static void MarkManualCheck(string saleId)
    {
        lock (Gate)
        {
            var all = LoadAllNoLock();
            var entry = all.FirstOrDefault(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.ManualCheck)
                return;
            entry.ManualCheck = true;
            SaveNoLock(all);
        }
    }

    /// <summary>Сервер отказал по существу (4xx): прошлая попытка не прошла, её ключ и остаток долга «до» не переносятся.</summary>
    public static void ClearAttempt(string saleId)
    {
        lock (Gate)
        {
            var all = LoadAllNoLock();
            var entry = all.FirstOrDefault(e => string.Equals(e.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;
            entry.PayKey = null;
            entry.PaySignature = null;
            entry.InstallmentId = null;
            entry.DebtBefore = null;
            entry.AttemptUtc = null;
            SaveNoLock(all);
        }
    }

    /// <summary>Снимает записи старше <see cref="MaxAge"/>.</summary>
    public static void DropExpired()
    {
        lock (Gate)
        {
            var all = LoadAllNoLock();
            var limit = DateTime.UtcNow - MaxAge;
            var dropped = all.Where(e => e.CreatedUtc < limit).ToList();
            if (dropped.Count == 0)
                return;
            foreach (var e in dropped)
                PosLogger.Log($"Предоплата долга по продаже {e.SaleId} ({e.CashReceived}) не довнесена за {MaxAge.TotalDays:0} дн. — снята с очереди, внесите вручную через «Оплата долга».", "WARNING");
            all.RemoveAll(e => e.CreatedUtc < limit);
            SaveNoLock(all);
        }
    }
}
