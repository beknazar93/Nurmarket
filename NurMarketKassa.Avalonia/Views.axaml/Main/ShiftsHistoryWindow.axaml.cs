using Avalonia.Controls;
using Avalonia.Input;
using NurMarketKassa.AvaloniaHost.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class ShiftsHistoryWindow : Window
{
    public ShiftsHistoryWindow()
    {
        InitializeComponent();
        DataContext = new ShiftHistoryViewModel(this);
        WindowState = WindowState.Maximized;
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
