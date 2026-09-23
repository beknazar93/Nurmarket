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

    public MainWindowViewModel(
        MainToolbarViewModel toolbar,
        CatalogPanelViewModel catalog,
        BasketPanelViewModel basket,
        SideMenuViewModel sideMenu,
        IAppSession session)
    {
        Toolbar = toolbar;
        Catalog = catalog;
        Basket = basket;
        SideMenu = sideMenu;
        _session = session;
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

        return Task.CompletedTask;
    }

    public void ToggleSideMenu() => IsSideMenuOpen = !IsSideMenuOpen;

    public void CloseSideMenu() => IsSideMenuOpen = false;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Toolbar.Status.Dispose();
    }
}
