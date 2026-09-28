using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Controls;
using NurMarketKassa.AvaloniaHost.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Customer.Parts;

/// <summary>Блок «QR оплаты / реклама» экрана покупателя (2026-09-28). Размер картинки и рамка
/// задаются видом экрана; анимированная реклама (GIF, видео) — через WebView2, который создаётся
/// только когда он действительно нужен (в предпросмотре редактора его нет вовсе).</summary>
public partial class CustomerInfoCard : UserControl
{
    public static readonly StyledProperty<double> ImageSizeProperty =
        AvaloniaProperty.Register<CustomerInfoCard, double>(nameof(ImageSize), 210);

    /// <summary>Без собственной рамки-карточки (вид сам рисует подложку).</summary>
    public static readonly StyledProperty<bool> FlatProperty =
        AvaloniaProperty.Register<CustomerInfoCard, bool>(nameof(Flat));

    private CustomerDisplayViewModel? _vm;
    private WebView2Host? _web;
    private string? _lastNavigatedAdMediaPath;
    private double _appliedSize = -1;

    public CustomerInfoCard()
    {
        InitializeComponent();
        ApplySize();
    }

    public double ImageSize
    {
        get => GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    public bool Flat
    {
        get => GetValue(FlatProperty);
        set => SetValue(FlatProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImageSizeProperty || change.Property == FlatProperty)
            ApplySize();
    }

    private void ApplySize()
    {
        ApplyImageSize(Math.Max(80, ImageSize));
        if (Flat)
        {
            CardBorder.Classes.Remove("cd-card");
            CardBorder.Background = Brushes.Transparent;
            CardBorder.BorderThickness = default;
            CardBorder.Padding = default;
        }
        InvalidateMeasure();
    }

    private void ApplyImageSize(double size)
    {
        if (Math.Abs(size - _appliedSize) < 0.5)
            return;
        _appliedSize = size;
        StaticImage.Width = size;
        StaticImage.Height = size;
        AnimatedHost.Width = size;
        AnimatedHost.Height = size;
        Placeholder.Height = Math.Max(90, size - 20);
    }

    /// <summary>Если блок не помещается по высоте (задний экран 1024×768, чек с оплатой и сдачей),
    /// QR уменьшается, а не обрезается снизу: обрезанный QR телефон не прочтёт. Уменьшается не
    /// меньше чем до 110 px; размер считается от доступной высоты, поэтому повторный замер даёт
    /// тот же результат и цикла раскладки нет.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);
        var full = Math.Max(80, ImageSize);
        var target = full;
        if (!double.IsInfinity(availableSize.Height))
        {
            var overhead = desired.Height - _appliedSize;
            target = Math.Clamp(availableSize.Height - overhead, Math.Min(110, full), full);
        }

        if (Math.Abs(target - _appliedSize) >= 0.5)
        {
            ApplyImageSize(target);
            desired = base.MeasureOverride(availableSize);
        }
        return desired;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as CustomerDisplayViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as CustomerDisplayViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
        _lastNavigatedAdMediaPath = null;
    }

    private void Attach(CustomerDisplayViewModel? vm)
    {
        if (!ReferenceEquals(vm, _vm))
        {
            if (_vm is not null)
                _vm.PresentationChanged -= OnPresentationChanged;
            _vm = vm;
            if (_vm is not null)
                _vm.PresentationChanged += OnPresentationChanged;
        }
        UpdateAdMedia();
    }

    private void OnPresentationChanged(object? sender, EventArgs e) => UpdateAdMedia();

    /// <summary>
    /// Avalonia.Image не умеет проигрывать GIF/видео (только первый кадр), поэтому для таких
    /// файлов рекламный слот рендерится через WebView2 — грузим маленькую локальную HTML-обёртку
    /// с &lt;img&gt; (GIF анимируется браузером сам) или &lt;video autoplay loop muted&gt;.
    /// Перезагружаем страницу только когда путь реально поменялся, иначе анимация будет
    /// перезапускаться на каждое обновление чека. (Перенесено из CustomerDisplayWindow, 2026-09-28.)
    /// </summary>
    private void UpdateAdMedia()
    {
        if (_vm is null || !_vm.IsAnimatedInformationMedia || VisualRoot is null)
        {
            _lastNavigatedAdMediaPath = null;
            return;
        }

        var path = _vm.InformationImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || path == _lastNavigatedAdMediaPath)
            return;

        _lastNavigatedAdMediaPath = path;
        try
        {
            if (_web is null)
            {
                _web = new WebView2Host();
                AnimatedHost.Child = _web;
            }

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
            _web.Navigate(new Uri(htmlPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Реклама на экране покупателя не показана: {ex.Message}", "CUSTOMER_DISPLAY");
        }
    }
}
