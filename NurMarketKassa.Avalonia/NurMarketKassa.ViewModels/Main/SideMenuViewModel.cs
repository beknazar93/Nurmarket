using System.Windows.Input;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Боковое меню (hamburger).</summary>
public sealed class SideMenuViewModel : ViewModelBase
{
    private readonly IAppSession _session;
    private readonly IPermissionService? _permissions;
    private string _shiftBalanceText = "—";
    private bool _canViewStaffTimesheet;
    private bool _canViewReturn = true;
    private bool _canViewDeferredReceipts = true;
    private bool _canViewRestock = true;
    private bool _canViewFinance = true;
    private bool _canViewSales = true;
    private bool _canViewClients = true;
    private bool _canViewSalary = true;
    private bool _canViewPayDebt = true;
    private bool _canViewCrm = true;
    private bool _canViewErrorLogs = true;
    private bool _canViewRemoteSupport = true;
    private bool _canViewKnowledgeBase = true;
    private bool _canViewMarketplace = true;
    private bool _isWorkSectionExpanded;
    private bool _isInfoSectionExpanded;
    private bool _isSystemSectionExpanded;

    public SideMenuViewModel(
        IAppSession session,
        Action closeMenu,
        Action? navigateWarehouse = null,
        Action? navigateStaffTimesheet = null,
        Action? navigateReturn = null,
        Action? navigateDeferredReceipts = null,
        Action? navigateFinance = null,
        Action? navigateSales = null,
        Action? navigateClients = null,
        Action? navigateAbc = null,
        Action? navigatePayDebt = null,
        Action? navigateSettings = null,
        Action? navigateMarketplace = null,
        Action? navigateCrm = null,
        Action? navigateErrorLogs = null,
        Action? navigateRemoteSupport = null,
        Action? navigateKnowledgeBase = null,
        Action? navigateRestock = null,
        Func<Task>? switchCashier = null,
        Func<Task>? logout = null,
        Action? exitApplication = null,
        IPermissionService? permissions = null,
        Action? navigateSalary = null,
        Func<Task>? openCashOperation = null)
    {
        _session = session;
        _permissions = permissions;
        RefreshEntitlements();
        CloseMenuCommand = new RelayCommand(closeMenu);
        NavigateWarehouseCommand = new RelayCommand(() => { navigateWarehouse?.Invoke(); closeMenu(); });
        NavigateStaffTimesheetCommand = new RelayCommand(() => { navigateStaffTimesheet?.Invoke(); closeMenu(); });
        NavigateMarketplaceCommand = new RelayCommand(() => { navigateMarketplace?.Invoke(); closeMenu(); });
        NavigateReturnCommand = new RelayCommand(() => { navigateReturn?.Invoke(); closeMenu(); });
        NavigateDeferredReceiptsCommand = new RelayCommand(() => { navigateDeferredReceipts?.Invoke(); closeMenu(); });
        NavigateFinanceCommand = new RelayCommand(() => { navigateFinance?.Invoke(); closeMenu(); });
        NavigateSalaryCommand = new RelayCommand(() => { navigateSalary?.Invoke(); closeMenu(); });
        OpenCashOperationCommand = new AsyncRelayCommand(async () =>
        {
            closeMenu();
            if (openCashOperation != null)
                await openCashOperation().ConfigureAwait(true);
        });
        NavigateSalesCommand = new RelayCommand(() => { navigateSales?.Invoke(); closeMenu(); });
        NavigateClientsCommand = new RelayCommand(() => { navigateClients?.Invoke(); closeMenu(); });
        NavigateAbcCommand = new RelayCommand(() => { navigateAbc?.Invoke(); closeMenu(); });
        NavigatePayDebtCommand = new RelayCommand(() => { navigatePayDebt?.Invoke(); closeMenu(); });
        NavigateSettingsCommand = new RelayCommand(() => { navigateSettings?.Invoke(); closeMenu(); });
        NavigateCrmCommand = new RelayCommand(() => { navigateCrm?.Invoke(); closeMenu(); });
        NavigateErrorLogsCommand = new RelayCommand(() => { navigateErrorLogs?.Invoke(); closeMenu(); });
        NavigateRemoteSupportCommand = new RelayCommand(() => { navigateRemoteSupport?.Invoke(); closeMenu(); });
        NavigateKnowledgeBaseCommand = new RelayCommand(() => { navigateKnowledgeBase?.Invoke(); closeMenu(); });
        NavigateRestockCommand = new RelayCommand(() => { navigateRestock?.Invoke(); closeMenu(); });
        ToggleWorkSectionCommand = new RelayCommand(() => IsWorkSectionExpanded = !IsWorkSectionExpanded);
        ToggleInfoSectionCommand = new RelayCommand(() => IsInfoSectionExpanded = !IsInfoSectionExpanded);
        ToggleSystemSectionCommand = new RelayCommand(() => IsSystemSectionExpanded = !IsSystemSectionExpanded);
        SwitchCashierCommand = new AsyncRelayCommand(async () =>
        {
            closeMenu();
            if (switchCashier != null)
                await switchCashier().ConfigureAwait(true);
        });
        LogoutCommand = new AsyncRelayCommand(async () =>
        {
            if (logout != null)
                await logout().ConfigureAwait(true);
            else
                closeMenu();
        });
        ExitApplicationCommand = new RelayCommand(() =>
        {
            if (exitApplication != null)
                exitApplication();
            else
                closeMenu();
        });
    }

