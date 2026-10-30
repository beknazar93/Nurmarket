using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-30: хранилище и синхронизация временной клиентской нумерации документов закупок (логика — в DocumentNumberingCore).
// Индекс (id, дата, закреплённый номер) лежит рядом с журналом закупок: %APPDATA%\NurMarketKassa\purchases\doc-numbers.json, запись атомарная
// (AtomicFile). Номера показываются, только когда индекс серии полный (прошли все страницы истории) — до этого в списке прежний короткий код,
// иначе первые страницы дали бы неверные порядковые номера.

/// <summary>Страница документов серии с сервера: документы, всего документов на сервере, есть ли следующая страница, можно ли верить «всего».</summary>
public sealed record DocPage(IReadOnlyList<DocEntry> Docs, int Count, bool HasNext, bool CountReliable);

/// <summary>Запрос страницы документов серии (страница с 1, размер).</summary>
public delegate Task<DocPage> DocPageFetcher(int page, int limit, CancellationToken ct);

public sealed class DocNumberStore
{
    private sealed class SeriesIndex
    {
        public bool Complete { get; set; }
        public int KnownCount { get; set; }
        public DateTime SyncedAt { get; set; }
        public List<DocEntry> Entries { get; set; } = new();
        /// <summary>Номера, зарезервированные этой кассой для документов (id → порядок), пока сервер их не вернул в комментарии.</summary>
        public Dictionary<string, int> LocalClaims { get; set; } = new();
        /// <summary>2026-10-30: префикс, с которым номер был присвоен (id → префикс на тот момент), пока сервер не вернул номер в комментарии.</summary>
        public Dictionary<string, string> LocalClaimPrefixes { get; set; } = new();
    }

    public sealed class Reservation
    {
        public string Key { get; set; } = "";
        public DocSeries Series { get; set; }
        public int Year { get; set; }
        public int Seq { get; set; }
        public bool Draft { get; set; }
        public string? DocId { get; set; }
        public DateTime At { get; set; }
        /// <summary>2026-10-30: префикс на момент резерва — номер документа фиксируется при присвоении и не меняется со сменой префикса в настройках.</summary>
        public string Prefix { get; set; } = "";
    }

    private sealed class CompanyData
    {
        public Dictionary<string, SeriesIndex> Series { get; set; } = new();
        public List<Reservation> Reservations { get; set; } = new();
        public Dictionary<string, int> OrderCounters { get; set; } = new();
    }

    private sealed class FileData
    {
        public DocNumberingSettings Settings { get; set; } = new();
        public Dictionary<string, CompanyData> Companies { get; set; } = new();
    }

    public const int PageLimit = 200;
    public const int MaxPages = 100;

    private readonly Func<string?> _dir;
    private readonly Func<string> _company;
    private readonly Action<string, string> _log;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private FileData? _data;
    private readonly Func<bool> _readOnly;
    private int _version;
    private readonly Dictionary<(string Company, DocSeries Series, int Version), Dictionary<string, DocNumber>> _cache = new();

    public DocNumberStore(Func<string?> dir, Func<string> company, Action<string, string>? log = null, Func<DateTime>? now = null, Func<bool>? readOnly = null)
    {
        _dir = dir;
        _company = company;
        _log = log ?? ((_, _) => { });
        _now = now ?? (() => DateTime.Now);
        _readOnly = readOnly ?? (() => false);
    }

    /// <summary>Изменилось (синхронизация, настройки) — окна перерисовывают номера.</summary>
    public event Action? Changed;

    private string? FilePath => _dir() is { Length: > 0 } d ? Path.Combine(d, "doc-numbers.json") : null;

    // ------------------------------------------------------------------ файл

    private FileData Data()
    {
        if (_data is not null)
            return _data;
        _data = new FileData();
        try
        {
            if (FilePath is { } path && !_readOnly() && AtomicFile.ReadAllText(path) is { } text)
                _data = JsonSerializer.Deserialize<FileData>(text) ?? new FileData();
        }
        catch (Exception ex)
        {
            _log($"Нумерация закупок: файл номеров не прочитан ({ex.Message}) — индекс соберётся заново.", "WARNING");
            _data = new FileData();
        }
        _data.Settings = _data.Settings.Normalized();
        return _data;
    }

