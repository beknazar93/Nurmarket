using FluentAssertions;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В8, Д4-40): «Очистить чек» без вопроса, но с «Вернуть» на 5 секунд — только когда вид умеет показать «Вернуть»; иначе вопрос остаётся.</summary>
[Collection("Wave5Database")]
public sealed class ClearReceiptUndoTests
{
    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(30);
        }
        return condition();
    }

    [Fact]
    public async Task WithoutUndoUi_StillAsksQuestion_AndKeepsReceiptOnNo()
    {
        using var h = new BasketVmHarness();
        h.AddProduct("p1", "Хлеб", 50);
        h.Prompts.Answer = false;

        h.Vm.ClearCartCommand.Execute(null);

        (await WaitAsync(() => h.Prompts.Questions.Count == 1)).Should().BeTrue("пока вид не умеет «Вернуть», вопрос задаётся как раньше");
        await Task.Delay(100);
        h.Cart.Items.Should().HaveCount(1, "кассир ответил «Отмена»");
        h.Vm.IsClearUndoVisible.Should().BeFalse();
    }

    [Fact]
    public async Task WithUndoUi_NoQuestion_ClearsAndUndoRestoresReceipt()
    {
        using var h = new BasketVmHarness();
        h.Vm.ClearUndoUiReady = true;
        h.AddProduct("p1", "Хлеб", 50, 2);
        h.AddProduct("p2", "Молоко", 120);

        h.Vm.ClearCartCommand.Execute(null);

        (await WaitAsync(() => h.Vm.IsClearUndoVisible)).Should().BeTrue();
        h.Prompts.Questions.Should().BeEmpty("вопроса нет: решение владельца — очистка без вопроса, но с «Вернуть»");
        h.Cart.Items.Should().BeEmpty();

        h.Vm.UndoClearCommand.Execute(null);

        h.Cart.Items.Should().HaveCount(2);
        h.Cart.TotalAmount.Should().Be(2 * 50m + 120m);
        h.Vm.IsClearUndoVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Undo_DoesNotOverwrite_NewItemsAddedAfterClear()
    {
        using var h = new BasketVmHarness();
        h.Vm.ClearUndoUiReady = true;
        h.AddProduct("p1", "Хлеб", 50);

        h.Vm.ClearCartCommand.Execute(null);
        (await WaitAsync(() => h.Vm.IsClearUndoVisible)).Should().BeTrue();
        h.AddProduct("p9", "Сок", 90); // кассир уже начал новый чек

        h.Vm.UndoClearCommand.Execute(null);

        h.Cart.Items.Should().ContainSingle(i => i.ProductId == "p9", "новый товар не затирается");
        h.Vm.IsClearUndoVisible.Should().BeFalse();
    }

    [Fact]
    public async Task UndoOffer_Expires_AfterWindow()
    {
        var before = NurMarketKassa.ViewModels.Main.BasketPanelViewModel.ClearUndoWindow;
        NurMarketKassa.ViewModels.Main.BasketPanelViewModel.ClearUndoWindow = TimeSpan.FromMilliseconds(150);
        try
        {
            using var h = new BasketVmHarness();
            h.Vm.ClearUndoUiReady = true;
            h.AddProduct("p1", "Хлеб", 50);

            h.Vm.ClearCartCommand.Execute(null);
            (await WaitAsync(() => h.Vm.IsClearUndoVisible)).Should().BeTrue();

            (await WaitAsync(() => !h.Vm.IsClearUndoVisible)).Should().BeTrue("через окно «Вернуть» пропадает");
            h.Vm.UndoClearCommand.Execute(null);
            h.Cart.Items.Should().BeEmpty("после окна вернуть уже нельзя");
        }
        finally
        {
            NurMarketKassa.ViewModels.Main.BasketPanelViewModel.ClearUndoWindow = before;
        }
    }
}
