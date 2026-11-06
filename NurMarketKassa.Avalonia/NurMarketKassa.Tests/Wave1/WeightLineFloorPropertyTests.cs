using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — property-тест «каждая весовая строка вниз» (CsCheck) по правилу сервера weight_line_total_floor (сервер ceba67c):
/// line_total КАЖДОЙ весовой строки (товар с is_weight) = FLOOR(цена × вес) до шага весов (0,01 при «tiyin», 1 при «som» — серверная единица суммы компании),
/// подытог = сумма строк; обычные (не весовые) строки — сумма без округления строк и половина вверх от СУММЫ.
/// Числа строятся целыми (тысячные доли веса, копейки цены), независимая модель — в decimal. Проверяется и CartTotalsCalculator, и тело быстрой оплаты
/// (QuickCheckoutBody.ComputeTotal), чтобы итог окна оплаты и итог запроса не расходились. 10 000+ случайных чеков суммарно.
/// Статические правила (WeightLineFloorEnabled / WeightStep) меняются — коллекция общая с другими тестами признаков сервера, правила возвращаются.</summary>
[Collection("ServerFeatures")]
public sealed class WeightLineFloorPropertyTests : IDisposable
{
    public WeightLineFloorPropertyTests()
    {
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 0.01m;
    }

    public void Dispose()
    {
        QuantityRules.WeightLineFloorEnabled = () => ServerCapabilities.Has(ServerCapabilities.WeightLineTotalFloor);
        QuantityRules.WeightStep = QuantityRules.DefaultWeightStep;
    }

    private readonly record struct Line(int QtyMilli, int PriceCents, bool Weighed)
    {
        public double Qty => QtyMilli / 1000.0;
        public double Price => PriceCents / 100.0;
        public decimal Exact => QtyMilli / 1000m * (PriceCents / 100m);
    }

