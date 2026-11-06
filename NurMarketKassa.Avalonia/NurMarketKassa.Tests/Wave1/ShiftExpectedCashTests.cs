using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты «ожидаемо в ящике»: ShiftExpectedCash (поправка к expected_cash сервера) и ShiftDrawerMath
/// (запасная формула X/Z и сводки смены). Это «Касса:» в шапке, X/Z и сводка в Telegram — одно число, тест держит правило.
/// Общая статика CorrectionProvider — тесты в одной коллекции и возвращают источник на место.</summary>
[Collection("ServerFeatures")]
public sealed class ShiftExpectedCashTests : IDisposable
{
    private readonly Func<string?, decimal> _saved = ShiftExpectedCash.CorrectionProvider;

    public void Dispose() => ShiftExpectedCash.CorrectionProvider = _saved;

    [Fact]
    public void Correction_WithoutSource_IsZero()
    {
        ShiftExpectedCash.CorrectionProvider = _ => 0m;
        ShiftExpectedCash.Correction("shift-1").Should().Be(0m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Correction_WithoutShiftId_IsZero_AndSourceNotAsked(string? shiftId)
    {
        var asked = false;
        ShiftExpectedCash.CorrectionProvider = _ => { asked = true; return -100m; };

        ShiftExpectedCash.Correction(shiftId).Should().Be(0m);

        asked.Should().BeFalse("без id смены источник поправок не спрашивается");
    }

    [Fact]
    public void Correction_SourceThrows_IsZero_ReportMustNotFail()
    {
        ShiftExpectedCash.CorrectionProvider = _ => throw new IOException("файл поправок занят");

        ShiftExpectedCash.Correction("s1").Should().Be(0m);
        ShiftExpectedCash.WithFix(500m, "s1").Should().Be(500m);
    }

    [Fact]
    public void WithFix_AddsCorrectionToServerExpected()
    {
        ShiftExpectedCash.CorrectionProvider = id => id == "s1" ? -250.50m : 0m;

        ShiftExpectedCash.WithFix(1000m, "s1").Should().Be(749.50m);
        ShiftExpectedCash.WithFix(1000m, "other").Should().Be(1000m, "поправка привязана к смене");
    }

    [Fact]
    public void WithFix_ServerSentNothing_StaysNull_CallerUsesFallbackFormula()
    {
        ShiftExpectedCash.CorrectionProvider = _ => -10m;
        ShiftExpectedCash.WithFix(null, "s1").Should().BeNull();
    }

    [Fact]
    public void WithFix_CanGoNegative_NoClamping()
    {
        // текущее поведение: сумму в минус не ограничивают (недостача видна как есть)
        ShiftExpectedCash.CorrectionProvider = _ => -500m;
        ShiftExpectedCash.WithFix(100m, "s1").Should().Be(-400m);
    }

    // ───────────── ShiftDrawerMath ─────────────

    [Fact]
    public void DrawerExpected_FollowsServerFormula()
    {
        // начало 1000 + наличные 5000 + долги наличными 300 + внесения 200 − изъятия 700 − наличные возвраты 150
        ShiftDrawerMath.Expected(1000m, 5000m, 300m, 200m, 700m, 150m).Should().Be(5650m);
    }

    [Fact]
    public void DrawerExpected_AllZero_IsOpeningOnly()
    {
        ShiftDrawerMath.Expected(1500m, 0m, 0m, 0m, 0m, 0m).Should().Be(1500m);
    }

    [Fact]
    public void DrawerNotOnServer_IsDepositsMinusWithdrawalsPlusQueuedCash()
    {
        ShiftDrawerMath.NotOnServer(pendingDeposits: 100m, pendingWithdrawals: 40m, queuedCash: 25.5m).Should().Be(85.5m);
        ShiftDrawerMath.NotOnServer(0m, 300m, 0m).Should().Be(-300m);
    }

    [Fact]
    public void Property_DrawerExpected_IsLinearInEachTerm() =>
        Gen.Int[0, 9_000_000].Array[6].Sample(c =>
        {
            decimal D(int i) => c[i] / 100m;
            var expected = ShiftDrawerMath.Expected(D(0), D(1), D(2), D(3), D(4), D(5));
            expected.Should().Be(D(0) + D(1) + D(2) + D(3) - D(4) - D(5));
            // лишняя копейка наличных — ровно копейка в ящике; лишняя копейка изъятий — минус копейка
            ShiftDrawerMath.Expected(D(0), D(1) + 0.01m, D(2), D(3), D(4), D(5)).Should().Be(expected + 0.01m);
            ShiftDrawerMath.Expected(D(0), D(1), D(2), D(3), D(4) + 0.01m, D(5)).Should().Be(expected - 0.01m);
        }, iter: 500);
}
