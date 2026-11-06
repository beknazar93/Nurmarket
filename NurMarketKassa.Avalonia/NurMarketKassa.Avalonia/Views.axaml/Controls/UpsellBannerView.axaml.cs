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
        DataContextChanged += (_, _) => HookPrice();
        AttachedToVisualTree += (_, _) => { Tr.LanguageChanged -= OnLanguageChanged; Tr.LanguageChanged += OnLanguageChanged; UpdateAcceptText(); };
        DetachedFromVisualTree += (_, _) => { UnhookPrice(); Tr.LanguageChanged -= OnLanguageChanged; };
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAcceptText);

    // 2026-11-06: В6 Д4-39 — на кнопке «Добавить» показываем цену товара («Добавить · 28 сом»). Цену берём из UpsellProductText («Товар — 28 сом»),
    // а не из отдельного свойства: BasketPanelViewModel принадлежит другой волне.
    private System.ComponentModel.INotifyPropertyChanged? _priceSource;

    private void HookPrice()
    {
        UnhookPrice();
        _priceSource = DataContext as System.ComponentModel.INotifyPropertyChanged;
        if (_priceSource is not null)
            _priceSource.PropertyChanged += OnPriceSourceChanged;
        UpdateAcceptText();
    }

    private void UnhookPrice()
    {
        if (_priceSource is not null)
            _priceSource.PropertyChanged -= OnPriceSourceChanged;
        _priceSource = null;
    }

    private void OnPriceSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "UpsellProductText")
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAcceptText);
    }

    private void UpdateAcceptText()
    {
        var label = this.TryFindResource("csh.upsell.add", ActualThemeVariant, out var res) && res is string s ? s : "+";
        var text = (DataContext as NurMarketKassa.ViewModels.Main.BasketPanelViewModel)?.UpsellProductText ?? "";
        var cut = text.LastIndexOf(" — ", System.StringComparison.Ordinal);
        AcceptButton.Content = cut >= 0 && cut + 3 < text.Length ? label + " · " + text[(cut + 3)..] : label;
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
