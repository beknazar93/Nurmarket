using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (ХМ3): скан коробки = N штук. grocery — как раньше (+1); hardware + галочка: коробка ×200 → +200; галочка выкл → +1;
/// дробное N — только при включённом дробном количестве (ХМ2); основной штрихкод и весовой товар — не коробка.</summary>
[Collection("ServerFeatures")]
public sealed class PackScanRulesTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _settings = UserPreferences.Instance.SphereSettingsJson;

    public PackScanRulesTests()
    {
        QuantityRules.ServerAcceptsFractional = false;
        QuantityRules.ServerFlagEnabled = () => false;
    }

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        UserPreferences.Instance.SphereSettingsJson = _settings;
        QuantityRules.ServerAcceptsFractional = false;
        QuantityRules.ServerFlagEnabled = () => ServerCapabilities.Has(ServerCapabilities.Hardware202611);
    }

    private static void Setup(string baseSphere, string? detail, bool? packScan)
    {
        UserPreferences.Instance.MarketSphere = baseSphere;
        UserPreferences.Instance.MarketSphereDetail = detail;
        UserPreferences.Instance.SphereSettingsJson = packScan is { } p
            ? "{\"hardware\":{\"size\":\"shop\",\"packScan\":" + (p ? "true" : "false") + "}}"
            : null;
    }

    private static CatalogProductTileVm Screws(string unit = "шт", bool weigh = false) =>
        new("p-screw", "Саморез 4x40", "1.50 сом", weigh)
        {
            Unit = unit,
            Barcode = "4600000000011",
            AlternateBarcodeVariants = new List<AlternateBarcodeVariant>
            {
                new AlternateBarcodeVariant { Barcode = "4600000000200", Name = null, Quantity = 200 },
                new AlternateBarcodeVariant { Barcode = "4600000000001", Name = "Клубничный", Quantity = 1 },
                new AlternateBarcodeVariant { Barcode = "4600000000255", Name = "Мешок", Quantity = 25.5 },
            },
        };

    [Fact]
    public void Hardware_WithSwitch_BoxScanAddsAllPieces()
    {
        Setup("grocery", "hardware", true);
        var tile = Screws();
        var variant = PackScanRules.Find(tile, "4600000000200", out var n);
        variant.Should().NotBeNull();
        n.Should().Be(200d);
        PackScanRules.Label(variant!, n).Should().Be("Коробка ×200");
        PackScanRules.QuantityText(n * 2).Should().Be("400", "повторный скан той же коробки прибавляет ещё N");
    }

    [Fact]
    public void Hardware_SwitchOff_ScanIsPlusOne()
    {
        Setup("grocery", "hardware", false);
        PackScanRules.Find(Screws(), "4600000000200", out var n).Should().BeNull();
        n.Should().Be(0d);
        Setup("grocery", "hardware", null);
        PackScanRules.Find(Screws(), "4600000000200", out _).Should().BeNull("у перешедшего магазина по умолчанию выкл");
    }

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    public void OtherSpheres_AreNotAffected_EvenWithSwitchInSettings(string baseSphere, string? detail)
    {
        Setup(baseSphere, detail, true);
        PackScanRules.Enabled.Should().BeFalse();
        PackScanRules.Find(Screws(), "4600000000200", out _).Should().BeNull("скан доп. кода работает как раньше (+1 и имя варианта)");
    }

    [Fact]
    public void QuantityOne_MainBarcode_AndWeighedGoods_AreNotBoxes()
    {
        Setup("grocery", "hardware", true);
        PackScanRules.Find(Screws(), "4600000000001", out _).Should().BeNull("N = 1 — обычный вариант");
        PackScanRules.Find(Screws(), "4600000000011", out _).Should().BeNull("основной штрихкод — одна единица");
        PackScanRules.Find(Screws(), "9999999999999", out _).Should().BeNull("чужой код");
        PackScanRules.Find(Screws("кг", weigh: true), "4600000000200", out _).Should().BeNull("весовой товар");
    }

    [Fact]
    public void FractionalPack_OnlyWhenFractionalQuantityIsOn()
    {
        Setup("grocery", "hardware", true);
        var tile = Screws("шт");
        PackScanRules.Find(tile, "4600000000255", out _).Should().BeNull("«шт» остаётся целым — 25,5 не добавляем");
        var bag = Screws("м²");
        PackScanRules.Find(bag, "4600000000255", out _).Should().BeNull("дробное выключено — как раньше");
        QuantityRules.ServerFlagEnabled = () => true;
        PackScanRules.Find(bag, "4600000000255", out var n).Should().NotBeNull();
        n.Should().Be(25.5d);
    }
}