    private void Save()
    {
        _version++;
        _cache.Clear();
        if (_readOnly() || FilePath is not { } path)
            return;
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Data()));
        }
        catch (Exception ex)
        {
            _log($"Нумерация закупок: файл номеров не записан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Забыть прочитанное (другая папка данных в стенде проверки, смена аккаунта).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _data = null;
            _version++;
            _cache.Clear();
        }
    }

    private CompanyData Company() =>
        Data().Companies.TryGetValue(_company(), out var c) ? c : Data().Companies[_company()] = new CompanyData();

    private SeriesIndex Index(DocSeries s) =>
        Company().Series.TryGetValue(s.ToString(), out var i) ? i : Company().Series[s.ToString()] = new SeriesIndex();

    // ------------------------------------------------------------------ настройки

    public DocNumberingSettings Settings
    {
        get
        {
            lock (_sync)
                return Data().Settings;
        }
    }

    public void SaveSettings(DocNumberingSettings value)
    {
        lock (_sync)
        {
            Data().Settings = value.Normalized();
            Save();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ номера документов

    private Dictionary<string, DocNumber> Numbers(DocSeries series, List<string>? conflicts = null)
    {
        var key = (_company(), series, _version);
        if (conflicts is null && _cache.TryGetValue(key, out var cached))
            return cached;
        var st = Data().Settings;
        var idx = Index(series);
        var entries = idx.Entries.Select(e => idx.LocalClaims.TryGetValue(e.Id, out var local) && e.Claim <= 0
            ? e with { ClaimYear = e.At.Year, Claim = local, ClaimPrefix = idx.LocalClaimPrefixes.GetValueOrDefault(e.Id) ?? "" } : e).ToList();
        var seqs = DocumentNumberingCore.Assign(entries, st.ResetByYear, conflicts);
        var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        // 2026-10-30: префикс номера — тот, что был при присвоении (из комментария сервера / резерва), а не текущий из настроек.
        var map = seqs.ToDictionary(kv => kv.Key, kv => new DocNumber(series, byId[kv.Key].At.Year, kv.Value, false, byId[kv.Key].Claim > 0 && byId[kv.Key].ClaimPrefix.Length > 0 ? byId[kv.Key].ClaimPrefix : null), StringComparer.Ordinal);
        _cache[key] = map;
        return map;
    }

    /// <summary>Номер документа серии, если индекс серии полный и документ в нём есть; иначе null (показываем прежний короткий код).</summary>
    public DocNumber? NumberOf(DocSeries series, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        lock (_sync)
        {
            if (!Index(series).Complete)
                return null;
            return Numbers(series).TryGetValue(id, out var n) ? n : null;
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.66): документ получил не тот номер, что записан у него в комментарии на сервере (две кассы взяли один номер,
    /// позднему выдан следующий свободный) — окно накладной предупреждает, что на бумажной копии мог остаться прежний.</summary>
    public bool IsConflicted(DocSeries series, string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        lock (_sync)
        {
            if (!Index(series).Complete)
                return false;
            var conflicts = new List<string>();
            Numbers(series, conflicts);
            return conflicts.Contains(id, StringComparer.Ordinal);
        }
    }

    /// <summary>Текст номера для списка/окон: «ПР-2026-000123» или <paramref name="fallback"/>, пока номер неизвестен.</summary>
    public string Text(DocSeries series, string? id, string fallback = "")
    {
        if (NumberOf(series, id) is { } n)
            return DocumentNumberingCore.Format(Settings, n);
        return fallback;
    }

    /// <summary>Строка для поиска по номеру документа: полный номер, номер без нулей и без дефисов.</summary>
    public string SearchKey(DocSeries series, string? id)
    {
        if (NumberOf(series, id) is not { } n)
            return "";
        var st = Settings;
        var full = DocumentNumberingCore.Format(st, n);
        return full + " " + full.Replace("-", "") + " " + n.Seq.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Подходит ли документ под запрос-номер (ПР-2026-123, 123, 000123).</summary>
    public bool MatchesQuery(DocSeries series, string? id, string? query)
        => NumberOf(series, id) is { } n && DocumentNumberingCore.Matches(Settings, n, query);

    /// <summary>Последний известный порядковый номер серии в году (0 — документов нет).</summary>
    public int LastSeq(DocSeries series, int year)
    {
        lock (_sync)
            return LastSeqLocked(series, year);
    }

    private int LastSeqLocked(DocSeries series, int year)
    {
        var st = Data().Settings;
        var max = 0;
        foreach (var kv in Numbers(series))
            if ((!st.ResetByYear || kv.Value.Year == year) && kv.Value.Seq > max)
                max = kv.Value.Seq;
        return max;
    }

    public bool IsComplete(DocSeries series)
    {
        lock (_sync)
            return Index(series).Complete;
    }

    public DateTime SyncedAt(DocSeries series)
    {
        lock (_sync)
            return Index(series).SyncedAt;
    }

    // ------------------------------------------------------------------ синхронизация с историей сервера

    /// <summary>
    /// Обновить индекс серии. Первый раз (или если число документов на сервере уменьшилось) — полный обход страниц по <see cref="PageLimit"/>;
    /// дальше — первая страница и, если документов стало больше, страницы, пока не найдены все новые (порядок страниц сервера не важен).
    /// Не дошли до конца (предел <see cref="MaxPages"/>, обрыв связи) — индекс не считается полным. Возвращает true, если индекс полный.
    /// </summary>
    public async Task<bool> SyncAsync(DocSeries series, DocPageFetcher fetch, CancellationToken ct)
    {
        var first = await fetch(1, PageLimit, ct).ConfigureAwait(false);
        HashSet<string> known;
        bool complete;
        int knownCount;
        lock (_sync)
        {
            var idx = Index(series);
            known = idx.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            complete = idx.Complete;
            knownCount = known.Count;
        }

        var collected = new List<DocEntry>(first.Docs);
        var page = 1;
        var hasNext = first.HasNext;
        var full = !complete || !first.CountReliable || first.Count < knownCount
            || (first.Count == knownCount && first.Docs.Any(d => !known.Contains(d.Id)));
        if (full)
        {
            while (hasNext && page < MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                var next = await fetch(++page, PageLimit, ct).ConfigureAwait(false);
                collected.AddRange(next.Docs);
                hasNext = next.HasNext && next.Docs.Count > 0;
            }
        }
        else if (first.Count > knownCount)
        {
            var need = first.Count - knownCount;
            var found = collected.Count(d => !known.Contains(d.Id));
            while (found < need && hasNext && page < MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                var next = await fetch(++page, PageLimit, ct).ConfigureAwait(false);
                collected.AddRange(next.Docs);
                found = collected.Count(d => !known.Contains(d.Id));
                hasNext = next.HasNext && next.Docs.Count > 0;
            }
        }

        bool nowComplete;
        List<string> conflictIds = new();
        lock (_sync)
        {
            var idx = Index(series);
            var changed = false;
            if (full)
            {
                var fresh = collected.GroupBy(d => d.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                // локально зарезервированные, но ещё не вернувшиеся с сервера документы (только что проведённые) не теряем
                var serverIds = fresh.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                var keep = idx.Entries.Where(e => !serverIds.Contains(e.Id) && idx.LocalClaims.ContainsKey(e.Id) && (_now() - e.At) < TimeSpan.FromHours(2));
                var merged = fresh.Concat(keep).ToList();
                changed = !SameEntries(idx.Entries, merged);
                idx.Entries = merged;
                nowComplete = !hasNext;
            }
            else
            {
                var byId = idx.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
                foreach (var d in collected)
                {
                    if (!byId.TryGetValue(d.Id, out var old) || old != d)
                    {
                        byId[d.Id] = d;
                        changed = true;
                    }
                }
                idx.Entries = byId.Values.ToList();
                nowComplete = true;
            }
            idx.Complete = nowComplete;
            idx.KnownCount = first.Count;
            idx.SyncedAt = _now();
            // закрепления, которые сервер уже вернул в комментарии, локальные не нужны
            foreach (var e in idx.Entries.Where(e => e.Claim > 0).ToList())
            {
                idx.LocalClaims.Remove(e.Id);
                idx.LocalClaimPrefixes.Remove(e.Id);
            }
            if (changed || nowComplete != complete)
            {
                Save();
                var conflicts = new List<string>();
                Numbers(series, conflicts);
                conflictIds = conflicts;
            }
            else
            {
                idx.SyncedAt = _now();
            }
        }
        if (conflictIds.Count > 0)
            _log($"Нумерация закупок ({series}): расхождение номеров у {conflictIds.Count} док. (две кассы взяли один номер) — позднему выдан следующий свободный: {string.Join(", ", conflictIds.Take(5))}.", "WARNING");
        Changed?.Invoke();
        return nowComplete;
    }

    private static bool SameEntries(List<DocEntry> a, List<DocEntry> b)
    {
        if (a.Count != b.Count)
            return false;
        var set = a.ToHashSet();
        return b.All(set.Contains);
    }

    // ------------------------------------------------------------------ резерв номера нового документа

    /// <summary>
    /// Резерв номера для нового прихода (ключ — ключ идемпотентности прихода, повтор отправки получает тот же номер). Индекс полный и свежий —
    /// постоянный номер «последний известный + 1» (если он занят другим документом — следующий свободный); иначе временный «ч» (офлайн-черновик).
    /// <paramref name="refresh"/> — попытка обновить индекс перед резервом (вызывающий передаёт запрос страниц; ошибка связи → черновик).
    /// </summary>
    public async Task<Reservation> ReserveAsync(DocSeries series, string key, DocPageFetcher? refresh, CancellationToken ct)
    {
        var synced = false;
        if (refresh is not null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                synced = await SyncAsync(series, refresh, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log($"Нумерация закупок: индекс не обновлён перед резервом номера ({ex.Message}) — номер будет временным.", "INFO");
            }
        }
        lock (_sync)
        {
            var company = Company();
            var year = _now().Year;
            var complete = Index(series).Complete;
            var fresh = complete && (synced || _now() - Index(series).SyncedAt < TimeSpan.FromMinutes(2));
            company.Reservations.RemoveAll(r => (_now() - r.At) > TimeSpan.FromDays(30) && r.DocId is null);
            var existing = company.Reservations.FirstOrDefault(r => r.Key == key && r.Series == series);
            // чужие (ещё не привязанные к документу) резервы того же года занимают свои номера
            int Taken(Reservation? self) => company.Reservations.Where(r => r.Series == series && r.Year == year && r.DocId is null && !ReferenceEquals(r, self) && !r.Draft)
                .Select(r => r.Seq).DefaultIfEmpty(0).Max();
            if (existing is not null && existing.DocId is null)
            {
                // постоянный номер сохраняем, пока он не занят чужим документом; черновик при появившемся свежем индексе — повышаем
                var lastNow = LastSeqLocked(series, year);
                if (!existing.Draft && existing.Seq > lastNow)
                    return existing;
                // 2026-10-30 (приёмка 1.17.66): повтор после потерянного ответа ПЕРЕИСПОЛЬЗУЕТ свой номер. Ключ прихода — один документ: если запрос
                // дошёл до сервера, индекс уже показывает его номер как занятый, но это наш же приход — новый номер (пропуск, «коллизия») не расходуем.
                // Новый номер выдаётся, только если этот номер уже закреплён за другим нашим проведённым документом.
                if (!existing.Draft && !company.Reservations.Any(r => !ReferenceEquals(r, existing) && r.Series == series && r.Year == existing.Year && r.Seq == existing.Seq && !r.Draft && r.DocId is not null))
                {
                    _log($"Нумерация закупок: повтор отправки — номер {existing.Seq} сохранён (приход мог уже дойти до сервера), новый не расходуем.", "INFO");
                    return existing;
                }
                if (existing.Draft && !fresh)
                    return existing;
                if (!existing.Draft)
                    _log($"Нумерация закупок: номер {existing.Seq} уже занят другим документом — выдан новый (коллизия касс).", "WARNING");
                var upgraded = fresh;
                existing.Seq = upgraded ? Math.Max(lastNow, Taken(existing)) + 1 : DraftSeq(company, series, year, existing);
                existing.Draft = !upgraded;
                existing.Year = year;
                if (upgraded)
                    existing.Prefix = Data().Settings.Prefix(series); // номер присваивается сейчас — префикс этого момента
                Save();
                return existing;
            }
            if (existing is not null)
                return existing;
            var r2 = new Reservation { Key = key, Series = series, Year = year, At = _now(), Prefix = Data().Settings.Prefix(series) };
            if (fresh)
            {
                r2.Seq = Math.Max(LastSeqLocked(series, year), Taken(null)) + 1;
                r2.Draft = false;
            }
            else
            {
                r2.Draft = true;
                r2.Seq = DraftSeq(company, series, year, null);
            }
            company.Reservations.Add(r2);
            Save();
            return r2;
        }
    }

    private static int DraftSeq(CompanyData company, DocSeries series, int year, Reservation? self) =>
        company.Reservations.Where(r => r.Series == series && r.Year == year && r.Draft && !ReferenceEquals(r, self)).Select(r => r.Seq).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Приход проведён: закрепить номер резерва за документом (до тех пор, пока сервер не вернёт его в комментарии).</summary>
    public void Bind(DocSeries series, string key, string? docId)
    {
        if (string.IsNullOrEmpty(docId))
            return;
        lock (_sync)
        {
            var company = Company();
            var r = company.Reservations.FirstOrDefault(x => x.Key == key && x.Series == series);
            if (r is null || r.Draft)
                return; // черновик постоянного номера не получал — у документа будет номер по порядку
            r.DocId = docId;
            var idx = Index(series);
            idx.LocalClaims[docId] = r.Seq;
            var prefix = r.Prefix.Length > 0 ? r.Prefix : Data().Settings.Prefix(series);
            idx.LocalClaimPrefixes[docId] = prefix;
            if (!idx.Entries.Any(e => e.Id == docId))
                idx.Entries.Add(new DocEntry(docId, _now(), r.Year, r.Seq, prefix));
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Отказ сервера по существу: резерв номера снимается (номер не использован), следующий приход получит его.</summary>
    public void Release(DocSeries series, string key)
    {
        lock (_sync)
        {
            if (Company().Reservations.RemoveAll(r => r.Key == key && r.Series == series && r.DocId is null) > 0)
                Save();
        }
    }

    /// <summary>Описание резерва по ключу (для показа временного номера в очереди).</summary>
    public Reservation? FindReservation(DocSeries series, string key)
    {
        lock (_sync)
            return Company().Reservations.FirstOrDefault(r => r.Key == key && r.Series == series);
    }

    public string Format(Reservation r) => DocumentNumberingCore.Format(Settings, new DocNumber(r.Series, r.Year, r.Seq, r.Draft, r.Prefix.Length > 0 ? r.Prefix : null));

    // ------------------------------------------------------------------ заказы (только эта касса)

    /// <summary>Следующий номер заказа поставщику: ЗП-ГГГГ-NNNNNN. Счётчик — по году (при сквозной нумерации общий) и только растёт.</summary>
    public string NextOrderNumber()
    {
        lock (_sync)
        {
            var st = Data().Settings;
            var year = _now().Year;
            var counters = Company().OrderCounters;
            var ck = st.ResetByYear ? year.ToString(CultureInfo.InvariantCulture) : "all";
            counters[ck] = counters.GetValueOrDefault(ck) + 1;
            Save();
            return DocumentNumberingCore.Format(st, new DocNumber(DocSeries.Order, year, counters[ck]));
        }
    }

    /// <summary>Сколько документов серии в индексе.</summary>
    public int Count(DocSeries series)
    {
        lock (_sync)
            return Index(series).Entries.Count;
    }
}

/// <summary>Нумерация закупок этого ПК: общий экземпляр (папка — рядом с журналом закупок, компания — текущая).</summary>
public static class DocumentNumbering
{
    public static DocNumberStore Store { get; } = new(() => PurchasesStore.StorageDir, () => PurchasesStore.CurrentCompany,
        (text, level) => PosLogger.Log(text, level), readOnly: () => PurchasesStore.ReadOnly);

    /// <summary>Стенд проверки: запись на диск выключена (PurchasesStore.ReadOnly) — индекс только в памяти.</summary>
    public static DocNumberingSettings Settings => Store.Settings;

    public static string Text(DocSeries series, string? id, string fallback = "") => Store.Text(series, id, fallback);

    public static string ReceiptText(string? id) => Store.Text(DocSeries.Receipt, id, ShortCode(id));

    /// <summary>Прежний короткий код прихода (первые 8 знаков id) — пока постоянный номер неизвестен.</summary>
    public static string ShortCode(string? id) => string.IsNullOrEmpty(id) ? "" : (id.Length >= 8 ? id[..8] : id).ToUpperInvariant();

    public static string SearchKey(DocSeries series, string? id) => Store.SearchKey(series, id);

    public static bool MatchesQuery(DocSeries series, string? id, string? query) => Store.MatchesQuery(series, id, query);

    /// <summary>Фоновое обновление индекса серии «приход» через запрос страниц; сбой связи — тихо в журнал.</summary>
    public static async Task<bool> TrySyncAsync(DocSeries series, DocPageFetcher fetch, CancellationToken ct)
    {
        try
        {
            return await Store.SyncAsync(series, fetch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Нумерация закупок ({series}): индекс не обновлён ({ex.Message}).", "INFO");
            return false;
        }
    }
}
