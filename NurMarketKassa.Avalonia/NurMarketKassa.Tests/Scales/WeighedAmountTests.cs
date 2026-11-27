using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-26 (владелец, фото клиента 24.11: «ввожу 1000, в чеке 999,90»; ответ Умара: строка весового товара принимает line_total): «По сумме» в окне «Взвесить» —
/// чек ровно на введённую сумму, вес до грамма. Тесты меняют общие статические настройки (признак сервера, шаг весов) — не параллельны другим.</summary>
[Collection("ServerFeatures")]
public sealed class WeighedAmountTests : IDisposable
{
    private const string Pid = "7d3b8b0e-5a54-4d61-8f33-0d2b4f5b9c11";
    private readonly CartService _cart = new();
    private readonly Func<bool> _savedEnabled = WeighedAmount.Enabled;
    private readonly Func<bool> _savedFloor = QuantityRules.WeightLineFloorEnabled;
    private readonly Func<decimal> _savedStep = QuantityRules.WeightStep;

    public WeighedAmountTests()
    {
        WeighedAmount.Enabled = () => true;
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 0.01m;
    }

    public void Dispose()
    {
        WeighedAmount.Enabled = _savedEnabled;
        QuantityRules.WeightLineFloorEnabled = _savedFloor;
        QuantityRules.WeightStep = _savedStep;
        _cart.Dispose();
    }

