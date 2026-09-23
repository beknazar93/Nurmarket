using System.Collections.ObjectModel;
using System.Globalization;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.ViewModels.Main;

public sealed class CatalogFilterViewModel : ViewModelBase
{
    public const string AllProducts = "Все товары";
    public const string WeightedProducts = "Весовые";
    public const string PieceProducts = "Штучные";
    public const string FavoriteProducts = "Избранные";
    public const string AllCategories = "Все категории";
    public const string AllBrands = "Все бренды";

    private string _searchText = "";
    private string _selectedKind = AllProducts;
    private string _selectedCategory = AllCategories;
    private string _selectedBrand = AllBrands;
    private string _priceMin = "";
    private string _priceMax = "";
    private bool _onlyInStock;

    public CatalogFilterViewModel(
        IEnumerable<CatalogProductTileVm> products,
        CatalogFilterCriteria? current = null,
        string initialSearch = "")
    {
        var list = products.ToList();
        Categories = new ObservableCollection<string>(new[] { AllCategories }.Concat(
            list.Select(x => x.Category)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)));
        Brands = new ObservableCollection<string>(new[] { AllBrands }.Concat(
            list.Select(x => x.Brand)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)));

        TotalCount = list.Count;
        WeightedCount = list.Count(x => x.MustWeigh);
        PieceCount = list.Count - WeightedCount;
        FavoriteCount = list.Count(x => x.IsFavorite);

        SearchText = !string.IsNullOrWhiteSpace(current?.SearchText) ? current.SearchText : initialSearch;
        SelectedKind = NormalizeChoice(current?.Kind, Kinds, AllProducts);
        SelectedCategory = NormalizeChoice(current?.Category, Categories, AllCategories);
        SelectedBrand = NormalizeChoice(current?.Brand, Brands, AllBrands);
        OnlyInStock = current?.OnlyInStock ?? false;
        PriceMin = FormatPrice(current?.PriceMin);
        PriceMax = FormatPrice(current?.PriceMax);
    }

    public CatalogFilterViewModel(IEnumerable<string> categories, string initialSearch = "")
        : this(Array.Empty<CatalogProductTileVm>(), null, initialSearch)
    {
        foreach (var category in categories.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!Categories.Contains(category)) Categories.Add(category);
    }

    public ObservableCollection<string> Kinds { get; } =
        [AllProducts, WeightedProducts, PieceProducts, FavoriteProducts];
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<string> Brands { get; }

    public int TotalCount { get; }
    public int WeightedCount { get; }
    public int PieceCount { get; }
    public int FavoriteCount { get; }
    public string SummaryText =>
        $"Всего {TotalCount} · Весовых {WeightedCount} · Штучных {PieceCount} · Избранных {FavoriteCount}";

    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value ?? ""); }
    public string SelectedKind { get => _selectedKind; set => SetProperty(ref _selectedKind, value ?? AllProducts); }
    public string SelectedCategory { get => _selectedCategory; set => SetProperty(ref _selectedCategory, value ?? AllCategories); }
    public string SelectedBrand { get => _selectedBrand; set => SetProperty(ref _selectedBrand, value ?? AllBrands); }
    public string PriceMin { get => _priceMin; set => SetProperty(ref _priceMin, value ?? ""); }
    public string PriceMax { get => _priceMax; set => SetProperty(ref _priceMax, value ?? ""); }
    public bool OnlyInStock { get => _onlyInStock; set => SetProperty(ref _onlyInStock, value); }

    public CatalogFilterCriteria BuildCriteria()
    {
        var minimum = ParsePrice(PriceMin);
        var maximum = ParsePrice(PriceMax);
        if (minimum.HasValue && maximum.HasValue && minimum > maximum)
            (minimum, maximum) = (maximum, minimum);

        return new CatalogFilterCriteria(
            SearchText.Trim(), SelectedKind,
            SelectedCategory == AllCategories ? null : SelectedCategory,
            SelectedBrand == AllBrands ? null : SelectedBrand,
            minimum, maximum, OnlyInStock);
    }

    public void Reset()
    {
        SearchText = "";
        SelectedKind = AllProducts;
        SelectedCategory = AllCategories;
        SelectedBrand = AllBrands;
        PriceMin = "";
        PriceMax = "";
        OnlyInStock = false;
    }

    private static string NormalizeChoice(string? value, IEnumerable<string> choices, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && choices.Contains(value, StringComparer.CurrentCultureIgnoreCase)
            ? choices.First(x => string.Equals(x, value, StringComparison.CurrentCultureIgnoreCase))
            : fallback;

    private static double? ParsePrice(string? value) =>
        double.TryParse((value ?? "").Trim().Replace(',', '.'), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : null;

    private static string FormatPrice(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
}

public sealed record CatalogFilterCriteria(
    string SearchText,
    string Kind,
    string? Category,
    string? Brand,
    double? PriceMin,
    double? PriceMax,
    bool OnlyInStock);
