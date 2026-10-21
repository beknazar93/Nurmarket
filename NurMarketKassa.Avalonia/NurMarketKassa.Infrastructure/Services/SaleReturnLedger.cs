using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-20 (живой аудит кассы, К1/К6): строка чека, как она была в продаже до возвратов.</summary>
public sealed class SaleLedgerLine
{
    /// <summary>id строки продажи на сервере (sale_item_id); у снимка из окна оплаты — пусто.</summary>
    public string? LineId { get; set; }
    public string Name { get; set; } = "";
    public double Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Сумма строки после скидки на позицию, но до скидки на весь чек.</summary>
    public decimal Total { get; set; }
}

/// <summary>2026-10-20: один возврат по чеку, как его посчитала касса (сколько стоили возвращённые товары с долей
/// скидки на чек и чем их вернули покупателю).</summary>
public sealed class SaleLedgerRefund
{
    public DateTimeOffset At { get; set; }
    /// <summary>Стоимость возвращённого (с долей скидки на чек) — никогда не больше остатка оплаченного.</summary>
    public decimal Amount { get; set; }
    public decimal Cash { get; set; }
    public decimal NonCash { get; set; }
    /// <summary>На сколько уменьшен долг клиента (продажа «в долг»).</summary>
    public decimal DebtReduced { get; set; }
    /// <summary>Сумма документа возврата сервера (если её прочитали) — для сверки.</summary>
    public decimal? ServerAmount { get; set; }
    /// <summary>id документа возврата сервера — следующий возврат не примет его за свой.</summary>
    public string? ServerDocId { get; set; }
    public bool Whole { get; set; }
    public List<SaleLedgerLine> Lines { get; set; } = new();
}

/// <summary>2026-10-20, владелец (долг Д5): платёж графика долга — дата и сумма.</summary>
public sealed class SaleLedgerDuePayment
{
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>2026-10-20: продажа, как её заплатили, и все возвраты по ней с этой кассы.</summary>
public sealed class SaleLedgerEntry
{
    public string SaleId { get; set; } = "";
    public string? Number { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? SoldAt { get; set; }
    /// <summary>cash / transfer / mixed / debt (как в окне оплаты или payment_method сервера).</summary>
    public string Method { get; set; } = "";
    /// <summary>Итог продажи (к оплате, со всеми скидками) — потолок суммы всех возвратов.</summary>
    public decimal Total { get; set; }
    /// <summary>Сумма строк до скидки на весь чек (0 — неизвестна): доля скидки = Total / LinesTotal.</summary>
    public decimal LinesTotal { get; set; }
    /// <summary>Сколько денег заплачено при продаже наличными и безналом (у долга — предоплата).</summary>
    public decimal PaidCash { get; set; }
    public decimal PaidNonCash { get; set; }
    /// <summary>Получено от покупателя и сдача (наличные) — для «Истории чеков» после возврата.</summary>
    public decimal? Received { get; set; }
    public decimal? Change { get; set; }
    /// <summary>Снимок строк продажи до первого возврата (из окна возврата; у снимка из оплаты — пусто).</summary>
    public List<SaleLedgerLine> Lines { get; set; } = new();
    public List<SaleLedgerRefund> Refunds { get; set; } = new();

    /// <summary>2026-10-20, владелец (долг Д5): срок возврата долга и график рассрочки — для копии чека и «Истории чеков»
    /// (у сервера в продаже этих полей нет). Клиент долга — имя и телефон, как их выбрали в окне оплаты.</summary>
    public DateTime? DebtDueDate { get; set; }
    public List<SaleLedgerDuePayment> DebtSchedule { get; set; } = new();
    public string? ClientName { get; set; }
    public string? ClientPhone { get; set; }

    public decimal Refunded => Refunds.Sum(r => r.Amount);
    public decimal Remaining => Math.Max(0m, Total - Refunded);
    /// <summary>Доля скидки на весь чек: стоимость строки × Factor = сколько за неё заплатили.</summary>
    public decimal Factor => LinesTotal > 0.005m && Total > 0m ? Math.Min(1m, Total / LinesTotal) : 1m;
    /// <summary>Неоплаченная часть при продаже (долг клиента по этому чеку).</summary>
    public decimal Debt => Math.Max(0m, Total - PaidCash - PaidNonCash);
    /// <summary>Долг по чеку минус уменьшения долга возвратами с этой кассы. 2026-10-21 (ревью денег, п. 2): оплат долга
    /// журнал не знает — окно возврата берёт текущий остаток с сервера (remaining_debt сделки) и уменьшает долг не больше
    /// него; без ответа сервера — предупреждение кассиру проверить долг.</summary>
    public decimal DebtLeft => Math.Max(0m, Debt - Refunds.Sum(r => r.DebtReduced));
    public decimal CashLeft => Math.Max(0m, PaidCash - Refunds.Sum(r => r.Cash));
    public decimal NonCashLeft => Math.Max(0m, PaidNonCash - Refunds.Sum(r => r.NonCash));
}

/// <summary>2026-10-20 (живой аудит кассы, К1–К3, К6). Сервер NurCRM считает возврат по строкам без доли скидки на
/// чек и после первого частичного возврата убирает скидку из продажи (чек 001837: оплачено 75,00, вернул 30,00 + 50,00
/// = 80,00), а у продажи после возврата пересчитывает «Получено/Сдача». Касса запоминает продажу, как её заплатили
/// (при оплате — деньги, при первом открытии в окне возврата — строки), и каждый свой возврат: сумма возврата строки —
/// её доля с учётом скидки на чек, все возвраты чека вместе — не больше оплаченного, «к выдаче» — только деньги, которые
/// покупатель реально внёс (долг уменьшается, а не выдаётся). Файл общий для кассы и программы владельца на этом ПК
/// (%LOCALAPPDATA%\NurMarketKassa\sale-returns.json), хранится 45 дней и не больше 4000 продаж.</summary>
public static class SaleReturnLedger
{
    private const int MaxEntries = 4000;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(45);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "sale-returns.json");

