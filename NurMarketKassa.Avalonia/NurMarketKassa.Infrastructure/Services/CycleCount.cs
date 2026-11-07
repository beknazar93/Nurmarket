using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (функция 9 «Циклическая инвентаризация», идея OCA stock_cycle_count / stock_inventory_discrepancy / stock_change_qty_reason — AGPL, код не заимствован):
// «Пересчёт дня» — каждый день 20–30 позиций вместо полной ревизии: сначала «подозрительные» (отрицательный остаток, частые удаления из чека, давно не считали),
// затем по классу ABC (A — раз в неделю, B — раз в месяц, C — раз в квартал). Причина расхождения из списка пишется в примечание акта ревизии на сервере;
// крупное расхождение требует PIN владельца; история «когда считали товар» хранится локально. Всё в этом файле — чистая логика без окон (тесты — CycleCountTests).

/// <summary>Один товар-кандидат на «Пересчёт дня».</summary>
/// <param name="Abc">«A» / «B» / «C» по выручке за 12 недель; пусто — продаж нет.</param>
/// <param name="Deletions">Сколько раз товар убирали из чека за последние 30 дней (журнал «Удалено из чека»).</param>
public sealed record CycleCandidate(string Id, string Name, string Abc, double Stock, DateTime? LastCountedAt, int Deletions);

/// <summary>Выбранная позиция и почему: коды из <see cref="CycleCountPlanner"/> (negative / deletions / overdue / abc).</summary>
public sealed record CycleCountPick(CycleCandidate Item, double Score, IReadOnlyList<string> Reasons, int DaysSinceCount);

public static class CycleCountPlanner
{
    public const string ReasonNegative = "negative";
    public const string ReasonDeletions = "deletions";
    public const string ReasonOverdue = "overdue";
    public const string ReasonAbc = "abc";

    /// <summary>Сколько удалений из чека за 30 дней делают товар «подозрительным».</summary>
    public const int SuspiciousDeletions = 3;

    /// <summary>Как часто считать товар класса A/B/C (дней); без продаж — раз в полгода.</summary>
    public static int PeriodDays(string abc) => abc switch { "A" => 7, "B" => 30, "C" => 90, _ => 180 };

