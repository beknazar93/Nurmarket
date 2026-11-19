using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-11-19 («Смета», ТЗ 5): вход окна «Смета» в кассу и «Добавить всё в чек». Деньги — только обычными путями кассы: товар кладётся в чек тем же способом, что «Сколько нужно?»
/// (AddWeighedProductWithKnownWeightAsync: открытая смена, просрочка, правило продажи без остатка), своя позиция — строкой «Доп. услуга» (как оплата работ в «Ремонте»).</summary>
public partial class MainWindow
{
    internal void NavigateEstimate()
    {
        if (!EstimateFeature.IsAvailable)
            return;
        EstimatesWindow.Open(this);
    }

    /// <summary>Товар в чек по цене каталога. true — количество в чеке выросло (кассир не отказался от продажи без остатка, смена открыта).</summary>
    internal async Task<bool> EstimateAddProductAsync(string productId, decimal quantity)
    {
        var product = CatalogCacheService.FindProductById(productId);
        if (product is null || quantity <= 0m)
            return false;
        double InCart() => _viewModel.Basket.Lines.Where(l => l.ProductId == productId).Sum(l => l.Quantity);
        var before = InCart();
        await AddWeighedProductWithKnownWeightAsync(product, (double)quantity).ConfigureAwait(true);
        return InCart() > before + 1e-9;
    }

    internal bool EstimateAddCustomLine(string name, decimal amount) =>
        amount > 0m && _viewModel.Basket.AddCustomItem(name, (double)amount, 1, false);
}
