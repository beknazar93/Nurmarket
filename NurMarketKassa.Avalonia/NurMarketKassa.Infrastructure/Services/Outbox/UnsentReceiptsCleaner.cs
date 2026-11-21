using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.Services;

// 2026-11-21, просьба владельца: «Кнопка «Очистить неотправленные чеки» надо добавить в кассу». Деньги: чек, пробитый на кассе, не должен пропасть молча.
// Порядок строгий: права руководителя + PIN → план (что можно очистить, что сейчас отправляется — не трогаем) → РЕЗЕРВНАЯ КОПИЯ (JSON + CSV) в папке данных
// (backup/unsent-receipts/<дата-время>) и её проверка → только потом закрытие чеков без отправки → журнал кассы и журнал действий → обновление счётчиков.
// Чек не удаляется физически: запись остаётся в pos_local.db со статусом «закрыт без отправки» (ClosedWithoutSendAt / ClosedReason), как у «Закрыть без отправки» в «Некорректных чеках».

/// <summary>Одна строка плана очистки (один неотправленный чек).</summary>
public sealed record UnsentClearLine(string Id, DateTimeOffset CreatedAt, string Status, decimal Amount, string Payment, string? Cashier, string? ShiftId,
    int LinesCount, string? LastError, bool Fiscal, bool MaybeOnServer, bool Debt);

/// <summary>План очистки: что будет очищено и что пропущено (и почему). Считается и для окна подтверждения, и перед самой очисткой.</summary>
public sealed class UnsentClearPlan
{
    public IReadOnlyList<UnsentClearLine> Items { get; init; } = Array.Empty<UnsentClearLine>();

    /// <summary>Id чеков, которые не трогаем: прямо сейчас отправляются, уже проведены на сервере, нет такой записи.</summary>
    public IReadOnlyList<string> SkippedIds { get; init; } = Array.Empty<string>();

    public int Count => Items.Count;
    public decimal Total => Items.Sum(i => i.Amount);
    public DateTimeOffset? Oldest => Items.Count == 0 ? null : Items.Min(i => i.CreatedAt);

    /// <summary>Чеки, у которых уже есть фискальные данные (ККМ приняла чек): ФД/ФМ в налоговой остаются, а продажи в учёте не будет.</summary>
    public int FiscalCount => Items.Count(i => i.Fiscal);

    /// <summary>Чеки, отправка которых уже начиналась — продажа могла дойти до сервера (ответ потерялся).</summary>
    public int MaybeOnServerCount => Items.Count(i => i.MaybeOnServer);

    public int DebtCount => Items.Count(i => i.Debt);

    public IReadOnlyList<string> Ids => Items.Select(i => i.Id).ToList();
}

public enum UnsentClearOutcome { Done, NotAllowed, PinRequired, NothingToClear, BackupFailed, Failed }

public sealed record UnsentClearResult(UnsentClearOutcome Outcome, int Cleared, int Skipped, string? BackupFolder, string Message)
{
    public bool Done => Outcome == UnsentClearOutcome.Done;
}

