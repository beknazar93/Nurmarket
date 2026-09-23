using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Координатор главного окна кассы. Делегирует зоны специализированным ViewModel.
/// Не содержит бизнес-логики корзины/каталога — только композиция и UI-состояние оболочки.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private int _disposed;
    private bool _isSideMenuOpen;
    private readonly IUpdateCheckService? _updateCheck;
    private readonly UpdateSettings? _updateSettings;

    public MainWindowViewModel(
        MainToolbarViewModel toolbar,
        CatalogPanelViewModel catalog,
        BasketPanelViewModel basket,
        SideMenuViewModel sideMenu,
        IAppSession session,
        IUpdateCheckService? updateCheck = null,
        AppSettings? appSettings = null)
    {
        Toolbar = toolbar;
        Catalog = catalog;
        Basket = basket;
        SideMenu = sideMenu;
        _session = session;
        _updateCheck = updateCheck;
        _updateSettings = appSettings?.Updates;
    }

    private readonly IAppSession _session;

    public MainToolbarViewModel Toolbar { get; }
    public CatalogPanelViewModel Catalog { get; }
    public BasketPanelViewModel Basket { get; }
    public SideMenuViewModel SideMenu { get; }

    public bool IsSideMenuOpen
    {
        get => _isSideMenuOpen;
        set => SetProperty(ref _isSideMenuOpen, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Toolbar.RefreshUserTitle();
        Toolbar.Status.RefreshFromSession();
        SideMenu.ShiftBalanceText = Toolbar.Status.ShiftBalanceText;

        if (_session.IsShiftOpen)
            Toolbar.NotifyShiftStateChanged();

        if (Catalog.RefreshCatalogCommand.CanExecute(null))
            Catalog.RefreshCatalogCommand.Execute(null);

        _ = CheckForUpdateInBackgroundAsync();
        return Task.CompletedTask;
    }

    public void ToggleSideMenu()
    {
        IsSideMenuOpen = !IsSideMenuOpen;
        if (IsSideMenuOpen)
            SideMenu.RefreshEntitlements();
    }

    public void CloseSideMenu() => IsSideMenuOpen = false;

    /// <summary>
    /// Ненавязчивая проверка версии при входе в кассу. Ничего не блокирует и не
    /// падает наружу: без настроенного ManifestUrl или при сетевой ошибке просто
    /// молча ничего не показывает.
    /// </summary>
    private async Task CheckForUpdateInBackgroundAsync()
    {
        if (_updateCheck is null)
            return;

        var settings = _updateSettings ?? new UpdateSettings();
        if (!settings.CheckOnStartup)
            return;

        var lastCheck = UserPreferences.Instance.LastUpdateCheckUtc;
        if (lastCheck.HasValue &&
            DateTime.UtcNow - lastCheck.Value < TimeSpan.FromHours(Math.Max(0, settings.MinHoursBetweenChecks)))
            return;

        try
        {
            var result = await _updateCheck.CheckAsync().ConfigureAwait(true);

            // Only spend the MinHoursBetweenChecks budget on a check that actually happened —
            // a skipped one (no ManifestUrl configured yet) must not delay the real first check.
            if (result.IsConfigured)
            {
                UserPreferences.Instance.LastUpdateCheckUtc = DateTime.UtcNow;
                UserPreferences.Instance.SaveToDisk();
            }

            if (result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.LatestVersion))
            {
                Toolbar.SetUpdateAvailable(result.LatestVersion!);
                PosLogger.Log(
                    $"Update check: newer version available {result.CurrentVersion} -> {result.LatestVersion}",
                    "UPDATE");
            }
            else if (result.IsConfigured)
            {
                PosLogger.Log(
                    $"Update check: up to date (current={result.CurrentVersion}, latest={result.LatestVersion})",
                    "UPDATE");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Startup update check failed: {ex}", "WARNING");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Toolbar.Status.Dispose();
    }
}
