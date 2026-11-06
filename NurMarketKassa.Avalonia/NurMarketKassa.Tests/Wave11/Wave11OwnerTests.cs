using System.Collections.Specialized;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Wave11;

/// <summary>2026-11-06 (В11, программа владельца): общие JSON под межпроцессным замком и уникальный .tmp (шаг 22), долги по общей цепочке обработчиков (25.1),
/// «сегодня» по серверным часам (Л4-06 хвост), сигнал продаж вместо частого таймера «Сводки» (шаг 5), список клиентов одним событием (п. 26.2), ширина столбца графика (Д4-41),
/// итог перемещения (макет MStock), уплотнение кучи и учёт закрытых разделов (ПМ-1).</summary>
public sealed class AtomicFileUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-w11-" + Guid.NewGuid().ToString("N"));

    public AtomicFileUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void UniqueTmpPath_is_different_each_time_and_lies_next_to_the_file()
    {
        var path = Path.Combine(_dir, "data.json");
        var names = Enumerable.Range(0, 50).Select(_ => AtomicFile.UniqueTmpPath(path)).ToList();

        names.Distinct().Should().HaveCount(50, "две программы пишут один файл — общий «data.json.tmp» им бы мешал");
        names.Should().OnlyContain(n => n.StartsWith(path + ".", StringComparison.Ordinal) && n.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteAllText_leaves_no_temporary_files_behind()
    {
        var path = Path.Combine(_dir, "data.json");
        AtomicFile.WriteAllText(path, "[1]");
        AtomicFile.WriteAllText(path, "[1,2]");

        File.ReadAllText(path).Should().Be("[1,2]");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Update_from_many_threads_loses_no_record()
    {
        var path = Path.Combine(_dir, "journal.json");
        const int writers = 40;

        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            AtomicFile.Update(path, text =>
            {
                var list = string.IsNullOrWhiteSpace(text) ? new List<int>() : JsonSerializer.Deserialize<List<int>>(text)!;
                list.Add(i);
                return JsonSerializer.Serialize(list);
            }));

        var result = JsonSerializer.Deserialize<List<int>>(File.ReadAllText(path))!;
        result.Should().HaveCount(writers, "читать-менять-писать идёт под замком: побеждает не «последний», а все");
        result.Distinct().Should().HaveCount(writers);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Update_does_not_write_when_nothing_changes()
    {
        var path = Path.Combine(_dir, "same.json");
        AtomicFile.WriteAllText(path, "[7]");
        var before = File.GetLastWriteTimeUtc(path);

        AtomicFile.Update(path, _ => null).Should().BeNull();

        File.ReadAllText(path).Should().Be("[7]");
        File.GetLastWriteTimeUtc(path).Should().Be(before);
    }

    [Fact]
    public void Update_creates_the_file_when_it_does_not_exist_yet()
    {
        var path = Path.Combine(_dir, "new.json");
        string? seen = "not-called";

        AtomicFile.Update(path, text => { seen = text; return "[]"; });

        seen.Should().BeNull("файла не было — преобразователь получает null");
        File.ReadAllText(path).Should().Be("[]");
    }

    [Fact]
    public void CrossProcessGateName_is_in_the_session_namespace_and_ignores_letter_case()
    {
        var a = AtomicFile.CrossProcessGateName(Path.Combine(_dir, "Loss-Sales.json"));
        var b = AtomicFile.CrossProcessGateName(Path.Combine(_dir, "loss-sales.JSON"));
        var other = AtomicFile.CrossProcessGateName(Path.Combine(_dir, "rental-alerts.json"));

        a.Should().StartWith(@"Local\", "Global\\ требует права и мешает второму пользователю ПК");
        a.Should().Be(b, "касса и программа владельца обязаны получить одно имя замка");
        a.Should().NotBe(other);
    }

    [Fact]
    public void LossSales_AppendTo_adds_the_record_drops_old_ones_and_survives_broken_json()
    {
        var old = new LossSaleRecord { At = DateTimeOffset.Now.AddDays(-200), Cashier = "старый" };
        var fresh = new LossSaleRecord { At = DateTimeOffset.Now.AddDays(-1), Cashier = "вчера" };
        var added = new LossSaleRecord { At = DateTimeOffset.Now, Cashier = "сейчас" };
        var current = JsonSerializer.Serialize(new List<LossSaleRecord> { old, fresh });

        var next = JsonSerializer.Deserialize<List<LossSaleRecord>>(LossSalesStore.AppendTo(current, added))!;
        next.Select(r => r.Cashier).Should().Equal("вчера", "сейчас");

        var fromBroken = JsonSerializer.Deserialize<List<LossSaleRecord>>(LossSalesStore.AppendTo("{не json", added))!;
        fromBroken.Select(r => r.Cashier).Should().Equal("сейчас");
        JsonSerializer.Deserialize<List<LossSaleRecord>>(LossSalesStore.AppendTo(null, added))!.Should().HaveCount(1);
    }
}

public sealed class ServerTodayTests
{
    [Fact]
    public void Today_follows_the_server_clock_not_the_pc_clock()
    {
        // ПК показывает 23:50 пятого числа, сервер впереди на 20 минут — для отчёта «Сегодня» уже шестое число
        var pc = new DateTime(2026, 11, 5, 23, 50, 0);
        ServerClock.TodayFor(pc, TimeSpan.FromMinutes(20)).Should().Be(new DateTime(2026, 11, 6));
        ServerClock.TodayFor(pc, TimeSpan.Zero).Should().Be(new DateTime(2026, 11, 5));
        // ПК убежал вперёд на 2 часа — сервер ещё в предыдущем дне
        ServerClock.TodayFor(new DateTime(2026, 11, 6, 1, 0, 0), TimeSpan.FromHours(-2)).Should().Be(new DateTime(2026, 11, 5));
    }

    [Fact]
    public void Today_without_skew_equals_the_calendar_date_of_the_pc()
    {
        ServerClock.Today.Date.Should().Be(ServerClock.Today);
        Math.Abs((ServerClock.Today - DateTime.Today).TotalDays).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Rental_kind_uses_the_server_today_by_default()
    {
        // «Вернуть сегодня» считается от серверной даты; явная дата по-прежнему работает (прежние тесты)
        var rental = new RentalDto("r1", 7, "c1", "Клиент", "active", false, null, new DateTime(2026, 11, 6), "day", "money", 0, "", "",
            Array.Empty<RentalItem>(), null, 0, "", null, null, 0, 0);
        RentalDueNotifier.Kind(rental, new DateTime(2026, 11, 6)).Should().Be(RentalDueNotifier.DueKind.Today);
        RentalDueNotifier.Kind(rental, new DateTime(2026, 11, 5)).Should().Be(RentalDueNotifier.DueKind.Tomorrow);
    }
}

[Collection("ServerFeatures")]
public sealed class ClientDebtsSharedChainTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task PayDebt_goes_through_the_shared_api_chain_and_keeps_its_idempotency_key()
    {
        _fake.Json("POST", "/api/main/clients/c-1/pay-debt/", 200, "{\"paid\":\"100.00\",\"left\":\"50.00\",\"applied\":[{}],\"replayed\":false}");
        var api = await LoggedInAsync(_fake);
        using var debts = new ClientDebtsApiService(api, new AppSettings { ApiBaseUrl = _fake.Url });

        var result = await debts.PayDebtAsync("c-1", 100, "cash", null, "key-debt-1");

        result.Paid.Should().Be(100);
        result.Left.Should().Be(50);
        var request = Requests(_fake, "POST", "/pay-debt/").Single();
        Header(request, "Idempotency-Key").Should().Be("key-debt-1");
        Header(request, "Authorization").Should().Be("Bearer test-access", "токен ставит общая цепочка обработчиков");
        // Заголовок версии кассы ставит ОБЩИЙ HttpClient NurMarketApiClient; у прежнего отдельного клиента его не было.
        Header(request, "X-NurMarket-Client").Should().StartWith("kassa/");
    }

    [Fact]
    public async Task PayDebt_server_refusal_is_still_an_ApiException_with_its_code()
    {
        _fake.Json("POST", "/api/main/pos/sales/s-9/pay-debt/", 400, "{\"code\":\"no_debt\",\"detail\":\"Долга нет\"}");
        var api = await LoggedInAsync(_fake);
        using var debts = new ClientDebtsApiService(api, new AppSettings { ApiBaseUrl = _fake.Url });

        var act = () => debts.PaySaleDebtAsync("s-9", 10, "cash", null, "key-2");

        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(400);
    }
}

public sealed class OverviewSignalTests
{
    [Fact]
    public void Sales_pulse_is_alive_only_after_a_recent_answer()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        SyncCenter.IsSalesPulseAlive(0, now, TimeSpan.FromSeconds(90)).Should().BeFalse("ответов ещё не было");
        SyncCenter.IsSalesPulseAlive(now.AddSeconds(-30).Ticks, now, TimeSpan.FromSeconds(90)).Should().BeTrue();
        SyncCenter.IsSalesPulseAlive(now.AddSeconds(-200).Ticks, now, TimeSpan.FromSeconds(90)).Should().BeFalse("пульс замолчал — таймер возвращается к прежнему");
    }

    [Fact]
    public void Overview_timer_is_only_a_safety_net_while_the_pulse_is_alive()
    {
        var thirty = TimeSpan.FromSeconds(30);
        SyncCenter.OverviewIntervalFor(thirty, pulseAlive: true).Should().Be(TimeSpan.FromMinutes(2));
        SyncCenter.OverviewIntervalFor(thirty, pulseAlive: false).Should().Be(thirty, "пульса нет — как раньше");
        SyncCenter.OverviewIntervalFor(TimeSpan.FromMinutes(5), pulseAlive: true).Should().Be(TimeSpan.FromMinutes(5), "редкий интервал простоя не сокращаем");
    }

    [Fact]
    public void Requests_per_minute_for_the_overview_drop_when_the_pulse_signals_sales()
    {
        // три запроса сводки за одно обновление: раньше каждые 30 с (6 в минуту), теперь страховка раз в 2 минуты (1,5 в минуту)
        const int requestsPerRefresh = 3;
        double PerMinute(TimeSpan interval) => requestsPerRefresh * 60.0 / interval.TotalSeconds;
        var before = PerMinute(SyncCenter.OverviewIntervalFor(TimeSpan.FromSeconds(30), pulseAlive: false));
        var after = PerMinute(SyncCenter.OverviewIntervalFor(TimeSpan.FromSeconds(30), pulseAlive: true));

        before.Should().BeApproximately(6, 0.001);
        after.Should().BeApproximately(1.5, 0.001);
    }
}

public sealed class OwnerListAndChartTests
{
    [Fact]
    public void ReplaceAll_raises_one_reset_event_instead_of_one_per_row()
    {
        var list = new BulkObservableCollection<int>(new[] { 1, 2, 3 });
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        list.ReplaceAll(Enumerable.Range(10, 6000));

        list.Should().HaveCount(6000);
        events.Should().Equal(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void ReplaceAll_between_two_empty_lists_raises_nothing()
    {
        var list = new BulkObservableCollection<string>();
        var count = 0;
        list.CollectionChanged += (_, _) => count++;

        list.ReplaceAll(Array.Empty<string>());

        count.Should().Be(0);
    }

    [Theory]
    [InlineData(0.0, 9.4, 170.0, 2.0)] // нет продаж — тонкая черта
    [InlineData(0.1, 9400.0, 170.0, 8.0)] // «<0,1» при дне 9,4 тыс. — столбец не уже 8 px (раньше 4)
    [InlineData(0.3, 9.4, 170.0, 8.0)]
    [InlineData(9.4, 9.4, 170.0, 170.0)]
    [InlineData(4.7, 9.4, 170.0, 85.0)]
    public void Chart_bar_is_never_thinner_than_eight_pixels_when_there_were_sales(double value, double scaleMax, double area, double expected)
    {
        ChartBarHeight.For(value, scaleMax, area).Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void Chart_bar_with_a_broken_scale_stays_visible()
    {
        ChartBarHeight.For(5, 0, 170).Should().Be(ChartBarHeight.MinVisible);
        ChartBarHeight.For(5, double.NaN, 170).Should().Be(ChartBarHeight.MinVisible);
    }
}

public sealed class TransferSummaryTextTests
{
    [Fact]
    public void Created_transfer_says_what_the_send_button_will_do()
    {
        var text = TransferSummaryText.Build(2, 110, StockTransferService.StatusCreated);

        text.Should().Contain("2").And.Contain("110");
        text.Should().Contain("В пути", "до нажатия «Отправить» видно, что документ станет «В пути» (макет MStock)");
    }

    [Fact]
    public void In_transit_transfer_says_it_will_become_delivered_and_a_closed_one_says_nothing_more()
    {
        TransferSummaryText.Build(1, 5, StockTransferService.StatusInTransit).Should().Contain("Доставлено");
        TransferSummaryText.Build(1, 5, StockTransferService.StatusDelivered).Should().NotContain("станет");
        TransferSummaryText.Build(1, 5, StockTransferService.StatusCancelled).Should().NotContain("станет");
    }

    [Fact]
    public void Empty_transfer_has_no_promise_about_the_status_and_fractions_are_not_rounded_away()
    {
        TransferSummaryText.Build(0, 0, StockTransferService.StatusCreated).Should().NotContain("станет");
        TransferSummaryText.Build(1, 2.5, StockTransferService.StatusCreated).Should().MatchRegex(@"2[.,]5");
    }
}

public sealed class OwnerMemoryTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private const long Big = 200L * 1024 * 1024;

    [Fact]
    public void Trim_runs_when_the_heap_is_big_enough_the_owner_is_not_typing_and_the_last_trim_is_old()
    {
        OwnerMemoryTrim.ShouldTrim(Now, DateTime.MinValue, Now.AddMinutes(-1), Big).Should().BeTrue();
    }

    [Fact]
    public void Trim_is_skipped_for_a_small_heap_a_recent_trim_or_while_typing()
    {
        OwnerMemoryTrim.ShouldTrim(Now, DateTime.MinValue, DateTime.MinValue, 30L * 1024 * 1024).Should().BeFalse("уплотнять нечего");
        OwnerMemoryTrim.ShouldTrim(Now, Now.AddSeconds(-60), DateTime.MinValue, Big).Should().BeFalse("не чаще раза в 2 минуты");
        OwnerMemoryTrim.ShouldTrim(Now, DateTime.MinValue, Now.AddSeconds(-2), Big).Should().BeFalse("владелец печатает");
        OwnerMemoryTrim.ShouldTrim(Now, Now.AddMinutes(-3), DateTime.MinValue, Big).Should().BeTrue();
    }

    private static object MakeGarbage() => new byte[1024];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TrackNew(ClosedObjectWatch watch, string name, DateTime closedUtc, int gen2)
    {
        var window = MakeGarbage();
        watch.Track(window, name, closedUtc, gen2);
    }

    [Fact]
    public void Watch_names_a_closed_section_that_is_still_held_and_forgets_the_collected_one()
    {
        var watch = new ClosedObjectWatch();
        var held = new object(); // «закрытое окно», которое кто-то держит (статическое событие, таймер)
        watch.Track(held, "Склад", Now.AddMinutes(-30), gen2Count: 5);
        TrackNew(watch, "Долги", Now.AddMinutes(-30), 5); // это окно никто не держит

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var survivors = watch.FindSurvivors(Now, gen2Now: 9, minAge: TimeSpan.FromMinutes(10), minGen2Collections: 3);

        survivors.Select(s => s.Name).Should().Equal("Склад");
        survivors[0].Age.Should().Be(TimeSpan.FromMinutes(30));
        watch.Count.Should().Be(1, "собранная запись убрана из учёта");
        GC.KeepAlive(held);
    }

    [Fact]
    public void Watch_does_not_accuse_a_section_that_was_closed_a_moment_ago_or_before_enough_collections()
    {
        var watch = new ClosedObjectWatch();
        var held = new object();
        watch.Track(held, "Закупки", Now.AddMinutes(-2), gen2Count: 8);

        watch.FindSurvivors(Now, gen2Now: 20, minAge: TimeSpan.FromMinutes(10), minGen2Collections: 3).Should().BeEmpty("прошло всего 2 минуты");
        watch.FindSurvivors(Now.AddMinutes(20), gen2Now: 9, minAge: TimeSpan.FromMinutes(10), minGen2Collections: 3).Should().BeEmpty("сборщик ещё не успел: после закрытия одна сборка");
        GC.KeepAlive(held);
    }

    [Fact]
    public void Watch_has_a_limit_so_it_cannot_grow_by_itself()
    {
        var watch = new ClosedObjectWatch();
        var keep = new List<object>();
        for (var i = 0; i < 500; i++)
        {
            var o = new object();
            keep.Add(o);
            watch.Track(o, "раздел " + i, Now, 1);
        }

        watch.Count.Should().BeLessThanOrEqualTo(200);
        GC.KeepAlive(keep);
    }
}
