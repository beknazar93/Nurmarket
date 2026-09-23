using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ProductDetailDialog : Window
{
    public ProductDetailDialog()
    {
        InitializeComponent();
    }


    public ProductDetailDialog(object? product) { InitializeComponent(); }

}
