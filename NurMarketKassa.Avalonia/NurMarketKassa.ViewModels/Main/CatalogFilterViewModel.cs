using System.Collections.ObjectModel;
using System.Globalization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

public sealed class CatalogFilterViewModel : ViewModelBase
{
    public const string AllProducts = "Все товары";
    public const string WeightedProducts = "Весовые";
    public const string PieceProducts = "Штучные";
    public const string FavoriteProducts = "Избранные";
    public const string AllCategories = "Все категории";
    public const string AllBrands = "Все бренды";

    private string _selectedKind = AllProducts;
    private string _selectedCategory = AllCategories;
    private string _selectedBrand = AllBrands;
    private string _priceMin = "";
    private string _priceMax = "";
    private bool _onlyInStock;

    public CatalogFilterViewModel(
        IEnumerable<CatalogProductTileVm> products,
        CatalogFilterCriteria? current = null)
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

        // 2026-11-21: своего поиска у фильтра нет — владелец: «Фильтр должен пользоваться поисковиком каталога»; строка поиска одна, в каталоге.
        SelectedKind = NormalizeChoice(current?.Kind, Kinds, AllProducts);
        SelectedCategory = NormalizeChoice(current?.Category, Categories, AllCategories);
        SelectedBrand = NormalizeChoice(current?.Brand, Brands, AllBrands);
        OnlyInStock = current?.OnlyInStock ?? false;
        PriceMin = FormatPrice(current?.PriceMin);
        PriceMax = FormatPrice(current?.PriceMax);
    }

    public CatalogFilterViewModel(IEnumerable<string> categories)
        : this(Array.Empty<CatalogProductTileVm>(), null)
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
        Tr.T($"Всего {TotalCount} · Весовых {WeightedCount} · Штучных {PieceCount} · Избранных {FavoriteCount}",
            $"Бардыгы: {TotalCount} · Салмактуу: {WeightedCount} · Даана: {PieceCount} · Тандалмалар: {FavoriteCount}",
            $"Total: {TotalCount} · Weighed: {WeightedCount} · Piece items: {PieceCount} · Favorites: {FavoriteCount}",
            $"Toplam: {TotalCount} · Tartılı: {WeightedCount} · Adetli: {PieceCount} · Favoriler: {FavoriteCount}",
            $"Jami: {TotalCount} · Vaznli: {WeightedCount} · Donali: {PieceCount} · Sevimlilar: {FavoriteCount}");

    // 2026-11-21: «Сбросить» в окне доступна, только когда есть что сбрасывать (CanReset) — иначе кнопка выключена и читается как выключенная.
    public string SelectedKind { get => _selectedKind; set { if (SetProperty(ref _selectedKind, value ?? AllProducts)) OnPropertyChanged(nameof(CanReset)); } }
    public string SelectedCategory { get => _selectedCategory; set { if (SetProperty(ref _selectedCategory, value ?? AllCategories)) OnPropertyChanged(nameof(CanReset)); } }
    public string SelectedBrand { get => _selectedBrand; set { if (SetProperty(ref _selectedBrand, value ?? AllBrands)) OnPropertyChanged(nameof(CanReset)); } }
    public string PriceMin { get => _priceMin; set { if (SetProperty(ref _priceMin, value ?? "")) OnPropertyChanged(nameof(CanReset)); } }
    public string PriceMax { get => _priceMax; set { if (SetProperty(ref _priceMax, value ?? "")) OnPropertyChanged(nameof(CanReset)); } }
    public bool OnlyInStock { get => _onlyInStock; set { if (SetProperty(ref _onlyInStock, value)) OnPropertyChanged(nameof(CanReset)); } }

    public bool CanReset =>
        _selectedKind != AllProducts || _selectedCategory != AllCategories || _selectedBrand != AllBrands
        || !string.IsNullOrWhiteSpace(_priceMin) || !string.IsNullOrWhiteSpace(_priceMax) || _onlyInStock;

    public CatalogFilterCriteria BuildCriteria()
    {
        var minimum = ParsePrice(PriceMin);
        var maximum = ParsePrice(PriceMax);
        if (minimum.HasValue && maximum.HasValue && minimum > maximum)
            (minimum, maximum) = (maximum, minimum);

        return new CatalogFilterCriteria(
            "", SelectedKind,
            SelectedCategory == AllCategories ? null : SelectedCategory,
            SelectedBrand == AllBrands ? null : SelectedBrand,
            minimum, maximum, OnlyInStock);
    }

    public void Reset()
    {
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
        double.TryParse((value ?? "").Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
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
