using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 (ответ Умара 21.11, всё в бою): штрихкоды товара — quantity у alternate_barcodes (целое &gt; 0 или null, name ≤255, barcode ≤64); POST/DELETE products/&lt;id&gt;/barcodes/ по одному коду
/// с If-Match; 409 version_conflict (перечитать и повторить один раз); откат на PATCH при 404/405; 409 barcode_duplicate (data: product_id, name, …); скан упаковки при приходе
/// (доп. код — N штук, штрихкод упаковки sale_units — factor). Сервер — WireMock (без настоящей сети).</summary>
[Collection("InvoiceBarcodeBinder")]
public sealed class ServerBarcodesTests : IDisposable
{
    private readonly string _saleUnitsFile = Path.Combine(Path.GetTempPath(), "nm-srvbc-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public ServerBarcodesTests()
    {
        Reset();
        ProductSaleUnits.FileOverride = _saleUnitsFile;
        ProductSaleUnits.ResetForTests();
    }

    public void Dispose()
    {
        Reset();
        ProductSaleUnits.FileOverride = null;
        ProductSaleUnits.ResetForTests();
        try { File.Delete(_saleUnitsFile); } catch { /* временный файл */ }
    }

    private static void Reset()
    {
        InvoiceBarcodeBinder.OwnerOverride = null;
        InvoiceBarcodeBinder.ReadCardOverride = null;
        InvoiceBarcodeBinder.SaveOverride = null;
        InvoiceBarcodeBinder.AddOverride = null;
        InvoiceBarcodeBinder.RemoveOverride = null;
    }

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    private static IEnumerable<WireMock.Logging.ILogEntry> Calls(FakeNurCrmServer server, string method, string pathPart) =>
        server.Server.LogEntries.Where(e => string.Equals(e.RequestMessage.Method, method, StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.Contains(pathPart, StringComparison.Ordinal));

    private static string HeaderOf(WireMock.Logging.ILogEntry e, string name) =>
        e.RequestMessage.Headers is { } h && h.TryGetValue(name, out var v) ? string.Join(",", v) : "";

    private const string DuplicateBody =
        "{\"code\":\"barcode_duplicate\",\"detail\":\"Штрихкод уже используется\",\"data\":{\"product_id\":\"p2\",\"name\":\"Чужой товар\",\"code\":\"15\",\"source\":\"alternate\",\"barcode\":\"4600000000011\"}}";

    // ------------------------------------------------------------------ а) тело alternate_barcodes

    [Fact]
    public void ServerQuantity_IsWholeNumberAboveOneOrNull_NeverZero()
    {
        InvoiceBarcodeRules.ServerQuantity(0).Should().BeNull();
        InvoiceBarcodeRules.ServerQuantity(1).Should().BeNull("одна штука — не упаковка");
        InvoiceBarcodeRules.ServerQuantity(0.4).Should().BeNull();
        InvoiceBarcodeRules.ServerQuantity(-5).Should().BeNull();
        InvoiceBarcodeRules.ServerQuantity(double.NaN).Should().BeNull();
        InvoiceBarcodeRules.ServerQuantity(12).Should().Be(12);
        InvoiceBarcodeRules.ServerQuantity(2.6).Should().Be(3, "дробное сервер не принимает — целое");
    }

    [Fact]
    public void ToServerList_TrimsNameAndBarcode_AndKeepsOnlyRealQuantities()
    {
        var list = InvoiceBarcodeRules.ToServerList(
        [
            new AlternateBarcodeVariant { Barcode = " 4600000000011 ", Name = null, Quantity = 0 },
            new AlternateBarcodeVariant { Barcode = "4600000000028", Name = new string('я', 300), Quantity = 24 },
            new AlternateBarcodeVariant { Barcode = new string('1', 80), Quantity = 1 },
            new AlternateBarcodeVariant { Barcode = "  ", Quantity = 5 },
        ]).Cast<Dictionary<string, object?>>().ToList();
        list.Should().HaveCount(3, "пустой код отброшен");
        list[0]["barcode"].Should().Be("4600000000011");
        list[0]["name"].Should().Be("");
        list[0]["quantity"].Should().BeNull("0 сервер отклоняет — null");
        ((string)list[1]["name"]!).Length.Should().Be(255);
        list[1]["quantity"].Should().Be(24L);
        ((string)list[2]["barcode"]!).Length.Should().Be(64);
        list[2]["quantity"].Should().BeNull();
    }

    [Fact]
    public void ProductBody_AlternateBarcodes_NeverSendZeroQuantity()
    {
        var request = new ProductEditRequest
        {
            Name = "Вода", Unit = "шт", AlternateBarcodes = "4600000000011\n4600000000028\n4600000000035",
            KnownAlternateBarcodeVariants =
            [
                new AlternateBarcodeVariant { Barcode = "4600000000011", Name = "", Quantity = 0 },
                new AlternateBarcodeVariant { Barcode = "4600000000028", Name = "Короб", Quantity = 12 },
            ],
        };
        var body = CatalogApiService.BuildProductBody(request);
        var list = ((object[])body["alternate_barcodes"]!).Cast<Dictionary<string, object?>>().ToList();
        list.Should().HaveCount(3);
        list[0]["quantity"].Should().BeNull();
        list[1]["quantity"].Should().Be(12L);
        list[2]["quantity"].Should().BeNull("кода нет среди известных — упаковки нет");
        list.Should().OnlyContain(d => !Equals(d["quantity"], 0) && !Equals(d["quantity"], 0d) && !Equals(d["quantity"], 0L));
    }

    // ------------------------------------------------------------------ б) POST/DELETE barcodes

    [Fact]
    public async Task Add_PostsOneCode_WithIfMatchVersion_AndOnlyRealFields()
    {
        using var server = new FakeNurCrmServer();
        server.Json("POST", "/api/main/products/p1/barcodes/", 200, "{\"ok\":true}");
        var api = await LoginAsync(server);
        var res = await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000028", Name = "Короб", Quantity = 12 }, "7");
        res.Ok.Should().BeTrue();
        var call = Calls(server, "POST", "/barcodes/").Single();
        HeaderOf(call, "If-Match").Should().Be("7");
        using var body = JsonDocument.Parse(call.RequestMessage.Body!);
        body.RootElement.GetProperty("barcode").GetString().Should().Be("4600000000028");
        body.RootElement.GetProperty("name").GetString().Should().Be("Короб");
        body.RootElement.GetProperty("quantity").GetInt64().Should().Be(12);

        // вариант без «штук в упаковке» и без названия — только barcode, quantity не уходит вовсе
        await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000035", Quantity = 0 }, null);
        var second = Calls(server, "POST", "/barcodes/").Last();
        HeaderOf(second, "If-Match").Should().BeEmpty("версия карточки неизвестна — без If-Match");
        second.RequestMessage.Body.Should().Be("{\"barcode\":\"4600000000035\"}");
    }

    [Fact]
    public async Task Remove_SendsDeleteOfOneCode_WithIfMatch()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p1/barcodes/4600000000028/", 200, "{}");
        var api = await LoginAsync(server);
        (await ProductBarcodeWriter.RemoveAsync(api, "p1", "4600000000028", "9")).Ok.Should().BeTrue();
        var call = Calls(server, "DELETE", "/barcodes/").Single();
        call.RequestMessage.Path.Should().EndWith("/barcodes/4600000000028/");
        HeaderOf(call, "If-Match").Should().Be("9");
    }

    [Fact]
    public async Task VersionConflict_RereadsCard_AndRetriesOnce_WithNewVersion()
    {
        using var server = new FakeNurCrmServer();
        const string scenario = "version";
        server.Server.Given(Request.Create().WithPath("/api/main/products/p1/barcodes/").UsingPost()).InScenario(scenario).WillSetStateTo("conflicted")
            .RespondWith(Response.Create().WithStatusCode(409).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"version_conflict\",\"detail\":\"версия устарела\"}"));
        server.Server.Given(Request.Create().WithPath("/api/main/products/p1/barcodes/").UsingPost()).InScenario(scenario).WhenStateIs("conflicted")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));
        server.Json("GET", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"version\":8,\"barcode\":\"1\",\"alternate_barcodes\":[]}");
        var api = await LoginAsync(server);
        var res = await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000028" }, "7");
        res.Ok.Should().BeTrue(res.Message + " | " + string.Join(" ; ", server.Server.LogEntries.Select(e => e.RequestMessage.Method + " " + e.RequestMessage.Path + " -> " + e.ResponseMessage.StatusCode)));
        var posts = Calls(server, "POST", "/barcodes/").ToList();
        posts.Should().HaveCount(2);
        HeaderOf(posts[0], "If-Match").Should().Be("7");
        HeaderOf(posts[1], "If-Match").Should().Be("8", "повтор — со свежей версией карточки");
    }

    [Fact]
    public async Task VersionConflict_Twice_StopsWithFriendlyText()
    {
        using var server = new FakeNurCrmServer();
        server.Json("POST", "/api/main/products/p1/barcodes/", 409, "{\"code\":\"version_conflict\",\"detail\":\"x\"}");
        server.Json("GET", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"version\":8}");
        var api = await LoginAsync(server);
        var res = await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000028" }, "7");
        res.Ok.Should().BeFalse();
        res.Message.Should().Be(ServerBarcodeErrors.VersionConflictText);
        Calls(server, "POST", "/barcodes/").Should().HaveCount(2, "ровно одна повторная попытка");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public async Task OldServer_FallsBackToMergedPatch(int status)
    {
        using var server = new FakeNurCrmServer();
        server.Json("POST", "/api/main/products/p1/barcodes/", status, "{\"detail\":\"Not found.\"}");
        server.Json("GET", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"barcode\":\"4600000000011\",\"alternate_barcodes\":[{\"barcode\":\"4600000000042\",\"name\":\"Пачка\",\"quantity\":10}]}");
        server.Json("PATCH", "/api/main/products/p1/", 200, "{\"id\":\"p1\"}");
        var api = await LoginAsync(server);
        var res = await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000028", Name = "Короб", Quantity = 24 }, "7");
        res.Ok.Should().BeTrue();
        res.UsedFallback.Should().BeTrue();
        using var body = JsonDocument.Parse(Calls(server, "PATCH", "/api/main/products/p1/").Single().RequestMessage.Body!);
        var list = body.RootElement.GetProperty("alternate_barcodes").EnumerateArray().ToList();
        list.Select(x => x.GetProperty("barcode").GetString()).Should().BeEquivalentTo("4600000000042", "4600000000028");
        list.Single(x => x.GetProperty("barcode").GetString() == "4600000000042").GetProperty("quantity").GetInt64().Should().Be(10, "серверный код не потерян");
    }

    [Fact]
    public async Task OldServer_Remove_FallsBackToPatchWithoutThatCode()
    {
        using var server = new FakeNurCrmServer();
        server.Json("DELETE", "/api/main/products/p1/barcodes/4600000000042/", 405, "{}");
        server.Json("GET", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"alternate_barcodes\":[{\"barcode\":\"4600000000042\",\"quantity\":10},{\"barcode\":\"4600000000059\",\"name\":\"Вкус\"}]}");
        server.Json("PATCH", "/api/main/products/p1/", 200, "{}");
        var api = await LoginAsync(server);
        (await ProductBarcodeWriter.RemoveAsync(api, "p1", "4600000000042", null)).Ok.Should().BeTrue();
        using var body = JsonDocument.Parse(Calls(server, "PATCH", "/api/main/products/p1/").Single().RequestMessage.Body!);
        body.RootElement.GetProperty("alternate_barcodes").EnumerateArray().Select(x => x.GetProperty("barcode").GetString()).Should().Equal("4600000000059");
    }

    // ------------------------------------------------------------------ в) barcode_duplicate

    [Fact]
    public void Parse_BarcodeDuplicate_ReadsOwnerFromData()
    {
        var owner = ServerBarcodeErrors.TryDuplicate(Card(DuplicateBody));
        owner.Should().NotBeNull();
        owner!.ProductId.Should().Be("p2");
        owner.Name.Should().Be("Чужой товар");
        owner.Code.Should().Be("15");
        owner.Source.Should().Be("alternate");
        owner.Barcode.Should().Be("4600000000011");
        ServerBarcodeErrors.TryDuplicate(Card("{\"code\":\"other\",\"data\":{}}")).Should().BeNull();
        ServerBarcodeErrors.TryDuplicate(Card("[1]")).Should().BeNull();
    }

    [Fact]
    public async Task Add_DuplicateCode_ReturnsOwnerAndTextWithName()
    {
        using var server = new FakeNurCrmServer();
        server.Json("POST", "/api/main/products/p1/barcodes/", 409, DuplicateBody);
        var api = await LoginAsync(server);
        var res = await ProductBarcodeWriter.AddAsync(api, "p1", new AlternateBarcodeVariant { Barcode = "4600000000011" }, null);
        res.Ok.Should().BeFalse();
        res.Duplicate!.ProductId.Should().Be("p2");
        res.Message.Should().Contain("Чужой товар").And.Contain("4600000000011");
        Calls(server, "POST", "/barcodes/").Should().HaveCount(1, "конфликт кода — не повторяем и не откатываемся на PATCH");
    }

    [Fact]
    public async Task Patch_DuplicateMainCode_MessageNamesOwner_ForCardAndWizard()
    {
        using var server = new FakeNurCrmServer();
        server.Json("PATCH", "/api/main/products/p1/", 409, DuplicateBody);
        var api = await LoginAsync(server);
        var act = async () => await api.PatchProductFieldsAsync("p1", new Dictionary<string, object?> { ["barcode"] = "4600000000011" });
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be(409);
        ex.Message.Should().Contain("Чужой товар");
        ServerBarcodeErrors.TryDuplicate(ex)!.ProductId.Should().Be("p2");
    }

    // ------------------------------------------------------------------ Binder: один код — POST, без полного списка

    [Fact]
    public async Task Bind_Alt_AddsOneCode_WithCardVersion_AndNeverPatchesWholeList()
    {
        var saves = 0;
        string? seenVersion = null;
        AlternateBarcodeVariant? seen = null;
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("{\"id\":\"p1\",\"version\":5,\"barcode\":\"4870200221000\",\"alternate_barcodes\":[{\"barcode\":\"4870200221017\",\"quantity\":10}]}"));
        InvoiceBarcodeBinder.SaveOverride = (_, _, _) => { saves++; return Task.FromResult<string?>(null); };
        InvoiceBarcodeBinder.AddOverride = (_, v, version, _) => { seen = v; seenVersion = version; return Task.FromResult(new BarcodeWriteOutcome(true)); };
        var r = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261", new AlternateBarcodeVariant { Barcode = "4870200221261", Name = "Коробка", Quantity = 24 });
        r.Ok.Should().BeTrue();
        r.Plan.Should().Be(InvoiceBindPlan.Alt);
        saves.Should().Be(0, "список целиком больше не пишется");
        seenVersion.Should().Be("5");
        seen!.Barcode.Should().Be("4870200221261");
        r.Variants!.Select(v => v.Barcode).Should().BeEquivalentTo("4870200221017", "4870200221261");
    }

    [Fact]
    public async Task Bind_Alt_ServerSaysDuplicate_ResultCarriesOwner()
    {
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("{\"id\":\"p1\",\"barcode\":\"4870200221000\"}"));
        var owner = new ServerBarcodeOwner("p2", "Чужой товар", "15", "product", "4870200221261");
        InvoiceBarcodeBinder.AddOverride = (_, _, _, _) => Task.FromResult(new BarcodeWriteOutcome(false, "занят", owner));
        var r = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261", new AlternateBarcodeVariant { Barcode = "4870200221261" });
        r.Ok.Should().BeFalse();
        r.Duplicate.Should().Be(owner);
    }

    [Fact]
    public async Task SaveAlternates_OnlyAdditionsAndRemovals_UsePostAndDelete()
    {
        var calls = new List<string>();
        InvoiceBarcodeBinder.AddOverride = (_, v, ver, _) => { calls.Add("add " + v.Barcode + " v" + ver); return Task.FromResult(new BarcodeWriteOutcome(true)); };
        InvoiceBarcodeBinder.RemoveOverride = (_, code, ver, _) => { calls.Add("del " + code + " v" + ver); return Task.FromResult(new BarcodeWriteOutcome(true)); };
        var saves = 0;
        InvoiceBarcodeBinder.SaveOverride = (_, _, _) => { saves++; return Task.FromResult<string?>(null); };
        var original = new List<AlternateBarcodeVariant> { new() { Barcode = "111111", Quantity = 5 }, new() { Barcode = "222222" } };
        var now = new List<AlternateBarcodeVariant> { new() { Barcode = "111111", Quantity = 5 }, new() { Barcode = "333333", Name = "Новый" } };
        var r = await InvoiceBarcodeBinder.SaveAlternatesAsync("p1", now, default, original, "4");
        r.Ok.Should().BeTrue();
        calls.Should().Equal("del 222222 v4", "add 333333 v");
        saves.Should().Be(0);
    }

    [Fact]
    public async Task SaveAlternates_ChangedPackOfExistingCode_ReplacesWholeSet()
    {
        var adds = 0;
        InvoiceBarcodeBinder.AddOverride = (_, _, _, _) => { adds++; return Task.FromResult(new BarcodeWriteOutcome(true)); };
        IReadOnlyDictionary<string, object?>? saved = null;
        InvoiceBarcodeBinder.SaveOverride = (_, fields, _) => { saved = fields; return Task.FromResult<string?>(null); };
        var original = new List<AlternateBarcodeVariant> { new() { Barcode = "111111", Quantity = 5 } };
        var now = new List<AlternateBarcodeVariant> { new() { Barcode = "111111", Quantity = 6 } };
        (await InvoiceBarcodeBinder.SaveAlternatesAsync("p1", now, default, original, "4")).Ok.Should().BeTrue();
        adds.Should().Be(0);
        ((object[])saved!["alternate_barcodes"]!).Should().HaveCount(1, "правка существующего кода — замена набора целиком (PATCH)");
    }

    // ------------------------------------------------------------------ г) приход: скан упаковки

    private static CatalogProductTileVm Tile(string id, string barcode, params (string Code, string? Name, double Qty)[] alts) =>
        new(id, "Плитка", "100 сом", false)
        {
            Barcode = barcode,
            AlternateBarcodeVariants = alts.Select(a => new AlternateBarcodeVariant { Barcode = a.Code, Name = a.Name, Quantity = a.Qty }).ToList(),
        };

    [Fact]
    public void PackFor_ProductWithSaleUnits_AltPackCodeIsNPieces_NotPlusOne()
    {
        ProductSaleUnits.Set("su1", [new ProductSaleUnits.Unit { Id = "7", Name = "Упаковка", UnitCode = "pack", Factor = 1.44m, Barcode = "4600000001440" }]);
        var tile = Tile("su1", "4600000000520", ("4600000000200", "Коробка", 12));
        var hit = InvoiceBarcodeRules.PackFor(tile, "4600000000200");
        hit.Should().NotBeNull("сервер доп. штрихкод при приходе не умножает — у товара с упаковками тоже");
        hit!.Quantity.Should().Be(12);
        InvoiceBarcodeRules.PackFor(tile, "4600000000520").Should().BeNull("основной код — одна единица");
    }

    [Fact]
    public void PackFor_SaleUnitBarcode_IsFactorBaseUnits()
    {
        ProductSaleUnits.Set("su2", [new ProductSaleUnits.Unit { Id = "7", Name = "Упаковка", UnitCode = "pack", Factor = 1.44m, Barcode = "4600000001440" }]);
        var tile = Tile("su2", "4600000000520");
        var hit = InvoiceBarcodeRules.PackFor(tile, "4600000001440");
        hit.Should().NotBeNull();
        hit!.Quantity.Should().Be(1.44, "qty в базовой единице (м²) без sale_unit_id — так сервер тоже принимает приход");
        // чужой товар с тем же кодом упаковки — не его упаковка
        InvoiceBarcodeRules.PackFor(Tile("other", "4600000000999"), "4600000001440").Should().BeNull();
    }

    [Fact]
    public void ReceiptComment_VariantNotesAreCutAt1500_AndWholeCommentAt2000()
    {
        var method = typeof(PurchaseReceivingService).GetMethod("AppendVariantNotes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var lines = new List<ReceivingLineVm> { new() { VariantNote = new string('в', 3000) } };
        var body = new Dictionary<string, object?> { ["comment"] = new string('н', 900) };
        method.Invoke(null, [body, lines]);
        ((string)body["comment"]!).Length.Should().BeLessThanOrEqualTo(2000);
        var only = new Dictionary<string, object?>();
        method.Invoke(null, [only, lines]);
        ((string)only["comment"]!).Length.Should().BeGreaterThan(1400).And.BeLessThanOrEqualTo(2000, "режем на 1500, а не на 600");
    }

    [Fact]
    public void ReceiptLine_ParsesBarcodeFromServer()
    {
        var json = "{\"id\":\"r1\",\"supplier_id\":\"s1\",\"items\":[{\"id\":\"l1\",\"product_id\":\"p1\",\"product_name\":\"Вода\",\"qty\":3,\"purchase_price\":40,\"barcode\":\"4600000000011\"},{\"id\":\"l2\",\"product_id\":\"p2\",\"product_name\":\"Хлеб\",\"qty\":1,\"purchase_price\":20}]}";
        var receipt = (SupplierReceipt)typeof(SupplierApiService).GetMethod("ParseReceipt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [Card(json)])!;
        receipt.Lines[0].Barcode.Should().Be("4600000000011");
        receipt.Lines[1].Barcode.Should().BeNull("старый сервер/нет поля — колонка берёт код из каталога");
    }
}
