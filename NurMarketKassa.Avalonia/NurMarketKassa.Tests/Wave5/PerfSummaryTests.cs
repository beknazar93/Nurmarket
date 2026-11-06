using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave5;

/// <summary>2026-11-06: В5, шаг 24 (18.2, 18.3) — суточная строка PERF: p50/p95 по путям API и операциям SQLite. Чистые функции, без базы и сети.</summary>
public sealed class PerfSummaryTests
{
    private static PerfSummary.Stats S(params double[] ms) =>
        new(ms.Length, ms.Sum(), ms.Length == 0 ? 0 : ms.Max(), ms.Count(v => v >= PerfSummary.SlowSqliteMs), ms);

    [Fact]
    public void Percentile_of_empty_is_zero_and_nearest_rank_otherwise()
    {
        PerfSummary.Percentile(Array.Empty<double>(), 95).Should().Be(0);
        var hundred = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToArray(); // порядок не важен
        PerfSummary.Percentile(hundred, 50).Should().Be(50);
        PerfSummary.Percentile(hundred, 95).Should().Be(95);
        PerfSummary.Percentile(hundred, 100).Should().Be(100);
        PerfSummary.Percentile(new[] { 7.0 }, 95).Should().Be(7);
    }

    [Fact]
    public void BuildLine_is_null_without_samples()
    {
        PerfSummary.BuildLine(
            Array.Empty<KeyValuePair<string, PerfSummary.Stats>>(),
            Array.Empty<KeyValuePair<string, PerfSummary.Stats>>(),
            TimeSpan.FromHours(24)).Should().BeNull();
    }

    [Fact]
    public void BuildLine_has_p50_p95_max_for_api_and_sqlite_and_lists_slow_operations()
    {
        var api = new[]
        {
            new KeyValuePair<string, PerfSummary.Stats>("GET /api/main/products/changes/", S(Enumerable.Range(1, 100).Select(i => (double)i).ToArray())),
            new KeyValuePair<string, PerfSummary.Stats>("POST /api/pos/sales/{id}/pay/", S(300, 500)),
        };
        var sqlite = new[]
        {
            new KeyValuePair<string, PerfSummary.Stats>("DatabaseService.LoadKnownSaleIds", S(10, 240, 260)),
            new KeyValuePair<string, PerfSummary.Stats>("DatabaseService.AppendSoldLineItems", S(2, 3, 4)),
        };

        var line = PerfSummary.BuildLine(api, sqlite, TimeSpan.FromHours(24))!;

        line.Should().StartWith("за 24 ч:");
        line.Should().Contain("API 102 запр.").And.Contain("max 500 мс");
        line.Should().Contain("GET /api/main/products/changes/ ×100 p50 50 p95 95");
        line.Should().Contain("SQLite 6 вызовов").And.Contain("дольше 200 мс: 2");
        line.Should().Contain("DatabaseService.LoadKnownSaleIds ×2 max 260 мс");
        line.Should().NotContain("AppendSoldLineItems ×", "быстрые операции в список медленных не попадают");
        line.Should().NotContain("\n", "это одна строка журнала");
    }

    [Fact]
    public void BuildLine_limits_the_number_of_paths()
    {
        var api = Enumerable.Range(0, 30)
            .Select(i => new KeyValuePair<string, PerfSummary.Stats>($"GET /api/p{i:D2}/", S(10 + i)))
            .ToList();
        var line = PerfSummary.BuildLine(api, Array.Empty<KeyValuePair<string, PerfSummary.Stats>>(), TimeSpan.FromHours(24))!;
        line.Split(" ×").Length.Should().Be(9, "в строку идут не больше 8 самых частых путей");
    }

    [Fact]
    public void ShouldEmit_once_per_calendar_day_and_only_with_enough_window()
    {
        var today = new DateTime(2026, 11, 6, 15, 0, 0);
        // нет замеров — не пишем
        PerfSummary.ShouldEmit(today, null, TimeSpan.FromHours(30), onExit: false, hasSamples: false).Should().BeFalse();
        // таймер: нужны полные сутки
        PerfSummary.ShouldEmit(today, null, TimeSpan.FromHours(23), onExit: false, hasSamples: true).Should().BeFalse();
        PerfSummary.ShouldEmit(today, null, TimeSpan.FromHours(24), onExit: false, hasSamples: true).Should().BeTrue();
        // выход из программы: достаточно получаса, но не короче
        PerfSummary.ShouldEmit(today, null, TimeSpan.FromMinutes(29), onExit: true, hasSamples: true).Should().BeFalse();
        PerfSummary.ShouldEmit(today, null, TimeSpan.FromMinutes(31), onExit: true, hasSamples: true).Should().BeTrue();
        // сегодня уже писали — не чаще раза в сутки
        PerfSummary.ShouldEmit(today, today.Date, TimeSpan.FromHours(48), onExit: false, hasSamples: true).Should().BeFalse();
        PerfSummary.ShouldEmit(today, today.Date.AddDays(-1), TimeSpan.FromMinutes(40), onExit: true, hasSamples: true).Should().BeTrue();
    }

    [Fact]
    public void Series_keeps_only_the_last_samples_but_counts_everything()
    {
        var series = new PerfSummary.Series();
        for (var i = 1; i <= PerfSummary.MaxSamplesPerKey * 2 + 10; i++)
            series.Add(i, slow: i % 2 == 0);

        series.Count.Should().Be(PerfSummary.MaxSamplesPerKey * 2 + 10);
        series.MaxMs.Should().Be(PerfSummary.MaxSamplesPerKey * 2 + 10);
        series.Snapshot().Length.Should().Be(PerfSummary.MaxSamplesPerKey);
        series.Slow.Should().Be((PerfSummary.MaxSamplesPerKey * 2 + 10) / 2);
    }

    [Fact]
    public void Recording_never_throws_and_empty_keys_are_ignored()
    {
        var act = () =>
        {
            PerfSummary.RecordApi("", 5);
            PerfSummary.RecordApi("GET /api/x/", double.NaN);
            PerfSummary.RecordSqlite("", 1);
            using (PerfSummary.MeasureSqlite("Wave5.Test.Fast")) { }
        };
        act.Should().NotThrow();
    }
}
