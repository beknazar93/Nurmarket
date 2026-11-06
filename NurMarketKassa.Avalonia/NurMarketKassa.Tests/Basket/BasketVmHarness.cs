using System.Reflection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

// 2026-11-06 (В8): простые заглушки, чтобы тестировать BasketPanelViewModel без окон: вопросы кассиру записываются, диалог оплаты «закрывается» по команде теста.

internal sealed class InlineDispatcher : IDispatcher
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

internal sealed class FakePrompts : IUserPrompts
{
    public List<string> Questions { get; } = new();
    public List<string> Toasts { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Ответ на «да/нет» вопросы (по умолчанию «да»).</summary>
    public bool Answer { get; set; } = true;

    public Task<bool> ConfirmAsync(string message)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }

    public Task<bool> ConfirmAsync(string message, string? confirmText, string? cancelText = null, bool danger = false) => ConfirmAsync(message);

    public void ShowToast(string message, bool isWarning = false) => Toasts.Add(message);

    public void ShowWarning(string message) => Errors.Add(message);

    public void ShowError(string message) => Errors.Add(message);

    public Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword) => Task.FromResult(true);

    public Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator) => Task.FromResult(true);
}

internal sealed class FakeCheckout : IPosCheckoutService
{
    public Task<PosCheckoutResult> CheckoutAsync(PosCheckoutRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(PosCheckoutResult.Failed("тест: оплата не вызывается"));

    public Task<bool> ApplyOrderDiscountAsync(Dictionary<string, string> discountBody, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

internal sealed class FakeDeferred : IDeferredCartService
{
    public Task<DeferredCartResult> DeferCurrentCartAsync(string? label = null, bool startNewSale = true, CancellationToken cancellationToken = default) =>
        Task.FromResult(DeferredCartResult.Failed("тест"));

    public int PendingCount => 0;
}

internal sealed class FakeDisplay : ICustomerDisplayService
{
    public void Show() { }

    public void Hide() { }

    public void UpdateCart(CustomerDisplayCartSnapshot snapshot) { }

    public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) { }

    public void SetSelectedBankQrPath(string? qrPath) { }
}

internal sealed class FakeDialogs : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

    public Task ShowInfoAsync(string message) => Task.CompletedTask;

    public Task ShowErrorAsync(string message) => Task.CompletedTask;

    public Task<PrinterNotConnectedResult> ShowPrinterNotConnectedAsync() => Task.FromResult(PrinterNotConnectedResult.Cancel);

    public Task<bool> ConfirmPaymentAsync() => Task.FromResult(true);
}

/// <summary>Окна: диалог оплаты «закрывается отменой» (результат null). <see cref="RentalRequestOnFirst"/> — при первом показе кассир жмёт «Оформить как прокат».</summary>
internal sealed class FakeWindows : IWindowService
{
    public int PaymentDialogs { get; private set; }

    public bool RentalRequestOnFirst { get; set; }

    public Task<TResult?> ShowDialogAsync<TViewModel, TResult>(TViewModel viewModel) where TViewModel : class
    {
        PaymentDialogs++;
        if (RentalRequestOnFirst && PaymentDialogs == 1 && viewModel is CheckoutViewModel checkout)
            typeof(CheckoutViewModel).GetProperty(nameof(CheckoutViewModel.RentalRequested), BindingFlags.Public | BindingFlags.Instance)!.SetValue(checkout, true);
        return Task.FromResult(default(TResult));
    }

    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }

    public void Close(object viewModel, bool? dialogResult = null) { }
}

internal sealed class BasketVmHarness : IDisposable
{
    public CartService Cart { get; } = new();
    public FakePrompts Prompts { get; } = new();
    public FakeWindows Windows { get; } = new();
    public BasketPanelViewModel Vm { get; }

    public BasketVmHarness(Func<string, Task>? onBarcodeNotFound = null)
    {
        CartTestHelper.StartEmptyCart(Cart);
        Vm = new BasketPanelViewModel(Cart, Prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), Windows, new FakeDialogs(), new InlineDispatcher(),
            onBarcodeNotFound: onBarcodeNotFound);
    }

    public void AddProduct(string id, string title, decimal price, double qty = 1)
    {
        Vm.AddProductFromCatalogWithOverride(CartTestHelper.CreateProduct(id, title, price), qty, (double)price);
    }

    public void Dispose()
    {
        Vm.Dispose();
        Cart.Dispose();
    }
}