    /// <summary>Выбрать до <paramref name="count"/> позиций. Порядок стабилен: при равных баллах — по названию, затем по Id.
    /// Мёртвые позиции (остаток 0, нет продаж, нет удалений) не берём: их пересчёт ничего не даёт.</summary>
    public static List<CycleCountPick> Pick(IEnumerable<CycleCandidate> candidates, int count, DateTime today)
    {
        count = Math.Clamp(count, 1, 200);
        var picks = new List<CycleCountPick>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Id))
                continue;
            if (Math.Abs(c.Stock) < 1e-9 && string.IsNullOrEmpty(c.Abc) && c.Deletions == 0)
                continue;
            var period = PeriodDays(c.Abc);
            // Ни разу не считали — как «просрочено втрое»: попадёт в выборку, но после отрицательных остатков и удалений.
            var since = c.LastCountedAt is { } last ? Math.Max(0, (int)(today.Date - last.Date).TotalDays) : period * 3;
            var ratio = (double)since / period;
            var reasons = new List<string>();
            var score = 0.0;
            if (c.Stock < -1e-9)
            {
                score += 1000; // «подозрительные» идут строго первыми: отрицательный остаток важнее удалений, удаления — важнее «давно не считали»
                reasons.Add(ReasonNegative);
            }
            if (c.Deletions >= SuspiciousDeletions)
            {
                score += 500 + Math.Min(c.Deletions, 10);
                reasons.Add(ReasonDeletions);
            }
            if (ratio >= 1)
            {
                score += 30 * Math.Min(ratio, 4);
                reasons.Add(ReasonOverdue);
            }
            else
                score += 5 * ratio; // ещё не пора: в конец очереди, но порядок между такими сохраняется
            switch (c.Abc)
            {
                case "A": score += 10; reasons.Add(ReasonAbc); break;
                case "B": score += 5; reasons.Add(ReasonAbc); break;
            }
            picks.Add(new CycleCountPick(c, score, reasons, since));
        }
        return picks
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Item.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    /// <summary>Короткая подпись причины выбора для окна «Пересчёт дня».</summary>
    public static string ReasonText(CycleCountPick p)
    {
        var parts = new List<string>();
        foreach (var r in p.Reasons)
        {
            switch (r)
            {
                case ReasonNegative:
                    parts.Add(Tr.T("отрицательный остаток", "терс калдык", "negative stock", "eksi stok", "manfiy qoldiq"));
                    break;
                case ReasonDeletions:
                    parts.Add(Tr.T($"частые удаления из чека ({p.Item.Deletions})", $"чектен тез-тез өчүрүү ({p.Item.Deletions})", $"frequent removals from the receipt ({p.Item.Deletions})",
                        $"fişten sık silme ({p.Item.Deletions})", $"chekdan tez-tez o'chirish ({p.Item.Deletions})"));
                    break;
                case ReasonOverdue:
                    parts.Add(p.Item.LastCountedAt is null
                        ? Tr.T("ещё не считали", "али саналган эмес", "never counted", "hiç sayılmadı", "hali sanalmagan")
                        : Tr.T($"давно не считали ({p.DaysSinceCount} дн.)", $"көптөн бери саналган эмес ({p.DaysSinceCount} күн)", $"not counted for long ({p.DaysSinceCount} d)",
                            $"uzun süredir sayılmadı ({p.DaysSinceCount} gün)", $"anchadan beri sanalmagan ({p.DaysSinceCount} kun)"));
                    break;
                case ReasonAbc:
                    parts.Add(Tr.T($"класс {p.Item.Abc}", $"{p.Item.Abc} классы", $"class {p.Item.Abc}", $"{p.Item.Abc} sınıfı", $"{p.Item.Abc} sinfi"));
                    break;
            }
        }
        return string.Join(", ", parts);
    }
}

/// <summary>Причины расхождения (список в окне перед проведением ревизии) и сборка примечания к акту.</summary>
public static class CycleCountReasons
{
    public static readonly string[] Codes = ["spoilage", "theft", "mixup", "receipt_error", "other"];

    public static string Title(string? code) => code switch
    {
        "spoilage" => Tr.T("порча", "бузулган", "spoilage", "bozulma", "buzilish"),
        "theft" => Tr.T("кража", "уурдоо", "theft", "hırsızlık", "o'g'irlik"),
        "mixup" => Tr.T("пересорт", "аралашып калуу", "mix-up", "karışıklık", "qayta saralash"),
        "receipt_error" => Tr.T("ошибка прихода", "кирүү катасы", "receiving error", "giriş hatası", "kirim xatosi"),
        "other" => Tr.T("другое", "башка", "other", "diğer", "boshqa"),
        _ => Tr.T("не указана", "көрсөтүлгөн эмес", "not specified", "belirtilmedi", "ko'rsatilmagan"),
    };

    private static int OrderOf(string code)
    {
        var i = Array.IndexOf(Codes, code);
        return i < 0 ? 99 : i;
    }

