using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Lifecycle;

public sealed class CancellationLifecycleTests
{
    [Fact]
    public void MainStatusViewModel_Dispose_IsIdempotent()
    {
        var viewModel = new MainStatusViewModel(
            new WaitingConnectivityService(),
            new TestSession(),
            new ImmediateDispatcher(),
            new NotAutonomousAuthService());

        var action = () =>
        {
            viewModel.Dispose();
            viewModel.Dispose();
        };

        action.Should().NotThrow<ObjectDisposedException>();
    }

    [Fact]
    public async Task ApplicationStateDebounce_AllowsRapidReplacementAndDispose()
    {
        var service = new ApplicationStateService();

        for (var index = 0; index < 100; index++)
            service.SaveDebounced(() => new ApplicationState(), delayMs: 5);

        service.Dispose();
        service.Dispose();
        await Task.Delay(30);
    }

    private sealed class WaitingConnectivityService : IConnectivityService
    {
        public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Func<Task> action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class TestSession : IAppSession
    {
        public string? CurrentUserId { get; set; }
        public string? CurrentUserDisplayName { get; set; }
        public string? ActiveShiftId { get; set; }
        public string? ActiveTerminal { get; set; }
        public string? PosCashboxDisplayName { get; set; }
        public bool IsShiftOpen => !string.IsNullOrWhiteSpace(ActiveShiftId);
        public bool IsOfflineBootstrap { get; set; }
        public string? OfflineBootstrapMessage { get; set; }
    }

    private sealed class NotAutonomousAuthService : IAutonomousAuthService
    {
        public bool IsActivated => false;
        public bool HasLocalAccount => false;
        public bool IsCurrentSessionAutonomous => false;
        public Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default) => throw new NotSupportedException();
        public (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName) => throw new NotSupportedException();
        public (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password) => throw new NotSupportedException();
        public (bool Success, string? Email, string? DisplayName) TryAutoResume() => throw new NotSupportedException();
        public void EndAutonomousSession() => throw new NotSupportedException();
        public void MarkCatalogPreservedForNextLogin() => throw new NotSupportedException();
    }
}
