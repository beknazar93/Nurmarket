using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-27 («Аналитика товаров» в программе владельца): один расчёт <see cref="StockAnalytics"/> — пустой склад, минусы, пороги «заканчивается», категории и доли,
/// роль без права видеть закупку, фильтры, «без штрихкода/цены/категории», топ-20, и сверка с формулами итогов «Склад → Товары» (WarehouseWindow.RefreshWarehouseTotals).</summary>
[Collection("UserPreferences")]
public sealed class StockAnalyticsTests : IDisposable
{
    private readonly AppLanguage _lang = UserPreferences.Instance.Language;

    public StockAnalyticsTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _lang;

    private static CatalogProductTileVm P(string id, string title, double price, double qty, double buy = 0, string? category = "Бакалея", string? barcode = "4600000000001",
        double? min = null, string unit = "шт")
    {
        var t = new CatalogProductTileVm(id, title, price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сом", false)
        {
            Category = category, Barcode = barcode, PurchasePrice = buy, Unit = unit,
        };
        t.MinQuantity = min;
        t.Quantity = qty;
        t.IsLowStock = CatalogProductTileVm.IsLow(qty, min); // как делает загрузка каталога (ProductUnitNormalizer / StockSyncService)
        return t;
    }

    [Fact]
    public void EmptyCatalog_AllZero_NoCrash()
    {
        var r = StockAnalytics.Build([], showPurchase: true);
        r.Positions.Should().Be(0);
        r.Units.Should().Be(0);
        r.SaleValue.Should().Be(0);
        r.PurchaseValue.Should().Be(0);
        r.Markup.Should().Be(0);
        r.MarkupPercent.Should().BeNull("делить на нулевую закупку нельзя");
        (r.OutOfStock + r.Low + r.Negative + r.NoPrice + r.NoBarcode + r.NoCategory).Should().Be(0);
        r.Categories.Should().BeEmpty();
        r.LowItems.Should().BeEmpty();
        r.OutItems.Should().BeEmpty();
        r.TopByValue.Should().BeEmpty();
    }

    [Fact]
    public void Totals_Values_Markup()
    {
        var r = StockAnalytics.Build(
        [
            P("a", "Рис", 100, 10, buy: 60),
            P("b", "Сахар", 50, 4, buy: 30),
        ], showPurchase: true);

        r.Positions.Should().Be(2);
        r.Units.Should().Be(14);
        r.SaleValue.Should().Be(1200);
        r.PurchaseValue.Should().Be(720);
        r.Markup.Should().Be(480);
        r.MarkupPercent.Should().BeApproximately(66.6667, 0.001);
    }

    [Fact]
    public void LowStock_Thresholds_MinOfCard_Or_Ten_ZeroAndNegativeAreOutNotLow()
    {
        var r = StockAnalytics.Build(
        [
            P("ten", "Ровно десять", 10, 10),                // порог 10: «меньше 10» — не заканчивается
            P("nine", "Девять", 10, 9.9),                    // заканчивается
            P("min50", "С минимумом 50", 10, 20, min: 50),   // минимум карточки 50 — заканчивается
            P("min50ok", "Выше минимума", 10, 60, min: 50),  // не заканчивается
            P("atMin", "Ровно минимум", 10, 50, min: 50),    // «не выше минимума» — заканчивается
            P("zero", "Ноль", 10, 0),                        // нет в наличии, не «заканчивается»
            P("minus", "Минус", 10, -3),                     // нет в наличии и в минусе
        ], showPurchase: true);

        r.Low.Should().Be(3);
        r.LowItems.Select(x => x.Id).Should().BeEquivalentTo(["nine", "min50", "atMin"]);
        r.OutOfStock.Should().Be(2);
        r.OutItems.Select(x => x.Id).Should().BeEquivalentTo(["zero", "minus"]);
        r.Negative.Should().Be(1);
        r.LowItems.First(x => x.Id == "min50").Threshold.Should().Be(50);
        r.LowItems.First(x => x.Id == "nine").Threshold.Should().Be(10);
    }

    [Fact]
    public void Categories_Trimmed_CaseInsensitive_SharesSum100()
    {
        var r = StockAnalytics.Build(
        [
            P("1", "Рис", 100, 10, category: "Бакалея"),
            P("2", "Гречка", 100, 5, category: " бакалея "),
            P("3", "Кола", 50, 10, category: "Напитки"),
            P("4", "Пакет", 10, 1, category: null),
            P("5", "Зажигалка", 10, 1, category: ""),
        ], showPurchase: false);

        r.Categories.Should().HaveCount(3);
        r.Categories[0].Name.Should().Be("Бакалея");
        r.Categories[0].Positions.Should().Be(2);
        r.Categories[0].Units.Should().Be(15);
        r.Categories[0].SaleValue.Should().Be(1500);
        r.Categories[1].Name.Should().Be("Напитки");
        r.Categories.Single(c => c.IsNone).Name.Should().Be("Без категории");
        r.Categories.Single(c => c.IsNone).Positions.Should().Be(2);
        r.NoCategory.Should().Be(2);
        r.Categories.Sum(c => c.Share).Should().BeApproximately(100, 0.0001);
        r.Categories[0].Products.Select(p => p.Id).Should().Equal("1", "2");
    }

    [Fact]
    public void NoPurchaseRight_PurchaseNeverLeaks()
    {
        var r = StockAnalytics.Build([P("a", "Рис", 100, 10, buy: 60)], showPurchase: false);

        r.ShowPurchase.Should().BeFalse();
        r.PurchaseValue.Should().BeNull();
        r.Markup.Should().BeNull();
        r.MarkupPercent.Should().BeNull();
        r.Categories.Should().OnlyContain(c => c.PurchaseValue == null);
        r.TopByValue.Should().OnlyContain(x => x.PurchasePrice == null && x.PurchaseValue == null);
        r.SaleValue.Should().Be(1000, "стоимость по продаже видна всем");
    }

    [Fact]
    public void NoPrice_NoBarcode_ServiceAndPluAreNotCountedWithoutBarcode()
    {
        var service = P("srv", "Доставка", 100, 0, barcode: null);
        service.Kind = "service";
        var weighed = P("w", "Яблоки", 100, 5, barcode: null, unit: "кг");
        weighed.Plu = 12;
        var r = StockAnalytics.Build(
        [
            P("a", "Без цены", 0, 5),
            P("b", "Без кода", 10, 5, barcode: "  "),
            service,
            weighed,
        ], showPurchase: true);

        r.NoPrice.Should().Be(1);
        r.NoBarcode.Should().Be(1, "услуга и товар с PLU весов штрихкода не требуют");
    }

    [Fact]
    public void Filters_ByCategory_AndSearch_AffectEverything()
    {
        var products = new[]
        {
            P("1", "Рис девзира", 100, 10, category: "Бакалея"),
            P("2", "Гречка", 80, 3, category: "Бакалея"),
            P("3", "Кола", 50, 10, category: "Напитки"),
            P("4", "Пакет", 10, 1, category: null),
        };

        var byCat = StockAnalytics.Build(products, true, category: "бакалея");
        byCat.Positions.Should().Be(2);
        byCat.Categories.Should().ContainSingle();

        var none = StockAnalytics.Build(products, true, category: StockAnalytics.NoCategoryKey);
        none.Positions.Should().Be(1);
        none.Categories.Single().IsNone.Should().BeTrue();

        var search = StockAnalytics.Build(products, true, search: "кола");
        search.Positions.Should().Be(1);
        search.TopByValue.Single().Title.Should().Be("Кола");

        StockAnalytics.Build(products, true, category: "Бакалея", search: "кола").Positions.Should().Be(0);
    }

    [Fact]
    public void TopByValue_Is20Max_SortedByValueDescending()
    {
        var products = Enumerable.Range(1, 30).Select(i => P("p" + i, "Товар " + i, i, 2)).ToList();
        products.Add(P("zero", "Нулевой", 500, 0));      // нулевая стоимость в топ не попадает

        var r = StockAnalytics.Build(products, true);
        r.TopByValue.Should().HaveCount(StockAnalytics.TopCount);
        r.TopByValue[0].Id.Should().Be("p30");
        r.TopByValue.Select(x => x.SaleValue).Should().BeInDescendingOrder();
        r.TopByValue.Should().NotContain(x => x.Id == "zero");
    }

    [Fact]
    public void SameFormulas_AsWarehouseTotals()
    {
        // Формулы «Склад → Товары» (WarehouseWindow.RefreshWarehouseTotals), повторённые дословно на том же наборе.
        var products = new List<CatalogProductTileVm>
        {
            P("1", "A", 120.5, 7, buy: 80.25, category: "X"),
            P("2", "B", 33, 0, buy: 20, category: "Y"),
            P("3", "C", 10, -2, buy: 5, category: null),
            P("4", "D", 999.99, 3, buy: 700, category: "X", min: 5),
            P("5", "E", 15, 12.5, buy: 9, category: "Z", unit: "кг"),
        };

        var units = products.Sum(p => p.Quantity);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * LocalCartService.ParsePrice(p.PriceLine));
        var low = products.Count(p => p.IsLowStock && p.Quantity > 0);
        var outOfStock = products.Count(p => p.Quantity <= 0);

        var r = StockAnalytics.Build(products, true);
        r.Positions.Should().Be(products.Count);
        r.Units.Should().Be(units);
        r.PurchaseValue.Should().Be(purchaseValue);
        r.SaleValue.Should().Be(saleValue);
        r.Low.Should().Be(low);
        r.OutOfStock.Should().Be(outOfStock);
        r.Categories.Sum(c => c.Units).Should().BeApproximately(units, 1e-9);
        r.Categories.Sum(c => c.SaleValue).Should().BeApproximately(saleValue, 1e-9);
        r.Categories.Sum(c => c.Low).Should().Be(low);
        r.Categories.Sum(c => c.OutOfStock).Should().Be(outOfStock);
    }

