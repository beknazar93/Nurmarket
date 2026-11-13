using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11 (Умар включил на боевом сервере атомарный POST main/products/&lt;id&gt;/convert-to-packages/): товар склада переводится в упаковки ОДНОЙ операцией сервера
/// (expected_quantity — остаток из подтверждения, Idempotency-Key — один на попытку), только потом приход упаковок. Отказы — понятные причины, строка не проводится; 404/405 —
/// прежний путь (чтение + PATCH) с предупреждением. Всё на заглушке (WireMock), реальный сервер не трогаем.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingConvertToPackagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-convert-" + Guid.NewGuid().ToString("N"));

    public ReceivingConvertToPackagesTests()
    {
        Directory.CreateDirectory(_dir);
        ReceivingPackSizes.DataDirOverride = _dir;
        ReceivingPackSizes.Reset();
    }

    public void Dispose()
    {
        ReceivingPackSizes.DataDirOverride = null;
        ReceivingPackSizes.Reset();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string ConvertPath = "/api/main/products/pa/convert-to-packages/";
    private static readonly PurchaseReceivingService.Supplier Supplier = new("s1", "Поставщик", null, null);

    /// <summary>Пример Умара: остаток 100 шт, цена 15 (закупка 10); принимаем упаковками по 10 — одна упаковка, кассир подтвердил остаток 100.</summary>
    private static ReceivingLineVm Line(double stockSeen = 100, string key = "key-1")
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Barcode = "4600000000011", StockBefore = stockSeen, Quantity = 1, OriginalPurchasePrice = 10, OriginalSalePrice = 15 };
        l.ProductId = "pa";
        l.Source = ReceivingSource.Warehouse;
        l.PurchasePrice = 10;
        l.SalePrice = 15;
        l.EnableReceivingPricing();
        l.SetPackSale(true, 10).Should().BeTrue();
        l.PackConvertExpected = stockSeen;
        l.PackConvertKey = key;
        return l;
    }

    private const string CardAfter = "{\"id\":\"pa\",\"quantity\":11,\"price\":\"150.00\",\"purchase_price\":\"100.00\",\"packages\":[{\"id\":\"k1\",\"name\":\"штука\",\"quantity_in_package\":10,\"piece_unit_price\":\"15.00\"}]}";

    /// <summary>card — карточка товара для GET после прихода (null — тест ставит свою).</summary>
    private static void Stub(FakeNurCrmServer s, string? card = CardAfter)
    {
        s.Server.ResetMappings();
        if (card is not null)
            s.Json("GET", "/api/main/products/pa/", 200, card);
        s.Json("GET", "/api/main/products/pb/", 200, "{\"id\":\"pb\",\"quantity\":5,\"price\":\"25.00\",\"purchase_price\":\"20.00\"}");
        s.Json("PATCH", "/api/main/products/pb/", 200, "{\"id\":\"pb\",\"price\":\"25.00\",\"purchase_price\":\"20.00\"}");
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"price\":\"150.00\",\"purchase_price\":\"100.00\"}");
        s.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"100.00\"}");
    }

    private static async Task<IDisposable> UseApiAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return new Restorer(() => PosApp.CatalogApi = previous);
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static List<WireMock.Logging.ILogEntry> Calls(FakeNurCrmServer s, string method, string pathPart) =>
        s.Server.LogEntries.Where(e => e.RequestMessage.Method == method && e.RequestMessage.Path.Contains(pathPart)).ToList();

    private static string Header(WireMock.Logging.ILogEntry e, string name) =>
        e.RequestMessage.Headers!.First(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value.First();

    // ------------------------------------------------------------------ успех

    [Fact]
    public async Task Success_converts_atomically_with_expected_quantity_and_key_then_receives_packs_once()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"150.00\",\"packages\":[{\"id\":\"k1\",\"name\":\"штука\",\"quantity_in_package\":10,\"piece_unit_price\":\"15.00\"}]}");
        using var guard = await UseApiAsync(server);
        var line = Line();

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        result.Errors.Should().BeEmpty();
        var convert = Calls(server, "POST", "convert-to-packages").Should().ContainSingle().Subject;
        var body = (JsonObject)JsonNode.Parse(convert.RequestMessage.Body!)!;
        body["quantity_in_package"]!.GetValue<double>().Should().Be(10);
        body["expected_quantity"]!.GetValue<double>().Should().Be(100, "остаток, который кассир видел в подтверждении");
        body["allow_rounding"]!.GetValue<bool>().Should().BeFalse("округление — только по явному согласию");
        body["idempotency_key"]!.GetValue<string>().Should().Be("key-1");
        Header(convert, "Idempotency-Key").Should().Be("key-1");
        body.ContainsKey("price").Should().BeFalse("цену упаковки сервер считает сам (цена × штук)");
        body.ContainsKey("piece_unit_price").Should().BeFalse();
        server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Body!.Contains("packages_input"), "прежний PATCH с окном гонки не нужен");
        var receipt = Calls(server, "POST", "/receipt/").Should().ContainSingle("приход упаковок — один").Subject;
        server.Server.LogEntries.ToList().IndexOf(convert).Should().BeLessThan(server.Server.LogEntries.ToList().IndexOf(receipt), "сначала перевод, потом приход");
        var item = ((JsonObject)JsonNode.Parse(receipt.RequestMessage.Body!)!)["items"]!.AsArray()[0]!.AsObject();
        item["qty"]!.GetValue<double>().Should().Be(1, "приход — в упаковках");
        item["purchase_price"]!.GetValue<double>().Should().Be(100, "по цене упаковки (10 × 10)");
    }

    [Fact]
    public void Calculation_after_conversion_and_confirmation_text_use_the_fresh_stock()
    {
        var line = Line(stockSeen: 100);
        line.StockBefore.Should().Be(100);
        line.ConvertedStock.Should().Be(10, "100 шт = 10 упаковок по 10");
        line.SalePrice.Should().Be(150, "цена упаковки = 15 × 10 (штучная цена 15 остаётся у упаковки «штука»)");
        PurchaseReceivingService.PackPieceSalePrice(line).Should().Be(15);
        line.PackConvertExpected = 90; // свежая карточка показала другой остаток
        line.ConvertedStock.Should().Be(9);
        ReceivingPacks.PackSaleConfirmMessage(new[] { line }).Should().Contain("было: 90 шт → станет: 9 упак. по 10 шт");

        line.PackConvertExpected = 105;
        line.ConvertedStock.Should().BeNull("105 ÷ 10 — не целое");
        PurchaseReceivingService.Validate(new[] { line }).Should().NotContain(p => p.Contains("не делится"), "заранее не блокируем: окно предложит «Округлить»");
        line.PackConvertAllowRounding = true;
        ReceivingPacks.PackSaleConfirmMessage(new[] { line }).Should().Contain("станет: ≈11 упак.");
    }

    [Fact]
    public void Zero_stock_is_converted_through_the_same_call_with_expected_zero()
    {
        var line = Line(stockSeen: 0);
        line.PackSaleConvertsExisting.Should().BeTrue();
        line.ConvertedStock.Should().Be(0);
        ReceivingPackConvert.BuildBody(line, "k")["expected_quantity"].Should().Be(0.0);
    }

    // ------------------------------------------------------------------ отказы

    [Fact]
    public async Task Stock_changed_stops_the_line_with_the_new_stock_and_no_receipt()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 409, "{\"code\":\"stock_changed\",\"current_quantity\":95,\"detail\":\"Остаток изменился\"}");
        using var guard = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, Supplier, true, "тест");

        result.PostedLines.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.Contains("остаток изменился (стало 95)") && e.Contains("обновите и повторите"));
        Calls(server, "POST", "/receipt/").Should().BeEmpty("упаковки к остатку в штуках не прибавили — двойного/ошибочного прихода нет");
    }

    [Fact]
    public async Task Not_divisible_without_consent_stops_the_line_and_suggests_rounding()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 400, "{\"error\":\"not_divisible\",\"detail\":\"Остаток не делится\"}");
        using var guard = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line(stockSeen: 105) }, Supplier, true, "тест");

        result.PostedLines.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.Contains("не делится") && e.Contains("Округлить"));
        Calls(server, "POST", "/receipt/").Should().BeEmpty();
    }

    [Fact]
    public async Task Rounding_is_sent_only_with_explicit_consent_and_the_preview_shows_the_change()
    {
        ReceivingPackConvert.RoundingPreview(105, 10).Should().Be((10.5, 11.0, 5.0));
        ReceivingPackConvert.RoundingPreview(104, 10).Should().Be((10.4, 10.0, -4.0));
        var line = Line(stockSeen: 105);
        ReceivingPackConvert.RoundingQuestion(line).Should().Contain("105 шт").And.Contain("станет 11 упак.").And.Contain("+5 шт");

        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 200, "{\"id\":\"pa\",\"quantity\":11,\"price\":\"150.00\"}");
        using var guard = await UseApiAsync(server);
        line.PackConvertAllowRounding = true; // кассир нажал «Округлить»

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        var body = (JsonObject)JsonNode.Parse(Calls(server, "POST", "convert-to-packages").Single().RequestMessage.Body!)!;
        body["allow_rounding"]!.GetValue<bool>().Should().BeTrue();
        body["expected_quantity"]!.GetValue<double>().Should().Be(105);
    }

    [Fact]
    public async Task Already_has_packages_of_the_same_size_counts_as_converted_and_receives_once()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, card: null);
        server.Json("POST", ConvertPath, 409, "{\"code\":\"already_has_packages\"}");
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"150.00\",\"packages\":[{\"id\":\"k1\",\"name\":\"штука\",\"quantity_in_package\":10,\"piece_unit_price\":\"15.00\"}]}");
        using var guard = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        Calls(server, "POST", "/receipt/").Should().ContainSingle();
        server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Body!.Contains("packages_input"));
    }

    [Fact]
    public async Task Already_has_packages_of_another_size_stops_the_line()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, card: null);
        server.Json("POST", ConvertPath, 409, "{\"code\":\"already_has_packages\"}");
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":5,\"price\":\"300.00\",\"packages\":[{\"id\":\"k1\",\"name\":\"блок\",\"quantity_in_package\":20,\"piece_unit_price\":\"15.00\"}]}");
        using var guard = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, Supplier, true, "тест");

        result.PostedLines.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.Contains("уже есть упаковка"));
        Calls(server, "POST", "/receipt/").Should().BeEmpty();
    }

    [Fact]
    public void Already_has_packages_on_the_confirmation_step_recalculates_the_line_into_the_existing_pack()
    {
        var line = Line();
        line.SetPackCount(4); // 4 упаковки по 10 = 40 шт
        line.Quantity.Should().Be(4);

        line.AdoptCardPack(20).Should().BeTrue("40 шт = 2 упаковки по 20");
        line.Quantity.Should().Be(2);
        line.PiecesPerPack.Should().Be(20);
        line.CardPackSize.Should().Be(20);
        line.PurchasePrice.Should().Be(200, "закупка упаковки: 100 ÷ 10 × 20");
        line.SalePrice.Should().Be(300);
        line.PackSaleConvertsExisting.Should().BeFalse("упаковка уже заведена — переводить нечего, приход в её упаковках");
        line.PackLinkText.Should().Contain("упак. + поштучно");
        ReceivingPackConvert.AdoptedText(line, 20).Should().Contain("упак. = 20 шт");

        var odd = Line();
        odd.SetPackCount(3); // 30 шт на упаковки по 20 не делится
        odd.AdoptCardPack(20).Should().BeFalse();
        odd.Quantity.Should().Be(3, "ничего не поменялось");
        ReceivingPackConvert.CannotAdoptText(odd, 20).Should().Contain("упак. = 20 шт");
    }

    [Theory]
    [InlineData(409, "{\"code\":\"weight_product_not_supported\"}", ReceivingPackConvert.Kind.WeightNotSupported, "весовой товар")]
    [InlineData(409, "{\"code\":\"variants_not_supported\"}", ReceivingPackConvert.Kind.VariantsNotSupported, "размеры")]
    [InlineData(409, "{\"detail\":{\"code\":\"has_promotion_tiers\"}}", ReceivingPackConvert.Kind.HasPromotionTiers, "цены от количества")]
    [InlineData(409, "{\"error\":{\"code\":\"product_in_open_cart\"}}", ReceivingPackConvert.Kind.InOpenCart, "открытом чеке")]
    [InlineData(400, "{\"code\":\"price_overflow\"}", ReceivingPackConvert.Kind.PriceOverflow, "предела сервера")]
    [InlineData(409, "{\"code\":\"negative_stock\"}", ReceivingPackConvert.Kind.NegativeStock, "в минусе")] // 2026-11-11 (сводка Умара)
    [InlineData(409, "{\"detail\":\"Что-то иное\"}", ReceivingPackConvert.Kind.Other, "сервер не перевёл")]
    public void Refusals_are_parsed_into_clear_reasons(int status, string payload, ReceivingPackConvert.Kind kind, string text)
    {
        var ex = new ApiException("ошибка сервера", status, JsonDocument.Parse(payload).RootElement.Clone());
        var outcome = ReceivingPackConvert.ParseRefusal(ex);
        outcome.Kind.Should().Be(kind);
        ReceivingPackConvert.Describe(outcome, Line()).Should().Contain(text).And.Contain("Строка не проведена");
    }

    /// <summary>2026-11-11 (сводка Умара): negative_stock — понятная причина, остаток из ответа (если прислан), строка не проводится.</summary>
    [Fact]
    public async Task Negative_stock_stops_the_line_with_clear_reason()
    {
        var o = ReceivingPackConvert.ParseRefusal(new ApiException("409", 409, JsonDocument.Parse("{\"code\":\"negative_stock\",\"current_quantity\":-3}").RootElement.Clone()));
        o.Kind.Should().Be(ReceivingPackConvert.Kind.NegativeStock);
        ReceivingPackConvert.Describe(o, Line()).Should().Contain("(-3)").And.Contain("ревизия"); // 5 языков проверяет check-tr (язык кассы здесь не переключаем — общий для параллельных тестов)

        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 409, "{\"code\":\"negative_stock\"}");
        using var guard = await UseApiAsync(server);
        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, Supplier, true, "тест");
        result.PostedLines.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.Contains("в минусе") && !e.Contains("(?)"));
        Calls(server, "POST", "/receipt/").Should().BeEmpty();
    }

    /// <summary>2026-11-11 (сводка Умара): по умолчанию unit «упак» и package_name «Штука»; название упаковки из строки — его.</summary>
    [Fact]
    public void Body_defaults_unit_pack_and_package_name_piece()
    {
        var line = Line();
        line.PackName = "";
        var body = ReceivingPackConvert.BuildBody(line, "k");
        body["unit"].Should().Be("упак");
        body["package_name"].Should().Be("Штука");
        line.PackName = "Пачка";
        ReceivingPackConvert.BuildBody(line, "k")["package_name"].Should().Be("Пачка");
    }

    [Fact]
    public void Stock_changed_reads_current_quantity_from_nested_detail()
    {
        var ex = new ApiException("409", 409, JsonDocument.Parse("{\"detail\":{\"code\":\"stock_changed\",\"current_quantity\":\"97\"}}").RootElement.Clone());
        var o = ReceivingPackConvert.ParseRefusal(ex);
        o.Kind.Should().Be(ReceivingPackConvert.Kind.StockChanged);
        o.CurrentQuantity.Should().Be(97);
    }

    [Fact]
    public async Task Product_in_open_cart_stops_the_line_and_other_lines_still_post()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 409, "{\"code\":\"product_in_open_cart\"}");
        using var guard = await UseApiAsync(server);
        var plain = new ReceivingLineVm { ProductName = "Хлеб", Barcode = "4600000000028", StockBefore = 3, Quantity = 2 };
        plain.ProductId = "pb";
        plain.Source = ReceivingSource.Warehouse;
        plain.PurchasePrice = 20;
        plain.SalePrice = 25;

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line(), plain }, Supplier, true, "тест");

        result.PostedLines.Should().ContainSingle().Which.ProductId.Should().Be("pb");
        result.Errors.Should().ContainSingle(e => e.Contains("открытом чеке"));
        var items = ((JsonObject)JsonNode.Parse(Calls(server, "POST", "/receipt/").Single().RequestMessage.Body!)!)["items"]!.AsArray();
        items.Should().ContainSingle("строка с отказом в приход не ушла");
    }

    // ------------------------------------------------------------------ повтор и откат

    [Fact]
    public async Task Repeat_after_a_lost_answer_uses_the_same_idempotency_key()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"150.00\"}");
        using var guard = await UseApiAsync(server);
        var line = Line(key: "same-key");

        await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");
        var again = JobLine.From(line).ToVm(); // очередь фоновых приходов: строка из файла задачи
        again.PackConvertKey.Should().Be("same-key");
        again.PackConvertExpected.Should().Be(100);
        await PurchaseReceivingService.Instance.PostAsync(new[] { again }, Supplier, true, "тест");

        var keys = Calls(server, "POST", "convert-to-packages").Select(e => Header(e, "Idempotency-Key")).ToList();
        keys.Should().HaveCount(2).And.OnlyContain(k => k == "same-key", "повтор после обрыва — тот же ключ: сервер не переведёт второй раз");
    }

    [Fact]
    public async Task Line_without_a_key_gets_one_and_keeps_it_for_the_retry()
    {
        using var server = new FakeNurCrmServer();
        Stub(server);
        server.Json("POST", ConvertPath, 200, "{\"id\":\"pa\",\"quantity\":10}");
        using var guard = await UseApiAsync(server);
        var line = Line();
        line.PackConvertKey = null;

        await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        line.PackConvertKey.Should().NotBeNullOrWhiteSpace();
        Header(Calls(server, "POST", "convert-to-packages").Single(), "Idempotency-Key").Should().Be(line.PackConvertKey);
    }

    [Fact]
    public async Task Old_server_without_the_endpoint_falls_back_to_the_patch_with_a_warning()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, card: null); // адреса convert-to-packages нет — заглушка отвечает 404
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":100,\"price\":\"15.00\",\"purchase_price\":\"10.00\",\"packages\":[]}");
        using var guard = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        result.Errors.Should().ContainSingle(e => e.Contains("прежний способ"));
        var patch = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Body!.Contains("packages_input"));
        ((JsonObject)JsonNode.Parse(patch.RequestMessage.Body!)!)["quantity"]!.GetValue<double>().Should().Be(10, "прежний путь: 100 шт → 10 упаковок");
        Calls(server, "POST", "/receipt/").Should().ContainSingle();
    }

    [Fact]
    public void Queue_job_line_keeps_the_confirmed_stock_rounding_and_key()
    {
        var line = Line(stockSeen: 105, key: "k-9");
        line.PackConvertAllowRounding = true;
        var json = JsonSerializer.Serialize(JobLine.From(line));
        var back = JsonSerializer.Deserialize<JobLine>(json)!.ToVm();
        back.PackConvertExpected.Should().Be(105);
        back.PackConvertAllowRounding.Should().BeTrue();
        back.PackConvertKey.Should().Be("k-9");
        JsonSerializer.Serialize(JobLine.From(new ReceivingLineVm { ProductName = "x", Quantity = 1 })).Should().NotContain("PackConvert", "у обычных строк файл задачи прежний");
    }
}
