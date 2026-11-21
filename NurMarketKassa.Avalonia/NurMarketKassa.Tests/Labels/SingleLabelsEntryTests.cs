using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-21 (владелец: «собери в складе все редакторы в одну вкладку»): печать ценников и штрихкодов, их редакторы и шаблоны — одна вкладка
/// Склада «Ценники и штрихкоды» (ключ «barcodes»). Нур Советник на любые слова про ценники, этикетки, штрихкоды и редактор ведёт в неё, отдельной
/// вкладки «Редактор этикетки» и отдельного раздела меню «Штрихкоды и ценники» больше нет.</summary>
public sealed class SingleLabelsEntryTests
{
    [Fact]
    public void AdvisorNavRouter_HasOneLabelsTab_AndNoOldLabelEditorTab()
    {
        var target = AdvisorNavRouter.ById("warehouse.barcodes");
        target.Should().NotBeNull();
        target!.Section.Should().Be("warehouse");
        target.Tab.Should().Be("barcodes");
        target.Names[0].Should().Be("Ценники и штрихкоды");
        AdvisorNavRouter.ById("warehouse.labels").Should().BeNull("вкладки «Редактор этикетки» больше нет");
    }

    [Theory]
    [InlineData("открой ценники")]
    [InlineData("открой редактор этикетки")]
    [InlineData("открой штрихкоды")]
    [InlineData("открой печать ценников")]
    [InlineData("открой этикетки")]
    public void AdvisorNavRouter_LabelWords_GoToTheSingleTab(string phrase)
    {
        var route = AdvisorNavRouter.Parse(phrase);
        route.Kind.Should().Be(AdvisorNavRouter.RouteKind.Single, phrase);
        route.Target!.Id.Should().Be("warehouse.barcodes", phrase);
    }

    [Fact]
    public void OwnerMenuSections_HaveNoSeparateBarcodesItem()
    {
        SectionVisibility.Catalog.Select(c => c.Key).Should().NotContain("barcodes", "отдельного пункта «Штрихкоды и ценники» в «Разделах меню» нет");
    }
}
