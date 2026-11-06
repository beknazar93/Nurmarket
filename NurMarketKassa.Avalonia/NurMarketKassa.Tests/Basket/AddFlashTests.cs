using FluentAssertions;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В8, Д4-06 VM): после добавления товара модель говорит, какая строка добавлена и повторный ли это скан; строка статуса «Товар добавлен.» убирается, когда вид готов.</summary>
[Collection("Wave5Database")]
public sealed class AddFlashTests
{
    [Fact]
    public void FirstAdd_MarksNewLine_NotRepeat()
    {
        using var h = new BasketVmHarness();

        h.AddProduct("p1", "Хлеб", 50);

        h.Vm.LastAddedItemId.Should().Be(h.Cart.Items.Single().Id);
        h.Vm.LastAddedWasRepeat.Should().BeFalse();
        h.Vm.LastAddedQuantity.Should().Be(1);
        h.Vm.AddFlashVersion.Should().Be(1);
    }

    [Fact]
    public void SecondScanOfSameProduct_IsRepeat_WithGrownQuantity_AndNewFlashVersion()
    {
        using var h = new BasketVmHarness();

        h.AddProduct("p1", "Хлеб", 50);
        var firstVersion = h.Vm.AddFlashVersion;
        h.Vm.AddProductFromCatalog(Helpers.CartTestHelper.CreateProduct("p1", "Хлеб", 50m));

        h.Vm.LastAddedWasRepeat.Should().BeTrue();
        h.Vm.LastAddedQuantity.Should().Be(2);
        h.Vm.AddFlashVersion.Should().BeGreaterThan(firstVersion, "каждое добавление запускает подсветку заново");
        h.Cart.Items.Should().HaveCount(1);
    }

    [Fact]
    public void StatusLine_IsWritten_UntilViewIsReady_ThenNot()
    {
        using var h = new BasketVmHarness();
        h.AddProduct("p1", "Хлеб", 50);
        h.Vm.CartMessage.Should().NotBeNullOrEmpty("пока вид не готов, надпись «Товар добавлен.» остаётся, как раньше");

        h.Vm.CartMessage = "";
        h.Vm.AddFlashUiReady = true;
        h.AddProduct("p2", "Сок", 90);

        h.Vm.CartMessage.Should().BeNullOrEmpty("вид показывает подсветку строки — строка статуса не нужна");
    }
}
