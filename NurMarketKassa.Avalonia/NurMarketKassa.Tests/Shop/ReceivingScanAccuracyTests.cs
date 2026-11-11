using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11 (владелец: «На приёмке криво работает определение штрихкодов. Многие товары, которые не находит, показывает как ВЕСОВОЙ. В массовом сканировании всё чётко»).
/// Приёмка ищет как массовое: точный штрихкод → склад на сервере (только товар с ЭТИМ штрихкодом) → общая база → этикетка весов только при настроенном префиксе весов → «Неизвестно».
/// И защита ячеек: штрихкод в цене/количестве/наценке не записывается, цена-великан не проводится.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingScanAccuracyTests
{
    /// <summary>13-значный код с верной контрольной цифрой EAN-13 (как настоящий штрихкод).</summary>
    private static string Ean13(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + ((10 - sum % 10) % 10);
    }

    [Fact]
    public void Code_starting_with_2x_is_not_a_scale_label_without_configured_scale_prefixes()
    {
        var savedW = WeightBarcodeParser.WeightPrefixes;
        var savedA = WeightBarcodeParser.AmountPrefixes;
        try
        {
            WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
            WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
            var code = Ean13("200012300456");

            ProductCodeLookup.ScalePrefixConfigured(code).Should().BeFalse("весы у магазина не настроены");
            ProductCodeLookup.FindWeightLabel(code).Should().BeNull("обычный штрихкод «20…» — не этикетка весов");

            WeightBarcodeParser.WeightPrefixes = new HashSet<string> { "20" };
            ProductCodeLookup.ScalePrefixConfigured(code).Should().BeTrue("префикс «20» настроен в Настройках → Весы");
            ProductCodeLookup.ScalePrefixConfigured(Ean13("210012300456")).Should().BeFalse("«21» не настроен");
        }
        finally
        {
            WeightBarcodeParser.WeightPrefixes = savedW;
            WeightBarcodeParser.AmountPrefixes = savedA;
        }
    }

    [Fact]
    public void Server_answer_is_accepted_only_for_the_scanned_barcode()
    {
        var forms = ProductCodeLookup.Candidates("4600000000011");
        System.Text.Json.JsonDocument.Parse("{\"id\":\"a\",\"barcode\":\"4600000000011\"}").RootElement.Let(p => PurchaseReceivingService.ServerAnswerHasCode(p, forms, true)).Should().BeTrue();
        System.Text.Json.JsonDocument.Parse("{\"id\":\"a\",\"barcode\":\"2000000000015\",\"alternate_barcodes\":[{\"barcode\":\"4600000000011\"}]}").RootElement
            .Let(p => PurchaseReceivingService.ServerAnswerHasCode(p, forms, true)).Should().BeTrue("доп. штрихкод товара");
        System.Text.Json.JsonDocument.Parse("{\"id\":\"w\",\"barcode\":\"\",\"is_weight\":true,\"plu\":45}").RootElement
            .Let(p => PurchaseReceivingService.ServerAnswerHasCode(p, forms, true)).Should().BeFalse("весовой товар без этого штрихкода — чужой");
        System.Text.Json.JsonDocument.Parse("{\"id\":\"x\",\"barcode\":\"4870000000014\"}").RootElement
            .Let(p => PurchaseReceivingService.ServerAnswerHasCode(p, forms, false)).Should().BeFalse("другой штрихкод — отбросить");
        System.Text.Json.JsonDocument.Parse("{\"name\":\"Из общей базы\"}").RootElement
            .Let(p => PurchaseReceivingService.ServerAnswerHasCode(p, forms, false)).Should().BeTrue("в ответе общей базы поля штрихкода нет — ответ на точный запрос принимаем");
        PurchaseReceivingService.MaskCode("4600000000011").Should().Be("46*********11", "в журнале — без середины кода");
    }

    [Fact]
    public async Task Foreign_weight_product_from_the_server_is_dropped_and_the_code_stays_unknown()
    {
        var code = Ean13("200045600789");
        using var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        // сервер по коду «20…» отвечает весовым товаром по PLU внутри кода — без этого штрихкода
        server.Json("GET", $"/api/main/products/warehouse-barcode/{code}/", 200, "{\"product\":{\"id\":\"w45\",\"name\":\"Колбаса весовая\",\"barcode\":\"\",\"is_weight\":true,\"unit\":\"кг\",\"quantity\":12,\"price\":\"500\"}}");
        server.Json("GET", $"/api/main/products/global-barcode/{code}/", 404, "{\"detail\":\"not found\"}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        var savedW = WeightBarcodeParser.WeightPrefixes;
        var savedA = WeightBarcodeParser.AmountPrefixes;
        try
        {
            WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
            WeightBarcodeParser.AmountPrefixes = new HashSet<string>();

            var line = await PurchaseReceivingService.Instance.LookupAsync(code, 1);

            line.Source.Should().Be(ReceivingSource.Unknown, "чужой весовой товар отброшен — «Неизвестно» с предложением создать");
            line.ProductId.Should().BeNull();
            line.IsWeight.Should().BeFalse("не весовой");
            line.Barcode.Should().Be(code);
            ProductCodeLookup.FindWeightLabel(code).Should().BeNull("весы не настроены — этикеткой не считаем");
        }
        finally
        {
            PosApp.CatalogApi = previous;
            WeightBarcodeParser.WeightPrefixes = savedW;
            WeightBarcodeParser.AmountPrefixes = savedA;
        }
    }

    [Fact]
    public async Task Warehouse_product_with_exactly_this_barcode_is_still_found_on_the_server()
    {
        using var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/products/warehouse-barcode/4600000000028/", 200, "{\"product\":{\"id\":\"p1\",\"name\":\"Вода\",\"barcode\":\"4600000000028\",\"unit\":\"шт\",\"quantity\":3,\"price\":\"30\",\"purchase_price\":\"25\"}}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            var line = await PurchaseReceivingService.Instance.LookupAsync("4600000000028", 1);
            line.Source.Should().Be(ReceivingSource.Warehouse);
            line.ProductId.Should().Be("p1");
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }

    // ------------------------------------------------------------------ защита ячеек

    [Fact]
    public void Barcode_typed_into_price_quantity_or_markup_is_rejected_and_the_old_value_stays()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 2, PurchasePrice = 25, SalePrice = 30 };

        l.PurchasePriceText = "4600000000011";
        l.PurchasePrice.Should().Be(25, "штрихкод в «Закупке» ценой не стал");
        l.PriceProblemText.Should().Be(ReceivingLineVm.BarcodeInCellText);
        l.CommitText();
        l.PurchasePriceText.Should().Be("25");

        l.SalePriceText = "46000000";
        l.SalePrice.Should().Be(30, "EAN-8 в «Цене продажи» — тоже отклонён");
        l.CommitText();

        l.QuantityText = "4600000000011";
        l.Quantity.Should().Be(2, "штрихкод в «Принимаем» количеством не стал");
        l.CommitText();

        l.MarkupText = "4600000000011";
        l.SalePrice.Should().Be(30, "штрихкод в «Наценке» цену не пересчитал");
    }

    [Fact]
    public void Giant_price_blocks_posting_and_giant_markup_warns()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 25, SalePrice = 30 };
        // 2026-11-11: «150000000» (одни цифры, от 8) — это вид штрихкода: ячейка его не принимает ценой (Б-4), поэтому предел проверяем числом с пробелами разрядов.
        l.SetSaleText("150000000");
        l.SalePrice.Should().Be(30, "штрихкод ценой не становится");
        l.CommitText();
        l.SetSaleText("150 000 000");
        l.SalePrice.Should().Be(30, "больше предела сервера (9 999 999,999) — не принято");
        l.PriceProblemText.Should().Be(ServerPriceLimits.SaleTooBigText);
        l.CommitText();

        // 2026-11-11 (ответ Умара): больше 1 000 000 — красная пометка, но проводить можно; блокирует только выше пределов сервера.
        l.SetSaleText("2 000 000");
        l.SalePrice.Should().Be(2_000_000);
        l.IsPriceTooBig.Should().BeTrue();
        l.LowMarginText.Should().Be(ReceivingLineVm.PriceTooBigText, "красная пометка «подозрительно большая цена»");
        PurchaseReceivingService.Validate(new[] { l }).Should().BeEmpty("выше 1 000 000, но в пределах сервера — только предупреждение");

        var m = new ReceivingLineVm { ProductName = "Спички", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 1, SalePrice = 200 };
        m.MarkupPercent.Should().BeGreaterThan(ReceivingLineVm.MarkupWarnMax);
        m.LowMarginText.Should().Be(ReceivingLineVm.MarkupTooBigText, "наценка больше 10 000 % — предупреждение");
        PurchaseReceivingService.Validate(new[] { m }).Should().BeEmpty("наценка-великан только предупреждает");
    }
}

