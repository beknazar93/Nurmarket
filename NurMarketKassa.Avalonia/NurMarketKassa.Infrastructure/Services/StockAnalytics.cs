using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (владелец: «Аналитика товаров в десктоп тоже добавь! Общее количество товаров, категории, общее количество, заканчивается — подробнее»):
/// ОДИН расчёт аналитики склада/товаров по каталогу (<see cref="CatalogCacheService.Products"/>) — для вкладки «Склад» раздела «Аналитика» программы владельца и
/// для выгрузки в Excel (<see cref="StockAnalyticsExport"/>). Формулы те же, что у итогов «Склад → Товары» (WarehouseWindow.RefreshWarehouseTotals): позиции — все товары каталога,
/// единицы — сумма остатков, стоимость по закупке и по продаже — остаток × цена, «заканчивается» — <see cref="CatalogProductTileVm.IsLowStock"/> при остатке больше нуля
/// (минимум карточки или общий порог 10), «нет в наличии» — остаток ноль и меньше. Деньги не округляются (показ — MoneyText), закупка без права на неё в отчёт не попадает вовсе.</summary>
public static class StockAnalytics
{
    /// <summary>Сколько товаров в списке «Самые дорогие остатки».</summary>
    public const int TopCount = 20;

    /// <summary>Ключ категории «Без категории» в фильтре (пустая строка); null в фильтре — все категории.</summary>
    public const string NoCategoryKey = "";

    public static string NoCategoryLabel => Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz");

    /// <summary>Название категории товара без пробелов по краям; пусто — «Без категории».</summary>
    public static string CategoryKeyOf(CatalogProductTileVm p) => (p.Category ?? "").Trim();

    /// <summary>Категории каталога для выбора (по алфавиту, без «Без категории» — её добавляет окно, если такие товары есть) и признак, что такие товары есть.</summary>
    public static (IReadOnlyList<string> Names, bool HasNone) CategoryOptions(IEnumerable<CatalogProductTileVm> products)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var none = false;
        foreach (var p in products)
        {
            var key = CategoryKeyOf(p);
            if (key.Length == 0)
                none = true;
            else
                names.Add(key);
        }

