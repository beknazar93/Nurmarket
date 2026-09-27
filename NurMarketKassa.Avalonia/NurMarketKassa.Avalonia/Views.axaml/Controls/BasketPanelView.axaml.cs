using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
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

    /// <summary>«Списание» в меню «Ещё» (2026-09-27). Логика — в <see cref="BasketExtraActions"/>
    /// (2026-09-28): то же меню есть у других раскладок кассы.</summary>
    private async void WriteOff_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.WriteOffAsync(this, DataContext as BasketPanelViewModel).ConfigureAwait(true);

    /// <summary>«Печать последнего чека» в меню «Ещё» (2026-09-27), см. <see cref="BasketExtraActions"/>.</summary>
    private async void PrintLastReceipt_Click(object? sender, RoutedEventArgs e) =>
        await BasketExtraActions.PrintLastReceiptAsync(sender as Button, DataContext as BasketPanelViewModel).ConfigureAwait(true);
}
