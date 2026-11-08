using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08 (запрос владельца «вернуть табель»): окно табеля в Avalonia.Headless на заглушках данных (сервер не нужен, файл отметок — во временной папке).
/// Проверяется: сетка сотрудники × дни месяца, ручная отметка и снятие, сверка со сменой («отмечен как не был, но была смена»), кассир видит только свою строку без правки,
/// текст не обрезан на 1024×600 и 1366×768 (ru, ky). Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\timesheet-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class StaffTimesheetWindowTests
{
    private static readonly DateTime Today = new(2026, 11, 8);

    private static ShiftHistoryEntry Shift(string id, string name, DateTime open, DateTime close) =>
        new() { ShiftNumber = Guid.NewGuid().ToString("N"), CashierId = id, Cashier = name, OpenedAt = open, ClosedAt = close };

    private static (TimesheetDeps Deps, string Dir) MakeDeps(bool? canEdit = true, string? me = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-ui-timesheet-" + Guid.NewGuid().ToString("N")[..8]);
        IReadOnlyList<ShiftHistoryEntry> shifts =
        [
            Shift("u1", "Айгуль Сыдыкова", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 17, 0, 0)),
            Shift("u2", "Бекзат Жумабеков", new DateTime(2026, 11, 6, 10, 0, 0), new DateTime(2026, 11, 6, 15, 30, 0)),
        ];
        IReadOnlyList<(string Key, string Name)> staff = [("u1", "Айгуль Сыдыкова"), ("u2", "Бекзат Жумабеков"), ("u3", "Чолпонай Эсенгулова")];
        var deps = new TimesheetDeps
        {
            Shifts = _ => Task.FromResult(shifts),
            Employees = _ => Task.FromResult(staff),
            Marks = new TimesheetMarksService(Path.Combine(dir, "timesheet-marks.json")),
            Today = () => Today,
            CanEdit = canEdit,
            CurrentUserKey = me,
            CurrentUserName = me == "u1" ? "Айгуль Сыдыкова" : null,
        };
        return (deps, dir);
    }

    private static List<Button> Cells(StaffTimesheetWindow w) =>
        w.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is ValueTuple<string, string, DateOnly>).ToList();

    private static Button CellOf(StaffTimesheetWindow w, string key, DateOnly date) =>
        Cells(w).Single(c => c.Tag is ValueTuple<string, string, DateOnly> t && t.Item1 == key && t.Item3 == date);

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Grid_ShowsEmployeesAndDays_MarksWork_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, dir) = MakeDeps();
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            Cells(window).Should().HaveCount(3 * 30, $"3 сотрудника × 30 дней ноября ({tag})");

            // Ручная отметка «Был» — часы совпали со сменой, предупреждения нет
            var aigul = new DateOnly(2026, 11, 5);
            window.ApplyMark("u1", "Айгуль Сыдыкова", aigul, TimesheetStatus.Present, 8, null);
            UiShot.Settle(4);
            var cell = CellOf(window, "u1", aigul);
            Avalonia.Automation.AutomationProperties.GetName(cell).Should().NotContain("Внимание").And.NotContain("Көңүл", "часы совпали со сменой");

            // Отмечен «Не был», но была смена — предупреждение в клетке (текстом, не только цветом)
            window.ApplyMark("u1", "Айгуль Сыдыкова", aigul, TimesheetStatus.Absent, null, null);
            UiShot.Settle(4);
            cell = CellOf(window, "u1", aigul);
            (Avalonia.Automation.AutomationProperties.GetName(cell) ?? "").Should().Match(lang == AppLanguage.Kyrgyz ? "*Көңүл буруңуз*" : "*Внимание*", "расхождение со сменой");
            cell.GetVisualDescendants().OfType<TextBlock>().First().Text.Should().EndWith("!");

            // Быстрые действия
            window.MarkAllToday().Should().Be(3);
            window.MarkAllToday().Should().Be(0, "повтор ничего не затирает");
            deps.Marks.Load().Log.Should().NotBeEmpty();

            window.ClearMark("u1", aigul);
            deps.Marks.Load().Marks.Should().NotContain(m => m.Date == aigul);

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "staff-timesheet", langCode);
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var outDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"timesheet-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Cashier_SeesOnlyOwnRow_AndCannotEdit()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, dir) = MakeDeps(canEdit: false, me: "u1");
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadAsync();
            UiShot.Settle(6);
            Cells(window).Select(c => ((ValueTuple<string, string, DateOnly>)c.Tag!).Item1).Distinct().Should().Equal("u1");
            window.MarkAllToday().Should().Be(0);
            window.ApplyMark("u1", "Айгуль Сыдыкова", new DateOnly(2026, 11, 5), TimesheetStatus.Present, 8, null);
            deps.Marks.Load().Marks.Should().BeEmpty("кассир отметки не ставит");
            UiDrive.Find<Button>(window, "JournalButton").IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Month_Switch_And_Tabs_Work_And_ExportHasRows()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, dir) = MakeDeps();
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadAsync();
            UiShot.Settle(6);
            window.ApplyMark("u2", "Бекзат Жумабеков", new DateOnly(2026, 11, 6), TimesheetStatus.Present, 5.5, "утро");
            window.BuildExportTable().Should().NotBeNull();
            window.BuildExportTable()!.Rows.Should().Contain(r => (string?)r[0] == "Бекзат Жумабеков");

            UiDrive.Click(window, UiDrive.Find<Button>(window, "PrevMonthButton"));
            UiShot.Settle(4);
            Cells(window).Should().HaveCount(3 * 31, "октябрь — 31 день");
            window.ShownMonthStartForTest.Month.Should().Be(10);

            // вкладка «Итоги по сменам» — прежняя таблица
            var tab = UiDrive.Find<RadioButton>(window, "ShiftsTabRadio");
            UiDrive.Click(window, tab);
            UiShot.Settle(4);
            UiDrive.Find<Border>(window, "ShiftsPanel").IsVisible.Should().BeTrue();
            UiDrive.Find<Grid>(window, "MarksPanel").IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
