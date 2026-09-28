using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Customer;

/// <summary>Окно экрана покупателя на втором мониторе. Само содержимое (вид экрана, цвета,
/// QR / реклама) — в CustomerDisplayView (2026-09-28: вынесено, чтобы тот же экран показывать
/// в предпросмотре редактора и менять вид вместе с видом кассы).</summary>
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
        ApplyThemeVariant();
    }

    public void ApplySettings(CustomerDisplaySettings settings)
    {
        _viewModel.ApplySettings(settings);
        ApplyThemeVariant();
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

    private void OnPresentationChanged(object? sender, EventArgs e) => ApplyThemeVariant();

    /// <summary>Светлый/тёмный вариант окна (полосы прокрутки и т.п.) — как у самого экрана:
    /// вид «Профи» тёмный всегда, остальные — по настройке «Тема» экрана покупателя.</summary>
    private void ApplyThemeVariant()
    {
        var variant = _viewModel.IsDarkDisplay ? ThemeVariant.Dark : ThemeVariant.Light;
        if (RequestedThemeVariant != variant)
            RequestedThemeVariant = variant;
    }
}
