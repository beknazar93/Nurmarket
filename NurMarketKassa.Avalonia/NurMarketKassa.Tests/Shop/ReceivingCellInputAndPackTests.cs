using System.ComponentModel;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-10 (жалоба владельца: «Удобства не удобно! Количество 1 не удаляется!! Генерация штрихкода как в вебе! В приёмке как в вебе — как упаковка, продажа поштучно!»).
/// Модель строки приёмки: ввод в ячейке (пустое поле во время набора, прежнее значение при выходе с пустым полем, запятая/точка, Esc), генерация штрихкода EAN-13 новому товару,
/// упаковка с поштучной продажей у ТОВАРА СКЛАДА (цены упаковки из штучных, пересчёт остатка, подтверждение «Было → станет», запрет при неделящемся остатке).</summary>
public sealed class ReceivingCellInputTests
{
    private static ReceivingLineVm Line(double qty = 1) => new() { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = qty, PurchasePrice = 25, SalePrice = 30 };

    [Fact]
    public void Slow_scanner_typing_a_barcode_into_a_cell_rolls_back_the_partial_number()
    {
        // 2026-11-11 (стык Б-4 и ввода в ячейках): медленный сканер печатает «46000001» по цифре — «4600000» успевало стать количеством/ценой 4 600 000.
        // Окно («Склад → Приёмка», CellEditEnded) забирает код и откатывает правку ячейки (CancelCellEdit) — прежние значения возвращаются.
        var l = Line(3);
        l.BeginCellEdit();
        foreach (var prefix in new[] { "4", "46", "460", "4600", "46000", "460000", "4600000", "46000001" })
            l.QtyInputText = prefix;
        l.Quantity.Should().Be(4_600_000, "до 8-й цифры это ещё число — так и было");
        l.TakeRejectedBarcode().Should().Be("46000001");
        l.CancelCellEdit();
        l.Quantity.Should().Be(3, "код в ячейке — правка откатывается, 4 600 000 не остаётся");

        l.BeginCellEdit();
        foreach (var prefix in new[] { "4", "46", "460", "4600", "46000", "460000", "4600000", "46000001" })
            l.PurchaseInputText = prefix;
        l.TakeRejectedBarcode().Should().Be("46000001");
        l.CancelCellEdit();
        l.PurchasePrice.Should().Be(25, "цена закупки тоже возвращается");
    }

    [Fact]
    public void Pack_fields_do_not_touch_quantity_or_prices_of_a_line_in_pieces()
    {
        // 2026-11-11 (регрессия «Принимаем 0 у всех строк» в «Закупки → Приём товара»): скрытое пустое поле «шт в упак.» вызывало SetPiecesPerPack(0) → количество 0.
        var l = Line(5);
        l.SetPiecesPerPack(0);
        l.Quantity.Should().Be(5, "строка в штуках — «шт в упак.» количество не меняет");
        l.SetPiecesPerPack(24);
        l.Quantity.Should().Be(5);
        l.PurchasePrice.Should().Be(25);
        l.SetPackCount(0);
        l.Quantity.Should().Be(5, "«упаковок» без режима упаковок количество не меняет");
        l.SetPackMode(true);
        l.PiecesPerPack.Should().Be(24, "размер, введённый до включения, запомнен");
        l.Quantity.Should().Be(5, "5 шт остаются 5 шт при включении режима");
    }

