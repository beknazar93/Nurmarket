using FluentAssertions;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Commands;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Execute_RejectsSecondInvocationWhileFirstIsRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref invocationCount);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        command.Execute(null);

        invocationCount.Should().Be(1);
        command.CanExecute(null).Should().BeFalse();

        release.SetResult();
        await WaitUntilAsync(() => command.CanExecute(null));
        invocationCount.Should().Be(1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }
}
