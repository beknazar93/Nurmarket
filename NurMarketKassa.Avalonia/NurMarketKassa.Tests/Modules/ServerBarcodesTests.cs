using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Modules;

/// <summary>2026-11-21 (ответ Умара 20.11): внутренние штрихкоды — POST main/barcodes/next/ {count 1..100} → {"barcodes": [...]}; сервер коды не резервирует, запасной локальный генератор.
/// Приход: sale_unit_id — только вместе с количеством и ценой в упаковках, без него — штуки и цена за штуку.</summary>
[Collection("UserPreferences")]
public sealed class ServerBarcodesTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();

    public ServerBarcodesTests()
    {
        ServerBarcodes.ResetForTests();
        ServerBarcodes.NextOverride = null;
    }

    public void Dispose()
    {
        ServerBarcodes.NextOverride = null;
        ServerBarcodes.ResetForTests();
        NoFactoryBarcodes.ResetForTests(); // 2026-11-21: server codes are remembered as ours (NoFactoryBarcodes) - do not leave them to other tests
        _server.Dispose();
    }

    private async Task<NurMarketApiClient> LoginAsync()
    {
        _server.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");
        return api;
    }

    [Fact]
    public void ParseCodes_TakesNonEmptyStrings_FromObjectOrArray_AndRejectsGarbage()
    {
        using var obj = JsonDocument.Parse("""{"barcodes":["1234567890123"," 3000000000007 ","",null,5000000000001]}""");
        ServerBarcodes.ParseCodes(obj.RootElement).Should().Equal("1234567890123", "3000000000007", "5000000000001");
        using var arr = JsonDocument.Parse("""["1111111111116"]""");
        ServerBarcodes.ParseCodes(arr.RootElement).Should().Equal("1111111111116");
        using var bad = JsonDocument.Parse("""{"detail":"x"}""");
        ServerBarcodes.ParseCodes(bad.RootElement).Should().BeNull();
        using var empty = JsonDocument.Parse("""{"barcodes":[]}""");
        ServerBarcodes.ParseCodes(empty.RootElement).Should().BeNull("пустой ответ — как «сервер не дал»");
    }

    [Theory]
    [InlineData("1234567890128", true)]
    [InlineData("9876543210987", true)]
    [InlineData("2000000000006", false)] // 2… — весовой префикс
    [InlineData("0123456789012", false)]
    [InlineData("12ab", false)]
    [InlineData("", false)]
    public void LooksUsable_RejectsWeightPrefixZeroAndNonDigits(string code, bool expected) =>
        ServerBarcodes.LooksUsable(code).Should().Be(expected);

    [Fact]
    public async Task NextAsync_PostsCount_ClampedTo100_AndReadsBarcodes()
    {
        _server.Json("POST", "/api/main/barcodes/next/", 200, """{"barcodes":["1000000000009","1000000000016"]}""");
        var api = await LoginAsync();

        var codes = await ServerBarcodes.NextAsync(api, 500, default);

        codes.Should().Equal("1000000000009", "1000000000016");
        var body = JsonDocument.Parse(_server.Server.LogEntries.Single(e => e.RequestMessage.Path == "/api/main/barcodes/next/").RequestMessage.Body!).RootElement;
        body.GetProperty("count").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task NextAsync_NoAddressOnServer_ReturnsNull_AndDoesNotAskAgainSoon()
    {
        _server.Error("POST", "/api/main/barcodes/next/", 404);
        var api = await LoginAsync();

        (await ServerBarcodes.NextAsync(api, 1, default)).Should().BeNull();
        (await ServerBarcodes.NextAsync(api, 1, default)).Should().BeNull();

        _server.Server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/barcodes/next/").Should().Be(1, "после 404 минут десять сервер не спрашиваем");
    }

    [Fact]
    public async Task GenerateMany_UsesServerCodes_SkipsWeightPrefixAndDuplicates_FillsRestLocally()
    {
        ServerBarcodes.NextOverride = (count, _) => Task.FromResult<IReadOnlyList<string>?>(new[] { "2000000000006", "4600000000011", "4600000000011", "5100000000008" });

        var codes = await BarcodeGenerator.GenerateManyForCatalogAsync(4, new[] { "5100000000008" });

        codes.Should().HaveCount(4).And.OnlyHaveUniqueItems();
        codes.Should().Contain("4600000000011", "годный код сервера берётся");
        codes.Should().NotContain("2000000000006", "весовой префикс — не годится");
        codes.Count(c => c == "5100000000008").Should().Be(0, "код уже занят в открытом окне");
        codes.Where(c => c != "4600000000011").Should().OnlyContain(c => BarcodeGenerator.HasValidCheckDigit(c) && c[0] != '0' && c[0] != '2', "остальное — запасной локальный генератор");
    }

    [Fact]
    public async Task GenerateMany_ServerUnavailable_FallsBackToLocalGenerator()
    {
        ServerBarcodes.NextOverride = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);

        var codes = await BarcodeGenerator.GenerateManyForCatalogAsync(5);
        var one = await BarcodeGenerator.GenerateForCatalogAsync();

        codes.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        codes.Concat([one]).Should().OnlyContain(c => c.Length == 13 && BarcodeGenerator.HasValidCheckDigit(c));
    }

    [Fact]
    public async Task GenerateOne_TakesTheServerCodeWhenItIsFree()
    {
        ServerBarcodes.NextOverride = (count, _) =>
        {
            count.Should().Be(1);
            return Task.FromResult<IReadOnlyList<string>?>(new[] { "7300000000007" });
        };

        (await BarcodeGenerator.GenerateForCatalogAsync()).Should().Be("7300000000007");
    }

    // ───────────── приход: sale_unit_id ─────────────

    [Fact]
    public void ReceivingLine_WithSaleUnit_GoesInPacks_WithoutItInPieces_NeverMixed()
    {
        var pieces = new ReceivingLineVm { Quantity = 10, PurchasePrice = 25 };
        PurchaseReceivingService.UsesSaleUnit(pieces).Should().BeFalse();
        PurchaseReceivingService.ServerQtyPrice(pieces).Should().Be((10d, 25d), "как раньше: штуки и цена за штуку");

        var packs = new ReceivingLineVm { Quantity = 120, PurchasePrice = 25, SaleUnitId = "su-7", SaleUnitFactor = 12 };
        PurchaseReceivingService.UsesSaleUnit(packs).Should().BeTrue();
        var (qty, price) = PurchaseReceivingService.ServerQtyPrice(packs);
        qty.Should().Be(10d, "120 штук = 10 упаковок по 12");
        price.Should().Be(300d, "цена ЗА УПАКОВКУ: 25 × 12");
        (qty * price).Should().Be(packs.Quantity * packs.PurchasePrice, "сумма строки не меняется");

        var noFactor = new ReceivingLineVm { Quantity = 5, PurchasePrice = 2, SaleUnitId = "su-1", SaleUnitFactor = 0 };
        PurchaseReceivingService.UsesSaleUnit(noFactor).Should().BeFalse("единица без коэффициента — штуки, смешивать нельзя");
        PurchaseReceivingService.ServerQtyPrice(noFactor).Should().Be((5d, 2d));
    }
}
