using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;
using CashOp = NurMarketKassa.Models.CashOperationModel;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

public sealed class ShiftHistoryViewModel : INotifyPropertyChanged
{
    private const int ShiftPageSize = 10;
    private const int SummaryPageSize = 12;

    private readonly Window _owner;
    private bool _isLoading;
    private ShiftPage _activePage = ShiftPage.History;
    private ShiftModel? _selectedShift;
    private int _currentPage = 1;
    private int _summaryCurrentPage = 1;
    private DateTimeOffset? _filterDateFrom;
    private DateTimeOffset? _filterDateTo;
    private string _filterCashier = "Все кассиры";
    private string _filterOperationType = "Все типы";
    private List<CashOperationModel> _allFilteredOperations = new();

    public ShiftHistoryViewModel(Window owner)
    {
        _owner = owner;

        MinimizeCommand = new RelayCommand(() => _owner.WindowState = Avalonia.Controls.WindowState.Minimized);
        CloseCommand = new RelayCommand(() => _owner.Close());
        ShowHistoryCommand = new RelayCommand(() => ActivePage = ShiftPage.History);
        ShowSummaryCommand = new RelayCommand(() => ActivePage = ShiftPage.Summary);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        NewOperationCommand = new RelayCommand(OpenNewOperation);
        ExportSummaryCommand = new RelayCommand(ExportSummary);
        OpenShiftActionsCommand = new RelayCommand<ShiftModel>(OpenShiftActions);
        GoToPageCommand = new RelayCommand<int?>(GoToPage);
        PreviousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => CanGoToPreviousPage);
        NextPageCommand = new RelayCommand(() => GoToPage(CurrentPage + 1), () => CanGoToNextPage);
        GoToSummaryPageCommand = new RelayCommand<int?>(GoToSummaryPage);
        SummaryPreviousPageCommand = new RelayCommand(() => GoToSummaryPage(SummaryCurrentPage - 1), () => CanGoToPreviousSummaryPage);
        SummaryNextPageCommand = new RelayCommand(() => GoToSummaryPage(SummaryCurrentPage + 1), () => CanGoToNextSummaryPage);