        return (names.ToList(), none);
    }

    /// <param name="category">null — все; <see cref="NoCategoryKey"/> — только без категории; иначе название категории.</param>
    /// <param name="search">Единый поиск (<see cref="SearchMatch"/>) по названию, штрихкоду, артикулу, категории, бренду.</param>
    /// <param name="showPurchase">Есть ли у роли право видеть закупку (<c>WarehouseWindow.RevisionShowPurchase</c>). Нет — закупочные суммы и наценка пустые (null).</param>
    public static StockAnalyticsReport Build(IEnumerable<CatalogProductTileVm> products, bool showPurchase, string? category = null, string? search = null)
    {
        // Копия массивом: каталог обновляется в UI-потоке, а расчёт может идти в фоне.
        var source = products.ToArray().AsEnumerable();
        if (category is not null)
            source = source.Where(p => string.Equals(CategoryKeyOf(p), category, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
            source = source.Where(p => SearchMatch.Matches(search, p.SearchKey));

        var rows = new List<StockProductRow>();
        int noPrice = 0, noBarcode = 0, noCategory = 0, negative = 0;
        foreach (var p in source)
        {
            var salePrice = LocalCartService.ParsePrice(p.PriceLine ?? "");
            var min = p.MinQuantity is > 0 ? p.MinQuantity.Value : 0;
            var row = new StockProductRow(
                p.Id, p.Title, CategoryKeyOf(p), p.Unit ?? "", p.Quantity, min,
                min > 0 ? min : CatalogProductTileVm.LowStockQuantityThreshold,
                salePrice, showPurchase ? p.PurchasePrice : null,
                p.Quantity * salePrice, showPurchase ? p.Quantity * p.PurchasePrice : null,
                p.IsLowStock && p.Quantity > 0);
            rows.Add(row);

            if (salePrice <= 0)
                noPrice++;
            // Штрихкод: услуга его не имеет, у весового товара вместо него PLU весов — они в «без штрихкода» не считаются.
            if (string.IsNullOrWhiteSpace(p.Barcode) && !p.IsService && p.Plu is not > 0)
                noBarcode++;
            if (row.Category.Length == 0)
                noCategory++;
            if (p.IsNegativeStock)
                negative++;
        }

        var saleValue = rows.Sum(r => r.SaleValue);
        double? purchaseValue = showPurchase ? rows.Sum(r => r.PurchaseValue ?? 0) : null;
        var positiveTotal = rows.Where(r => r.SaleValue > 0).Sum(r => r.SaleValue);

        var categories = rows
            .GroupBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new StockCategoryRow(
                g.Key.Length == 0 ? NoCategoryLabel : g.Key,
                g.Key.Length == 0,
                g.Count(),
                g.Sum(r => r.Quantity),
                g.Sum(r => r.SaleValue),
                showPurchase ? g.Sum(r => r.PurchaseValue ?? 0) : null,
                g.Count(r => r.Low),
                g.Count(r => r.IsOut),
                positiveTotal > 0 ? Math.Max(0, g.Sum(r => r.SaleValue)) / positiveTotal * 100 : 0,
                g.OrderByDescending(r => r.SaleValue).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .OrderByDescending(c => c.SaleValue)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new StockAnalyticsReport
        {
            ShowPurchase = showPurchase,
            Positions = rows.Count,
            Units = rows.Sum(r => r.Quantity),
            SaleValue = saleValue,
            PurchaseValue = purchaseValue,
            Markup = purchaseValue is { } buy ? saleValue - buy : null,
            MarkupPercent = purchaseValue is > 0 ? (saleValue - purchaseValue.Value) / purchaseValue.Value * 100 : null,
            OutOfStock = rows.Count(r => r.IsOut),
            Low = rows.Count(r => r.Low),
            Negative = negative,
            NoPrice = noPrice,
            NoBarcode = noBarcode,
            NoCategory = noCategory,
            Categories = categories,
            LowItems = rows.Where(r => r.Low).OrderBy(r => r.Quantity / r.Threshold).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            OutItems = rows.Where(r => r.IsOut).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            TopByValue = rows.Where(r => r.SaleValue > 0).OrderByDescending(r => r.SaleValue).Take(TopCount).ToList(),
        };
    }
}

/// <summary>Один товар в аналитике склада. <c>PurchasePrice</c> и <c>PurchaseValue</c> — null, если у роли нет права видеть закупку.</summary>
/// <param name="MinQuantity">«Минимальный остаток» карточки; 0 — не задан.</param>
/// <param name="Threshold">Порог «заканчивается»: минимум карточки, иначе общий (10).</param>
public sealed record StockProductRow(
    string Id, string Title, string Category, string Unit, double Quantity, double MinQuantity, double Threshold,
    double SalePrice, double? PurchasePrice, double SaleValue, double? PurchaseValue, bool Low)
{
    /// <summary>Нет в наличии: остаток ноль и меньше (минус тоже).</summary>
    public bool IsOut => Quantity <= 0;
}

/// <summary>Категория в аналитике склада: сколько товаров, единиц, денег, сколько заканчивается и нет в наличии.</summary>
public sealed record StockCategoryRow(
    string Name, bool IsNone, int Positions, double Units, double SaleValue, double? PurchaseValue, int Low, int OutOfStock, double Share,
    IReadOnlyList<StockProductRow> Products);

/// <summary>Итоги аналитики склада (<see cref="StockAnalytics.Build"/>).</summary>
public sealed class StockAnalyticsReport
{
    public bool ShowPurchase { get; init; }
    public int Positions { get; init; }
    public double Units { get; init; }
    public double SaleValue { get; init; }
    public double? PurchaseValue { get; init; }
    public double? Markup { get; init; }
    public double? MarkupPercent { get; init; }
    public int OutOfStock { get; init; }
    public int Low { get; init; }
    public int Negative { get; init; }
    public int NoPrice { get; init; }
    public int NoBarcode { get; init; }
    public int NoCategory { get; init; }
    public IReadOnlyList<StockCategoryRow> Categories { get; init; } = [];
    public IReadOnlyList<StockProductRow> LowItems { get; init; } = [];
    public IReadOnlyList<StockProductRow> OutItems { get; init; } = [];
    public IReadOnlyList<StockProductRow> TopByValue { get; init; } = [];
}
