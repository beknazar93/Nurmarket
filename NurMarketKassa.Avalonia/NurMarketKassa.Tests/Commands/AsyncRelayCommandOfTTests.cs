using FluentAssertions;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Commands;

public sealed class AsyncRelayCommandOfTTests
{
    [Fact]
    public async Task Execute_PassesParameterThrough()
    {
        string? received = null;
        var command = new AsyncRelayCommand<string>(param =>
        {
            received = param;
            return Task.CompletedTask;
        });

        command.Execute("hello");
        await WaitUntilAsync(() => received != null);

        received.Should().Be("hello");
    }

    [Fact]
    public async Task Execute_RejectsSecondInvocationWhileFirstIsRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var command = new AsyncRelayCommand<string>(async _ =>
        {
            Interlocked.Increment(ref invocationCount);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute("a");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        command.Execute("b");

        invocationCount.Should().Be(1);
        command.CanExecute("c").Should().BeFalse();

        release.SetResult();
        await WaitUntilAsync(() => command.CanExecute("c"));
        invocationCount.Should().Be(1);
    }

    [Fact]
    public void CanExecute_UsesPredicateWithParameter()
    {
        var command = new AsyncRelayCommand<CartLineStub>(
            _ => Task.CompletedTask,
            line => line != null && !string.IsNullOrEmpty(line.ItemId));

        command.CanExecute(new CartLineStub { ItemId = "1" }).Should().BeTrue();
        command.CanExecute(new CartLineStub { ItemId = "" }).Should().BeFalse();
        command.CanExecute(null).Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CartLineStub
    {
        public string ItemId { get; init; } = "";
    }
}
