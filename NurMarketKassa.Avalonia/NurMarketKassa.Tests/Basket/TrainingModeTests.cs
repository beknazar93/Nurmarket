using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-13 (Н-1, «Первый час»): учебная продажа не создаёт ни одного обращения к службе оплаты (сервер, офлайн-очередь), не трогает смену и историю продаж;
/// действия с настоящими данными в учебном режиме не выполняются; после выхода касса работает как раньше.</summary>
[Collection("UserPreferences")]
public sealed class TrainingModeTests : IDisposable
{
    public TrainingModeTests() => TrainingMode.Exit();

    public void Dispose() => TrainingMode.Exit();

    /// <summary>Служба оплаты: считает каждое обращение (в учебном режиме их быть не должно).</summary>
    private sealed class CountingCheckout : IPosCheckoutService
    {
        public int CheckoutCalls, DiscountCalls, PrepareCalls, RestartCalls;

        public Task<PosCheckoutResult> CheckoutAsync(PosCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            CheckoutCalls++;
            return Task.FromResult(PosCheckoutResult.Failed("тест: настоящая оплата"));
        }

        public Task<bool> ApplyOrderDiscountAsync(Dictionary<string, string> discountBody, CancellationToken cancellationToken = default)
        {
            DiscountCalls++;
            return Task.FromResult(true);
        }

        public Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.CompletedTask;
        }

