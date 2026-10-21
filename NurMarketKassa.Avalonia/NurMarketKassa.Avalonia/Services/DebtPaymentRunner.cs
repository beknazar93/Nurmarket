using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-18: оплата долга «в пути» или без ответа сервера. SaleId null — «одной суммой». Key — ключ идемпотентности
/// (создан для этой попытки, повтор — только с ним); ShiftId — смена, в которой взяли деньги (у владельца null).</summary>
/// <param name="Program">2026-10-18, седьмой круг: программа, начавшая оплату ("kassa" / "owner"). Журнал общий для кассы и
/// владельца на этом ПК; повтор и решение — только в своей программе (смена кассы — в базе кассы). Нет поля — касса (раньше
/// журнал был в папке кассы) или владелец (старый файл в папке владельца).</param>
internal sealed record UnknownDebtPayment(
    string CompanyId, string ClientId, string? ClientName, string? SaleId, double Amount, string Method, string Key, string? ShiftId, DateTime AtUtc,
    string? SaleLabel = null, string? Program = null);

/// <summary>2026-10-18: запрос оплаты не отправлен (нет связи, компания не определена, по клиенту есть незавершённая оплата,
/// журнал незавершённых оплат недоступен) — окна различают по исходу (DebtPayOutcome), текст — в Message.</summary>
internal sealed class DebtPayNotSentException : InvalidOperationException
{
    public DebtPayNotSentException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>2026-10-18, седьмой круг ревью (п.10): ОДИН исход операции с долгом вместо пяти флагов.</summary>
internal enum DebtPayOutcome
{
    /// <summary>Сервер ответил 2xx (в т. ч. replayed), запись удалена.</summary>
    Success,
    /// <summary>Сервер не знает адреса pay-debt (только новая оплата; касса идёт старым путём).</summary>
    EndpointMissing,
    /// <summary>Запрос не уходил (нет связи, компания не определена, оплата начата в другой программе).</summary>
    NotSent,
    /// <summary>Исход неизвестен (ответ не дошёл; повтор не удался) — замок остаётся.</summary>
    Unknown,
    /// <summary>Сервер отказал (4xx) на новой оплате — запись удалена, деньги не приняты.</summary>
    Rejected,
    /// <summary>Записи с этим ключом уже нет — оплату завершили в другом окне / программе.</summary>
    AlreadyDone,
    /// <summary>Новая оплата отклонена: по клиенту уже есть запись (окно показывает замок).</summary>
    PendingExists,
    /// <summary>Журнал незавершённых оплат недоступен (занят, повреждён, не записан) — ничего не отправлено или сервер принял,
    /// а запись не удалена (замок остаётся, «Повторить» тем же ключом).</summary>
    StoreUnavailable,
}

/// <summary>Итог операции с долгом. Result — ответ сервера 2xx (есть и у StoreUnavailable, если сервер принял, а журнал не
/// записан). IsRetry — повтор неизвестной оплаты. RecordedToOtherShift — смена оплаты уже не текущая. ShiftNotRecorded —
/// событие в смену не записалось (ошибка базы) — оплата есть на сервере, в отчёте смены её нет.</summary>
internal sealed record DebtPayAttempt(
    DebtPayOutcome Outcome, PayDebtResult? Result, Exception? Error, bool IsRetry, bool RecordedToOtherShift = false, bool ShiftNotRecorded = false);

/// <summary>Решение по замку после проверки на сайте NurCRM.</summary>
internal enum DebtResolution
{
    /// <summary>Оплата ЕСТЬ на сайте — отметить как проведённую (событие в смену той оплаты, журнал, LAN).</summary>
    PaidOnSite,
    /// <summary>Оплаты НЕТ на сайте — снять блокировку.</summary>
    NotOnSite,
}

/// <summary>
/// 2026-10-18 (второй — седьмой круг ревью, решения главной сессии): ОДНА реализация приёма оплаты долга для кассы (PayDebtDialog)
/// и программы владельца (AcceptDebtPaymentWindow).
///
/// «Исход неизвестен»: ответ не дошёл (таймаут, обрыв, 5xx, 408, 429) — оплата могла пройти. Единственный источник правды — повтор
/// С ТЕМ ЖЕ ключом. Вывод по изменению остатка не делается.
///
/// «Запись заранее»: каждая новая оплата — новый ключ; ДО отправки запись «в пути» атомарно ложится в debt-payments-unknown.json;
/// окончательный ответ — запись удаляется; неизвестный исход — запись остаётся замком. Ключ хранилища — (компания, клиент).
///
/// ОДНА ОЧЕРЕДЬ: все операции с долгами (новая оплата, повтор, решение по замку) идут по одной: SemaphoreSlim(1,1) в процессе и
/// (седьмой круг) файловая блокировка debt-payments.lock между процессами на всё время операции. Журнал — ОБЩИЙ для кассы и
/// программы владельца на этом ПК (%APPDATA%\NurMarketKassa\data); в начале каждой операции он перечитывается с диска (другая
/// программа могла его изменить). Повтор и решение — только в программе, начавшей оплату; другая программа видит замок.
/// Файл не читается — оплаты закрыты (IOException — до следующей попытки; повреждён — файл переименовывается в .bad-…json,
/// оплаты долгов закрыты до разбора поддержкой); непрочитанный файл никогда не перезаписывается.
/// Деньги (седьмой круг): после ответа 2xx и при «Оплата есть» СНАЧАЛА удаляется запись, и только при успехе пишется событие
/// в смену — иначе запись в смене + оставшийся замок → «Оплаты нет» → оплату взяли бы второй раз.
/// </summary>
internal static class DebtPaymentRunner
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Очередь всех операций с долгами в этой программе.</summary>
    private static readonly SemaphoreSlim Queue = new(1, 1);

    private static readonly object Sync = new();
    private static Dictionary<string, UnknownDebtPayment>? _store;
    /// <summary>Ключ записи, запрос по которой сейчас идёт (только для подписи «идёт в другом окне»).</summary>
    private static string? _sendingKey;

    /// <summary>Хранилище изменилось (замок появился/снят, оплата началась/закончилась, другая программа изменила журнал) —
    /// окна перечитывают состояние. Может прийти не с UI-потока (наблюдение за файлом) — подписчики делают Post.</summary>
    public static event Action? Changed;

    public const string ProgramKassa = "kassa";
    public const string ProgramOwner = "owner";

    private static string CurrentProgram => AppMode.IsOwner ? ProgramOwner : ProgramKassa;

    /// <summary>Оплату начала эта программа (повтор и решение — только здесь).</summary>
    public static bool IsOwnProgram(UnknownDebtPayment p) => string.Equals(p.Program ?? ProgramKassa, CurrentProgram, StringComparison.Ordinal);

    private static string AppDataRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>2026-10-18, седьмой круг (деньги): ОДИН журнал на компьютер для кассы и владельца (один пользователь) —
    /// %APPDATA%\NurMarketKassa\debt-payments; владелец пишет туда же (та же учётная запись). НЕ в папке data: её
    /// AccountDataIsolation уносит в accounts\… при смене компании (записи и так разделены по компании). Раньше журналы
    /// лежали в data у каждой программы — см. LegacyFiles.</summary>
    private static string DataDir => Path.Combine(AppDataRoot, "NurMarketKassa", "debt-payments");

    private static string FilePath => Path.Combine(DataDir, "debt-payments-unknown.json");

    /// <summary>Межпроцессная блокировка (FileShare.None — работает на Windows, Linux и Android).</summary>
    private static string LockPath => Path.Combine(DataDir, "debt-payments.lock");

    /// <summary>Журналы до седьмого круга (data кассы и data владельца) — записи переносятся в общий при первой операции.</summary>
    private static (string Path, string Program)[] LegacyFiles =>
    [
        (Path.Combine(AppDataRoot, "NurMarketKassa", "data", "debt-payments-unknown.json"), ProgramKassa),
        (Path.Combine(AppDataRoot, "NurMarketOwner", "data", "debt-payments-unknown.json"), ProgramOwner),
    ];

    /// <summary>Сколько ждать операцию другой программы (её запрос к серверу) — потом отказ «журнал занят».</summary>
    private const int LockWaitSeconds = 20;

    private static string StoreKey(string companyId, string clientId) => companyId + "|" + clientId;

    private static string? CurrentCompany => CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : null;

    /// <summary>2026-10-18, шестой круг ревью: повреждённый (переименованный) файл — признак в поле; диск проверяется при каждой
    /// операции и при перечитывании (CheckCorruptOnDisk), не в геттерах. ERROR в журнал — один раз.</summary>
    private static string? _corruptFile;

    /// <summary>Есть ли в папке данных отложенный повреждённый файл (под Sync).</summary>
    private static void CheckCorruptOnDisk()
    {
        string? bad;
        try
        {
            bad = Directory.Exists(DataDir) ? Directory.GetFiles(DataDir, "debt-payments-unknown.bad-*.json").FirstOrDefault() : null;
        }
        catch
        {
            return; // папку не прочитать — прежний признак остаётся
        }
        if (bad is not null && _corruptFile is null)
            PosLogger.Log($"Оплата долга: в папке данных повреждённый журнал незавершённых оплат ({bad}) — оплаты долгов закрыты до разбора поддержкой.", "ERROR");
        _corruptFile = bad;
    }

    private enum DiskRead { Ok, Busy, Corrupt }