    /// <summary>Хвост примечания акта: «Причины: порча — Молоко (−3), Сыр (−1); кража — …». Не длиннее <paramref name="maxLength"/> (сервер лимит примечания не подтвердил — берём с запасом).
    /// Позиции без причины в примечание не попадают.</summary>
    public static string NoteSuffix(IEnumerable<(string Name, double Difference, string? ReasonCode)> rows, int maxLength = 400)
    {
        var inv = CultureInfo.InvariantCulture;
        var groups = rows.Where(r => !string.IsNullOrWhiteSpace(r.ReasonCode))
            .GroupBy(r => r.ReasonCode!, StringComparer.Ordinal)
            .OrderBy(g => OrderOf(g.Key))
            .ToList();
        if (groups.Count == 0)
            return "";
        var head = Tr.T("Причины: ", "Себептер: ", "Reasons: ", "Nedenler: ", "Sabablar: ");
        var sb = new StringBuilder(head);
        var first = true;
        foreach (var g in groups)
        {
            if (!first)
                sb.Append("; ");
            first = false;
            sb.Append(Title(g.Key)).Append(" — ");
            var items = g.Select(r => $"{r.Name} ({(r.Difference > 0 ? "+" : r.Difference < 0 ? "−" : "")}{Math.Abs(r.Difference).ToString("0.###", inv)})").ToList();
            sb.Append(string.Join(", ", items));
        }
        var text = sb.ToString();
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}

/// <summary>Порог расхождения, после которого ревизию проводит только владелец (PIN).</summary>
public static class CycleCountRules
{
    /// <summary>Нужен ли PIN: сумма недостач (по закупке, нет закупки — по цене продажи) или одна позиция не меньше порога. Порог ≤ 0 — проверка выключена.
    /// Излишки на PIN не влияют: деньги теряются на недостаче.</summary>
    public static bool NeedsOwnerPin(IEnumerable<(double Difference, double UnitPrice)> rows, double threshold)
    {
        if (!(threshold > 0) || double.IsNaN(threshold) || double.IsInfinity(threshold))
            return false;
        var total = 0.0;
        foreach (var (diff, price) in rows)
        {
            if (diff >= 0 || double.IsNaN(price) || price <= 0)
                continue;
            var loss = -diff * price;
            if (loss >= threshold)
                return true;
            total += loss;
        }
        return total >= threshold;
    }

    /// <summary>2026-11-05 (ревью волн 0–3, п. 1): спрашивать ли PIN у этого человека. Старший (владелец, администратор, право «Настройки», автономный режим)
    /// PIN не вводит; порог 0 — выключено.</summary>
    public static bool MustAskPin(IEnumerable<(double Difference, double UnitPrice)> rows, double threshold, bool isSenior) =>
        !isSenior && NeedsOwnerPin(rows, threshold);

    /// <summary>2026-11-05 (ревью волн 0–3, п. 1): можно ли завершать ревизию после проверки PIN. Принят — да. Неверный/заблокирован — нет.
    /// Не удалось проверить (нет связи, нет прав у сессии, ответ сервера непонятен) — да, но с честным предупреждением: иначе без интернета ревизию не провести вообще.</summary>
    // 2026-11-06 (аудит №5, Л5-06): обход PIN — только при настоящем отсутствии связи (Unavailable); ошибка/«нет прав» сервера (Error) — отказ.
    public static bool PinOutcomeAllowsFinish(LatePinStatus status) => status is LatePinStatus.Ok or LatePinStatus.Unavailable;
}

/// <summary>Одна запись истории пересчёта товара.</summary>
public sealed class CycleCountHistoryEntry
{
    public DateTime At { get; set; }
    public double Difference { get; set; }
    public string? Reason { get; set; }
}

public sealed class CycleCountData
{
    /// <summary>Порог для PIN владельца, сом (0 — выключено). 2026-11-05 (ревью волн 0–3, п. 1): по умолчанию ВЫКЛЮЧЕНО — раньше 5000 включало PIN
    /// для любой обычной ревизии, а без связи завершить её было нельзя. Включает владелец в настройках «Пересчёта дня».</summary>
    public double PinThreshold { get; set; }

    /// <summary>Сколько позиций в «Пересчёте дня» (20–30; допустимо 5–100).</summary>
    public int DayCount { get; set; } = 25;

    public Dictionary<string, List<CycleCountHistoryEntry>> History { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Normalize()
    {
        History = new Dictionary<string, List<CycleCountHistoryEntry>>(History ?? new(), StringComparer.OrdinalIgnoreCase);
        DayCount = Math.Clamp(DayCount, 5, 100);
        PinThreshold = double.IsFinite(PinThreshold) ? Math.Max(0, PinThreshold) : 0; // 2026-11-05: сбой значения = выключено, а не 5000
    }
}

/// <summary>Локальное хранилище «Пересчёта дня»: настройки и история «когда считали товар» (по 6 последних записей на товар).
/// Файл cycle-count.json в папке данных программы (как shop-automation.json). Сервера для истории нет — локально, только этой кассы.</summary>
public static class CycleCountStore
{
    private const int KeepPerProduct = 6;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static CycleCountData? _current;
    private static DateTime _readStamp;

    /// <summary>Только для тестов и стенда: другая папка вместо %LOCALAPPDATA%\NurMarketKassa.</summary>
    public static string? DataDirOverride { get; set; }

    // 2026-11-06 (аудит №5, Л5-11): порог PIN ревизии и история пересчётов — ОБЩИЙ файл кассы и программы владельца (%LOCALAPPDATA%\NurMarketKassa, как shop-automation.json).
    // Раньше у каждой программы был свой файл (AppMode.DataFolderName), и порог, поставленный владельцем, касса не видела. Старые файлы при первом чтении переносятся.
    private static string FilePath => Path.Combine(DataDirOverride ?? AppPaths.KassaLocal, "cycle-count.json");

    /// <summary>Данные с учётом изменений файла другой программой (отметка времени).</summary>
    private static CycleCountData Load()
    {
        var stamp = StampOf(FilePath);
        if (_current is null || stamp != _readStamp)
        {
            var fresh = Read(out var ok);
            if (ok || _current is null)
                _current = fresh;
            if (ok)
                _readStamp = stamp;
        }
        return _current;
    }

    private static string MigratedFlagPath => Path.Combine(AppPaths.KassaLocal, "cycle-count.migrated");

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception) { return default; }
    }

    public static CycleCountData Current
    {
        get
        {
            lock (Gate)
                return Load();
        }
    }

    public static void Update(Action<CycleCountData> change)
    {
        lock (Gate)
        {
            var data = Load();
            change(data);
            data.Normalize();
            Save(data);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _current = null;
            _readStamp = default;
        }
    }

    /// <summary>Записать итог пересчёта: дата, расхождение и причина по каждой посчитанной позиции.</summary>
    public static void Record(IEnumerable<(string ProductId, double Difference, string? Reason)> rows, DateTime at)
    {
        lock (Gate)
        {
            var data = Load();
            foreach (var (id, diff, reason) in rows)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!data.History.TryGetValue(id, out var list))
                    data.History[id] = list = new List<CycleCountHistoryEntry>();
                list.Add(new CycleCountHistoryEntry { At = at, Difference = diff, Reason = reason });
                if (list.Count > KeepPerProduct)
                    list.RemoveRange(0, list.Count - KeepPerProduct);
            }
            data.Normalize();
            Save(data);
        }
    }

    public static DateTime? LastCounted(string productId)
    {
        lock (Gate)
        {
            var data = Load();
            return data.History.TryGetValue(productId, out var list) && list.Count > 0 ? list.Max(e => e.At) : null;
        }
    }

    /// <summary>«Считали: 12.10.2026 (−2, порча); 03.09.2026 (0)» — для карточки ревизии. Пусто, если не считали.</summary>
    public static string HistoryText(string productId, int take = 3)
    {
        List<CycleCountHistoryEntry> list;
        lock (Gate)
        {
            var data = Load();
            if (!data.History.TryGetValue(productId, out var found) || found.Count == 0)
                return "";
            list = found.OrderByDescending(e => e.At).Take(take).ToList();
        }
        var inv = CultureInfo.InvariantCulture;
        var parts = list.Select(e =>
        {
            var diff = Math.Abs(e.Difference) < 0.0005 ? "0" : (e.Difference > 0 ? "+" : "−") + Math.Abs(e.Difference).ToString("0.###", inv);
            var reason = string.IsNullOrWhiteSpace(e.Reason) ? "" : ", " + CycleCountReasons.Title(e.Reason);
            return $"{e.At:dd.MM.yyyy} ({diff}{reason})";
        });
        return Tr.T("Считали: ", "Саналды: ", "Counted: ", "Sayıldı: ", "Sanalgan: ") + string.Join("; ", parts);
    }

    private static void Save(CycleCountData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(data, Json));
            _readStamp = StampOf(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Пересчёт дня: файл не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>ok = false — файл есть, но прочитать не удалось (занят): прежние настройки остаются, поверх файла умолчания не пишутся (Л5-10).</summary>
    private static CycleCountData Read(out bool ok)
    {
        ok = true;
        try
        {
            if (File.Exists(FilePath))
            {
                var r = AtomicFile.TryRead(FilePath, attempts: 2, pauseMs: 50);
                if (r.Status == AtomicFile.ReadStatus.Unreadable)
                {
                    ok = false;
                    PosLogger.Log("Пересчёт дня: файл занят и не прочитан — остаются прежние настройки.", "WARNING");
                }
                else
                {
                    var data = JsonSerializer.Deserialize<CycleCountData>(r.Text ?? "{}", Json) ?? new CycleCountData();
                    data.Normalize();
                    return data;
                }
            }
            else if (DataDirOverride is null && !File.Exists(MigratedFlagPath))
            {
                // перенос делается один раз: после смены компании общий файл уносится в _previous, и без отметки старый файл соседней программы «воскресил» бы чужую историю
                var migrated = MigrateLegacy();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(MigratedFlagPath)!);
                    File.WriteAllText(MigratedFlagPath, DateTime.Now.ToString("O"));
                }
                catch (Exception) { /* без отметки перенос просто повторится */ }
                if (migrated is not null)
                    return migrated;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Пересчёт дня: файл не прочитан ({ex.GetType().Name}) — взяты умолчания.", "WARNING");
        }
        var fresh = new CycleCountData();
        fresh.Normalize();
        return fresh;
    }

    /// <summary>2026-11-06 (Л5-11): личные файлы кассы и владельца (%APPDATA%\NurMarketKassa|NurMarketOwner\cycle-count.json) → общий. Берётся более новый;
    /// история пересчётов сливается (по товару — записи обоих файлов). Старые файлы не удаляются. null — старых нет.</summary>
    private static CycleCountData? MigrateLegacy()
    {
        var found = new List<(DateTime At, CycleCountData Data)>();
        foreach (var folder in new[] { "NurMarketKassa", "NurMarketOwner" })
        {
            try
            {
                var old = Path.Combine(AppPaths.Roaming, folder, "cycle-count.json");
                if (!File.Exists(old))
                    continue;
                var d = JsonSerializer.Deserialize<CycleCountData>(File.ReadAllText(old), Json);
                if (d is null)
                    continue;
                d.Normalize();
                found.Add((File.GetLastWriteTimeUtc(old), d));
            }
            catch (Exception)
            {
                // испорченный старый файл пропускаем — он остаётся на месте
            }
        }
        if (found.Count == 0)
            return null;
        found.Sort((a, b) => b.At.CompareTo(a.At));
        var merged = found[0].Data;
        for (var i = 1; i < found.Count; i++)
        {
            foreach (var (id, list) in found[i].Data.History)
            {
                if (!merged.History.TryGetValue(id, out var mine))
                    merged.History[id] = mine = new List<CycleCountHistoryEntry>();
                foreach (var entry in list.Where(x => !mine.Any(m => m.At == x.At)).ToList())
                    mine.Add(entry);
                mine.Sort((a, b) => a.At.CompareTo(b.At));
                if (mine.Count > KeepPerProduct)
                    mine.RemoveRange(0, mine.Count - KeepPerProduct);
            }
            if (merged.PinThreshold <= 0 && found[i].Data.PinThreshold > 0)
                merged.PinThreshold = found[i].Data.PinThreshold; // защита не ослабляется
        }
        merged.Normalize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(merged, Json));
            PosLogger.Log($"Пересчёт дня: настройки и история собраны в общий файл из {found.Count} старых.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Пересчёт дня: общий файл не записан ({ex.GetType().Name}).", "WARNING");
        }
        return merged;
    }
}
