using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>Аналитика склада: показатели, самые дорогие остатки и остатки по категориям.
///
/// 2026-09-27 вынесена из окна склада (раньше — WarehouseWindow.RefreshAnalytics и разметка его
/// вкладки «Аналитика») в отдельный элемент без изменений в расчёте: в программе владельца вся
/// аналитика собрана в разделе «Аналитика», и там она стоит вкладкой «Склад», а в кассе осталась
/// вкладкой склада, как была. Одна копия кода на оба места — правка расчёта не разойдётся.
///
/// Функция платная (UserPreferences.WarehouseAnalyticsUnlocked) — проверяет это тот, кто её
/// показывает.</summary>
public partial class WarehouseStockAnalyticsView : UserControl
{
    public WarehouseStockAnalyticsView()
    {
        InitializeComponent();
    }

    private sealed class KpiCardVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private sealed class AnalyticsTopValueRow
    {
        public string Title { get; init; } = "";
        public string StockText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private sealed class AnalyticsCategoryRow
    {
        public string CategoryName { get; init; } = "";
        public string SkuCountText { get; init; } = "";
        public string StockText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    private static double ParsePriceValue(string? priceLine) =>
        LocalCartService.ParsePrice(priceLine ?? "");

    /// <summary>Считается локально из уже загруженного каталога (CatalogCacheService.Products) —
    /// без отдельных запросов к серверу, поэтому мгновенно и без дополнительной нагрузки.</summary>
    public void Refresh()
    {
        var products = CatalogCacheService.Products.ToList();

        var skuCount = products.Count;
        var outOfStock = products.Count(p => p.Quantity <= 0);
        var lowStock = products.Count(p => p.IsLowStock && p.Quantity > 0);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine));

        AnalyticsKpiPanel.ItemsSource = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Всего товаров (SKU)", "Бардык товарлар (SKU)", "Total products (SKU)", "Toplam ürün (SKU)", "Jami mahsulotlar (SKU)"), Value = skuCount.ToString() },
            new() { Label = Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), Value = outOfStock.ToString() },
            new() { Label = Tr.T("Низкий остаток", "Аз калды", "Low stock", "Düşük stok", "Kam qoldiq"), Value = lowStock.ToString() },
            new() { Label = Tr.T("Остаток по закупке", "Сатып алуу баасы боюнча калдык", "Stock at purchase price", "Stok değeri (alış)", "Qoldiq (xarid narxida)"), Value = $"{purchaseValue:N0} {Som}" },
            new() { Label = Tr.T("Остаток по продаже", "Сатуу баасы боюнча калдык", "Stock at sale price", "Stok değeri (satış)", "Qoldiq (sotuv narxida)"), Value = $"{saleValue:N0} {Som}" },
        };

        var topValue = products
            .Select(p => new { Product = p, Value = p.Quantity * ParsePriceValue(p.PriceLine) })
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Take(10)
            .ToList();

        AnalyticsTopValueGrid.ItemsSource = topValue
            .Select(x => new AnalyticsTopValueRow
            {
                Title = x.Product.Title,
                StockText = x.Product.Quantity.ToString("0.###"),
                PriceText = $"{ParsePriceValue(x.Product.PriceLine):N2} {Som}",
                ValueText = $"{x.Value:N2} {Som}",
            })
            .ToList();
        BarChartRenderer.Render(AnalyticsTopValueChart, topValue
            .Select(x => (x.Product.Title, (double)x.Value, $"{x.Value:N0} {Som}"))
            .ToList());

        var byCategory = products
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz") : p.Category!.Trim())
            .Select(g => new
            {
                Name = g.Key,
                SkuCount = g.Count(),
                Stock = g.Sum(p => p.Quantity),
                Value = g.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine)),
            })
            .OrderByDescending(g => g.Value)
            .ToList();

        AnalyticsCategoryGrid.ItemsSource = byCategory
            .Select(g => new AnalyticsCategoryRow
            {
                CategoryName = g.Name,
                SkuCountText = g.SkuCount.ToString(),
                StockText = g.Stock.ToString("0.###"),
                ValueText = $"{g.Value:N2} {Som}",
            })
            .ToList();
        var categoryChartData = byCategory
            .Take(10)
            .Select(g => (g.Name, g.Value, $"{g.Value:N0} {Som}"))
            .ToList();
        BarChartRenderer.Render(AnalyticsCategoryChart, categoryChartData);
        BarChartRenderer.RenderPie(AnalyticsCategoryPie, categoryChartData);
    }
}
