using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты скидок и бонусов: разбор скидки руками (OrderDiscountHelper), округление скидки чека в CartTotalsCalculator
/// (примеры из опытов на сервере NBS), локальные бонусы клиента (ClientLoyaltyStore → SQLite во временной папке), окно оплаты (CheckoutViewModel:
/// скидка процентом/суммой, списание бонусов не больше баланса и не больше суммы к оплате, тело скидки для сервера).
/// Настройки (LoyaltyEnabled) возвращаются на место; коллекция общая с другими тестами UserPreferences.</summary>
[Collection("UserPreferences")]
public sealed class DiscountsAndBonusesTests : IDisposable
{
    private readonly bool _savedLoyalty = UserPreferences.Instance.LoyaltyEnabled;

    public void Dispose() => UserPreferences.Instance.LoyaltyEnabled = _savedLoyalty;

    private static string Id() => "w1-" + Guid.NewGuid().ToString("N")[..10];

    // ───────────── OrderDiscountHelper ─────────────

    [Theory]
    [InlineData("12,5", true, OrderDiscountHelper.DiscountParse.Ok, "12.5")]
    [InlineData("12.5 %", true, OrderDiscountHelper.DiscountParse.Ok, "12.5")]
    [InlineData("0,50", false, OrderDiscountHelper.DiscountParse.Ok, "0.5")]  // раньше «0,50» применялось как 50 (баг Д-1)
    [InlineData("100", false, OrderDiscountHelper.DiscountParse.Ok, "100")]
    [InlineData("", false, OrderDiscountHelper.DiscountParse.Empty, "0")]
    [InlineData("   ", true, OrderDiscountHelper.DiscountParse.Empty, "0")]
    [InlineData("%", true, OrderDiscountHelper.DiscountParse.Empty, "0")]
    [InlineData("-5", false, OrderDiscountHelper.DiscountParse.Negative, "0")]
    [InlineData("−5", false, OrderDiscountHelper.DiscountParse.Negative, "0")] // типографский минус
    [InlineData("1,234", false, OrderDiscountHelper.DiscountParse.TooManyDecimals, "0")]
    [InlineData("abc", false, OrderDiscountHelper.DiscountParse.Invalid, "0")]
    [InlineData("1e3", false, OrderDiscountHelper.DiscountParse.Invalid, "0")]
    public void TryParseValue_ByCases(string text, bool percent, OrderDiscountHelper.DiscountParse expected, string value)
    {
        OrderDiscountHelper.TryParseValue(text, percent, out var parsed).Should().Be(expected);
        parsed.Should().Be(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("0,00", true)]
    [InlineData("0.0", true)]
    [InlineData("5", false)]
    [InlineData("abc", false)] // не число — не «пусто» (дальше его отвергнет проверка)
    public void IsEmptyOrZeroLike_ByCases(string? raw, bool expected) =>
        OrderDiscountHelper.IsEmptyOrZeroLike(raw).Should().Be(expected);

    [Fact]
    public void BuildPatchBody_OnlyOneFieldOfDiscount()
    {
        OrderDiscountHelper.BuildPatchBody(true, "10,5").Should().Equal(new Dictionary<string, string> { ["order_discount_percent"] = "10.5" });
        OrderDiscountHelper.BuildPatchBody(false, "150").Should().Equal(new Dictionary<string, string> { ["order_discount_total"] = "150" });
        OrderDiscountHelper.BuildPatchBody(true, "0").Should().BeEmpty("нулевая скидка — «не задана»");
        OrderDiscountHelper.BuildPatchBody(false, null).Should().BeEmpty();
    }

    [Fact]
    public void BuildClearPatchBody_ClearsOnlyTheFieldUsedBefore_NeverBoth()
    {
        OrderDiscountHelper.BuildClearPatchBody("10", null).Should().Equal(new Dictionary<string, string> { ["order_discount_percent"] = "0" });
        OrderDiscountHelper.BuildClearPatchBody(null, "50").Should().Equal(new Dictionary<string, string> { ["order_discount_total"] = "0" });
        OrderDiscountHelper.BuildClearPatchBody("10", "50").Should().HaveCount(1, "сервер принимает ровно одно поле скидки");
        OrderDiscountHelper.BuildClearPatchBody("", "0").Should().BeEmpty();
    }

    [Fact]
    public void SanitizePatchBody_KeepsAtMostOneField_PercentWins()
    {
        var both = new Dictionary<string, string> { ["order_discount_percent"] = "5,5", ["order_discount_total"] = "100" };
        OrderDiscountHelper.SanitizePatchBody(both).Should().Equal(new Dictionary<string, string> { ["order_discount_percent"] = "5.5" });

        var zeroPercent = new Dictionary<string, string> { ["order_discount_percent"] = "0", ["order_discount_total"] = "100" };
        OrderDiscountHelper.SanitizePatchBody(zeroPercent).Should().Equal(new Dictionary<string, string> { ["order_discount_total"] = "100" });

        OrderDiscountHelper.SanitizePatchBody(zeroPercent, isClear: true).Should().Equal(new Dictionary<string, string> { ["order_discount_percent"] = "0" },
            "при сбросе нулевое поле — как раз то, что нужно отправить");

        OrderDiscountHelper.SanitizePatchBody(new Dictionary<string, string>()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("50", true)]
    [InlineData("100", true)]
    [InlineData("100,5", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    public void ValidatePercent_ErrorOnlyWhenWrongOrAbove100(string raw, bool ok) =>
        (OrderDiscountHelper.ValidatePercent(raw) is null).Should().Be(ok);

    [Theory]
    [InlineData("0", true)]
    [InlineData("12,5", true)]
    [InlineData("", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    public void ValidateSum_ByCases(string raw, bool ok) =>
        (OrderDiscountHelper.ValidateSum(raw) is null).Should().Be(ok);

    // ───────────── округление скидки чека (примеры сервера NBS) ─────────────

    private static CartTotalsCalculator.CartTotals Calc(decimal price, double qty = 1, Action<JsonObject>? cart = null, Action<JsonObject>? line = null)
    {
        var item = new JsonObject { ["product_id"] = "p1", ["quantity"] = qty, ["unit_price"] = (double)price, ["server_is_weight"] = false };
        line?.Invoke(item);
        var root = new JsonObject { ["items"] = new JsonArray(item) };
        cart?.Invoke(root);
        using var doc = JsonDocument.Parse(root.ToJsonString());
        return CartTotalsCalculator.Calculate(doc.RootElement);
    }

    [Theory]
    [InlineData("17.00", 2.5, "0.43", "16.57")]  // 0,425 → 0,43 (половина вверх)
    [InlineData("5.00", 2.5, "0.13", "4.87")]    // 0,125 → 0,13
    [InlineData("1.425", 50, "0.72", "0.71")]    // подытог 1,43, 50 % = 0,715 → 0,72, итог 0,71 (чек 1977 NBS)
    [InlineData("100.00", 10, "10.00", "90.00")]
    [InlineData("100.00", 100, "100.00", "0.00")]
    public void OrderDiscountPercent_RoundsOnce_HalfUp(string price, double percent, string discount, string total)
    {
        var t = Calc(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), cart: c => c["order_discount_percent"] = percent);

        ((decimal)t.OrderDiscount).Should().Be(decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture));
        ((decimal)t.TotalDue).Should().Be(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OrderDiscountSum_IsCappedByReceiptAmount()
    {
        var t = Calc(30m, cart: c => c["order_discount_total"] = 50.0);
        t.OrderDiscount.Should().Be(30);
        t.TotalDue.Should().Be(0);
    }

    [Fact]
    public void LineDiscount_ReducesTotal_AndPercentOfOrderCountsFromReducedBase()
    {
        // 2 × 10 = 20, скидка строки 3 → 17; скидка чека 10 % от (20 − 3) = 1,70 → итог 15,30
        var t = Calc(10m, qty: 2, cart: c => c["order_discount_percent"] = 10.0, line: l => l["discount_total"] = 3.0);

        t.Subtotal.Should().Be(20);
        t.LineDiscounts.Should().Be(3);
        t.OrderDiscount.Should().Be(1.7);
        t.TotalDue.Should().Be(15.3);
    }

    [Fact]
    public void LineDiscountPercent_IsTakenFromExactLineAmount()
    {
        // 3 × 33,33 = 99,99; 10 % = 9,999 → 10,00; итог 89,99
        var t = Calc(33.33m, qty: 3, line: l => l["discount_percent"] = 10.0);

        t.LineDiscounts.Should().Be(10.00);
        t.TotalDue.Should().Be(89.99);
    }

    [Fact]
    public void DiscountBiggerThanReceipt_TotalNeverNegative_ButRawTotalShowsTheOverrun()
    {
        var t = Calc(10m, line: l => l["discount_total"] = 25.0);

        t.TotalDue.Should().Be(0);
        t.RawTotal.Should().Be(-15, "RawTotal нужен, чтобы блокировать оплату нонсенсного чека");
    }

    // ───────────── локальные бонусы клиента ─────────────

    [Fact]
    public void Loyalty_AdjustBalance_AccrueAndRedeem_NeverBelowZero()
    {
        var client = Id();
        ClientLoyaltyStore.GetBalance(client).Should().Be(0);

        ClientLoyaltyStore.AdjustBalance(client, 120.5);
        ClientLoyaltyStore.AdjustBalance(client, -20.5);
        ClientLoyaltyStore.GetBalance(client).Should().Be(100);

        ClientLoyaltyStore.AdjustBalance(client, -500);
        ClientLoyaltyStore.GetBalance(client).Should().Be(0, "баланс никогда не уходит ниже нуля");
    }

    [Fact]
    public void Loyalty_EmptyClientOrZeroDelta_DoesNothing()
    {
        ClientLoyaltyStore.AdjustBalance("", 100);
        ClientLoyaltyStore.AdjustBalance("  ", 100);
        ClientLoyaltyStore.GetBalance("").Should().Be(0);

        var client = Id();
        ClientLoyaltyStore.AdjustBalance(client, 0);
        ClientLoyaltyStore.GetBalance(client).Should().Be(0);
    }

    [Fact]
    public void Loyalty_RecordTransaction_RemainingForSale_IsTheNetChange_AndRecordedOnce()
    {
        var client = Id();
        var sale = Id();
        ClientLoyaltyStore.RemainingForSale(sale).Should().Be(0, "записи нет");

        ClientLoyaltyStore.RecordTransaction(sale, client, 25);
        ClientLoyaltyStore.RecordTransaction(sale, client, 999); // повтор для той же продажи — no-op

        ClientLoyaltyStore.RemainingForSale(sale).Should().Be(25);
    }

    [Fact]
    public void Loyalty_FullReturn_ReversesAccruedPoints_Once()
    {
        var client = Id();
        var sale = Id();
        ClientLoyaltyStore.AdjustBalance(client, 40);
        ClientLoyaltyStore.RecordTransaction(sale, client, 40); // начислено 40 за продажу

        DatabaseService.Instance.ReverseRemainingClientLoyaltyForSale(sale).Should().BeTrue();
        ClientLoyaltyStore.GetBalance(client).Should().Be(0, "возврат чека снимает начисленные за него баллы");
        ClientLoyaltyStore.RemainingForSale(sale).Should().Be(0);

        DatabaseService.Instance.ReverseRemainingClientLoyaltyForSale(sale).Should().BeTrue();
        ClientLoyaltyStore.GetBalance(client).Should().Be(0, "повтор ничего не меняет");
        DatabaseService.Instance.ReverseRemainingClientLoyaltyForSale(Id()).Should().BeFalse("для продажи без записи — false");
    }

    [Fact]
    public void Loyalty_FullReturn_GivesBackRedeemedPoints()
    {
        var client = Id();
        var sale = Id();
        ClientLoyaltyStore.AdjustBalance(client, 100);
        ClientLoyaltyStore.AdjustBalance(client, -30); // списано 30 на оплату
        ClientLoyaltyStore.RecordTransaction(sale, client, -30);
        ClientLoyaltyStore.RemainingForSale(sale).Should().Be(-30);

        DatabaseService.Instance.ReverseRemainingClientLoyaltyForSale(sale);

        ClientLoyaltyStore.GetBalance(client).Should().Be(100);
    }

    [Fact]
    public void ShiftAdjustments_AreSummedPerShift_AndRepeatForSameSaleIsIgnored()
    {
        var shift = Id();
        var saleA = Id();
        ClientLoyaltyStore.RecordSaleAdjustment(saleA, shift, discountTotal: 50, pointsRedeemed: 20);
        ClientLoyaltyStore.RecordSaleAdjustment(saleA, shift, 999, 999); // повтор досылки очереди
        ClientLoyaltyStore.RecordSaleAdjustment(Id(), shift, 10, 0);
        ClientLoyaltyStore.RecordSaleAdjustment(Id(), shift, 0, 0);      // пустую запись не пишем
        ClientLoyaltyStore.RecordSaleAdjustment(Id(), Id(), 7, 7);       // чужая смена

        var (discounts, points, receipts) = ClientLoyaltyStore.AdjustmentsForShift(shift);

        discounts.Should().Be(60);
        points.Should().Be(20);
        receipts.Should().Be(2);
        ClientLoyaltyStore.AdjustmentsForShift(null).Should().Be((0d, 0d, 0));
    }

    // ───────────── окно оплаты ─────────────

    private static CheckoutViewModel Vm(double total)
    {
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = total, TotalDue = total, LineCount = 1 };
        return new CheckoutViewModel(totals, "", "");
    }

    [Fact]
    public void Checkout_PercentDiscount_ReducesTotal_AndBodyHasOnlyPercentField()
    {
        var vm = Vm(1000);
        vm.IsDiscountPercent = true;
        vm.DiscountInput = "10";

        vm.OrderDiscountAmount.Should().Be(100);
        vm.EffectiveTotalDue.Should().Be(900);
        vm.PendingOrderDiscountBody.Should().Equal(new Dictionary<string, string> { ["order_discount_percent"] = "10" });
    }

    [Fact]
    public void Checkout_SumDiscount_IsCappedByReceipt_PercentOver100IsCapped()
    {
        var vm = Vm(100);
        vm.IsDiscountSum = true;
        vm.DiscountInput = "150";
        vm.OrderDiscountAmount.Should().Be(100, "скидка суммой не больше суммы чека");
        vm.EffectiveTotalDue.Should().Be(0);

        vm.IsDiscountPercent = true;
        vm.DiscountInput = "250";
        vm.OrderDiscountAmount.Should().Be(100, "процент ограничен 100");
    }

    [Fact]
    public void Checkout_NoDiscountTouched_NoBodyForServer()
    {
        var vm = Vm(500);
        vm.PendingOrderDiscountBody.Should().BeNull();
        vm.EffectiveTotalDue.Should().Be(500);
    }

    [Fact]
    public void Checkout_Bonuses_AreCappedByClientBalance_AndSentAsOneSum()
    {
        UserPreferences.Instance.LoyaltyEnabled = true;
        var client = Id();
        ClientLoyaltyStore.AdjustBalance(client, 300);
        var vm = Vm(1000);
        vm.PreselectClient(new ClientOption { Id = client, DisplayName = "Тест · 0700000000" });

        vm.PointsToRedeemInput = "500"; // просят больше баланса

        vm.PointsRedeemed.Should().Be(300);
        vm.EffectiveTotalDue.Should().Be(700);
        // бонусы — деньги, не процент: одна сумма в order_discount_total
        vm.PendingOrderDiscountBody.Should().Equal(new Dictionary<string, string> { ["order_discount_total"] = "300.00" });
    }

    [Fact]
    public void Checkout_Bonuses_AreCappedByAmountToPay_TotalNeverNegative()
    {
        UserPreferences.Instance.LoyaltyEnabled = true;
        var client = Id();
        ClientLoyaltyStore.AdjustBalance(client, 500);
        var vm = Vm(200);
        vm.PreselectClient(new ClientOption { Id = client, DisplayName = "Тест · 0700000001" });

        vm.PointsToRedeemInput = "500";

        vm.PointsRedeemed.Should().Be(200);
        vm.EffectiveTotalDue.Should().Be(0);
    }

    [Fact]
    public void Checkout_Bonuses_AddToOrderDiscountInOneSum()
    {
        UserPreferences.Instance.LoyaltyEnabled = true;
        var client = Id();
        ClientLoyaltyStore.AdjustBalance(client, 50);
        var vm = Vm(1000);
        vm.PreselectClient(new ClientOption { Id = client, DisplayName = "Тест · 0700000002" });
        vm.IsDiscountPercent = true;
        vm.DiscountInput = "10"; // 100 сом

        vm.PointsToRedeemInput = "50";

        vm.EffectiveTotalDue.Should().Be(850);
        vm.PendingOrderDiscountBody.Should().Equal(new Dictionary<string, string> { ["order_discount_total"] = "150.00" },
            "обычная скидка + бонусы уходят на сервер одной суммой, а не процентом");
    }

    [Fact]
    public void Checkout_Bonuses_IgnoredWhenLoyaltyOffOrNoClient()
    {
        UserPreferences.Instance.LoyaltyEnabled = false;
        var client = Id();
        ClientLoyaltyStore.AdjustBalance(client, 300);
        var vm = Vm(1000);
        vm.PreselectClient(new ClientOption { Id = client, DisplayName = "Тест" });
        vm.PointsToRedeemInput = "100";
        vm.PointsRedeemed.Should().Be(0);

        UserPreferences.Instance.LoyaltyEnabled = true;
        var noClient = Vm(1000);
        noClient.PointsToRedeemInput = "100";
        noClient.PointsRedeemed.Should().Be(0);
    }
}
