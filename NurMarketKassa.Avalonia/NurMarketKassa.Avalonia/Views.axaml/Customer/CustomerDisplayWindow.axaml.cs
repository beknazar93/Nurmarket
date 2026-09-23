using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Customer;

public partial class CustomerDisplayWindow : Window
{
    private readonly CustomerDisplayViewModel _viewModel;
    private string? _lastNavigatedAdMediaPath;

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
        UpdateAdMedia();
    }

    /// <summary>
    /// Avalonia.Image не умеет проигрывать GIF/видео (только первый кадр), поэтому для таких
    /// файлов рекламный слот рендерится через WebView2 — грузим маленькую локальную HTML-обёртку
    /// с &lt;img&gt; (GIF анимируется браузером сам) или &lt;video autoplay loop muted&gt;.
    /// Перезагружаем страницу только когда путь реально поменялся, иначе анимация будет
    /// перезапускаться на каждое обновление чека.
    /// </summary>
    private void UpdateAdMedia()
    {
        if (!_viewModel.IsAnimatedInformationMedia)
        {
            _lastNavigatedAdMediaPath = null;
            return;
        }

        var path = _viewModel.InformationImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || path == _lastNavigatedAdMediaPath)
            return;

        _lastNavigatedAdMediaPath = path;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var fileUri = new Uri(path).AbsoluteUri;
        var media = extension == ".gif"
            ? $"<img src=\"{fileUri}\" style=\"max-width:100%;max-height:100%;\"/>"
            : $"<video src=\"{fileUri}\" autoplay loop muted playsinline style=\"max-width:100%;max-height:100%;\"></video>";
        var html =
            "<html><body style=\"margin:0;background:#fff;display:flex;align-items:center;" +
            $"justify-content:center;overflow:hidden;\">{media}</body></html>";

        var htmlPath = Path.Combine(Path.GetTempPath(), "nurmarket_ad_media.html");
        File.WriteAllText(htmlPath, html);
        AdMediaHost.Navigate(new Uri(htmlPath).AbsoluteUri);
    }
}