    // вес 0,001–20 кг, цена 0,01–5000 сом
    private static readonly Gen<Line> WeighedGen = Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], (q, p) => new Line(q, p, true));
    private static readonly Gen<Line> AnyGen = Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], Gen.Bool, (q, p, w) => new Line(q, p, w));

    /// <summary>Как бывает в кассе: штучная строка — целое количество (в тело запроса оно уходит целым: PieceText), весовая — любое до грамма.</summary>
    private static readonly Gen<Line> RealisticGen = Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], Gen.Bool,
        (q, p, w) => new Line(w ? q : (q % 20 + 1) * 1000, p, w));

    private static string CartJson(IEnumerable<Line> lines)
    {
        var items = new JsonArray();
        var i = 0;
        foreach (var l in lines)
        {
            items.Add(new JsonObject
            {
                ["product_id"] = "00000000-0000-0000-0000-" + (i++).ToString("D12"),
                ["quantity"] = l.Qty,
                ["unit_price"] = l.Price,
                ["server_is_weight"] = l.Weighed,
                ["is_weight"] = l.Weighed,
            });
        }
        return new JsonObject { ["items"] = items }.ToJsonString();
    }

    private static CartTotalsCalculator.CartTotals Calc(IEnumerable<Line> lines)
    {
        using var doc = JsonDocument.Parse(CartJson(lines));
        return CartTotalsCalculator.Calculate(doc.RootElement);
    }

    private static decimal FloorToStep(decimal value, decimal step) => Math.Floor(value / step) * step;

    /// <summary>Независимая модель правила сервера: весовая строка вниз до шага, остальные — сумма и половина вверх.</summary>
    private static decimal ServerSubtotal(IReadOnlyCollection<Line> lines, decimal step) =>
        lines.Where(l => l.Weighed).Sum(l => FloorToStep(l.Exact, step))
        + Math.Round(lines.Where(l => !l.Weighed).Sum(l => l.Exact), 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void EveryWeighedLine_RoundsDown_ToCopeck_SubtotalIsSumOfLines() =>
        WeighedGen.List[1, 8].Sample(lines =>
        {
            var totals = Calc(lines);
            var expected = lines.Sum(l => FloorToStep(l.Exact, 0.01m));
            ((decimal)totals.Subtotal).Should().Be(expected);
            ((decimal)totals.TotalDue).Should().Be(expected);
            // не больше точной суммы и меньше неё не больше чем на копейку на строку: «вниз», а не «куда попало»
            var exact = lines.Sum(l => l.Exact);
            ((decimal)totals.Subtotal).Should().BeLessThanOrEqualTo(exact);
            (exact - (decimal)totals.Subtotal).Should().BeLessThan(0.01m * lines.Count);
        }, iter: 3000);

    [Fact]
    public void EveryWeighedLine_RoundsDown_ToWholeSom_WhenServerUnitIsSom()
    {
        QuantityRules.WeightStep = () => 1m;
        WeighedGen.List[1, 8].Sample(lines =>
        {
            var totals = Calc(lines);
            var expected = lines.Sum(l => FloorToStep(l.Exact, 1m));
            ((decimal)totals.Subtotal).Should().Be(expected);
            ((decimal)totals.Subtotal % 1m).Should().Be(0m, "при единице «сом» весовая строка — целое число сомов");
        }, iter: 3000);
    }

    [Fact]
    public void MixedLines_WeighedPerLineDown_PlusOthersHalfUpOfSum() =>
        AnyGen.List[1, 8].Sample(lines =>
        {
            var totals = Calc(lines);
            ((decimal)totals.Subtotal).Should().Be(ServerSubtotal(lines, 0.01m));
            ((decimal)totals.TotalDue).Should().Be(ServerSubtotal(lines, 0.01m));
        }, iter: 3000);

    [Fact]
    public void Floor_IsNeverMoreThanTheOldRule_FloorOfSum() =>
        // правило «каждая строка вниз» всегда не больше прежнего «вниз от суммы» (сумма «вниз» ≤ «вниз» суммы): касса никогда не берёт больше сервера
        WeighedGen.List[1, 8].Sample(lines =>
        {
            var perLine = (decimal)Calc(lines).Subtotal;
            QuantityRules.WeightLineFloorEnabled = () => false;
            var oldRule = (decimal)Calc(lines).Subtotal;
            QuantityRules.WeightLineFloorEnabled = () => true;
            perLine.Should().BeLessThanOrEqualTo(oldRule);
        }, iter: 1500, threads: 1); // 2026-11-06: В1 — правило переключается внутри проверки, поэтому один поток

    [Fact]
    public void Total_DoesNotDependOnLineOrder() =>
        AnyGen.List[2, 8].Sample(lines =>
            Calc(lines).TotalDue.Should().Be(Calc(Enumerable.Reverse(lines)).TotalDue), iter: 1500);

    [Theory]
    [InlineData(false)] // шаг 0,01
    [InlineData(true)]  // шаг 1 (единица суммы «som»)
    public void QuickCheckoutBody_Total_EqualsCartTotals(bool stepSom)
    {
        QuantityRules.WeightStep = () => stepSom ? 1m : 0.01m;
        RealisticGen.List[1, 6].Sample(lines =>
        {
            var cartJson = CartJson(lines);
            using var doc = JsonDocument.Parse(cartJson);
            var totals = CartTotalsCalculator.Calculate(doc.RootElement);
            var body = QuickCheckoutBody.TryBuild(cartJson, "cash", "100000000", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
            body.Should().NotBeNull();
            QuickCheckoutBody.ComputeTotal(body!).Should().Be(totals.TotalDue, "итог окна оплаты и итог тела запроса — одно число");
        }, iter: 800);
    }

    [Fact]
    public void FeatureOff_UsesOldRule_FloorOfWeighedSum() =>
        WeighedGen.List[1, 8].Sample(lines =>
        {
            QuantityRules.WeightLineFloorEnabled = () => false;
            var totals = Calc(lines);
            QuantityRules.WeightLineFloorEnabled = () => true;
            ((decimal)totals.Subtotal).Should().Be(FloorToStep(lines.Sum(l => l.Exact), 0.01m));
        }, iter: 1500, threads: 1);

    [Fact]
    public void KnownServerExamples()
    {
        // пример сервера ceba67c: 0,486 × 66 = 32,076 → 32,07; две весовые строки по 10,00 дают 20,00
        Calc([new Line(486, 6600, true)]).Subtotal.Should().Be(32.07);
        Calc([new Line(1000, 1000, true), new Line(1000, 1000, true)]).Subtotal.Should().Be(20.00);
        var two = Calc([new Line(1000, 1001, true), new Line(1000, 1001, true)]); // 10,01 × 2: ровные копейки не теряются
        two.Subtotal.Should().Be(20.02);
    }
}
