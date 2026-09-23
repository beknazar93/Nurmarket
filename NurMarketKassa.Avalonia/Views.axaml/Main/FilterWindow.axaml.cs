using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Input;
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

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

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
