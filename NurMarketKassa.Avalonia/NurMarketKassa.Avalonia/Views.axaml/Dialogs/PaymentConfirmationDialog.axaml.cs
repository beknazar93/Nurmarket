using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentConfirmationDialog : Window
{
    public PaymentConfirmationDialog() => InitializeComponent();

    public static new bool Show(Window? owner) =>
        PosDialogHost.Show(new PaymentConfirmationDialog(), owner) == true;

    /// <summary>2026-10-04, Android-касса: из async-кода (в Windows — прежний синхронный Show).</summary>
    public static async Task<bool> ShowModalAsync(Window? owner) =>
        await PosDialogHost.ShowModalAsync(new PaymentConfirmationDialog(), owner).ConfigureAwait(true) == true;

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
