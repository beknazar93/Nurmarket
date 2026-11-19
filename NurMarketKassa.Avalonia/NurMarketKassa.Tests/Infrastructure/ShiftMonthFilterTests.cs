using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-19 (ответ Умара 19.11, п. 1): смены месяца одним запросом (?month=YYYY-MM&amp;page_size=100, признак shifts_month_filter) и готовый табель сервера (main/staff/timesheet/).
/// Проверяется: разбор признака, запрос и пагинация месяца, кэш по месяцам, «без признака — прежний перебор страниц», 400 → прежний перебор, ошибка месяца, 400 period_invalid,
/// терпимый разбор табеля. Сервер — WireMock / поддельные страницы.</summary>
[Collection("ServerFeatures")]
public sealed class ShiftMonthFilterTests
{
    private static ShiftHistoryEntry E(string id, DateTime opened) => new() { ShiftNumber = id, OpenedAt = opened, ClosedAt = opened.AddHours(8) };

    private sealed class Fake
    {
        public Dictionary<(int, int), List<ShiftHistoryEntry>> Months = new();
        public List<(int Y, int M, int Page)> MonthCalls = new();
        public List<int> LegacyCalls = new();
        public List<ShiftHistoryEntry> Legacy = new();
        public Func<int, int, int, Exception?>? Fail;
        public bool NextUnknown; // сервер не прислал поле next

        public Task<IReadOnlyList<ShiftHistoryEntry>?> LegacyPage(int page, CancellationToken ct)
        {
            lock (LegacyCalls)
                LegacyCalls.Add(page);
            var rows = Legacy.Skip((page - 1) * 4).Take(4).ToList();
            return Task.FromResult<IReadOnlyList<ShiftHistoryEntry>?>(rows.Count == 0 ? null : rows);
        }

        public Task<(IReadOnlyList<ShiftHistoryEntry> Rows, bool? HasNext)?> MonthPage(int y, int m, int page, CancellationToken ct)
        {
            lock (MonthCalls)
                MonthCalls.Add((y, m, page));
            if (Fail?.Invoke(y, m, page) is { } ex)
                throw ex;
            var all = Months.TryGetValue((y, m), out var l) ? l : new List<ShiftHistoryEntry>();
            var rows = all.Skip((page - 1) * 3).Take(3).ToList(); // «страница» из трёх, чтобы проверить пагинацию без сотни строк
            bool? next = NextUnknown ? null : page * 3 < all.Count;
            return Task.FromResult<(IReadOnlyList<ShiftHistoryEntry>, bool?)?>(rows.Count == 0 ? null : (rows, next));
        }
    }