    /// <summary>2026-10-21 (ревью денег, п. 3): копия прошлой версии журнала — читается, если основной файл не прочитан.</summary>
    private static readonly string BackupPath = FilePath + ".bak";

    private static readonly object Gate = new();
    private static Dictionary<string, SaleLedgerEntry>? _cache;

    public static SaleLedgerEntry? Get(string? saleId)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return null;
        lock (Gate)
            return Load().TryGetValue(saleId.Trim(), out var entry) ? entry : null;
    }

    /// <summary>Продажа только что оплачена (MainWindow после оплаты): деньги — как их приняли в окне оплаты.</summary>
    public static void RememberPayment(string saleId, decimal total, string method, decimal paidCash, decimal paidNonCash,
        decimal? received, decimal? change, decimal linesTotal, DateTimeOffset soldAt,
        DateTime? debtDueDate = null, IReadOnlyList<SaleLedgerDuePayment>? debtSchedule = null, string? clientName = null, string? clientPhone = null)
    {
        if (string.IsNullOrWhiteSpace(saleId) || total < 0m)
            return;
        Update(saleId, entry =>
        {
            // 2026-10-20 (долг Д5): срок и график долга, клиент — для копии чека.
            entry.DebtDueDate ??= debtDueDate;
            if (debtSchedule is { Count: > 0 } && entry.DebtSchedule.Count == 0)
                entry.DebtSchedule = debtSchedule.ToList();
            entry.ClientName ??= clientName;
            entry.ClientPhone ??= clientPhone;
            // Повторная запись той же продажи (повтор события) — деньги не трогаем, если уже были возвраты.
            if (entry.Refunds.Count > 0)
                return;
            entry.Total = total;
            entry.Method = method ?? "";
            entry.PaidCash = Math.Max(0m, paidCash);
            entry.PaidNonCash = Math.Max(0m, paidNonCash);
            entry.Received = received;
            entry.Change = change;
            if (linesTotal > 0m)
                entry.LinesTotal = linesTotal;
            entry.SoldAt = soldAt;
        });
    }

