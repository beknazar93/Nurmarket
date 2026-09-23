using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Customer;

public partial class CustomerDisplayWindow : Window
{
    private readonly CustomerDisplayViewModel _viewModel;

    public CustomerDisplayWindow() : this(App.GetRequiredService<CustomerDisplayViewModel>()) { }

    public CustomerDisplayWindow(CustomerDisplayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PresentationChanged += OnPresentationChanged;
        Closed += (_, _) => _viewModel.PresentationChanged -= OnPresentationChanged;
        ApplyVisualResources();
    }

    public void ApplySettings(CustomerDisplaySettings settings)
    {
        _viewModel.ApplySettings(settings);
        RequestedThemeVariant = settings.Theme switch
        {
            CustomerDisplayTheme.Dark => ThemeVariant.Dark,
            CustomerDisplayTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        ApplyVisualResources();
    }

    public void SetPreviewMode(bool enabled)
    {
        _viewModel.SetPreviewMode(enabled);
        ShowInTaskbar = enabled;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape ||
            (e.Key == Key.F12 &&
             e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
             e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            if (_viewModel.CloseCustomerDisplayCommand.CanExecute(null))
                _viewModel.CloseCustomerDisplayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPresentationChanged(object? sender, EventArgs e) => ApplyVisualResources();

    private void ApplyVisualResources()
    {
        Resources["CustomerDisplayTextBrush"] = _viewModel.TextBrush;
        Resources["CustomerDisplaySecondaryTextBrush"] = _viewModel.SecondaryTextBrush;
        Resources["CustomerDisplaySurfaceAltBrush"] = _viewModel.SurfaceAltBrush;
        Resources["CustomerDisplayAccentBrush"] = _viewModel.AccentBrush;
        Resources["CustomerDisplayAccentSoftBrush"] = _viewModel.AccentSoftBrush;
        Resources["CustomerDisplayBorderBrush"] = _viewModel.BorderBrushValue;
        Resources["CustomerDisplayBodyFontSize"] = _viewModel.BodyFontSize;
        Resources["CustomerDisplayCornerRadius"] = _viewModel.DisplayCornerRadius;
        Resources["CustomerDisplayShowBarcode"] = _viewModel.Settings.ShowBarcode;
        Resources["CustomerDisplayShowProductImage"] = _viewModel.Settings.ShowProductImage;
    }
}
