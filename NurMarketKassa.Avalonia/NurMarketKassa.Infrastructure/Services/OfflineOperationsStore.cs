using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: строка возврата в записи офлайн-возврата. Key — ключ строки в снимке чека (индекс), Name — название,
/// Quantity — сколько возвращают, Amount — её стоимость с долей скидки на чек (сумма строк = сумма возврата).</summary>
public sealed class OfflineRefundLine
{
    public string Key { get; set; } = "";
    public string? ProductId { get; set; }
    public string Name { get; set; } = "";
    public double Quantity { get; set; }
    public double OriginalQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>2026-10-29: строка возврата в том виде, как она ушла (или уходит) на сервер: id строки продажи на сервере и количество. Записывается ДО запроса —
/// повтор после потерянного ответа идёт тем же телом и тем же ключом, а не пересчитывается по остатку на сервере (первая попытка могла его уже уменьшить).</summary>
public sealed class OfflineSentLine
{
    public string LineId { get; set; } = "";
    public string? ProductId { get; set; }
    public string Title { get; set; } = "";
    public double Quantity { get; set; }
    public double OriginalQuantity { get; set; }
}

/// <summary>2026-10-29, владелец разрешил «возврат» и «оплату долга» без интернета. Запись очереди такой операции: лежит в JSON-файле рядом с
/// остальными очередями (схема pos_local.db не менялась), уходит на сервер ПОСЛЕ чеков очереди, тем же ключом идемпотентности (Id).</summary>
public sealed class OfflineOperationEntry
{
    public const string KindRefund = "refund";
    public const string KindDebtPayment = "debt_payment";

    public const string PendingSync = "pending_sync";
    public const string Syncing = "syncing";
    public const string Synced = "synced";
    public const string Failed = "failed";

    /// <summary>Он же Idempotency-Key запроса: повтор того же ключа не проводит операцию второй раз.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = KindRefund;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Status { get; set; } = PendingSync;
    public string? LastError { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>«Требует проверки» — сервер отказал по существу (лимит, клиент удалён, уже возвращено…) либо исход неясен; решает человек.</summary>
    public bool ReviewNeeded { get; set; }
    public bool OwnerNotified { get; set; }
    public int Attempts { get; set; }

    /// <summary>Запрос с этим ключом мог дойти до сервера (ответа не было) — повтор только тем же ключом и телом.</summary>
    public bool RequestSent { get; set; }

    public string? ShiftId { get; set; }
    public string? CashierName { get; set; }

    /// <summary>Что должно уйти раньше: id чеков очереди (продажа → возврат/оплата долга по ней) и id прошлых операций.</summary>
    public List<string> DependsOn { get; set; } = new();

    // ── возврат ──
    /// <summary>Чек, по которому возврат: id записи очереди (SaleRefIsLocal) или id продажи на сервере.</summary>
    public string? SaleRef { get; set; }
    public bool SaleRefIsLocal { get; set; }
    public string? ReceiptNumber { get; set; }
    public List<OfflineRefundLine> Lines { get; set; } = new();
    /// <summary>Строки для сервера (после сопоставления с чеком на сервере); пусто — ещё не сопоставлены либо возвращается весь чек (Whole).</summary>
    public List<OfflineSentLine>? SentLines { get; set; }
    public bool Whole { get; set; }
    /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): запрос «весь чек» уже уходил на сервер (тело «весь чек» строк не содержит, SentLines пуст). Повтор после
    /// потерянного ответа идёт ТЕМ ЖЕ ключом и тем же телом, без повторной сверки чека на сервере: чек уже мог быть возвращён, и сверка отправила бы операцию
    /// в «Требует проверки», откуда её возвращали бы руками (двойной возврат).</summary>
    public bool WholeSent { get; set; }
    /// <summary>Возвращено всё, что оставалось по чеку: после отправки отменяются бонусы за чек.</summary>
    public bool ReturnsAll { get; set; }
    public string? Reason { get; set; }
    public decimal Cash { get; set; }
    public decimal NonCash { get; set; }
    public decimal DebtReduced { get; set; }

    // ── общее: сумма операции (возврат — стоимость вернувшегося; оплата долга — сумма оплаты) ──
    public decimal Amount { get; set; }

