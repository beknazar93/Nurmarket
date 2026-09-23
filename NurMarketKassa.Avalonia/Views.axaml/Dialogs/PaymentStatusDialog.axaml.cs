using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentStatusDialog : Window
{
    private readonly DispatcherTimer _spinnerTimer;
    private double _spinnerAngle;

    public PaymentStatusDialog() : this(0) { }

    public PaymentStatusDialog(double totalAmount)
    {
        InitializeComponent();
        AmountText.Text = $"{totalAmount:0.00} сом";

        _spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _spinnerTimer.Tick += OnSpinnerTick;
        _spinnerTimer.Start();
        Closed += (_, _) => StopSpinner();
    }

    public void ShowResult(bool isSuccess, string? message)
    {
        StopSpinner();
        LoadingPanel.IsVisible = false;
        ResultCircle.IsVisible = true;
        AmountText.IsVisible = false;

        if (isSuccess)
        {
            ResultCircle.Background = ThemeBrush("BrushSuccessSoft", Brushes.DarkGreen);
            ResultIcon.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            ResultIcon.Text = "✓";
            StatusTitle.Text = "Оплата успешно";
            StatusTitle.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            StatusMessage.Text = string.IsNullOrWhiteSpace(message)
                ? "Платёж принят. Открываем новый чек."
                : message;
            CloseButton.IsVisible = false;
            return;
        }

        ResultCircle.Background = ThemeBrush("BrushDangerSoft", Brushes.DarkRed);
        ResultIcon.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        ResultIcon.Text = "×";
        StatusTitle.Text = "Оплата не прошла";
        StatusTitle.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        StatusMessage.Text = string.IsNullOrWhiteSpace(message)
            ? "Не удалось выполнить оплату. Попробуйте ещё раз."
            : message;
        CloseButton.IsVisible = true;
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        _spinnerAngle = (_spinnerAngle + 5) % 360;
        if (SpinnerPath.RenderTransform is RotateTransform rotation)
            rotation.Angle = _spinnerAngle;
    }

    private void StopSpinner()
    {
        _spinnerTimer.Stop();
        _spinnerTimer.Tick -= OnSpinnerTick;
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
