using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-11-08 (запрос владельца «вернуть табель, отмечать сотрудника или авто-отметка»): статус дня сотрудника в табеле отметок.</summary>
public enum TimesheetStatus
{
    /// <summary>Был на работе (часы — в отметке).</summary>
    Present,
    /// <summary>Не был.</summary>
    Absent,
    /// <summary>Больничный.</summary>
    Sick,
    /// <summary>Отпуск.</summary>
    Vacation,
    /// <summary>Выходной.</summary>
    DayOff,
}

/// <summary>Отметка сотрудника на один день. Дата — календарный день по местному времени (как день ОТКРЫТИЯ смены в <see cref="StaffTimesheetService"/>).</summary>
public sealed class TimesheetMark
{
    /// <summary>id сотрудника на сервере; нет id (кассир известен только по сменам) — "n:Имя".</summary>
    public string EmployeeKey { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public DateOnly Date { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TimesheetStatus Status { get; set; }
    /// <summary>Часы за день (только для «Был»); null — не заданы (автоотметка до закрытия смены).</summary>
    public double? Hours { get; set; }
    public string? Comment { get; set; }
    /// <summary>true — поставлена кассой автоматически и ещё не правилась владельцем; ручная правка сбрасывает.</summary>
    public bool Auto { get; set; }
    /// <summary>Время прихода/ухода автоотметки (с часовым поясом: смена пояса не искажает часы).</summary>
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>Состояние отметки до/после изменения (для журнала).</summary>
public sealed class TimesheetSnapshot
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TimesheetStatus Status { get; set; }
    public double? Hours { get; set; }
    public string? Comment { get; set; }
    public bool Auto { get; set; }
}

/// <summary>Запись журнала «кто и когда поменял». Action: set, clear, auto-arrive, auto-leave.</summary>
public sealed class TimesheetLogEntry
{
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public string Action { get; set; } = "";
    public string EmployeeKey { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public DateOnly Date { get; set; }
    public TimesheetSnapshot? Before { get; set; }
    public TimesheetSnapshot? After { get; set; }
}

public sealed class TimesheetData
{
    public int Version { get; set; } = 1;
    public List<TimesheetMark> Marks { get; set; } = new();
    public List<TimesheetLogEntry> Log { get; set; } = new();
}

/// <summary>Результат сверки отметки дня со сменами.</summary>
public enum TimesheetCheck
{
    None,
    /// <summary>Отмечен «не был / больничный / отпуск / выходной», но в этот день была закрытая смена.</summary>
    AbsentButShift,
    /// <summary>Часы в отметке заметно (больше часа) отличаются от часов по сменам.</summary>
    HoursDiffer,
}

/// <summary>Итоги по сотруднику за период.</summary>
public sealed class TimesheetTotals
{
    public int PresentDays { get; init; }
    public double PresentHours { get; init; }
    public int AbsentDays { get; init; }
    public int SickDays { get; init; }
    public int VacationDays { get; init; }
    public int DayOffDays { get; init; }
}

/// <summary>2026-11-08: ручные и автоматические отметки сотрудников (табель отметок). У NurCRM серверного API посещаемости нет (Grep по коду и docs) —
/// хранение ЛОКАЛЬНОЕ: файл timesheet-marks.json в %LOCALAPPDATA%\NurMarketKassa (общий для кассы и программы владельца на одном ПК, запись — под межпроцессным
/// замком AtomicFile.Update; по компании — в реестре хранилищ как данные компании). Между разными компьютерами отметки НЕ синхронизируются.
/// Деньги и зарплату сервис не трогает.</summary>
public sealed class TimesheetMarksService
{
    public const string FileName = "timesheet-marks.json";
    private const int MaxLog = 3000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;

    public TimesheetMarksService(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Хранилище по умолчанию (путь через AppPaths).</summary>
    public static TimesheetMarksService Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public TimesheetData Load()
    {
        var text = AtomicFile.ReadAllText(_path);
        return Parse(text);
    }

    private static TimesheetData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TimesheetData();
        try
        {
            return JsonSerializer.Deserialize<TimesheetData>(text, Json) ?? new TimesheetData();
        }
        catch (JsonException)
        {
            return new TimesheetData();
        }
    }

    private TimesheetData Mutate(Func<TimesheetData, bool> change)
    {
        var result = new TimesheetData();
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            result = data;
            if (!change(data))
                return null;
            if (data.Log.Count > MaxLog)
                data.Log.RemoveRange(0, data.Log.Count - MaxLog);
            return JsonSerializer.Serialize(data, Json);
        });
        return result;
    }

    private static TimesheetSnapshot? Snap(TimesheetMark? m) =>
        m is null ? null : new TimesheetSnapshot { Status = m.Status, Hours = m.Hours, Comment = m.Comment, Auto = m.Auto };

    private static int IndexOf(TimesheetData d, string key, DateOnly date) =>
        d.Marks.FindIndex(m => m.Date == date && string.Equals(m.EmployeeKey, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ручная отметка владельца: ставит или заменяет отметку дня (автоотметку тоже — ручная имеет приоритет). Часы только у «Был», 0..24.</summary>
    public TimesheetMark SetManual(string employeeKey, string employeeName, DateOnly date, TimesheetStatus status, double? hours, string? comment, string by)
    {
        if (string.IsNullOrWhiteSpace(employeeKey))
            throw new ArgumentException("employeeKey", nameof(employeeKey));
        double? h = status == TimesheetStatus.Present && hours is { } v ? Math.Round(Math.Clamp(v, 0, 24), 2) : null;
        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        TimesheetMark? saved = null;
        Mutate(d =>
        {
            var i = IndexOf(d, employeeKey, date);
            var before = i >= 0 ? d.Marks[i] : null;
            var beforeSnap = Snap(before);
            var mark = new TimesheetMark
            {
                EmployeeKey = employeeKey, EmployeeName = employeeName, Date = date, Status = status, Hours = h, Comment = note,
                Auto = false, UpdatedAt = _now(), UpdatedBy = by ?? "",
            };
            // Ничего не изменилось (та же ручная отметка) — журнал не засоряем.
            if (before is { Auto: false } && before.Status == status && before.Hours == h && before.Comment == note)
            {
                saved = before;
                return false;
            }
            if (i >= 0) d.Marks[i] = mark; else d.Marks.Add(mark);
            d.Log.Add(new TimesheetLogEntry { At = mark.UpdatedAt, By = mark.UpdatedBy, Action = "set", EmployeeKey = employeeKey, EmployeeName = employeeName, Date = date, Before = beforeSnap, After = Snap(mark) });
            saved = mark;
            return true;
        });
        return saved!;
    }

    /// <summary>Снять отметку дня (с записью в журнал). false — отметки не было.</summary>
    public bool Clear(string employeeKey, DateOnly date, string by)
    {
        var removed = false;
        Mutate(d =>
        {
            var i = IndexOf(d, employeeKey, date);
            if (i < 0)
                return false;
            var old = d.Marks[i];
            d.Marks.RemoveAt(i);
            d.Log.Add(new TimesheetLogEntry { At = _now(), By = by ?? "", Action = "clear", EmployeeKey = old.EmployeeKey, EmployeeName = old.EmployeeName, Date = date, Before = Snap(old), After = null });
            removed = true;
            return true;
        });
        return removed;
    }

    /// <summary>Автоотметка прихода: «Был» на сегодня с временем прихода. Ничего не делает, если автоотметка выключена, нет сотрудника или на этот день УЖЕ есть любая
    /// отметка (ручная имеет приоритет, дубли не создаются). true — отметка создана.</summary>
    public bool AutoArrive(bool enabled, string? employeeKey, string? employeeName)
    {
        if (!enabled || string.IsNullOrWhiteSpace(employeeKey))
            return false;
        var now = _now();
        var date = DateOnly.FromDateTime(now.DateTime);
        var name = string.IsNullOrWhiteSpace(employeeName) ? employeeKey! : employeeName!;
        var created = false;
        Mutate(d =>
        {
            if (IndexOf(d, employeeKey!, date) >= 0)
                return false;
            var mark = new TimesheetMark
            {
                EmployeeKey = employeeKey!, EmployeeName = name, Date = date, Status = TimesheetStatus.Present, Hours = null, Auto = true,
                ArrivedAt = now, UpdatedAt = now, UpdatedBy = "",
            };
            d.Marks.Add(mark);
            d.Log.Add(new TimesheetLogEntry { At = now, By = "", Action = "auto-arrive", EmployeeKey = employeeKey!, EmployeeName = name, Date = date, After = Snap(mark) });
            created = true;
            return true;
        });
        return created;
    }

    /// <summary>Автоотметка ухода (закрытие смены): дописывает часы в последнюю автоотметку сотрудника, у которой ещё нет ухода и приход не старше суток.
    /// Ручные отметки не трогает. true — часы дописаны.</summary>
    public bool AutoLeave(bool enabled, string? employeeKey)
    {
        if (!enabled || string.IsNullOrWhiteSpace(employeeKey))
            return false;
        var now = _now();
        var done = false;
        Mutate(d =>
        {
            var mark = d.Marks
                .Where(m => m.Auto && m.Status == TimesheetStatus.Present && m.LeftAt is null && m.ArrivedAt is not null
                            && string.Equals(m.EmployeeKey, employeeKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.ArrivedAt)
                .FirstOrDefault();
            if (mark is null)
                return false;
            var span = now - mark.ArrivedAt!.Value;
            if (span > TimeSpan.FromHours(24))
                return false; // приход давний (забыли закрыть) — часы недостоверны, оставляем владельцу
            var before = Snap(mark);
            mark.LeftAt = now;
            mark.Hours = Math.Round((mark.Hours ?? 0) + Math.Max(0, span.TotalHours), 2);
            mark.UpdatedAt = now;
            d.Log.Add(new TimesheetLogEntry { At = now, By = "", Action = "auto-leave", EmployeeKey = mark.EmployeeKey, EmployeeName = mark.EmployeeName, Date = mark.Date, Before = before, After = Snap(mark) });
            done = true;
            return true;
        });
        return done;
    }

    // ───────────────────────── чистые расчёты (без файла) ─────────────────────────

    /// <summary>Закрытая смена принадлежит сотруднику: совпал id (если у смены он есть) или имя.</summary>
    public static bool ShiftBelongsTo(ShiftHistoryEntry shift, string employeeKey, string employeeName)
    {
        if (!string.IsNullOrWhiteSpace(shift.CashierId) && string.Equals(employeeKey, shift.CashierId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (employeeKey.StartsWith("n:", StringComparison.Ordinal) && string.Equals(employeeKey[2..], shift.Cashier, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(employeeName) && string.Equals(employeeName.Trim(), shift.Cashier?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Часы по закрытым сменам сотрудника по дням открытия смены (тот же день, что в <see cref="StaffTimesheetService"/>).</summary>
    public static Dictionary<DateOnly, double> ShiftHoursByDay(IEnumerable<ShiftHistoryEntry> shifts, string employeeKey, string employeeName)
    {
        var map = new Dictionary<DateOnly, double>();
        foreach (var s in shifts)
        {
            if (s.OpenedAt is not { } o || s.ClosedAt is not { } c || c <= o || !ShiftBelongsTo(s, employeeKey, employeeName))
                continue;
            var day = DateOnly.FromDateTime(o);
            map[day] = map.GetValueOrDefault(day) + (c - o).TotalHours;
        }
        return map;
    }

    /// <summary>Часы по умолчанию при ручной отметке «Был»: по сменам этого дня (до 0,25 ч), иначе 8 ч.</summary>
    public static double SuggestHours(double shiftHours) =>
        shiftHours > 0 ? Math.Min(24, Math.Round(shiftHours * 4, MidpointRounding.AwayFromZero) / 4) : 8;

    /// <summary>Сверка отметки дня со сменами (<paramref name="shiftHours"/> — часы закрытых смен этого дня, 0 — смен не было).</summary>
    public static TimesheetCheck Reconcile(TimesheetMark? mark, double shiftHours)
    {
        if (mark is null || shiftHours <= 0)
            return TimesheetCheck.None;
        if (mark.Status != TimesheetStatus.Present)
            return TimesheetCheck.AbsentButShift;
        if (mark.Hours is { } h && Math.Abs(h - shiftHours) > 1.0)
            return TimesheetCheck.HoursDiffer;
        return TimesheetCheck.None;
    }

    /// <summary>Часы отметки «Был» в итогах: заданные, иначе по сменам дня, иначе 0.</summary>
    public static double EffectiveHours(TimesheetMark mark, double shiftHours) =>
        mark.Status != TimesheetStatus.Present ? 0 : mark.Hours ?? Math.Round(shiftHours, 2);

    /// <summary>Итоги по отметкам сотрудника за период (даты включительно).</summary>
    public static TimesheetTotals Summarize(IEnumerable<TimesheetMark> marks, DateOnly from, DateOnly to, IReadOnlyDictionary<DateOnly, double>? shiftHours = null)
    {
        int present = 0, absent = 0, sick = 0, vac = 0, off = 0;
        double hours = 0;
        foreach (var m in marks.Where(m => m.Date >= from && m.Date <= to))
        {
            switch (m.Status)
            {
                case TimesheetStatus.Present:
                    present++;
                    hours += EffectiveHours(m, shiftHours?.GetValueOrDefault(m.Date) ?? 0);
                    break;
                case TimesheetStatus.Absent: absent++; break;
                case TimesheetStatus.Sick: sick++; break;
                case TimesheetStatus.Vacation: vac++; break;
                case TimesheetStatus.DayOff: off++; break;
            }
        }
        return new TimesheetTotals { PresentDays = present, PresentHours = Math.Round(hours, 2), AbsentDays = absent, SickDays = sick, VacationDays = vac, DayOffDays = off };
    }

    /// <summary>Сотрудники для сетки: из списка сервера, из смен и из отметок; одинаковые id или имена сливаются (id сервера важнее).</summary>
    public static List<(string Key, string Name)> MergeEmployees(IEnumerable<(string Key, string Name)> fromServer, IEnumerable<ShiftHistoryEntry> shifts, IEnumerable<TimesheetMark> marks)
    {
        var result = new List<(string Key, string Name)>();
        void Add(string key, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "—")
                name = key;
            var i = result.FindIndex(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(r.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                result.Add((key, name));
            else if (result[i].Key.StartsWith("n:", StringComparison.Ordinal) && !key.StartsWith("n:", StringComparison.Ordinal))
                result[i] = (key, name); // id сервера вместо «n:Имя»
        }
        foreach (var e in fromServer)
            Add(e.Key, e.Name);
        foreach (var s in shifts)
        {
            if (s.OpenedAt is null || s.ClosedAt is null)
                continue;
            Add(!string.IsNullOrWhiteSpace(s.CashierId) ? s.CashierId : "n:" + s.Cashier, s.Cashier);
        }
        foreach (var m in marks)
            Add(m.EmployeeKey, m.EmployeeName);
        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ───────────────────────── тексты ─────────────────────────

    public static string StatusName(TimesheetStatus s) => s switch
    {
        TimesheetStatus.Present => Tr.T("Был", "Келди", "Present", "Geldi", "Keldi"),
        TimesheetStatus.Absent => Tr.T("Не был", "Келген жок", "Absent", "Gelmedi", "Kelmadi"),
        TimesheetStatus.Sick => Tr.T("Больничный", "Ооруп калган", "Sick leave", "Hastalık izni", "Kasallik ta'tili"),
        TimesheetStatus.Vacation => Tr.T("Отпуск", "Өргүү", "Vacation", "İzin", "Ta'til"),
        _ => Tr.T("Выходной", "Дем алыш", "Day off", "Hafta tatili", "Dam olish"),
    };

    /// <summary>Короткий код статуса для узких клеток сетки и Excel (рядом всегда есть подпись-легенда).</summary>
    public static string StatusCode(TimesheetStatus s) => s switch
    {
        TimesheetStatus.Present => Tr.T("Б", "К", "P", "G", "K"),
        TimesheetStatus.Absent => Tr.T("Н", "Ж", "A", "Y", "Y"),
        TimesheetStatus.Sick => Tr.T("Бол", "Оор", "S", "H", "Kas"),
        TimesheetStatus.Vacation => Tr.T("От", "Өр", "V", "İz", "Ta"),
        _ => Tr.T("Вых", "Дем", "D", "Tt", "Dam"),
    };

    public static string Legend() => string.Join(", ", Enum.GetValues<TimesheetStatus>().Select(s => $"{StatusCode(s)} — {StatusName(s)}"));

    /// <summary>Часы текстом: «8 ч», «7,5 ч».</summary>
    public static string HoursText(double h) =>
        $"{h.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)} {Tr.T("ч", "саат", "h", "sa", "soat")}";

    /// <summary>Таблица для Excel: строка на сотрудника, колонки — дни месяца (код статуса или часы), затем итоги. Внизу — легенда.</summary>
    public static SectionTable BuildExportTable(int year, int month, IReadOnlyList<(string Key, string Name)> employees, IEnumerable<TimesheetMark> marks, IEnumerable<ShiftHistoryEntry> shifts)
    {
        var days = DateTime.DaysInMonth(year, month);
        var from = new DateOnly(year, month, 1);
        var to = new DateOnly(year, month, days);
        var markList = marks.ToList();
        var shiftList = shifts.ToList();
        var cols = new List<XlColumn> { new(Tr.T("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim")) };
        for (var d = 1; d <= days; d++)
            cols.Add(new XlColumn(d.ToString(System.Globalization.CultureInfo.InvariantCulture), XlKind.Text, XlTotal.None, 8));
        cols.Add(new XlColumn(Tr.T("Дней", "Күн", "Days", "Gün", "Kun"), XlKind.Integer));
        cols.Add(new XlColumn(Tr.T("Часов", "Саат", "Hours", "Saat", "Soat"), XlKind.Decimal2));
        cols.Add(new XlColumn(Tr.T("Больничных", "Оору күндөрү", "Sick days", "Hastalık günü", "Kasallik kunlari"), XlKind.Integer));
        cols.Add(new XlColumn(Tr.T("Отпуск", "Өргүү", "Vacation", "İzin", "Ta'til"), XlKind.Integer));
        cols.Add(new XlColumn(Tr.T("Выходных", "Дем алыш", "Days off", "Tatil günü", "Dam olish"), XlKind.Integer));
        cols.Add(new XlColumn(Tr.T("Не был", "Келген жок", "Absent", "Gelmedi", "Kelmadi"), XlKind.Integer));

        var rows = new List<object?[]>();
        foreach (var (key, name) in employees)
        {
            var mine = markList.Where(m => string.Equals(m.EmployeeKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
            var byShift = ShiftHoursByDay(shiftList, key, name);
            if (mine.Count(m => m.Date >= from && m.Date <= to) == 0 && !byShift.Keys.Any(k => k >= from && k <= to))
                continue; // за месяц нет ни отметок, ни смен — строку не печатаем
            var row = new object?[cols.Count];
            row[0] = name;
            for (var d = 1; d <= days; d++)
            {
                var date = new DateOnly(year, month, d);
                var m = mine.FirstOrDefault(x => x.Date == date);
                row[d] = m is null ? null
                    : m.Status == TimesheetStatus.Present
                        ? (EffectiveHours(m, byShift.GetValueOrDefault(date)) is var h && h > 0 ? h.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : StatusCode(m.Status))
                        : StatusCode(m.Status);
            }
            var t = Summarize(mine, from, to, byShift);
            row[days + 1] = t.PresentDays;
            row[days + 2] = (decimal)t.PresentHours;
            row[days + 3] = t.SickDays;
            row[days + 4] = t.VacationDays;
            row[days + 5] = t.DayOffDays;
            row[days + 6] = t.AbsentDays;
            rows.Add(row);
        }
        // Пустая строка и легенда: цифра в клетке — часы «Был», остальное — коды.
        var legend = new object?[cols.Count];
        legend[0] = Tr.T("Обозначения: число — часы (Был), ", "Белгилер: сан — саат (Келди), ", "Legend: a number is hours (Present), ", "Açıklama: sayı — saat (Geldi), ", "Belgilar: son — soat (Keldi), ") + Legend();
        rows.Add(new object?[cols.Count]);
        rows.Add(legend);
        return new SectionTable(Tr.T("Табель отметок", "Белгилер табели", "Attendance sheet", "Devam çizelgesi", "Davomat tabeli"), $"{month:00}.{year}", cols, rows);
    }
}
