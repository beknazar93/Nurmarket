using System;
using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Customer.Layouts;

namespace NurMarketKassa.AvaloniaHost.Views.Customer;

/// <summary>Содержимое экрана покупателя (2026-09-28, «при смене вида кассы меняй и 2 экран»).
///
/// Одно на окно второго монитора и на предпросмотр в редакторе экрана покупателя. Держит фон
/// (картинка + затемнение), вид экрана — один из шести, по CustomerDisplayViewModel.EffectiveStyle
/// («как у кассы» или выбранный в редакторе) — и ресурсы, из которых виды берут цвета
/// (CustomerDisplay*Brush) и размеры шрифта (CdFont*). Вид пересоздаётся только когда он сменился
/// (App.ApplyMainLayoutMode → AvaloniaCustomerDisplayService.RefreshStyle), цвета обновляются на
/// каждое обновление модели — поэтому открытый экран перестраивается без перезапуска.</summary>
public partial class CustomerDisplayView : UserControl
{
    private static readonly double[] FontSizes = [11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 26, 28, 32, 36, 40, 48];

    private CustomerDisplayViewModel? _vm;
    private string? _style;

    public CustomerDisplayView()
    {
        InitializeComponent();
    }

    /// <summary>Вид, который сейчас построен (standard / table / …).</summary>
    public string? CurrentStyle => _style;

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
        // Модель окна живёт всё время работы кассы — отписываемся, чтобы закрытое окно не держалось в памяти.
        Attach(null);
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

        if (_vm is not null)
            Apply();
    }

    private void OnPresentationChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        if (_vm is null)
            return;

        foreach (var (key, brush) in _vm.PaletteResources)
            SetResource(key, brush);

        var scale = _vm.Settings.Scale;
        foreach (var size in FontSizes)
            SetResource($"CdFont{size:0}", Math.Round(size * scale, 1));
        SetResource("CustomerDisplayCornerRadius", _vm.DisplayCornerRadius);
        SetResource("CustomerDisplayBodyFontSize", _vm.BodyFontSize);
        SetResource("CustomerDisplayShowBarcode", _vm.Settings.ShowBarcode);
        SetResource("CustomerDisplayShowProductImage", _vm.Settings.ShowProductImage);

        EnsureLayout(_vm.EffectiveStyle);
    }

    /// <summary>Ресурс меняется только если значение другое: каждое изменение ресурса проходит по
    /// всему дереву экрана, а модель обновляется на каждый пробитый товар.</summary>
    private void SetResource(string key, object value)
    {
        if (Resources.TryGetValue(key, out var old) && Equals(old, value))
            return;
        Resources[key] = value;
    }

    private void EnsureLayout(string style)
    {
        if (style == _style && LayoutHost.Content is not null)
            return;

        _style = style;
        LayoutHost.Content = style switch
        {
            KassaLayouts.Table => new TableCustomerLayout(),
            KassaLayouts.Cards => new CardsCustomerLayout(),
            KassaLayouts.Minimal => new MinimalCustomerLayout(),
            KassaLayouts.Pro => new ProCustomerLayout(),
            KassaLayouts.OneC => new OneCCustomerLayout(),
            _ => new ClassicCustomerLayout(),
        };
    }
}