    /// <summary>Чтение без блокировки записи у другой программы: FileShare.ReadWrite | Delete (атомарная замена файла не ждёт нас).</summary>
    private static string? ReadText(string path)
    {
        if (!File.Exists(path))
            return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>2026-10-18, седьмой круг (п.8): разбор с проверкой КАЖДОЙ записи. Любая ошибка (не JSON, null, другой тип, пустые
    /// компания/клиент/ключ, сумма не больше нуля, неизвестный способ, две записи одного клиента) — файл повреждён; исключений
    /// не бросает.</summary>
    /// <param name="skipKeys">2026-10-18, восьмой круг (п.3): ключи уже перенесённых записей старого файла — не сливаются снова.</param>
    /// <param name="added">Ключи, добавленные из этого текста (для отметки о переносе).</param>
    private static bool TryParse(string text, string defaultProgram, Dictionary<string, UnknownDebtPayment> into, out string? problem,
        ISet<string>? skipKeys = null, List<string>? added = null)
    {
        problem = null;
        try
        {
            var list = JsonSerializer.Deserialize<List<UnknownDebtPayment?>>(text);
            if (list is null)
            {
                problem = "документ null";
                return false;
            }
            foreach (var p in list)
            {
                if (p is null)
                    problem = "пустая запись";
                else if (string.IsNullOrWhiteSpace(p.CompanyId) || string.IsNullOrWhiteSpace(p.ClientId) || string.IsNullOrWhiteSpace(p.Key))
                    problem = "запись без компании, клиента или ключа";
                else if (!double.IsFinite(p.Amount) || p.Amount <= 0.005)
                    problem = "неверная сумма";
                else if (p.Method is not (DebtPaymentSettings.MethodCash or DebtPaymentSettings.MethodTransfer))
                    problem = "неизвестный способ";
                else if ((p.Program ?? defaultProgram) is not (ProgramKassa or ProgramOwner))
                    problem = "неизвестная программа";
                else if (skipKeys is not null && skipKeys.Contains(p.Key))
                    continue; // уже перенесена в общий журнал (там её могли и снять) — старый файл её не возвращает
                else if (into.TryGetValue(StoreKey(p.CompanyId, p.ClientId), out var same) && same.Key == p.Key)
                    continue; // та же запись в общем и старом файле (перенос прервался) — одна
                else if (into.ContainsKey(StoreKey(p.CompanyId, p.ClientId)))
                    problem = "две записи одного клиента с разными ключами";
                if (problem is not null)
                    return false;
                into[StoreKey(p!.CompanyId, p.ClientId)] = p.Program is null ? p with { Program = defaultProgram } : p;
                added?.Add(p.Key);
            }
            return true;
        }
        catch (Exception ex)
        {
            problem = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>2026-10-18, восьмой круг (п.3): отметка о переносе — ключи записей старых файлов, уже перенесённых в общий.
    /// Старый файл, который не удалось убрать, больше не возвращает снятые замки.</summary>
    private static string MigratedKeysPath => Path.Combine(DataDir, "debt-payments-migrated-keys.txt");

    /// <summary>Прочитать общий журнал и старые (до седьмого круга) файлы обеих программ — их ещё не перенесённые замки видны
    /// обеим. Ничего не меняет на диске. legacy — какие старые файлы дали новые записи и их ключи (для переноса).</summary>
    private static DiskRead ReadDisk(out Dictionary<string, UnknownDebtPayment> map, out string? problem, out string? corruptPath,
        out List<(string Path, List<string> Keys)> legacy, out HashSet<string> migrated)
    {
        map = new Dictionary<string, UnknownDebtPayment>(StringComparer.OrdinalIgnoreCase);
        problem = null;
        corruptPath = null;
        legacy = new List<(string, List<string>)>();
        migrated = new HashSet<string>(StringComparer.Ordinal);
        string? main;
        var olds = new List<(string Path, string Program, string Text)>();
        try
        {
            main = ReadText(FilePath);
            foreach (var (path, program) in LegacyFiles)
                if (ReadText(path) is { } old)
                    olds.Add((path, program, old));
            if (olds.Count > 0 && ReadText(MigratedKeysPath) is { } marks)
                foreach (var line in marks.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    migrated.Add(line);
        }
        catch (Exception ex)
        {
            problem = ex.GetType().Name + ": " + ex.Message;
            return DiskRead.Busy;
        }
        if (main is not null && !TryParse(main, ProgramKassa, map, out problem))
        {
            corruptPath = FilePath;
            return DiskRead.Corrupt;
        }
        foreach (var (path, program, text) in olds)
        {
            var added = new List<string>();
            if (!TryParse(text, program, map, out problem, migrated, added))
            {
                corruptPath = path;
                return DiskRead.Corrupt;
            }
            legacy.Add((path, added));
        }
        return DiskRead.Ok;
    }

    /// <summary>Перечитать журнал с диска в НАЧАЛЕ операции (под Sync и файловой блокировкой). Повреждён — файл отложен в
    /// .bad-…json, оплаты закрыты. Старый файл владельца — перенесён в общий. busyText — текст «недоступен» для этой операции.</summary>
    private static bool ReloadForOperation(string busyText, out string? error)
    {
        error = null;
        CheckCorruptOnDisk();
        if (_corruptFile is { } bad)
        {
            error = CorruptText(bad);
            return false;
        }
        switch (ReadDisk(out var map, out var problem, out var corruptPath, out var legacy, out var migrated))
        {
            case DiskRead.Busy:
                // Файл занят/недоступен — отказ сейчас, следующая попытка прочитает его заново (ничего не перезаписываем).
                PosLogger.Log($"Оплата долга: журнал незавершённых оплат не прочитан ({problem}) — операция отложена.", "WARNING");
                error = busyText;
                return false;
            case DiskRead.Corrupt:
            {
                // Повреждён — НЕ считать пустым: отложить в сторону (в общую папку) и закрыть оплаты долгов.
                var badPath = Path.Combine(DataDir, $"debt-payments-unknown.bad-{(corruptPath == FilePath ? "" : "old-")}{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try
                {
                    Directory.CreateDirectory(DataDir);
                    File.Move(corruptPath!, badPath);
                }
                catch (Exception moveEx)
                {
                    badPath = corruptPath!;
                    PosLogger.Log($"Оплата долга: повреждённый журнал не переименован ({moveEx.Message}).", "ERROR");
                }
                PosLogger.Log($"Оплата долга: журнал незавершённых оплат повреждён ({problem}) — отложен в {badPath}; оплаты долгов закрыты до разбора поддержкой.", "ERROR");
                _corruptFile = badPath;
                error = CorruptText(badPath);
                return false;
            }
        }
        _store = map;
        // 2026-10-18, восьмой круг (п.2): записи «точно не проведена», которые не удалось снять, — снять сейчас.
        if (DropSettled())
            SaveStore();
        // 2026-10-18, восьмой круг (п.3): перенос старых файлов — ОДИН раз. Новые записи старого файла → общий журнал →
        // ключи в отметку о переносе (дальше старый файл их не возвращает, даже если его не убрать) → старый файл в сторону.
        var newKeys = legacy.SelectMany(l => l.Keys).ToList();
        if (newKeys.Count > 0)
        {
            if (!SaveStore())
            {
                error = busyText;
                return false;
            }
            migrated.UnionWith(newKeys);
            try
            {
                var tmp = MigratedKeysPath + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", migrated.OrderBy(k => k, StringComparer.Ordinal)) + "\n");
                File.Move(tmp, MigratedKeysPath, overwrite: true);
                PosLogger.Log($"Оплата долга: записи старых журналов ({newKeys.Count}) перенесены в общий ({FilePath}).", "PAYMENT");
            }
            catch (Exception ex)
            {
                // Без отметки старый файл вернул бы снятые замки — операцию не начинаем (общий журнал записи уже содержит).
                PosLogger.Log($"Оплата долга: отметка о переносе старого журнала не записана ({ex.Message}) — операция отложена.", "ERROR");
                error = busyText;
                return false;
            }
        }
        foreach (var (old, _) in legacy)
        {
            try
            {
                File.Move(old, old + $".migrated-{DateTime.Now:yyyyMMdd-HHmmss}");
            }
            catch (Exception ex)
            {
                // Не убран — не страшно: его записи уже в отметке о переносе и снова не сольются.
                PosLogger.Log($"Оплата долга: старый журнал {old} не убран ({ex.Message}) — его записи уже перенесены.", "WARNING");
            }
        }
        return true;
    }

    /// <summary>Чтение для окон (без переименования и записи): первый запрос, открытие окна, изменение файла другой программой.
    /// Не прочитать — прежнее состояние в памяти (операции всё равно перечитают и закроются сами).</summary>
    private static bool RefreshFromDisk()
    {
        if (ReadDisk(out var map, out var problem, out _, out _, out _) != DiskRead.Ok)
        {
            _diskFailedAtUtc = DateTime.UtcNow;
            if (!_refreshWarned)
                PosLogger.Log($"Оплата долга: журнал незавершённых оплат не прочитан для окна ({problem}).", "WARNING");
            _refreshWarned = true;
            return false;
        }
        _refreshWarned = false;
        DropSettled(map);
        var changed = Signature(_store) != Signature(map);
        _store = map;
        return changed;
    }

    private static bool _refreshWarned;
    /// <summary>2026-10-18, девятый круг (п.6): время последней неудачи чтения журнала для окон (геттеры — не чаще раза в 5 с).</summary>
    private static DateTime _diskFailedAtUtc = DateTime.MinValue;

    private static string Signature(Dictionary<string, UnknownDebtPayment>? map) =>
        map is null ? "∅" : string.Join(";", map.Values.Select(p => p.CompanyId + "|" + p.ClientId + "|" + p.Key).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>2026-10-18, седьмой круг: перечитать общий журнал (окно открылось, файл изменила другая программа). Изменилось —
    /// Changed.</summary>
    public static void Refresh()
    {
        bool changed;
        try
        {
            lock (Sync)
            {
                EnsureWatcher();
                CheckCorruptOnDisk();
                changed = RefreshFromDisk();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: журнал не перечитан ({ex.Message}).", "WARNING");
            return;
        }
        if (changed)
            RaiseChanged();
    }

    private static FileSystemWatcher? _watcher;
    private static bool _watcherTried;
    private static System.Threading.Timer? _refreshTimer;

    /// <summary>Наблюдение за общим журналом: другая программа на этом ПК его изменила — окна узнают через ~0,3 с. Не
    /// получилось (платформа) — окна перечитывают при открытии, операции — в начале каждой (под Sync).</summary>
    private static void EnsureWatcher()
    {
        if (_watcherTried)
            return;
        _watcherTried = true;
        try
        {
            Directory.CreateDirectory(DataDir);
            var w = new FileSystemWatcher(DataDir, "debt-payments-unknown*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            w.Changed += (_, _) => ScheduleRefresh();
            w.Created += (_, _) => ScheduleRefresh();
            w.Deleted += (_, _) => ScheduleRefresh();
            w.Renamed += (_, _) => ScheduleRefresh();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: наблюдение за журналом незавершённых оплат не включено ({ex.Message}) — перечитывание при открытии окна и перед операцией.", "WARNING");
        }
    }

    private static void ScheduleRefresh()
    {
        try
        {
            lock (Sync)
            {
                _refreshTimer ??= new System.Threading.Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
                _refreshTimer.Change(300, Timeout.Infinite);
            }
        }
        catch
        {
            // наблюдение — удобство; операции всё равно перечитывают журнал сами
        }
    }

    /// <summary>Межпроцессная очередь: файловая блокировка на всё время операции (другая программа ждёт). null — не получили
    /// за LockWaitSeconds (идёт операция в другой программе) или папка недоступна.</summary>
    private static async Task<FileStream?> AcquireFileLockAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(LockWaitSeconds);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Оплата долга: журнал оплат занят другой программой или недоступен ({ex.GetType().Name}: {ex.Message}) — операция не начата.", "WARNING");
                return null;
            }
        }
    }

    /// <summary>Атомарная запись (временный файл + замена). Только если хранилище было прочитано (под Sync и файловой блокировкой).</summary>
    private static bool SaveStore()
    {
        if (_store is null)
            return false;
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store.Values.ToList()));
            File.Move(tmp, FilePath, overwrite: true);
            // Записи «точно не проведена», которых в записанном журнале уже нет, — сняты окончательно.
            _settledKeys.RemoveWhere(k => !_store.Values.Any(r => r.Key == k));
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: журнал незавершённых оплат не записан ({ex.Message}).", "ERROR");
            return false;
        }
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: окна не обновлены ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Запись «в пути» или замок этого клиента в текущей компании (компания не определена или журнал недоступен — null;
    /// оплата в этом случае всё равно закрыта внутри очереди). Никогда не бросает (п.8).</summary>
    public static UnknownDebtPayment? UnknownFor(string? clientId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(clientId) || CurrentCompany is not { } company)
                return null;
            lock (Sync)
            {
                // 2026-10-18, девятый круг (п.6): журнал не прочитан (занят/повреждён) — геттер не трогает диск чаще раза в 5 с;
                // дальше — только наблюдение за файлом и начало операции.
                if (_store is null && DateTime.UtcNow - _diskFailedAtUtc >= TimeSpan.FromSeconds(5))
                {
                    EnsureWatcher();
                    RefreshFromDisk();
                }
                return _store is not null && _store.TryGetValue(StoreKey(company, clientId), out var p) ? p : null;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оплата долга: состояние замка не прочитано ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Запрос по записи этого клиента идёт прямо сейчас (в каком-то окне этой программы).</summary>
    public static bool IsInFlight(string? clientId) =>
        UnknownFor(clientId) is { } p && string.Equals(Volatile.Read(ref _sendingKey), p.Key, StringComparison.Ordinal);

    // ── операции (все — в очереди) ──────────────────────────────────────────────────────────────

    /// <summary>Новая оплата. key — новый ключ идемпотентности от окна (окно запоминает его, чтобы узнать своё оповещение).
    /// Внутри очереди (процесс + файловая блокировка): компания, журнал с диска, нет ли по клиенту незавершённой оплаты (есть —
    /// отказ, не повтор; в т. ч. начатой другой программой), связь; затем запись «в пути» на диск и только потом запрос.
    /// Исключений не бросает.</summary>
    public static async Task<DebtPayAttempt> PayNewAsync(ClientDebtsApiService api, string key, string clientId, string? clientName,
        string? saleId, string? saleLabel, double amount, string method, string? shiftId)
    {
        await Queue.WaitAsync().ConfigureAwait(true);
        try
        {
            if (CurrentCompany is not { } company)
                return NotSent(CompanyUnknownText, false);
            using var fileLock = await AcquireFileLockAsync().ConfigureAwait(true);
            if (fileLock is null)
                return Unavailable(LockBusyText, false);
            UnknownDebtPayment record;
            lock (Sync)
            {
                if (!ReloadForOperation(StoreBusyText, out var error))
                    return Unavailable(error!, false);
                var storeKey = StoreKey(company, clientId);
                if (_store!.ContainsKey(storeKey))
                    return new DebtPayAttempt(DebtPayOutcome.PendingExists, null, new DebtPayNotSentException(PendingExistsText), false);
                if (OfflineModeHelper.IsServerOutage)
                    return NotSent(NotSentText, false);
                record = new UnknownDebtPayment(company, clientId, clientName, saleId, amount, method, key, shiftId, DateTime.UtcNow,
                    string.IsNullOrWhiteSpace(saleLabel) ? null : saleLabel, CurrentProgram);
                _store[storeKey] = record;
                // Запись заранее обязательна: без неё падение программы во время запроса оставило бы оплату без следа.
                if (!SaveStore())
                {
                    _store.Remove(storeKey);
                    return Unavailable(StoreWriteFailedText, false);
                }
            }
            RaiseChanged();
            return await SendAsync(api, record, isRetry: false).ConfigureAwait(true);
        }
        finally
        {
            Queue.Release();
        }
    }

    /// <summary>Повтор неизвестной оплаты ТОЛЬКО по её ключу. Записи с этим ключом уже нет — «оплата уже завершена», ничего не
    /// отправляется; запись другой программы — отказ (повтор там).</summary>
    public static async Task<DebtPayAttempt> RetryAsync(ClientDebtsApiService api, string key)
    {
        await Queue.WaitAsync().ConfigureAwait(true);
        try
        {
            using var fileLock = await AcquireFileLockAsync().ConfigureAwait(true);
            if (fileLock is null)
                return Unavailable(LockBusyText, true);
            UnknownDebtPayment? record;
            lock (Sync)
            {
                if (!ReloadForOperation(RetryStoreBusyText, out var error))
                    return Unavailable(error!, true);
                record = _store!.Values.FirstOrDefault(r => r.Key == key);
            }
            if (record is null)
                return new DebtPayAttempt(DebtPayOutcome.AlreadyDone, null, new DebtPayNotSentException(AlreadyDoneText), true);
            if (!IsOwnProgram(record))
                return NotSent(ForeignText(record), true);
            if (IsRetryExpired(record))
                return NotSent(RetryExpiredText, true);
            if (OfflineModeHelper.IsServerOutage)
                return NotSent(RetryNotSentText, true);
            return await SendAsync(api, record, isRetry: true).ConfigureAwait(true);
        }
        finally
        {
            Queue.Release();
        }
    }

    /// <summary>Решение по замку после проверки на сайте (подтверждение и код окно спрашивает ДО вызова). Запись закрывается
    /// только если её ключ совпадает и её начала эта программа. «Оплата есть» — СНАЧАЛА запись удаляется, потом событие в смену
    /// той оплаты по сумме записи, журнал, LAN. Warning — решение принято, но событие в смену не записалось.</summary>
    /// <param name="appliedAmount">2026-10-18, десятый круг (п.5): «Оплата есть» одной суммой — сумма, которую сервер реально
    /// провёл (ввёл кассир по сайту; не больше суммы записи). null — сумма записи.</param>
    public static async Task<(bool Done, string? Error, string? Warning)> ResolveAsync(string key, DebtResolution resolution, double? appliedAmount = null)
    {
        await Queue.WaitAsync().ConfigureAwait(true);
        try
        {
            using var fileLock = await AcquireFileLockAsync().ConfigureAwait(true);
            if (fileLock is null)
                return (false, LockBusyText, null);
            UnknownDebtPayment? record;
            lock (Sync)
            {
                if (!ReloadForOperation(ResolveStoreBusyText, out var error))
                    return (false, error, null);
                record = _store!.Values.FirstOrDefault(r => r.Key == key);
            }
            if (record is null)
                return (false, AlreadyDoneText, null);
            if (!IsOwnProgram(record))
                return (false, ForeignText(record), null);
            var amount = appliedAmount ?? record.Amount;
            if (!double.IsFinite(amount) || amount <= 0.005 || amount > record.Amount + 0.005)
                return (false, StateChangedText, null);
            amount = Math.Round(amount, 2);
            // 2026-10-18, седьмой круг (п.2, деньги): сначала удалить запись с диска. Не удалилась — в смену НЕ пишем: иначе деньги
            // в смене + замок → «Оплаты нет» → оплату взяли бы снова (двойной счёт).
            if (!RemoveRecord(record))
                return (false, ResolveStoreFailedText, null);
            var warnings = new List<string>();
            var who = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId ?? "?";
            if (resolution == DebtResolution.PaidOnSite)
            {
                // 2026-10-18, десятый круг (п.5): та же проверка, что у ответа сервера, — смена оплаты уже не текущая.
                if (!AppMode.IsOwner && !string.IsNullOrWhiteSpace(record.ShiftId) && !string.Equals(record.ShiftId, PosApp.ActiveShiftId, StringComparison.Ordinal))
                {
                    warnings.Add(OtherShiftText);
                    PosLogger.Log($"Оплата долга: «оплата есть на сайте» {amount:0.00} сом (ключ {record.Key}) учтена в смене {record.ShiftId}, она уже не текущая.", "WARNING");
                }
                // 2026-10-18, восьмой круг (п.7, п.8): общий RecordAndAnnounce; остаток после такой оплаты неизвестен (Left < 0) —
                // получатели перечитывают долг с сервера.
                if (RecordAndAnnounce(record, amount, UnknownLeft))
                    warnings.Add(ShiftNotRecordedText);
            }
            var warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
            PosLogger.Log($"Оплата долга: решение по неизвестной оплате — {(resolution == DebtResolution.PaidOnSite ? "оплата ЕСТЬ на сайте, отмечена проведённой" : "оплаты НЕТ на сайте, блокировка снята")}; " +
                          $"клиент {record.ClientId}, {amount:0.00} сом{(amount < record.Amount - 0.005 ? $" (из {record.Amount:0.00} по записи)" : "")} ({DebtPaymentFlow.MethodLog(record.Method)}), " +
                          $"{(record.SaleId is null ? "одной суммой" : "чек " + record.SaleId)}, ключ {record.Key}, смена {record.ShiftId ?? "—"}; решил(а) {who}.", "PAYMENT");
            RaiseChanged();
            return (true, null, warning);
        }
        finally
        {
            Queue.Release();
        }
    }

    private static DebtPayAttempt NotSent(string text, bool isRetry) =>
        new(DebtPayOutcome.NotSent, null, new DebtPayNotSentException(text), isRetry);

    private static DebtPayAttempt Unavailable(string text, bool isRetry) =>
        new(DebtPayOutcome.StoreUnavailable, null, new DebtPayNotSentException(text), isRetry);

    /// <summary>Удалить запись с диска (под файловой блокировкой операции). false — не записалось: запись в памяти возвращена
    /// (память = диск), замок остаётся.</summary>
    private static bool RemoveRecord(UnknownDebtPayment record)
    {
        lock (Sync)
        {
            var storeKey = StoreKey(record.CompanyId, record.ClientId);
            if (_store is null || !_store.TryGetValue(storeKey, out var cur) || cur.Key != record.Key)
                return true;
            _store.Remove(storeKey);
            if (SaveStore())
                return true;
            _store[storeKey] = cur;
            return false;
        }
    }

    /// <summary>Отправка по записи (вызывается только внутри очереди и файловой блокировки).</summary>
    private static async Task<DebtPayAttempt> SendAsync(ClientDebtsApiService api, UnknownDebtPayment record, bool isRetry)
    {
        var (clientId, clientName, saleId, amount, method, shiftId, key) =
            (record.ClientId, record.ClientName, record.SaleId, record.Amount, record.Method, record.ShiftId, record.Key);
        Volatile.Write(ref _sendingKey, key);
        RaiseChanged();
        var watch = Stopwatch.StartNew();
        PayDebtResult result;
        try
        {
            result = saleId is null
                ? await api.PayDebtAsync(clientId, amount, method, shiftId, key).ConfigureAwait(true)
                : await api.PaySaleDebtAsync(saleId, amount, method, shiftId, key).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _sendingKey, null);
            DebtPayAttempt failed;
            if (isRetry)
            {
                // Повтор не удался (любая ошибка) — замок и ключ остаются: исход первой попытки по-прежнему неизвестен.
                var notSent = DefinitelyNotSent(ex);
                PosLogger.Log($"Оплата долга: повтор неизвестной оплаты (клиент {clientId}, ключ {key}) не удался{(notSent ? " — не отправлен, нет соединения" : "")}: " +
                              $"{ex.GetType().Name} {(ex as ApiException)?.StatusCode} {ex.Message}.", "PAYMENT");
                failed = notSent
                    ? new DebtPayAttempt(DebtPayOutcome.NotSent, null, new DebtPayNotSentException(RetryNotSentText, ex), true)
                    : new DebtPayAttempt(DebtPayOutcome.Unknown, null, ex, true);
            }
            else if (ex is ApiException missing && ClientDebtsApiService.IsEndpointMissing(missing))
            {
                PosLogger.Log($"Pay-debt: сервер не знает {(saleId is null ? "clients/{id}/pay-debt/" : "pos/sales/{id}/pay-debt/")} ({missing.StatusCode}).", "PAYMENT");
                // 2026-10-18, восьмой круг (п.2): старый путь — только когда запись действительно снята (иначе замок остался бы рядом).
                failed = SettleNotApplied(record, new DebtPayAttempt(DebtPayOutcome.EndpointMissing, null, ex, false), NotSentText);
            }
            else if (DefinitelyNotSent(ex))
            {
                PosLogger.Log($"Оплата долга не проведена — клиент {clientId}, {amount:0.00} сом: нет соединения с сервером ({ex.Message}).", "PAYMENT");
                failed = SettleNotApplied(record, NotSent(NotSentText, false) with { Error = new DebtPayNotSentException(NotSentText, ex) }, NotSentText);
            }
            else if (!PendingIdempotencyKey.IsOutcomeUnknown(ex))
            {
                PosLogger.Log($"Оплата долга не принята — клиент {clientId}, {amount:0.00} сом ({DebtPaymentFlow.MethodLog(method)}): {(ex as ApiException)?.StatusCode} {ex.Message}.", "PAYMENT");
                failed = SettleNotApplied(record, new DebtPayAttempt(DebtPayOutcome.Rejected, null, ex, false), RejectedText(ex, saleId is not null));
            }
            else
            {
                PosLogger.Log($"Оплата долга: исход неизвестен — клиент {clientId}, {amount:0.00} сом ({DebtPaymentFlow.MethodLog(method)}), " +
                              $"{(saleId is null ? "одной суммой" : "чек " + saleId)}, смена {shiftId ?? "—"}, ключ {key}; изменения закрыты до повтора " +
                              $"({ex.GetType().Name} {(ex as ApiException)?.StatusCode} {ex.Message}).", "PAYMENT");
                failed = new DebtPayAttempt(DebtPayOutcome.Unknown, null, ex, false);
            }
            RaiseChanged();
            return failed;
        }

        watch.Stop();
        Volatile.Write(ref _sendingKey, null);
        // 2026-10-18, седьмой круг (п.2, деньги): СНАЧАЛА удалить запись, потом событие в смену. Запись не удалилась (диск полон) —
        // в смену не пишем, замок остаётся: «Повторить» тем же ключом → replayed → запишется один раз. Иначе деньги в смене +
        // замок → «Оплаты нет» → оплату взяли бы второй раз. Падение между удалением и записью в смену — недосчёт (строка
        // PAYMENT ниже пишется до события смены), не двойной счёт.
        if (!RemoveRecord(record))
        {
            PosLogger.Log($"Оплата долга: сервер ПРИНЯЛ {result.Paid:0.00} сом (клиент {clientId}, ключ {key}), но журнал незавершённых оплат не записан — " +
                          "замок оставлен, в смену не записано; «Повторить» тем же ключом после освобождения диска.", "ERROR");
            RaiseChanged();
            return new DebtPayAttempt(DebtPayOutcome.StoreUnavailable, result, new DebtPayNotSentException(AcceptedButStoreFailedText(result.Paid)), isRetry);
        }
        var otherShift = !AppMode.IsOwner && !string.IsNullOrWhiteSpace(shiftId) && !string.Equals(shiftId, PosApp.ActiveShiftId, StringComparison.Ordinal);
        var who = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId ?? "?";
        PosLogger.Log($"{(AppMode.IsOwner ? "Владелец" : "Касса")}: принята оплата долга — клиент {clientId} «{clientName}», {result.Paid:0.00} сом " +
                      $"({DebtPaymentFlow.MethodLog(method)}), {(saleId is null ? $"одной суммой, взносов {result.AppliedCount}" : "чек " + saleId)}, остаток {(result.Left >= 0 ? result.Left.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "неизвестен")}, " +
                      $"принял {who}, {(AppMode.IsOwner ? "без смены" : "смена " + (shiftId ?? "— (не открыта)"))}, ключ {key}, replayed={(result.Replayed ? "true" : "false")}, {watch.ElapsedMilliseconds} мс" +
                      $"{(result.Replayed ? ", повтор по ключу (второй раз не проведена)" : "")}{(isRetry ? ", повтор неизвестной оплаты" : "")}" +
                      $"{(otherShift ? " — смена оплаты уже не текущая: записано в ту смену, в текущую не попало" : "")}.", "PAYMENT");
        var shiftFailed = RecordAndAnnounce(record, result.Paid, result.Left);
        if (otherShift)
            PosLogger.Log($"Оплата долга: {result.Paid:0.00} сом (клиент {clientId}, ключ {key}) учтена в смене {shiftId}, она уже не текущая — " +
                          "в текущую смену и её ожидаемую сумму не попала.", "WARNING");
        RaiseChanged();
        return new DebtPayAttempt(DebtPayOutcome.Success, result, null, isRetry, otherShift, shiftFailed);
    }

    /// <summary>Остаток после оплаты неизвестен (LanDebtPaidPayload.Left &lt; 0) — получатели перечитывают долг с сервера.</summary>
    private const double UnknownLeft = -1;

    /// <summary>2026-10-18, восьмой круг (п.8): ОДНА реализация «оплата проведена» для ответа сервера и для «Оплата есть на
    /// сайте» — событие в смену (источник «pay-debt[-sale]:ключ», уникальный индекс не даст записать дважды), ERROR при
    /// сбое, LAN/окна (Announce), SalesChanged. Вызывается ТОЛЬКО после удаления записи с диска. true — в смену НЕ записалось.
    /// Касса пишет и без открытой смены (shift_id null — как 1.17.58: аналитика за период считает и такие оплаты);
    /// владелец не пишет (деньги не в кассе; его отчёты — с сервера, debt_repayments).</summary>
    private static bool RecordAndAnnounce(UnknownDebtPayment record, double amount, double left)
    {
        var shiftWritten = false;
        var shiftFailed = false;
        if (!AppMode.IsOwner)
        {
            var shiftId = string.IsNullOrWhiteSpace(record.ShiftId) ? null : record.ShiftId;
            shiftWritten = ShiftEventsStore.TryRecord(DebtPaymentFlow.ShiftKind(record.Method), shiftId,
                (record.SaleId is null ? "pay-debt:" : "pay-debt-sale:") + record.Key, amount, record.ClientName);
            if (!shiftWritten)
            {
                shiftFailed = true;
                PosLogger.Log($"Оплата долга: событие в смену {shiftId ?? "—"} НЕ записано (клиент {record.ClientId}, {amount:0.00} сом, ключ {record.Key}) — " +
                              "на сервере оплата есть, в отчёте смены её нет; сверить с сайтом.", "ERROR");
            }
        }
        // Оповещение несёт ключ оплаты — окно-автор узнаёт своё и себя не перечитывает.
        DebtPaymentFlow.Announce(record.Key, record.ClientId, record.ClientName, amount, left, record.Method);
        // 2026-10-18, шестой круг: ShiftEventsStore уже поднял SalesChanged — второй раз только без записи в смену.
        if (!shiftWritten)
            PosDataEvents.RaiseSalesChanged();
        return shiftFailed;
    }

    /// <summary>2026-10-18, восьмой круг (п.2): ключи записей, по которым сервер ТОЧНО ничего не провёл (отказ, адрес неизвестен,
    /// запрос не ушёл), а удалить их с диска не удалось. Эта программа их не показывает и удаляет при следующей операции
    /// (DropSettled + SaveStore); после перезапуска такая запись — обычный замок («Оплаты нет» — верное решение).</summary>
    private static readonly HashSet<string> _settledKeys = new(StringComparer.Ordinal);

    /// <summary>Убрать из карты записи «точно не проведена» (под Sync). true — что-то убрано.</summary>
    private static bool DropSettled(Dictionary<string, UnknownDebtPayment>? map = null)
    {
        map ??= _store;
        if (map is null || _settledKeys.Count == 0)
            return false;
        var gone = map.Where(kv => _settledKeys.Contains(kv.Value.Key)).Select(kv => kv.Key).ToList();
        foreach (var k in gone)
            map.Remove(k);
        return gone.Count > 0;
    }

    /// <summary>Окончательный исход «не проведена»: снять запись. Не удалось — запомнить её как «точно не проведена» (снимем
    /// при следующей операции) и честно сказать кассиру: оплата не проведена, но журнал не обновился, клиент временно закрыт.</summary>
    private static DebtPayAttempt SettleNotApplied(UnknownDebtPayment record, DebtPayAttempt outcome, string reason)
    {
        if (RemoveRecord(record))
            return outcome;
        lock (Sync)
        {
            _settledKeys.Add(record.Key);
            DropSettled();
        }
        PosLogger.Log($"Оплата долга: не проведена (клиент {record.ClientId}, ключ {record.Key}), но журнал незавершённых оплат не обновился — " +
                      "запись снимется при следующей операции; оплаты клиента временно закрыты.", "ERROR");
        return new DebtPayAttempt(DebtPayOutcome.StoreUnavailable, null, new DebtPayNotSentException(NotAppliedStoreFailedText + " " + reason, outcome.Error), outcome.IsRetry);
    }

    /// <summary>Запрос ТОЧНО не ушёл — ошибка соединения до отправки (DNS, отказ, сеть/узел недоступны). Сомнение — неизвестный исход.</summary>
    private static bool DefinitelyNotSent(Exception ex)
    {
        if (ex is not HttpRequestException)
            return false;
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.ConnectionRefused or SocketError.NetworkUnreachable
                     or SocketError.HostUnreachable or SocketError.TryAgain })
                return true;
        return false;
    }

    /// <summary>Ответ сервера про закрытую смену (повтор шлёт сохранённую смену оплаты).</summary>
    private static bool IsShiftClosedError(Exception ex)
    {
        if (ex is not ApiException { StatusCode: >= 400 and < 500 } api)
            return false;
        var text = ((api.Payload?.GetRawText() ?? "") + " " + api.Message).ToLowerInvariant();
        return (text.Contains("shift") || text.Contains("смен")) && (text.Contains("clos") || text.Contains("закры"));
    }

    /// <summary>Текст ошибки повтора: закрытая смена — что делать; иначе — «повтор не удался, блокировка остаётся».</summary>
    public static string RetryErrorText(Exception ex) => IsShiftClosedError(ex) ? ShiftClosedText : RetryFailedText(ex);

    /// <summary>2026-10-18, седьмой круг ревью (п.10): ОДНО сопоставление исхода с текстом для кассы и владельца. Это сообщение
    /// окна; при замке на экране окно показывает UnknownText(запись) и к нему — это уточнение (null — уточнять нечего).
    /// EndpointMissing у кассы — null (она идёт старым путём по взносам).</summary>
    public static string? OutcomeText(DebtPayAttempt a, bool bySale, bool owner)
    {
        switch (a.Outcome)
        {
            case DebtPayOutcome.Success:
                // 2026-10-18, девятый круг (п.7): предупреждения (другая смена, не записано в смену) — отдельно, OutcomeWarning.
                return SuccessText(a.Result!, bySale, owner);
            case DebtPayOutcome.EndpointMissing:
                return owner ? OwnerEndpointMissingText : null;
            case DebtPayOutcome.Unknown:
                // Первая попытка — всё объясняет замок; повтор не удался — причина (закрытая смена и т. п.).
                return a.IsRetry && a.Error is { } ex ? RetryErrorText(ex) : null;
            case DebtPayOutcome.Rejected:
                return a.Error is { } rejected ? RejectedText(rejected, bySale) : null;
            default:
                // NotSent, AlreadyDone, PendingExists, StoreUnavailable — готовый текст Runner в Error.Message.
                return a.Error?.Message;
        }
    }

    /// <summary>Отказ сервера (оплата точно не проведена): «долга нет», 404 с JSON — «чек/клиент не найден» (десятый круг),
    /// иначе — текст сервера.</summary>
    private static string RejectedText(Exception ex, bool bySale)
    {
        if (DebtPaymentFlow.IsNoDebt(ex))
            return DebtPaymentFlow.NoDebtMessage;
        if (ex is ApiException { StatusCode: 404 })
            return bySale
                ? T("Чек не найден на сервере — оплата не проведена. Обновите список.", "Чек серверден табылган жок — төлөм өткөн жок. Тизмени жаңыртыңыз.",
                    "The receipt was not found on the server — the payment was not made. Refresh the list.", "Fiş sunucuda bulunamadı — ödeme yapılmadı. Listeyi yenileyin.",
                    "Chek serverda topilmadi — to'lov o'tmadi. Ro'yxatni yangilang.")
                : T("Клиент не найден на сервере — оплата не проведена. Обновите список.", "Кардар серверден табылган жок — төлөм өткөн жок. Тизмени жаңыртыңыз.",
                    "The client was not found on the server — the payment was not made. Refresh the list.", "Müşteri sunucuda bulunamadı — ödeme yapılmadı. Listeyi yenileyin.",
                    "Mijoz serverda topilmadi — to'lov o'tmadi. Ro'yxatni yangilang.");
        return T("Сервер не принял оплату: ", "Сервер төлөмдү кабыл алган жок: ", "The server didn't accept the payment: ",
            "Sunucu ödemeyi kabul etmedi: ", "Server to'lovni qabul qilmadi: ") + ex.Message;
    }

    /// <summary>2026-10-18, десятый круг (п.2, деньги): сколько сервер помнит ключ идемпотентности — по ТЗ бэкенда п.1.5 не меньше
    /// 24 ч. Старше — повтор тем же ключом уже НЕ безопасен (сервер мог забыть ключ и списать второй раз): «Повторить» закрыта,
    /// только решение по сайту.</summary>
    public static readonly TimeSpan RetryKeyLifetime = TimeSpan.FromHours(24);

    public static bool IsRetryExpired(UnknownDebtPayment p) => DateTime.UtcNow - p.AtUtc > RetryKeyLifetime;

    public static string RetryExpiredText => T(
        "Прошло больше суток — сервер мог забыть ключ, повтор может списать второй раз. Проверьте оплату на сайте NurCRM и выберите «Оплата есть на сайте» или «Оплаты нет на сайте».",
        "Бир суткадан ашык өттү — сервер ачкычты унутушу мүмкүн, кайталоо экинчи жолу алып коюшу мүмкүн. Төлөмдү NurCRM сайтынан текшерип, «Төлөм сайтта бар» же «Сайтта төлөм жок» тандаңыз.",
        "More than a day has passed — the server may have forgotten the key, a retry could charge twice. Check the payment on the NurCRM site and choose “Payment is on the site” or “Payment not on the site”.",
        "Bir günden fazla geçti — sunucu anahtarı unutmuş olabilir, tekrar ikinci kez tahsil edebilir. Ödemeyi NurCRM sitesinde kontrol edip «Ödeme sitede var» veya «Ödeme sitede yok»u seçin.",
        "Bir sutkadan ko'p o'tdi — server kalitni unutgan bo'lishi mumkin, qayta urinish ikkinchi marta yechishi mumkin. To'lovni NurCRM saytida tekshirib, «To'lov saytda bor» yoki «To'lov saytda yo'q»ni tanlang.");

    /// <summary>2026-10-18, девятый круг (п.7): предупреждение к успеху — показывается отдельной заметной плашкой, которую
    /// кассир закрывает сам (не стирается следующим действием). null — нет.</summary>
    public static string? OutcomeWarning(DebtPayAttempt a)
    {
        if (a.Outcome != DebtPayOutcome.Success)
            return null;
        var parts = new List<string>();
        if (a.RecordedToOtherShift)
            parts.Add(OtherShiftText);
        if (a.ShiftNotRecorded)
            parts.Add(ShiftNotRecordedText);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>После исхода окну нужно перечитать список долгов с сервера (оплату завершили в другом месте / долга уже нет).</summary>
    public static bool NeedsReload(DebtPayAttempt a) =>
        a.Outcome is DebtPayOutcome.Success or DebtPayOutcome.AlreadyDone || (a.Error is { } e && DebtPaymentFlow.IsNoDebt(e));

    /// <summary>Подтверждение решения по замку (красная кнопка). Код сотрудника окно спрашивает отдельно, до ResolveAsync.
    /// 2026-10-18, десятый круг (п.5): результат — сумма для ResolveAsync (null — отмена). «Оплата есть» одной суммой — кассир
    /// вводит сумму, которую сервер провёл (по умолчанию — сумма записи, не больше неё); по чеку и «Оплаты нет» — сумма записи.</summary>
    public static async Task<double?> ConfirmResolutionAsync(Window? owner, UnknownDebtPayment p, DebtResolution resolution)
    {
        if (resolution == DebtResolution.PaidOnSite && p.SaleId is null && owner is not null)
            return await new DebtAppliedAmountDialog(p).ShowDialog<double?>(owner).ConfigureAwait(true);
        var ok = await PosConfirmDialog.ShowModalAsync(owner,
            resolution == DebtResolution.PaidOnSite ? PaidOnSiteTitle : UnlockTitle,
            resolution == DebtResolution.PaidOnSite ? PaidOnSiteMessage(p) : UnlockMessage(p),
            resolution == DebtResolution.PaidOnSite ? PaidOnSiteConfirmText : UnlockConfirmText,
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger).ConfigureAwait(true);
        return ok ? p.Amount : null;
    }

    // ── тексты ───────────────────────────────────────────────────────────────────────────────

    public static string UnlockTitle => T("Оплаты нет на сайте — снять блокировку?", "Сайтта төлөм жокпу — бөгөттү алуубу?", "Payment not on the site — unlock?", "Ödeme sitede yok — kilit kaldırılsın mı?", "To'lov saytda yo'q — blokni olib tashlashmi?");

    public static string UnlockMessage(UnknownDebtPayment p) => T($"Оплата {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} осталась без ответа сервера. Снимайте блокировку, только если проверили на сайте NurCRM (клиент → долги, оплаты): этой оплаты НЕТ. Если она там есть — выберите «Оплата есть на сайте». Иначе деньги будут учтены дважды.",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөм сервердин жообусуз калды. NurCRM сайтынан текшерип (кардар → карыздар, төлөмдөр), бул төлөм ЖОК болсо гана бөгөттү алыңыз. Эгер ал бар болсо — «Төлөм сайтта бар» тандаңыз. Болбосо акча эки жолу эсептелет.",
        $"The payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} got no answer from the server. Unlock only if you checked on the NurCRM site (customer → debts, payments) that this payment is NOT there. If it is there, choose “Payment is on the site”. Otherwise the money will be counted twice.",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödeme sunucudan yanıt almadı. Kilidi yalnızca NurCRM sitesinde (müşteri → borçlar, ödemeler) bu ödemenin OLMADIĞINI kontrol ettiyseniz kaldırın. Varsa «Ödeme sitede var»ı seçin. Aksi halde para iki kez sayılır.",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lov server javobisiz qoldi. Blokni faqat NurCRM saytida (mijoz → qarzlar, to'lovlar) bu to'lov YO'Qligini tekshirgan bo'lsangiz olib tashlang. Agar u bor bo'lsa — «To'lov saytda bor»ni tanlang. Aks holda pul ikki marta hisoblanadi.");

    public static string UnlockConfirmText => T("Я проверил(а): этой оплаты нет", "Текшердим: бул төлөм жок", "I checked: this payment is not there", "Kontrol ettim: bu ödeme yok", "Tekshirdim: bu to'lov yo'q");

    public static string PaidOnSiteTitle => T("Оплата есть на сайте — отметить проведённой?", "Төлөм сайтта барбы — өткөн деп белгилейбизби?", "Payment is on the site — mark as made?", "Ödeme sitede var — yapıldı olarak işaretlensin mi?", "To'lov saytda bor — o'tkazilgan deb belgilashmi?");

    public static string PaidOnSiteMessage(UnknownDebtPayment p) => T(
        $"Отметьте, только если на сайте NurCRM (клиент → долги, оплаты) эта оплата {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ЕСТЬ. Она будет учтена в смене, где брали деньги, повторно на сервер не отправляется.",
        $"NurCRM сайтында (кардар → карыздар, төлөмдөр) бул {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөм БАР болсо гана белгилеңиз. Ал акча алынган сменада эсептелет, серверге кайра жөнөтүлбөйт.",
        $"Mark only if this payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} IS on the NurCRM site (customer → debts, payments). It will be counted in the shift where the money was taken and is not sent to the server again.",
        $"Yalnızca bu {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödeme NurCRM sitesinde (müşteri → borçlar, ödemeler) VARSA işaretleyin. Paranın alındığı vardiyada sayılır, sunucuya tekrar gönderilmez.",
        $"Faqat bu {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lov NurCRM saytida (mijoz → qarzlar, to'lovlar) BOR bo'lsa belgilang. U pul olingan smenada hisoblanadi, serverga qayta yuborilmaydi.");

    public static string PaidOnSiteConfirmText => T("Я проверил(а): оплата есть", "Текшердим: төлөм бар", "I checked: the payment is there", "Kontrol ettim: ödeme var", "Tekshirdim: to'lov bor");

    public static string PaidOnSiteLabel => T("Оплата есть на сайте", "Төлөм сайтта бар", "Payment is on the site", "Ödeme sitede var", "To'lov saytda bor");

    public static string UnlockLabel => T("Оплаты нет на сайте", "Сайтта төлөм жок", "Payment not on the site", "Ödeme sitede yok", "To'lov saytda yo'q");

    public static string ResolvedPaidText(UnknownDebtPayment p) => T(
        $"Оплата {DebtPaymentFlow.Money(p.Amount)} отмечена проведённой (есть на сайте).", $"{DebtPaymentFlow.Money(p.Amount)} төлөм өткөн деп белгиленди (сайтта бар).",
        $"The payment of {DebtPaymentFlow.Money(p.Amount)} is marked as made (it is on the site).", $"{DebtPaymentFlow.Money(p.Amount)} ödeme yapıldı olarak işaretlendi (sitede var).",
        $"{DebtPaymentFlow.Money(p.Amount)} to'lov o'tkazilgan deb belgilandi (saytda bor).");

    public static string ResolvedUnlockedText => T("Блокировка снята — можно принять оплату заново.", "Бөгөт алынды — төлөмдү кайра кабыл алса болот.",
        "Unlocked — you can take the payment again.", "Kilit kaldırıldı — ödemeyi yeniden alabilirsiniz.", "Blok olib tashlandi — to'lovni qayta qabul qilish mumkin.");

    public static string PendingExistsText => T(
        "По клиенту есть незавершённая оплата — сначала решите её («Повторить» или проверка на сайте).",
        "Кардар боюнча аягына чыкпаган төлөм бар — адегенде аны чечиңиз («Кайталоо» же сайттан текшерүү).",
        "This client has an unfinished payment — resolve it first (“Retry” or check on the site).",
        "Bu müşterinin tamamlanmamış bir ödemesi var — önce onu çözün («Tekrar dene» veya sitede kontrol).",
        "Mijozda tugallanmagan to'lov bor — avval uni hal qiling («Qayta urinish» yoki saytda tekshirish).");

    public static string AlreadyDoneText => T("Эта оплата уже завершена (в другом окне) — список обновлён.", "Бул төлөм мурунтан эле аяктаган (башка терезеде) — тизме жаңыртылды.",
        "This payment is already finished (in another window) — the list has been refreshed.", "Bu ödeme zaten tamamlandı (başka pencerede) — liste yenilendi.",
        "Bu to'lov allaqachon yakunlangan (boshqa oynada) — ro'yxat yangilandi.");

    public static string StoreBusyText => T("Журнал незавершённых оплат сейчас недоступен — оплата не проведена. Повторите через несколько секунд.",
        "Аягына чыкпаган төлөмдөрдүн журналы азыр жеткиликсиз — төлөм өткөн жок. Бир нече секунддан кийин кайталаңыз.",
        "The unfinished payments log is unavailable right now — the payment was not made. Try again in a few seconds.",
        "Tamamlanmamış ödemeler günlüğü şu anda kullanılamıyor — ödeme yapılmadı. Birkaç saniye sonra tekrar deneyin.",
        "Tugallanmagan to'lovlar jurnali hozir mavjud emas — to'lov o'tmadi. Bir necha soniyadan keyin qayta urining.");

    public static string StoreWriteFailedText => T("Не удалось записать журнал незавершённых оплат — оплата не проведена. Проверьте место на диске и повторите.",
        "Аягына чыкпаган төлөмдөрдүн журналын жазуу мүмкүн болгон жок — төлөм өткөн жок. Дисктеги орунду текшерип, кайталаңыз.",
        "Couldn't write the unfinished payments log — the payment was not made. Check disk space and try again.",
        "Tamamlanmamış ödemeler günlüğü yazılamadı — ödeme yapılmadı. Disk alanını kontrol edip tekrar deneyin.",
        "Tugallanmagan to'lovlar jurnalini yozib bo'lmadi — to'lov o'tmadi. Diskdagi joyni tekshirib, qayta urining.");

    public static string CorruptText(string file) => T(
        $"Журнал незавершённых оплат повреждён — оплаты долгов закрыты. Обратитесь в поддержку (файл {file}).",
        $"Аягына чыкпаган төлөмдөрдүн журналы бузулган — карыз төлөмдөрү жабык. Колдоо кызматына кайрылыңыз (файл {file}).",
        $"The unfinished payments log is damaged — debt payments are closed. Contact support (file {file}).",
        $"Tamamlanmamış ödemeler günlüğü bozuk — borç ödemeleri kapalı. Destekle iletişime geçin (dosya {file}).",
        $"Tugallanmagan to'lovlar jurnali buzilgan — qarz to'lovlari yopiq. Qo'llab-quvvatlashga murojaat qiling (fayl {file}).");

    /// <summary>Способ одним словом в тексте («оплата 500 сом наличными»).</summary>
    public static string MethodShort(string method) => method == DebtPaymentSettings.MethodTransfer
        ? T("безналом", "накталай эмес", "non-cash", "nakitsiz", "naqd emas")
        : T("наличными", "накталай", "in cash", "nakit", "naqd");

    /// <summary>2026-10-18, четвёртый круг: оплата клиента сейчас идёт в другом окне — ждать её ответа.</summary>
    /// <param name="ownOperation">2026-10-18, шестой круг: запрос по этой записи отправляет само это окно — «Повторяем…», а не
    /// «идёт в другом окне».</param>
    /// <remarks>2026-10-18, седьмой круг: запись другой программы на этом ПК — ForeignText (действовать там).</remarks>
    public static string UnknownText(UnknownDebtPayment p, bool ownOperation = false) =>
        !IsOwnProgram(p) ? ForeignText(p) : IsInFlight(p.ClientId) ? (ownOperation ? RetryingText(p) : InFlightText(p)) : LockText(p);

    /// <summary>2026-10-18, седьмой круг: оплату начала другая программа на этом ПК (касса / программа владельца).</summary>
    public static string ForeignText(UnknownDebtPayment p)
    {
        var owner = p.Program == ProgramOwner;
        var m = DebtPaymentFlow.Money(p.Amount);
        var ms = MethodShort(p.Method);
        return T(
            $"Оплата {m} {ms} этого клиента начата {(owner ? "в программе владельца" : "в кассе")} на этом компьютере и не завершена. Повторите её или решите там — здесь оплаты этого клиента закрыты.",
            $"Бул кардардын {m} {ms} төлөмү ушул компьютердеги {(owner ? "ээсинин программасында" : "кассада")} башталып, аягына чыккан жок. Аны ошол жерде кайталаңыз же чечиңиз — бул жерде бул кардардын төлөмдөрү жабык.",
            $"A payment of {m} {ms} for this client was started {(owner ? "in the owner app" : "at the till")} on this computer and isn't finished. Retry or resolve it there — payments for this client are closed here.",
            $"Bu müşterinin {m} {ms} ödemesi bu bilgisayarda {(owner ? "sahip programında" : "kasada")} başlatıldı ve tamamlanmadı. Orada tekrarlayın veya çözün — burada bu müşterinin ödemeleri kapalı.",
            $"Bu mijozning {m} {ms} to'lovi shu kompyuterda {(owner ? "egasi dasturida" : "kassada")} boshlangan va tugallanmagan. Uni o'sha yerda qaytaring yoki hal qiling — bu yerda bu mijozning to'lovlari yopiq.");
    }

    public static string LockBusyText => T(
        "Журнал оплат долгов занят другой программой на этом компьютере (там идёт оплата) — ничего не отправлено. Повторите через минуту.",
        "Карыз төлөмдөрүнүн журналын ушул компьютердеги башка программа колдонуп жатат (ал жерде төлөм жүрүп жатат) — эч нерсе жөнөтүлгөн жок. Бир мүнөттөн кийин кайталаңыз.",
        "The debt payments log is in use by another program on this computer (a payment is in progress there) — nothing was sent. Try again in a minute.",
        "Borç ödemeleri günlüğü bu bilgisayardaki başka bir program tarafından kullanılıyor (orada ödeme sürüyor) — hiçbir şey gönderilmedi. Bir dakika sonra tekrar deneyin.",
        "Qarz to'lovlari jurnali shu kompyuterdagi boshqa dastur tomonidan band (u yerda to'lov ketmoqda) — hech narsa yuborilmadi. Bir daqiqadan keyin qayta urining.");

    public static string RetryStoreBusyText => T(
        "Журнал незавершённых оплат сейчас недоступен — повтор не отправлен. Исход первой оплаты по-прежнему неизвестен, блокировку не снимайте. Повторите через несколько секунд.",
        "Аягына чыкпаган төлөмдөрдүн журналы азыр жеткиликсиз — кайталоо жөнөтүлгөн жок. Биринчи төлөмдүн натыйжасы дагы эле белгисиз, бөгөттү албаңыз. Бир нече секунддан кийин кайталаңыз.",
        "The unfinished payments log is unavailable right now — the retry was not sent. The outcome of the first payment is still unknown; don't unlock. Try again in a few seconds.",
        "Tamamlanmamış ödemeler günlüğü şu anda kullanılamıyor — tekrar gönderilmedi. İlk ödemenin sonucu hâlâ bilinmiyor, kilidi kaldırmayın. Birkaç saniye sonra tekrar deneyin.",
        "Tugallanmagan to'lovlar jurnali hozir mavjud emas — qayta urinish yuborilmadi. Birinchi to'lov natijasi hamon noma'lum, blokni olib tashlamang. Bir necha soniyadan keyin qayta urining.");

    public static string ResolveStoreBusyText => T(
        "Журнал незавершённых оплат сейчас недоступен — решение не сохранено, блокировка осталась. Повторите через несколько секунд.",
        "Аягына чыкпаган төлөмдөрдүн журналы азыр жеткиликсиз — чечим сакталган жок, бөгөт калды. Бир нече секунддан кийин кайталаңыз.",
        "The unfinished payments log is unavailable right now — the decision was not saved, the lock stays. Try again in a few seconds.",
        "Tamamlanmamış ödemeler günlüğü şu anda kullanılamıyor — karar kaydedilmedi, kilit duruyor. Birkaç saniye sonra tekrar deneyin.",
        "Tugallanmagan to'lovlar jurnali hozir mavjud emas — qaror saqlanmadi, blok qoldi. Bir necha soniyadan keyin qayta urining.");

    public static string ResolveStoreFailedText => T(
        "Не удалось записать журнал незавершённых оплат — решение не сохранено, блокировка осталась. Проверьте место на диске и повторите.",
        "Аягына чыкпаган төлөмдөрдүн журналын жазуу мүмкүн болгон жок — чечим сакталган жок, бөгөт калды. Дисктеги орунду текшерип, кайталаңыз.",
        "Couldn't write the unfinished payments log — the decision was not saved, the lock stays. Check disk space and try again.",
        "Tamamlanmamış ödemeler günlüğü yazılamadı — karar kaydedilmedi, kilit duruyor. Disk alanını kontrol edip tekrar deneyin.",
        "Tugallanmagan to'lovlar jurnalini yozib bo'lmadi — qaror saqlanmadi, blok qoldi. Diskdagi joyni tekshirib, qayta urining.");

    /// <summary>Сервер принял, а запись не удалилась (диск) — замок остаётся, в смену не записано.</summary>
    public static string AcceptedButStoreFailedText(double paid)
    {
        var m = DebtPaymentFlow.Money(paid);
        return T(
            $"Сервер принял оплату {m}, но журнал незавершённых оплат не записан — на этом компьютере оплата пока не учтена, блокировка осталась. Освободите место на диске и нажмите «Повторить»: тот же ключ — сервер второй раз деньги не проведёт.",
            $"Сервер {m} төлөмдү кабыл алды, бирок аягына чыкпаган төлөмдөрдүн журналы жазылган жок — бул компьютерде төлөм азырынча эсептелген жок, бөгөт калды. Дисктен орун бошотуп, «Кайталоо» басыңыз: ошол эле ачкыч — сервер акчаны экинчи жолу өткөрбөйт.",
            $"The server accepted the payment of {m}, but the unfinished payments log was not written — the payment is not counted on this computer yet, the lock stays. Free up disk space and press “Retry”: same key — the server won't take the money twice.",
            $"Sunucu {m} ödemeyi kabul etti, ancak tamamlanmamış ödemeler günlüğü yazılamadı — ödeme bu bilgisayarda henüz sayılmadı, kilit duruyor. Diskte yer açıp «Tekrar dene»ye basın: aynı anahtar — sunucu parayı iki kez almaz.",
            $"Server {m} to'lovni qabul qildi, lekin tugallanmagan to'lovlar jurnali yozilmadi — bu kompyuterda to'lov hozircha hisoblanmadi, blok qoldi. Diskda joy bo'shatib, «Qayta urinish»ni bosing: o'sha kalit — server pulni ikki marta o'tkazmaydi.");
    }

    /// <summary>2026-10-18, восьмой круг (п.2): сервер оплату точно не провёл, а журнал не обновился.</summary>
    public static string NotAppliedStoreFailedText => T(
        "Оплата не проведена, но журнал незавершённых оплат не обновился (диск или доступ) — освободите место на диске; оплаты этого клиента временно закрыты.",
        "Төлөм өткөн жок, бирок аягына чыкпаган төлөмдөрдүн журналы жаңырган жок (диск же уруксат) — дисктен орун бошотуңуз; бул кардардын төлөмдөрү убактылуу жабык.",
        "The payment was not made, but the unfinished payments log was not updated (disk or access) — free up disk space; payments for this client are temporarily closed.",
        "Ödeme yapılmadı, ancak tamamlanmamış ödemeler günlüğü güncellenmedi (disk veya erişim) — diskte yer açın; bu müşterinin ödemeleri geçici olarak kapalı.",
        "To'lov o'tmadi, lekin tugallanmagan to'lovlar jurnali yangilanmadi (disk yoki ruxsat) — diskda joy bo'shating; bu mijozning to'lovlari vaqtincha yopiq.");

    public static string ShiftNotRecordedText => T(
        "Внимание: оплата не записалась в смену на этом компьютере (ошибка базы кассы) — в отчёте смены её не будет, на сервере NurCRM она есть. Сообщите владельцу.",
        "Көңүл буруңуз: төлөм бул компьютердеги сменага жазылган жок (кассанын базасынын катасы) — смена отчётунда ал болбойт, NurCRM серверинде ал бар. Ээсине билдириңиз.",
        "Attention: the payment was not written to the shift on this computer (till database error) — it won't be in the shift report; it is on the NurCRM server. Tell the owner.",
        "Dikkat: ödeme bu bilgisayarda vardiyaya yazılamadı (kasa veritabanı hatası) — vardiya raporunda olmayacak, NurCRM sunucusunda var. Sahibine bildirin.",
        "Diqqat: to'lov bu kompyuterda smenaga yozilmadi (kassa bazasi xatosi) — smena hisobotida bo'lmaydi, NurCRM serverida bor. Egasiga xabar bering.");

    public static string OwnerEndpointMissingText => T(
        "Сервер NurCRM пока не принимает оплату долга без кассы. Примите оплату в кассе (меню → «Оплата долга»).",
        "NurCRM сервери азырынча кассасыз карыз төлөмүн кабыл албайт. Төлөмдү кассада кабыл алыңыз (меню → «Карыз төлөө»).",
        "The NurCRM server doesn't accept debt payments without a till yet. Take the payment at the till (menu → “Debt payment”).",
        "NurCRM sunucusu henüz kasasız borç ödemesi kabul etmiyor. Ödemeyi kasada alın (menü → «Borç ödemesi»).",
        "NurCRM serveri hozircha kassasiz qarz to'lovini qabul qilmaydi. To'lovni kassada qabul qiling (menyu → «Qarzni to'lash»).");

    public static string RetryingText(UnknownDebtPayment p) => T(
        $"Повторяем оплату {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} тем же ключом — ждём ответа сервера…",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөмүн ошол эле ачкыч менен кайталап жатабыз — сервердин жообун күтөбүз…",
        $"Retrying the payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} with the same key — waiting for the server…",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödeme aynı anahtarla tekrarlanıyor — sunucunun yanıtı bekleniyor…",
        $"{DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lovni o'sha kalit bilan qaytaryapmiz — server javobini kutyapmiz…");

    public static string StateChangedText => T("Состояние оплаты изменилось — проверьте экран.", "Төлөмдүн абалы өзгөрдү — экранды текшериңиз.",
        "The payment state changed — check the screen.", "Ödeme durumu değişti — ekranı kontrol edin.", "To'lov holati o'zgardi — ekranni tekshiring.");

    public static string InFlightText(UnknownDebtPayment p) => T(
        $"Оплата {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} этого клиента сейчас идёт в другом окне — дождитесь её ответа.",
        $"Бул кардардын {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөмү азыр башка терезеде жүрүп жатат — анын жообун күтүңүз.",
        $"A payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} for this client is in progress in another window — wait for its answer.",
        $"Bu müşterinin {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödemesi şu anda başka bir pencerede sürüyor — yanıtını bekleyin.",
        $"Bu mijozning {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lovi hozir boshqa oynada ketmoqda — javobini kuting.");

    private static string LockText(UnknownDebtPayment p) => IsRetryExpired(p)
        ? T($"Ответ сервера не дошёл — неизвестно, прошла ли оплата {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)}. Сумма, способ и чек закрыты. ",
              $"Сервердин жообу келген жок — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөм өттүбү, белгисиз. Сумма, ыкма жана чек жабык. ",
              $"The server's answer didn't arrive — it's unknown whether the payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} went through. Amount, method and receipt are locked. ",
              $"Sunucunun yanıtı gelmedi — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödemenin geçip geçmediği bilinmiyor. Tutar, yöntem ve fiş kilitli. ",
              $"Server javobi kelmadi — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lov o'tganmi, noma'lum. Summa, usul va chek yopiq. ") + RetryExpiredText
        : T(
        $"Ответ сервера не дошёл — неизвестно, прошла ли оплата {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)}. Сумма, способ и чек закрыты. «Повторить» — безопасно: тот же ключ, сервер не проведёт её дважды.",
        $"Сервердин жообу келген жок — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} төлөм өттүбү, белгисиз. Сумма, ыкма жана чек жабык. «Кайталоо» — коопсуз: ошол эле ачкыч, сервер аны эки жолу өткөрбөйт.",
        $"The server's answer didn't arrive — it's unknown whether the payment of {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} went through. Amount, method and receipt are locked. “Retry” is safe: same key, the server won't take it twice.",
        $"Sunucunun yanıtı gelmedi — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} ödemenin geçip geçmediği bilinmiyor. Tutar, yöntem ve fiş kilitli. «Tekrar dene» güvenlidir: aynı anahtar, sunucu iki kez almaz.",
        $"Server javobi kelmadi — {DebtPaymentFlow.Money(p.Amount)} {MethodShort(p.Method)} to'lov o'tganmi, noma'lum. Summa, usul va chek yopiq. «Qayta urinish» — xavfsiz: o'sha kalit, server uni ikki marta o'tkazmaydi.");

    public static string RetryLabel(UnknownDebtPayment p) =>
        T("Повторить ", "Кайталоо ", "Retry ", "Tekrar dene ", "Qayta urinish ") + DebtPaymentFlow.Money(p.Amount);

    public static string LockedText => T(
        // 2026-10-18, девятый круг (п.4): названия существующих кнопок панели.
        "Сначала решите прошлую оплату этого клиента: «Повторить», «Оплата есть на сайте» или «Оплаты нет на сайте».",
        "Адегенде бул кардардын мурунку төлөмүн чечиңиз: «Кайталоо», «Төлөм сайтта бар» же «Сайтта төлөм жок».",
        "First resolve this client's previous payment: “Retry”, “Payment is on the site” or “Payment not on the site”.",
        "Önce bu müşterinin önceki ödemesini çözün: «Tekrar dene», «Ödeme sitede var» veya «Ödeme sitede yok».",
        "Avval bu mijozning oldingi to'lovini hal qiling: «Qayta urinish», «To'lov saytda bor» yoki «To'lov saytda yo'q».");

    public static string RetryFailedText(Exception ex) => T(
        "Повтор не удался — блокировка остаётся, ключ тот же. Причина: ",
        "Кайталоо болгон жок — бөгөт калат, ачкыч ошол эле. Себеби: ",
        "The retry failed — the lock stays, same key. Reason: ",
        "Tekrar başarısız — kilit duruyor, anahtar aynı. Neden: ",
        "Qayta urinish muvaffaqiyatsiz — blok qoladi, kalit o'sha. Sababi: ") + ex.Message;

    public static string NotSentText => T(
        "Нет связи с сервером — оплата не проведена. Повторите, когда связь появится.",
        "Сервер менен байланыш жок — төлөм өткөн жок. Байланыш болгондо кайталаңыз.",
        "No connection to the server — the payment was not made. Try again when the connection is back.",
        "Sunucu bağlantısı yok — ödeme yapılmadı. Bağlantı gelince tekrar deneyin.",
        "Server bilan aloqa yo'q — to'lov o'tmadi. Aloqa tiklanganda qayta urining.");

    public static string ShiftClosedText => T(
        "Смена, в которой брали деньги, закрыта. Проверьте оплату на сайте NurCRM: если её нет — снимите блокировку и примите оплату заново в текущей смене.",
        "Акча алынган смена жабылган. Төлөмдү NurCRM сайтынан текшериңиз: эгер ал жок болсо — бөгөттү алып, төлөмдү учурдагы сменада кайра кабыл алыңыз.",
        "The shift in which the money was taken is closed. Check the payment on the NurCRM site: if it isn't there, unlock and take the payment again in the current shift.",
        "Paranın alındığı vardiya kapalı. Ödemeyi NurCRM sitesinde kontrol edin: yoksa kilidi kaldırın ve ödemeyi mevcut vardiyada yeniden alın.",
        "Pul olingan smena yopilgan. To'lovni NurCRM saytida tekshiring: agar u bo'lmasa — blokni olib tashlang va to'lovni joriy smenada qayta qabul qiling.");

    public static string CompanyUnknownText => T(
        "Не удалось определить компанию — оплата не проведена. Проверьте вход и связь, затем повторите.",
        "Компанияны аныктоо мүмкүн болгон жок — төлөм өткөн жок. Кирүүнү жана байланышты текшерип, кайталаңыз.",
        "Couldn't determine the company — the payment was not made. Check the sign-in and connection, then try again.",
        "Şirket belirlenemedi — ödeme yapılmadı. Girişi ve bağlantıyı kontrol edip tekrar deneyin.",
        "Kompaniyani aniqlab bo'lmadi — to'lov o'tmadi. Kirish va aloqani tekshirib, qayta urining.");

    public static string BusyText => T(
        "Оплата этого клиента уже идёт (в другом окне) — дождитесь её ответа.",
        "Бул кардардын төлөмү башка терезеде жүрүп жатат — анын жообун күтүңүз.",
        "A payment for this client is already in progress (in another window) — wait for its answer.",
        "Bu müşterinin ödemesi zaten sürüyor (başka bir pencerede) — yanıtını bekleyin.",
        "Bu mijozning to'lovi allaqachon ketmoqda (boshqa oynada) — javobini kuting.");

    /// <summary>2026-10-18, четвёртый круг: повтор не отправлен — это НЕ значит, что первая оплата не прошла.</summary>
    public static string RetryNotSentText => T(
        "Нет связи — повтор не отправлен. Исход первой оплаты по-прежнему неизвестен, блокировку не снимайте.",
        "Байланыш жок — кайталоо жөнөтүлгөн жок. Биринчи төлөмдүн натыйжасы дагы эле белгисиз, бөгөттү албаңыз.",
        "No connection — the retry was not sent. The outcome of the first payment is still unknown; don't unlock.",
        "Bağlantı yok — tekrar gönderilmedi. İlk ödemenin sonucu hâlâ bilinmiyor, kilidi kaldırmayın.",
        "Aloqa yo'q — qayta urinish yuborilmadi. Birinchi to'lov natijasi hamon noma'lum, blokni olib tashlamang.");

    /// <summary>2026-10-18, четвёртый круг: текст успеха — один на кассу и владельца (касса «Оплачено», владелец «Принято»).</summary>
    public static string SuccessText(PayDebtResult result, bool bySale, bool owner) =>
        // 2026-10-21 (ревью денег, мелочь): Left < 0 — сервер не прислал остаток; не пишем «Долг погашен полностью».
        SuccessText(result.Paid, result.Left >= 0 ? result.Left : null, bySale, owner) + (result.Replayed ? " " + ReplayedNote : "");

    /// <summary>2026-10-18, десятый круг (п.10): то же для старого пути по взносам. leftAmount null — остаток неизвестен (не пишем).</summary>
    public static string SuccessText(double paidAmount, double? leftAmount, bool bySale, bool owner)
    {
        var paid = DebtPaymentFlow.Money(paidAmount);
        var head = owner
            ? T($"Принято {paid}.", $"{paid} кабыл алынды.", $"Received {paid}.", $"{paid} alındı.", $"{paid} qabul qilindi.")
            : T($"Оплачено {paid}.", $"{paid} төлөндү.", $"Paid {paid}.", $"{paid} ödendi.", $"{paid} to'landi.");
        if (leftAmount is not { } leftValue)
            return head;
        var left = DebtPaymentFlow.Money(leftValue);
        var tail = leftValue <= 0.005
            ? (bySale
                ? T("Долг по чеку погашен.", "Чек боюнча карыз жабылды.", "The receipt's debt is paid off.", "Fişin borcu kapandı.", "Chek bo'yicha qarz yopildi.")
                : T("Долг погашен полностью.", "Карыз толугу менен жабылды.", "The debt is fully paid off.", "Borç tamamen kapandı.", "Qarz to'liq yopildi."))
            : (bySale
                ? T($"Остаток по чеку: {left}.", $"Чек боюнча калдык: {left}.", $"Left on the receipt: {left}.", $"Fişte kalan: {left}.", $"Chek bo'yicha qoldiq: {left}.")
                : T($"Остаток долга: {left}.", $"Карыздын калдыгы: {left}.", $"Remaining debt: {left}.", $"Kalan borç: {left}.", $"Qarz qoldig'i: {left}."));
        return head + " " + tail;
    }

    public static string OtherShiftText => T(
        "Оплата учтена в той смене, где брали деньги (она уже закрыта) — в текущую смену и её ожидаемую сумму не попала.",
        "Төлөм акча алынган сменада эсептелди (ал жабылган) — учурдагы сменага жана анын күтүлгөн суммасына кирген жок.",
        "The payment was counted in the shift where the money was taken (already closed) — not in the current shift or its expected amount.",
        "Ödeme, paranın alındığı vardiyada sayıldı (kapalı) — mevcut vardiyaya ve beklenen tutarına girmedi.",
        "To'lov pul olingan smenada hisoblandi (u yopilgan) — joriy smenaga va uning kutilgan summasiga tushmadi.");

    public static string ReplayedNote => T(
        "(Это повтор уже проведённой оплаты — второй раз деньги не списаны.)",
        "(Бул мурун өткөн төлөмдүн кайталоосу — акча экинчи жолу алынган жок.)",
        "(This is a repeat of a payment already made — the money was not taken twice.)",
        "(Bu zaten yapılmış bir ödemenin tekrarı — para ikinci kez alınmadı.)",
        "(Bu allaqachon o'tkazilgan to'lovning takrori — pul ikkinchi marta olinmadi.)");
}
