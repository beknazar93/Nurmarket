using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (владелец, фото экрана клиента 24.11, чек 000006, клиент «Нан»): понятные списки смены и случай «пропадает 120 сом».
/// Что проверяется: (1) возврат по чеку «в долг» делится верно — сначала гасится остаток долга, наличными возвращается только оплаченное сверх долга
/// (итог 1 074,25, внесено 500, оплаты долга 120 зачтены → остаток долга 454,25; возврат 574 → в счёт долга 454,25, наличными 119,75 — РОВНО то, что на фото;
/// без зачёта оплат долга: остаток 574,25 → в счёт долга 574, наличными 0); (2) «Возвраты» смены — текст вместо «cash=…;debt=…»; (3) «Оплата долгов» — «Наличными» /
/// «Безналом» / «Смешанно»; (4) строка чека «Погашено позже» объясняет разницу «итог − внесено − долг»; (5) защита от повторной оплаты той же суммы.</summary>
[Collection("UserPreferences")]
public sealed class ShiftEventTextTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ShiftEventTextTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static SaleLedgerEntry Check000006() => new()
    {
        SaleId = "sale-6", Number = "000006", Method = "debt", Total = 1074.25m, LinesTotal = 1074.25m, PaidCash = 500m, PaidNonCash = 0m, Own = true,
    };

    // ── 1. арифметика возврата чека 000006 ────────────────────────────────────────────────────────

    [Fact]
    public void Return574_NoDebtPaymentsApplied_GoesAllToDebt_NothingInCash()
    {
        // Клиент внёс только 500 при продаже, долг по чеку остался 574,25 (оплаты долга на этот чек не зачтены).
        var r = RefundSplit.Compute(Check000006(), "debt", 574m, cashOut: null, serverDebtLeft: 574.25m);
        r.DebtReduced.Should().Be(574m, "возврат сначала гасит долг по чеку");
        r.Cash.Should().Be(0m, "наличными возвращается только оплаченное сверх долга — здесь всё внесённое осталось за товарами, которые клиент оставил");
        r.NonCash.Should().Be(0m);
        (r.Cash + r.NonCash + r.DebtReduced).Should().Be(574m);
    }

    [Fact]
    public void Return574_DebtPaymentsOf120Applied_DebtFirst_CashIsOnlyWhatClientPaidOverDebt()
    {
        // Оплаты долга клиента «Нан» 50 + 50 + 20 = 120 сервер зачёл на этот чек: остаток долга сейчас 454,25 (= 574,25 − 120). Они записаны на этой кассе наличными.
        var r = RefundSplit.Compute(Check000006(), "debt", 574m, cashOut: null, serverDebtLeft: 454.25m, localDebtPaid: 120m, localDebtCash: 120m);
        r.DebtReduced.Should().Be(454.25m, "ровно остаток долга по чеку — как на фото «debt=454.25»");
        r.Cash.Should().Be(119.75m, "клиент заплатил 500 + 120 = 620, за оставленные товары (500,25) положено 620 − 500,25 = 119,75 — как на фото «cash=119.75»");
        r.NonCash.Should().Be(0m);
        r.SplitKnown.Should().BeTrue("оплаты долга наличными записаны в смене — способ известен");
        (r.Cash + r.NonCash + r.DebtReduced).Should().Be(574m);
    }

    [Fact]
    public void Return574_DebtPaymentsApplied_ClientNeverGetsMoreThanHePaid()
    {
        // Страховка: к выдаче никогда не больше, чем клиент реально внёс (500 при продаже + 120 оплатами долга).
        var r = RefundSplit.Compute(Check000006(), "debt", 574m, cashOut: null, serverDebtLeft: 454.25m, localDebtPaid: 120m, localDebtCash: 120m);
        (r.Cash + r.NonCash).Should().BeLessThanOrEqualTo(500m + 120m);
        // а после полного возврата чека (1074,25) — всё внесённое (620) и долг 454,25
        var all = RefundSplit.Compute(Check000006(), "debt", 1074.25m, cashOut: null, serverDebtLeft: 454.25m, localDebtPaid: 120m, localDebtCash: 120m);
        all.DebtReduced.Should().Be(454.25m);
        (all.Cash + all.NonCash).Should().Be(620m, "500 при продаже + 120 оплатами долга — все деньги клиента возвращаются");
    }

    // ── 2. строка чека «Погашено позже» ───────────────────────────────────────────────────────────

    [Fact]
    public void DebtPaidLater_ExplainsMissing120()
    {
        // Итог 1 074,25 − внесено 500 − долг сейчас 454,25 = 120 (50 + 50 + 20 оплат долга).
        SaleReceiptCardData.DebtPaidLater(1074.25m, 500m, 454.25m).Should().Be(120m);
    }

    [Theory]
    [InlineData(1074.25, 500, 574.25)] // оплат долга не было — строки нет
    [InlineData(1074.25, 500, 600)]    // остаток сервера больше ожидаемого — строки нет (не выдумываем отрицательное)
    public void DebtPaidLater_NothingToExplain_IsNull(double total, double paid, double debt) =>
        SaleReceiptCardData.DebtPaidLater((decimal)total, (decimal)paid, (decimal)debt).Should().BeNull();

    // ── 3. список «Возвраты» смены ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReturnSplit_NonZeroPartsOnly_NoInternalCode()
    {
        var text = ShiftEventText.ReturnSplit("cash=119.75;noncash=0.00;debt=454.25", 574);
        text.Should().Be("Наличными 119,75 · В счёт долга 454,25");
        text.Should().NotContain("cash=").And.NotContain("noncash").And.NotContain("debt=").And.NotContain("На карту");
    }

    [Fact]
    public void ReturnSplit_ShowsCardWhenReturnedToCard()
    {
        ShiftEventText.ReturnSplit("cash=0.00;noncash=300.00;debt=0.00", 300).Should().Be("На карту 300,00");
        ShiftEventText.ReturnSplit("cash=100.00;noncash=200.00;debt=0.00", 300).Should().Be("Наличными 100,00 · На карту 200,00");
    }

    [Fact]
    public void ReturnSplit_OldRecordWithoutSplit_IsAllCash()
    {
        ShiftEventText.ReturnSplit(null, 250).Should().Be("Наличными 250,00");
    }

    [Fact]
    public void ReturnSplit_StornoAndExchange_AreLabelled()
    {
        ShiftEventText.ReturnSplit("cash=50.00;noncash=0.00;debt=0.00;storno=1", -50).Should().StartWith("Отмена возврата · ");
        ShiftEventText.ReturnSplit("cash=50.00;noncash=0.00;debt=0.00;storno=1;prev=1", -50).Should().StartWith("Отмена возврата прошлой смены");
        ShiftEventText.ReturnSplit("cash=10.00;noncash=0.00;debt=0.00;exchange=1", 10).Should().StartWith("Обмен · ");
    }

    [Fact]
    public void ReturnDetails_HasReceiptGoodsAndSplit()
    {
        var goods = new List<SaleLedgerLine> { new() { Name = "Баранина", Quantity = 0.575 }, new() { Name = "Бука лакым", Quantity = 1 } };
        var text = ShiftEventText.ReturnDetails("cash=119.75;noncash=0.00;debt=454.25", 574, "000006", goods);
        text.Should().Be("Чек № 000006 · Баранина × 0,575, Бука лакым × 1 · Наличными 119,75 · В счёт долга 454,25");
    }

    [Fact]
    public void Goods_MoreThanThree_ShowsCounter()
    {
        var lines = Enumerable.Range(1, 5).Select(i => new SaleLedgerLine { Name = "Т" + i, Quantity = i }).ToList();
        ShiftEventText.Goods(lines).Should().Be("Т1 × 1, Т2 × 2, Т3 × 3 и ещё 2");
    }

    [Fact]
    public void SaleIdFromSource_TakesIdBeforeTimestamp()
    {
        ShiftEventText.SaleIdFromSource("a1b2-c3:20261124151700123").Should().Be("a1b2-c3");
        ShiftEventText.SaleIdFromSource("op:20261124151700123").Should().BeNull();
        ShiftEventText.SaleIdFromSource(null).Should().BeNull();
    }

    [Fact]
    public void FindRefund_PicksTheOneWithSameAmountNearestInTime()
    {
        var ledger = Check000006();
        var at = new DateTimeOffset(2026, 11, 24, 15, 20, 0, TimeSpan.Zero);
        ledger.Refunds.Add(new SaleLedgerRefund { At = at.AddHours(-3), Amount = 574m });
        var near = new SaleLedgerRefund { At = at, Amount = 574m, Lines = [new SaleLedgerLine { Name = "Баранина", Quantity = 0.5 }] };
        ledger.Refunds.Add(near);
        ShiftEventText.FindRefund(ledger, at.UtcDateTime.AddSeconds(2), 574).Should().BeSameAs(near);
        ShiftEventText.FindRefund(ledger, at.UtcDateTime, 100).Should().BeNull("другая сумма — не наш возврат");
        ShiftEventText.FindRefund(null, at.UtcDateTime, 574).Should().BeNull();
    }

    // ── 4. список «Оплата долгов»: Наличными / Безналом / Смешанно ───────────────────────────────

    private static DateTime T(int h, int m, int s = 0) => new(2026, 11, 24, h, m, s, DateTimeKind.Utc);

    // 2026-11-26 (живая проверка): сливаются только две части ОДНОЙ операции «Смешанно» — у них общий идентификатор в source_id (ShiftEventText.DebtSourceId).
    private static string Src(string id, string? mix = null) => ShiftEventText.DebtSourceId("pay-debt:" + id, mix);

    [Fact]
    public void DebtRows_EachPaymentHasMethod()
    {
        var rows = ShiftEventText.BuildDebtRows(
        [
            (T(13, 55), 50, "Нан", false, Src("1")), (T(13, 56), 50, "Нан", false, Src("2")), (T(15, 14), 20, "Нан", false, Src("3")), (T(15, 40), 100, "Нан", true, Src("4")),
        ]);
        rows.Select(r => r.Details).Should().Equal("Нан · Наличными", "Нан · Наличными", "Нан · Наличными", "Нан · Безналом");
        rows.Sum(r => r.Amount).Should().Be(220);
        rows.Any(r => r.IsMixed).Should().BeFalse("50 + 50 наличными подряд — это две оплаты, а не смешанная");
    }

    [Fact]
    public void DebtRows_CashAndNonCashInOneGo_AreOneMixedRow()
    {
        // 40 наличными + 60 безналом за один раз: два взноса сервера подряд с разницей в секунды.
        var rows = ShiftEventText.BuildDebtRows([(T(16, 0, 0), 40, "Нан", false, Src("a", "g1")), (T(16, 0, 7), 60, "Нан", true, Src("b", "g1"))]);
        rows.Should().ContainSingle();
        rows[0].Amount.Should().Be(100);
        rows[0].Cash.Should().Be(40);
        rows[0].NonCash.Should().Be(60);
        rows[0].IsMixed.Should().BeTrue();
        rows[0].Details.Should().Be("Нан · Смешанно: наличными 40,00 + безналом 60,00");
    }

    [Fact]
    public void DebtRows_NonCashFirstThenCash_IsAlsoMixed_AndOtherClientIsNot()
    {
        var rows = ShiftEventText.BuildDebtRows([(T(16, 0, 0), 60, "Нан", true, Src("a", "g1")), (T(16, 0, 5), 40, "Нан", false, Src("b", "g1"))]);
        rows.Should().ContainSingle().Which.IsMixed.Should().BeTrue();
        var other = ShiftEventText.BuildDebtRows([(T(16, 0, 0), 60, "Нан", true, Src("a", "g1")), (T(16, 0, 5), 40, "Айбек", false, Src("b", "g1"))]);
        other.Should().HaveCount(2, "разные клиенты — не смешанная оплата");
    }

    [Fact]
    public void DebtRows_TwoSeparatePayments_WithinMinute_AreTwoRows_NotMixed()
    {
        // Живая проверка: наличные 100 и безнал 100 за минуту — это ДВЕ отдельные оплаты (кассир нажимал «Наличные», потом «Безнал»), а не одна «Смешанно».
        var rows = ShiftEventText.BuildDebtRows([(T(16, 0, 0), 100, "Нан", false, Src("a")), (T(16, 0, 40), 100, "Нан", true, Src("b"))]);
        rows.Should().HaveCount(2);
        rows.Any(r => r.IsMixed).Should().BeFalse();
        rows.Select(r => r.Details).Should().Equal("Нан · Наличными", "Нан · Безналом");
        // записи без источника (старые, до этой правки) — тоже отдельные строки
        ShiftEventText.BuildDebtRows([(T(16, 0, 0), 100, "Нан", false, null), (T(16, 0, 5), 100, "Нан", true, null)]).Should().HaveCount(2);
    }

    [Fact]
    public void DebtRows_DifferentMixGroups_AreNotMerged_AndLonelyPartStaysPlain()
    {
        // Две смешанные оплаты подряд — по строке на каждую; у второй безнал не принят (нет второй части) — строка «Наличными».
        var rows = ShiftEventText.BuildDebtRows(
        [
            (T(16, 0, 0), 40, "Нан", false, Src("a", "g1")), (T(16, 0, 3), 60, "Нан", true, Src("b", "g1")),
            (T(16, 0, 20), 30, "Нан", false, Src("c", "g2")),
        ]);
        rows.Select(r => r.Details).Should().Equal("Нан · Смешанно: наличными 40,00 + безналом 60,00", "Нан · Наличными");
    }

    [Fact]
    public void MixMarker_RoundTrips_InSourceId()
    {
        ShiftEventText.MixGroupOf(ShiftEventText.DebtSourceId("pay-debt:k1", "abc")).Should().Be("abc");
        ShiftEventText.MixGroupOf("pay-debt:k1").Should().BeNull();
        ShiftEventText.MixGroupOf(null).Should().BeNull();
        ShiftEventText.DebtSourceId("pay-debt:k1", null).Should().Be("pay-debt:k1");
    }

    [Theory]
    [InlineData(0.0, "0,00")]
    [InlineData(0.004, "0,00")]
    [InlineData(-0.001, "0,00")]
    [InlineData(454.25, "−454,25")]
    public void DebtMinus_NoMinusBeforeZero(double value, string expected)
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        try { RefundSplit.DebtMinus((decimal)value).Should().Be(expected); }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(100, 60, true, 40, 60)]
    [InlineData(100, 99.99, true, 0.01, 99.99)]
    [InlineData(100, 0, false, 0, 0)]       // безнала нет — это не смешанная
    [InlineData(100, 100, false, 0, 100)]   // всё безналом — не смешанная
    [InlineData(100, 150, false, 0, 150)]   // безнала больше общей суммы
    [InlineData(100, -5, false, 0, 0)]
    public void TrySplitMixed_NonCashStrictlyBetweenZeroAndTotal(double total, double non, bool ok, double cash, double nonOut)
    {
        ShiftEventText.TrySplitMixed((decimal)total, (decimal)non, out var c, out var n).Should().Be(ok);
        if (ok)
        {
            c.Should().Be((decimal)cash);
            n.Should().Be((decimal)nonOut);
            (c + n).Should().Be((decimal)total);
        }
    }

    // ── 5. защита от повторной оплаты той же суммы ────────────────────────────────────────────────

    [Fact]
    public void FindRecentSamePayment_SameClientSameAmountWithinWindow_IsFound()
    {
        var events = new[] { (T(13, 55), 50.0, (string?)"Нан", false) };
        ShiftEventText.FindRecentSamePayment(events, "Нан", 50, T(13, 56), TimeSpan.FromMinutes(5)).Should().Be(T(13, 55));
    }

    [Fact]
    public void FindRecentSamePayment_OtherAmountOtherClientOrOld_IsNotFound()
    {
        var events = new[] { (T(13, 55), 50.0, (string?)"Нан", false) };
        ShiftEventText.FindRecentSamePayment(events, "Нан", 20, T(13, 56), TimeSpan.FromMinutes(5)).Should().BeNull();
        ShiftEventText.FindRecentSamePayment(events, "Айбек", 50, T(13, 56), TimeSpan.FromMinutes(5)).Should().BeNull();
        ShiftEventText.FindRecentSamePayment(events, "Нан", 50, T(14, 30), TimeSpan.FromMinutes(5)).Should().BeNull();
    }

    // ── 6. подписи на 5 языках ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Labels_AreFilledInAllFiveLanguages()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            ShiftEventText.MixedLabel.Should().NotBeNullOrWhiteSpace();
            ShiftEventText.NonCashLabel.Should().NotBeNullOrWhiteSpace();
            var text = ShiftEventText.ReturnSplit("cash=1.00;noncash=2.00;debt=3.00;storno=1", 6);
            text.Should().NotContain("cash=");
            SaleReceiptCardData.DebtPaidLaterLabel.Should().NotBeNullOrWhiteSpace();
        }

        UserPreferences.Instance.Language = AppLanguage.Russian;
    }
}
