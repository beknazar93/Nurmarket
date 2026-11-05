using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-05: тесты исправлений по ревью волн 0–3 («деньги и данные»): PIN ревизии, sold_at офлайн-чека, Polly-срок тяжёлых отчётов, ключ компании, права.
/// Без сети, окон и боевых данных.</summary>
public sealed class ReviewWaves03FixesTests
{
    // ───────────── п. 1: PIN при недостаче ревизии ─────────────

    [Fact]
    public void CycleCount_PinThreshold_IsOffByDefault()
    {
        var data = new CycleCountData();
        data.PinThreshold.Should().Be(0);
        data.Normalize();
        data.PinThreshold.Should().Be(0);

        var broken = new CycleCountData { PinThreshold = double.NaN };
        broken.Normalize();
        broken.PinThreshold.Should().Be(0, "сбой значения = выключено, а не 5000");
    }

    [Fact]
    public void CycleCount_MustAskPin_SeniorAndZeroThresholdSkip()
    {
        (double, double)[] bigShortage = [(-10, 1000)]; // 10 000 сом
        CycleCountRules.MustAskPin(bigShortage, 0, isSenior: false).Should().BeFalse("порог 0 — выключено");
        CycleCountRules.MustAskPin(bigShortage, 5000, isSenior: true).Should().BeFalse("владелец/администратор PIN не вводят");
        CycleCountRules.MustAskPin(bigShortage, 5000, isSenior: false).Should().BeTrue();
        CycleCountRules.MustAskPin([(-1, 100)], 5000, isSenior: false).Should().BeFalse("маленькая недостача");
    }

    [Theory]
    [InlineData(LatePinStatus.Ok, true)]
    [InlineData(LatePinStatus.Error, true)]   // нет связи/прав — ревизию не блокируем, показываем предупреждение
    [InlineData(LatePinStatus.Wrong, false)]
    [InlineData(LatePinStatus.Locked, false)]
    public void CycleCount_PinOutcome_OnlyWrongOrLockedBlocks(LatePinStatus status, bool allows) =>
        CycleCountRules.PinOutcomeAllowsFinish(status).Should().Be(allows);

    [Fact]
    public void CycleCount_NoteSuffix_RespectsShortLimit()
    {
        var rows = Enumerable.Range(1, 30).Select(i => ($"Товар номер {i}", -1.0, (string?)"spoilage")).ToList();
        CycleCountReasons.NoteSuffix(rows, 150).Length.Should().BeLessThanOrEqualTo(150);
    }

    // ───────────── п. 4: sold_at по поправке часов в момент продажи ─────────────

    [Fact]
    public void SoldAt_UsesSkewRememberedAtSale_NotCurrent()
    {
        var created = new DateTimeOffset(2026, 11, 5, 23, 50, 0, TimeSpan.FromHours(6));
        var serverNow = created.AddHours(2);
        // поправки в момент продажи не было — чек уходит как есть, даже если сейчас часы разошлись
        ServerClock.SoldAt(created, null, serverNow).Should().Be(created);
        ServerClock.SoldAt(created, 0, serverNow).Should().Be(created);
        // была +10 мин в момент продажи
        ServerClock.SoldAt(created, 600, serverNow).Should().Be(created.AddMinutes(10));
    }

    [Fact]
    public void SoldAt_ClampsSkewTo30Minutes_AndNeverLaterThanServerNow()
    {
        var created = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        ServerClock.SoldAt(created, 7200, created.AddHours(5)).Should().Be(created.AddMinutes(30), "поправка не больше 30 минут");
        ServerClock.SoldAt(created, -7200, created.AddHours(5)).Should().Be(created.AddMinutes(-30));
        // чек не может быть продан «в будущем»: поправка +20 мин, а серверное «сейчас» — через 5 мин после продажи
        ServerClock.SoldAt(created, 1200, created.AddMinutes(5)).Should().Be(created.AddMinutes(5));
        ServerClock.SoldAt(created, double.NaN, created.AddHours(1)).Should().Be(created);
    }

    [Fact]
    public void ServerClock_Median_IgnoresSingleOutlier()
    {
        ServerClock.Median([5, 5, 5, 5, 3600]).Should().Be(5);
        ServerClock.Median([1, 2, 3, 4]).Should().Be(2.5);
        ServerClock.Median([]).Should().Be(0);
    }

    // ───────────── п. 2: Polly — тяжёлые отчёты и предохранитель ─────────────

    [Theory]
    [InlineData("/api/main/analytics/pnl/", true)]
    [InlineData("/api/main/analytics/market/", true)]
    [InlineData("api/main/products/export/", true)]
    [InlineData("/api/main/products/", false)]
    [InlineData("/api/users/clients/", false)]
    [InlineData(null, false)]
    public void IsHeavyReport_OnlyAnalyticsAndExports(string? path, bool heavy) =>
        TransportTuningHandler.IsHeavyReport(path).Should().Be(heavy);

    [Fact]
    public void HeavyReports_GetLongerTimeoutThanOrdinaryGet() =>
        TransportTuningHandler.HeavyGetTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(90));

    [Fact]
    public void BreakerTimeouts_OnePathAlone_NeverCountsAsServerFailure()
    {
        TimeoutPathTracker.Reset();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
            TimeoutPathTracker.CountsAsServerFailure("/api/main/analytics/pnl/", now.AddSeconds(i)).Should().BeFalse("медленный один путь — не мёртвый сервер");
        // второй, другой путь тоже не ответил — теперь это похоже на сервер
        TimeoutPathTracker.CountsAsServerFailure("/api/main/products/", now.AddSeconds(11)).Should().BeTrue();
        // окно прошло — снова по одному
        TimeoutPathTracker.CountsAsServerFailure("/api/main/products/", now.AddMinutes(5)).Should().BeFalse();
        TimeoutPathTracker.Reset();
    }

    // ───────────── п. 9–10: ключ компании и боевой адрес ─────────────

    [Fact]
    public void CompanyKey_AddsStandPrefix_AndIsEmptyWithoutCompany()
    {
        AppPaths.CompanyKey("42", "").Should().Be("c:42");
        AppPaths.CompanyKey(" 42 ", "localhost:5123|").Should().Be("localhost:5123|c:42");
        AppPaths.CompanyKey(null, "x|").Should().BeEmpty();
        AppPaths.CompanyKey("  ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://app.nurcrm.kg:443", null)]
    [InlineData("https://app.nurcrm.kg:8443/api", null)]
    [InlineData("https://www.app.nurcrm.kg", null)]
    [InlineData("https://market.nurcrm.kg/", null)]
    [InlineData("http://192.168.1.5:8000", "192.168.1.5:8000")]
    public void NonProductionHostOf_IgnoresPortAndNurcrmSubdomains(string url, string? expected) =>
        AppPaths.NonProductionHostOf(url).Should().Be(expected);

    // ───────────── п. 5 (Л4-10): права по роли профиля, а не по ключу --owner ─────────────

    [Theory]
    [InlineData("""{"user":{"id":"1","role":"owner"}}""", true)]
    [InlineData("""{"role":"admin"}""", true)]
    [InlineData("""{"user":{"id":"2","role":"cashier"}}""", false)]
    [InlineData("""{"user":{"id":"3","role":"manager"}}""", false)]
    [InlineData("""{}""", false)]
    public void IsOwnerOrAdmin_DependsOnlyOnProfileRole(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        PermissionService.IsOwnerOrAdmin(doc.RootElement.Clone()).Should().Be(expected);
    }
}
