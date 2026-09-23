using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Input;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Каталог кассира: Offline-First (SQLite → UI), затем фоновая синхронизация с API.
/// </summary>
public sealed class CatalogPanelViewModel : ViewModelBase
{
    private const int PageSize = 50;
    private const int MaxVisiblePagerButtons = 9;

    private readonly ICatalogCacheService _catalogCache;
    private readonly IDispatcher _dispatcher;
    private readonly IConnectivityService _connectivity;
    private readonly Action<CatalogProductTileVm>? _onProductSelected;
    private readonly ICatalogApiService? _catalogApi;
    private readonly MySqlAuditService? _auditDb;
    private readonly IUserPrompts? _prompts;

    private int _selectedTabIndex;
    private string _searchText = "";
    private bool _isLoading;
    private bool _isOfflineBannerVisible;
    private string _statusText = "Загрузка каталога…";
    private string _productCountText = "";
    private List<CatalogProductTileVm> _allProducts = [];
    private int _currentPage = 1;
    private int _totalPages = 1;
    private int _filteredProductCount;
    private readonly int[] _tabPages = [1, 1, 1];
    private CatalogFilterCriteria? _advancedFilter;
    private CatalogViewMode _viewMode = UserPreferences.Instance.CatalogViewMode;

    public CatalogPanelViewModel(
        ICatalogCacheService catalogCache,
        IDispatcher dispatcher,
        IConnectivityService connectivity,
        Action<CatalogProductTileVm>? onProductSelected = null,
        ICatalogApiService? catalogApi = null,
        MySqlAuditService? auditDb = null,
        IUserPrompts? prompts = null)
    {
        _catalogCache = catalogCache;
        _dispatcher = dispatcher;
        _connectivity = connectivity;
        _onProductSelected = onProductSelected;
        _catalogApi = catalogApi;
        _auditDb = auditDb;
        _prompts = prompts;

        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(SearchText));
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync, () => !IsLoading);
        SelectProductCommand = new RelayCommand<CatalogProductTileVm>(SelectProduct);
        ToggleFavoriteCommand = new RelayCommand<CatalogProductTileVm>(vm => _ = ToggleFavoriteAsync(vm));
        OpenFilterCommand = new RelayCommand(() => { /* модуль «Фильтр» */ });
        OpenWarehouseCommand = new RelayCommand(() => { /* модуль «Склад» */ });
        PreviousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => CanGoToPreviousPage);
        NextPageCommand = new RelayCommand(() => GoToPage(CurrentPage + 1), () => CanGoToNextPage);
        GoToPageCommand = new RelayCommand<int?>(GoToPage);

        Products.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProducts));
            OnPropertyChanged(nameof(IsCatalogEmpty));
        };
    }

    public ObservableCollection<CatalogTabVm> Tabs { get; } =
    [
        new CatalogTabVm("Все товары", "\uE8B7"),
        new CatalogTabVm("Весовые", "\uE9D9"),
        new CatalogTabVm("Штучные", "\uE7B8"),
    ];

    public event EventHandler? StateChanged;

    public ObservableCollection<CatalogProductTileVm> Products { get; } = new();
    public ObservableCollection<CatalogPagerEntry> PagerEntries { get; } = new();

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, value))
                return;
            // A category switch is a new catalogue context.  Always start it
            // from its first page, rather than leaving the user on page N.
            CurrentPage = 1;
            ApplyFilter();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? ""))
                return;
            (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            ResetCurrentPage();
            ApplyFilter();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;
            OnPropertyChanged(nameof(IsCatalogEmpty));
            (RefreshCatalogCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Плашка «работа из локальной БД / автономный режим».</summary>
    public bool IsOfflineBannerVisible
    {
        get => _isOfflineBannerVisible;
        private set => SetProperty(ref _isOfflineBannerVisible, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value ?? "");
    }

    public string ProductCountText
    {
        get => _productCountText;
        set => SetProperty(ref _productCountText, value ?? "");
    }

    public bool HasProducts => Products.Count > 0;

    public bool IsCatalogEmpty => !HasProducts;

    public CatalogViewMode ViewMode
    {
        get => _viewMode;
        private set
        {
            if (!SetProperty(ref _viewMode, value))
                return;
            OnPropertyChanged(nameof(IsCardView));
            OnPropertyChanged(nameof(IsTableView));
        }
    }

    public bool IsCardView => ViewMode == CatalogViewMode.Cards;
    public bool IsTableView => ViewMode == CatalogViewMode.Table;

    public void SetViewMode(CatalogViewMode mode)
    {
        ViewMode = mode;
        UserPreferences.Instance.CatalogViewMode = mode;
        UserPreferences.Instance.SaveToDisk();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> AvailableCategories => _allProducts
        .Select(product => product.Category)
        .Where(category => !string.IsNullOrWhiteSpace(category))
        .Cast<string>()
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public CatalogFilterViewModel CreateFilterViewModel() =>
        new(_allProducts, _advancedFilter, SearchText);

    public void ApplyAdvancedFilter(CatalogFilterCriteria criteria)
    {
        _searchText = criteria.SearchText;
        OnPropertyChanged(nameof(SearchText));
        (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _advancedFilter = criteria with { SearchText = "" };
        ResetCurrentPage();
        ApplyFilter();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            var normalized = Math.Max(1, value);
            if (!SetProperty(ref _currentPage, normalized))
                return;

            SetTabPage(_selectedTabIndex, normalized);
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(PageStatusText));
            RaisePagerCommandState();
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (!SetProperty(ref _totalPages, Math.Max(1, value)))
                return;

            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(ShowPager));
            OnPropertyChanged(nameof(PageStatusText));
            RaisePagerCommandState();
        }
    }

    public bool CanGoToPreviousPage => CurrentPage > 1;
    public bool CanGoToNextPage => CurrentPage < TotalPages;
    public bool ShowPager => _filteredProductCount > 0;
    public string PageStatusText => $"Страница {CurrentPage} из {TotalPages}";

    public ICommand ClearSearchCommand { get; }
    public ICommand RefreshCatalogCommand { get; }
    public ICommand SelectProductCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand OpenFilterCommand { get; }
    public ICommand OpenWarehouseCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand GoToPageCommand { get; }


    public CatalogPanelState CaptureState() =>
        new()
        {
            SelectedTabIndex = SelectedTabIndex,
            SearchText = SearchText,
            TabPages = [.. _tabPages],
        };

    public void RestoreState(CatalogPanelState? state)
    {
        if (state is null)
            return;

        for (var index = 0; index < _tabPages.Length && index < state.TabPages.Length; index++)
            _tabPages[index] = Math.Max(1, state.TabPages[index]);

        _selectedTabIndex = Math.Clamp(state.SelectedTabIndex, 0, _tabPages.Length - 1);
        _searchText = state.SearchText ?? "";
        _currentPage = GetTabPage(_selectedTabIndex);
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(SearchText));
        (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        ApplyFilter();
    }
    private void ClearSearch() => SearchText = "";

    private void SelectProduct(CatalogProductTileVm? product)
    {
        if (product is null)
            return;
        _onProductSelected?.Invoke(product);
    }

    private async Task ToggleFavoriteAsync(CatalogProductTileVm? product)
    {
        if (product is null)
            return;

        var newState = !product.IsFavorite;
        product.IsFavorite = newState;
        CatalogCacheService.SetFavorite(product.Id, newState);
        ApplyFilter();

        if (_catalogApi is null)
        {
            _prompts?.ShowToast(
                newState ? "Добавлено в избранное (локально)" : "Убрано из избранного (локально)");
            return;
        }

        try
        {
            var synced = await _catalogApi
                .SetProductFavoriteAsync(product.Id, newState)
                .ConfigureAwait(true);
            if (synced)
            {
                _auditDb?.LogFavorite(product.Id, newState);
                _prompts?.ShowToast(
                    newState ? "Добавлено в избранное на сайте" : "Убрано из избранного на сайте");
            }
            else
            {
                _prompts?.ShowToast("Избранное сохранено локально (сайт не ответил)", isWarning: true);
            }
        }
        catch (ApiException ex)
        {
            _prompts?.ShowToast($"Синхронизация избранного: {ex.Message}", isWarning: true);
        }
        catch (HttpRequestException)
        {
            _prompts?.ShowToast("Избранное сохранено локально (нет сети)", isWarning: true);
        }
    }

    private async Task RefreshCatalogAsync()
    {
        await _dispatcher.InvokeAsync(() => IsLoading = true).ConfigureAwait(false);

        try
        {
            // 1) Мгновенно показываем SQLite.
            var loadedFromDb = _catalogCache.TryLoadFromDatabase();
            var localProducts = _catalogCache.GetProducts().ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                PublishProducts(localProducts);
                if (localProducts.Count > 0)
                {
                    StatusText = $"Каталог из локальной БД ({localProducts.Count}). Проверка обновлений…";
                    IsOfflineBannerVisible = false;
                }
                else
                {
                    StatusText = "Локальный каталог пуст. Загрузка с сервера…";
                }
            }).ConfigureAwait(false);

            var online = await IsOnlineAsync().ConfigureAwait(false);
            if (!online)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsOfflineBannerVisible = true;
                    StatusText = localProducts.Count > 0
                        ? $"Работа в автономном режиме (из локальной БД). Товаров: {localProducts.Count}."
                        : "Нет подключения и локальный каталог пуст. Проверьте сеть и нажмите «Обновить».";
                }).ConfigureAwait(false);
                return;
            }

            // 2) Сеть доступна — синхронизация с API + upsert в SQLite.
            await _dispatcher.InvokeAsync(() =>
                StatusText = localProducts.Count > 0
                    ? "Синхронизация каталога с сервером…"
                    : "Загрузка каталога с сервера…").ConfigureAwait(false);

            var syncResult = await _catalogCache.SyncCatalogFullAsync().ConfigureAwait(false);
            _catalogCache.TryLoadFromDatabase();
            var products = _catalogCache.GetProducts().ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                PublishProducts(products);

                if (syncResult.Success)
                {
                    IsOfflineBannerVisible = false;
                    StatusText = products.Count > 0
                        ? $"Каталог обновлён. Товаров: {products.Count}."
                        : "Каталог на сервере пуст.";
                }
                else
                {
                    IsOfflineBannerVisible = products.Count > 0;
                    StatusText = products.Count > 0
                        ? $"Сервер недоступен — показан локальный каталог ({products.Count}). {syncResult.ErrorMessage}"
                        : syncResult.ErrorMessage ?? "Не удалось загрузить каталог.";
                }
            }).ConfigureAwait(false);

            PosLogger.Log(
                $"CATALOG refresh done: localWas={loadedFromDb}, online={online}, " +
                $"success={syncResult.Success}, count={products.Count}",
                "CATALOG");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG refresh failed: {ex}", "CATALOG");
            await _dispatcher.InvokeAsync(() =>
            {
                IsOfflineBannerVisible = _allProducts.Count > 0;
                StatusText = _allProducts.Count > 0
                    ? $"Ошибка синхронизации — показан локальный каталог ({_allProducts.Count})."
                    : "Ошибка загрузки каталога.";
            }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    private void PublishProducts(List<CatalogProductTileVm> products)
    {
        _allProducts = products;
        ResetCurrentPage();
        ApplyFilter();
        ProductCountText = $"Товаров: {_allProducts.Count}";
    }

    private async Task<bool> IsOnlineAsync()
    {
        try
        {
            return await _connectivity.IsOnlineAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Connectivity check failed: {ex}", "NETWORK");
            return false;
        }
    }

    private void ApplyFilter()
    {
        Products.Clear();
        var query = _searchText.Trim();
        var filtered = _allProducts
            .Where(MatchesTab)
            .Where(p => MatchesSearch(p, query))
            .Where(MatchesAdvancedFilter)
            .OrderByDescending(p => p.IsFavorite)
            .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _filteredProductCount = filtered.Count;
        OnPropertyChanged(nameof(ShowPager));
        TotalPages = Math.Max(1, (int)Math.Ceiling(_filteredProductCount / (double)PageSize));
        if (CurrentPage > TotalPages)
            CurrentPage = TotalPages;

        foreach (var product in filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize))
            Products.Add(product);

        ProductCountText = _filteredProductCount == _allProducts.Count
            ? $"Товаров: {_allProducts.Count}"
            : $"Показано: {_filteredProductCount} из {_allProducts.Count}";

        RebuildPagerEntries();
    }

    private void GoToPage(int? page)
    {
        if (page is not int target || target < 1 || target > TotalPages || target == CurrentPage)
            return;

        CurrentPage = target;
        ApplyFilter();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetCurrentPage() => CurrentPage = 1;

    private int GetTabPage(int tabIndex) =>
        tabIndex >= 0 && tabIndex < _tabPages.Length ? _tabPages[tabIndex] : 1;

    private void SetTabPage(int tabIndex, int page)
    {
        if (tabIndex >= 0 && tabIndex < _tabPages.Length)
            _tabPages[tabIndex] = Math.Max(1, page);
    }

    private void RebuildPagerEntries()
    {
        PagerEntries.Clear();
        foreach (var entry in BuildPagerEntries(CurrentPage, TotalPages))
            PagerEntries.Add(entry);
    }

    private static IEnumerable<CatalogPagerEntry> BuildPagerEntries(int current, int total)
    {
        if (total <= MaxVisiblePagerButtons)
        {
            for (var page = 1; page <= total; page++)
                yield return CatalogPagerEntry.Page(page, page == current);
            yield break;
        }

        var pages = new SortedSet<int> { 1, total };
        for (var page = Math.Max(2, current - 2); page <= Math.Min(total - 1, current + 2); page++)
            pages.Add(page);

        var previous = 0;
        foreach (var page in pages)
        {
            if (previous > 0 && page - previous > 1)
                yield return CatalogPagerEntry.Ellipsis();

            yield return CatalogPagerEntry.Page(page, page == current);
            previous = page;
        }
    }

    private void RaisePagerCommandState()
    {
        (PreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoToPageCommand as RelayCommand<int?>)?.RaiseCanExecuteChanged();
    }

    private bool MatchesTab(CatalogProductTileVm product) => _selectedTabIndex switch
    {
        1 => product.MustWeigh,
        2 => !product.MustWeigh,
        _ => true,
    };

    private bool MatchesAdvancedFilter(CatalogProductTileVm product)
    {
        if (_advancedFilter is null)
            return true;

        var filter = _advancedFilter;
        if (!string.IsNullOrWhiteSpace(filter.SearchText) && !MatchesSearch(product, filter.SearchText))
            return false;
        if (filter.Kind == "Весовые" && !product.MustWeigh || filter.Kind == "Штучные" && product.MustWeigh)
            return false;
        if (filter.Kind == "Избранные" && !product.IsFavorite)
            return false;
        if (!string.IsNullOrWhiteSpace(filter.Category) &&
            !string.Equals(product.Category, filter.Category, StringComparison.CurrentCultureIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(filter.Brand) &&
            !string.Equals(product.Brand, filter.Brand, StringComparison.CurrentCultureIgnoreCase))
            return false;
        var price = ParseProductPrice(product.PriceLine);
        if (filter.PriceMin.HasValue && price < filter.PriceMin.Value)
            return false;
        if (filter.PriceMax.HasValue && price > filter.PriceMax.Value)
            return false;
        return !filter.OnlyInStock || product.Quantity > 0;
    }

    private static double ParseProductPrice(string? value)
    {
        var match = Regex.Match(value ?? "", @"\d+(?:[\.,]\d+)?");
        return match.Success && double.TryParse(
            match.Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
            ? price
            : 0;
    }

    private static bool MatchesSearch(CatalogProductTileVm product, string query)
    {
        if (query.Length < 2)
            return true;

        var q = query.ToLowerInvariant();
        return product.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (product.Barcode?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || product.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (product.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

/// <summary>
/// Вкладка фильтра каталога (все / весовые / штучные).
/// </summary>
public sealed class CatalogTabVm
{
    public CatalogTabVm(string title, string iconGlyph)
    {
        Title = title;
        IconGlyph = iconGlyph;
    }

    public string Title { get; }
    public string IconGlyph { get; }
}

public sealed class CatalogPagerEntry
{
    public CatalogPagerEntryKind Kind { get; init; }
    public int? PageNumber { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsPage => Kind == CatalogPagerEntryKind.Page;
    public bool IsEllipsis => Kind == CatalogPagerEntryKind.Ellipsis;
    public string Display => IsEllipsis ? "…" : PageNumber?.ToString() ?? "";

    public static CatalogPagerEntry Page(int pageNumber, bool isCurrent) =>
        new() { Kind = CatalogPagerEntryKind.Page, PageNumber = pageNumber, IsCurrent = isCurrent };

    public static CatalogPagerEntry Ellipsis() =>
        new() { Kind = CatalogPagerEntryKind.Ellipsis };
}

public enum CatalogPagerEntryKind
{
    Page,
    Ellipsis,
}
