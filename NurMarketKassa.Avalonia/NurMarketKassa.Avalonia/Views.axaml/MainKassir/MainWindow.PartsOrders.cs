using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-11-20 (сфера «Автозапчасти», часть Б): вход окна «Заказы под клиента» в кассу (меню ☰) и «Оформить продажу» — позиции заказа в чек.
/// Деньги — только обычными путями кассы (AddWeighedProductWithKnownWeightAsync: открытая смена, просрочка, правило продажи без остатка); строка «под заказ»
/// без товара каталога идёт как «Доп. услуга» (AddCustomItem). Предоплата в чек не вносится: чек на полную сумму, предоплату кассир кладёт из конверта (см. PartsOrder).</summary>
public partial class MainWindow
{
    internal void NavigatePartsOrders()
    {
        if (!AutoPartsRules.Active)
            return;
        PartsOrdersWindow.Open(this, new PartsOrderDeps { SellAsync = PartsOrderSellAsync });
    }

    /// <summary>Сколько позиций заказа попало в чек. Цена товара каталога может отличаться от цены в заказе — кассиру говорим об этом в сообщении чека.</summary>
    internal async Task<int> PartsOrderSellAsync(PartsOrder order)
    {
        var added = 0;
        var differ = new List<string>();
        foreach (var line in order.Lines)
        {
            if (line.Qty <= 0m)
                continue;
            var product = string.IsNullOrWhiteSpace(line.ProductId) ? null : CatalogCacheService.FindProductById(line.ProductId);
            if (product is not null)
            {
                if (await EstimateAddProductAsync(product.Id, line.Qty).ConfigureAwait(true))
                {
                    added++;
                    if (Math.Abs((decimal)product.PriceValue - line.Price) > 0.005m)
                        differ.Add(line.Title);
                }
            }
            else if (_viewModel.Basket.AddCustomItem(line.Title, (double)line.Price, (double)line.Qty, false))
            {
                added++;
            }
        }
        if (differ.Count > 0)
            _viewModel.Basket.CartMessage = Tr.T($"Цена в заказе №{order.Number} отличается от каталога: {string.Join(", ", differ)} — проверьте.",
                $"№{order.Number} заказдагы баа каталогдон айырмаланат: {string.Join(", ", differ)} — текшериңиз.",
                $"The price in order No. {order.Number} differs from the catalog: {string.Join(", ", differ)} — please check.",
                $"{order.Number} numaralı siparişteki fiyat katalogdan farklı: {string.Join(", ", differ)} — kontrol edin.",
                $"№{order.Number} buyurtmadagi narx katalogdan farq qiladi: {string.Join(", ", differ)} — tekshiring.");
        return added;
    }
}
