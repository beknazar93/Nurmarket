using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-06 (просьба владельца «удаление товара даже если что-то связано через спец код, и как это отразится в аналитике»): единый путь удаления товара —
/// предпросмотр последствий (остаток × закупка, счётчики из 409, комплекты, размеры, коды, весы), шаг «удалить → 409 product_has_history → один вопрос про архив → архив»
/// на поддельном сервере WireMock, архив недоступен, память об удалённом товаре и аналитика, которая не падает и не теряет прошлые периоды на строках удалённого товара.
/// Запускать только tools/qa/test-safe.ps1 (база — во временной папке NURMARKET_DATA_ROOT).</summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class ProductRemovalTests : IDisposable
{
    private const string HistoryBody = """{"code":"product_has_history","detail":"Товар нельзя удалить.","data":{"receipts":2,"sales":5,"transfers":0,"supplier_returns":1,"purchase_orders":0}}""";
    private readonly string _memoryFile = Path.Combine(Path.GetTempPath(), "nm-removed-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<string> _saleIds = new();

    public ProductRemovalTests()
    {
        RemovedProductMemory.PathOverride = _memoryFile;
        LocalProductRepository.Instance.EnsureSchema();
    }

    public void Dispose()
    {
        RemovedProductMemory.PathOverride = null;
        try { File.Delete(_memoryFile); } catch { }
        try { File.Delete(_memoryFile + ".bak"); } catch { }
        if (_saleIds.Count > 0)
            SoldLineItemsStore.RemoveSales(_saleIds);
    }

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    private static CatalogProductTileVm Tile(string id, string title, double purchase, double qty = 0, string? category = null, string? brand = null) =>
        new(id, title, "100.00 сом", false) { PurchasePrice = purchase, Category = category, Brand = brand, Quantity = qty };

    // ───────────── предпросмотр ─────────────

    [Fact]
    public void Preview_WithStock_ShowsStockCost_Loss_AndAnalyticsLine()
    {
        var input = new ProductRemovalInput("Сыр", 10, "10 шт.", 40, 55, ProductLinks.None);
        var text = ProductRemoval.PreviewText(input);

        text.Should().Contain("Сыр").And.Contain("10 шт.");
        text.Should().Contain("списан", "остаток спишется актом");
        text.Should().Contain(MoneyText.Som(400.0), "10 шт. × закупка 40 = убыток 400");
        text.Should().Contain(MoneyText.Som(550.0), "«Склад по продаже» уменьшится на 10 × 55");
        text.Should().Contain("История продаж и приходов сохранится").And.Contain("«Заканчивается»").And.Contain("«Нет продаж»").And.Contain("«Склад по закупке»");
        text.Should().Contain("архив", "заранее предупреждаем, что с историей сервер не удалит");
        ProductRemoval.StockCost(10, 40).Should().Be(400);
    }

    [Fact]
    public void Preview_NoStock_HasNoWriteOffLine_AndNoMoneyForStock()
    {
        var text = ProductRemoval.PreviewText(new ProductRemovalInput("Хлеб", 0, "0 шт.", 20, 30, ProductLinks.None));
        text.Should().NotContain("будет списан");
        text.Should().Contain("История продаж и приходов сохранится");
        text.Should().NotContain("«Склад по закупке» уменьшится");
        ProductRemoval.StockCost(0, 20).Should().Be(0);
    }

    [Fact]
    public void Preview_WithoutPurchasePrice_SaysLossIsCountedBySalePrice()
    {
        var text = ProductRemoval.PreviewText(new ProductRemovalInput("Яблоки", 3, "3 кг", 0, 120, ProductLinks.None));
        text.Should().Contain("закупочная цена не указана");
        text.Should().NotContain("«Склад по закупке» уменьшится");
        text.Should().Contain(MoneyText.Som(360.0), "по продаже 3 × 120");
    }

    [Fact]
    public void Preview_ShowsBundles_Variants_Codes_AndScales()
    {
        var links = new ProductLinks(new[] { "Набор «Завтрак»", "Набор «Чай»" }, false, 4, 2, new[] { "Весы кухня" }, "PLU 12, код 15509");
        var text = ProductRemoval.PreviewText(new ProductRemovalInput("Яблоки", 0, "0 кг", 0, 120, links));

        text.Should().Contain("Входит в комплекты").And.Contain("Набор «Завтрак»").And.Contain("Набор «Чай»");
        text.Should().Contain("Размеры и цвета (4)").And.Contain("вместе с товаром");
        text.Should().Contain("Доп. штрихкоды (2)");
        text.Should().Contain("PLU 12, код 15509");
        text.Should().Contain("Весы кухня").And.Contain("обновите весы");
        links.HasAny.Should().BeTrue();
        ProductLinks.None.HasAny.Should().BeFalse();
        ProductRemoval.LinksText(ProductLinks.None).Should().BeEmpty();
    }

    [Fact]
    public void Preview_Bundle_SaysCompositionGoesWithIt()
    {
        var text = ProductRemoval.LinksText(new ProductLinks(Array.Empty<string>(), true, 0, 0, Array.Empty<string>(), null));
        text.Should().Contain("Это комплект").And.Contain("товары состава останутся");
    }

    [Fact]
    public void Preview_ManyBundles_AreTruncated()
    {
        var names = Enumerable.Range(1, 8).Select(i => "К" + i).ToList();
        var text = ProductRemoval.LinksText(new ProductLinks(names, false, 0, 0, Array.Empty<string>(), null));
        text.Should().Contain("«К5»").And.NotContain("«К6»").And.Contain("+3");
    }

    // ───────────── счётчики из 409 ─────────────

    [Fact]
    public void Counts_AreParsedFromConflictPayload()
    {
        var payload = System.Text.Json.JsonDocument.Parse(HistoryBody).RootElement.Clone();
        var counts = ProductHistoryCounts.FromPayload(payload);

        counts.Should().Be(new ProductHistoryCounts(2, 5, 0, 1, 0));
        counts.Total.Should().Be(8);
        counts.ListText().Should().Contain("приходов: 2").And.Contain("продаж: 5").And.Contain("возвратов поставщику: 1")
            .And.NotContain("перемещений").And.NotContain("заказов");
        ProductHistoryCounts.FromPayload(null).Should().Be(ProductHistoryCounts.Empty);
        ProductHistoryCounts.FromPayload(System.Text.Json.JsonDocument.Parse("""{"code":"x"}""").RootElement.Clone()).Total.Should().Be(0);
    }

    [Fact]
    public void ArchiveText_ShowsCounts_StockAndAnalytics_InOneMessage()
    {
        var text = ProductRemoval.ArchiveText("Сыр", new ProductHistoryCounts(2, 5, 0, 1, 0), stockLeft: 4, purchasePrice: 40, salePrice: 55, stockWrittenOff: false);
        text.Should().Contain("приходов: 2").And.Contain("продаж: 5").And.Contain("архив");
        text.Should().Contain("исчезнет из кассы", "остаток не списан — предупреждение");
        text.Should().Contain(MoneyText.Som(160.0));
        text.Should().Contain("История продаж и приходов сохранится");

        var done = ProductRemoval.ArchiveText("Сыр", new ProductHistoryCounts(1, 0, 0, 0, 0), 0, 40, 55, stockWrittenOff: true);
        done.Should().Contain("Остаток уже списан").And.NotContain("исчезнет из кассы").And.NotContain("«Склад по закупке» уменьшится");
    }

    [Fact]
    public void NoArchiveText_ExplainsWhatToDo()
    {
        var text = ProductRemoval.NoArchiveText("Сыр", new ProductHistoryCounts(1, 2, 0, 0, 0), stockWrittenOff: true);
        text.Should().Contain("Архива на этом сервере нет").And.Contain("переименуйте").And.Contain("Остаток уже списан").And.Contain("продаж: 2");
    }

    [Fact]
    public void IsInUseError_Recognizes409_AndProtectedText()
    {
        ProductRemoval.IsInUseError(new ApiException("x", 409)).Should().BeTrue();
        ProductRemoval.IsInUseError(new ApiException("ProtectedError: cannot delete", 400)).Should().BeTrue();
        ProductRemoval.IsInUseError(new ApiException("forbidden", 403)).Should().BeFalse();
    }

    // ───────────── единый путь «удаление → 409 → архив» (WireMock) ─────────────

    [Fact]
    public async Task Removal_Deleted_WhenServerAccepts_NoQuestions()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-del/", 204, "");
        var api = await LoginAsync(server);
        var asked = 0;

        var result = await ProductRemoval.RunAsync(api, "p-del", _ => Task.FromResult(true), _ => { asked++; return Task.FromResult(true); });

        result.Outcome.Should().Be(ProductRemovalOutcome.Deleted);
        asked.Should().Be(0, "товар без истории удаляется без вопроса про архив");
    }

    [Fact]
    public async Task Removal_History409_GoesToArchive_WithSingleConfirmation()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-hist/", 409, HistoryBody);
        server.Json("POST", "/api/main/products/p-hist/archive/", 200, """{"id":"p-hist","is_archived":true}""");
        var api = await LoginAsync(server);
        ProductHistoryCounts? shown = null;
        var asked = 0;

        var result = await ProductRemoval.RunAsync(api, "p-hist", _ => Task.FromResult(true), c => { asked++; shown = c; return Task.FromResult(true); });

        result.Outcome.Should().Be(ProductRemovalOutcome.Archived);
        asked.Should().Be(1, "ОДНО подтверждение вместо цепочки «нельзя удалить» + «архив»");
        shown.Should().Be(new ProductHistoryCounts(2, 5, 0, 1, 0), "счётчики документов из ответа 409 попали в вопрос");
        result.Counts.Total.Should().Be(8);
        server.Server.LogEntries.Count(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.EndsWith("/archive/")).Should().Be(1);
    }

    [Fact]
    public async Task Removal_History409_OwnerDeclinesArchive_NothingIsPosted()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-no/", 409, HistoryBody);
        var api = await LoginAsync(server);

        var result = await ProductRemoval.RunAsync(api, "p-no", _ => Task.FromResult(true), _ => Task.FromResult(false));

        result.Outcome.Should().Be(ProductRemovalOutcome.ArchiveDeclined);
        server.Server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/archive/")).Should().BeFalse();
    }

    [Fact]
    public async Task Removal_History409_ArchiveUnavailable_IsNotADeadEnd_NoQuestionAsked()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-old/", 409, HistoryBody);
        var api = await LoginAsync(server);
        var asked = 0;

        var result = await ProductRemoval.RunAsync(api, "p-old", _ => Task.FromResult(false), _ => { asked++; return Task.FromResult(true); });

        result.Outcome.Should().Be(ProductRemovalOutcome.ArchiveUnavailable);
        asked.Should().Be(0);
        result.Counts.Total.Should().Be(8);
        ProductRemoval.NoArchiveText("Сыр", result.Counts, false).Should().Contain("обнулите остаток").And.Contain("переименуйте");
    }

    [Fact]
    public async Task Removal_CapabilityCheckThrows_TreatedAsUnavailable()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-cap/", 409, HistoryBody);
        var api = await LoginAsync(server);

        var result = await ProductRemoval.RunAsync(api, "p-cap", _ => throw new HttpRequestException("нет связи"), _ => Task.FromResult(true));

        result.Outcome.Should().Be(ProductRemovalOutcome.ArchiveUnavailable);
    }

    [Fact]
    public async Task Removal_ArchiveFails_ReturnsErrorOutcome()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-fail/", 409, HistoryBody);
        server.Json("POST", "/api/main/products/p-fail/archive/", 500, """{"detail":"boom"}""");
        var api = await LoginAsync(server);

        var result = await ProductRemoval.RunAsync(api, "p-fail", _ => Task.FromResult(true), _ => Task.FromResult(true));

        result.Outcome.Should().Be(ProductRemovalOutcome.ArchiveFailed);
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Removal_ConflictWithoutHistoryCode_DoesNotOfferArchive()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-other/", 409, """{"detail":"protected: связан с комплектом"}""");
        var api = await LoginAsync(server);
        var asked = 0;

        var result = await ProductRemoval.RunAsync(api, "p-other", _ => Task.FromResult(true), _ => { asked++; return Task.FromResult(true); });

        result.Outcome.Should().Be(ProductRemovalOutcome.ArchiveUnavailable, "ответ не product_has_history — архив не предлагаем, показываем совет");
        asked.Should().Be(0);
    }

    [Fact]
    public async Task Removal_NotFoundOnBothPaths_IsNotAccepted()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p-404/", 404, "{}");
        server.Json("DELETE", "/api/main/products/list/p-404/", 404, "{}");
        var api = await LoginAsync(server);

        var result = await ProductRemoval.RunAsync(api, "p-404", _ => Task.FromResult(true), _ => Task.FromResult(true));

        result.Outcome.Should().Be(ProductRemovalOutcome.NotAccepted);
    }

    // ───────────── память об удалённом товаре и аналитика ─────────────

    [Fact]
    public void Memory_RemembersPurchaseCategoryBrand_AndForgets()
    {
        RemovedProductMemory.Remember(Tile("gone-1", "Сыр Гауда", 60, 3, "Сыры", "Бренд"), archived: true);

        var info = RemovedProductMemory.Find("GONE-1");
        info.Should().NotBeNull();
        info!.PurchasePrice.Should().Be(60);
        info.Category.Should().Be("Сыры");
        info.Archived.Should().BeTrue();
        RemovedProductMemory.FindByTitle("сыр гауда")!.Id.Should().Be("gone-1");
        RemovedProductMemory.ToTile(info).PurchasePrice.Should().Be(60);
        RemovedProductMemory.ToTile(info).Quantity.Should().Be(0, "в аналитике остатка у удалённого товара нет");

        RemovedProductMemory.Forget("gone-1");
        RemovedProductMemory.Contains("gone-1").Should().BeFalse("товар вернули из архива");
    }

    [Fact]
    public void Memory_CapsEntries()
    {
        for (var i = 0; i < 12; i++)
            RemovedProductMemory.Remember(Tile("m" + i, "Т" + i, 1), false);
        RemovedProductMemory.All().Count.Should().Be(12);
        RemovedProductMemory.Remember(Tile("m0", "Т0 новое", 2), false); // тот же id — не дубль
        RemovedProductMemory.All().Count.Should().Be(12);
        RemovedProductMemory.Find("m0")!.Title.Should().Be("Т0 новое");
    }

    private void AppendSale(string productId, string name, double qty, double price)
    {
        var saleId = "removal-test-" + Guid.NewGuid().ToString("N");
        _saleIds.Add(saleId);
        SoldLineItemsStore.AppendSale(new[] { (productId, name, qty, price, DateTime.UtcNow.AddMinutes(-5)) }, saleId);
    }

    [Fact]
    public void Analytics_LineOfRemovedProduct_DoesNotThrow_AndKeepsNameAndRevenue()
    {
        AppendSale("gone-nofail-" + Guid.NewGuid().ToString("N"), "Удалённый товар ТЕСТ-А", 2, 100);

        var data = AnalyticsReportData.Build(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), includeSeasonality: false);

        data.TopProducts.Should().Contain(t => t.Name == "Удалённый товар ТЕСТ-А" && t.Sum >= 200, "название берётся из строки чека, не из каталога");
        data.Abc.Should().Contain(r => r.Name == "Удалённый товар ТЕСТ-А");
    }

    [Fact]
    public void Analytics_Profit_KeepsRemovedProduct_ThroughRemovedProductMemory()
    {
        var id = "gone-profit-" + Guid.NewGuid().ToString("N");
        const string name = "Удалённый товар ТЕСТ-Б";
        AppendSale(id, name, 2, 100);

        var before = AnalyticsReportData.Build(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), includeSeasonality: false);
        before.AbcSlices.First(s => s.Key == AnalyticsReportData.KeyProfit).Rows.Should().NotContain(r => r.Name == name,
            "воспроизведение: без закупки удалённый товар выпадал из среза «по прибыли»");

        RemovedProductMemory.Remember(Tile(id, name, 60, 0, "Сыры", "Бренд"), archived: false);
        var after = AnalyticsReportData.Build(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), includeSeasonality: false);

        var row = after.AbcSlices.First(s => s.Key == AnalyticsReportData.KeyProfit).Rows.Single(r => r.Name == name);
        row.Sum.Should().BeApproximately(80, 0.001, "2 × (100 − 60): прошлый период не потерял прибыль удалённого товара");
        after.AbcSlices.First(s => s.Key == AnalyticsReportData.KeyRevenue).Rows.Single(r => r.Name == name).Sum.Should().BeApproximately(200, 0.001);
    }

    [Fact]
    public void AbcXyz_UsesLineNames_ForRemovedProducts()
    {
        var today = new DateTime(2026, 11, 6);
        var lines = Enumerable.Range(0, 6).Select(w => ("gone-abc", "Удалённый товар ТЕСТ-В", 1.0, 50.0, today.AddDays(-7 * w))).ToList();

        var result = AbcXyz.Compute(lines, today);

        result.Items.Should().ContainSingle(i => i.Key == "gone-abc" && i.Name == "Удалённый товар ТЕСТ-В");
    }
}
