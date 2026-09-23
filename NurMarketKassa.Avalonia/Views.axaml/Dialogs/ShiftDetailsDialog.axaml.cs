using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ShiftDetailsDialog : Window
{
    public bool? DialogResult { get; set; }

    public ShiftDetailsDialog() => InitializeComponent();

    public ShiftDetailsDialog(object? model) : this()
    {
        if (model is ShiftModel shift)
            BindShift(shift);
    }

    public ShiftDetailsDialog(ShiftModel shift) : this() => BindShift(shift);

    private void BindShift(ShiftModel shift)
    {
        ShiftNumberText.Text = string.IsNullOrWhiteSpace(shift.ShiftNumber) ? "—" : shift.ShiftNumber;
        OpenedAtText.Text = shift.OpenedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        ClosedAtText.Text = shift.ClosedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        CashierText.Text = string.IsNullOrWhiteSpace(shift.Cashier) ? "—" : shift.Cashier;
        StatusText.Text = string.IsNullOrWhiteSpace(shift.Status) ? "—" : shift.Status;
        RevenueText.Text = $"{shift.Revenue:N2} сом";
        SalesCountText.Text = "—";
        CashText.Text = "—";
        CardText.Text = "—";

        if (shift.IsActive)
        {
            StatusBadge.Background = ThemeBrush("BrushSuccessSoft", Brushes.DarkGreen);
            StatusDot.Fill = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            StatusText.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
        }
        else
        {
            StatusBadge.Background = ThemeBrush("BrushDangerSoft", Brushes.DarkRed);
            StatusDot.Fill = ThemeBrush("BrushDanger", Brushes.Red);
            StatusText.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        }
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }
}
