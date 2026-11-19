using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using static NurMarketKassa.Services.DailyReconciliation;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-13 (Л-12): ежедневная сверка «касса ↔ сервер» — сравнение чистое (без сети и файлов), сообщает только расхождения и ничего не правит.</summary>
public sealed class DailyReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 18, 0, 0, TimeSpan.FromHours(6));

    private static LocalSale Sale(string id, decimal total, int minutesAgo = 120, bool refunds = false) =>
        new(id, "1" + id, total, Now.AddMinutes(-minutesAgo), refunds);

    private static ServerSale Srv(string id, decimal total, string status = "paid") => new(id, total, status);

    private static Report Run(IReadOnlyList<LocalSale>? local = null, IReadOnlyList<ServerSale>? server = null, bool complete = true,
        IReadOnlyList<QueuedSale>? queue = null, IReadOnlyList<StockPair>? stock = null) =>
        Compare(local ?? [], server ?? [], complete, queue ?? [], stock ?? [], Now);

    [Fact]
    public void Everything_Matches_NoFindings()
    {
        var report = Run([Sale("a", 100m), Sale("b", 250.5m)], [Srv("a", 100m), Srv("b", 250.50m), Srv("other-till", 70m)],
            stock: [new StockPair("p", "Хлеб", 5, 5)]);
        report.HasProblems.Should().BeFalse("расхождений нет — кассиру ничего не показывается");
        report.FullText.Should().Contain("расхождений нет");
    }

    [Fact]
    public void SaleMissingOnServer_IsReported_WithCountAndSum()
    {
        var report = Run([Sale("a", 100m), Sale("lost", 300m), Sale("lost2", 50m)], [Srv("a", 100m)]);
        var f = report.Findings.Should().ContainSingle(x => x.Kind == Kind.MissingOnServer).Subject;
        f.Text.Should().Contain("2").And.Contain("350");
    }

    [Fact]
    public void FreshSale_AndServerListNotComplete_AreNotReportedAsMissing()
    {
        Run([Sale("fresh", 10m, minutesAgo: 2)], []).HasProblems.Should().BeFalse("чек пробит 2 минуты назад — сервер мог ещё не отдать его в списке");
        Run([Sale("old", 10m)], [], complete: false).HasProblems.Should().BeFalse("список сервера прочитан не до конца — «нет на сервере» не заявляется");
    }

    [Fact]
    public void SaleUnderQueueId_IsNotMissing()
    {
        var queue = new[] { new QueuedSale("local-q", 90m, OfflineSaleEntry.Synced, Now.AddHours(-1), null) };
        Run([Sale("local-q", 90m)], [], queue: queue).HasProblems.Should().BeFalse();
    }

    [Fact]
    public void AmountDiffers_OnlyForPaidWithoutRefunds()
    {
        Run([Sale("a", 100m)], [Srv("a", 90m)]).Findings.Should().ContainSingle(x => x.Kind == Kind.AmountDiffers);
        Run([Sale("a", 100m, refunds: true)], [Srv("a", 90m)]).HasProblems.Should().BeFalse("после возврата сервер сам пересчитывает итог");
        Run([Sale("a", 100m)], [Srv("a", 100.005m)]).HasProblems.Should().BeFalse("копейки округления — не расхождение");
        Run([Sale("a", 100m)], [Srv("a", 10m, "cancelled")]).HasProblems.Should().BeFalse("отменённый на сервере чек — не сверяем сумму");
    }

    [Fact]
    public void QueueUnsentAndFailed_AreReported()
    {
        var queue = new[]
        {
            new QueuedSale("q1", 100m, OfflineSaleEntry.PendingSync, Now.AddHours(-3), null),
            new QueuedSale("q2", 50m, OfflineSaleEntry.PendingSync, Now.AddMinutes(-2), null), // свежий — ещё не повод
            new QueuedSale("q3", 70m, OfflineSaleEntry.Failed, Now.AddHours(-1), "Недостаточно остатка"),
        };
        var report = Run(queue: queue);
        report.Findings.Should().Contain(x => x.Kind == Kind.Unsent && x.Text.Contains("1 чеков") && x.Text.Contains("100"));
        report.Findings.Should().Contain(x => x.Kind == Kind.Failed && x.Text.Contains("Недостаточно остатка"));
    }

    [Fact]
    public void StockDiffers_IsReported_AndLimitedToTen()
    {
        var stock = Enumerable.Range(0, 14).Select(i => new StockPair("p" + i, "Товар " + i, 5, 4)).Append(new StockPair("ok", "Ровный", 3, 3)).ToList();
        var report = Run(stock: stock);
        report.Findings.Count(x => x.Kind == Kind.StockDiffers).Should().Be(11, "10 товаров и одна строка «…и ещё 4»");
        report.Findings.Last().Text.Should().Contain("ещё 4");
        report.FullText.Should().Contain("ничего не исправляла");
    }

    [Fact]
    public void ParseServerSale_ReadsIdTotalStatus_AndSkipsOpenCarts()
    {
        using var doc = JsonDocument.Parse("""[{"id":"s1","total":"150.50","status":"PAID"},{"id":"s2","total":20,"status":"new"},{"total":5}]""");
        var rows = doc.RootElement.EnumerateArray().Select(DailyReconciliation.ParseServerSale).ToList();
        rows[0].Should().Be(new ServerSale("s1", 150.50m, "paid"));
        rows[1].Should().BeNull("открытая корзина — не чек");
        rows[2].Should().BeNull("нет id");
    }

    [Fact]
    public async Task RunAsync_WithFakeSources_UsesRecheckAndSkipsStockWhenQueueHasUnsent()
    {
        var recent = new List<(string Id, string Name, double Local)> { ("p1", "Хлеб", 5), ("p2", "Молоко", 7) };
        var serverStock = new Dictionary<string, double> { ["p1"] = 4, ["p2"] = 7 };
        var calls = 0;
        var sources = new Sources
        {
            Local = () => [Sale("a", 100m)],
            Queue = () => [],
            RecentProducts = () => recent,
            LocalStock = id => id == "p1" ? 5 : 7,
            ServerSales = (_, _, _) => Task.FromResult((new List<ServerSale> { Srv("a", 100m) }, true)),
            ServerStock = (id, _) => { calls++; return Task.FromResult<double?>(serverStock[id]); },
            RecheckDelay = TimeSpan.FromMilliseconds(5),
        };
        var report = await RunAsync(null, null, CancellationToken.None, sources, Now);
        report.Findings.Should().ContainSingle(x => x.Kind == Kind.StockDiffers && x.Text.Contains("Хлеб"));
        report.StockChecked.Should().Be(2);
        calls.Should().Be(3, "два товара + повторное чтение расходящегося");

        var withQueue = new Sources
        {
            Local = () => [],
            Queue = () => [new QueuedSale("q", 10m, OfflineSaleEntry.PendingSync, Now.AddMinutes(-1), null)],
            RecentProducts = () => recent,
            ServerSales = (_, _, _) => Task.FromResult((new List<ServerSale>(), true)),
            ServerStock = (id, _) => Task.FromResult<double?>(0),
        };
        var r2 = await RunAsync(null, null, CancellationToken.None, withQueue, Now);
        r2.Findings.Should().BeEmpty("чек в очереди: остатки на сервере ещё не списаны — не сверяем, чтобы не дать ложную тревогу");
        r2.StockChecked.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ServerUnavailable_GivesErrorReport_NotFindings()
    {
        var sources = new Sources
        {
            Local = () => [Sale("a", 100m)],
            Queue = () => [],
            ServerSales = (_, _, _) => throw new HttpRequestException("нет сети"),
        };
        var report = await RunAsync(null, null, CancellationToken.None, sources, Now);
        report.Error.Should().NotBeNull();
        report.HasProblems.Should().BeFalse("без связи расхождений не заявляем");
        report.FullText.Should().Contain("Сверка не выполнена");
    }

    [Fact]
    public void IsDue_Once_Per_23_Hours()
    {
        IsDue(Now, null).Should().BeTrue();
        IsDue(Now, (Now.AddHours(-5), 0, "")).Should().BeFalse();
        IsDue(Now, (Now.AddHours(-24), 0, "")).Should().BeTrue();
    }
}
