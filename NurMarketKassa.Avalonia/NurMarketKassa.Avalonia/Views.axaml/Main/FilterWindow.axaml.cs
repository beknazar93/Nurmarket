using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using NurMarketKassa.Services;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class FilterWindow : Window
{
    public FilterWindow() : this(new CatalogFilterViewModel(
        Array.Empty<NurMarketKassa.Models.Pos.CatalogProductTileVm>()))
    {
    }

    public FilterWindow(CatalogFilterViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        // 2026-11-12 (Д-5): значения списков («Все товары», «Весовые», «Все категории»…) — внутренние русские ключи фильтра (сравниваются в CatalogPanelViewModel),
        // поэтому не меняем сами значения, а показываем их на языке программы через шаблон элемента (в ky/uz были русские слова).
        foreach (var combo in new[] { CatalogKindCombo, CategoryCombo, BrandCombo })
            combo.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock { Text = ChoiceText(item), VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        CatalogKindCombo.ItemsSource = ViewModel.Kinds;
        CategoryCombo.ItemsSource = ViewModel.Categories;
        BrandCombo.ItemsSource = ViewModel.Brands;
        CatalogKindCombo.SelectedItem = ViewModel.SelectedKind;
        CategoryCombo.SelectedItem = ViewModel.SelectedCategory;
        BrandCombo.SelectedItem = ViewModel.SelectedBrand;
        SearchBox.Text = ViewModel.SearchText;
        PriceMinBox.Text = ViewModel.PriceMin;
        PriceMaxBox.Text = ViewModel.PriceMax;
    }

    public CatalogFilterViewModel ViewModel { get; }

    /// <summary>2026-11-12 (Д-5): подпись внутреннего ключа фильтра на 5 языках; название категории или бренда из каталога — как есть.</summary>
    internal static string ChoiceText(string? key) => key switch
    {
        CatalogFilterViewModel.AllProducts => Tr.T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha tovarlar"),
        CatalogFilterViewModel.WeightedProducts => Tr.T("Весовые", "Салмактуу", "Weighed", "Tartılı", "Vaznli"),
        CatalogFilterViewModel.PieceProducts => Tr.T("Штучные", "Даана", "Piece items", "Adetli", "Donali"),
        CatalogFilterViewModel.FavoriteProducts => Tr.T("Избранные", "Тандалмалар", "Favorites", "Favoriler", "Sevimlilar"),
        CatalogFilterViewModel.AllCategories => Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"),
        CatalogFilterViewModel.AllBrands => Tr.T("Все бренды", "Бардык бренддер", "All brands", "Tüm markalar", "Barcha brendlar"),
        _ => key ?? "",
    };

    // 2026-10-22 (модалки, пакет 1): своя полоса заголовка с перетаскиванием и «—» (свернуть модалку) убраны —
    // окно в общей оболочке ModalShell, как остальные модалки кассы.

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.Reset();
        SearchBox.Text = ViewModel.SearchText;
        CatalogKindCombo.SelectedItem = ViewModel.SelectedKind;
        CategoryCombo.SelectedItem = ViewModel.SelectedCategory;
        BrandCombo.SelectedItem = ViewModel.SelectedBrand;
        PriceMinBox.Text = ViewModel.PriceMin;
        PriceMaxBox.Text = ViewModel.PriceMax;
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text ?? "";
        ViewModel.SelectedKind = CatalogKindCombo.SelectedItem as string ?? CatalogFilterViewModel.AllProducts;
        ViewModel.SelectedCategory = CategoryCombo.SelectedItem as string ?? CatalogFilterViewModel.AllCategories;
        ViewModel.SelectedBrand = BrandCombo.SelectedItem as string ?? CatalogFilterViewModel.AllBrands;
        ViewModel.PriceMin = PriceMinBox.Text ?? "";
        ViewModel.PriceMax = PriceMaxBox.Text ?? "";
        Close(true);
    }
}