    [Theory]
    [InlineData(1000, 1100, "0.909")]
    [InlineData(1000, 870, "1.149")]
    [InlineData(500, 333, "1.502")]
    [InlineData(100, 120, "0.833")]
    [InlineData(1, 1100, "0.001")]
    [InlineData(300000, 1100, "272.727")] // вес больше 200 кг
    [InlineData(0.01, 0.01, "1")] // самая маленькая цена
    public void Plan_WithServerFlag_ReceiptIsExactlyTheEnteredAmount(decimal amount, decimal price, string kg)
    {
        var plan = WeighedAmount.Plan(amount, price, exactAllowed: true, step: 0.01m)!;

        plan.Kg.Should().Be(decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture));
        plan.LineTotal.Should().Be(amount);
        plan.ExactLine.Should().BeTrue();
        decimal.Round(plan.Kg, 3).Should().Be(plan.Kg, "вес — до грамма");
    }

    [Theory]
    [InlineData(1000, 1100, "999.90")]
    [InlineData(1000, 870, "999.63")]
    public void Plan_WithoutServerFlag_ShowsHonestWeightTimesPrice(decimal amount, decimal price, string total)
    {
        var plan = WeighedAmount.Plan(amount, price, exactAllowed: false, step: 0.01m)!;

        plan.ExactLine.Should().BeFalse();
        plan.LineTotal.Should().Be(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Plan_UnderOneGram_IsNull()
    {
        WeighedAmount.Plan(0.5m, 1100m, exactAllowed: true, step: 0.01m).Should().BeNull();
        WeighedAmount.Plan(100m, 0m, exactAllowed: true, step: 0.01m).Should().BeNull();
    }

    [Fact]
    public void Plan_ScaleStepInSom_RequiresWholeAmount()
    {
        WeighedAmount.Plan(1000m, 1100m, exactAllowed: true, step: 1m)!.ExactLine.Should().BeTrue();
        // 1000,50 при шаге в сомах сервер отвечает 400 line_total_step — точную сумму не шлём, показываем честную (целую) сумму
        var plan = WeighedAmount.Plan(1000.50m, 1100m, exactAllowed: true, step: 1m)!;
        plan.ExactLine.Should().BeFalse();
        (plan.LineTotal % 1m).Should().Be(0m);
    }

    [Fact]
    public void AddWeighedAmount_CartLineIsExactlyTheEnteredAmount()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);

        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        _cart.LineCount.Should().Be(1);
        _cart.TotalAmount.Should().Be(1000m);
        var line = _cart.Items.Should().ContainSingle().Subject;
        line.Quantity.Should().Be(0.909);
        line.LineTotal.Should().Be(1000m);
        line.UnitPrice.Should().Be(1100m, "цену за кг в кассе не трогаем — цену из суммы ставит сервер");
    }

    [Fact]
    public void AddWeighedAmount_WithoutServerFlag_IsRefused_AndOldWayGivesWeightTimesPrice()
    {
        WeighedAmount.Enabled = () => false;
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);

        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeFalse();
        _cart.AddItem(product, 0.909);

        _cart.Items.Single().LineTotal.Should().Be(999.90m);
    }

    [Fact]
    public void AddWeighedAmount_SameProductTwice_OneLine_AmountsAdd()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);

        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        _cart.LineCount.Should().Be(1);
        _cart.TotalAmount.Should().Be(2000m);
        _cart.Items.Single().Quantity.Should().Be(1.818);
    }

    [Fact]
    public void AddWeighedAmount_ToLineAddedByWeight_MergesIntoOneLine()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        _cart.AddItem(product, 0.5); // 550,00

        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        _cart.LineCount.Should().Be(1);
        _cart.TotalAmount.Should().Be(1550m);
        _cart.Items.Single().Quantity.Should().Be(1.409);
    }

    [Fact]
    public void WeightAddedToAmountLine_GrowsAmountByWeightTimesPrice()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        _cart.AddItem(product, 0.5);

        _cart.LineCount.Should().Be(1);
        _cart.TotalAmount.Should().Be(1550m);
    }

    [Fact]
    public void ReweighByWeight_DropsExactAmount()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        _cart.UpdateQuantity(_cart.Items.Single().Id!, 0.909);

        _cart.TotalAmount.Should().Be(999.90m);
    }

    [Fact]
    public void LineDiscount_DropsExactAmount_ServerRefusesDiscountInExactLine()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        ReceiptSnapshotCartEditor.PatchLineDiscount(_cart, _cart.Items.Single().Id!, "sum", "10");

        _cart.TotalAmount.Should().Be(989.90m);
        var body = QuickCheckoutBody.TryBuild(_cart.GetRawText(), "cash", "1000", null, null, null, null, false, null, false, out _)!;
        body["items"]![0]!["line_total"].Should().BeNull();
        body["items"]![0]!["discount"]!.GetValue<string>().Should().Be("10.00");
    }

    [Fact]
    public void ReweighInAmountMode_ReplacesWeightAndAmount()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        ReceiptSnapshotCartEditor.SetLineWeighedAmount(_cart, _cart.Items.Single().Id!, 0.455, 500m).Should().BeTrue();

        _cart.TotalAmount.Should().Be(500m);
        _cart.Items.Single().Quantity.Should().Be(0.455);
    }

    [Fact]
    public void CheckoutBody_ExactLine_SendsLineTotal_NoDiscount_AndTotalMatches()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();

        var body = QuickCheckoutBody.TryBuild(_cart.GetRawText(), "cash", "1000", null, null, null, null, false, null, false, out _)!;

        var line = (JsonObject)body["items"]![0]!;
        line["qty"]!.GetValue<string>().Should().Be("0.909");
        line["line_total"]!.GetValue<string>().Should().Be("1000.00");
        line["discount"].Should().BeNull();
        QuickCheckoutBody.ComputeTotal(body).Should().Be(1000.0);
    }

    [Fact]
    public void CheckoutBody_WithoutServerFlag_IsWeightTimesPrice()
    {
        CartTestHelper.StartEmptyCart(_cart);
        var product = CartTestHelper.CreateProduct(Pid, "Бука лакым", 1100m, mustWeigh: true);
        ReceiptSnapshotCartEditor.AddWeighedAmount(_cart, product, 0.909, 1000m).Should().BeTrue();
        WeighedAmount.Enabled = () => false; // сервер/офлайн: признака нет

        var body = QuickCheckoutBody.TryBuild(_cart.GetRawText(), "cash", "1000", null, null, null, null, false, null, false, out _)!;

        body["items"]![0]!["line_total"].Should().BeNull();
        QuickCheckoutBody.ComputeTotal(body).Should().Be(999.9);
    }
}