        _ = LoadAsync();
    }

    public ObservableCollection<ShiftModel> AllShifts { get; } = new();
    public ObservableCollection<ShiftModel> PagedShifts { get; } = new();
    public ObservableCollection<CashOperationModel> RecentOperations { get; } = new();
    public ObservableCollection<CashOperationModel> PagedFilteredOperations { get; } = new();
    public ObservableCollection<PagerEntry> ShiftPagerEntries { get; } = new();
    public ObservableCollection<PagerEntry> SummaryPagerEntries { get; } = new();

    public ICommand MinimizeCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand ShowSummaryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand NewOperationCommand { get; }
    public ICommand ExportSummaryCommand { get; }
    public ICommand OpenShiftActionsCommand { get; }
    public ICommand GoToPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand GoToSummaryPageCommand { get; }
    public ICommand SummaryPreviousPageCommand { get; }
    public ICommand SummaryNextPageCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public ShiftPage ActivePage
    {
        get => _activePage;
        set
        {
            if (_activePage == value) return;
            _activePage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(IsSummaryPage));
            if (value == ShiftPage.Summary)
            {
                ApplySummaryFilters();
                RecalculateSummaryFromOperations(_allFilteredOperations);
            }
            else
                RecalculateSummary();
        }
    }

    public bool IsHistoryPage => ActivePage == ShiftPage.History;
    public bool IsSummaryPage => ActivePage == ShiftPage.Summary;

    public ShiftModel? SelectedShift
    {
        get => _selectedShift;
        set
        {
            if (_selectedShift == value) return;
            _selectedShift = value;
            OnPropertyChanged();
            RecalculateSummary();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            (PreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public int SummaryCurrentPage
    {
        get => _summaryCurrentPage;
        private set
        {
            _summaryCurrentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoToPreviousSummaryPage));
            OnPropertyChanged(nameof(CanGoToNextSummaryPage));
            (SummaryPreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SummaryNextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public int TotalPages { get; private set; } = 1;
    public int SummaryTotalPages { get; private set; } = 1;
    public bool CanGoToPreviousPage => CurrentPage > 1;
    public bool CanGoToNextPage => CurrentPage < TotalPages;
    public bool CanGoToPreviousSummaryPage => SummaryCurrentPage > 1;
    public bool CanGoToNextSummaryPage => SummaryCurrentPage < SummaryTotalPages;

    public DateTimeOffset? FilterDateFrom
    {
        get => _filterDateFrom;
        set { _filterDateFrom = value; OnPropertyChanged(); ApplySummaryFilters(); }
    }

    public DateTimeOffset? FilterDateTo
    {
        get => _filterDateTo;
        set { _filterDateTo = value; OnPropertyChanged(); ApplySummaryFilters(); }
    }

    public string FilterCashier
    {
        get => _filterCashier;
        set { _filterCashier = value ?? "Все кассиры"; OnPropertyChanged(); ApplySummaryFilters(); }
    }

    public string FilterOperationType
    {
        get => _filterOperationType;
        set { _filterOperationType = value ?? "Все типы"; OnPropertyChanged(); ApplySummaryFilters(); }
    }

    public ObservableCollection<string> CashierFilterOptions { get; } = new() { "Все кассиры" };
    public ObservableCollection<string> OperationTypeFilterOptions { get; } = new()
    {
        "Все типы", "Внесение", "Изъятие", "Открытие смены", "Закрытие смены",
    };

    public decimal OpeningBalance { get; private set; }
    public decimal DepositedTotal { get; private set; }
    public decimal WithdrawnTotal { get; private set; }
    public decimal ExpectedBalance { get; private set; }
    public decimal ActualBalance { get; private set; }
    public decimal Difference { get; private set; }
    public decimal RevenueTotal { get; private set; }

    public string OpeningBalanceText => FormatMoney(OpeningBalance);
    public string DepositedTotalText => $"+{FormatMoney(DepositedTotal)}";
    public string WithdrawnTotalText => $"-{FormatMoney(WithdrawnTotal)}";
    public string ExpectedBalanceText => FormatMoney(ExpectedBalance);
    public string ActualBalanceText => FormatMoney(ActualBalance);
    public string DifferenceText => FormatSignedMoney(Difference);
    public string RevenueTotalText => FormatMoney(RevenueTotal);
    public bool HasFilteredOperations => _allFilteredOperations.Count > 0;
    public bool ShowSummaryPager => SummaryTotalPages > 1;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var shifts = await ShiftHistoryService.LoadAsync().ConfigureAwait(true);
            AllShifts.Clear();
            foreach (var shift in shifts.Select(ShiftModel.FromEntry))
                AllShifts.Add(shift);

            if (SelectedShift == null && AllShifts.Count > 0)
                SelectedShift = AllShifts[0];
            else if (SelectedShift != null)
                SelectedShift = AllShifts.FirstOrDefault(s => s.Id == SelectedShift.Id) ?? AllShifts.FirstOrDefault();

            ReloadOperations();
            RebuildShiftPagination();
            RecalculateSummary();
            ApplySummaryFilters();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShiftHistory LoadAsync failed: {ex}", "SHIFTS");
            PosMessageBox.Show(_owner, $"Не удалось загрузить данные: {ex.Message}", "История смен",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ReloadOperations()
    {
        var ops = ShiftCashOperationsStore.LoadAll();
        RecentOperations.Clear();
        foreach (var op in ops.Take(6))
            RecentOperations.Add(op);

        CashierFilterOptions.Clear();
        CashierFilterOptions.Add("Все кассиры");
        foreach (var cashier in ops.Select(o => o.Cashier).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            CashierFilterOptions.Add(cashier);
    }

    private void RebuildShiftPagination()
    {
        TotalPages = Math.Max(1, (int)Math.Ceiling(AllShifts.Count / (double)ShiftPageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        UpdatePagerEntries(ShiftPagerEntries, CurrentPage, TotalPages);
        PagedShifts.Clear();
        foreach (var shift in AllShifts.Skip((CurrentPage - 1) * ShiftPageSize).Take(ShiftPageSize))
            PagedShifts.Add(shift);
        OnPropertyChanged(nameof(TotalPages));
    }

    private void RebuildSummaryPagination()
    {
        SummaryTotalPages = Math.Max(1, (int)Math.Ceiling(_allFilteredOperations.Count / (double)SummaryPageSize));
        if (SummaryCurrentPage > SummaryTotalPages) SummaryCurrentPage = SummaryTotalPages;
        UpdatePagerEntries(SummaryPagerEntries, SummaryCurrentPage, SummaryTotalPages);
        PagedFilteredOperations.Clear();
        foreach (var op in _allFilteredOperations.Skip((SummaryCurrentPage - 1) * SummaryPageSize).Take(SummaryPageSize))
            PagedFilteredOperations.Add(op);
        OnPropertyChanged(nameof(SummaryTotalPages));
        OnPropertyChanged(nameof(ShowSummaryPager));
    }

    private static void UpdatePagerEntries(ObservableCollection<PagerEntry> target, int current, int total)
    {
        target.Clear();
        foreach (var entry in BuildPagerEntries(current, total))
            target.Add(entry);
    }

    private static IEnumerable<PagerEntry> BuildPagerEntries(int current, int total)
    {
        if (total <= 0) yield break;
        if (total == 1)
        {
            yield return new PagerEntry { Kind = PagerEntryKind.Page, PageNumber = 1, IsCurrent = true };
            yield break;
        }

        var pages = new SortedSet<int> { 1, total, current };
        for (var i = current - 1; i <= current + 1; i++)
            if (i >= 1 && i <= total) pages.Add(i);
        if (current <= 3)
            for (var i = 2; i <= Math.Min(5, total - 1); i++) pages.Add(i);
        if (current >= total - 2)
            for (var i = Math.Max(2, total - 4); i <= total - 1; i++) pages.Add(i);

        int? previous = null;
        foreach (var page in pages)
        {
            if (previous is { } prev && page - prev > 1)
                yield return new PagerEntry { Kind = PagerEntryKind.Ellipsis };
            yield return new PagerEntry { Kind = PagerEntryKind.Page, PageNumber = page, IsCurrent = page == current };
            previous = page;
        }
    }

    private void GoToPage(int? page)
    {
        if (page is not int target || target < 1 || target > TotalPages) return;
        CurrentPage = target;
        RebuildShiftPagination();
    }

    private void GoToSummaryPage(int? page)
    {
        if (page is not int target || target < 1 || target > SummaryTotalPages) return;
        SummaryCurrentPage = target;
        RebuildSummaryPagination();
    }

    private void RecalculateSummary()
    {
        if (ActivePage == ShiftPage.Summary)
        {
            RecalculateSummaryFromOperations(_allFilteredOperations);
            return;
        }

        var shift = SelectedShift;
        RevenueTotal = shift?.Revenue ?? 0m;
        var ops = ShiftCashOperationsStore.LoadAll();
        if (shift?.OpenedAt is { } opened)
        {
            var closed = shift.ClosedAt ?? DateTime.Now;
            ops = ops.Where(o => o.CreatedAt >= opened && o.CreatedAt <= closed).ToList();
        }
        RecalculateSummaryFromOperations(ops, shiftRevenue: RevenueTotal);
    }

    private void RecalculateSummaryFromOperations(IEnumerable<CashOperationModel> ops, decimal? shiftRevenue = null)
    {
        var list = ops as IList<CashOperationModel> ?? ops.ToList();
        RevenueTotal = shiftRevenue ?? list.Where(o => o.Kind == CashOperationKind.ShiftClose).Sum(o => o.Amount);
        OpeningBalance = list.Where(o => o.Kind == CashOperationKind.OpeningBalance).Sum(o => o.Amount);
        DepositedTotal = list.Where(o => o.Kind == CashOperationKind.Deposit).Sum(o => o.Amount);
        WithdrawnTotal = list.Where(o => o.Kind == CashOperationKind.Withdrawal).Sum(o => o.Amount);
        ExpectedBalance = OpeningBalance + DepositedTotal - WithdrawnTotal + RevenueTotal;
        ActualBalance = list.Aggregate(0m, (sum, op) => op.Kind switch
        {
            CashOperationKind.Deposit or CashOperationKind.OpeningBalance => sum + op.Amount,
            CashOperationKind.Withdrawal => sum - op.Amount,
            _ => sum,
        });
        Difference = ActualBalance - ExpectedBalance;
        NotifySummaryProperties();
    }

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(OpeningBalanceText));
        OnPropertyChanged(nameof(DepositedTotalText));
        OnPropertyChanged(nameof(WithdrawnTotalText));
        OnPropertyChanged(nameof(ExpectedBalanceText));
        OnPropertyChanged(nameof(ActualBalanceText));
        OnPropertyChanged(nameof(DifferenceText));
        OnPropertyChanged(nameof(RevenueTotalText));
    }

    private void ApplySummaryFilters()
    {
        var query = ShiftCashOperationsStore.LoadAll().AsEnumerable();
        if (FilterDateFrom is { } from) query = query.Where(o => o.CreatedAt.Date >= from.Date);
        if (FilterDateTo is { } to) query = query.Where(o => o.CreatedAt.Date <= to.Date);
        if (!string.IsNullOrWhiteSpace(FilterCashier) && FilterCashier != "Все кассиры")
            query = query.Where(o => string.Equals(o.Cashier, FilterCashier, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(FilterOperationType) && FilterOperationType != "Все типы")
            query = query.Where(o => (o.Type ?? "").Contains(FilterOperationType, StringComparison.OrdinalIgnoreCase));

        _allFilteredOperations = query.OrderByDescending(o => o.CreatedAt).ToList();
        SummaryCurrentPage = 1;
        RebuildSummaryPagination();
        if (ActivePage == ShiftPage.Summary)
            RecalculateSummaryFromOperations(_allFilteredOperations);
        OnPropertyChanged(nameof(HasFilteredOperations));
    }

    private void OpenNewOperation()
    {
        var dialog = new NewOperationDialog();
        if (PosDialogHost.Show(dialog, _owner) == true && dialog.ResultOperation is { } op)
        {
            ShiftCashOperationsStore.Append(op);
            ReloadOperations();
            RecalculateSummary();
            ApplySummaryFilters();
        }
    }

    private void OpenShiftActions(ShiftModel? shift)
    {
        if (shift == null) return;
        SelectedShift = shift;
        var menu = new ShiftActionsMenu();
        if (PosDialogHost.Show(menu, _owner) != true || menu.SelectedAction is not { } action)
            return;

        switch (action)
        {
            case ShiftMenuAction.View:
                PosDialogHost.Show(new ShiftDetailsDialog(shift), _owner);
                break;
            case ShiftMenuAction.Print:
                PosMessageBox.Show(_owner, $"Z-отчёт по смене {shift.ShiftNumber} будет доступен в следующем обновлении.",
                    "Z-отчёт", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case ShiftMenuAction.Deposit:
                OpenTypedOperation(isDeposit: true);
                break;
            case ShiftMenuAction.Withdraw:
                OpenTypedOperation(isDeposit: false);
                break;
            case ShiftMenuAction.Export:
                PosMessageBox.Show(_owner, $"X-отчёт по смене {shift.ShiftNumber} будет доступен в следующем обновлении.",
                    "X-отчёт", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case ShiftMenuAction.Delete:
                if (PosMessageBox.Show(_owner, $"Удалить смену {shift.ShiftNumber} из локального списка?",
                        "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    AllShifts.Remove(shift);
                    SelectedShift = AllShifts.FirstOrDefault();
                    RebuildShiftPagination();
                }
                break;
        }
    }

    private void OpenTypedOperation(bool isDeposit)
    {
        var dialog = new NewOperationDialog(isDeposit);
        if (PosDialogHost.Show(dialog, _owner) == true && dialog.ResultOperation is { } op)
        {
            ShiftCashOperationsStore.Append(op);
            ReloadOperations();
            RecalculateSummary();
            ApplySummaryFilters();
        }
    }

    private void ExportSummary() =>
        PosMessageBox.Show(_owner, "Экспорт общего плана будет доступен в следующем обновлении.",
            "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);

    private static string FormatMoney(decimal value) => $"{value:N2} сом";
    private static string FormatSignedMoney(decimal value) =>
        value >= 0 ? $"+{value:N2} сом" : $"{value.ToString("N2", CultureInfo.InvariantCulture)} сом";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