    [Fact]
    public void Clearing_quantity_keeps_the_field_empty_while_typing_and_never_pushes_a_number_back()
    {
        var l = Line();
        var shown = new List<string>();
        l.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReceivingLineVm.QtyInputText) or nameof(ReceivingLineVm.QuantityText))
                shown.Add(l.QtyInputText);
        };
        l.BeginCellEdit();

        l.QtyInputText = ""; // Backspace по выделенной «1»

        l.QtyInputText.Should().Be("", "пока набирают, поле пустое — «1» не возвращается");
        shown.Should().OnlyContain(s => s == "", "таблица ни разу не получила «0» или «1» вместо пустого поля");
        l.QtyInputText = "7";
        l.Quantity.Should().Be(7, "набор поверх стёртого числа");
        l.EndCellEdit().Should().BeFalse("поле не пустое — возвращать нечего");
        l.QtyInputText.Should().Be("7");
    }

    [Fact]
    public void Leaving_an_empty_cell_restores_the_previous_value_of_that_field()
    {
        var l = Line(3);
        l.BeginCellEdit();
        l.QtyInputText = "";
        l.Quantity.Should().Be(0);

        l.EndCellEdit().Should().BeTrue("поле оставили пустым — вернули прежнее");

        l.Quantity.Should().Be(3);
        l.QtyInputText.Should().Be("3");

        l.BeginCellEdit();
        l.PurchaseInputText = "";
        l.EndCellEdit().Should().BeTrue();
        l.PurchasePrice.Should().Be(25, "пустая закупка при выходе — прежняя");

        l.BeginCellEdit();
        l.SalePriceText = "";
        l.EndCellEdit().Should().BeTrue();
        l.SalePrice.Should().Be(30, "пустая цена продажи при выходе — прежняя");
    }

    [Theory]
    [InlineData("2,5")]
    [InlineData("2.5")]
    public void Comma_and_dot_are_both_decimal_separators(string typed)
    {
        var l = new ReceivingLineVm { ProductName = "Сахар", Source = ReceivingSource.Unknown, Unit = "кг", Quantity = 1 };
        l.BeginCellEdit();
        l.QtyInputText = typed;
        l.EndCellEdit();
        l.Quantity.Should().Be(2.5);

        l.BeginCellEdit();
        l.PurchaseInputText = typed.Replace("2", "100");
        l.EndCellEdit();
        l.PurchasePrice.Should().Be(100.5);
    }

    [Fact]
    public void Escape_rolls_back_the_whole_cell_edit()
    {
        var l = Line(4);
        l.BeginCellEdit();
        l.QtyInputText = "9";
        l.PurchaseInputText = "99";

        l.CancelCellEdit();

        l.Quantity.Should().Be(4);
        l.PurchasePrice.Should().Be(25);
        l.QtyInputText.Should().Be("4");
        l.InCellEdit.Should().BeFalse();
    }

    [Fact]
    public void Pack_mode_fields_follow_the_same_rules()
    {
        var l = Line();
        l.SetPackMode(true, 24);
        l.SetPackCount(5);
        l.BeginCellEdit();
        l.QtyInputText = "";
        l.PiecesInputText = "";
        l.QtyInputText.Should().Be("", "в режиме упаковок поле «упак.» тоже остаётся пустым во время набора");

        l.EndCellEdit().Should().BeTrue();

        l.PackCount.Should().Be(5);
        l.PiecesPerPack.Should().Be(24);
        l.Quantity.Should().Be(120);
    }

    [Fact]
    public void Simple_fields_window_restores_an_empty_field_from_the_snapshot()
    {
        var l = Line(6); // «Закупки → Приём товара»: поле пишет Quantity напрямую
        l.BeginCellEdit();
        l.Quantity = 0;
        l.RestoreEmptyField(ReceivingCellField.Quantity).Should().BeTrue();
        l.ForgetCellEdit();
        l.Quantity.Should().Be(6);
        l.RestoreEmptyField(ReceivingCellField.Quantity).Should().BeFalse("снимка больше нет");
    }

    [Fact]
    public void Name_tip_shows_the_full_name()
    {
        var l = new ReceivingLineVm { ProductName = "Реле зарядки Audi красное длинное название", Source = ReceivingSource.Unknown };
        l.NameTip.Should().Be("Реле зарядки Audi красное длинное название");
    }
}

