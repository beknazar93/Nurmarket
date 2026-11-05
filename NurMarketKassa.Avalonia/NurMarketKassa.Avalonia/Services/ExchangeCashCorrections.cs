using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, проверка обмена на тестовом аккаунте: при обмене товара из чека ЗАКРЫТОЙ смены (купил вчера —
/// меняет сегодня) сервер завышает ожидаемую наличность текущей смены (expected_cash) ровно на сумму возвращённого.
/// Обмен пишет в смену пару движений «Возврат по чеку (Наличные)» и «Наличные остались в кассе: возврат отдан безналом»,
/// и у чека закрытой смены в expected_cash попадает только второе (№1585: вернули 40, сдача 5 — ящик −5, сервер +35).
/// Чек текущей или другой открытой смены сервер считает верно (+8, −5, +4 — проверено через кассу).
/// Кассир при закрытии смены увидел бы недостачу, которой нет. ТЗ бэкенда, часть 14, п. 14.1.
///
/// Пока сервер не исправлен, касса после каждого обмена сравнивает, на сколько сервер изменил expected_cash, с тем,
/// сколько наличных реально прошло через ящик, и запоминает поправку на смену — только если разница совпала
/// с суммой возвращённого (это и есть ошибка; любое другое расхождение — чужие операции в это же время, их не трогаем).
/// Когда сервер исправят, разницы не будет и поправки перестанут записываться сами.
/// Поправка прибавляется к остатку смены там же, где неотправленные внесения (ShiftCashOperationsStore.NetForShift).</summary>
public static class ExchangeCashCorrections
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static string FilePath => Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        AppMode.DataFolderName,
        "exchange_cash_fix.json");

    private sealed class Entry
    {
        public string ShiftId { get; set; } = "";
        public string ExchangeId { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Архив старых поправок: <see cref="FilePath"/> с суффиксом. Появление файла = «миграция под сервер ceba67c сделана».</summary>
    private static string ArchivePath => Path.Combine(Path.GetDirectoryName(FilePath)!, "exchange_cash_fix.archived.json");

    private sealed class ArchiveFile
    {
        public DateTime ArchivedAt { get; set; }
        public string Reason { get; set; } = "";
        public List<Entry> Entries { get; set; } = new();
    }

    // 2026-11-05 (сервер ceba67c, признак debt_return_drawer_cash): сервер сам снимает наличные с ожидаемой наличности смены при возврате долгового чека и ПЕРЕСЧИТАЛ прошлые смены задним числом
    // (смена 19fefe82: 987 → 979). Все поправки, записанные кассой ДО этого, поэтому вычитались бы второй раз. Как только признак виден, ВСЕ старые записи файла переносятся в архив
    // (один раз; файл-архив остаётся для разбора) и больше не применяются. Поправки, записанные уже после (новые ошибки сервера: обмен, предоплата безналом), работают как раньше.
    private static bool _archiveDone;

    /// <summary>true — старые поправки уже в архиве (или переносить нечего). Вызывать под <see cref="FileLock"/>. false — перенос не удался (файл занят): пока считаем поправки нулевыми.</summary>
    private static bool EnsureOldCorrectionsArchived()
    {
        if (_archiveDone)
            return true;
        try
        {
            if (File.Exists(ArchivePath))
                return _archiveDone = true;
            var read = AtomicFile.TryRead(FilePath, attempts: 2, pauseMs: 60);
            if (read.Status == AtomicFile.ReadStatus.Unreadable || read.FromMemory)
                return false; // файл занят — попробуем при следующем пересчёте
            var old = Parse(read.Text) ?? new List<Entry>();
            AtomicFile.WriteAllText(ArchivePath, JsonSerializer.Serialize(new ArchiveFile
            {
                ArchivedAt = DateTime.Now,
                Reason = "debt_return_drawer_cash: сервер пересчитал прошлые смены сам; поправки кассы до этого момента больше не применяются",
                Entries = old,
            }, JsonOpts));
            foreach (var e in old)
                RemovedKeys[e.ExchangeId] = 0;
            if (old.Count > 0)
                Save(new List<Entry>());
            PosLogger.Log($"Поправки ящика: сервер ведёт возврат долгового чека сам (debt_return_drawer_cash) — {old.Count} старых записей перенесены в exchange_cash_fix.archived.json и не применяются" +
                          (old.Count > 0 ? $" (сумма {old.Sum(e => e.Amount):0.00})." : "."), "WARNING");
            return _archiveDone = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Поправки ящика: архив старых записей не создан ({ex.Message}).", "WARNING");
            return false;
        }
    }

    /// <summary>Проверяется при каждом пересчёте «Касса:»; false — признака нет / список ещё не получен (поведение как раньше).</summary>
    internal static Func<bool> ServerSubtractsDebtRefund { get; set; } = () => NurMarketKassa.Services.Api.ServerCapabilities.Has(NurMarketKassa.Services.Api.ServerCapabilities.DebtReturnDrawerCash);

    /// <summary>true — у этого аккаунта сервер сам снимает наличные при возврате долгового чека (признак debt_return_drawer_cash): поправку не пишем и не применяем.</summary>
    public static bool DebtRefundHandledByServer => ServerSubtractsDebtRefund();

    private static bool _featureNoted;

    /// <summary>2026-11-05: в журнал (один раз за запуск) — возврат долгового чека на сумму, сервер сам снял её с ожидаемой наличности; наша поправка не применялась.</summary>
    public static void NoteServerFeatureDebtRefund(string? shiftId, decimal cashOut)
    {
        if (_featureNoted)
            return;
        _featureNoted = true;
        PosLogger.Log($"Возврат долгового чека на {cashOut:0.00}: сервер сам снимает наличные с ожидаемой наличности смены (признак debt_return_drawer_cash) — поправка кассы не применяется.", "SALES");
    }

    /// <summary>Сумма поправок по смене (обычно отрицательная — сервер завысил остаток).</summary>
    public static decimal ForShift(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        var serverDoesIt = ServerSubtractsDebtRefund();
        lock (FileLock)
        {
            if (serverDoesIt && !EnsureOldCorrectionsArchived())
                return 0m; // старые записи не применяем и тогда, когда архив пока не создать (файл занят)
            return Load(forWrite: false).Where(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Amount);
        }
    }

    // 2026-11-05 (аудит №3, Р3-06 / Д3-11): наличные возврата, которые сервер уже вычел из остатка смены, но кассе об этом ещё не сказал (ответ на «остаток смены» медленный) —
    // шапка «Касса:» вычитает их сразу, а не через ~8 с. Запись снимается, когда пришёл остаток, который КАССА СПРОСИЛА ПОСЛЕ неё; через 90 с — сама.
    private static readonly List<(DateTime At, string ShiftId, decimal Amount)> PendingServerCashList = new();

    /// <summary>Возврат выдал наличные <paramref name="cashOut"/>, сервер вычтет их из остатка смены. Только для чеков НЕ в долг (у долгового сервер наличные не вычитает — там своя поправка).</summary>
    public static void NotePendingServerCash(string? shiftId, decimal cashOut)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || cashOut <= 0.005m)
            return;
        lock (PendingServerCashList)
            PendingServerCashList.Add((DateTime.UtcNow, shiftId, cashOut));
        PosDataEvents.RaiseSalesChanged(); // шапка пересчитывается сразу, даже если окно возврата ещё открыто
    }

    /// <summary>Сколько наличных возвратов по смене сервер ещё не отразил в остатке (для шапки «Касса:»).</summary>
    public static decimal PendingServerCash(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return 0m;
        var limit = DateTime.UtcNow.AddSeconds(-90);
        lock (PendingServerCashList)
        {
            PendingServerCashList.RemoveAll(e => e.At < limit);
            return PendingServerCashList.Where(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Amount);
        }
    }

    /// <summary>Пришёл остаток смены, запрос которого начался в <paramref name="requestedAtUtc"/>: все возвраты, выданные раньше, в нём уже учтены.</summary>
    public static void ClearPendingServerCash(string? shiftId, DateTime requestedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return;
        lock (PendingServerCashList)
            PendingServerCashList.RemoveAll(e => e.At < requestedAtUtc && string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>expected_cash смены с сервера; null — не удалось узнать (тогда поправку не считаем).</summary>
    public static async Task<decimal?> ReadExpectedCashAsync(NurMarketApiClient api, string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var row = await api.RequestAsync(HttpMethod.Get, $"api/construction/shifts/{Uri.EscapeDataString(shiftId)}/", null, null, cts.Token, TimeSpan.FromSeconds(6))
                .ConfigureAwait(false);
            // 2026-11-01 (контрольный аудит, К2-05): число разбираем общим культурно-независимым разбором — «985,00» не станет 98500.
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("expected_cash", out var v)
                && ServerRefundFigures.ParseMoney(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) is { } d)
                return d;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обмен: остаток смены с сервера не получен ({ex.Message}).", "WARNING");
        }
        return null;
    }

    /// <summary>После обмена: сверяет изменение expected_cash с реальным движением наличных и при известной ошибке
    /// сервера запоминает поправку. <paramref name="cashDelta"/> — сколько наличных реально пришло (+) или ушло (−).</summary>
    public static async Task CheckAsync(NurMarketApiClient api, string? shiftId, decimal? expectedBefore, decimal cashDelta, decimal returnedAmount, string exchangeId)
    {
        if (expectedBefore is not { } before || string.IsNullOrWhiteSpace(shiftId) || returnedAmount <= 0m)
            return;
        var after = await ReadExpectedCashAsync(api, shiftId).ConfigureAwait(false);
        if (after is not { } a)
            return;
        var overstated = (a - before) - cashDelta;
        if (Math.Abs(overstated) < 0.01m)
        {
            PosLogger.Log($"Обмен {exchangeId}: остаток смены на сервере изменился верно ({a - before:0.00}).", "SALES");
            return;
        }
        if (Math.Abs(overstated - returnedAmount) >= 0.01m)
        {
            PosLogger.Log($"Обмен {exchangeId}: остаток смены на сервере изменился на {a - before:0.00}, наличных прошло {cashDelta:0.00} — расхождение {overstated:0.00} не похоже на известную ошибку, поправку не пишем.", "WARNING");
            return;
        }
        lock (FileLock)
        {
            var list = Load();
            if (list.Any(e => string.Equals(e.ExchangeId, exchangeId, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(new Entry { ShiftId = shiftId, ExchangeId = exchangeId, Amount = -overstated, CreatedAt = DateTime.Now });
            // Старше 60 дней — смены давно закрыты.
            list.RemoveAll(e => e.CreatedAt < DateTime.Now.AddDays(-60));
            Save(list);
        }
        PosLogger.Log($"Обмен {exchangeId}: сервер завысил остаток смены на {overstated:0.00} (ошибка сервера с возвращённым товаром) — касса учитывает поправку.", "WARNING");
        PosDataEvents.RaiseSalesChanged();
    }

    /// <summary>2026-11-01 (контрольный аудит, К2-03): поправка «по факту» — касса выдала/приняла наличных иначе, чем записал сервер (обмен по чеку со скидкой на весь чек: сервер
    /// засчитал возвращённое по полной цене). <paramref name="amount"/> = реально прошедшие наличные − записанные сервером (плюс — серверный остаток занижен). Один раз на ключ.</summary>
    public static void RecordAdjustment(string? shiftId, string key, decimal amount, string what)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || string.IsNullOrWhiteSpace(key) || Math.Abs(amount) < 0.005m)
            return;
        lock (FileLock)
        {
            var list = Load();
            if (list.Any(e => string.Equals(e.ExchangeId, key, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(new Entry { ShiftId = shiftId, ExchangeId = key, Amount = amount, CreatedAt = DateTime.Now });
            list.RemoveAll(e => e.CreatedAt < DateTime.Now.AddDays(-60));
            Save(list);
        }
        PosLogger.Log($"{what}: наличные в ящике разошлись с записью сервера на {amount:0.00} — касса учитывает поправку.", "WARNING");
        PosDataEvents.RaiseSalesChanged();
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-А): та же поправка для долговых операций. Сервер считает в «ожидаемо в кассе»
    /// безналичную предоплату долга наличными (+N) и не вычитает наличные при возврате долгового чека (журнал смены верный,
    /// шапка «Касса» врала). <paramref name="overstated"/> — на сколько сервер ушёл от реального движения наличных
    /// (изменение остатка минус наличные, прошедшие через ящик); поправка пишется, только если оно равно известной сумме
    /// <paramref name="suspect"/> (чужие операции в это же время её не вызывают). Исправят сервер — разницы не будет.</summary>
    public static void RecordIfKnownServerError(string? shiftId, string key, decimal overstated, decimal suspect, string what, bool debtRefund = false)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || string.IsNullOrWhiteSpace(key) || suspect <= 0m)
            return;
        // 2026-11-05: возврат долгового чека, а сервер сам вычитает наличные (признак debt_return_drawer_cash) — поправку не пишем.
        if (debtRefund && ServerSubtractsDebtRefund())
            return;
        if (Math.Abs(overstated) < 0.01m)
            return;
        if (Math.Abs(overstated - suspect) >= 0.01m)
        {
            PosLogger.Log($"{what}: остаток смены на сервере разошёлся с кассой на {overstated:0.00} — не похоже на известную ошибку ({suspect:0.00}), поправку не пишем.", "WARNING");
            return;
        }
        lock (FileLock)
        {
            var list = Load();
            if (list.Any(e => string.Equals(e.ExchangeId, key, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(new Entry { ShiftId = shiftId, ExchangeId = key, Amount = -overstated, CreatedAt = DateTime.Now });
            list.RemoveAll(e => e.CreatedAt < DateTime.Now.AddDays(-60));
            Save(list);
        }
        PosLogger.Log($"{what}: сервер ошибся в остатке смены на {overstated:0.00} — касса учитывает поправку.", "WARNING");
        PosDataEvents.RaiseSalesChanged();
    }

    private static bool _serverFixNoted;

    /// <summary>2026-11-05 (приёмка волн 0–3, С-1): возврат долгового чека, а сервер вычел наличные сам (остаток изменился ровно на выдачу) — ошибка сервера исправлена.
    /// Новые поправки при этом не пишутся (RecordIfKnownServerError: разницы нет), двойного вычета нет. Поправки этой же смены, записанные ДО исправления, остаются: сервер мог
    /// пересчитать их задним числом, а мог и нет — по одному остатку это не отличить; в журнал (один раз за запуск) идёт сумма, чтобы при закрытии смены сверить ящик.</summary>
    public static void NoteServerSubtractsDebtRefund(string? shiftId, decimal cashOut)
    {
        if (_serverFixNoted || string.IsNullOrWhiteSpace(shiftId))
            return;
        _serverFixNoted = true;
        var kept = ForShift(shiftId);
        PosLogger.Log($"Возврат долгового чека на {cashOut:0.00}: сервер сам вычел наличные из остатка смены — ошибка сервера исправлена, новые поправки не пишутся."
                      + (Math.Abs(kept) > 0.005m ? $" Поправки этой смены ({kept:0.00}) записаны до исправления — при закрытии смены сверьте ящик." : ""), "WARNING");
    }

    /// <summary>2026-10-23 (живой аудит, Б-1): возврат долгового чека с безналичной предоплатой — сервер вычел её из
    /// «ожидаемо в кассе» (запись «Возврат по чеку (Наличные)»), то есть ошибка предоплаты сама обнулилась. Старая поправка
    /// «debtprepay:…saleId…» больше не нужна — иначе закрытие смены предлагает на сумму предоплаты меньше. Возвращает true, если снята.</summary>
    public static bool RemoveDebtPrepay(string? shiftId, string? saleId)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || string.IsNullOrWhiteSpace(saleId))
            return false;
        bool removed;
        lock (FileLock)
        {
            var list = Load();
            // 2026-11-05 (аудит №3, Д3-06): снятые ключи запоминаем — при занятом файле отложенная запись объединяет память с файлом и иначе вернула бы снятую поправку («воскресла» бы).
            foreach (var gone in list.Where(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)
                && e.ExchangeId.StartsWith("debtprepay:", StringComparison.OrdinalIgnoreCase)
                && e.ExchangeId.Contains(saleId, StringComparison.OrdinalIgnoreCase)))
                RemovedKeys[gone.ExchangeId] = 0;
            removed = list.RemoveAll(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)
                && e.ExchangeId.StartsWith("debtprepay:", StringComparison.OrdinalIgnoreCase)
                && e.ExchangeId.Contains(saleId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                Save(list);
        }
        if (removed)
        {
            PosLogger.Log($"Возврат долгового чека {saleId}: сервер вычел безналичную предоплату сам — поправка снята.", "WARNING");
            PosDataEvents.RaiseSalesChanged();
        }
        return removed;
    }

    // 2026-11-02 (аудит №2 кассы, К3-01; приёмка круга 3): «файл занят» (антивирус, индексатор) — это НЕ «пусто». Файл не прочитан или записать не вышло — запись откладывается
    // (AtomicFile.DeferWrite): самое свежее состояние лежит в памяти и читается ПЕРВЫМ, таймер дописывает его в файл (с объединением), при закрытии — запасной файл *.pending.
    private static bool _blocked;

    /// <summary>2026-11-05 (Д3-06): ключи поправок, снятых в этом сеансе (памяти хватает: их единицы за смену). Читается/пишется под <see cref="FileLock"/>; MergeEntries зовётся из AtomicFile — читаем копию.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RemovedKeys = new(StringComparer.OrdinalIgnoreCase);

    private static List<Entry>? Parse(string? text)
    {
        if (text is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(text, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Объединение при отложенной записи: поправки из памяти + те, что есть только в файле (по ключу обмена).</summary>
    private static string MergeEntries(string? fileText, string memoryText)
    {
        var memory = Parse(memoryText) ?? new List<Entry>();
        foreach (var e in Parse(fileText) ?? new List<Entry>())
            if (!memory.Any(x => string.Equals(x.ExchangeId, e.ExchangeId, StringComparison.OrdinalIgnoreCase))
                && !RemovedKeys.ContainsKey(e.ExchangeId)) // 2026-11-05 (Д3-06): снятая в этом сеансе поправка из файла не возвращается
                memory.Add(e);
        return JsonSerializer.Serialize(memory, JsonOpts);
    }

    private static List<Entry> Load(bool forWrite = true)
    {
        // Для показа (шапка «Касса», X-отчёт) — короткие повторы, чтобы не подвешивать окно; для записи — полные.
        var read = forWrite ? AtomicFile.TryRead(FilePath) : AtomicFile.TryRead(FilePath, attempts: 2, pauseMs: 60);
        var list = Parse(read.Text) ?? new List<Entry>();
        // Данные из памяти или из копии .bak (файл не прочитан) — писать их поверх файла нельзя, только через отложенную запись с объединением.
        var stale = read.Status == AtomicFile.ReadStatus.Unreadable || read.FromMemory;
        if (stale)
            _blocked = true;
        else if (forWrite)
            _blocked = false;
        return list;
    }

    private static void Save(List<Entry> list)
    {
        var text = JsonSerializer.Serialize(list, JsonOpts);
        if (_blocked)
        {
            AtomicFile.DeferWrite(FilePath, "exchange_cash_fix.json", text, MergeEntries);
            return;
        }
        try
        {
            // 2026-11-01 (Н-07): запись через временный файл + замену — переживает обрыв питания (раньше полфайла и потеря поправок смены).
            AtomicFile.WriteAllText(FilePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AtomicFile.DeferWrite(FilePath, "exchange_cash_fix.json", text, MergeEntries);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поправка обмена не сохранена: {ex.Message}", "WARNING");
        }
    }
}
