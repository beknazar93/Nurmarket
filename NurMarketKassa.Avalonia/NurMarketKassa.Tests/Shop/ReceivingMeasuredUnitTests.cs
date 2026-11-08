using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-08 (жалоба владельца: «на складе 3 л, пришло 10 л, а приёмка ведёт строку как штучную»): строка приёмки берёт единицу из карточки товара (л, кг, м…),
/// количество вводится в ней (дробное можно), остаток после прихода = остаток + принятое (3 + 10 = 13 л); единица переживает очередь «Неотправленное»; упаковки и весовые кг не ломаются.
/// Без сети и без боевых данных.</summary>
public sealed class ReceivingMeasuredUnitTests
{
    private static CatalogProductTileVm Tile(string unit, double stock) =>
        new("p1", "Масло подсолнечное", "120.00 сом", false) { Unit = unit, Quantity = stock, PurchasePrice = 90 };

    [Fact]
    public void Scan_of_a_litre_product_keeps_the_card_unit_and_stock_adds_up()
    {
        var line = PurchaseReceivingService.FromTile(Tile("л", 3), 1);
        line.Unit.Should().Be("л", "единица берётся из карточки, а не «шт» по умолчанию");
        line.OriginalUnit.Should().Be("л");
        line.StockBefore.Should().Be(3);

        line.Quantity = 10; // ввод «10» — это 10 л
        (line.StockBefore + line.Quantity).Should().Be(13, "3 л + 10 л = 13 л");
        line.Quantity = 2.5;
        (line.StockBefore + line.Quantity).Should().Be(5.5, "дробное количество допустимо в литрах");
    }

    [Fact]
    public void Fractional_quantity_is_fine_for_litres_and_kilos_but_not_for_pieces_of_a_new_product()
    {
        ReceivingLineVm New(string unit, double qty, bool weight = false) =>
            new() { Barcode = "4600", ProductName = "Новый", Unit = unit, Source = ReceivingSource.Unknown, Quantity = qty, IsWeight = weight };

        PurchaseReceivingService.FractionalProblems(new[] { New("л", 2.5), New("м", 0.5), New("кг", 1.25, weight: true) }).Should().BeEmpty();
        PurchaseReceivingService.FractionalProblems(new[] { New("шт", 2.5) }).Should().HaveCount(1);
    }

    [Fact]
    public void Unit_and_fractional_quantity_survive_the_unsent_queue()
    {
        var line = PurchaseReceivingService.FromTile(Tile("л", 3), 10.5);
        var back = JobLine.From(line).ToVm();
        back.Unit.Should().Be("л");
        back.OriginalUnit.Should().Be("л");
        back.Quantity.Should().Be(10.5);
        back.StockBefore.Should().Be(3);
    }

    [Fact]
    public void Pack_mode_does_not_touch_the_unit_and_switching_it_off_keeps_the_numbers()
    {
        var line = PurchaseReceivingService.FromTile(Tile("л", 3), 1);
        line.SetPackMode(true, 12);
        line.SetPackCount(2);
        line.Quantity.Should().Be(24, "2 упаковки × 12");
        line.Unit.Should().Be("л");
        line.SetPackMode(false);
        line.Quantity.Should().Be(24);
        line.Unit.Should().Be("л");
    }

    [Fact]
    public void Weight_kilo_line_is_unchanged()
    {
        var tile = new CatalogProductTileVm("p2", "Сахар", "60.00 сом", true) { Unit = "кг", Quantity = 3.5, PurchasePrice = 40 };
        var line = PurchaseReceivingService.FromTile(tile, 2.25);
        line.Unit.Should().Be("кг");
        (line.StockBefore + line.Quantity).Should().Be(5.75);
    }
}