    private static ShiftMonthLoader Loader(Fake f, bool filter, DateTime? now = null) =>
        new(f.LegacyPage, null, () => now ?? new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc), f.MonthPage, _ => Task.FromResult(filter));

    [Theory]
    [InlineData("{\"features\":{\"shifts_month_filter\":true,\"shifts_date_filter\":true}}", true, true)]
    [InlineData("{\"capabilities\":{\"features\":[\"shifts_month_filter\"]}}", true, false)]
    [InlineData("{\"capabilities\":{\"features\":{\"shifts_date_filter\":true}}}", false, true)]
    [InlineData("{\"features\":{\"shifts_month_filter\":false}}", false, false)]
    public void Capabilities_Parse_FindsShiftsFilters(string json, bool month, bool date)
    {
        using var doc = JsonDocument.Parse(json);
        var set = ServerCapabilities.Parse(doc.RootElement);
        set.Contains(ServerCapabilities.ShiftsMonthFilter).Should().Be(month);
        set.Contains(ServerCapabilities.ShiftsDateFilter).Should().Be(date);
    }

    [Fact]
    public async Task WithFlag_AsksOneMonthRequest_NotWholeHistory_AndPaginates()
    {
        var f = new Fake();
        f.Months[(2026, 11)] = Enumerable.Range(0, 7).Select(i => E("n" + i, new DateTime(2026, 11, 19).AddDays(-i))).ToList();
        var res = await Loader(f, true).LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        res.Failed.Should().BeFalse();
        res.Covered.Should().BeTrue();
        res.Entries.Should().HaveCount(7, "7 смен = 3 страницы по 3 (HasNext от сервера)");
        f.MonthCalls.Should().Equal([(2026, 11, 1), (2026, 11, 2), (2026, 11, 3)]);
        f.LegacyCalls.Should().BeEmpty("перебор всей истории не нужен");
    }

    [Fact]
    public async Task WithFlag_ServerWithoutNext_FullPageMeansMore_ShortPageEnds()
    {
        var f = new Fake { NextUnknown = true };
        // без поля next решает число строк: страница «полная» — только если 100; три строки — конец
        f.Months[(2026, 11)] = Enumerable.Range(0, 3).Select(i => E("n" + i, new DateTime(2026, 11, 1).AddDays(i))).ToList();
        var res = await Loader(f, true).LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        res.Entries.Should().HaveCount(3);
        f.MonthCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task WithFlag_CacheIsPerMonth_PreviousMonthAsksOnlyMissing_ForceReloads()
    {
        var f = new Fake();
        f.Months[(2026, 11)] = [E("n1", new DateTime(2026, 11, 5))];
        f.Months[(2026, 10)] = [E("o1", new DateTime(2026, 10, 5))];
        var loader = Loader(f, true);
        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        f.MonthCalls.Select(c => c.M).Should().Equal(11);

        var oct = await loader.LoadAsync(new DateTime(2026, 10, 1), false, null, CancellationToken.None);
        f.MonthCalls.Select(c => c.M).Should().Equal(11, 10); // ноябрь из кэша, спросили только октябрь
        oct.Entries.Select(e => e.ShiftNumber).Should().BeEquivalentTo(["n1", "o1"], "с 1 октября и новее — как у прежнего загрузчика");

        await loader.LoadAsync(new DateTime(2026, 10, 1), false, null, CancellationToken.None);
        f.MonthCalls.Should().HaveCount(2, "оба месяца уже в кэше");

        await loader.LoadAsync(new DateTime(2026, 10, 1), true, null, CancellationToken.None);
        f.MonthCalls.Should().HaveCount(4, "force — заново");
    }

    [Fact]
    public async Task WithoutFlag_OldBehavior_PagesOfHistory_NoMonthRequests()
    {
        var f = new Fake { Legacy = Enumerable.Range(0, 10).Select(i => E("s" + i, new DateTime(2026, 11, 19).AddDays(-i * 3))).ToList() };
        var res = await Loader(f, false).LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        res.Covered.Should().BeTrue();
        f.MonthCalls.Should().BeEmpty();
        f.LegacyCalls.Should().NotBeEmpty();
        res.Entries.Count(s => s.OpenedAt >= new DateTime(2026, 11, 1)).Should().Be(7);
    }

    [Fact]
    public async Task Server400_OnMonth_FallsBackToPaging_AndRemembers()
    {
        var f = new Fake
        {
            Fail = (_, _, _) => new ApiException("validation_error", 400),
            Legacy = Enumerable.Range(0, 6).Select(i => E("s" + i, new DateTime(2026, 11, 19).AddDays(-i))).ToList(),
        };
        var loader = Loader(f, true);
        var res = await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        res.Failed.Should().BeFalse();
        res.Entries.Should().NotBeEmpty("ушли на прежний перебор страниц");
        f.LegacyCalls.Should().NotBeEmpty();
        var monthCalls = f.MonthCalls.Count;
        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        f.MonthCalls.Count.Should().Be(monthCalls, "сервер отверг месяц — повторно не спрашиваем до смены входа/кэша");
    }

    [Fact]
    public async Task MonthNetworkError_ReturnsFailed_KeepsOtherMonths_RetryAsksOnlyMissing()
    {
        var f = new Fake { Fail = (_, m, _) => m == 10 ? new HttpRequestException("нет связи") : null };
        f.Months[(2026, 11)] = [E("n1", new DateTime(2026, 11, 5))];
        var loader = Loader(f, true);
        var res = await loader.LoadAsync(new DateTime(2026, 10, 1), false, null, CancellationToken.None);
        res.Failed.Should().BeTrue();
        res.Covered.Should().BeFalse();
        res.Entries.Should().ContainSingle(e => e.ShiftNumber == "n1", "пришедший месяц не теряем");
        f.LegacyCalls.Should().BeEmpty("при обрыве связи прежний (ещё более долгий) перебор не запускаем");

        f.Fail = null;
        f.Months[(2026, 10)] = [E("o1", new DateTime(2026, 10, 7))];
        f.MonthCalls.Clear();
        var again = await loader.LoadAsync(new DateTime(2026, 10, 1), false, null, CancellationToken.None);
        again.Failed.Should().BeFalse();
        f.MonthCalls.Select(c => c.M).Should().Equal(10);
    }

    [Fact]
    public async Task TooManyMonthsBack_UsesOldPaging()
    {
        var f = new Fake { Legacy = [E("s0", new DateTime(2024, 1, 5))] };
        var res = await Loader(f, true).LoadAsync(new DateTime(2024, 1, 1), false, null, CancellationToken.None);
        f.MonthCalls.Should().BeEmpty("больше 12 месяцев — месячными запросами не качаем");
        f.LegacyCalls.Should().NotBeEmpty();
        res.Entries.Should().NotBeEmpty();
    }

    // ───────────── запрос к серверу (WireMock) ─────────────

    private static async Task<(WireMockServer Server, ShiftApiService Shifts)> StartAsync()
    {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        return (server, new ShiftApiService(api));
    }

    private static void Json(WireMockServer server, string path, int status, string body)
    {
        server.ResetMappings(); // новый ответ вместо прежнего (вход уже выполнен)
        server.Given(Request.Create().WithPath(path).UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));
    }

    [Fact]
    public async Task Api_MonthPage_SendsMonthAndPageSize_ReadsNext()
    {
        var (server, shifts) = await StartAsync();
        using var _ = server;
        Json(server, "/api/construction/shifts/", 200, "{\"count\":150,\"next\":\"https://x/api/construction/shifts/?page=2\",\"results\":[{\"id\":\"1\"}]}");
        var page = await shifts.ConstructionShiftsMonthPageAsync(2026, 11, 1);
        page.Should().NotBeNull();
        page!.HasNext.Should().BeTrue();
        page.Rows.GetArrayLength().Should().Be(1);
        var req = server.LogEntries.Last(e => e.RequestMessage.Path == "/api/construction/shifts/").RequestMessage;
        req.Query!["month"].Should().Equal("2026-11");
        req.Query["page_size"].Should().Equal("100");

        Json(server, "/api/construction/shifts/", 200, "{\"count\":1,\"next\":null,\"results\":[{\"id\":\"1\"}]}");
        (await shifts.ConstructionShiftsMonthPageAsync(2026, 11, 2))!.HasNext.Should().BeFalse();

        Json(server, "/api/construction/shifts/", 200, "[{\"id\":\"1\"}]");
        var bare = await shifts.ConstructionShiftsMonthPageAsync(2026, 11, 1);
        bare!.HasNext.Should().BeFalse("голый массив — без постраничности");
        (await shifts.ConstructionShiftsMonthPageAsync(2026, 11, 2)).Should().BeNull();
    }

    [Fact]
    public async Task Api_InvalidMonth_400_ThrowsApiException_WithServerCode()
    {
        var (server, shifts) = await StartAsync();
        using var _ = server;
        Json(server, "/api/construction/shifts/", 400, "{\"code\":\"validation_error\",\"detail\":\"month\"}");
        var ex = await Assert.ThrowsAsync<ApiException>(() => shifts.ConstructionShiftsMonthPageAsync(2026, 13, 1));
        ex.StatusCode.Should().Be(400);

        Json(server, "/api/main/staff/timesheet/", 400, "{\"code\":\"period_invalid\",\"detail\":\"month\"}");
        var ts = await Assert.ThrowsAsync<ApiException>(() => shifts.StaffTimesheetAsync(2026, 13));
        ts.StatusCode.Should().Be(400);
        ServerErrorCodes.Code(ts).Should().Be("period_invalid");
    }

    [Fact]
    public async Task Api_Timesheet_AsksMonth_403And404_AreNotErrors()
    {
        var (server, shifts) = await StartAsync();
        using var _ = server;
        Json(server, "/api/main/staff/timesheet/", 200, "{\"employees\":[]}");
        (await shifts.StaffTimesheetAsync(2026, 11)).Should().NotBeNull();
        server.LogEntries.Last(e => e.RequestMessage.Path == "/api/main/staff/timesheet/").RequestMessage.Query!["month"].Should().Equal("2026-11");

        Json(server, "/api/main/staff/timesheet/", 403, "{\"detail\":\"forbidden\"}");
        (await shifts.StaffTimesheetAsync(2026, 11)).Should().BeNull();
        Json(server, "/api/main/staff/timesheet/", 404, "{\"detail\":\"not found\"}");
        (await shifts.StaffTimesheetAsync(2026, 11)).Should().BeNull();
    }

    // ───────────── разбор табеля сервера ─────────────

    private static ServerTimesheet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ServerTimesheet.Parse(doc.RootElement, 2026, 11);
    }

    [Fact]
    public void Timesheet_Parse_ContractShape_DaysTotalsAndHours()
    {
        var ts = Parse("""
            {"month":"2026-11","employees":[
              {"employee":{"id":"u1","full_name":"Айгуль"},
               "days":[{"date":"2026-11-05","planned_start":"09:00","planned_end":"18:00","opened_at":"2026-11-05T09:12:00+06:00","closed_at":"2026-11-05T17:00:00+06:00","late_minutes":12,"worked_minutes":468,"revenue":"1500.50","extra":"x"}],
               "totals":{"worked_minutes":468,"late_minutes":12,"days_worked":1,"revenue":1500.5}}]}
            """);
        ts.Employees.Should().ContainSingle();
        var e = ts.Employees[0];
        (e.Key, e.Name, e.WorkedMinutes, e.LateMinutes, e.DaysWorked, e.Revenue).Should().Be(("u1", "Айгуль", 468, 12, 1, (decimal?)1500.5m));
        e.Days[0].PlannedStart.Should().Be(new DateTime(2026, 11, 5, 9, 0, 0));
        e.Days[0].LateMinutes.Should().Be(12);
        ts.HoursByDay("u1", "").Should().ContainKey(new DateOnly(2026, 11, 5)).WhoseValue.Should().BeApproximately(7.8, 0.001);
        ts.HoursByDay("", "Айгуль").Should().HaveCount(1, "и по имени");
        ts.HoursByDay("u9", "Чужой").Should().BeEmpty();
    }

    [Fact]
    public void Timesheet_Parse_Tolerant_OtherShapes_SelfOnly_Garbage()
    {
        // итогов нет — считаются по дням; дни — объект «дата → день»; корень — массив
        var arr = Parse("""[{"employee_id":7,"name":"Бекзат","days":{"2026-11-06":{"worked_minutes":330},"2026-11-07":{"worked_minutes":0}}}]""");
        arr.Employees.Should().ContainSingle();
        (arr.Employees[0].Key, arr.Employees[0].WorkedMinutes, arr.Employees[0].DaysWorked).Should().Be(("7", 330, 1));

        // кассир без права «Сотрудники»: сервер вернул только себя прямо в корне — не ошибка
        var self = Parse("""{"employee":"u3","employee_name":"Чолпонай","days":[{"date":"2026-11-03","worked_minutes":360}]}""");
        self.Employees.Should().ContainSingle(e => e.Key == "u3");

        // пусто и мусор — пустой табель, без исключений
        Parse("""{"employees":[]}""").Employees.Should().BeEmpty();
        Parse("""{"unexpected":true}""").Employees.Should().BeEmpty();
        Parse("""[1,2,"x"]""").Employees.Should().BeEmpty();
        Parse("""{"employees":[{"id":"u1","days":[{"date":"плохая"},{"worked_minutes":5}]}]}""").Employees.Single().Days.Should().BeEmpty();
    }
}
