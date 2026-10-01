using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-01, «Умная допродажа»: подсказка «С этим часто берут» над итогом чека.
/// Вся логика — в BasketPanelViewModel (UpsellService); здесь только разметка.</summary>
public partial class UpsellBannerView : UserControl
{
    public UpsellBannerView() => InitializeComponent();
}
