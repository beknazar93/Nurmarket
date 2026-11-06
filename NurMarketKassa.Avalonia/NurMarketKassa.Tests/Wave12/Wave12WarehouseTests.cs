using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Wave12;

/// <summary>2026-11-06: волна В12 «Склад, товар, закупки» плана «десктоп 10 из 10»: Н-11 (округление возврата поставщику), С7 (выбор строк «Списать просроченное»),
/// ЖП Н-3 (счётчики «Пересчёта дня»), ЖП Н-5 («Нет продаж» при продажах), ЖП Н-6 (прибыль строки с расходами), Б-5/Б-8 (скидка, наценка, страна, вес и «Минимальный остаток»
/// в базе кассы), Б-7 (штрихкод и код в ревизии), Л5-06 хвост (событие «ревизия без PIN»). Настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1) и поддельный сервер WireMock.
/// Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class Wave12WarehouseTests : IDisposable
{
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public Wave12WarehouseTests() => _repo.EnsureSchema();

    public void Dispose() => _repo.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());

    // ───────────── Н-11: округление возврата поставщику ─────────────

    [Fact]
    public void ReturnMath_HalfKopeck_RoundsAwayFromZero_LikeReceipt()
    {
        // ДО: Math.Round(0,125 × 1, 2) = 0,12 («банковское»), а приход (сервер) записал 0,13 → долг 0,01.
        Math.Round(0.125 * 1, 2).Should().Be(0.12, "прежнее поведение окна возврата");
        SupplierReturnMath.LineTotal(1, 0.125).Should().Be(0.13);
        SupplierReturnMath.LineTotal(3, 0.125).Should().Be(0.38, "0,375 → 0,38");
        SupplierReturnMath.LineTotal(2, 0.12).Should().Be(0.24);
        SupplierReturnMath.LineTotal(-1, 5).Should().Be(0, "отрицательное количество не даёт суммы");
        SupplierReturnMath.Total(new[] { (1.0, 0.125), (2.0, 0.12) }).Should().Be(0.37);
    }

    [Theory]
    [InlineData(1, 0.125, true)]
    [InlineData(3, 0.125, true)]
    [InlineData(2, 0.125, false)]   // 0,25 — ровно копейки
    [InlineData(1, 0.12, false)]
    [InlineData(1, 0.1234, false)]
    public void ReturnMath_DetectsHalfKopeck(double qty, double price, bool expected) =>
        SupplierReturnMath.IsHalfKopeck(qty, price).Should().Be(expected);

    // ───────────── С7: выбор строк «Списать просроченное» ─────────────

    private static ProductExpiryIndex.Mark Mark(string id, string name, DateTime date, double qty) =>
        new(id, name, date, (int)(date.Date - DateTime.Today).TotalDays, date.Date < DateTime.Today, qty);

    [Fact]
    public void ExpiredWriteOff_Plan_SkipsRowsWithFreshReceipt_AndSumsOnlySelected()
    {
        var today = new DateTime(2026, 11, 6);
        var marks = new[]
        {
            Mark("a", "Молоко", new DateTime(2026, 11, 1), 10),
            Mark("b", "Йогурт", new DateTime(2026, 11, 1), 4),       // после даты годности был приход → свежая партия
            Mark("c", "Сыр", new DateTime(2026, 12, 1), 3),          // не просрочен
            Mark("d", "Кефир", new DateTime(2026, 10, 1), 0),        // остатка нет
        };
        var receipts = new Dictionary<string, DateTime> { ["b"] = new DateTime(2026, 11, 3) };
        var items = ExpiredWriteOffPlan.Build(marks, today,
            catalogQty: id => id == "a" ? 7 : null,                  // в каталоге меньше, чем в справочнике срока
            purchasePrice: id => id == "a" ? 50 : 20,
            lastReceipt: id => receipts.TryGetValue(id, out var at) ? at : null);

        items.Select(i => i.ProductId).Should().Equal("a", "b");
        items[0].Qty.Should().Be(7, "не списываем больше, чем есть в каталоге");
        items[0].MayHaveFreshStock.Should().BeFalse();
        items[1].MayHaveFreshStock.Should().BeTrue();

        var selected = ExpiredWriteOffPlan.DefaultSelection(items);
        selected.Should().BeEquivalentTo(new[] { "a" }, "строка со свежим приходом по умолчанию не отмечена");
        ExpiredWriteOffPlan.Cost(ExpiredWriteOffPlan.Selected(items, selected)).Should().Be(350);

        selected.Add("b");
        ExpiredWriteOffPlan.Selected(items, selected).Should().HaveCount(2);
        ExpiredWriteOffPlan.Cost(ExpiredWriteOffPlan.Selected(items, selected)).Should().Be(430);
        ExpiredWriteOffPlan.Selected(items, new HashSet<string>()).Should().BeEmpty("ничего не отмечено — ничего не списывается");
    }

    // ───────────── ЖП Н-3: счётчики «Пересчёта дня» ─────────────

    [Fact]
    public void Revision_SubsetCounts_AreOnlyForChosenItems()
    {
        var tiles = Enumerable.Range(1, 40).Select(i => new CatalogProductTileVm("p" + i, "Товар " + i, "10,00 сом", false, null) { Quantity = 5, Barcode = "46000" + i.ToString("000") }).ToList();
        var s = RevisionSession.Start(tiles, new RevisionOptions { PersistDraft = false });
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p1", "p2", "p3" };
        s.SetFact(s.Rows.First(r => r.ProductId == "p1"), 5);    // без расхождения
        s.SetFact(s.Rows.First(r => r.ProductId == "p2"), 3);    // с расхождением
        s.SetFact(s.Rows.First(r => r.ProductId == "p10"), 1);   // посчитан, но не из выбранных

        var all = s.GetTotals();
        all.Total.Should().Be(40, "ДО: фильтры показывали все позиции ревизии");
        var subset = s.GetSubsetCounts(ids);
        subset.Total.Should().Be(3);
        subset.Counted.Should().Be(2);
        subset.NotCounted.Should().Be(1);
        subset.WithDifference.Should().Be(1);
    }

    // ───────────── Б-7: штрихкод и код в ревизии ─────────────

    [Fact]
    public void Revision_Row_UsesBarcodeDisplayAndCodeDisplay()
    {
        var tile = new CatalogProductTileVm("w1", "Яблоки", "100,00 сом", false, null) { Quantity = 2, Barcode = "", AlternateBarcodesRaw = "4600000000011", Plu = 12 };
        var s = RevisionSession.Start(new[] { tile }, new RevisionOptions { PersistDraft = false });
        var row = s.Rows.Single();
        row.Barcode.Should().Be("4600000000011", "основной штрихкод пуст — берётся первый дополнительный, как в Складе");
        row.Article.Should().Be("12", "нет артикула и кода — PLU");
    }

    // ───────────── ЖП Н-6: прибыль строки с доп. расходами ─────────────

    [Fact]
    public void ReceivingLine_Profit_UsesLandedCost_WhenExtraCostsExist()
    {
        var line = new ReceivingLineVm { ProductName = "Товар", Unit = "шт", PurchasePrice = 0.125, SalePrice = 1.00 };
        line.ProfitText.Should().Contain("0,88").And.Contain("прибыль");   // без расходов — от закупки (1,00 − 0,125)
        line.LandedCostText.Should().BeEmpty();

        line.LandedExtraPerUnit = 2.0;                                      // доля расходов 2,00 на штуку → себестоимость 2,125
        line.CostPrice.Should().Be(2.125);
        line.LandedCostText.Should().Contain("2,125");
        line.ProfitText.Should().StartWith("убыток").And.Contain("1,13", "как в итоге под списком: 1,00 − 2,125");
    }

    // ───────────── ЖП Н-5: «Нет продаж» при продажах ─────────────

    [Fact]
    public void Planner_Status_NotNoSales_WhenSoldInLast30Days()
    {
        PlannerStatusRules.HasNoSales(0, 11).Should().BeFalse("ДО: perDay периода 7 дней = 0 давало «Нет продаж» при продаже 11 за 30 дней");
        PlannerStatusRules.HasNoSales(0, 0).Should().BeTrue();
        PlannerStatusRules.EffectivePerDay(0, 30).Should().Be(1);
        PlannerStatusRules.EffectivePerDay(2, 30).Should().Be(2);

        var now = DateTime.Now;
        var product = new PlannerProduct("p1", "Клей карандаш", "", "", "шт", 3, 10, false);
        var sales = new[] { new PlannerSale("p1", 11, now.AddDays(-12).ToUniversalTime()) };   // продано 12 дней назад — вне периода 7 дней
        var rows = PurchasePlanner.Build(new[] { product }, sales, new Dictionary<string, (DateTime At, double Qty)>(), new PlannerSettings(PeriodDays: 7, CoverDays: 14, SafetyDays: 3), now, null);
        var row = rows.Single();
        row.Sales7.Should().Be(0);
        row.Sales30.Should().Be(11);
        row.Status.Should().NotBe(PlannerStatus.NoSales);
        row.Status.Should().Be(PlannerStatus.Soon, "остаток 3 при 11 продажах за 30 дней хватит ~8 дней — меньше запаса 14");
    }

    [Fact]
    public async Task Planner_ServerRow_WithZeroPeriodButSales30_IsNotNoSales()
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.Json("GET", "/api/main/suppliers/s1/recommendations/", 200, """
            {"results":[
              {"product_id":"p1","product_name":"Клей карандаш 8гр","current_stock":"5","is_weight":false,
               "sales":{"last_7_days":0,"last_30_days":11,"average_per_day":0},"stock_forecast":{}},
              {"product_id":"p2","product_name":"Старьё","current_stock":"5","is_weight":false,
               "sales":{"last_7_days":0,"last_30_days":0,"average_per_day":0},"stock_forecast":{}}
            ],"next":null}
            """);
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");

        var rows = await new SupplierApiService(api).GetRecommendationsAsync("s1", new PlannerSettings(PeriodDays: 7, CoverDays: 14, SafetyDays: 3));
        rows.Should().NotBeNull();
        rows!.Single(r => r.Product.Id == "p1").Status.Should().NotBe(PlannerStatus.NoSales);
        rows.Single(r => r.Product.Id == "p2").Status.Should().Be(PlannerStatus.NoSales, "ни за период, ни за 30 дней продаж нет");
    }

    // ───────────── Б-5, Б-8: поля карточки в базе ─────────────

    [Fact]
    public void ProductColumns_AddedToOldTable_WithoutResettingCatalogVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Products (id TEXT PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL DEFAULT 0);"
                + "CREATE TABLE catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                + "INSERT INTO catalog_meta VALUES ('catalog_version', '77');"
                + "INSERT INTO Products (id, name, price) VALUES ('x', 'Старый товар', 5);";
            create.ExecuteNonQuery();
        }

        // из-за столбцов wholesale_price и IsWeight версия сбрасывается — это было и раньше; проверяем, что НОВЫЕ столбцы сами её не сбрасывают
        LocalProductRepository.EnsureProductColumns(connection);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Products);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }
        columns.Should().Contain(new[] { "discount_percent", "markup_percent", "country", "weight_kg", "min_quantity" });

        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = "INSERT OR REPLACE INTO catalog_meta VALUES ('catalog_version', '78');";
            reset.ExecuteNonQuery();
        }
        LocalProductRepository.EnsureProductColumns(connection).Should().Be(0, "второй вызов ничего не добавляет");
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT value FROM catalog_meta WHERE key = 'catalog_version';";
        check.ExecuteScalar().Should().Be("78", "повторная проверка столбцов версию каталога не сбрасывает");
    }

    [Fact]
    public void Tile_DiscountMarkupCountryWeightAndMinQuantity_SurviveDatabaseRoundTrip()
    {
        var request = new ProductEditRequest
        {
            Name = "Товар со скидкой", Unit = "шт", Quantity = 20, Price = 100, IsNew = true,
            DiscountPercent = 15, MarkupPercent = 40, Country = "Китай", WeightKg = 1.25, MinimumQuantity = 50,
        };
        var id = LocalProductEditor.SaveLocally(request, existingId: null);

        // «перезапуск кассы»: плитки читаются из базы заново
        var saved = _repo.LoadAllTiles().Should().ContainSingle(t => t.Id == id).Which;
        saved.DiscountPercent.Should().Be(15, "ДО: плашка скидки и скидка на ценнике A4 пропадали после перезапуска");
        saved.HasDiscount.Should().BeTrue();
        saved.MarkupPercent.Should().Be(40);
        saved.Country.Should().Be("Китай");
        saved.WeightKg.Should().Be(1.25);
        saved.MinQuantity.Should().Be(50);
    }

    [Fact]
    public void Tile_LowStock_UsesMinimumQuantity_WhenSet()
    {
        CatalogProductTileVm.IsLow(20, 50).Should().BeTrue("минимум 50, остаток 20 → заканчивается (ДО: порог 10 для всех)");
        CatalogProductTileVm.IsLow(20, null).Should().BeFalse();
        CatalogProductTileVm.IsLow(9, null).Should().BeTrue();
        CatalogProductTileVm.IsLow(9, 2).Should().BeFalse("минимум 2, остаток 9 → не заканчивается (ДО: помечался при 9)");
        CatalogProductTileVm.IsLow(2, 2).Should().BeTrue();
        CatalogProductTileVm.IsLow(5, 0).Should().BeTrue("0 = минимум не задан → общий порог 10");

        var tile = new CatalogProductTileVm("m1", "Товар", "10,00 сом", false, null) { Quantity = 20 };
        tile.IsLowStock.Should().BeFalse();
        tile.MinQuantity = 50;
        tile.IsLowStock.Should().BeTrue();
        tile.StockForeground.Should().Be("#DC2626");
        tile.MinQuantity = null;
        tile.IsLowStock.Should().BeFalse();
    }

    // ───────────── Л5-06 хвост: событие «ревизия без PIN» ─────────────

    [Fact]
    public void FraudKind_RevisionNoPin_HasTitleWeightAndDescription()
    {
        FraudKinds.All.Should().Contain(FraudKinds.RevisionNoPin);
        FraudKinds.Weight(FraudKinds.RevisionNoPin).Should().BeGreaterThan(0);
        FraudText.Title(FraudKinds.RevisionNoPin).Should().NotBeNullOrWhiteSpace();
        FraudText.Describe(new FraudEvent { Kind = FraudKinds.RevisionNoPin, Cashier = "Айдай", Amount = 5000 }).Should().Contain("Айдай").And.Contain("5");
    }
}