/// <summary>2026-11-11 (видео 11.09.30: штучные «Ак Кант», «макароны» в приёмке становились ВЕСОВЫМИ): штучный товар с кодом «20…»/«27…» (наши внутренние коды тоже на «20…»)
/// находится ТОЧНЫМ совпадением и остаётся штучным — даже при настроенном префиксе весов и весовом товаре, чей PLU совпал бы с «кодом внутри».</summary>
[Collection("Wave5Database")]
public sealed class ReceivingPieceCodeOn2xTests : IDisposable
{
    private readonly List<NurMarketKassa.Models.Pos.CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();
    private readonly IReadOnlySet<string> _savedW = WeightBarcodeParser.WeightPrefixes;

    public ReceivingPieceCodeOn2xTests() => CatalogCacheService.Products.Clear();

    public void Dispose()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
        WeightBarcodeParser.WeightPrefixes = _savedW;
    }

    [Theory]
    [InlineData("2071107000024")]
    [InlineData("2700000000017")]
    public async Task Piece_product_with_a_2x_code_is_found_exactly_and_stays_a_piece_line(string code)
    {
        var piece = NurMarketKassa.Tests.Helpers.CatalogTestHelper.CreateTile("ak1", "Ак Кант БайЭл 200гр", code, 40);
        piece.Unit = "шт";
        var weighed = NurMarketKassa.Tests.Helpers.CatalogTestHelper.CreateTile("w1", "Колбаса весовая", "", 5);
        weighed.Unit = "кг";
        weighed.Plu = int.Parse(code.Substring(2, 5), System.Globalization.CultureInfo.InvariantCulture); // «код товара» внутри как у этикетки весов
        CatalogCacheService.Products.AddRange(new[] { piece, weighed });
        WeightBarcodeParser.WeightPrefixes = new HashSet<string> { code[..2] }; // даже весы настроены на этот префикс

        var line = await PurchaseReceivingService.Instance.LookupAsync(code, 1);

        line.Source.Should().Be(ReceivingSource.Warehouse, "точное совпадение штрихкода в базе — раньше любых весовых правил");
        line.ProductId.Should().Be("ak1");
        line.Unit.Should().Be("шт");
        line.IsWeight.Should().BeFalse("карточка «шт» не становится весовой из-за префикса кода");
        line.Quantity.Should().Be(1);
    }

    [Fact]
    public void Repeat_scan_in_pack_mode_adds_a_pack_and_a_typed_barcode_is_handed_back_as_a_scan()
    {
        var l = new ReceivingLineVm { ProductName = "Ак Кант", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 10, SalePrice = 15 };
        l.SetPackMode(true, 24);
        l.SetPackCount(40);
        ReceivingRepeatScan.AddTo(l, 1);
        l.PackCount.Should().Be(41, "Б-4: повторный скан в режиме упаковок — +1 упаковка, как показано в колонке");
        l.Quantity.Should().Be(41 * 24);

        l.QtyInputText = "4870000000014"; // медленный сканер «напечатал» код в ячейку
        l.PackCount.Should().Be(41, "код количеством не стал");
        l.TakeRejectedBarcode().Should().Be("4870000000014", "окно обработает его как обычный скан");
        l.TakeRejectedBarcode().Should().BeNull();
    }
}

internal static class JsonTestExtensions
{
    public static T Let<T>(this System.Text.Json.JsonElement e, Func<System.Text.Json.JsonElement, T> f) => f(e);
}
