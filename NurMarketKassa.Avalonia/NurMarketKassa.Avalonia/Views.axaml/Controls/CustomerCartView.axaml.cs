using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>
/// Reusable customer receipt/cart area of the cashier workspace.
/// Its <see cref="DataContext"/> is a <c>BasketPanelViewModel</c> supplied by the host.
/// </summary>
public partial class CustomerCartView : UserControl
{
    public CustomerCartView() => InitializeComponent();
}
