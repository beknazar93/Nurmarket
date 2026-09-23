using System.Collections.ObjectModel;

using System.Globalization;

using NurMarketKassa.Core.Contracts;

using NurMarketKassa.Models;

using NurMarketKassa.Models.Pos;

using NurMarketKassa.Ui.Shared;
using NurMarketKassa.Services;



namespace NurMarketKassa.ViewModels.Catalog;



public sealed class CatalogViewModel : ViewModelBase, IDisposable

{

    private const int SearchDebounceMs = 275;



    private readonly ICatalogCacheService _catalogCache;

    private readonly IDispatcher _dispatcher;

    private readonly IAppSession _session;

    private readonly IConnectivityService _connectivity;



    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly object _searchCancellationSync = new();

    private CancellationTokenSource? _searchDebounceCts;

    private bool _disposed;

    private bool _catalogLoadBusy;

    private string _searchText = "";

    private CategoryDto? _selectedCategory;

    private FilterCriteria? _activeFilter;

    private bool _isCatalogLoading;



    public CatalogViewModel(

        ICatalogCacheService catalogCache,

        IDispatcher dispatcher,

        IAppSession session,

        IConnectivityService connectivity)

    {

        _catalogCache = catalogCache;

        _dispatcher = dispatcher;

        _session = session;

        _connectivity = connectivity;

    }



    public ObservableCollection<CatalogProductTileVm> RemoteTiles { get; } = new();



    public ObservableCollection<CatalogProductTileVm> FilteredTiles { get; } = new();



    public ObservableCollection<CategoryDto> Categories { get; } = new();



    public CategoryDto? SelectedCategory

    {

        get => _selectedCategory;

        set

        {

            if (!SetProperty(ref _selectedCategory, value))

                return;

            _ = RunCatalogSearchAsync();

        }

    }



    public string SearchText

    {

        get => _searchText;

        set

        {

            if (!SetProperty(ref _searchText, value ?? ""))

                return;

            ScheduleDebouncedSearch();

        }

    }



    public bool IsCatalogLoading

    {

        get => _isCatalogLoading;

        private set => SetProperty(ref _isCatalogLoading, value);

    }



    public FilterCriteria? ActiveFilter

    {

        get => _activeFilter;

        set

        {

            if (!SetProperty(ref _activeFilter, value))

                return;

            _ = RunCatalogSearchAsync();

        }

    }



    public event Action<CatalogLoadCompletedEventArgs>? LoadCompleted;



    public async Task LoadCatalogAsync(CancellationToken cancellationToken = default, bool manual = false)

    {

        if (_catalogLoadBusy)

            return;



        if (await ShouldUseLocalCatalogAsync(cancellationToken).ConfigureAwait(false))

        {

            var restored = await TryRestoreFromCacheAsync().ConfigureAwait(false);

            await RaiseLoadCompletedAsync(new CatalogLoadCompletedEventArgs

            {

                Success = restored,

                RestoredFromCache = restored,

                ProductCount = restored ? await CountRemoteTilesAsync().ConfigureAwait(false) : 0,

            }).ConfigureAwait(false);

            return;

        }



        _catalogLoadBusy = true;

        await _dispatcher.InvokeAsync(() => IsCatalogLoading = true).ConfigureAwait(false);

        try

        {

            if (manual)

                await Task.Delay(80, cancellationToken).ConfigureAwait(false);



            var result = await _catalogCache

                .SyncCatalogFullAsync(cancellationToken)

                .ConfigureAwait(false);



            if (!result.Success)

            {

                var restored = await TryRestoreFromCacheAsync().ConfigureAwait(false);

                await RaiseLoadCompletedAsync(new CatalogLoadCompletedEventArgs

                {

                    Success = restored,

                    RestoredFromCache = restored,

                    ErrorMessage = result.ErrorMessage,

                    ProductCount = await CountRemoteTilesAsync().ConfigureAwait(false),

                }).ConfigureAwait(false);

                return;

            }



            await ApplyCatalogDataAsync().ConfigureAwait(false);

            await RaiseLoadCompletedAsync(new CatalogLoadCompletedEventArgs

            {

                Success = true,

                Added = result.Added,

                Changed = result.Changed,

                Deleted = result.Deleted,

                ProductCount = await CountRemoteTilesAsync().ConfigureAwait(false),

                ManualRefresh = manual,

            }).ConfigureAwait(false);

        }

        catch (TaskCanceledException)

        {

            var restored = await TryRestoreFromCacheAsync().ConfigureAwait(false);

            await RaiseLoadCompletedAsync(new CatalogLoadCompletedEventArgs

            {

                Success = restored,

                RestoredFromCache = restored,

                ErrorMessage = restored ? null : "Каталог: таймаут.",

                ProductCount = await CountRemoteTilesAsync().ConfigureAwait(false),

            }).ConfigureAwait(false);

        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)

        {

            throw;

        }

        catch (Exception ex)

        {

            var restored = await TryRestoreFromCacheAsync().ConfigureAwait(false);

            await RaiseLoadCompletedAsync(new CatalogLoadCompletedEventArgs

            {

                Success = restored,

                RestoredFromCache = restored,

                ErrorMessage = ex.Message,

                ProductCount = await CountRemoteTilesAsync().ConfigureAwait(false),

            }).ConfigureAwait(false);

        }

        finally

        {

            _catalogLoadBusy = false;

            await _dispatcher.InvokeAsync(() => IsCatalogLoading = false).ConfigureAwait(false);

        }

    }



