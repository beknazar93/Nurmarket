using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-19: «месячный» загрузчик смен табеля (ShiftMonthLoader) на поддельных страницах: граница месяца, пустая история, ошибка на второй странице,
/// кэш страниц между месяцами. Сервер не нужен.</summary>
public sealed class ShiftMonthLoaderTests
{
    private const int PageSize = 4;

    /// <summary>Смены с шагом в сутки от новых к старым: страница N — PageSize смен подряд.</summary>
    private static List<ShiftHistoryEntry> History(DateTime newest, int total) =>
        Enumerable.Range(0, total).Select(i => new ShiftHistoryEntry { ShiftNumber = "s" + i, OpenedAt = newest.AddDays(-i) }).ToList();

    private sealed class FakeServer
    {
        public List<ShiftHistoryEntry> All = new();
        public List<int> Requested = new();
        public int? FailPage;

        public Task<IReadOnlyList<ShiftHistoryEntry>?> Page(int page, CancellationToken ct)
        {
            lock (Requested)
                Requested.Add(page);
            if (page == FailPage)
                throw new InvalidOperationException("нет связи");
            var rows = All.Skip((page - 1) * PageSize).Take(PageSize).ToList();
            return Task.FromResult<IReadOnlyList<ShiftHistoryEntry>?>(rows.Count == 0 ? null : rows);
        }
    }

    [Fact]
    public async Task EmptyHistory_IsCoveredAndEmpty_NoError()
    {
        var server = new FakeServer();
        var loader = new ShiftMonthLoader(server.Page);
        var res = await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        res.Entries.Should().BeEmpty();
        res.Failed.Should().BeFalse();
        res.Covered.Should().BeTrue();
        server.Requested.Should().Equal(1);
    }

    [Fact]
    public async Task CurrentMonth_StopsAtMonthStart_AndDoesNotReadWholeHistory()
    {
        // 100 суток истории, новая смена 19.11: до 1.11 — 19 смен (страницы 1–5), старше качать не нужно
        var server = new FakeServer { All = History(new DateTime(2026, 11, 19, 9, 0, 0), 100) };
        var loader = new ShiftMonthLoader(server.Page);
        var res = await loader.LoadAsync(ShiftMonthLoader.MonthStart(2026, 11), false, null, CancellationToken.None);
        res.Failed.Should().BeFalse();
        res.Covered.Should().BeTrue();
        res.Entries.Count(s => s.OpenedAt >= new DateTime(2026, 11, 1)).Should().Be(19, "все смены месяца на месте");
        server.Requested.Max().Should().BeLessThan(10, "100 смен — это 25 страниц, читать их все незачем");
    }

    [Fact]
    public async Task MonthBoundary_ShiftOnFirstDay_IsKept_AndOlderPageIsRead()
    {
        // страница из четырёх смен: 4.11, 3.11, 2.11, 1.11 00:00 — последняя ровно на границе, «старше» ещё нет → читаем следующую
        var server = new FakeServer { All = History(new DateTime(2026, 11, 4), 12) };
        var loader = new ShiftMonthLoader(server.Page);
        var res = await loader.LoadAsync(ShiftMonthLoader.MonthStart(2026, 11), false, null, CancellationToken.None);
        res.Entries.Should().Contain(s => s.OpenedAt == new DateTime(2026, 11, 1));
        res.Entries.Should().Contain(s => s.OpenedAt < new DateTime(2026, 11, 1), "страница с границей не доказывает, что дальше пусто");
        res.Covered.Should().BeTrue();
        server.Requested.Should().Contain(2);
    }

    [Fact]
    public async Task ErrorOnSecondPage_ReturnsFirstPage_AndFailed()
    {
        var server = new FakeServer { All = History(new DateTime(2026, 11, 19), 30), FailPage = 2 };
        var loader = new ShiftMonthLoader(server.Page);
        var progress = new List<int>();
        var res = await loader.LoadAsync(new DateTime(2026, 8, 1), false, rows => progress.Add(rows.Count), CancellationToken.None);
        res.Failed.Should().BeTrue();
        res.Covered.Should().BeFalse();
        res.Entries.Should().HaveCount(PageSize, "первая страница дошла — её не теряем");
        progress.Should().NotBeEmpty();

        // «Повторить» (без force) продолжает со страницы 2, а не начинает заново
        server.FailPage = null;
        server.Requested.Clear();
        var again = await loader.LoadAsync(new DateTime(2026, 11, 10), false, null, CancellationToken.None);
        again.Failed.Should().BeFalse();
        server.Requested.Should().NotContain(1);
    }

    [Fact]
    public async Task SwitchingToCoveredMonth_UsesPageCache_ForceReloads()
    {
        var server = new FakeServer { All = History(new DateTime(2026, 11, 19), 60) };
        var loader = new ShiftMonthLoader(server.Page);
        await loader.LoadAsync(new DateTime(2026, 10, 1), false, null, CancellationToken.None);
        var calls = server.Requested.Count;

        var nov = await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        server.Requested.Count.Should().Be(calls, "ноябрь уже покрыт скачанными страницами");
        nov.Covered.Should().BeTrue();

        await loader.LoadAsync(new DateTime(2026, 11, 1), true, null, CancellationToken.None);
        server.Requested.Count.Should().BeGreaterThan(calls, "force — заново с сервера");
    }

    [Fact]
    public async Task CacheExpiresAndDoesNotCrossLogins()
    {
        var server = new FakeServer { All = History(new DateTime(2026, 11, 19), 6) };
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var key = "A";
        var loader = new ShiftMonthLoader(server.Page, () => key, () => now);
        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        var calls = server.Requested.Count;

        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        server.Requested.Count.Should().Be(calls, "в пределах 5 минут — из кэша");

        key = "B";
        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        server.Requested.Count.Should().BeGreaterThan(calls, "другой вход — чужие смены не показываем");

        calls = server.Requested.Count;
        now = now.AddMinutes(6);
        await loader.LoadAsync(new DateTime(2026, 11, 1), false, null, CancellationToken.None);
        server.Requested.Count.Should().BeGreaterThan(calls, "кэш старше 5 минут — заново");
    }
}
