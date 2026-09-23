using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.ViewModels.Settings;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.SaveCustomizationCommand.CanExecute(null))
            vm.SaveCustomizationCommand.Execute(null);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        (this.VisualRoot as Window)?.Close();
    }
}