    public void RefreshCategoriesAndBrands()

    {

        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in RemoteTiles)

        {

            if (!string.IsNullOrWhiteSpace(vm.Category))

                categories.Add(vm.Category);

        }



        Categories.Clear();

        foreach (var name in categories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))

            Categories.Add(new CategoryDto { Name = name });

    }



    public Task RunCatalogSearchAsync() =>

        _dispatcher.InvokeAsync(ApplyFilterToFilteredTiles);



    public bool FilterPredicate(CatalogProductTileVm vm)

    {

        if (_activeFilter != null)

        {

            if (_activeFilter.DateFrom.HasValue && vm.CreatedAt < _activeFilter.DateFrom.Value)

                return false;

            if (_activeFilter.DateTo.HasValue && vm.CreatedAt > _activeFilter.DateTo.Value)

                return false;



            if (!string.IsNullOrEmpty(_activeFilter.Category) &&

                !string.Equals(vm.Category ?? "", _activeFilter.Category, StringComparison.OrdinalIgnoreCase))

                return false;



            if (!string.IsNullOrEmpty(_activeFilter.Brand) &&

                !string.Equals(vm.Brand ?? "", _activeFilter.Brand, StringComparison.OrdinalIgnoreCase))

                return false;



            if (!string.IsNullOrEmpty(_activeFilter.Client) &&

                !string.Equals(vm.ClientName ?? "", _activeFilter.Client, StringComparison.OrdinalIgnoreCase))

                return false;



            if (!string.IsNullOrEmpty(_activeFilter.Status) &&

                !string.Equals(vm.Status ?? "", _activeFilter.Status, StringComparison.OrdinalIgnoreCase))

                return false;



            if (!string.IsNullOrEmpty(_activeFilter.HotkeyGroup) &&

                !string.Equals(vm.HotkeyGroup ?? "", _activeFilter.HotkeyGroup, StringComparison.OrdinalIgnoreCase))

                return false;



            if (!string.IsNullOrWhiteSpace(_activeFilter.SearchQuery))

            {

                var query = _activeFilter.SearchQuery.Trim().ToLowerInvariant();

                if (!MatchesSearchQuery(vm, query))

                    return false;

            }



            if (_activeFilter.OnlyWeight && !vm.MustWeigh)

                return false;

            if (_activeFilter.OnlyPiece && vm.MustWeigh)

                return false;

            if (_activeFilter.OnlyInStock && vm.Quantity <= 0)

                return false;

            if (_activeFilter.OnlyFavorite && !vm.IsFavorite)

                return false;



            var price = TryParseTilePrice(vm.PriceLine);

            if (_activeFilter.PriceMin.HasValue && price < _activeFilter.PriceMin.Value)

                return false;

            if (_activeFilter.PriceMax.HasValue && price > _activeFilter.PriceMax.Value)

                return false;

        }



        if (_selectedCategory != null &&

            !string.Equals(vm.Category ?? "", _selectedCategory.Name, StringComparison.OrdinalIgnoreCase))

            return false;



        var text = _searchText.Trim();

        if (text.Length >= 2 && !MatchesSearchQuery(vm, text.ToLowerInvariant()))

            return false;



        return true;

    }



    public void Dispose()

    {

        lock (_searchCancellationSync)

        {

            if (_disposed)

                return;

            _disposed = true;

            _searchDebounceCts?.Cancel();

            _lifetimeCts.Cancel();

        }

        _lifetimeCts.Dispose();

    }



    private async Task<bool> ShouldUseLocalCatalogAsync(CancellationToken cancellationToken)

    {

        if (_session.IsOfflineBootstrap)

            return true;



        try

        {

            return !await _connectivity.IsOnlineAsync(cancellationToken).ConfigureAwait(false);

        }

        catch

        {

            return true;

        }

    }



    private async Task<bool> TryRestoreFromCacheAsync()

    {

        if (!_catalogCache.TryLoadFromDatabase())

            return false;



        await ApplyCatalogDataAsync().ConfigureAwait(false);

        return await CountRemoteTilesAsync().ConfigureAwait(false) > 0;

    }



    private Task ApplyCatalogDataAsync()

    {

        var products = _catalogCache.GetProducts();

        return _dispatcher.InvokeAsync(() =>

        {

            RemoteTiles.Clear();

            foreach (var tile in products)

                RemoteTiles.Add(tile);

            RefreshCategoriesAndBrands();

            ApplyFilterToFilteredTiles();

        });

    }



    private async Task<int> CountRemoteTilesAsync()
    {
        var count = 0;
        await _dispatcher.InvokeAsync(() => count = RemoteTiles.Count).ConfigureAwait(false);
        return count;
    }



    private void ApplyFilterToFilteredTiles()

    {

        FilteredTiles.Clear();

        foreach (var tile in RemoteTiles.Where(FilterPredicate))

            FilteredTiles.Add(tile);

    }



    private Task RaiseLoadCompletedAsync(CatalogLoadCompletedEventArgs args) =>

        _dispatcher.InvokeAsync(() => LoadCompleted?.Invoke(args));



    private void ScheduleDebouncedSearch()

    {

        CancellationTokenSource currentCts;

        lock (_searchCancellationSync)

        {

            if (_disposed)

                return;

            _searchDebounceCts?.Cancel();

            currentCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

            _searchDebounceCts = currentCts;

        }

        _ = DebounceSearchAsync(currentCts);

    }



    private async Task DebounceSearchAsync(CancellationTokenSource cts)

    {

        try

        {

            await Task.Delay(SearchDebounceMs, cts.Token).ConfigureAwait(false);

            await RunCatalogSearchAsync().ConfigureAwait(false);

        }

        catch (OperationCanceledException)

        {

            PosLogger.Log("Catalog search superseded by a newer query.", "DEBUG");

        }

        finally

        {

            lock (_searchCancellationSync)

            {

                if (ReferenceEquals(_searchDebounceCts, cts))

                    _searchDebounceCts = null;

            }

            cts.Dispose();

        }

    }



    private static bool MatchesSearchQuery(CatalogProductTileVm vm, string query)

    {

        var title = vm.Title.ToLowerInvariant();

        var barcode = vm.Barcode?.ToLowerInvariant() ?? string.Empty;

        var id = vm.Id.ToLowerInvariant();

        return title.Contains(query) || barcode.Contains(query) || id.Contains(query);

    }



    private static double TryParseTilePrice(string? priceLine)

    {

        if (string.IsNullOrWhiteSpace(priceLine))

            return 0;



        var digits = new string(priceLine

            .TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',')

            .ToArray())

            .Replace(',', '.');



        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)

            ? price

            : 0;

    }

}



public sealed class CatalogLoadCompletedEventArgs

{

    public bool Success { get; init; }



    public bool RestoredFromCache { get; init; }



    public int Added { get; init; }



    public int Changed { get; init; }



    public int Deleted { get; init; }



    public string? ErrorMessage { get; init; }



    public int ProductCount { get; init; }



    public bool ManualRefresh { get; init; }

}