/// <summary>2026-11-10: генерация штрихкода новому товару строки приёмки — тот же генератор, что в окне товара.</summary>
[Collection("Wave5Database")]
public sealed class ReceivingBarcodeGenerationTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();

    public ReceivingBarcodeGenerationTests() => CatalogCacheService.Products.Clear();

    public void Dispose()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    [Fact]
    public void Only_a_new_product_without_barcode_can_get_a_generated_one()
    {
        new ReceivingLineVm { Source = ReceivingSource.Unknown }.CanGenerateBarcode.Should().BeTrue("«+ Новый товар» без штрихкода");
        new ReceivingLineVm { Source = ReceivingSource.Internet }.CanGenerateBarcode.Should().BeTrue();
        new ReceivingLineVm { Source = ReceivingSource.Unknown, Barcode = "4600000000011" }.CanGenerateBarcode.Should().BeFalse("заводской штрихкод со сканера не заменяем");
        var existing = new ReceivingLineVm { Source = ReceivingSource.Warehouse };
        existing.ProductId = "p1";
        existing.CanGenerateBarcode.Should().BeFalse("у товара склада штрихкод правится в карточке");
        new ReceivingLineVm { Source = ReceivingSource.GlobalBase }.CanGenerateBarcode.Should().BeFalse();
    }

    [Fact]
    public void Generated_barcodes_are_valid_ean13_like_the_product_window_and_unique_among_catalog_and_lines()
    {
        CatalogCacheService.Products.Add(CatalogTestHelper.CreateTile("c1", "Есть в каталоге", "4870000000014", 3));
        var lines = Enumerable.Range(0, 300).Select(i => new ReceivingLineVm { ProductName = "Новый " + i, Source = ReceivingSource.Unknown }).ToList();
        lines.Add(new ReceivingLineVm { ProductName = "Со сканера", Source = ReceivingSource.Unknown, Barcode = "4600000000011" });

        foreach (var line in lines.Where(l => l.CanGenerateBarcode))
            BarcodeGenerator.GenerateForReceivingLine(line, lines).Should().BeTrue();

        var codes = lines.Select(l => l.Barcode).ToList();
        codes.Should().OnlyHaveUniqueItems("дублей среди строк приёмки нет");
        codes.Should().NotContain("4870000000014", "код каталога не повторяется");
        foreach (var code in codes.Take(300))
        {
            code.Should().HaveLength(13);
            BarcodeGenerator.HasValidCheckDigit(code).Should().BeTrue("контрольная цифра EAN-13");
            code[0].Should().NotBe('0', "как в окне товара: не с 0");
            code[0].Should().NotBe('2', "не с 2 — префиксы 20–29 касса читает как весовые");
        }
        lines.Last().Barcode.Should().Be("4600000000011", "ручной/сканированный код не тронут");
        BarcodeGenerator.GenerateForReceivingLine(lines[0], lines).Should().BeFalse("штрихкод уже есть — второй раз не генерируем");
    }
}

