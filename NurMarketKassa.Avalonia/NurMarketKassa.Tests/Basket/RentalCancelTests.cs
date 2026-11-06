using FluentAssertions;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В8, Л4-09): прокат из чека оформлен до оплаты; если оплату отменили — кассиру предлагают отменить прокат, а чек не остаётся с висящим прокатом.</summary>
[Collection("Wave5Database")]
public sealed class RentalCancelTests
{
    private static RentalDto Rental(int number = 17) => new("rent-" + number, number, "c1", "Клиент", "active", false,
        DateTime.Today, DateTime.Today.AddDays(2), "day", "money", 500, "cash", "", Array.Empty<RentalItem>(), null, 0, "", null, null, 0, 0);

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task PaymentCancelled_AfterRentalFromCart_CashierCancelsRental_RentalClosedAndLineRemoved()
    {
        using var h = new BasketVmHarness();
        h.Windows.RentalRequestOnFirst = true;
        h.AddProduct("p1", "Лыжи", 300);
        var cancelledIds = new List<string>();
        h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(), 300));
        h.Vm.CancelUnpaidRental = id => { cancelledIds.Add(id); return Task.FromResult(true); };
        h.Prompts.Answer = true; // «Отменить прокат»

        h.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => cancelledIds.Count == 1)).Should().BeTrue("после отмены оплаты кассиру предложено отменить прокат, он согласился");
        cancelledIds.Should().Equal("rent-17");
        h.Prompts.Questions.Should().ContainSingle(q => q.Contains("17"));
        (await WaitAsync(() => h.Cart.Items.Count == 0)).Should().BeTrue("строка «Прокат №17…» убрана из чека");
    }

    [Fact]
    public async Task PaymentCancelled_AfterRentalFromCart_CashierKeepsRental_NothingCancelled()
    {
        using var h = new BasketVmHarness();
        h.Windows.RentalRequestOnFirst = true;
        h.AddProduct("p1", "Лыжи", 300);
        var cancelled = 0;
        h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(18), 300));
        h.Vm.CancelUnpaidRental = _ => { cancelled++; return Task.FromResult(true); };
        h.Prompts.Answer = false; // «Оставить»

        h.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => h.Prompts.Questions.Count == 1)).Should().BeTrue();
        await Task.Delay(300);
        cancelled.Should().Be(0, "кассир выбрал «Оставить»: прокат не трогаем");
        h.Cart.Items.Should().ContainSingle(i => i.Name.StartsWith("Прокат №18"), "чек с прокатом ждёт оплаты");
    }

    [Fact]
    public async Task CancelFails_ShowsError_AndKeepsRentalLine()
    {
        using var h = new BasketVmHarness();
        h.Windows.RentalRequestOnFirst = true;
        h.AddProduct("p1", "Лыжи", 300);
        h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(19), 300));
        h.Vm.CancelUnpaidRental = _ => Task.FromResult(false);
        h.Prompts.Answer = true;

        h.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => h.Prompts.Errors.Count == 1)).Should().BeTrue("сервер не закрыл прокат: кассир узнаёт, что делать");
        h.Cart.Items.Should().ContainSingle(i => i.Name.StartsWith("Прокат №19"));
    }

    [Fact]
    public async Task PlainPaymentCancel_WithoutRental_AsksNothing()
    {
        using var h = new BasketVmHarness();
        h.AddProduct("p1", "Хлеб", 50);
        h.Vm.CancelUnpaidRental = _ => Task.FromResult(true);

        h.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => h.Windows.PaymentDialogs == 1)).Should().BeTrue();
        await Task.Delay(300);
        h.Prompts.Questions.Should().BeEmpty("проката не было — вопросов нет");
    }
}
