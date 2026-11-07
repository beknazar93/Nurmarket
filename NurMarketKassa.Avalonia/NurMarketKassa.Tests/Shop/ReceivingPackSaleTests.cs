using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-07 (владелец «Да» + «типа поштучной продажи»): в приёмке — два способа «упаковками»: (а) «в штуки» (ReceivingByPacksTests) и (б) «упаковка с поштучной продажей»:
/// на склад идут УПАКОВКИ, в карточку НОВОГО товара при создании пишется упаковка (packages_input, как «Поштучная продажа из упаковки» в окне товара). У товара с остатком (и у существующего вообще) (б) недоступно.
/// Те же строки читает «Склад → Приёмка» (поля QtyInputText / PiecesInputText / PurchaseInputText) — расчёты общие.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingPackSaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-packsale-" + Guid.NewGuid().ToString("N"));

    public ReceivingPackSaleTests()
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

    /// <summary>Новый товар (как «+ Новый товар» / неизвестный штрихкод): 5 упаковок по 10 шт, упаковка закупается за 500, продаётся за 600 (штука — 60).</summary>
    private static ReceivingLineVm NewLine(double packs = 5, double perPack = 10, string packPrice = "500", double sale = 600)
    {
        var l = new ReceivingLineVm { ProductName = "Сигареты", Barcode = "4602000000017", Source = ReceivingSource.Unknown, Quantity = 1 };
        l.SetPackSale(true).Should().BeTrue();
        l.SetPackCount(packs);
        l.SetPiecesPerPack(perPack);
        l.SetPackPriceText(packPrice);
        l.SalePrice = sale;
        return l;
    }

    private static ReceivingLineVm ExistingLine(double stock = 7)
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Barcode = "4600000000011", StockBefore = stock, Quantity = 1 };
        l.ProductId = "pa";
        l.Source = ReceivingSource.Warehouse;
        l.PurchasePrice = 25;
        l.SalePrice = 30;
        return l;
    }

    // ------------------------------------------------------------------ расчёт: остаток в упаковках

    [Fact]
    public void Pack_sale_keeps_packs_and_pack_price_as_the_main_values_and_shows_pieces_only_as_a_note()
    {
        var l = NewLine();

        l.PackSale.Should().BeTrue();
        l.PackSaleSize.Should().Be(10);
        l.Quantity.Should().Be(5, "на склад — УПАКОВКИ, не штуки");
        l.PurchasePrice.Should().Be(500, "цена закупки — за упаковку");
        l.LineTotal.Should().Be(2500);
        l.PackNote.Should().StartWith("5 × 10 = 50 шт", "подпись для глаз; склад получит 5");
        l.PackPriceNote.Should().Contain("50", "за штуку 500 ÷ 10 = 50 — только в подписи");
        l.PackLossText.Should().BeEmpty("остаток в упаковках — округлять нечего");
        PurchaseReceivingService.PackPieceSalePrice(l).Should().Be(60, "цена штуки = цена продажи упаковки 600 ÷ 10");
        PurchaseReceivingService.Validate(new[] { l }).Should().BeEmpty();
    }

    [Fact]
    public void Pack_sale_is_blocked_for_existing_products_with_stock_and_for_weight_goods()
    {
        var existing = ExistingLine(stock: 7);
        existing.CanPackSale.Should().BeFalse("у товара с остатком в штуках смена единицы остатка исказит склад");
        existing.SetPackSale(true).Should().BeFalse();
        existing.PackSale.Should().BeFalse();
        existing.PackMode.Should().BeFalse("ничего не изменилось");
        existing.Quantity.Should().Be(1);
        existing.PurchasePrice.Should().Be(25);

        var fromBase = new ReceivingLineVm { ProductName = "Из базы", Source = ReceivingSource.GlobalBase, Quantity = 1 };
        fromBase.SetPackSale(true).Should().BeFalse("товар общей базы создаётся другим запросом без упаковки");

        var weight = new ReceivingLineVm { ProductName = "Сахар", Source = ReceivingSource.Unknown, Quantity = 1, IsWeight = true };
        weight.SetPackSale(true).Should().BeFalse("весовой товар — без поштучной продажи");

        ReceivingLineVm.PackSaleBlockedText.Should().Contain("НОВОГО");
    }

    [Fact]
    public void Validate_blocks_pack_sale_without_size_with_fractional_size_and_without_sale_price()
    {
        var noSize = new ReceivingLineVm { ProductName = "Сигареты", Source = ReceivingSource.Unknown, Quantity = 1 };
        noSize.SetPackSale(true);
        noSize.SetPackPriceText("500");
        noSize.SalePrice = 600;
        PurchaseReceivingService.Validate(new[] { noSize }).Should().Contain(p => p.Contains("упаков"), "«укажите, сколько штук в упаковке»");

        var one = NewLine(perPack: 1);
        PurchaseReceivingService.Validate(new[] { one }).Should().Contain(p => p.Contains("от 2"), "как в карточке товара: штук в упаковке — целое от 2");
        var frac = NewLine(perPack: 2.5);
        PurchaseReceivingService.Validate(new[] { frac }).Should().Contain(p => p.Contains("от 2"));

        var noSale = NewLine(sale: 0);
        PurchaseReceivingService.Validate(new[] { noSale }, requireSalePrice: false).Should().Contain(p => p.Contains("цену продажи упаковки"), "цена штуки считается от неё — без неё упаковку не заводим");
    }

    // ------------------------------------------------------------------ переходы между способами

    [Fact]
    public void Switching_from_pieces_by_packs_to_pack_sale_and_back_converts_quantity_and_prices_both_ways()
    {
        var l = new ReceivingLineVm { ProductName = "Сигареты", Barcode = "4602", Source = ReceivingSource.Unknown, Quantity = 1 };
        l.SetPackMode(true);
        l.SetPackCount(5);
        l.SetPiecesPerPack(24);
        l.SetPackPriceText("600"); // 5 упаковок по 24 шт → 120 шт по 25
        l.SalePrice = 30; // за штуку
        l.Quantity.Should().Be(120);

        l.SetPackSale(true).Should().BeTrue();
        l.PackSale.Should().BeTrue();
        l.Quantity.Should().Be(5, "из «упаковок → штуки» в «поштучную продажу»: количество — упаковки");
        l.PurchasePrice.Should().Be(600, "закупка — цена упаковки");
        l.SalePrice.Should().Be(720, "цена продажи ШТУКИ 30 × 24 → цена продажи УПАКОВКИ");
        l.PiecesPerPack.Should().Be(24);

        l.SetPackSale(false);
        l.PackMode.Should().BeFalse();
        l.Quantity.Should().Be(120, "обратно в штуки: упаковки × штук");
        l.PurchasePrice.Should().Be(25);
        l.SalePrice.Should().Be(30);

        // из ввода «упаковками → штуки» меню берёт SetPackMode(true): из поштучной продажи сначала возврат в штуки
        l.SetPackMode(true); // обратно в «упаковки → штуки»: 120 шт ÷ 24 = 5 упаковок
        l.SetPackSale(true);
        l.SetPackMode(true);
        l.PackSale.Should().BeFalse();
        l.PackMode.Should().BeTrue();
        l.Quantity.Should().Be(120);
        l.PackCount.Should().Be(5);
        l.PackPrice.Should().Be(600);
    }

    [Fact]
    public void Switching_to_pack_sale_from_plain_pieces_keeps_the_typed_numbers_as_packs()
    {
        var l = new ReceivingLineVm { ProductName = "Сигареты", Source = ReceivingSource.Unknown, Quantity = 3 };
        l.PurchasePrice = 480;
        l.SalePrice = 600;

        l.SetPackSale(true, suggestedPiecesPerPack: 10);

        l.Quantity.Should().Be(3, "три упаковки");
        l.PurchasePrice.Should().Be(480, "уже цена упаковки — вводят сразу за упаковку");
        l.SalePrice.Should().Be(600);
        l.PiecesPerPack.Should().Be(10, "подставлен предложенный размер");
    }

    // ------------------------------------------------------------------ поля таблицы Склада

    [Fact]
    public void Warehouse_grid_fields_read_and_write_packs_pieces_per_pack_and_pack_price_with_raw_text_kept_while_typing()
    {
        var l = new ReceivingLineVm { ProductName = "Сигареты", Source = ReceivingSource.Unknown, Quantity = 1 };
        l.QtyInputText.Should().Be("1", "вне режима упаковок «Принимаем» — штуки как раньше");
        l.PurchaseInputText = "12,5";
        l.PurchasePrice.Should().Be(12.5);

        l.SetPackSale(true);
        l.QtyInputText = "5,"; // пока набирают — разделитель не теряется
        l.QtyInputText.Should().Be("5,");
        l.Quantity.Should().Be(5);
        l.PiecesInputText = "24";
        l.PiecesPerPack.Should().Be(24);
        l.PurchaseInputText = "2400,5";
        l.PurchasePrice.Should().Be(2400.5);

        l.CommitText();
        l.QtyInputText.Should().Be("5", "по выходу из ячейки — нормальный показ");
        l.PurchaseInputText.Should().Be("2400.50");

        l.SetPackMode(false); // назад: поля читают обычные штуки
        l.QtyInputText.Should().Be(l.QuantityText);
        l.PurchaseInputText.Should().Be(l.PurchasePriceText);
    }

    [Fact]
    public void By_packs_to_pieces_mode_in_warehouse_grid_fields_gives_the_same_pieces_as_in_purchases()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 1 };
        l.SetPackMode(true);
        l.QtyInputText = "5";
        l.PiecesInputText = "24";
        l.PurchaseInputText = "600";

        l.Quantity.Should().Be(120);
        l.PurchasePrice.Should().Be(25);
        l.PackShortNote.Should().StartWith("× 24 = 120");
        l.PackLinkText.Should().Contain("упак.").And.Contain("штуки");
        l.PackNeedsSize.Should().BeFalse();
    }

    [Fact]
    public void Missing_size_in_by_packs_mode_blocks_posting_from_the_warehouse_grid_too()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Barcode = "46001", Source = ReceivingSource.Unknown, Quantity = 1 };
        l.PurchasePrice = 10;
        l.SalePrice = 15;
        l.SetPackMode(true);
        l.QtyInputText = "2";

        l.PackNeedsSize.Should().BeTrue();
        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains("упаков"));
    }

    // ------------------------------------------------------------------ очередь

    [Fact]
    public void Queue_job_line_keeps_pack_sale_size_and_restores_the_pack_sale_line()
    {
        var stored = JobLine.From(NewLine());

        stored.PackSaleSize.Should().Be(10);
        stored.Quantity.Should().Be(5, "в очереди — упаковки и цена упаковки");
        stored.PurchasePrice.Should().Be(500);

        var json = JsonSerializer.Serialize(stored);
        json.Should().Contain("PackSaleSize");
        var back = JsonSerializer.Deserialize<JobLine>(json)!.ToVm();
        back.PackSale.Should().BeTrue("возврат строки в приёмку / повтор задачи — снова упаковка с поштучной продажей");
        back.Quantity.Should().Be(5);
        back.PurchasePrice.Should().Be(500);
        back.SalePrice.Should().Be(600);
        back.PiecesPerPack.Should().Be(10);

        JsonSerializer.Serialize(JobLine.From(new ReceivingLineVm { ProductName = "Обычный", Quantity = 2 })).Should().NotContain("PackSaleSize", "обычные задачи в файле очереди не меняются");
    }

    [Fact]
    public void Pack_sale_confirmation_names_pack_size_stock_in_packs_and_piece_price()
    {
        var text = ReceivingPacks.PackSaleConfirmMessage(new[] { NewLine() });

        text.Should().Contain("по 10 шт").And.Contain("остаток — в упаковках").And.Contain("Продолжить?");
        text.Should().Contain("Сигареты").And.Contain("5 упак.").And.Contain("60");
    }

    // ------------------------------------------------------------------ проведение: создание товара с упаковкой

    private static void StubCreate(FakeNurCrmServer s, string supplierId)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("POST", "/api/main/products/create-manual/", 201, "{\"id\":\"n1\",\"name\":\"Сигареты\",\"quantity\":0,\"price\":\"600.00\",\"purchase_price\":\"500.00\",\"markup_percent\":\"20\"}");
        s.Json("POST", $"/api/main/suppliers/{supplierId}/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"2500.00\"}");
        s.Json("GET", "/api/main/products/n1/", 200, "{\"id\":\"n1\",\"quantity\":5,\"price\":\"600.00\",\"purchase_price\":\"500.00\",\"markup_percent\":\"20\"}");
        s.Json("PATCH", "/api/main/products/n1/", 200, "{\"id\":\"n1\",\"price\":\"600.00\",\"purchase_price\":\"500.00\"}");
    }

    private static async Task<(NurMarketApiClient Api, IDisposable Restore)> UseApiAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return (api, new Restorer(() => PosApp.CatalogApi = previous));
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    [Fact]
    public async Task Posting_creates_the_new_product_with_a_pack_and_receives_packs_at_pack_price()
    {
        using var server = new FakeNurCrmServer();
        StubCreate(server, "s1");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var line = NewLine();

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        var create = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("create-manual"));
        var body = (JsonObject)JsonNode.Parse(create.RequestMessage.Body!)!;
        var pack = body["packages_input"]!.AsArray().Single()!.AsObject();
        pack["quantity_in_package"]!.GetValue<double>().Should().Be(10, "в карточку — упаковка на 10 штук (тот же контракт, что в окне товара)");
        double.Parse(pack["piece_unit_price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(60, "цена штуки = 600 ÷ 10");
        double.Parse(body["price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(600, "цена товара — цена упаковки");
        double.Parse(body["purchase_price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(500);

        var receipt = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/"));
        var item = ((JsonObject)JsonNode.Parse(receipt.RequestMessage.Body!)!)["items"]!.AsArray()[0]!.AsObject();
        item["qty"]!.GetValue<double>().Should().Be(5, "на склад — упаковки, не штуки");
        item["purchase_price"]!.GetValue<double>().Should().Be(500, "цена упаковки");
    }

    [Fact]
    public async Task Plain_new_product_is_still_created_without_a_pack()
    {
        using var server = new FakeNurCrmServer();
        StubCreate(server, "s1");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var line = new ReceivingLineVm { ProductName = "Сигареты", Barcode = "4602000000017", Source = ReceivingSource.Unknown, Quantity = 5, PurchasePrice = 50, SalePrice = 60 };

        await PurchaseReceivingService.Instance.PostAsync(new[] { line }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), true, "тест");

        var create = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("create-manual"));
        var body = (JsonObject)JsonNode.Parse(create.RequestMessage.Body!)!;
        (body["packages_input"] is null || body["packages_input"]!.AsArray().Count == 0).Should().BeTrue("обычный новый товар — без упаковки, как раньше");
    }
}
