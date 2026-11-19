using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
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

    /// <summary>2026-11-19 (живой обход владельца): смены не возвращаются никогда — окно сразу показывает сетку и подпись месяца, по лимиту — ошибка и «Повторить», без вечной «Загрузки…».</summary>
    [AvaloniaFact]
    public async Task HungShifts_ShowsGridAtOnce_ThenErrorAndRetry()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (basic, dir) = MakeDeps();
        var tcs = new TaskCompletionSource<IReadOnlyList<ShiftHistoryEntry>>();
        var calls = 0;
        var deps = new TimesheetDeps
        {
            Shifts = _ => { calls++; return tcs.Task; },
            Employees = basic.Employees,
            Marks = basic.Marks,
            Today = basic.Today,
            CanEdit = true,
            LoadTimeout = TimeSpan.FromSeconds(1.5),
        };
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var load = window.LoadAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(1) && Cells(window).Count == 0)
            {
                UiShot.Settle(1);
                await Task.Delay(30);
            }
            Cells(window).Should().HaveCount(3 * 30, "сетка и сотрудники видны сразу, пока смены не пришли");
            UiDrive.Find<TextBlock>(window, "MonthText").Text.Should().NotBeNullOrWhiteSpace();
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeFalse();

            await load.WaitAsync(TimeSpan.FromSeconds(10));
            UiShot.Settle(4);
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeTrue("по лимиту — «Повторить»");
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().Contain("Не удалось загрузить смены").And.NotContain("Загрузка");
            calls.Should().BeGreaterThan(0, "Loaded мог запустить загрузку ещё раз — важно лишь, что смены запрашивались");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>2026-11-19: боевой режим — смены грузятся «за месяц»: окно просит загрузчик с начала показанного месяца, пишет «Смены за … загружены», переключение ‹ › просит октябрь;
    /// на ошибке второй страницы (пришло неполное) — строка с причиной и «Повторить», уже пришедшее остаётся в сетке.</summary>
    [AvaloniaFact]
    public async Task MonthMode_AsksFromMonthStart_ShowsLoaded_SwitchAsksPreviousMonth_ErrorKeepsPartial()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (basic, dir) = MakeDeps();
        var asked = new List<(DateTime Since, bool Force)>();
        var fail = false;
        var shifts = new List<ShiftHistoryEntry>
        {
            Shift("u1", "Айгуль Сыдыкова", new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 5, 17, 0, 0)),
        };
        var deps = new TimesheetDeps
        {
            MonthShifts = (since, force, progress, ct) =>
            {
                asked.Add((since, force));
                return Task.FromResult(new ShiftMonthResult { Entries = shifts, Failed = fail, Covered = !fail });
            },
            Employees = basic.Employees,
            Marks = basic.Marks,
            Today = basic.Today,
            CanEdit = true,
        };
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadAsync();
            UiShot.Settle(4);
            asked.Should().NotBeEmpty();
            asked.All(a => a.Since == new DateTime(2026, 11, 1)).Should().BeTrue("ноябрь: смены нужны с 1 ноября");
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().Contain("Смены за").And.Contain("загружены");
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeFalse();

            UiDrive.Click(window, UiDrive.Find<Button>(window, "PrevMonthButton"));
            for (var i = 0; i < 40 && !asked.Any(a => a.Since == new DateTime(2026, 10, 1)); i++)
            {
                UiShot.Settle(1);
                await Task.Delay(25);
            }
            asked.Should().Contain(a => a.Since == new DateTime(2026, 10, 1), "переключили месяц — смены просят с 1 октября");

            fail = true;
            await window.LoadAsync(true);
            UiShot.Settle(4);
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeTrue("неполная загрузка — «Повторить»");
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().Contain("Не удалось загрузить смены");
            asked.Last().Force.Should().BeTrue("«Обновить» — заново с сервера");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void Stub(WireMock.Server.WireMockServer server, string method, string path, int status, string body) =>
        server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    /// <summary>2026-11-19 (ответ Умара: shifts_month_filter и main/staff/timesheet/): окно на заглушке СЕРВЕРА (WireMock) с боевыми ShiftApiService и ShiftMonthLoader. Сетка видна сразу; смены приходят ОДНИМ запросом
    /// ?month=2026-11&amp;page_size=100 (без перебора истории); готовый табель сервера добавляет часы дню, где смен нет (Чолпонай, 3 ноября) — отметка «не был» даёт предупреждение; смена Айгуль (5 ноября)
    /// остаётся по сменам. Сервер ответил ошибкой на месяц и на табель (400 period_invalid) — «Не удалось загрузить смены…» и «Повторить», сетка на месте.</summary>
    [AvaloniaFact]
    public async Task ServerStub_MonthRequest_And_ServerTimesheet_GridAtOnce_DataArrives_ErrorShowsRetry()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (basic, dir) = MakeDeps();
        using var server = WireMock.Server.WireMockServer.Start();
        Stub(server, "POST", "/api/users/auth/login/", 200, "{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        var shiftApi = new ShiftApiService(api);
        Stub(server, "GET", "/api/construction/shifts/", 200,
            "{\"count\":1,\"next\":null,\"results\":[{\"id\":\"s1\",\"cashier\":\"u1\",\"cashier_display\":\"Айгуль Сыдыкова\",\"opened_at\":\"2026-11-05T09:00:00\",\"closed_at\":\"2026-11-05T17:00:00\",\"sales_total\":\"1000\"}]}");
        Stub(server, "GET", "/api/main/staff/timesheet/", 200,
            "{\"employees\":[{\"employee\":{\"id\":\"u3\",\"full_name\":\"Чолпонай Эсенгулова\"},\"days\":[{\"date\":\"2026-11-03\",\"worked_minutes\":360,\"late_minutes\":5}]}]}");

        var loader = new ShiftMonthLoader(
            (_, _) => Task.FromResult<IReadOnlyList<ShiftHistoryEntry>?>(null),
            fetchMonthPage: async (y, m, p, ct) => await shiftApi.ConstructionShiftsMonthPageAsync(y, m, p, ct) is { } r ? (ShiftHistoryService.Parse(r.Rows), r.HasNext) : null,
            monthFilterAvailable: _ => Task.FromResult(true));
        var deps = new TimesheetDeps
        {
            MonthShifts = (since, force, progress, ct) => loader.LoadAsync(since, force, progress, ct),
            Timesheet = async (y, m, ct) => await shiftApi.StaffTimesheetAsync(y, m, ct) is { } raw ? ServerTimesheet.Parse(raw, y, m) : null,
            Employees = basic.Employees,
            Marks = basic.Marks,
            Today = basic.Today,
            CanEdit = true,
        };
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var load = window.LoadAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(1) && Cells(window).Count == 0)
            {
                UiShot.Settle(1);
                await Task.Delay(30);
            }
            Cells(window).Should().HaveCount(3 * 30, "сетка видна сразу, до ответа сервера");
            await load.WaitAsync(TimeSpan.FromSeconds(20));
            UiShot.Settle(4);
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeFalse();
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().Contain("Смены за").And.Contain("загружены");
            var shiftCalls = server.LogEntries.Where(e => e.RequestMessage.Path == "/api/construction/shifts/").ToList();
            shiftCalls.Should().HaveCount(1, "месяц — один запрос, а не перебор всей истории");
            shiftCalls[0].RequestMessage.Query!["month"].Should().Equal("2026-11");
            shiftCalls[0].RequestMessage.Query["page_size"].Should().Equal("100");

            // Айгуль: смена 5 ноября пришла — «не был» с предупреждением; Чолпонай: смен нет, но табель сервера дал 6 ч — тоже предупреждение
            var aigul = new DateOnly(2026, 11, 5);
            window.ApplyMark("u1", "Айгуль Сыдыкова", aigul, TimesheetStatus.Absent, null, null);
            UiShot.Settle(2);
            (Avalonia.Automation.AutomationProperties.GetName(CellOf(window, "u1", aigul)) ?? "").Should().Match("*Внимание*");
            var day3 = new DateOnly(2026, 11, 3);
            for (var i = 0; i < 60; i++)
            {
                window.ApplyMark("u3", "Чолпонай Эсенгулова", day3, TimesheetStatus.Absent, null, null);
                UiShot.Settle(2);
                if ((Avalonia.Automation.AutomationProperties.GetName(CellOf(window, "u3", day3)) ?? "").Contains("Внимание"))
                    break;
                await Task.Delay(25);
            }
            (Avalonia.Automation.AutomationProperties.GetName(CellOf(window, "u3", day3)) ?? "").Should().Match("*Внимание*", "часы из табеля сервера");
            server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/main/staff/timesheet/" && e.RequestMessage.Query!["month"].Contains("2026-11"));

            // сервер упал: месяц — 500, табель — 400 period_invalid → «Повторить», сетка остаётся
            server.ResetMappings();
            Stub(server, "GET", "/api/construction/shifts/", 500, "{\"detail\":\"boom\"}");
            Stub(server, "GET", "/api/main/staff/timesheet/", 400, "{\"code\":\"period_invalid\"}");
            await window.LoadAsync(true).WaitAsync(TimeSpan.FromSeconds(60));
            UiShot.Settle(4);
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeTrue("месяц не пришёл — «Повторить»");
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().Contain("Не удалось загрузить смены");
            Cells(window).Should().HaveCount(3 * 30);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>2026-11-19: кассир без права «Сотрудники» — сервер отдаёт в табеле только его самого: это обычный ответ, без «Повторить» и без ошибки; чужих строк не появляется.</summary>
    [AvaloniaFact]
    public async Task Cashier_ServerTimesheetWithOnlySelf_IsNotAnError()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (basic, dir) = MakeDeps(canEdit: false, me: "u1");
        using var doc = System.Text.Json.JsonDocument.Parse(
            "{\"employee\":\"u1\",\"employee_name\":\"Айгуль Сыдыкова\",\"days\":[{\"date\":\"2026-11-02\",\"worked_minutes\":300}]}");
        var self = ServerTimesheet.Parse(doc.RootElement, 2026, 11);
        var deps = new TimesheetDeps
        {
            MonthShifts = (_, _, _, _) => Task.FromResult(new ShiftMonthResult { Entries = [], Failed = false, Covered = true }),
            Timesheet = (_, _, _) => Task.FromResult<ServerTimesheet?>(self),
            Employees = basic.Employees,
            Marks = basic.Marks,
            Today = basic.Today,
            CanEdit = false,
            CurrentUserKey = "u1",
            CurrentUserName = "Айгуль Сыдыкова",
        };
        var window = new StaffTimesheetWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            await window.LoadAsync();
            UiShot.Settle(6);
            UiDrive.Find<Button>(window, "RetryButton").IsVisible.Should().BeFalse();
            Cells(window).Select(c => ((ValueTuple<string, string, DateOnly>)c.Tag!).Item1).Distinct().Should().Equal("u1");
            UiDrive.Find<TextBlock>(window, "StatusText").Text.Should().NotContain("Не удалось");
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