    // ── оплата долга / клиент долгового чека ──
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string Method { get; set; } = "cash";

    [JsonIgnore]
    public bool IsPendingLike => Status is PendingSync or Syncing;
}

/// <summary>2026-10-29: очередь операций без связи (возврат, оплата долга). Файл offline_operations.json в папке данных кассы; запись атомарная
/// (tmp + замена). Хранит и карту «id чека очереди → id продажи на сервере»: запись чека из очереди после отправки удаляется, а возврат/оплата
/// долга по нему должны знать серверный id. Проведённые операции остаются в файле 45 дней — по ним считается «сколько уже возвращено».</summary>
public static class OfflineOperationsStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Путь к файлу. Тест/стенд подменяет, чтобы не трогать настоящий профиль.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "offline_operations.json");

    public static readonly TimeSpan KeepSynced = TimeSpan.FromDays(45);

    /// <summary>Что изменилось (добавлена/обновлена/снята операция) — шапка обновляет счётчики, синхронизация просыпается.</summary>
    public static event Action? Changed;

    /// <summary>Операция только что поставлена в очередь — SyncService досылает её почти сразу (если есть связь), а не ждёт такта.</summary>
    public static event Action? Appended;

    private sealed class FileModel
    {
        public List<OfflineOperationEntry> Operations { get; set; } = new();
        public Dictionary<string, string> SaleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Когда запись карты сделана (чистка старше 45 дней).</summary>
        public Dictionary<string, DateTimeOffset> SaleMapAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>2026-10-29 (ревью, Б-2): файл очереди не прочитан ни сам, ни из копии .bak. Рядом лежит метка — «очередь повреждена»: новые операции не
    /// принимаются, в шапке красная плашка, окно операций объясняет и ждёт решения владельца (<see cref="ClearCorrupt"/>). Молча «пустой» очереди нет.</summary>
    public static bool IsCorrupt => File.Exists(CorruptMarker);

    private static string CorruptMarker => FilePath + ".corrupt";

    public static string CorruptText => Tr.T(
        "Файл очереди операций без связи повреждён (возможно, пропало питание), копии нет. Возвраты и оплаты долга без связи сейчас не принимаются. Проверьте на сайте операции, сделанные без связи, и сообщите разработчику; битый файл сохранён рядом с расширением .bad.",
        "Байланышсыз операциялар кезегинин файлы бузулган (электр өчкөн болушу мүмкүн), көчүрмөсү жок. Байланышсыз кайтаруу жана карыз төлөмү азыр кабыл алынбайт. Байланышсыз жасалган операцияларды сайттан текшериңиз жана өнүктүрүүчүгө кабарлаңыз; бузулган файл .bad кеңейтүүсү менен сакталды.",
        "The offline operations queue file is damaged (perhaps a power cut) and has no backup. Returns and debt payments without a connection are not accepted now. Check the operations made offline on the website and tell the developer; the damaged file is kept with the .bad extension.",
        "Bağlantısız işlem kuyruğu dosyası bozuk (belki elektrik kesildi) ve yedeği yok. Bağlantısız iade ve borç ödemesi şu an kabul edilmiyor. Bağlantısız yapılan işlemleri sitede kontrol edin ve geliştiriciye bildirin; bozuk dosya .bad uzantısıyla saklandı.",
        "Aloqasiz operatsiyalar navbati fayli buzilgan (elektr o'chgan bo'lishi mumkin), nusxasi yo'q. Aloqasiz qaytarish va qarz to'lovi hozir qabul qilinmaydi. Aloqasiz bajarilgan operatsiyalarni saytda tekshiring va dasturchiga xabar bering; buzilgan fayl .bad kengaytmasi bilan saqlandi.");

    /// <summary>Владелец прочитал и решил продолжать с пустой очередью: метка снимается (битый файл остаётся как .bad).</summary>
    public static void ClearCorrupt()
    {
        try { if (File.Exists(CorruptMarker)) File.Delete(CorruptMarker); }
        catch (Exception ex) { PosLogger.Log($"Очередь операций: метка повреждения не снята: {ex.Message}", "WARNING"); }
        RaiseChanged();
    }

    private static bool ValidModel(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<FileModel>(text, Json) is { Operations: not null };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static FileModel LoadNoLock()
    {
        if (IsCorrupt)
            throw new IOException(CorruptText);
        try
        {
            var existed = File.Exists(FilePath);
            // AtomicFile: битый рабочий файл → .bad, данные из .bak; «занят другой программой» — IOException (не порча).
            var text = AtomicFile.ReadAllText(FilePath, ValidModel);
            if (text is null)
            {
                if (!existed)
                    return new FileModel();
                // Файл был, а прочитать нечего: ни он, ни копия. Не молчим — метка «очередь повреждена», новые операции закрыты.
                try { File.WriteAllText(CorruptMarker, DateTimeOffset.Now.ToString("O")); } catch { /* метка — подсказка; исключение ниже всё равно */ }
                PosLogger.Log("Очередь операций без связи ПОВРЕЖДЕНА и не восстановлена из копии — новые операции закрыты.", "ERROR");
                RaiseChanged();
                throw new IOException(CorruptText);
            }

            var model = JsonSerializer.Deserialize<FileModel>(text, Json) ?? new FileModel();
            model.SaleMap = new Dictionary<string, string>(model.SaleMap ?? new(), StringComparer.OrdinalIgnoreCase);
            model.SaleMapAt = new Dictionary<string, DateTimeOffset>(model.SaleMapAt ?? new(), StringComparer.OrdinalIgnoreCase);
            model.Operations ??= new();
            return model;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Файл не прочитан — НЕ затираем его пустым (иначе потеряем деньги): бросаем, вызывающий покажет «запись невозможна».
            PosLogger.Log($"Очередь операций без связи не прочитана: {ex.Message}", "WARNING");
            throw new IOException("Очередь операций без связи не прочитана: " + ex.Message, ex);
        }
    }

    private static void SaveNoLock(FileModel model)
    {
        var cutoff = DateTimeOffset.Now - KeepSynced;
        model.Operations.RemoveAll(o => o.Status == OfflineOperationEntry.Synced && (o.SyncedAt ?? o.CreatedAt) < cutoff);
        // 2026-10-29 (ревью, мелочь 8): карта «чек очереди → серверный id» не растёт бесконечно.
        foreach (var old in model.SaleMapAt.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            model.SaleMapAt.Remove(old);
            model.SaleMap.Remove(old);
        }
        // 2026-10-29 (ревью, Б-2): запись через AtomicFile — на диск (fsync), с копией .bak; обрыв питания не оставляет полфайла.
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(model, Json));
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Очередь операций: обработчик изменения не отработал: {ex.Message}", "WARNING"); }
    }

    public static List<OfflineOperationEntry> LoadAll()
    {
        lock (Gate)
        {
            try { return LoadNoLock().Operations.OrderBy(o => o.CreatedAt).ToList(); }
            catch (IOException) { return new List<OfflineOperationEntry>(); }
        }
    }

    /// <summary>Ставит операцию в очередь. Исключение — запись не удалась: деньги по операции не принимать/не выдавать.</summary>
    public static void Add(OfflineOperationEntry entry)
    {
        lock (Gate)
        {
            var model = LoadNoLock();
            if (model.Operations.Any(o => string.Equals(o.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                return; // тот же ключ — та же операция, второй раз не ставим
            model.Operations.Add(entry);
            SaveNoLock(model);
        }
        PosLogger.Log($"OFFLINE ops: поставлена {entry.Kind} {entry.Id}, сумма {entry.Amount:0.00}, клиент {entry.ClientId ?? "—"}, чек {entry.SaleRef ?? "—"}.", "OFFLINE");
        RaiseChanged();
        try { Appended?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Очередь операций: обработчик постановки не отработал: {ex.Message}", "WARNING"); }
    }

    public static OfflineOperationEntry? Get(string id)
    {
        lock (Gate)
        {
            try { return LoadNoLock().Operations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)); }
            catch (IOException) { return null; }
        }
    }

    public static void Update(string id, Action<OfflineOperationEntry> change)
    {
        lock (Gate)
        {
            var model = LoadNoLock();
            var entry = model.Operations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;
            change(entry);
            SaveNoLock(model);
        }
        RaiseChanged();
    }

    /// <summary>Чек очереди ушёл на сервер: запоминаем его серверный id (операции по нему теперь можно отправлять) и переносим на него
    /// журнал возвратов. serverSaleId пуст — сервер не вернул номер: операции по этому чеку уйдут в «Требует проверки».</summary>
    public static void NoteSaleSynced(string localId, string? serverSaleId)
    {
        if (string.IsNullOrWhiteSpace(localId))
            return;
        lock (Gate)
        {
            FileModel model;
            try { model = LoadNoLock(); }
            catch (IOException) when (IsCorrupt) { return; } // очередь повреждена — карты нет, чек всё равно уходит (операции закрыты)
            // Карту ведём всегда, даже если операций по чеку пока нет: возврат можно оформить уже после отправки чека.
            // 2026-10-29 (ревью, В-1): ошибка записи карты НЕ глотается — вызывающий не снимет чек из очереди, пока карта не записана.
            model.SaleMap[localId.Trim()] = (serverSaleId ?? "").Trim();
            model.SaleMapAt[localId.Trim()] = DateTimeOffset.Now;
            SaveNoLock(model);
        }
        if (!string.IsNullOrWhiteSpace(serverSaleId))
        {
            try { SaleReturnLedger.Rekey(localId, serverSaleId!); }
            catch (Exception ex) { PosLogger.Log($"Журнал возвратов не перенесён на серверный id: {ex.Message}", "WARNING"); }
        }
        RaiseChanged();
    }

    /// <summary>Серверный id чека очереди: null — чек ещё не отправлен; "" — отправлен, но id неизвестен.</summary>
    public static string? ServerIdOf(string localId)
    {
        lock (Gate)
        {
            try { return LoadNoLock().SaleMap.TryGetValue(localId.Trim(), out var id) ? id : null; }
            catch (IOException) { return null; }
        }
    }

    public static int PendingCount => LoadAll().Count(o => o.IsPendingLike && !o.ReviewNeeded);

    /// <summary>«Отклонено / требует проверки» — для красной плашки в шапке.</summary>
    public static int FailedCount => LoadAll().Count(o => o.Status == OfflineOperationEntry.Failed && o.LastError != CanceledMarker) + (IsCorrupt ? 1 : 0); // 2026-10-29: повреждённая очередь — красная плашка

    /// <summary>Нужно ли что-то отправлять сейчас (есть ожидающие операции).</summary>
    public static bool HasPending => LoadAll().Any(o => o.IsPendingLike);

    /// <summary>Операции по чеку (по локальному или серверному id), кроме отклонённых «навсегда»: считаем возвраты, которые касса уже оформила.</summary>
    public static List<OfflineOperationEntry> RefundsForSale(string saleRef)
    {
        if (string.IsNullOrWhiteSpace(saleRef))
            return new List<OfflineOperationEntry>();
        lock (Gate)
        {
            FileModel model;
            try { model = LoadNoLock(); }
            catch (IOException) { return new List<OfflineOperationEntry>(); }
            var same = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { saleRef.Trim() };
            foreach (var (local, server) in model.SaleMap)
            {
                if (string.Equals(local, saleRef, StringComparison.OrdinalIgnoreCase) && server.Length > 0)
                    same.Add(server);
                if (string.Equals(server, saleRef, StringComparison.OrdinalIgnoreCase))
                    same.Add(local);
            }
            return model.Operations
                .Where(o => o.Kind == OfflineOperationEntry.KindRefund && o.SaleRef != null && same.Contains(o.SaleRef)
                            && !(o.Status == OfflineOperationEntry.Failed && o.LastError == CanceledMarker))
                .OrderBy(o => o.CreatedAt)
                .ToList();
        }
    }

    /// <summary>Пометка снятой вручную операции (владелец решил, что её не нужно отправлять).</summary>
    public const string CanceledMarker = "[снято вручную]";

    /// <summary>Наличные, которые ушли из ящика/пришли в ящик по ещё не отправленным операциям смены: оплата долга наличными (+) и наличный
    /// возврат (−). К остатку смены с сервера прибавляется в шапке (EffectiveShiftCashBalance), пока операция не доехала.</summary>
    public static decimal PendingCashDelta(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        // 2026-10-29 (ревью, В-4): и «Требует проверки» — деньги по такой операции уже в ящике / уже выданы, пока владелец не решил.
        return LoadAll()
            .Where(o => (o.IsPendingLike || (o.Status == OfflineOperationEntry.Failed && o.LastError != CanceledMarker))
                        && string.Equals(o.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
            .Sum(o => o.Kind == OfflineOperationEntry.KindDebtPayment
                ? (string.Equals(o.Method, "cash", StringComparison.OrdinalIgnoreCase) ? o.Amount : 0m)
                : -o.Cash);
    }

    /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): «Проверено / закрыть без отправки» — человек разобрал операцию «Требует проверки» (проверил на сервере или
    /// решил, что отправлять не нужно). Помечаем CanceledMarker: в плашку «Отклонено», остаток ящика и долг клиента такая операция больше не входит, в файле
    /// остаётся как след. Журнал — PosLogger. Возвращает false, если операции нет или она не в статусе «Требует проверки»/«Ошибка».</summary>
    public static bool CloseWithoutSending(string id, string? who = null)
    {
        var entry = Get(id);
        if (entry is null || entry.Status != OfflineOperationEntry.Failed || entry.LastError == CanceledMarker)
            return false;
        var was = entry.LastError;
        Update(id, e =>
        {
            e.Status = OfflineOperationEntry.Failed;
            e.ReviewNeeded = false;
            e.LastError = CanceledMarker;
        });
        PosLogger.Log($"OFFLINE ops: операция {entry.Kind} {id} ({entry.Amount:0.00}) закрыта вручную без отправки{(string.IsNullOrWhiteSpace(who) ? "" : ", кто: " + who)}. Была причина: {was}", "WARNING");
        return true;
    }

    /// <summary>Возвращает операцию «Требует проверки» в очередь (кнопка «Повторить»). Тот же ключ: если прошлая попытка всё же дошла — сервер вернёт повтор.</summary>
    public static bool Requeue(string id)
    {
        var entry = Get(id);
        if (entry is null || entry.Status != OfflineOperationEntry.Failed)
            return false;
        Update(id, e =>
        {
            e.Status = OfflineOperationEntry.PendingSync;
            e.ReviewNeeded = false;
            e.OwnerNotified = false;
            e.LastError = null;
        });
        PosLogger.Log($"OFFLINE ops: операция {id} возвращена в очередь вручную.", "OFFLINE");
        return true;
    }

    /// <summary>2026-10-29 (ревью, В-4): оплаты долга наличными по смене, ещё не отправленные (и «Требует проверки») — к значению отчёта сервера в окнах закрытия смены.</summary>
    public static decimal UnsentDebtCash(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        return LoadAll()
            .Where(o => o.Kind == OfflineOperationEntry.KindDebtPayment && o.Status != OfflineOperationEntry.Synced && o.LastError != CanceledMarker
                        && string.Equals(o.Method, "cash", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(o.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
            .Sum(o => o.Amount);
    }

    /// <summary>2026-10-29 (ревью, В-5): сколько единиц товара уже возвращено офлайн-операциями по чеку и ещё НЕ отправлено (или ждёт решения) — онлайн-окно
    /// возврата вычитает это из «можно вернуть»: сервер этих возвратов пока не знает. Сопоставление по товару, иначе по названию.</summary>
    public static double UnsentRefundQuantity(string? saleId, string? productId, string? title)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return 0;
        double sum = 0;
        foreach (var op in RefundsForSale(saleId!).Where(o => o.Status != OfflineOperationEntry.Synced))
            foreach (var l in op.Lines)
            {
                var same = !string.IsNullOrWhiteSpace(productId) && !string.IsNullOrWhiteSpace(l.ProductId)
                    ? string.Equals(l.ProductId, productId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(l.Name, title, StringComparison.OrdinalIgnoreCase);
                if (same)
                    sum += l.Quantity;
            }
        return sum;
    }

    /// <summary>Сколько неотправленных операций в файле по пути <paramref name="path"/> (для вопроса при смене компании). Не разобрали, но файл не пустой — 1.</summary>
    public static int CountUnsent(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
                return 0;
            return ops.EnumerateArray().Count(o => !(o.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.String
                                                     && string.Equals(st.GetString(), OfflineOperationEntry.Synced, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return new FileInfo(path).Length > 4 ? 1 : 0;
        }
    }

    public static string Inv(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
