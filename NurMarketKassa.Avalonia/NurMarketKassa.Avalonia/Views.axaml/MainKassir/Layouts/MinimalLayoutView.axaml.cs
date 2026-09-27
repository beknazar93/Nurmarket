namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>Раскладка «Минимал» (2026-09-28): крупные цветные плитки, «Текущая продажа» справа и
/// огромная кнопка «Оплатить» с суммой. Вся логика — во ViewModel кассы и в <see cref="KassaLayoutBase"/>.</summary>
public partial class MinimalLayoutView : KassaLayoutBase
{
    public MinimalLayoutView()
    {
        InitializeComponent();
        RegisterProductList(TilesList);
        SearchBox = ProductSearchBox;
    }
}
