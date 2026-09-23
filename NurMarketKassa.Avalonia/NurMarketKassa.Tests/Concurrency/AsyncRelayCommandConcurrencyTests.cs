using FluentAssertions;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Concurrency;

/// <summary>
/// КОНКУРЕНТНОСТЬ / ЗАВИСАНИЯ: <see cref="AsyncRelayCommand"/>.
/// <para>
/// Команда реализует <c>ICommand.Execute</c> как <c>async void</c>: без внутреннего
/// <c>catch</c> любое исключение из делегата ушло бы в SynchronizationContext и уронило бы
/// приложение кассы. Ещё команда обязана освобождать флаг <c>_isExecuting</c> в <c>finally</c>,
/// иначе кнопка кассира навсегда останется заблокированной («зависание» с точки зрения UX).
/// </para>
/// <para>
/// ПРОБЕЛ В ПОКРЫТИИ (осознанный, не забытый): <c>JwtBearerRefreshHandler</c> НЕ покрыт
/// unit-тестом на конкурентные refresh-запросы. <c>NurMarketApiClient</c>
/// (<c>NurMarketKassa.Infrastructure/Services/NurMarketApiClient.cs</c>) — <c>sealed</c> и в
/// конструкторе жёстко создаёт <c>new JwtBearerRefreshHandler(this) { InnerHandler = new HttpClientHandler() }</c>,
/// точки внедрения тестового <c>HttpMessageHandler</c> нет, а сам refresh-запрос уходит через
/// реальный <c>_http</c> на реальный сервер. Безопасно (без сетевых обращений и без риска
/// разлогинить рабочую кассу) протестировать это невозможно без изменения продакшен-кода —
/// добавления seam для DI тестового <c>HttpMessageHandler</c>, что не входит в объём задачи.
/// </para>
/// </summary>
public sealed class AsyncRelayCommandConcurrencyTests
{
    private static readonly TimeSpan HangBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Execute_WhenDelegateThrows_DoesNotCrashProcess_AndResetsBusyFlag()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(() =>
        {
            entered.TrySetResult();
            throw new InvalidOperationException("test-concurrency: boom inside async void command");
        });

        // Если бы исключение не было перехвачено, async void утащил бы его в
        // SynchronizationContext / unhandled exception и уронил бы весь тестовый хост.
        command.Execute(null);

        await entered.Task.WaitAsync(HangBudget);
        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);

        command.CanExecute(null).Should().BeTrue(
            "busy-флаг обязан сбрасываться в finally даже после исключения, иначе кнопка залипнет навсегда");
    }

    [Fact]
    public async Task Execute_AfterFailure_CanRunAgain()
    {
        var attempts = 0;
        var lastAttemptDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            await Task.Yield();
            if (attempt == 1)
                throw new InvalidOperationException("test-concurrency: first attempt fails");
            lastAttemptDone.TrySetResult();
        });

        command.Execute(null);
        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);

        command.Execute(null);
        await lastAttemptDone.Task.WaitAsync(HangBudget);
        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);

        attempts.Should().Be(2, "после сбоя команда обязана снова быть работоспособной");
    }

    [Fact]
    public async Task Execute_UnderParallelStorm_RunsDelegateOnlyOnce_WhileBusy()
    {
        const int stormSize = 64;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;

        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref invocations);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(HangBudget);

        // Гвардия: Interlocked.CompareExchange(ref _isExecuting, 1, 0) — второй вход обязан быть отвергнут
        // даже при одновременном нажатии/автоповторе скана из нескольких потоков.
        var storm = Enumerable.Range(0, stormSize)
            .Select(_ => Task.Run(() => command.Execute(null)))
            .ToArray();

        var stormCompletion = Task.WhenAll(storm);
        var finished = await Task.WhenAny(stormCompletion, Task.Delay(HangBudget));
        finished.Should().BeSameAs(stormCompletion, "possible deadlock: отклонение повторных вызовов не должно блокировать поток");
        await stormCompletion;

        invocations.Should().Be(1, "повторные вызовы во время выполнения обязаны отбрасываться");
        command.CanExecute(null).Should().BeFalse();

        release.SetResult();
        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);
        invocations.Should().Be(1);
    }

    [Fact]
    public async Task Execute_WithAwaitingDelegate_CompletesWithoutDeadlock_WhenCalledSynchronously()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            await Task.Delay(50);
            completed.TrySetResult();
        });

        // Синхронный вызов из потока теста: async void не должен требовать «прокачки» цикла сообщений.
        command.Execute(null);

        var finished = await Task.WhenAny(completed.Task, Task.Delay(HangBudget));
        finished.Should().BeSameAs(
            completed.Task,
            "possible deadlock: команда с await Task.Delay не завершилась за {0} с",
            HangBudget.TotalSeconds);

        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);
    }

    [Fact]
    public async Task CanExecute_IsSafeToPollFromManyThreads_WhileCommandRuns()
    {
        const int readerCount = 16;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(HangBudget);

        var busyObservations = 0;
        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                if (!command.CanExecute(null))
                    Interlocked.Increment(ref busyObservations);
            }
        })).ToArray();

        var completion = Task.WhenAll(readers);
        var finished = await Task.WhenAny(completion, Task.Delay(HangBudget));
        finished.Should().BeSameAs(completion, "possible deadlock: параллельное чтение CanExecute зависло");
        await completion;

        busyObservations.Should().Be(readerCount * 2_000, "Volatile.Read обязан отдавать актуальный busy-флаг всем потокам");

        release.SetResult();
        await WaitUntilAsync(() => command.CanExecute(null), HangBudget);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"possible deadlock: условие не выполнилось за {timeout.TotalSeconds} с");
            }
        }
    }
}