/// <summary>2026-11-10: «упаковка с поштучной продажей» у товара склада — модель, проверки и проведение на заглушке сервера.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingExistingPackSaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-expack-" + Guid.NewGuid().ToString("N"));

    public ReceivingExistingPackSaleTests()
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

    /// <summary>Товар склада «Сигареты»: остаток 48 шт, закупка 25 за штуку, продажа 30 за штуку (в каталоге этого ПК его нет — упаковки в карточке нет).</summary>
    private static ReceivingLineVm Existing(double stock = 48)
    {
        var l = new ReceivingLineVm { ProductName = "Сигареты", Barcode = "4600000000011", StockBefore = stock, Quantity = 1, OriginalPurchasePrice = 25, OriginalSalePrice = 30 };
        l.ProductId = "pa";
        l.Source = ReceivingSource.Warehouse;
        l.PurchasePrice = 25;
        l.SalePrice = 30;
        l.EnableReceivingPricing();
        return l;
    }

    [Fact]
    public void Existing_product_switches_to_pack_sale_with_pack_prices_from_piece_prices_and_converted_stock()
    {
        var l = Existing();

        l.SetPackSale(true, 24).Should().BeTrue();

        l.PackSale.Should().BeTrue();
        l.Quantity.Should().Be(1, "одна отсканированная единица — одна упаковка");
        l.PurchasePrice.Should().Be(600, "закупка упаковки = 25 × 24");
        l.SalePrice.Should().Be(720, "продажа упаковки = 30 × 24");
        PurchaseReceivingService.PackPieceSalePrice(l).Should().Be(30, "штука в продаже — по прежней цене");
        l.PurchaseChangeText.Should().Be("без изменений", "сравнение с прошлой закупкой — тоже за упаковку (25 × 24)");
        l.PackSaleConvertsExisting.Should().BeTrue();
        l.ConvertedStock.Should().Be(2, "48 шт = 2 упаковки по 24");
        PurchaseReceivingService.Validate(new[] { l }).Should().BeEmpty();

        l.SetPiecesPerPack(12);
        l.PurchasePrice.Should().Be(300, "цены следуют за «шт в упак.», пока их не ввели руками");
        l.ConvertedStock.Should().Be(4);
        l.SetPackPriceText("280");
        l.SetPiecesPerPack(24);
        l.PurchasePrice.Should().Be(280, "цену упаковки ввели руками — больше не пересчитывается");

        var text = ReceivingPacks.PackSaleConfirmMessage(new[] { l });
        text.Should().Contain("было: 48 шт → станет: 2 упак. по 24 шт").And.Contain("приход: +1 упак.").And.Contain("продажа поштучно: цена штуки 30 сом");

        l.SetPackSale(false);
        l.PackSale.Should().BeFalse();
        l.PurchasePrice.Should().BeApproximately(280.0 / 24, 0.0001, "обратно в штуки — цена за штуку");
    }

    [Fact]
    public void Stock_that_does_not_divide_into_whole_packs_blocks_posting()
    {
        var l = Existing(stock: 50);
        l.SetPackSale(true, 24);

        l.ConvertedStock.Should().BeNull("50 ÷ 24 — не целое: округление исказило бы склад");
        // 2026-11-11 (convert-to-packages): заранее не блокируем — перед подтверждением окно читает свежий остаток и предлагает «Округлить» только по согласию;
        // без согласия сервер ответит not_divisible (ReceivingConvertToPackagesTests).
        PurchaseReceivingService.Validate(new[] { l }).Should().NotContain(p => p.Contains("не делится"));
        ReceivingPacks.TryConvertStock(0, 0, 24, out var zero).Should().BeTrue();
        zero.Should().Be(0, "нулевой остаток — пересчитывать нечего");
        ReceivingPacks.TryConvertStock(5, 10, 25, out var repacked).Should().BeTrue("5 упаковок по 10 = 50 шт = 2 по 25");
        repacked.Should().Be(2);
    }

    [Fact]
    public void Queue_restores_an_existing_pack_sale_line_without_scaling_prices_again()
    {
        var l = Existing();
        l.SetPackSale(true, 24);
        var back = JobLine.From(l).ToVm();
        back.PackSale.Should().BeTrue();
        back.PurchasePrice.Should().Be(600, "в очереди цены уже за упаковку — второй раз ×24 не умножаем");
        back.SalePrice.Should().Be(720);
    }

    // ------------------------------------------------------------------ проведение на заглушке

    private static void Stub(FakeNurCrmServer s, string cardJson)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("GET", "/api/main/products/pa/", 200, cardJson);
        s.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"price\":\"720.00\",\"purchase_price\":\"600.00\"}");
        s.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"600.00\"}");
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

    private static readonly PurchaseReceivingService.Supplier Supplier = new("s1", "Поставщик", null, null);

    [Fact]
    public async Task Posting_writes_the_pack_and_converted_stock_to_the_card_first_then_receives_packs()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, "{\"id\":\"pa\",\"quantity\":48,\"price\":\"30.00\",\"purchase_price\":\"25.00\",\"packages\":[]}");
        using var guard = await UseApiAsync(server);
        var line = Existing();
        line.SetPackSale(true, 24);
        line.PackName = "Пачка";

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        var entries = server.Server.LogEntries.ToList();
        var packPatch = entries.First(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Body!.Contains("packages_input"));
        var body = (JsonObject)JsonNode.Parse(packPatch.RequestMessage.Body!)!;
        var pack = body["packages_input"]!.AsArray().Single()!.AsObject();
        pack["quantity_in_package"]!.GetValue<double>().Should().Be(24, "тот же контракт packages_input, что окно товара");
        pack["name"]!.GetValue<string>().Should().Be("Пачка");
        double.Parse(pack["piece_unit_price"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(30);
        body["quantity"]!.GetValue<double>().Should().Be(2, "48 шт → 2 упаковки: склад не искажается");
        body["price"]!.GetValue<double>().Should().Be(720, "цена товара — цена упаковки");

        var receipt = entries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/"));
        entries.IndexOf(packPatch).Should().BeLessThan(entries.IndexOf(receipt), "сначала карточка в упаковках, потом приход упаковок");
        var item = ((JsonObject)JsonNode.Parse(receipt.RequestMessage.Body!)!)["items"]!.AsArray()[0]!.AsObject();
        item["qty"]!.GetValue<double>().Should().Be(1, "приход — в упаковках");
        item["purchase_price"]!.GetValue<double>().Should().Be(600, "по цене упаковки");
    }

    [Fact]
    public async Task Server_stock_that_does_not_divide_stops_the_line_without_touching_the_card()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, "{\"id\":\"pa\",\"quantity\":50,\"price\":\"30.00\",\"purchase_price\":\"25.00\",\"packages\":[]}");
        using var guard = await UseApiAsync(server);
        var line = Existing(); // в приёмке видели 48, а на сервере уже 50
        line.SetPackSale(true, 24);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        result.PostedLines.Should().BeEmpty();
        result.Errors.Should().Contain(e => e.Contains("не делится"));
        server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "PATCH", "карточку не трогали");
        server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/receipt/"), "упаковки к остатку в штуках не прибавили");
    }

    [Fact]
    public async Task Repeat_after_the_pack_already_reached_the_card_does_not_convert_twice()
    {
        using var server = new FakeNurCrmServer();
        Stub(server, "{\"id\":\"pa\",\"quantity\":2,\"price\":\"720.00\",\"purchase_price\":\"600.00\",\"packages\":[{\"id\":\"k1\",\"name\":\"упаковка\",\"quantity_in_package\":24,\"piece_unit_price\":\"30.00\"}]}");
        using var guard = await UseApiAsync(server);
        var line = Existing();
        line.SetPackSale(true, 24);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, true, "тест");

        result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
        server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Body!.Contains("packages_input"), "упаковка уже есть — остаток второй раз не пересчитываем");
    }
}