        public Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            return Task.FromResult<string?>(null);
        }

        public int Total => CheckoutCalls + DiscountCalls + PrepareCalls + RestartCalls;
    }

    /// <summary>Отложенные чеки: считает обращения.</summary>
    private sealed class CountingDeferred : IDeferredCartService
    {
        public int Calls;

        public Task<DeferredCartResult> DeferCurrentCartAsync(string? label = null, bool startNewSale = true, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(DeferredCartResult.Failed("тест"));
        }

        public int PendingCount => 0;
    }

    /// <summary>Окно оплаты: кассир нажимает «Оплатить» (true) или отменяет (null).</summary>
    private sealed class PayingWindows : IWindowService
    {
        public bool Confirm { get; set; } = true;
        public int Dialogs { get; private set; }

        public Task<TResult?> ShowDialogAsync<TViewModel, TResult>(TViewModel viewModel) where TViewModel : class
        {
            Dialogs++;
            return Task.FromResult(Confirm ? (TResult?)(object?)true : default);
        }

        public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }

        public void Close(object viewModel, bool? dialogResult = null) { }
    }

    private sealed class Kit : IDisposable
    {
        public CartService Cart { get; } = new();
        public FakePrompts Prompts { get; } = new();
        public CountingCheckout Checkout { get; } = new();
        public CountingDeferred Deferred { get; } = new();
        public PayingWindows Windows { get; } = new();
        public BasketPanelViewModel Vm { get; }
        public int PaymentCommitted, CheckoutSucceeded, CheckoutSuccessCallback, ShiftChecks;

        public Kit()
        {
            CartTestHelper.StartEmptyCart(Cart);
            Vm = new BasketPanelViewModel(Cart, Prompts, Checkout, Deferred, new FakeDisplay(), Windows, new FakeDialogs(), new InlineDispatcher(),
                onCheckoutSuccess: () => CheckoutSuccessCallback++);
            Vm.PaymentCommitted += (_, _) => PaymentCommitted++;
            Vm.CheckoutSucceeded += (_, _) => CheckoutSucceeded++;
            Vm.EnsureShiftBeforePayment = () =>
            {
                ShiftChecks++;
                return Task.FromResult(true);
            };
        }

        public void Add(string id, string title, decimal price, double qty = 1) =>
            Vm.AddProductFromCatalogWithOverride(CartTestHelper.CreateProduct(id, title, price), qty, (double)price);

        public void Dispose()
        {
            Vm.Dispose();
            Cart.Dispose();
        }
    }

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
    public async Task TrainingSale_ClearsReceipt_WithoutCheckoutServiceShiftOrHistory()
    {
        using var kit = new Kit();
        TrainingMode.Enter();
        kit.Add("p1", "Хлеб", 45m, 2);
        kit.Add("p2", "Молоко", 80m);
        kit.Vm.Lines.Should().HaveCount(2);

        kit.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => kit.Vm.Lines.Count == 0 && !kit.Vm.IsBusy)).Should().BeTrue("учебная продажа очищает чек");
        kit.Windows.Dialogs.Should().Be(1, "окно оплаты показано один раз — как в настоящей продаже");
        kit.Checkout.Total.Should().Be(0, "служба оплаты (сервер и офлайн-очередь) в учебном режиме не вызывается ни одним методом");
        kit.ShiftChecks.Should().Be(0, "смена не проверяется и не открывается");
        kit.PaymentCommitted.Should().Be(0, "событие «оплата проведена» не наступает — состояние кассы и журналы не пишутся");
        kit.CheckoutSucceeded.Should().Be(0);
        kit.CheckoutSuccessCallback.Should().Be(0, "остатки каталога не пересчитываются");
        kit.Deferred.Calls.Should().Be(0);
        TrainingMode.SalesDone.Should().Be(1);
        kit.Vm.CartMessage.Should().NotBeNullOrWhiteSpace("кассир видит, что продажа была учебной");
    }

    [Fact]
    public async Task TrainingSale_Cancelled_KeepsReceipt_AndStillCallsNothing()
    {
        using var kit = new Kit();
        kit.Windows.Confirm = false;
        TrainingMode.Enter();
        kit.Add("p1", "Хлеб", 45m);

        kit.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => kit.Windows.Dialogs == 1 && !kit.Vm.IsBusy)).Should().BeTrue();
        kit.Vm.Lines.Should().HaveCount(1, "отмена окна оплаты чек не трогает");
        kit.Checkout.Total.Should().Be(0);
        TrainingMode.SalesDone.Should().Be(0);
    }

    [Fact]
    public async Task Training_BlocksDeferNewReceiptAndPayDebt()
    {
        using var kit = new Kit();
        TrainingMode.Enter();
        kit.Add("p1", "Хлеб", 45m);

        await kit.Vm.DeferCartAsync();
        kit.Deferred.Calls.Should().Be(0, "отложенные чеки — настоящие данные");
        kit.Vm.Lines.Should().HaveCount(1);

        var tabsBefore = kit.Vm.ReceiptTabs.Count;
        kit.Vm.NewReceiptCommand.Execute(null);
        kit.Vm.ReceiptTabs.Count.Should().Be(tabsBefore, "в учебном режиме чек один");
        kit.Vm.CartMessage.Should().Be(TrainingMode.BlockedText);
    }

    [Fact]
    public void CanEnterTraining_OnlyWithSingleEmptyReceipt()
    {
        using var kit = new Kit();
        kit.Vm.CanEnterTraining.Should().BeTrue();
        kit.Add("p1", "Хлеб", 45m);
        kit.Vm.CanEnterTraining.Should().BeFalse("в чеке настоящие товары — учебная оплата стёрла бы их");
    }

    [Fact]
    public void ClearTrainingReceipt_RemovesItems()
    {
        using var kit = new Kit();
        TrainingMode.Enter();
        kit.Add("p1", "Хлеб", 45m, 3);

        kit.Vm.ClearTrainingReceipt();

        kit.Vm.Lines.Should().BeEmpty();
        kit.Cart.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterExit_RealPaymentGoesThroughCheckoutServiceAgain()
    {
        using var kit = new Kit();
        TrainingMode.Enter();
        TrainingMode.Exit();
        kit.Add("p1", "Хлеб", 45m);

        kit.Vm.PayCommand.Execute(null);

        (await WaitAsync(() => kit.Checkout.CheckoutCalls == 1)).Should().BeTrue("после выхода из учебного режима оплата настоящая");
        kit.ShiftChecks.Should().Be(1);
    }

    [Fact]
    public void TrainingMode_StateAndEvents()
    {
        var changes = 0;
        void OnChanged() => changes++;
        TrainingMode.Changed += OnChanged;
        try
        {
            TrainingMode.IsActive.Should().BeFalse();
            TrainingMode.NoteSaleDone();
            TrainingMode.SalesDone.Should().Be(0, "вне режима учебные продажи не считаются");
            TrainingMode.Enter();
            TrainingMode.Enter();
            TrainingMode.IsActive.Should().BeTrue();
            TrainingMode.NoteSaleDone();
            TrainingMode.SalesDone.Should().Be(1);
            TrainingMode.Exit();
            TrainingMode.Exit();
            TrainingMode.IsActive.Should().BeFalse();
            changes.Should().Be(3, "включили, продажа, выключили — повторные вызовы событий не дают");
            TrainingMode.Enter();
            TrainingMode.SalesDone.Should().Be(0, "новый вход — счёт с нуля");
        }
        finally
        {
            TrainingMode.Changed -= OnChanged;
            TrainingMode.Exit();
        }
    }

    [Fact]
    public void OfflineQueue_RefusesAppend_InTraining()
    {
        TrainingMode.Enter();
        var before = OfflinePendingSalesStore.LoadAll().Count;

        OfflinePendingSalesStore.Append(new OfflineSaleEntry { Id = "training-" + Guid.NewGuid().ToString("N") });

        OfflinePendingSalesStore.LoadAll().Count.Should().Be(before, "в учебном режиме в офлайн-очередь ничего не пишется");
    }
}
