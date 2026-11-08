using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Staff;

/// <summary>2026-11-08: табель отметок — приоритет ручной над автоотметкой, дубли, смена дня и часового пояса, журнал, сверка со сменами, итоги, экспорт.
/// Файл отметок — во временной папке теста (настоящие данные кассы не трогаются).</summary>
public sealed class TimesheetMarksServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-timesheet-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(6));

    private string FilePath => Path.Combine(_dir, "timesheet-marks.json");
    private TimesheetMarksService Svc() => new(FilePath, () => _now);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка — не критично */ }
    }

    private static readonly DateOnly D = new(2026, 11, 8);

    [Fact]
    public void AutoMarkSetting_IsOffByDefault_AndWritesNothing()
    {
        new UserPreferences().StaffAutoMarkMode.Should().BeNull("по умолчанию автоотметка выключена");
        var svc = Svc();
        svc.AutoArrive(enabled: false, "u1", "Айгуль").Should().BeFalse();
        svc.AutoLeave(enabled: false, "u1").Should().BeFalse();
        File.Exists(FilePath).Should().BeFalse("выключено — ничего не пишем, даже пустой файл");
    }

    [Fact]
    public void AutoArrive_CreatesPresentMark_AndDoesNotDuplicate()
    {
        var svc = Svc();
        svc.AutoArrive(true, "u1", "Айгуль").Should().BeTrue();
        svc.AutoArrive(true, "u1", "Айгуль").Should().BeFalse("на этот день отметка уже есть");
        var data = svc.Load();
        var m = data.Marks.Should().ContainSingle().Subject;
        m.Status.Should().Be(TimesheetStatus.Present);
        m.Auto.Should().BeTrue();
        m.Date.Should().Be(D);
        m.ArrivedAt.Should().Be(_now);
        m.Hours.Should().BeNull();
        data.Log.Should().ContainSingle().Which.Action.Should().Be("auto-arrive");
    }

    [Fact]
    public void ManualMark_HasPriority_OverAuto_BothWays()
    {
        var svc = Svc();
        svc.SetManual("u1", "Айгуль", D, TimesheetStatus.Sick, null, "температура", "Владелец");
        svc.AutoArrive(true, "u1", "Айгуль").Should().BeFalse("ручная отметка не перезаписывается автоотметкой");
        svc.Load().Marks.Single().Status.Should().Be(TimesheetStatus.Sick);

        var svc2 = Svc();
        svc2.Clear("u1", D, "Владелец").Should().BeTrue();
        svc2.AutoArrive(true, "u1", "Айгуль").Should().BeTrue();
        _now = _now.AddHours(1);
        // владелец правит автоотметку — становится ручной, закрытие смены её больше не меняет
        svc2.SetManual("u1", "Айгуль", D, TimesheetStatus.Present, 6, null, "Владелец");
        svc2.Load().Marks.Single().Auto.Should().BeFalse();
        _now = _now.AddHours(8);
        svc2.AutoLeave(true, "u1").Should().BeFalse();
        svc2.Load().Marks.Single().Hours.Should().Be(6);
    }

    [Fact]
    public void AutoLeave_AddsHours_Once_AndIgnoresStaleArrival()
    {
        var svc = Svc();
        svc.AutoLeave(true, "u1").Should().BeFalse("прихода нет — нечего дописывать");
        svc.AutoArrive(true, "u1", "Айгуль");
        _now = _now.AddHours(8).AddMinutes(30);
        svc.AutoLeave(true, "u1").Should().BeTrue();
        var m = svc.Load().Marks.Single();
        m.Hours.Should().Be(8.5);
        m.LeftAt.Should().Be(_now);
        svc.AutoLeave(true, "u1").Should().BeFalse("уход уже записан");

        // давний приход (забыли закрыть смену) — часы недостоверны, не трогаем
        _now = _now.AddDays(3);
        svc.AutoArrive(true, "u2", "Бекзат");
        _now = _now.AddHours(30);
        svc.AutoLeave(true, "u2").Should().BeFalse();
        svc.Load().Marks.Single(x => x.EmployeeKey == "u2").Hours.Should().BeNull();
    }

    [Fact]
    public void Midnight_And_TimeZone_Change_DoNotBreakHours()
    {
        // пришёл в 23:40 по +06:00 — день отметки по его местному времени; ушёл в 00:20 следующего дня — часы 0,67, день прежний
        _now = new DateTimeOffset(2026, 11, 8, 23, 40, 0, TimeSpan.FromHours(6));
        var svc = Svc();
        svc.AutoArrive(true, "u1", "Айгуль");
        _now = new DateTimeOffset(2026, 11, 9, 0, 20, 0, TimeSpan.FromHours(6));
        svc.AutoLeave(true, "u1");
        var m = svc.Load().Marks.Single();
        m.Date.Should().Be(new DateOnly(2026, 11, 8));
        m.Hours.Should().Be(0.67);

        // смена часового пояса между приходом и уходом: считаем по моментам времени, а не по «настенным» часам
        // 10:00+06 = 04:00 UTC; 08:00+02 = 06:00 UTC => 2 ч
        var svc2 = new TimesheetMarksService(Path.Combine(_dir, "tz.json"), () => _now);
        _now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(6));
        svc2.AutoArrive(true, "u9", "Гульнара");
        _now = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.FromHours(2));
        svc2.AutoLeave(true, "u9");
        svc2.Load().Marks.Single().Hours.Should().Be(2);
    }

    [Fact]
    public void Journal_RecordsWhoWhenAndBeforeAfter_NoNoiseOnRepeat()
    {
        var svc = Svc();
        svc.SetManual("u1", "Айгуль", D, TimesheetStatus.Present, 8, null, "Владелец");
        svc.SetManual("u1", "Айгуль", D, TimesheetStatus.Present, 8, null, "Владелец"); // то же самое
        _now = _now.AddMinutes(5);
        svc.SetManual("u1", "Айгуль", D, TimesheetStatus.Absent, 7, "прогул", "Админ"); // часы у «Не был» не хранятся
        svc.Clear("u1", D, "Админ");
        var log = svc.Load().Log;
        log.Should().HaveCount(3);
        log[0].Before.Should().BeNull();
        log[0].After!.Status.Should().Be(TimesheetStatus.Present);
        log[1].By.Should().Be("Админ");
        log[1].Before!.Hours.Should().Be(8);
        log[1].After!.Status.Should().Be(TimesheetStatus.Absent);
        log[1].After!.Hours.Should().BeNull();
        log[1].At.Should().Be(_now);
        log[2].Action.Should().Be("clear");
        svc.Load().Marks.Should().BeEmpty();
        svc.Clear("u1", D, "Админ").Should().BeFalse();
    }

    [Fact]
    public void SetManual_ClampsHours_AndRequiresEmployee()
    {
        var svc = Svc();
        svc.SetManual("u1", "А", D, TimesheetStatus.Present, 99, null, "в").Hours.Should().Be(24);
        svc.SetManual("u1", "А", D, TimesheetStatus.Present, -3, null, "в").Hours.Should().Be(0);
        var act = () => svc.SetManual(" ", "А", D, TimesheetStatus.Present, 1, null, "в");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuggestHours_FromShifts_Or_Eight()
    {
        TimesheetMarksService.SuggestHours(0).Should().Be(8);
        TimesheetMarksService.SuggestHours(7.9).Should().Be(8);
        TimesheetMarksService.SuggestHours(6.1).Should().Be(6);
        TimesheetMarksService.SuggestHours(30).Should().Be(24);
    }

    [Fact]
    public void Reconcile_FlagsAbsentWithShift_AndBigHoursGap()
    {
        TimesheetMark M(TimesheetStatus s, double? h = null) => new() { Status = s, Hours = h };
        TimesheetMarksService.Reconcile(null, 8).Should().Be(TimesheetCheck.None);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Absent), 0).Should().Be(TimesheetCheck.None);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Absent), 8).Should().Be(TimesheetCheck.AbsentButShift);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Vacation), 3).Should().Be(TimesheetCheck.AbsentButShift);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Present, 8), 8.5).Should().Be(TimesheetCheck.None);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Present, 8), 4).Should().Be(TimesheetCheck.HoursDiffer);
        TimesheetMarksService.Reconcile(M(TimesheetStatus.Present, null), 4).Should().Be(TimesheetCheck.None);
    }

    private static ShiftHistoryEntry Shift(string id, string name, DateTime open, DateTime? close) => new() { ShiftNumber = Guid.NewGuid().ToString("N"), CashierId = id, Cashier = name, OpenedAt = open, ClosedAt = close };

    [Fact]
    public void ShiftHours_ByDay_MatchById_Or_Name_IgnoreOpenShifts()
    {
        var shifts = new[]
        {
            Shift("u1", "Айгуль", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 13, 0, 0)),
            Shift("u1", "Айгуль", new DateTime(2026, 11, 5, 14, 0, 0), new DateTime(2026, 11, 5, 18, 0, 0)),
            Shift("", "Айгуль", new DateTime(2026, 11, 6, 9, 0, 0), new DateTime(2026, 11, 6, 10, 0, 0)),
            Shift("u1", "Айгуль", new DateTime(2026, 11, 7, 9, 0, 0), null),
            Shift("u2", "Бекзат", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 20, 0, 0)),
        };
        var map = TimesheetMarksService.ShiftHoursByDay(shifts, "u1", "Айгуль");
        map[new DateOnly(2026, 11, 5)].Should().Be(8);
        map[new DateOnly(2026, 11, 6)].Should().Be(1, "смена без id сопоставилась по имени");
        map.Should().NotContainKey(new DateOnly(2026, 11, 7));
    }

    [Fact]
    public void Summarize_CountsStatuses_AndUsesShiftHoursForAutoWithoutHours()
    {
        var marks = new[]
        {
            new TimesheetMark { Date = new DateOnly(2026, 11, 1), Status = TimesheetStatus.Present, Hours = 8 },
            new TimesheetMark { Date = new DateOnly(2026, 11, 2), Status = TimesheetStatus.Present, Hours = null, Auto = true },
            new TimesheetMark { Date = new DateOnly(2026, 11, 3), Status = TimesheetStatus.Sick },
            new TimesheetMark { Date = new DateOnly(2026, 11, 4), Status = TimesheetStatus.Vacation },
            new TimesheetMark { Date = new DateOnly(2026, 11, 5), Status = TimesheetStatus.DayOff },
            new TimesheetMark { Date = new DateOnly(2026, 11, 6), Status = TimesheetStatus.Absent },
            new TimesheetMark { Date = new DateOnly(2026, 12, 1), Status = TimesheetStatus.Present, Hours = 8 }, // другой месяц
        };
        var t = TimesheetMarksService.Summarize(marks, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            new Dictionary<DateOnly, double> { [new DateOnly(2026, 11, 2)] = 5.5 });
        t.PresentDays.Should().Be(2);
        t.PresentHours.Should().Be(13.5);
        (t.SickDays, t.VacationDays, t.DayOffDays, t.AbsentDays).Should().Be((1, 1, 1, 1));
    }

    [Fact]
    public void MergeEmployees_JoinsServerShiftsAndMarks_WithoutDuplicates()
    {
        var shifts = new[] { Shift("", "Бекзат", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 10, 0, 0)), Shift("u1", "Айгуль", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 10, 0, 0)) };
        var marks = new[] { new TimesheetMark { EmployeeKey = "n:Бекзат", EmployeeName = "Бекзат", Date = D }, new TimesheetMark { EmployeeKey = "u3", EmployeeName = "Чолпон", Date = D } };
        var list = TimesheetMarksService.MergeEmployees([("u1", "Айгуль"), ("u2", "Бекзат")], shifts, marks);
        list.Select(x => x.Name).Should().Equal("Айгуль", "Бекзат", "Чолпон");
        list.Single(x => x.Name == "Бекзат").Key.Should().Be("u2", "id сервера важнее n:Имя");
    }

    [Fact]
    public void Export_HasDaysStatusesAndTotals()
    {
        var svc = Svc();
        svc.SetManual("u1", "Айгуль", new DateOnly(2026, 11, 2), TimesheetStatus.Present, 8, null, "в");
        svc.SetManual("u1", "Айгуль", new DateOnly(2026, 11, 3), TimesheetStatus.Sick, null, null, "в");
        svc.SetManual("u1", "Айгуль", new DateOnly(2026, 11, 4), TimesheetStatus.Vacation, null, null, "в");
        var table = TimesheetMarksService.BuildExportTable(2026, 11, [("u1", "Айгуль"), ("u2", "Пустой")], svc.Load().Marks, []);
        table.Columns.Should().HaveCount(1 + 30 + 6);
        var row = table.Rows[0];
        row[0].Should().Be("Айгуль");
        row[2].Should().Be("8");
        row[3].Should().Be(TimesheetMarksService.StatusCode(TimesheetStatus.Sick));
        row[4].Should().Be(TimesheetMarksService.StatusCode(TimesheetStatus.Vacation));
        row[31].Should().Be(1);
        row[32].Should().Be(8m);
        row[33].Should().Be(1);
        table.Rows.Count(r => (string?)r[0] == "Пустой").Should().Be(0, "сотрудник без отметок и смен за месяц не печатается");
        ((string)table.Rows[^1][0]!).Should().Contain(TimesheetMarksService.StatusName(TimesheetStatus.Sick), "легенда внизу");
        table.Period.Should().Be("11.2026");
    }

    [Fact]
    public void ParallelWriters_DoNotLoseMarks()
    {
        Parallel.For(0, 12, i => Svc().SetManual("u" + i, "Имя" + i, D, TimesheetStatus.Present, 8, null, "в"));
        Svc().Load().Marks.Should().HaveCount(12);
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty_NotCrash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ не json");
        Svc().Load().Marks.Should().BeEmpty();
    }
}
