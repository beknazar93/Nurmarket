using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-01, «Умная допродажа»: подсказка «С этим часто берут» над итогом чека.
/// Вся логика — в BasketPanelViewModel (UpsellService); здесь только разметка.</summary>
public partial class UpsellBannerView : UserControl
{
    public UpsellBannerView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyNarrow(e.NewSize.Width < NarrowWidth);
    }

    // 2026-10-20, живая проверка: в «1С» подсказка стоит в узкой правой колонке, и кнопки «Добавить»/«Пропустить»
    // сжимали текст до «С …». Уже 420 точек кнопки уходят на вторую строку справа, текст — на всю ширину.
    private const double NarrowWidth = 420;
    private bool? _narrow;

    private void ApplyNarrow(bool narrow)
    {
        if (_narrow == narrow)
            return;
        _narrow = narrow;
        Grid.SetRow(AcceptButton, narrow ? 1 : 0);
        Grid.SetRow(SkipButton, narrow ? 1 : 0);
        Grid.SetColumn(AcceptButton, narrow ? 1 : 2);
        AcceptButton.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        AcceptButton.Margin = narrow ? new Avalonia.Thickness(0, 6, 0, 0) : new Avalonia.Thickness(6, 0, 0, 0);
        SkipButton.Margin = narrow ? new Avalonia.Thickness(6, 6, 0, 0) : new Avalonia.Thickness(6, 0, 0, 0);
        Grid.SetColumnSpan(BannerTextPanel, narrow ? 3 : 1);
    }
}