    [Fact]
    public void CategoryOptions_SortedByName_HasNoneFlag()
    {
        var (names, hasNone) = StockAnalytics.CategoryOptions(
        [
            P("1", "A", 1, 1, category: "Яблоки"), P("2", "B", 1, 1, category: "Бакалея"), P("3", "C", 1, 1, category: " Бакалея"), P("4", "D", 1, 1, category: null),
        ]);
        names.Should().Equal("Бакалея", "Яблоки");
        hasNone.Should().BeTrue();
    }

    [Fact]
    public void Export_WritesWorkbook_WithAndWithoutPurchase()
    {
        var products = new[]
        {
            P("1", "Рис", 100, 3, buy: 60, category: "Бакалея"),
            P("2", "Кола", 50, 0, buy: 30, category: null),
        };
        foreach (var show in new[] { true, false })
        {
            var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "stock-analytics-" + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                StockAnalyticsExport.ExportToExcel(path, StockAnalytics.Build(products, show), "Магазин", "Категория: Бакалея");
                new FileInfo(path).Length.Should().BeGreaterThan(2000);
            }
            finally
            {
                try { File.Delete(path); } catch { /* временный файл */ }
            }
        }

        // пустой склад — файл тоже собирается (диаграммы без данных не строятся)
        var empty = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "stock-analytics-empty-" + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
        try
        {
            StockAnalyticsExport.ExportToExcel(empty, StockAnalytics.Build([], true), null);
            File.Exists(empty).Should().BeTrue();
        }
        finally
        {
            try { File.Delete(empty); } catch { /* временный файл */ }
        }
    }
}
