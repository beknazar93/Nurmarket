using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class CatalogPanelView : UserControl
{
    public CatalogPanelView() => InitializeComponent();

    public void FocusProductSearch()
    {
        ProductSearchBox.Focus();
        ProductSearchBox.SelectAll();
    }

    private void OpenFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CatalogPanelViewModel catalog)
            return;

        var filter = catalog.CreateFilterViewModel();
        var dialog = new FilterWindow(filter);
        if (PosDialogHost.Show(dialog, TopLevel.GetTopLevel(this) as Window) == true)
            catalog.ApplyAdvancedFilter(filter.BuildCriteria());
    }

    private void CardView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
            catalog.SetViewMode(CatalogViewMode.Cards);
    }

    private void TableView_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CatalogPanelViewModel catalog)
            catalog.SetViewMode(CatalogViewMode.Table);
    }
}