/// <summary>2026-11-10: товар, у которого упаковка уже в карточке, — «Изменить штук в упаковке» (остаток в упаковках пересчитывается в новые).</summary>
[Collection("Wave5Database")]
public sealed class ReceivingCardPackChangeTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();

    public ReceivingCardPackChangeTests() => CatalogCacheService.Products.Clear();

    public void Dispose()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    [Fact]
    public void Changing_pieces_per_pack_of_a_card_pack_recalculates_stock_and_leaving_keeps_numbers()
    {
        var tile = CatalogTestHelper.CreateTile("po1", "Сигареты", "46001", 5);
        tile.PieceOption = new ProductPackageOption { Id = "pk1", Name = "упаковка", QuantityInPackage = 10, PieceUnitPrice = 3 };
        CatalogCacheService.Products.Add(tile);
        var l = new ReceivingLineVm { ProductName = "Сигареты", StockBefore = 5, Quantity = 2 };
        l.ProductId = "po1";
        l.Source = ReceivingSource.Warehouse;
        l.PurchasePrice = 25;
        l.SalePrice = 30;
        l.CardPackSize.Should().Be(10);
        l.PackLinkClickable.Should().BeTrue("подпись «упак. = 10 шт» теперь открывает «Изменить штук в упаковке»");

        l.SetPackSale(true, l.CardPackSize).Should().BeTrue();
        l.PackSaleConvertsExisting.Should().BeFalse("размер тот же — карточку не меняем");
        l.PurchasePrice.Should().Be(25, "цены уже за упаковку — не умножаются");
        l.SetPiecesPerPack(25);
        l.PackSaleConvertsExisting.Should().BeTrue();
        l.ConvertedStock.Should().Be(2, "5 упаковок по 10 = 50 шт = 2 по 25");
        ReceivingPacks.PackSaleConfirmMessage(new[] { l }).Should().Contain("упаковка по 10 шт, остаток 5 упак.").And.Contain("станет: 2 упак. по 25 шт");

        l.SetPackSale(false);
        l.Quantity.Should().Be(2, "у товара с упаковкой в карточке количество уже в упаковках — обратно не умножаем");
        l.PurchasePrice.Should().Be(25);
    }
}
