using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class PrintSettingsView : UserControl
{
    public event EventHandler? SaveRequested;

    public PrintSettingsView()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);
}
