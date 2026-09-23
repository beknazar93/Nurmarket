using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class ScaleSettingsView : UserControl
{
    public event EventHandler? SaveRequested;

    public ScaleSettingsView()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);
}
