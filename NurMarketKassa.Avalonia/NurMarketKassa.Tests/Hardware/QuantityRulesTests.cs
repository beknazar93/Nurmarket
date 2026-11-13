using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (ХМ2, ТЗ хозмаг): дробное количество «Хозтоваров» включается ФЛАГОМ СЕРВЕРА (company_flags.hardware_2026_11), а не галочкой.
/// Проверяется: флаг + сфера; у других сфер ничего не меняется; «14,40 м² в чеке» уходит на сервер как «14.4» (тело add-item, WireMock), а при
/// выключенной функции дробное количество не попадает в чек вовсе (чек и тело совпадают); метка строки переживает «мигание» флага; разбор company_flags.
/// Меняет общие статические настройки — не параллелен другим.</summary>
[Collection("ServerFeatures")]
public sealed class QuantityRulesTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-qty-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeNurCrmServer _fake = new();

    public QuantityRulesTests()
    {
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests();
        QuantityRules.ServerAcceptsFractional = false;
        QuantityRules.ServerFlagEnabled = () => false;
    }

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        QuantityRules.ServerAcceptsFractional = false;
        QuantityRules.ServerFlagEnabled = () => ServerCapabilities.Has(ServerCapabilities.Hardware202611);
        ServerCapabilities.DirOverride = null;
        ServerCapabilities.SetFeaturesForTests();
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static void SetSphere(string baseSphere, string? detail)
    {
        UserPreferences.Instance.MarketSphere = baseSphere;
        UserPreferences.Instance.MarketSphereDetail = detail;
    }

    private static CatalogProductTileVm Tile(string unit, bool weigh = false) =>
        new("p-" + Guid.NewGuid().ToString("N")[..6], "Товар", "520.00 сом", weigh) { Unit = unit, Quantity = 100 };

    private static string Snapshot(CatalogProductTileVm tile, string qty)
    {
        var cart = new CartService();
        LocalCartService.StartNewLocalCart(cart);
        LocalCartService.AddProduct(cart, tile, qty);
        return cart.Root.GetRawText();
    }

    private async Task<string> SentQuantityAsync(string snapshot)
    {
        _fake.Json("POST", "/api/main/pos/sales/*/add-item/", 200, "{}");
        var api = await LoggedInAsync(_fake);
        await StagingCartService.PushItemsFromSnapshotAsync(new SalesApiService(api), "cart-1", snapshot, default);
        var body = BodyOf(Requests(_fake, "POST", "/add-item/").Last());
        return body["quantity"]!.ToString();
    }

    // ───────────── флаг и сфера ─────────────

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    public void OtherSpheres_NeverActive_EvenWithFlagOn(string baseSphere, string? detail)
    {
        SetSphere(baseSphere, detail);
        QuantityRules.ServerFlagEnabled = () => true;
        QuantityRules.ServerAcceptsFractional = true;
        QuantityRules.Active.Should().BeFalse();
        QuantityRules.ForUnit("м²").Should().BeNull();
    }

    [Fact]
    public void Hardware_ActiveOnlyWithServerFlag_OrStandSwitch()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.Active.Should().BeFalse("флага сервера нет — как раньше");
        QuantityRules.ServerFlagEnabled = () => true;
        QuantityRules.Active.Should().BeTrue();
        QuantityRules.ForUnit("м²")!.Step.Should().Be(0.01m);
        QuantityRules.ForUnit("м")!.Min.Should().Be(1m);
        QuantityRules.ForUnit("шт").Should().BeNull("штучная единица остаётся целой");
        QuantityRules.ForUnit("кг").Should().BeNull("весовой путь не меняется");
        QuantityRules.ServerFlagEnabled = () => false;
        QuantityRules.ServerAcceptsFractional = true; // переключатель стенда
        QuantityRules.Active.Should().BeTrue();
    }

    [Fact]
    public void Hardware_FlagComesFromCapabilitiesCompanyFlags()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => ServerCapabilities.Has(ServerCapabilities.Hardware202611);
        ServerCapabilities.SetFeaturesForTests();
        QuantityRules.Active.Should().BeFalse();
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.Hardware202611);
        QuantityRules.Active.Should().BeTrue();
    }

    [Fact]
    public void Capabilities_Parse_ReadsTrueCompanyFlags_NotFalseOnes()
    {
        using var doc = JsonDocument.Parse("{\"api\":\"2026-10\",\"features\":[\"checkout_branch\"],\"company_flags\":{\"hardware_2026_11\":true,\"api_strict_2026_10\":false}}");
        var set = ServerCapabilities.Parse(doc.RootElement);
        set.Should().Contain("hardware_2026_11").And.Contain("checkout_branch").And.NotContain("api_strict_2026_10");
        using var off = JsonDocument.Parse("{\"features\":[\"checkout_branch\"],\"company_flags\":{\"hardware_2026_11\":false}}");
        ServerCapabilities.Parse(off.RootElement).Should().NotContain("hardware_2026_11");
    }

    // ───────────── что уходит на сервер ─────────────

    [Fact]
    public async Task Hardware_FractionalM2_GoesToServerAs14Point4_AndCartMatches()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => true;
        var snapshot = Snapshot(Tile("м²"), "14.4");

        using var doc = JsonDocument.Parse(snapshot);
        var line = doc.RootElement.GetProperty("items")[0];
        line.GetProperty("quantity").GetDouble().Should().Be(14.4);
        line.GetProperty("frac_qty").GetBoolean().Should().BeTrue();
        ((decimal)line.GetProperty("line_total").GetDouble()).Should().Be(7488m, "14,4 × 520");

        (await SentQuantityAsync(snapshot)).Should().Be("14.4", "в теле add-item — настоящее количество, а не 14");
    }

    [Fact]
    public async Task Hardware_FlagOff_FractionalNeverReachesTheReceipt_BodyMatches()
    {
        SetSphere("grocery", "hardware");
        var snapshot = Snapshot(Tile("м²"), "14.4"); // функция выключена: чек получает целое
        using var doc = JsonDocument.Parse(snapshot);
        doc.RootElement.GetProperty("items")[0].GetProperty("quantity").GetDouble().Should().Be(14d);
        (await SentQuantityAsync(snapshot)).Should().Be("14", "чек и тело запроса совпадают");
    }

    [Fact]
    public async Task Hardware_PieceUnit_StaysWhole_EvenWithFlagOn()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => true;
        var snapshot = Snapshot(Tile("шт"), "2.4");
        using var doc = JsonDocument.Parse(snapshot);
        doc.RootElement.GetProperty("items")[0].GetProperty("quantity").GetDouble().Should().Be(2d);
        (await SentQuantityAsync(snapshot)).Should().Be("2");
    }

    [Fact]
    public async Task Hardware_MarkSurvivesFlagFlicker_LineStillGoesAs14Point4()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => true;
        var snapshot = Snapshot(Tile("м²"), "14.4");
        QuantityRules.ServerFlagEnabled = () => false; // список признаков пропал между добавлением и оплатой
        (await SentQuantityAsync(snapshot)).Should().Be("14.4", "метка дробной строки не теряется");
    }

    [Fact]
    public async Task Grocery_BehavesAsBefore_WholeQuantityBody()
    {
        SetSphere("grocery", null);
        QuantityRules.ServerFlagEnabled = () => true;
        var snapshot = Snapshot(Tile("м²"), "3");
        (await SentQuantityAsync(snapshot)).Should().Be("3");
        QuantityRules.PieceText(JsonDocument.Parse("{\"quantity\":14.4,\"unit\":\"м²\"}").RootElement, 14.4).Should().Be("14", "прежнее Math.Round(qty, 0)");
    }

    [Fact]
    public async Task Weighted_Unchanged_ThreeDecimals()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => true;
        var snapshot = Snapshot(Tile("кг", weigh: true), "0.375");
        (await SentQuantityAsync(snapshot)).Should().Be("0.375");
    }

    // ───────────── шаг, минимум, деньги ─────────────

    [Fact]
    public void Normalize_StepMinAndDecimal()
    {
        SetSphere("grocery", "hardware");
        QuantityRules.ServerFlagEnabled = () => true;
        var m = QuantityRules.ForUnit("м")!;
        QuantityRules.Normalize(0.5, m).Should().Be(1d, "минимум 1 м");
        QuantityRules.Normalize(2.46, m).Should().Be(2.5d, "шаг 0,1 м");
        QuantityRules.Normalize(0.1 + 0.2 + 1, m).Should().Be(1.3d, "без хвоста двоичной погрешности");
        QuantityRules.Gross(0.1, 41.05).Should().Be(4.11, "0,1 × 41,05 = 4,105 → 4,11");
    }
}