    /// <summary>Чек открыт в окне возврата, по нему ещё не было возвратов: строки и итог — с сервера (у сервера id строк).
    /// Деньги из оплаты (если касса их запомнила) не трогаем; иначе — из полей продажи сервера.</summary>
    public static SaleLedgerEntry RememberFromSale(string saleId, string? number, decimal total, decimal linesTotal,
        IReadOnlyList<SaleLedgerLine> lines, string? method, decimal? cashReceived, decimal? change, DateTimeOffset? soldAt)
    {
        return Update(saleId, entry =>
        {
            if (entry.Refunds.Count > 0)
                return;
            entry.Number = number ?? entry.Number;
            entry.Lines = lines.ToList();
            if (linesTotal > 0m)
                entry.LinesTotal = linesTotal;
            entry.SoldAt ??= soldAt;
            var knownFromPayment = !string.IsNullOrEmpty(entry.Method) && entry.Total > 0m;
            if (knownFromPayment)
                return;
            entry.Total = total;
            entry.Method = (method ?? "").Trim().ToLowerInvariant();
            var received = cashReceived is > 0m ? cashReceived.Value : 0m;
            switch (entry.Method)
            {
                case "cash":
                    entry.PaidCash = total;
                    entry.Received = cashReceived is > 0m ? cashReceived : null;
                    entry.Change = change;
                    break;
                case "mixed" or "split":
                    entry.PaidCash = Math.Min(total, received);
                    entry.PaidNonCash = Math.Max(0m, total - entry.PaidCash);
                    break;
                case "debt":
                    entry.PaidCash = Math.Min(total, received);
                    break;
                default:
                    entry.PaidNonCash = total;
                    break;
            }
        });
    }

