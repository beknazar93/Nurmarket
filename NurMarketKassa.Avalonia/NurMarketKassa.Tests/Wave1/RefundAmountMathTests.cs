using System.Text.Json;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты существующего поведения RefundAmountMath (сумма возврата строки чека со скидкой на чек).
/// Тесты фиксируют ТЕКУЩИЕ правила (Д-4 ответа Умара: копейки «от нуля», последний возврат = остаток), а не «как надо»:
/// изменение правила в В2 должно сначала менять эти тесты осознанно.</summary>
public sealed class RefundAmountMathTests
{
    [Theory]
    [InlineData("1.005", "1.01")]
    [InlineData("1.004", "1.00")]
    [InlineData("2.675", "2.68")]
    [InlineData("-1.005", "-1.01")] // «от нуля»: у минуса тоже вверх по модулю
    [InlineData("0", "0.00")]
    public void Round2_IsAwayFromZero(string input, string expected) =>
        RefundAmountMath.Round2(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void LineShare_DiscountOnReceipt_SplitsPaidAmountByLineShare()
    {
        // пример из комментария класса: строки 15 + 15 = 30, оплачено 28 (скидка на чек 2) → строка 15 = 14,00
        RefundAmountMath.LineShare(15m, 30m, 28m).Should().Be(14.00m);
    }

    [Fact]
    public void LineShare_NoDiscount_ReturnsLineTotal()
    {
        RefundAmountMath.LineShare(15m, 30m, 30m).Should().Be(15m);
    }

    [Fact]
    public void LineShare_PaidMoreThanLines_ShareIsCappedAtOne()
    {
        // доля не больше 1: переплата (оплачено больше суммы строк) не увеличивает возврат
        RefundAmountMath.LineShare(15m, 30m, 45m).Should().Be(15m);
    }

    [Theory]
    [InlineData(0, 30, 28, 0)]       // нулевая строка — ноль
    [InlineData(-5, 30, 28, 0)]      // отрицательная строка («Расход») — ноль
    [InlineData(15, 0, 28, 15)]      // суммы строк нет — строка как есть
    [InlineData(15, 0.004, 28, 15)]  // меньше 0,005 — тоже «нет»
    [InlineData(15, 30, 0, 15)]      // оплаты нет (долг) — строка как есть
    [InlineData(15, 30, -3, 15)]     // отрицательная оплата — строка как есть
    public void LineShare_DegenerateInputs_FallBackToRoundedLine(double line, double linesSum, double paid, double expected) =>
        RefundAmountMath.LineShare((decimal)line, (decimal)linesSum, (decimal)paid).Should().Be((decimal)expected);

    [Fact]
    public void LineShare_RoundsHalfAwayFromZero()
    {
        // 10 × (3 / 7) = 4,285714… → 4,29; а 1 × (0,5/1) = 0,50
        RefundAmountMath.LineShare(10m, 7m, 3m).Should().Be(4.29m);
        // 0,05 × (0,5 / 1) = 0,025 → 0,03 (половина вверх)
        RefundAmountMath.LineShare(0.05m, 1m, 0.5m).Should().Be(0.03m);
    }

    [Theory]
    [InlineData(10.0, 4.0, 4.0)]
    [InlineData(10.0, 20.0, 10.0)]
    [InlineData(10.0, null, 10.0)]   // сервер остаток не назвал — сумма как есть
    [InlineData(10.0, 0.0, 10.0)]    // остаток 0 или меньше — «не знаем», сумма как есть (текущее поведение)
    [InlineData(10.0, -1.0, 10.0)]
    public void ClampToDue_LimitsByRefundable_OnlyWhenPositive(double requested, double? refundable, double expected) =>
        RefundAmountMath.ClampToDue((decimal)requested, refundable is { } r ? (decimal)r : null).Should().Be((decimal)expected);

    [Fact]
    public void ClampToDue_RoundsBothSides()
    {
        RefundAmountMath.ClampToDue(5.005m, 5.004m).Should().Be(5.00m);
        RefundAmountMath.ClampToDue(5.005m, null).Should().Be(5.01m);
    }

    [Fact]
    public void AmountForReturn_LastReturn_IsExactlyTheRemainder()
    {
        // последний возврат по чеку — ровно остаток, без копеек округления долей
        RefundAmountMath.AmountForReturn(sharesSum: 9.99m, remaining: 10.00m, returnsAllLeft: true).Should().Be(10.00m);
    }

    [Fact]
    public void AmountForReturn_PartialReturn_IsSharesButNotMoreThanRemainder()
    {
        RefundAmountMath.AmountForReturn(4.50m, 10m, returnsAllLeft: false).Should().Be(4.50m);
        RefundAmountMath.AmountForReturn(12m, 10m, returnsAllLeft: false).Should().Be(10m);
    }

    [Fact]
    public void AmountForReturn_NegativeRemainder_IsZero()
    {
        RefundAmountMath.AmountForReturn(5m, -1m, returnsAllLeft: false).Should().Be(0m);
        RefundAmountMath.AmountForReturn(5m, -1m, returnsAllLeft: true).Should().Be(0m);
    }

    private static ApiException Exceeds(string body, int status = 409)
    {
        using var doc = JsonDocument.Parse(body);
        return new ApiException("refund failed", status, doc.RootElement.Clone());
    }

    [Fact]
    public void TryParseExceeds_ParsesServerNumbers()
    {
        var ex = Exceeds("{\"code\":\"refund_exceeds_paid\",\"data\":{\"paid\":\"28.00\",\"refunded\":12,\"requested\":\"2.00\",\"refundable\":1.8}}");

        RefundAmountMath.TryParseExceeds(ex, out var info).Should().BeTrue();

        info.Paid.Should().Be(28.00m);
        info.Refunded.Should().Be(12m);
        info.Requested.Should().Be(2.00m);
        info.Refundable.Should().Be(1.8m);
    }

    [Fact]
    public void TryParseExceeds_CodeWithoutData_IsStillTrue_WithEmptyInfo()
    {
        RefundAmountMath.TryParseExceeds(Exceeds("{\"code\":\"refund_exceeds_paid\"}"), out var info).Should().BeTrue();
        info.Should().Be(new RefundAmountMath.ExceedsInfo(null, null, null, null));
    }

    [Theory]
    [InlineData("Возврат больше положенного")]
    [InlineData("Возврат больше, чем оплачено")]
    public void TryParseExceeds_RecognizesByMessageText(string message) =>
        RefundAmountMath.TryParseExceeds(new ApiException(message, 400), out _).Should().BeTrue();

    [Fact]
    public void TryParseExceeds_OtherError_IsFalse()
    {
        RefundAmountMath.TryParseExceeds(new ApiException("Внутренняя ошибка сервера", 500), out var info).Should().BeFalse();
        info.Should().Be(new RefundAmountMath.ExceedsInfo(null, null, null, null));
    }

    // ───────────── property: инварианты (CsCheck) ─────────────

    [Fact]
    public void Property_LineShare_NeverExceedsLine_NeverNegative_AndHasTwoDecimals() =>
        Gen.Select(Gen.Int[1, 1_000_000], Gen.Int[1, 5_000_000], Gen.Int[0, 5_000_000]).Sample((lineCents, sumCents, paidCents) =>
        {
            var line = lineCents / 100m;
            var share = RefundAmountMath.LineShare(line, sumCents / 100m, paidCents / 100m);
            share.Should().BeGreaterThanOrEqualTo(0m);
            share.Should().BeLessThanOrEqualTo(line);
            share.Should().Be(Math.Round(share, 2), "результат — деньги до копейки");
        }, iter: 2000);

    [Fact]
    public void Property_AmountForReturn_NeverExceedsRemainder() =>
        Gen.Select(Gen.Int[0, 5_000_000], Gen.Int[-1000, 5_000_000], Gen.Bool).Sample((sharesCents, remCents, all) =>
        {
            var amount = RefundAmountMath.AmountForReturn(sharesCents / 100m, remCents / 100m, all);
            amount.Should().BeGreaterThanOrEqualTo(0m);
            amount.Should().BeLessThanOrEqualTo(Math.Max(0m, remCents / 100m));
        }, iter: 2000);
}
