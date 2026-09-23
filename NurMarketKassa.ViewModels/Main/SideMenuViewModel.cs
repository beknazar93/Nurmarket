using System.Windows.Input;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Боковое меню (hamburger).</summary>
public sealed class SideMenuViewModel : ViewModelBase
{
    private readonly IAppSession _session;
    private string _shiftBalanceText = "—";

    public SideMenuViewModel(
        IAppSession session,
        Action closeMenu,
        Action? navigateWarehouse = null,
        Action? navigateShifts = null,
        Action? navigateReturn = null,
        Action? navigateCashOperations = null,
        Action? navigateFinance = null,
        Action? navigateSales = null,
        Action? navigateSettings = null,
        Func<Task>? logout = null)
    {
        _session = session;
        CloseMenuCommand = new RelayCommand(closeMenu);
        NavigateMainCommand = new RelayCommand(closeMenu);
        NavigateWarehouseCommand = new RelayCommand(() => { navigateWarehouse?.Invoke(); closeMenu(); });
        NavigateShiftsCommand = new RelayCommand(() => { navigateShifts?.Invoke(); closeMenu(); });
        NavigateReturnCommand = new RelayCommand(() => { navigateReturn?.Invoke(); closeMenu(); });
        NavigateCashOperationsCommand = new RelayCommand(() => { navigateCashOperations?.Invoke(); closeMenu(); });
        NavigateFinanceCommand = new RelayCommand(() => { navigateFinance?.Invoke(); closeMenu(); });
        NavigateSalesCommand = new RelayCommand(() => { navigateSales?.Invoke(); closeMenu(); });
        NavigateSettingsCommand = new RelayCommand(() => { navigateSettings?.Invoke(); closeMenu(); });
        LogoutCommand = new AsyncRelayCommand(async () =>
        {
            if (logout != null)
                await logout().ConfigureAwait(true);
            else
                closeMenu();
        });
    }

    public string ShiftBalanceText
    {
        get => _shiftBalanceText;
        set => SetProperty(ref _shiftBalanceText, value ?? "—");
    }

    public string CashboxTitle =>
        string.IsNullOrWhiteSpace(_session.PosCashboxDisplayName)
            ? "NurCrm Market"
            : _session.PosCashboxDisplayName!;

    public ICommand CloseMenuCommand { get; }
    public ICommand NavigateMainCommand { get; }
    public ICommand NavigateWarehouseCommand { get; }
    public ICommand NavigateShiftsCommand { get; }
    public ICommand NavigateReturnCommand { get; }
    public ICommand NavigateCashOperationsCommand { get; }
    public ICommand NavigateFinanceCommand { get; }
    public ICommand NavigateSalesCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand LogoutCommand { get; }
}