/// <summary>«Очистить неотправленные чеки»: логика без окон (окно и PIN — в проекте кассы, UnsentClearFlow). PIN проверяет вызывающий (сервер) и передаёт <c>pinVerified</c>;
/// здесь он не хранится и в журнал не попадает.</summary>
public static class UnsentReceiptsCleaner
{
    /// <summary>Корень резервных копий: папка данных кассы (или программы владельца) \data\backup\unsent-receipts. Тесты подставляют свой.</summary>
    public static string BackupRoot => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "data", "backup", "unsent-receipts");

    private static readonly JsonSerializerOptions BackupJson = new() { WriteIndented = true };

    /// <summary>Что очистится, если очистить эти чеки (id из очереди чеков). <paramref name="isSendingNow"/> — замок ручной отправки (ключ «sale:id»); по умолчанию — замок OutboxService.</summary>
    public static UnsentClearPlan Plan(IEnumerable<string> ids, Func<string, bool>? isSendingNow = null, FiscalQueueStore? fiscal = null)
    {
        isSendingNow ??= key => OutboxService.Default.IsSending(key);
        var fiscalDocs = LoadAcceptedFiscalDocIds(fiscal);
        var items = new List<UnsentClearLine>();
        var skipped = new List<string>();
        foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var e = OfflinePendingSalesStore.TryGetById(id);
            if (e is null || e.IsAutonomous || e.CheckoutCompleted)
            {
                skipped.Add(id);
                continue;
            }
            var pending = string.Equals(e.Status, OfflineSaleEntry.PendingSync, StringComparison.OrdinalIgnoreCase);
            var failed = string.Equals(e.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase);
            // «Отправляется» (автоматика или ручная отправка) и закрытые/отправленные — не трогаем.
            if ((!pending && !failed) || isSendingNow(SalesQueueOutboxSource.SourceName + ":" + e.Id))
            {
                skipped.Add(id);
                continue;
            }
            items.Add(ToLine(e, fiscalDocs));
        }
        return new UnsentClearPlan { Items = items.OrderBy(i => i.CreatedAt).ToList(), SkippedIds = skipped };
    }

    /// <summary>Очистить: права → PIN → план → резервная копия (и её проверка) → закрытие без отправки → журналы → обновление счётчиков.
    /// Любая ошибка до закрытия (права, PIN, копия) — ничего не тронуто.</summary>
    public static UnsentClearResult Clear(IEnumerable<string> ids, OutboxAccess who, bool pinVerified, string? approvedBy,
        string? backupRoot = null, Func<string, bool>? isSendingNow = null, FiscalQueueStore? fiscal = null)
    {
        if (!who.IsAdmin)
            return new UnsentClearResult(UnsentClearOutcome.NotAllowed, 0, 0, null, Texts.NotAllowed);
        if (!pinVerified)
            return new UnsentClearResult(UnsentClearOutcome.PinRequired, 0, 0, null, Texts.PinRequired);

        var plan = Plan(ids, isSendingNow, fiscal);
        if (plan.Count == 0)
            return new UnsentClearResult(UnsentClearOutcome.NothingToClear, 0, plan.SkippedIds.Count, null, Texts.Nothing);

        var who2 = string.IsNullOrWhiteSpace(who.UserName) ? "—" : who.UserName!;
        var approved = string.IsNullOrWhiteSpace(approvedBy) ? who2 : approvedBy!;
        var now = DateTimeOffset.Now;
        string folder;
        try
        {
            folder = WriteBackup(plan, who2, approved, now, backupRoot ?? BackupRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Копия не сохранена — чеки НЕ трогаем.
            PosLogger.Log($"Очистка неотправленных чеков ОТМЕНЕНА: резервная копия не записана ({ex.GetType().Name}: {ex.Message}). Чеки не тронуты.", "ERROR");
            return new UnsentClearResult(UnsentClearOutcome.BackupFailed, 0, plan.SkippedIds.Count, null, Texts.BackupFailed(ex.Message));
        }

        var reason = Texts.ClosedReason(who2, now);
        var cleared = new List<UnsentClearLine>();
        var notCleared = new List<string>(plan.SkippedIds);
        foreach (var line in plan.Items)
        {
            // Последняя проверка прямо перед закрытием: чек мог уйти в отправку, пока писалась копия.
            var sendingNow = (isSendingNow ?? (k => OutboxService.Default.IsSending(k)))(SalesQueueOutboxSource.SourceName + ":" + line.Id);
            if (!sendingNow && OfflinePendingSalesStore.CloseUnsentWithoutSending(line.Id, reason))
                cleared.Add(line);
            else
                notCleared.Add(line.Id);
        }

        var total = cleared.Sum(c => c.Amount);
        var summary = $"очищено чеков {cleared.Count} из {plan.Count} на {OutboxFormat.Money(total)} сом (пропущено {notCleared.Count}), в т.ч. фискальных {cleared.Count(c => c.Fiscal)}; кто: {who2}, PIN подтвердил: {approved}; копия: {folder}";
        OutboxService.Audit("Неотправленное: УБРАНЫ неотправленные чеки без отправки — " + summary + $", когда: {now:dd.MM.yyyy HH:mm:ss}.");
        TryAppendJournal(folder, who2, approved, now, cleared, notCleared.Count);

        // Счётчики: «В очереди»/«Не отправлено» в шапке и значки, окно «Неотправленное», подсказка связи.
        try { OutboxService.Default.NotifyChanged(); } catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Очистка чеков: окно не обновлено ({ex.Message}).", "WARNING"); }
        try { SyncService.Current?.RefreshQueueIndicators(); } catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Очистка чеков: счётчик очереди не обновлён ({ex.Message}).", "WARNING"); }

        if (cleared.Count == 0)
            return new UnsentClearResult(UnsentClearOutcome.Failed, 0, notCleared.Count, folder, Texts.NoneCleared(folder));
        return new UnsentClearResult(UnsentClearOutcome.Done, cleared.Count, notCleared.Count, folder, Texts.Done(cleared.Count, total, notCleared.Count, folder));
    }

    // ------------------------------------------------------------------ план

    private static UnsentClearLine ToLine(OfflineSaleEntry e, HashSet<string> fiscalDocs)
    {
        decimal total = 0;
        var lines = 0;
        string? cashier = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(e.CartJson) ? "{}" : e.CartJson);
            var totals = CartTotalsCalculator.Calculate(doc.RootElement);
            lines = totals.LineCount;
            total = totals.LineCount > 0 ? (decimal)totals.TotalDue : 0m;
            cashier = CartDisplayHelper.TryCashierName(doc.RootElement);
        }
        catch (JsonException)
        {
            // сумма — только для показа; чек от этого не страдает
        }
        // Фискальные данные чека из очереди кассы лежат в fiscal-queue.json под id продажи (у офлайн-чека — его id или серверный SyncedSaleId).
        var fiscal = fiscalDocs.Contains(e.Id) || (!string.IsNullOrWhiteSpace(e.SyncedSaleId) && fiscalDocs.Contains(e.SyncedSaleId!));
        var maybeOnServer = e.CheckoutSubmittedAt is not null || e.QuickCheckoutAttempted || e.BatchCheckoutAttempted || !string.IsNullOrWhiteSpace(e.SyncCartId);
        return new UnsentClearLine(e.Id, e.CreatedAt, e.Status, total, e.PaymentMethod ?? "", cashier, e.ShiftId, lines, e.LastError, fiscal, maybeOnServer, e.OfflineDebt);
    }

    /// <summary>Номера документов, по которым ККМ уже приняла фискальный чек (очередь fiscal-queue.json). Не прочитан файл — пусто (журнал), очистка не блокируется.</summary>
    private static HashSet<string> LoadAcceptedFiscalDocIds(FiscalQueueStore? store)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in (store ?? FiscalQueueStore.Default).All())
                if (f.Kind == FiscalDocKind.Sale && (f.Status == FiscalStatus.Accepted || f.Requisites is { Status: FiscalStatus.Accepted }) && !string.IsNullOrWhiteSpace(f.DocId))
                    set.Add(f.DocId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Очистка чеков: очередь фискальных чеков не прочитана ({ex.Message}).", "WARNING");
        }
        return set;
    }

    // ------------------------------------------------------------------ резервная копия

    /// <summary>Записать копию очищаемых чеков (полные записи очереди — JSON, сводка — CSV) и проверить, что файлы на диске не пустые. Возвращает папку.</summary>
    private static string WriteBackup(UnsentClearPlan plan, string who, string approvedBy, DateTimeOffset now, string root)
    {
        var folder = Path.Combine(root, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var n = 1;
        while (Directory.Exists(folder))
            folder = Path.Combine(root, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + (++n));
        Directory.CreateDirectory(folder);

        var entries = new List<OfflineSaleEntry>();
        foreach (var line in plan.Items)
            entries.Add(OfflinePendingSalesStore.TryGetById(line.Id) ?? throw new IOException("чек " + line.Id + " пропал из очереди"));

        var jsonPath = Path.Combine(folder, "receipts.json");
        var csvPath = Path.Combine(folder, "receipts.csv");
        var json = JsonSerializer.Serialize(new
        {
            created_at = now,
            created_by = who,
            pin_confirmed_by = approvedBy,
            count = plan.Count,
            total = plan.Total,
            oldest = plan.Oldest,
            receipts = entries,
        }, BackupJson);
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        File.WriteAllText(csvPath, BuildCsv(plan), new UTF8Encoding(true)); // с BOM — Excel открывает кириллицу правильно

        foreach (var p in new[] { jsonPath, csvPath })
            if (!File.Exists(p) || new FileInfo(p).Length == 0)
                throw new IOException("файл копии не записан: " + Path.GetFileName(p));
        // Читаем JSON обратно: копия должна быть валидной и содержать все чеки.
        using (var check = JsonDocument.Parse(File.ReadAllText(jsonPath)))
        {
            if (!check.RootElement.TryGetProperty("receipts", out var arr) || arr.GetArrayLength() != plan.Count)
                throw new IOException("копия чеков записана не полностью");
        }
        return folder;
    }

    private static string BuildCsv(UnsentClearPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id;created;status;amount;payment;cashier;shift;lines;fiscal;maybe_on_server;debt;last_error");
        foreach (var l in plan.Items)
            sb.AppendLine(string.Join(';', Csv(l.Id), Csv(l.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)), Csv(l.Status),
                Csv(OutboxFormat.Money(l.Amount)), Csv(l.Payment), Csv(l.Cashier), Csv(l.ShiftId), l.LinesCount.ToString(CultureInfo.InvariantCulture),
                l.Fiscal ? "yes" : "no", l.MaybeOnServer ? "yes" : "no", l.Debt ? "yes" : "no", Csv(l.LastError)));
        return sb.ToString();
    }

    private static string Csv(string? v)
    {
        var s = (v ?? "").Replace("\r", " ").Replace("\n", " ");
        // Ячейка, начинающаяся с = + - @, в Excel считалась бы формулой — защита от «CSV-инъекции» (текст из сервера).
        if (s.Length > 0 && "=+-@".Contains(s[0]))
            s = "'" + s;
        return s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Журнал действий очисток: одна строка JSON на очистку, рядом с копиями (clear-journal.jsonl). Сбой записи — только в журнал кассы: чеки уже закрыты, копия есть.</summary>
    private static void TryAppendJournal(string backupFolder, string who, string approvedBy, DateTimeOffset now, IReadOnlyList<UnsentClearLine> cleared, int notCleared)
    {
        try
        {
            var root = Path.GetDirectoryName(backupFolder) ?? backupFolder;
            var line = JsonSerializer.Serialize(new
            {
                at = now,
                action = "clear_unsent_receipts",
                by = who,
                pin_confirmed_by = approvedBy,
                cleared = cleared.Count,
                skipped = notCleared,
                total = cleared.Sum(c => c.Amount),
                fiscal = cleared.Count(c => c.Fiscal),
                backup = backupFolder,
                ids = cleared.Select(c => c.Id).ToArray(),
            });
            File.AppendAllText(Path.Combine(root, "clear-journal.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Очистка чеков: запись в журнал действий не удалась ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ тексты (5 языков)

    public static class Texts
    {
        public static string NotAllowed => Tr.T("Очищать неотправленные чеки может только владелец или администратор.", "Жөнөтүлбөгөн чектерди ээси же администратор гана тазалай алат.",
            "Only the owner or an administrator can clear unsent receipts.", "Gönderilmemiş fişleri yalnızca sahip veya yönetici temizleyebilir.", "Yuborilmagan cheklarni faqat egasi yoki administrator tozalay oladi.");

        public static string PinRequired => Tr.T("Нужен PIN владельца или администратора. Чеки не тронуты.", "Ээсинин же администратордун PIN коду керек. Чектерге тийилген жок.",
            "The owner's or administrator's PIN is required. The receipts were not touched.", "Sahip veya yönetici PIN'i gerekli. Fişlere dokunulmadı.", "Egasi yoki administrator PIN kodi kerak. Cheklarga tegilmadi.");

        public static string Nothing => Tr.T("Очищать нечего: чеков нет или они прямо сейчас отправляются.", "Тазалоого эч нерсе жок: чектер жок же дал азыр жөнөтүлүүдө.",
            "Nothing to clear: there are no receipts or they are being sent right now.", "Temizlenecek bir şey yok: fiş yok veya şu anda gönderiliyor.", "Tozalaydigan narsa yo'q: cheklar yo'q yoki hozir yuborilmoqda.");

        public static string BackupFailed(string error) => Tr.T($"Чеки НЕ очищены: не удалось сохранить резервную копию ({error}). Освободите место или проверьте папку данных и повторите.",
            $"Чектер ТАЗАЛАНГАН ЭМЕС: камдык көчүрмө сакталган жок ({error}). Орун бошотуп же дайындар папкасын текшерип, кайталаңыз.",
            $"Receipts were NOT cleared: the backup could not be saved ({error}). Free some space or check the data folder and retry.",
            $"Fişler temizlenMEDİ: yedek kaydedilemedi ({error}). Yer açın veya veri klasörünü kontrol edip tekrar deneyin.",
            $"Cheklar TOZALANMADI: zaxira nusxa saqlanmadi ({error}). Joy bo'shating yoki ma'lumotlar papkasini tekshirib, qayta urining.");

        public static string ClosedReason(string who, DateTimeOffset at) => Tr.T($"Очищено владельцем/администратором ({who}) {at:dd.MM.yyyy HH:mm}: чек на сервер не отправлен, копия сохранена в backup/unsent-receipts",
            $"Ээси/администратор ({who}) тазалады {at:dd.MM.yyyy HH:mm}: чек серверге жөнөтүлгөн жок, көчүрмө backup/unsent-receipts ичинде сакталды",
            $"Cleared by the owner/administrator ({who}) {at:dd.MM.yyyy HH:mm}: the receipt was not sent to the server, a copy is in backup/unsent-receipts",
            $"Sahip/yönetici ({who}) tarafından temizlendi {at:dd.MM.yyyy HH:mm}: fiş sunucuya gönderilmedi, kopya backup/unsent-receipts içinde",
            $"Egasi/administrator ({who}) tozaladi {at:dd.MM.yyyy HH:mm}: chek serverga yuborilmadi, nusxa backup/unsent-receipts da saqlangan");

        public static string Done(int cleared, decimal total, int skipped, string folder) => Tr.T(
            $"Очищено чеков: {cleared} на {MoneyText.Som(total)}." + (skipped > 0 ? $" Пропущено: {skipped} (отправляются или уже на сервере)." : "") + $"\nКопия чеков (JSON и CSV): {folder}",
            $"Тазаланган чектер: {cleared}, {MoneyText.Som(total)}." + (skipped > 0 ? $" Өткөрүлүп жиберилди: {skipped} (жөнөтүлүүдө же серверде)." : "") + $"\nЧектердин көчүрмөсү (JSON жана CSV): {folder}",
            $"Receipts cleared: {cleared} worth {MoneyText.Som(total)}." + (skipped > 0 ? $" Skipped: {skipped} (being sent or already on the server)." : "") + $"\nBackup of the receipts (JSON and CSV): {folder}",
            $"Temizlenen fiş: {cleared}, toplam {MoneyText.Som(total)}." + (skipped > 0 ? $" Atlandı: {skipped} (gönderiliyor veya zaten sunucuda)." : "") + $"\nFişlerin yedeği (JSON ve CSV): {folder}",
            $"Tozalangan cheklar: {cleared} ta, {MoneyText.Som(total)}." + (skipped > 0 ? $" O'tkazib yuborildi: {skipped} ta (yuborilmoqda yoki serverda)." : "") + $"\nCheklar nusxasi (JSON va CSV): {folder}");

        public static string NoneCleared(string folder) => Tr.T($"Ни один чек не очищен: они уже отправляются или изменились. Копия сохранена: {folder}",
            $"Бир да чек тазаланган жок: алар жөнөтүлүүдө же өзгөргөн. Көчүрмө сакталды: {folder}",
            $"No receipt was cleared: they are being sent or have changed. A copy was saved: {folder}",
            $"Hiçbir fiş temizlenmedi: gönderiliyor veya değişti. Yedek kaydedildi: {folder}",
            $"Birorta chek tozalanmadi: ular yuborilmoqda yoki o'zgargan. Nusxa saqlandi: {folder}");
    }
}
