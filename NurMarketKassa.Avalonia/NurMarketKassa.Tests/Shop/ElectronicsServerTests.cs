using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-13 («Сервер 13.11 — серийные номера, гарантия, Электроника», сборка apl1311+sn1311): касса и сервер. Поля товара track_serials / warranty_months, serial_numbers в
/// продаже (pos/checkout, отдельный POST …/serials/), ошибки serial_*, поиск serials/?serial=, serial_numbers и warranty_until в чеке, возврат с номерами, досылка офлайн-чека.
/// Всё включается флагом компании electronics_2026_11 (ElectronicsProfile.ServerFlagEnabled); без флага тело продажи прежнее. Сервер — заглушка WireMock, настоящий не нужен.</summary>
[Collection("UserPreferences")]
public sealed class ElectronicsServerTests : IDisposable
{
    private const string Imei = "490154203237518";
    private readonly FakeNurCrmServer _fake = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "electronics-srv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Func<bool> _oldFlag;
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ElectronicsServerTests()
    {
        Directory.CreateDirectory(_dir);
        ProductSerialRules.FileOverride = Path.Combine(_dir, "product-serials.json");
        ProductSerialRules.ResetForTests();
        _oldFlag = ElectronicsProfile.ServerFlagEnabled;
        ElectronicsProfile.ServerFlagEnabled = () => true;
        UserPreferences.Instance.Language = AppLanguage.Russian; // проверки текстов — по-русски; языки перебирает отдельный тест
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ElectronicsProfile.ServerFlagEnabled = _oldFlag;
        ProductSerialRules.FileOverride = null;
        ProductSerialRules.ResetForTests();
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ───────────── 1. товар: track_serials / warranty_months ─────────────

    [Fact]
    public void ProductCard_TrackSerialsAndWarranty_AreRemembered_FromListChangesAndCard()
    {
        ProductCatalogMapper.TryTile(J("""{"id":"p-1","name":"Телефон","price":"20000","quantity":"3","track_serials":true,"warranty_months":12}"""), "http://x").Should().NotBeNull();
        ProductSerialRules.TrackSerials("p-1").Should().BeTrue();
        ProductSerialRules.WarrantyMonths("p-1").Should().Be(12);

        // products/changes — сокращённая карточка: полей нет — прежние значения не стираются.
        ProductCatalogMapper.TryTile(J("""{"id":"p-1","name":"Телефон","price":"21000","quantity":"2"}"""), "http://x");
        ProductSerialRules.TrackSerials("p-1").Should().BeTrue();
        ProductSerialRules.WarrantyMonths("p-1").Should().Be(12);

        // строка и null: гарантия «нет» = 0; track_serials строкой.
        ProductCatalogMapper.TryTile(J("""{"id":"p-2","name":"Чехол","price":"500","quantity":"9","track_serials":"false","warranty_months":null}"""), "http://x");
        ProductSerialRules.TrackSerials("p-2").Should().BeFalse();
        ProductSerialRules.WarrantyMonths("p-2").Should().Be(0);

        // Старый сервер: полей нет вообще — «не знаем» (касса решает по категории, как раньше).
        ProductCatalogMapper.TryTile(J("""{"id":"p-3","name":"Хлеб","price":"40","quantity":"9"}"""), "http://x");
        ProductSerialRules.TrackSerials("p-3").Should().BeNull();
        ProductSerialRules.WarrantyMonths("p-3").Should().BeNull();
    }

    [Fact]
    public void ProductRules_AreSavedToDisk_AndReadBack()
    {
        ProductSerialRules.Remember("p-9", J("""{"track_serials":true,"warranty_months":24}"""));
        ProductSerialRules.SaveIfDirty();
        File.Exists(ProductSerialRules.FileOverride).Should().BeTrue();

        ProductSerialRules.ResetForTests(); // как перезапуск кассы
        ProductSerialRules.TrackSerials("p-9").Should().BeTrue();
        ProductSerialRules.WarrantyMonths("p-9").Should().Be(24);
    }

    [Fact]
    public void ServerTrackSerials_MakesProductNeedSerial_WhateverTheCategory_AndWarrantyFromCardWins()
    {
        var p = UserPreferences.Instance;
        var (sphere, detail) = (p.MarketSphere, p.MarketSphereDetail);
        try
        {
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = ElectronicsProfile.Id;
            ElectronicsProfile.NeedsSerialFor("p-7", "Чехлы").Should().BeFalse();
            ProductSerialRules.Remember("p-7", J("""{"track_serials":true,"warranty_months":6}"""));
            ElectronicsProfile.NeedsSerialFor("p-7", "Чехлы").Should().BeTrue("сервер сказал track_serials");
            ElectronicsProfile.WarrantyMonthsFor("p-7", "Чехлы").Should().Be(6);
            ElectronicsProfile.NeedsSerialFor("p-unknown", "Смартфоны").Should().BeTrue("карточки нет — по категории, как раньше");
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = null;
            ElectronicsProfile.NeedsSerialFor("p-7", "Чехлы").Should().BeFalse("не электроника — серийники не спрашиваются");
        }
        finally
        {
            p.MarketSphere = sphere;
            p.MarketSphereDetail = detail;
        }
    }

    // ───────────── 2. продажа: serial_numbers в pos/checkout ─────────────

    private static string CartWithSerials(string productId, params (double Qty, string[] Serials)[] lines)
    {
        var items = new JsonArray();
        var n = 0;
        foreach (var (qty, serials) in lines)
        {
            var item = new JsonObject
            {
                ["id"] = "line-" + ++n, ["product_id"] = productId, ["product_name"] = "Телефон X1", ["quantity"] = qty, ["unit_price"] = 20000.0, ["line_total"] = qty * 20000.0,
            };
            if (serials.Length > 0)
                item[ElectronicsProfile.SerialsField] = new JsonArray(serials.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            items.Add(item);
        }
        return new JsonObject { ["items"] = items }.ToJsonString();
    }

    private static readonly string Shift = Guid.NewGuid().ToString();

    private static JsonObject? Quick(string cart) =>
        QuickCheckoutBody.TryBuild(cart, "cash", "100000", null, null, Shift, null, false, null, false, out _);

    [Fact]
    public void Checkout_SendsSerialNumbers_WithFlag_AndNothingWithout()
    {
        var pid = Guid.NewGuid().ToString();
        var cart = CartWithSerials(pid, (2, new[] { Imei, "F2LXK0QJ1234" }));

        var on = Quick(cart)!;
        on["items"]![0]!["serial_numbers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal(Imei, "F2LXK0QJ1234");

        ElectronicsProfile.ServerFlagEnabled = () => false;
        var off = Quick(cart)!;
        off.ToJsonString().Should().NotContain("serial_numbers").And.NotContain(Imei, "без флага компании тело продажи прежнее");
    }

    [Fact]
    public void Checkout_MergedLines_MergeSerialsWithoutRepeats_AndLineWithoutSerialsStaysClean()
    {
        var pid = Guid.NewGuid().ToString();
        var merged = Quick(CartWithSerials(pid, (1, new[] { "AAA11111" }), (2, new[] { "aaa11111", "BBB22222" })))!;
        var item = merged["items"]!.AsArray().Single()!;
        item["qty"]!.GetValue<string>().Should().Be("3");
        item["serial_numbers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal(new[] { "AAA11111", "BBB22222" }, "одинаковый номер (регистр не важен) записан один раз");

        var plain = Quick(CartWithSerials(pid, (1, Array.Empty<string>())))!;
        plain["items"]![0]!.AsObject().ContainsKey("serial_numbers").Should().BeFalse();
    }

    [Fact]
    public void Fingerprint_IgnoresSerials_SoFlagFlipDoesNotChangeTheRetryKey()
    {
        var pid = Guid.NewGuid().ToString();
        var cart = CartWithSerials(pid, (1, new[] { Imei }));
        var with = Quick(cart)!;
        ElectronicsProfile.ServerFlagEnabled = () => false;
        var without = Quick(cart)!;
        QuickCheckoutBody.Fingerprint(with).Should().Be(QuickCheckoutBody.Fingerprint(without));
    }

    [Fact]
    public void OfflineQueue_ReplayBody_CarriesSerials()
    {
        var pid = Guid.NewGuid().ToString();
        var entry = new OfflineSaleEntry { CartJson = CartWithSerials(pid, (1, new[] { Imei })), PaymentMethod = "cash", CashReceived = "100000", CreatedAt = DateTimeOffset.Now.AddMinutes(-3) };
        var body = SyncService.BuildQuickReplayBody(entry);
        body.Should().NotBeNull();
        body!["items"]![0]!["serial_numbers"]![0]!.GetValue<string>().Should().Be(Imei, "серийники офлайн-чека уходят при досылке");
        body["sold_at"].Should().NotBeNull();
    }

    // ───────────── 3. уже добавленная строка: POST …/serials/ ─────────────

    [Fact]
    public async Task CartLine_Serials_AreReplacedThroughSerialsEndpoint()
    {
        _fake.Json("POST", "/api/main/pos/sales/cart-1/items/it-1/serials/", 200, "{\"id\":\"it-1\",\"serial_numbers\":[\"AAA11111\",\"BBB22222\"]}");
        var api = await LoggedInAsync(_fake);

        var answer = await new SalesApiService(api).PosCartItemSerialsAsync("cart-1", "it-1", new[] { "AAA11111", "bbb 22222".Replace(" ", ""), "aaa11111", " " });

        var body = BodyOf(Requests(_fake, "POST", "/serials/").Single());
        body["serial_numbers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal(new[] { "AAA11111", "bbb22222" }, "повторы и пустые не уходят");
        ServerSerials.LineSerials(answer).Should().Equal("AAA11111", "BBB22222");
    }

    [Fact]
    public async Task OldPath_PushesMergedSerialsToTheServerLine_AfterAddItem_OnlyWithFlag()
    {
        var pid = Guid.NewGuid().ToString();
        _fake.Json("POST", "/api/main/pos/sales/cart-9/add-item/", 200, "{}");
        _fake.Json("GET", "/api/main/pos/carts/cart-9/", 200, "{\"id\":\"cart-9\",\"items\":[{\"id\":\"it-9\",\"product_id\":\"" + pid + "\",\"quantity\":\"3\",\"unit_price\":\"20000.00\"}]}");
        _fake.Json("POST", "/api/main/pos/sales/cart-9/items/it-9/serials/", 200, "{\"id\":\"it-9\"}");
        var api = await LoggedInAsync(_fake);
        var cart = CartWithSerials(pid, (1, new[] { "AAA11111" }), (2, new[] { "BBB22222", "CCC33333" }));

        await StagingCartService.PushItemsFromSnapshotAsync(new SalesApiService(api), "cart-9", cart, default);

        var post = Requests(_fake, "POST", "/items/it-9/serials/").Should().ContainSingle().Subject;
        BodyOf(post)["serial_numbers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("AAA11111", "BBB22222", "CCC33333");

        _fake.Server.ResetLogEntries();
        ElectronicsProfile.ServerFlagEnabled = () => false;
        await StagingCartService.PushItemsFromSnapshotAsync(new SalesApiService(api), "cart-9", cart, default);
        Requests(_fake, "POST", "/serials/").Should().BeEmpty("без флага компании серийники на сервер не уходят");
    }

    // ───────────── 4. ошибки сервера ─────────────

    private static ApiException Err(int status, string body) => new("server", status, J(body));

    [Fact]
    public void SerialErrors_HaveFriendlyTexts_AndCheckStaysInTheTill()
    {
        var sold = Err(400, """{"code":"serial_already_sold","index":1,"serial":"AAA11111","sale_id":"s-1"}""");
        var text = ServerErrorCodes.Friendly(sold)!;
        text.Should().Contain("AAA11111").And.Contain("Строка 2").And.Contain("Чек остаётся в кассе");

        ServerErrorCodes.Friendly(Err(400, """{"code":"serial_count_mismatch","index":0,"count":3,"quantity":2}"""))!.Should().Contain("3 / 2");
        ServerErrorCodes.Friendly(Err(400, """{"code":"serial_duplicate_in_line","index":0,"serial":"AAA11111"}"""))!.Should().Contain("дважды");
        ServerErrorCodes.Friendly(Err(400, """{"code":"serial_too_long","index":0}"""))!.Should().Contain("64");

        // Форма «массив ошибок строк».
        ServerErrorCodes.Friendly(Err(400, """{"errors":[{"code":"serial_already_sold","index":2,"serial":"ZZZ99999"}]}"""))!.Should().Contain("ZZZ99999").And.Contain("Строка 3");

        // Не серийник — как раньше.
        ServerErrorCodes.Friendly(Err(400, """{"code":"что-то_другое"}""")).Should().BeNull();
        ServerErrorCodes.IsSerialError(sold).Should().BeTrue();
        ServerErrorCodes.IsSerialError(Err(400, """{"code":"duplicate_line"}""")).Should().BeFalse();
    }

    [Fact]
    public void SerialErrors_ReachTheCashier_ThroughPaymentMessages_AndOutageMonitor()
    {
        var sold = Err(400, """{"code":"serial_already_sold","serial":"AAA11111"}""");
        PaymentErrorMessages.ForCashier(sold).Should().Be(ServerErrorCodes.SerialFriendly(sold));
        ServerOutageMonitor.FriendlyError(sold).Should().Be(ServerErrorCodes.SerialFriendly(sold));
    }

    [Fact]
    public void SerialErrors_AreTranslatedIntoAllFiveLanguages()
    {
        var sold = Err(400, """{"code":"serial_already_sold","serial":"AAA11111"}""");
        var seen = new HashSet<string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            var text = ServerErrorCodes.SerialFriendly(sold)!;
            text.Should().Contain("AAA11111");
            seen.Add(text);
        }
        seen.Should().HaveCount(5, "на каждом языке свой текст");
    }

    [Fact]
    public async Task OnlineCheckout_SerialRefusal_IsRaisedWithPayload_SoTillCanExplainIt()
    {
        _fake.Json("POST", "/api/main/pos/checkout/", 400, """{"code":"serial_already_sold","index":0,"serial":"AAA11111","sale_id":"s-1"}""");
        var api = await LoggedInAsync(_fake);
        var act = async () => await api.PostWithIdempotencyKeyAsync("api/main/pos/checkout/", new { payment_method = "cash" }, "k-1");
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ServerErrorCodes.SerialFriendly(ex).Should().Contain("AAA11111");
    }

    // ───────────── 5. поиск serials/ ─────────────

    private const string LookupAnswer = """
        {"serial":"AAA11111","sold":true,"results":[
          {"serial":"AAA11111","sale_id":"s-1","sale_number":1234,"sale_status":"paid","sale_item_id":"si-1","product_id":"p-1","name":"Телефон X1",
           "sold_at":"2026-11-13T10:00:00+06:00","warranty_until":"2027-11-13","warranty_active":true,"returned":false,"returned_at":null,"sale_return_id":null,"duplicate":false},
          {"serial":"AAA11111","sale_id":"s-0","sale_number":900,"sale_status":"paid","sale_item_id":"si-0","product_id":"p-1","name":"Телефон X1",
           "sold_at":"2025-10-01T10:00:00+06:00","warranty_until":"2026-10-01","warranty_active":false,"returned":true,"returned_at":"2025-10-05T10:00:00+06:00","sale_return_id":"r-1","duplicate":true}]}
        """;

    [Fact]
    public async Task SerialLookup_AsksServer_AndParsesSoldReturnedWarrantyAndDuplicate()
    {
        _fake.Json("GET", "/api/main/serials/", 200, LookupAnswer);
        var api = await LoggedInAsync(_fake);

        var found = await new SalesApiService(api).SerialLookupAsync(" AAA11111 ");

        Requests(_fake, "GET", "/api/main/serials/").Single().RequestMessage.Query!["serial"].Single().Should().Be("AAA11111");
        found.Sold.Should().BeTrue();
        found.Results.Should().HaveCount(2);
        var first = found.Results[0];
        (first.SaleId, first.SaleNumber, first.WarrantyActive, first.Returned, first.Duplicate).Should().Be(("s-1", "1234", true, false, false));
        first.WarrantyUntil.Should().Be(new DateTime(2027, 11, 13));
        first.StatusText().Should().Contain("Продан").And.Contain("1234").And.Contain("13.11.2027").And.Contain("гарантия действует");
        var old = found.Results[1];
        old.Returned.Should().BeTrue();
        old.Duplicate.Should().BeTrue();
        old.StatusText().Should().Contain("Возвращён").And.Contain("гарантия истекла").And.Contain("повтор номера");
        first.ToRecord().Should().Match<SerialRecord>(r => r.SaleId == "s-1" && r.ReceiptNo == "1234" && r.Serial == "AAA11111" && r.WarrantyMonths == 12);
    }

    [Fact]
    public async Task SerialLookup_EmptyQuery_DoesNotAskServer()
    {
        var api = await LoggedInAsync(_fake);
        var found = await new SalesApiService(api).SerialLookupAsync("  ");
        found.Results.Should().BeEmpty();
        Requests(_fake, "GET", "/serials/").Should().BeEmpty();
    }

    [Fact]
    public void LocalJournal_RemainsTheFallback_WhenServerIsOffOrSilent()
    {
        var journal = Path.Combine(_dir, "serial-journal.json");
        SerialJournal.FileOverride = journal;
        try
        {
            SerialJournal.Append(new[] { new SerialRecord { SaleId = "sale-1", ReceiptNo = "5", SoldAt = DateTime.Now, Title = "Телефон", Serial = Imei } });
            SerialJournal.Find("237518").Should().ContainSingle("офлайн/без ответа сервера поиск идёт по журналу кассы");
        }
        finally
        {
            SerialJournal.FileOverride = null;
        }
    }

    // ───────────── 6. чек: serial_numbers и warranty_until ─────────────

    private const string SoldReceipt = """
        {"id":"sale-5","number":1234,"created_at":"2026-11-13T10:00:00+06:00","items":[
          {"id":"si-1","product_id":"p-1","name":"Телефон X1","quantity":"2","unit_price":"20000.00","serial_numbers":["490154203237518","F2LXK0QJ1234"],"warranty_until":"2027-11-13"},
          {"id":"si-2","product_id":"p-2","name":"Чехол","quantity":"1","unit_price":"500.00"}]}
        """;

    [Fact]
    public void SoldReceipt_ServerSerialsAndWarrantyUntil_BecomeRecordsAndReceiptLines()
    {
        var sale = J(SoldReceipt);
        var records = ServerSerials.RecordsFromSale(sale);
        records.Should().HaveCount(2);
        records.Should().OnlyContain(r => r.SaleId == "sale-5" && r.ReceiptNo == "1234" && r.WarrantyUntil == new DateTime(2027, 11, 13) && r.WarrantyMonths == 12);
        records.Select(r => r.Serial).Should().Equal(Imei, "F2LXK0QJ1234");

        var p = UserPreferences.Instance;
        var (sphere, detail) = (p.MarketSphere, p.MarketSphereDetail);
        try
        {
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = ElectronicsProfile.Id;
            var phone = CartDisplayHelper.EnumerateSaleLineItems(sale).First();
            var lines = WarrantyCard.ItemLinesFromServer(phone, 32);
            lines.Should().Contain("IMEI: " + Imei).And.Contain("S/N: F2LXK0QJ1234").And.Contain("Гарантия до 13.11.2027");
            WarrantyCard.ItemLinesFromServer(CartDisplayHelper.EnumerateSaleLineItems(sale).Last(), 32).Should().BeEmpty("у чехла номеров нет");

            var card = WarrantyCard.BuildCard(records[0], 32);
            card.Should().Contain("1234").And.Contain(Imei).And.Contain("13.11.2027").And.Contain("12 мес.");
            p.MarketSphereDetail = null;
            WarrantyCard.ItemLinesFromServer(phone, 32).Should().BeEmpty("в других сферах чек прежний");
        }
        finally
        {
            p.MarketSphere = sphere;
            p.MarketSphereDetail = detail;
        }
    }

    [Fact]
    public void CartLineFromServer_SerialNumbers_AreReadWhenLocalFieldIsAbsent()
    {
        var line = J("""{"id":"it-1","serial_numbers":["AAA11111"]}""");
        SerialJournal.ReadSerials(line).Should().Equal("AAA11111");
        SerialJournal.ReadSerials(J("""{"id":"it-1","serials":["LOCAL001"],"serial_numbers":["AAA11111"]}""")).Should().Equal("LOCAL001");
    }

    // ───────────── 7. возврат с серийниками ─────────────

    [Fact]
    public async Task Return_SendsReturnedSerialNumbers_InItems_AndWarrantyReasonAsPlainText()
    {
        _fake.Json("POST", "/api/main/pos/sales/sale-5/return/", 200, "{\"id\":\"sale-5\",\"refund\":{\"amount\":\"20000.00\"}}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var line = new PosRefundLineRequest { LineId = "si-1", Title = "Телефон X1", Quantity = 1, OriginalQuantity = 2, SerialNumbers = new[] { Imei } };

        await sales.PosReturnWithDetailsAsync("sale-5", new[] { line }, ElectronicsProfile.WarrantyReason, false, default, "k-1");

        var body = BodyOf(Requests(_fake, "POST", "/return/").Single());
        body["reason"]!.GetValue<string>().Should().Be("Гарантия");
        var item = body["items"]!.AsArray().Single()!;
        item["sale_item_id"]!.GetValue<string>().Should().Be("si-1");
        item["serial_numbers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal(Imei);

        _fake.Server.ResetLogEntries();
        await sales.PosReturnSaleAsync("sale-5", new[] { line }, default, "k-2");
        BodyOf(Requests(_fake, "POST", "/return/").Single())["items"]![0]!["serial_numbers"]![0]!.GetValue<string>().Should().Be(Imei, "и в обычном запросе возврата");
    }

    [Fact]
    public async Task Return_WithoutFlagOrSerials_BodyIsAsBefore()
    {
        _fake.Json("POST", "/api/main/pos/sales/sale-6/return/", 200, "{\"id\":\"sale-6\"}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var plain = new PosRefundLineRequest { LineId = "si-1", Title = "Хлеб", Quantity = 1, OriginalQuantity = 1 };
        await sales.PosReturnSaleAsync("sale-6", new[] { plain }, default, "k-3");
        BodyOf(Requests(_fake, "POST", "/return/").Single())["items"]![0]!.AsObject().ContainsKey("serial_numbers").Should().BeFalse();

        _fake.Server.ResetLogEntries();
        ElectronicsProfile.ServerFlagEnabled = () => false;
        var withSerials = new PosRefundLineRequest { LineId = "si-1", Title = "Телефон", Quantity = 1, OriginalQuantity = 1, SerialNumbers = new[] { Imei } };
        await sales.PosReturnSaleAsync("sale-6", new[] { withSerials }, default, "k-4");
        BodyOf(Requests(_fake, "POST", "/return/").Single())["items"]![0]!.AsObject().ContainsKey("serial_numbers").Should().BeFalse("без флага компании номера не шлём");
    }

    [Theory]
    [InlineData("Гарантия", true)]
    [InlineData(" гарантия ", true)]
    [InlineData("Warranty", true)]
    [InlineData("Kafolat", true)]
    [InlineData("Передумал", false)]
    [InlineData("", false)]
    public void WarrantyReason_IsRecognisedInAnyLanguage(string reason, bool expected) =>
        ElectronicsProfile.IsWarrantyReason(reason).Should().Be(expected);

    // ───────────── 8. флаг профиля ─────────────

    [Fact]
    public void CompanyFlag_Electronics_IsReadFromCapabilities_LikeHardware()
    {
        using var on = JsonDocument.Parse("{\"features\":[\"checkout_branch\"],\"company_flags\":{\"electronics_2026_11\":true,\"hardware_2026_11\":false}}");
        ServerCapabilities.Parse(on.RootElement).Should().Contain("electronics_2026_11").And.NotContain("hardware_2026_11");
        using var off = JsonDocument.Parse("{\"features\":[\"checkout_branch\"],\"company_flags\":{\"electronics_2026_11\":false}}");
        ServerCapabilities.Parse(off.RootElement).Should().NotContain("electronics_2026_11");
        ServerCapabilities.Electronics202611.Should().Be("electronics_2026_11");
    }
}
