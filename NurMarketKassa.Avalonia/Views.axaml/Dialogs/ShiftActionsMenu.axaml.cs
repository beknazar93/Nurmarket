using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ShiftActionsMenu : Window
{
    public ShiftMenuAction? SelectedAction { get; set; }
    public bool? DialogResult { get; set; }

    public ShiftActionsMenu() => InitializeComponent();

    public ShiftActionsMenu(object? context) : this() { }

    private void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        ShiftMenuAction? action = button.Tag switch
        {
            ShiftMenuAction a => a,
            _ => null,
        };

        if (action is null)
            return;

        SelectedAction = action;
        DialogResult = true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }
}
