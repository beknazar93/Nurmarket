using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class BasketPanelView : UserControl
{
    public BasketPanelView() => InitializeComponent();

    // Cashiers often click away (e.g. straight to "Оплатить") instead of pressing Enter
    // after typing a quantity — commit on blur too, not just on the Enter key binding.
    private void QuantityInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CartLineItemVm line } && line.SetQuantityCommand?.CanExecute(line) == true)
            line.SetQuantityCommand.Execute(line);
    }
}
