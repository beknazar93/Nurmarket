namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>Раскладка «Табличная» (2026-09-28): чек таблицей, справа быстрые товары и цветные
/// кнопки оплаты. Вся логика — во ViewModel кассы и в <see cref="KassaLayoutBase"/>.</summary>
public partial class TableLayoutView : KassaLayoutBase
{
    public TableLayoutView()
    {
        InitializeComponent();
        RegisterProductList(QuickProductsList);
        SearchBox = ProductSearchBox;
    }
}
