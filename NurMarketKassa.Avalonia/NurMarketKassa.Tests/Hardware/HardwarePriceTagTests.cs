using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (ХМ7, ТЗ хозмаг 3.5): ценник «Хозтовары» — цена за единицу крупно и цена за упаковку; без упаковки — одна цена; прежние виды ценников не затронуты.</summary>
public sealed class HardwarePriceTagTests
{
    private static CatalogProductTileVm Tile(string title, string unit, string price = "520.00 сом") =>
        new("p1", title, price, false) { Unit = unit, Barcode = "4600000000011", Article = "ART-1" };

    [Fact]
    public void Tile_WithPackInTitle_ShowsUnitAndPackPrice()
    {
        var t = HardwarePriceTag.Build(Tile("Плитка 30×60, 1,44 м²", "м²"));
        t.UnitPrice.Should().Be("520,00 сом/м²");
        t.PackPrice.Should().Be("748,80 сом/уп 1,44 м²", "520 × 1,44 = 748,80");
        t.PackLabel.Should().Be("уп 1,44 м²");
    }

    [Fact]
    public void NoPack_ShowsOnlyUnitPrice()
    {
        var t = HardwarePriceTag.Build(Tile("Кабель ВВГ 3×2,5", "м", "85.00 сом"));
        t.UnitPrice.Should().Be("85,00 сом/м");
        t.PackPrice.Should().BeNull();
        t.PackLabel.Should().BeNull();
    }

    [Fact]
    public void PieceUnit_NeverGetsPack()
    {
        HardwarePriceTag.Build(Tile("Саморез коробка 200 шт, 1,44 м²", "шт")).PackPrice.Should().BeNull("«шт» — штучный товар");
    }

    [Fact]
    public void PackFromCardPackages_WinsOverTitle()
    {
        var tile = Tile("Краска белая 2,5 л", "л", "400.00 сом");
        tile.PieceOption = new ProductPackageOption { Id = "pk1", Name = "банка", QuantityInPackage = 5, PieceUnitPrice = 400 };
        var t = HardwarePriceTag.Build(tile);
        t.PackPrice.Should().Be("2000,00 сом/уп 5 л");
    }

    [Fact]
    public void Render_HardwareKind_Is58x40_AndOldKindsStillRender()
    {
        PriceTagService.GetDefaultSize(PriceTagKind.HardwareUnitPack).Should().Be((58d, 40d));
        PriceTagService.GetDefaultSize(PriceTagKind.Simple).Should().Be((40d, 20d), "прежний размер не менялся");
        var data = new PriceTagData("Плитка", "4600000000011", "520,00 сом/м²", PackPriceText: "748,80 сом/уп 1,44 м²", PackLabel: "уп 1,44 м²");
        using (var bmp = PriceTagService.GenerateBitmap(PriceTagKind.HardwareUnitPack, data, 58, 40))
            bmp.Width.Should().BeGreaterThan(300);
        using var old = PriceTagService.GenerateBitmap(PriceTagKind.Simple, new PriceTagData("Хлеб", null, "30,00 сом"), 40, 20);
        old.Width.Should().BeGreaterThan(100);
        PriceTagService.BuildScene(PriceTagKind.HardwareUnitPack, data, 58, 40).Items.Should().NotBeEmpty();
    }

    [Fact]
    public void KindNumbers_OfOldValues_AreUnchanged()
    {
        // BulkPriceTagKind хранит число вида — новый вид только в конце.
        ((int)PriceTagKind.Simple).Should().Be(0);
        ((int)PriceTagKind.WithQr).Should().Be(5);
        ((int)PriceTagKind.Custom).Should().Be(6);
        ((int)PriceTagKind.HardwareUnitPack).Should().Be(7);
    }
}
