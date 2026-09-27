namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>Раскладка «Профи» (2026-09-28): тёмная боковая панель разделов (те же команды, что в
/// меню ☰), крупный поиск со списком результатов, чек с блоком покупателя. Вся логика — во
/// ViewModel кассы и в <see cref="KassaLayoutBase"/>.</summary>
public partial class ProLayoutView : KassaLayoutBase
{
    public ProLayoutView()
    {
        InitializeComponent();
        RegisterProductList(ResultsList);
        SearchBox = ProductSearchBox;
    }
}