    public string ShiftBalanceText
    {
        get => _shiftBalanceText;
        set => SetProperty(ref _shiftBalanceText, value ?? "—");
    }

    /// <summary>Кто сейчас за кассой. В шапке главного окна этого нет вовсе, а при пересменке
    /// это первое, что нужно проверить: чек уйдёт на сервер от того, кто здесь записан.</summary>
    public string CashierName =>
        !string.IsNullOrWhiteSpace(_session.CurrentUserDisplayName) ? _session.CurrentUserDisplayName!
        : !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName) ? PosApp.CurrentUserDisplayName!
        : "—";

    public bool IsShiftOpen => _session.IsShiftOpen;

    public string ShiftStateText => _session.IsShiftOpen
        ? Tr.T("Смена открыта", "Смена ачык", "Shift open", "Vardiya açık", "Smena ochiq")
        : Tr.T("Смена закрыта", "Смена жабык", "Shift closed", "Vardiya kapalı", "Smena yopiq");

    public string CashierLabel =>
        Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir");

    /// <summary>Сессия — обычный объект без уведомлений, поэтому карточку в шапке меню
    /// обновляет тот же код, что пересчитывает остаток в кассе (MainWindow.UpdateShiftBalanceUi):
    /// смена открылась, закрылась или кассир сменился — три момента, когда это меняется.</summary>
    public void RefreshSessionInfo()
    {
        OnPropertyChanged(nameof(CashierName));
        OnPropertyChanged(nameof(IsShiftOpen));
        OnPropertyChanged(nameof(ShiftStateText));
        OnPropertyChanged(nameof(CashierLabel));
        OnPropertyChanged(nameof(CashboxTitle));
    }

    public string CashboxTitle =>
        string.IsNullOrWhiteSpace(_session.PosCashboxDisplayName)
            ? "NurCrm Market"
            : _session.PosCashboxDisplayName!;

    /// <summary>
    /// Visibility mirrors the permission each Navigate* handler already enforces in
    /// MainWindow — hiding the item is a UX improvement on top of that enforcement,
    /// not a replacement for it. Склад и Настройки НЕ ограничиваются тарифом Старт (2026-09-07,
    /// явное указание пользователя: "убери в старте всё кроме настройки и склада") — единственные
    /// два пункта меню, которые остаются доступны на любом тарифе.
    /// </summary>
    public bool CanViewWarehouse => (_permissions?.HasPermission(PosPermissions.ViewProcurement) ?? true)
                                    && NurMarketKassa.Services.AppMode.OwnerSectionsInKassa;
    public bool CanViewSettings => _permissions?.HasPermission(PosPermissions.ViewSettings) ?? true;

    /// <summary>2026-09-07: остальные пункты меню (кроме Склада/Настроек — см. CanViewWarehouse
    /// выше) на тарифе Старт скрываются полностью, независимо от прав кассира — по прямому
    /// указанию пользователя. Все свойства ниже — сохраняемые (SetProperty), а не вычисляемые
    /// геттеры, по той же причине, что и CanViewClients: TariffGate.IsStartTariff зависит от
    /// CompanyInfoService.LastCompany, который подгружается асинхронно ПОСЛЕ первого построения
    /// меню (и может смениться посреди сессии при смене кассира на другую компанию) — обычный
    /// геттер прочитался бы один раз и навсегда, а тариф после этого мог бы поменяться.
    /// Перечитываются в RefreshEntitlements() при каждом открытии меню.</summary>
    public bool CanViewReturn
    {
        get => _canViewReturn;
        private set => SetProperty(ref _canViewReturn, value);
    }

    public bool CanViewDeferredReceipts
    {
        get => _canViewDeferredReceipts;
        private set => SetProperty(ref _canViewDeferredReceipts, value);
    }

    public bool CanViewRestock
    {
        get => _canViewRestock;
        private set => SetProperty(ref _canViewRestock, value);
    }

    public bool CanViewFinance
    {
        get => _canViewFinance;
        private set => SetProperty(ref _canViewFinance, value);
    }

    /// <summary>«Зарплата» (2026-09-25): суммы начислений всех сотрудников — только тем, кому
    /// открыты настройки (владелец/администратор), и не на тарифе Старт, как «Финансы».</summary>
    public bool CanViewSalary
    {
        get => _canViewSalary;
        private set => SetProperty(ref _canViewSalary, value);
    }

    public bool CanViewSales
    {
        get => _canViewSales;
        private set => SetProperty(ref _canViewSales, value);
    }

    public bool CanViewPayDebt
    {
        get => _canViewPayDebt;
        private set => SetProperty(ref _canViewPayDebt, value);
    }

    public bool CanViewCrm
    {
        get => _canViewCrm;
        private set => SetProperty(ref _canViewCrm, value);
    }

    public bool CanViewErrorLogs
    {
        get => _canViewErrorLogs;
        private set => SetProperty(ref _canViewErrorLogs, value);
    }

    public bool CanViewRemoteSupport
    {
        get => _canViewRemoteSupport;
        private set => SetProperty(ref _canViewRemoteSupport, value);
    }

    public bool CanViewKnowledgeBase
    {
        get => _canViewKnowledgeBase;
        private set => SetProperty(ref _canViewKnowledgeBase, value);
    }

    /// <summary>2026-09-07: Маркетплейс НЕ ограничивается тарифом Старт (в отличие от остальных
    /// CanView* выше) — пользователь явно попросил оставить доступ к покупке/установке доп.
    /// функций на любом тарифе, иначе владелец Старта не смог бы вообще ничего докупить.
    /// Гейтится только правом доступа к настройкам, как и раньше.</summary>
    public bool CanViewMarketplace
    {
        get => _canViewMarketplace;
        private set => SetProperty(ref _canViewMarketplace, value);
    }

    /// <summary>Unlike the permission-based CanView* above, this depends on a purchasable
    /// doprop flag (UserPreferences.StaffTimesheetUnlocked) that can flip mid-session — right
    /// after buying it in the Marketplace, without restarting the app. UserPreferences has no
    /// change-notification event, so this is re-read explicitly via RefreshEntitlements()
    /// (called on every side-menu open, see MainWindowViewModel.ToggleSideMenu) rather than
    /// bound live. 2026-09-07: НЕ гейтится тарифом Старт — доп. функция, купленная через
    /// Маркетплейс, должна работать независимо от базового тарифа NurCRM (иначе покупка
    /// на Старте была бы бессмысленной).</summary>
    public bool CanViewStaffTimesheet
    {
        get => _canViewStaffTimesheet;
        private set => SetProperty(ref _canViewStaffTimesheet, value);
    }

    /// <summary>2026-09-07, реальный баг ("тариф Старт не работает" — пункт «Клиенты» оставался
    /// виден в меню даже на тарифе Старт): раньше это было такое же вычисляемое свойство-геттер,
    /// как CanViewWarehouse выше — единственный раз читалось при построении меню, ДО того как
    /// CompanyInfoService.LastCompany успевал загрузиться с сервера (компания подгружается
    /// асинхронно уже после того, как окно и меню построены — см. лог: "Company loaded"
    /// приходит примерно через секунду после запуска). TariffGate.IsStartTariff в этот момент
    /// всегда false (компания ещё не известна), поэтому "Клиенты" показывался и оставался
    /// показанным навсегда — сам клик по нему НЕ пускал (NavigateClients перечитывает
    /// TariffGate.IsStartTariff заново и показывает тост), но кнопка обманывала. Тот же приём,
    /// что уже применён для CanViewStaffTimesheet — сохраняемое значение, перечитывается в
    /// RefreshEntitlements() при каждом открытии меню.</summary>
    public bool CanViewClients
    {
        get => _canViewClients;
        private set => SetProperty(ref _canViewClients, value);
    }

    public void RefreshEntitlements()
    {
        var isStart = NurMarketKassa.Services.TariffGate.IsStartTariff;
        // 2026-09-26, разделение программ: склад, продажи, финансы, зарплата, ABC, клиенты, CRM и
        // пополнение — в программе владельца. В кассе они остаются только в автономном режиме
        // (см. AppMode.OwnerSectionsInKassa).
        var owner = NurMarketKassa.Services.AppMode.OwnerSectionsInKassa;
        OnPropertyChanged(nameof(CanViewWarehouse));
        CanViewStaffTimesheet = NurMarketKassa.Services.UserPreferences.Instance.StaffTimesheetUnlocked;
        CanViewReturn = (_permissions?.HasPermission(PosPermissions.EmployeeReturn) ?? true) && !isStart;
        CanViewDeferredReceipts = !isStart;
        CanViewRestock = !isStart && owner;
        CanViewFinance = !isStart && owner;
        CanViewSalary = (_permissions?.HasPermission(PosPermissions.ViewSettings) ?? true) && !isStart && owner;
        CanViewSales = (_permissions?.HasPermission(PosPermissions.ViewSales) ?? true) && !isStart && owner;
        CanViewClients = (_permissions?.HasPermission(PosPermissions.ViewSales) ?? true) && NurMarketKassa.Services.TariffGate.CanViewClients && owner;
        CanViewPayDebt = (_permissions?.HasPermission(PosPermissions.ViewSales) ?? true) && !isStart;
        CanViewCrm = !isStart && owner;
        CanViewErrorLogs = !isStart;
        CanViewRemoteSupport = !isStart;
        CanViewKnowledgeBase = !isStart;
        CanViewMarketplace = _permissions?.HasPermission(PosPermissions.ViewSettings) ?? true;
    }

    /// <summary>Три раздела меню сворачиваются по клику на заголовок (2026-09-05, по просьбе
    /// пользователя: "приведи в порядок бургер меню, главное что бы там не было так много") —
    /// все три свёрнуты по умолчанию, так что при открытии меню видно только заголовки разделов
    /// плюс всегда открытые Настройки/Сменить кассира/Выход внизу, а не полтора десятка пунктов
    /// сразу. Ничего не удалено — каждый пункт по-прежнему доступен, но на один клик дальше.</summary>
    public bool IsWorkSectionExpanded
    {
        get => _isWorkSectionExpanded;
        set => SetProperty(ref _isWorkSectionExpanded, value);
    }

    public bool IsInfoSectionExpanded
    {
        get => _isInfoSectionExpanded;
        set => SetProperty(ref _isInfoSectionExpanded, value);
    }

    public bool IsSystemSectionExpanded
    {
        get => _isSystemSectionExpanded;
        set => SetProperty(ref _isSystemSectionExpanded, value);
    }

    public ICommand CloseMenuCommand { get; }
    public ICommand NavigateWarehouseCommand { get; }
    public ICommand NavigateStaffTimesheetCommand { get; }
    public ICommand NavigateMarketplaceCommand { get; }
    public ICommand NavigateReturnCommand { get; }
    public ICommand NavigateDeferredReceiptsCommand { get; }
    public ICommand NavigateFinanceCommand { get; }
    public ICommand NavigateSalaryCommand { get; }

    /// <summary>«Внесение / изъятие» — под карточкой смены, видно только при открытой смене.</summary>
    public ICommand OpenCashOperationCommand { get; }
    public ICommand NavigateSalesCommand { get; }

    /// <summary>Раздел ABC-анализа. Видимость привязана к тому же праву, что и «Продажи»:
    /// это та же информация о выручке, только в другом разрезе.</summary>
    public ICommand NavigateAbcCommand { get; }
    public ICommand NavigateClientsCommand { get; }
    public ICommand NavigatePayDebtCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateCrmCommand { get; }
    public ICommand NavigateErrorLogsCommand { get; }
    public ICommand NavigateRemoteSupportCommand { get; }
    public ICommand NavigateKnowledgeBaseCommand { get; }
    public ICommand NavigateRestockCommand { get; }
    public ICommand ToggleWorkSectionCommand { get; }
    public ICommand ToggleInfoSectionCommand { get; }
    public ICommand ToggleSystemSectionCommand { get; }
    public ICommand SwitchCashierCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand ExitApplicationCommand { get; }
}
