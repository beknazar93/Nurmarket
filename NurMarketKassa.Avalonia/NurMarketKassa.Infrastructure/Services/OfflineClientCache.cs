using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: клиент в локальном кэше кассы — для «в долг» и оплаты долга без связи. Данные те, что касса видела на сервере в последний
/// раз (UpdatedAt / DebtAt); долг, поставленный в очередь без связи, в Debt НЕ входит — он считается по очередям (<see cref="OfflineClientCache.UnconfirmedDebt"/>).</summary>
public sealed class OfflineClientRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>Долг по сводке сервера на момент DebtAt (null — сводку долгов касса не получала).</summary>
    public decimal? Debt { get; set; }
    public DateTimeOffset? DebtAt { get; set; }

    /// <summary>Лимит долга клиента с сервера (поля debt_limit / credit_limit); null — не задан или сервер его не отдаёт.</summary>
    public decimal? DebtLimit { get; set; }

    /// <summary>Клиент заблокирован / неактивен по данным сервера.</summary>
    public bool Blocked { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Операции очереди, уже учтённые в Debt после отправки (чтобы не посчитать дважды); хвост ограничен.</summary>
    public List<string> AppliedOps { get; set; } = new();
}

/// <summary>2026-10-29: локальный кэш клиентов для «в долг» / оплаты долга без связи. Файл offline_clients.json (схема pos_local.db не менялась).
/// Наполняется при каждой загрузке списка клиентов и сводки должников с сервера. Правило: сервер — истина; кэш только подсказка с меткой времени
/// («данные клиента от ЧЧ:ММ»), а долг, не подтверждённый сервером, считается из очередей и показывается отдельно.</summary>
public static class OfflineClientCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, OfflineClientRecord>? _cache;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "offline_clients.json");

    /// <summary>Сколько хранится запись о клиенте без обновления.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);

    private static Dictionary<string, OfflineClientRecord> LoadNoLock()
    {
        if (_cache is not null)
            return _cache;
        _loadBusy = false;
        var dict = new Dictionary<string, OfflineClientRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 2026-10-29 (ревью, Б-2): чтение через AtomicFile (битый файл → .bad, данные из .bak). Кэш можно собрать заново с сервера — пустым он
            // становится только с записью в журнал; «в долг» без связи при этом закрывается проверкой «клиента нет в кэше».
            var text = AtomicFile.ReadAllText(FilePath);
            if (text is not null)
            {
                var list = JsonSerializer.Deserialize<List<OfflineClientRecord>>(text, Json) ?? new();
                foreach (var r in list.Where(r => !string.IsNullOrWhiteSpace(r.Id)))
                    dict[r.Id] = r;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 2026-10-30 (приёмка 1.17.65, блокер 3): файл занят — не кэшируем «пусто» и не пишем поверх (см. SaveNoLock).
            _loadBusy = true;
            PosLogger.Log($"Кэш клиентов для работы без связи: файл занят ({ex.Message}) — не затираем.", "WARNING");
            return dict;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов для работы без связи не прочитан: {ex.Message}", "WARNING");
        }
        return _cache = dict;
    }

    /// <summary>2026-10-30: последнее чтение файла упало из-за «файл занят» — писать поверх нельзя.</summary>
    private static bool _loadBusy;

    private static void SaveNoLock(Dictionary<string, OfflineClientRecord> all)
    {
        if (_loadBusy && _cache is null)
            return; // 2026-10-30: прежние клиенты на диске не читались — не заменяем их куском
        try
        {
            var cutoff = DateTimeOffset.Now - KeepFor;
            foreach (var old in all.Values.Where(r => r.UpdatedAt < cutoff).Select(r => r.Id).ToList())
                all.Remove(old);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all.Values.ToList(), Json)); // 2026-10-29: на диск, с копией .bak
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов для работы без связи не записан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Обновление клиентов и долгов с сервера. Боевую реализацию ставит приложение (App): полный список клиентов и сводка должников. SyncService зовёт
    /// <see cref="RefreshIfStaleAsync"/> на проходах синхронизации, когда связь есть.</summary>
    public static Func<bool, CancellationToken, Task>? Refresher { get; set; }

    private static DateTime _lastClientsUtc = DateTime.MinValue;
    private static DateTime _lastDebtsUtc = DateTime.MinValue;

    /// <summary>Раз в 30 минут — полный список клиентов, раз в 5 — сводка долгов (один запрос). Любой сбой — только в журнал: кэш остаётся прежним.</summary>
    public static async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        var refresher = Refresher;
        if (refresher is null)
            return;
        var now = DateTime.UtcNow;
        var clientsDue = now - _lastClientsUtc >= TimeSpan.FromMinutes(30);
        var debtsDue = now - _lastDebtsUtc >= TimeSpan.FromMinutes(5);
        if (!clientsDue && !debtsDue)
            return;
        if (clientsDue)
            _lastClientsUtc = now;
        _lastDebtsUtc = now;
        try
        {
            await refresher(clientsDue, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов: обновление с сервера не удалось ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Сменили компанию/аккаунт — кэш чужих клиентов не должен остаться (вызывает AccountDataIsolation вместе с очисткой файлов).</summary>
    public static void ResetMemory()
    {
        lock (Gate)
            _cache = null;
        // 2026-10-29 (ревью, мелочь 7): после смены компании кэш обновляется сразу, а не через 30 минут.
        _lastClientsUtc = DateTime.MinValue;
        _lastDebtsUtc = DateTime.MinValue;
    }

    public static int Count
    {
        get { lock (Gate) return LoadNoLock().Count; }
    }

    /// <summary>Когда кэш клиентов обновлялся в последний раз (null — никогда).</summary>
    public static DateTimeOffset? LastUpdatedAt
    {
        get { lock (Gate) return LoadNoLock().Values.Select(r => (DateTimeOffset?)r.UpdatedAt).DefaultIfEmpty(null).Max(); }
    }

    public static OfflineClientRecord? Get(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return null;
        lock (Gate)
            return LoadNoLock().TryGetValue(clientId.Trim(), out var r) ? Clone(r) : null;
    }

    public static List<OfflineClientRecord> All()
    {
        lock (Gate)
            return LoadNoLock().Values.Select(Clone).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static OfflineClientRecord Clone(OfflineClientRecord r) => new()
    {
        Id = r.Id, Name = r.Name, Phone = r.Phone, Debt = r.Debt, DebtAt = r.DebtAt, DebtLimit = r.DebtLimit,
        Blocked = r.Blocked, UpdatedAt = r.UpdatedAt, AppliedOps = new List<string>(r.AppliedOps),
    };

    /// <summary>Список клиентов с сервера (GET clients/). Поставщики и подрядчики (type не client) не нужны. Долг и лимит старой записи
    /// сохраняются, если сервер их в этом ответе не прислал.</summary>
    public static void RememberClients(IEnumerable<JsonElement> rows)
    {
        try
        {
            var now = DateTimeOffset.Now;
            lock (Gate)
            {
                var all = LoadNoLock();
                var changed = false;
                foreach (var row in rows)
                {
                    if (row.ValueKind != JsonValueKind.Object)
                        continue;
                    var id = Str(row, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var type = Str(row, "type");
                    if (!string.IsNullOrEmpty(type) && !string.Equals(type, "client", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!all.TryGetValue(id!, out var rec))
                        all[id!] = rec = new OfflineClientRecord { Id = id! };
                    rec.Name = Str(row, "full_name") ?? Str(row, "name") ?? rec.Name;
                    rec.Phone = Str(row, "phone") ?? rec.Phone;
                    // Лимит: поля сервера debt_limit (есть сейчас) и credit_limit (Б-56, планируется). Пустое/0 — лимита нет.
                    var limit = Dec(row, "debt_limit") ?? Dec(row, "credit_limit");
                    if (row.TryGetProperty("debt_limit", out _) || row.TryGetProperty("credit_limit", out _))
                        rec.DebtLimit = limit is > 0m ? limit : null;
                    rec.Blocked = Bool(row, "is_blocked") || Bool(row, "blocked") || (row.TryGetProperty("is_active", out var act) && act.ValueKind == JsonValueKind.False);
                    rec.UpdatedAt = now;
                    changed = true;
                }
                if (changed)
                    SaveNoLock(all);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов: список с сервера не запомнен: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Сводка должников с сервера (GET clients/debtors/): долг каждого клиента на сейчас. Клиенты, которых в сводке нет, —
    /// без долга (0), если сводка получена целиком.</summary>
    public static void RememberDebtors(IEnumerable<(string ClientId, string Name, string? Phone, decimal Debt)> debtors)
    {
        try
        {
            var now = DateTimeOffset.Now;
            lock (Gate)
            {
                var all = LoadNoLock();
                var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in debtors)
                {
                    if (string.IsNullOrWhiteSpace(d.ClientId))
                        continue;
                    listed.Add(d.ClientId.Trim());
                    if (!all.TryGetValue(d.ClientId.Trim(), out var rec))
                        all[d.ClientId.Trim()] = rec = new OfflineClientRecord { Id = d.ClientId.Trim(), Name = d.Name, Phone = d.Phone ?? "" };
                    rec.Debt = d.Debt;
                    rec.DebtAt = now;
                    rec.UpdatedAt = now;
                    // Сервер уже учёл всё, что отправили до этого момента, — отметки «учтено после отправки» больше не нужны.
                    rec.AppliedOps.Clear();
                }
                foreach (var rec in all.Values.Where(r => !listed.Contains(r.Id)))
                {
                    rec.Debt = 0m;
                    rec.DebtAt = now;
                    rec.AppliedOps.Clear();
                }
                SaveNoLock(all);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов: долги с сервера не запомнены: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Операция очереди дошла до сервера — долг клиента в кэше меняется на её сумму (один раз на операцию: opId запоминается), пока
    /// следующая сводка сервера не даст точное число. delta &gt; 0 — долг вырос (продажа в долг), &lt; 0 — уменьшился (оплата, возврат).</summary>
    public static void ApplyConfirmed(string? clientId, string opId, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(opId) || delta == 0m)
            return;
        try
        {
            lock (Gate)
            {
                var all = LoadNoLock();
                if (!all.TryGetValue(clientId.Trim(), out var rec))
                    return;
                if (rec.AppliedOps.Contains(opId, StringComparer.OrdinalIgnoreCase))
                    return;
                rec.Debt = Math.Max(0m, (rec.Debt ?? 0m) + delta);
                rec.AppliedOps.Add(opId);
                if (rec.AppliedOps.Count > 200)
                    rec.AppliedOps.RemoveRange(0, rec.AppliedOps.Count - 200);
                SaveNoLock(all);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов: долг после отправки не пересчитан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Долг клиента, поставленный в очередь без связи и ещё не подтверждённый сервером: продажи «в долг» (минус предоплата) минус оплаты долга и
    /// уменьшение долга возвратами. includeRejectedSales — считать ли отклонённые чеки «в долг» (для проверки лимита — да: они могут вернуться в очередь;
    /// для проверки оплаты — нет). Может быть отрицательным (оплат в очереди больше, чем долгов в очереди).</summary>
    public static decimal UnconfirmedDebt(string? clientId, bool includeRejectedSales)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return 0m;
        var id = clientId.Trim();
        decimal sum = 0m;
        try
        {
            foreach (var sale in OfflinePendingSalesStore.LoadDebtSalesForClient(id, includeRejectedSales))
                sum += sale.DebtAmount;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Кэш клиентов: чеки «в долг» очереди не прочитаны: {ex.Message}", "WARNING");
        }

        foreach (var op in OfflineOperationsStore.LoadAll())
        {
            if (!op.IsPendingLike && op.Status != OfflineOperationEntry.Failed)
                continue;
            if (op.LastError == OfflineOperationsStore.CanceledMarker)
                continue; // 2026-10-30: закрыта вручную без отправки — долг не меняет
            if (!string.Equals(op.ClientId, id, StringComparison.OrdinalIgnoreCase))
                continue;
            // 2026-10-29 (ревью, В-7): отклонённые («Требует проверки») оплаты и возвраты долг НЕ уменьшают при проверке лимита «в долг»
            // (includeRejectedSales) — они могут так и не уйти; при проверке оплаты (без отклонённых чеков) они вычитаются: деньги уже взяты.
            if (op.Status == OfflineOperationEntry.Failed && includeRejectedSales)
                continue;
            sum -= op.Kind == OfflineOperationEntry.KindDebtPayment ? op.Amount : op.DebtReduced;
        }

        return sum;
    }

    /// <summary>Долг клиента для проверки: сводка сервера (если была) + неподтверждённое в очереди. null — сводки долгов касса не получала.</summary>
    public static decimal? KnownDebt(OfflineClientRecord rec, bool includeRejectedSales) =>
        rec.Debt is { } d ? Math.Max(0m, d + UnconfirmedDebt(rec.Id, includeRejectedSales)) : null;

    private static string? Str(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
            return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static decimal? Dec(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    private static bool Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