    public static void AddRefund(string saleId, SaleLedgerRefund refund)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return;
        Update(saleId, entry => entry.Refunds.Add(refund));
    }

    private static SaleLedgerEntry Update(string saleId, Action<SaleLedgerEntry> change)
    {
        var id = saleId.Trim();
        lock (Gate)
        {
            // 2026-10-21 (ревью денег, п. 3): межпроцессный замок — касса и программа владельца пишут один файл,
            // без него две одновременные записи теряли одну из них.
            using var crossProcess = CrossProcessLock.Take();
            // Касса и программа владельца пишут один файл — перед записью перечитываем, чтобы не потерять чужую запись.
            _cache = null;
            var all = Load(out var readOk);
            if (!all.TryGetValue(id, out var entry))
            {
                entry = new SaleLedgerEntry { SaleId = id };
                all[id] = entry;
            }

            change(entry);
            // 2026-10-21 (ревью денег, п. 3): файл не прочитался (занят антивирусом/другой программой) — не перезаписываем
            // его одной записью: все сохранённые продажи пропали бы. Эта запись остаётся только в ответе вызывающему.
            if (readOk)
                Save(all);
            else
                PosLogger.Log($"Журнал возвратов по чекам не прочитан — запись по продаже {id} на диск не сохранена (файл не тронут).", "WARNING");
            return entry;
        }
    }

    private static Dictionary<string, SaleLedgerEntry> Load() => Load(out _);

    /// <summary>2026-10-21 (ревью денег, п. 3): основной файл не прочитан — для расчёта берём копию .bak (прошлая запись).
    /// readOk = false — файл занят или ошибка ввода-вывода: результат нельзя записывать поверх файла (копия на одну запись
    /// старше — потерялась бы последняя) и нельзя кэшировать. Файл испорчен (не JSON) — откладываем его рядом (.broken-…,
    /// не стираем), журнал продолжается с копии (или пустой) — иначе он не записывался бы больше никогда.</summary>
    private static Dictionary<string, SaleLedgerEntry> Load(out bool readOk)
    {
        readOk = true;
        if (_cache is not null)
            return _cache;
        var result = new Dictionary<string, SaleLedgerEntry>(StringComparer.OrdinalIgnoreCase);
        if (!TryRead(FilePath, result, out var mainError, out var mainBroken))
        {
            result.Clear();
            var bakOk = TryRead(BackupPath, result, out var bakError, out _);
            if (!bakOk)
                result.Clear();
            readOk = false;
            if (mainBroken)
            {
                try
                {
                    File.Move(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), true);
                    readOk = true;
                }
                catch (Exception ex)
                {
                    mainError += "; не отложен: " + ex.Message;
                }
            }

            PosLogger.Log($"Журнал возвратов по чекам не прочитан: {mainError}; копия .bak: {(bakOk ? "взята" : bakError)}"
                          + (readOk ? " — испорченный файл отложен (.broken-…), журнал продолжен." : " — запись в файл пропущена."), "WARNING");
        }

        if (readOk)
            _cache = result;
        return result;
    }

    /// <summary>Нет основного файла — успех (пустой журнал); нет копии — неуспех. broken — файл прочитан, но это не JSON журнала.</summary>
    private static bool TryRead(string path, Dictionary<string, SaleLedgerEntry> into, out string? error, out bool broken)
    {
        error = null;
        broken = false;
        try
        {
            if (!File.Exists(path))
            {
                if (path == FilePath)
                    return true;
                error = "нет копии";
                return false;
            }

            var list = JsonSerializer.Deserialize<List<SaleLedgerEntry>>(File.ReadAllText(path)) ?? new();
            foreach (var e in list.Where(e => !string.IsNullOrWhiteSpace(e.SaleId)))
                into[e.SaleId] = e;
            return true;
        }
        catch (JsonException ex)
        {
            broken = true;
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Save(Dictionary<string, SaleLedgerEntry> all)
    {
        try
        {
            var cutoff = DateTimeOffset.Now - KeepFor;
            foreach (var old in all.Values.Where(e => e.RecordedAt < cutoff).Select(e => e.SaleId).ToList())
                all.Remove(old);
            if (all.Count > MaxEntries)
            {
                foreach (var extra in all.Values.OrderBy(e => e.RecordedAt).Take(all.Count - MaxEntries).Select(e => e.SaleId).ToList())
                    all.Remove(extra);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList()));
            // 2026-10-21 (ревью денег, п. 3): атомарная замена с копией прошлой версии в .bak (её читает Load при сбое).
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(FilePath, BackupPath, true);
                }
            }

            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            // Журнал — подсказка для расчёта, не повод ломать возврат или оплату.
            PosLogger.Log($"Журнал возвратов по чекам не записан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-21 (ревью денег, п. 3): именованный Mutex на сеанс пользователя — общий у кассы и программы
    /// владельца. Где именованный Mutex не поддерживается (Android) или он занят дольше 5 с — пишем как раньше,
    /// под замком процесса (журнал — подсказка, возврат из-за него не ждёт).</summary>
    private sealed class CrossProcessLock : IDisposable
    {
        private Mutex? _mutex;

        public static CrossProcessLock Take()
        {
            var holder = new CrossProcessLock();
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, @"Local\NurMarketKassaSaleReturns");
                bool taken;
                try
                {
                    taken = mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    taken = true; // другая программа упала, держа замок, — замок наш
                }

                if (taken)
                {
                    holder._mutex = mutex;
                    return holder;
                }

                PosLogger.Log("Журнал возвратов по чекам: замок занят другой программой дольше 5 с — пишем без него.", "WARNING");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал возвратов по чекам: межпроцессный замок недоступен ({ex.GetType().Name}).", "INFO");
            }

            mutex?.Dispose();
            return holder;
        }

        public void Dispose()
        {
            if (_mutex is null)
                return;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // уже освобождён
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>«40,00» — для журнала и примечаний (точка, без разрядов).</summary>
    public static string Inv(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
