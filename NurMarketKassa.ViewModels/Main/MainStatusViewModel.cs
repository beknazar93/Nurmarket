using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Статусная строка: сеть, смена, баланс кассы.</summary>
public sealed class MainStatusViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IConnectivityService _connectivity;
    private readonly IAppSession _session;
    private readonly IDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    private bool _isOnline = true;
    private string _networkModeText = "";
    private string _shiftBalanceText = "Касса: 0.00 сом";
    private string _statusLabel = "Онлайн";

    public MainStatusViewModel(
        IConnectivityService connectivity,
        IAppSession session,
        IDispatcher dispatcher)
    {
        _connectivity = connectivity;
        _session = session;
        _dispatcher = dispatcher;
        RefreshFromSession();
        _ = MonitorConnectivityAsync(_lifetimeCts.Token);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (!SetProperty(ref _isOnline, value))
                return;
            StatusLabel = value ? "Онлайн" : "Офлайн";
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public string NetworkModeText
    {
        get => _networkModeText;
        set => SetProperty(ref _networkModeText, value ?? "");
    }

    public string ShiftBalanceText
    {
        get => _shiftBalanceText;
        set => SetProperty(ref _shiftBalanceText, value ?? "");
    }

    public void RefreshFromSession()
    {
        var name = _session.PosCashboxDisplayName;
        NetworkModeText = string.IsNullOrWhiteSpace(name)
            ? (_session.IsOfflineBootstrap ? "Офлайн-режим" : "")
            : name;

        if (_session.IsOfflineBootstrap && !string.IsNullOrWhiteSpace(_session.OfflineBootstrapMessage))
            NetworkModeText = _session.OfflineBootstrapMessage;
    }

    public void SetShiftBalance(decimal balance) =>
        ShiftBalanceText = $"Касса: {balance:0.00} сом";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private async Task MonitorConnectivityAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var online = await _connectivity.IsOnlineAsync(ct).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() => IsOnline = online).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await _dispatcher.InvokeAsync(() => IsOnline = false).ConfigureAwait(false);
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
