using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-27 (владелец: «печатать штрихкод и ценники из накладной приёмки»): какие позиции накладной и сколько копий уходят в «Ценники и штрихкоды».
/// Копии = принятое количество (целое 1..99; весовые, дробные и кг/л/м — 1; больше 99 — 100 «с пометкой»), один товар в двух строках — одна позиция.</summary>
public sealed class ReceiptLabelPlannerTests
{
    private static ReceiptLabelSource L(string? id, string? code, double qty, string? unit = "шт", bool isNew = false, bool variants = false) => new(id, code, qty, unit, isNew, variants);

    [Fact]
    public void Copies_EqualReceivedQuantity_AndBarcodeIsKept()
    {
        var plan = ReceiptLabelPlanner.Build(new[] { L("p1", "4600000000011", 12), L("p2", " 4600000000028 ", 3) });
        plan.Items.Should().Equal(("p1", "4600000000011", 12), ("p2", "4600000000028", 3));
        plan.CopiesTotal.Should().Be(15);
        plan.NotInCatalog.Should().Be(0);
        plan.Skipped.Should().Be(0);
    }

    [Fact]
    public void SameProductInTwoLines_IsOneItem_WithSummedQuantity_AndFirstBarcode()
    {
        var plan = ReceiptLabelPlanner.Build(new[] { L("p1", null, 4), L("p2", "222", 1), L("P1", "111", 6) });
        plan.Items.Should().Equal(("p1", "111", 10), ("p2", "222", 1));
    }

    [Fact]
    public void ZeroOrNegativeQuantity_IsIgnored()
    {
        var plan = ReceiptLabelPlanner.Build(new[] { L("p1", "1", 0), L("p2", "2", -3), L("p3", "3", double.NaN), L("p4", "4", 2) });
        plan.Items.Should().Equal(("p4", "4", 2));
    }

    [Fact]
    public void NewProductsWithoutCard_AreCountedNotPrinted_VariantLinesSkipped()
    {
        var plan = ReceiptLabelPlanner.Build(new[] { L("p1", "1", 2), L(null, "999", 5, isNew: true), L("", null, 1, isNew: true), L("p3", "3", 7, variants: true), L(null, null, 2, isNew: false) });
        plan.Items.Should().Equal(("p1", "1", 2));
        plan.NotInCatalog.Should().Be(2);
        plan.Skipped.Should().Be(2); // строка с размерами и строка без товара и без признака «новый»
    }

    [Theory]
    [InlineData(2.5, "шт", false, 1)]   // дробное количество — одна этикетка
    [InlineData(10.0, "кг", false, 1)]  // весовой по единице
    [InlineData(10.0, "шт", true, 1)]   // весовой по карточке
    [InlineData(99.0, "шт", false, 99)]
    [InlineData(250.0, "шт", false, 100)] // больше 99 — 100: окно печати напечатает 99 и скажет об этом
    public void CopiesRules_MatchStockLabelRules(double qty, string unit, bool weighed, int expected)
    {
        var plan = ReceiptLabelPlanner.Build(new[] { L("p1", "1", qty, unit) }, _ => weighed);
        plan.Items.Single().Copies.Should().Be(expected);
        plan.CopiesTotal.Should().Be(Math.Min(expected, 99));
    }

    [Fact]
    public void EmptyInput_GivesEmptyPlan()
    {
        var plan = ReceiptLabelPlanner.Build(Array.Empty<ReceiptLabelSource>());
        plan.Items.Should().BeEmpty();
        plan.CopiesTotal.Should().Be(0);
    }
}
