using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ»): способ приёмки по умолчанию («В штуках / Упаковками / Поштучная продажа») с верхней панели — применение к НОВЫМ строкам
/// и к строкам по кнопке «Ко всем строкам». Решает только «каким строкам можно» и зовёт готовые переключатели строки; расчёты упаковок (ReceivingByPacksTests,
/// ReceivingPackSaleTests) и подтверждение «Было → станет» (ReceivingConvertToPackagesTests) не дублируются.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingPackDefaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-packdefaults-" + Guid.NewGuid().ToString("N"));

    public ReceivingPackDefaultsTests()
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

    private static ReceivingLineVm Unknown(string code = "4602000000017") =>
        new() { ProductName = "Сигареты", Barcode = code, Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 20, SalePrice = 30 };

    private static ReceivingLineVm Existing(string id = "pa")
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Barcode = "4600000000011", StockBefore = 7, Quantity = 1, ProductId = id, Source = ReceivingSource.Warehouse, PurchasePrice = 25, SalePrice = 30 };
        return l;
    }

    [Fact]
    public void Pieces_mode_leaves_a_new_line_untouched()
    {
        var l = Unknown();
        ReceivingPackDefaults.ApplyToNew(l, ReceivingPackDefaultMode.Pieces, 12).Should().BeFalse();
        l.PackMode.Should().BeFalse();
        l.Quantity.Should().Be(1);
        l.PurchasePrice.Should().Be(20);
    }

    [Fact]
    public void Packs_mode_turns_a_new_line_into_one_pack_of_the_default_size_and_keeps_the_piece_price()
    {
        var l = Unknown();
        ReceivingPackDefaults.ApplyToNew(l, ReceivingPackDefaultMode.Packs, 12).Should().BeTrue();

        l.PackMode.Should().BeTrue();
        l.PackSale.Should().BeFalse();
        l.PiecesPerPack.Should().Be(12);
        l.PackCount.Should().Be(1, "число «1» после скана — одна упаковка");
        l.Quantity.Should().Be(12, "на склад — штуки");
        l.PurchasePrice.Should().Be(20, "цена за штуку не меняется");
        l.PackPrice.Should().Be(240, "цена упаковки = цена за штуку × штук в упаковке");
        l.PackNeedsSize.Should().BeFalse();
    }

    [Fact]
    public void Packs_mode_without_default_size_leaves_size_empty_and_the_line_blocked_until_it_is_typed()
    {
        var l = Unknown();
        ReceivingPackDefaults.ApplyToNew(l, ReceivingPackDefaultMode.Packs, 0).Should().BeTrue();
        l.PackMode.Should().BeTrue();
        l.PackNeedsSize.Should().BeTrue("размер не знаем — строку нельзя провести, пока его не впишут");
        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains("упаков"));
    }

    [Fact]
    public void Remembered_size_of_the_product_wins_over_the_default_size()
    {
        var remembered = Existing("rem");
        remembered.SetPackMode(true, 24);
        ReceivingPackSizes.Remember(remembered);

        var again = Existing("rem");
        ReceivingPackDefaults.ApplyToNew(again, ReceivingPackDefaultMode.Packs, 12).Should().BeTrue();
        again.PiecesPerPack.Should().Be(24, "в прошлый раз этот товар принимали по 24");

        var other = Existing("other");
        other.Barcode = "4600000000099"; // другой товар: у «Existing» один и тот же штрихкод, а память ищет и по нему
        ReceivingPackDefaults.ApplyToNew(other, ReceivingPackDefaultMode.Packs, 12);
        other.PiecesPerPack.Should().Be(12, "у товара без памяти — размер по умолчанию с панели");
    }

    [Fact]
    public void Pack_sale_mode_goes_to_new_products_only_and_a_warehouse_product_is_left_for_the_row_menu()
    {
        var fresh = Unknown();
        ReceivingPackDefaults.ApplyToNew(fresh, ReceivingPackDefaultMode.PackSale, 10).Should().BeTrue();
        fresh.PackSale.Should().BeTrue();
        fresh.PiecesPerPack.Should().Be(10);
        fresh.Quantity.Should().Be(1, "на склад — УПАКОВКИ");

        var warehouse = Existing();
        ReceivingPackDefaults.ApplyToNew(warehouse, ReceivingPackDefaultMode.PackSale, 10).Should().BeFalse("у товара склада поштучная продажа меняет единицу остатка — только явно, в самой строке");
        warehouse.PackMode.Should().BeFalse();
    }

    [Fact]
    public void Card_pack_weight_and_size_lines_are_never_changed()
    {
        var cardPack = Existing("cp");
        cardPack.SetCardPackSizeFromServer(10);
        cardPack.CardPackSize.Should().Be(10);
        ReceivingPackDefaults.ApplyToNew(cardPack, ReceivingPackDefaultMode.Packs, 12).Should().BeFalse("склад и так ведёт товар упаковками — «упак. = N шт»");
        cardPack.PackMode.Should().BeFalse();

        var weight = new ReceivingLineVm { ProductName = "Сахар", Source = ReceivingSource.Unknown, Quantity = 1, IsWeight = true };
        ReceivingPackDefaults.ApplyToNew(weight, ReceivingPackDefaultMode.Packs, 12).Should().BeFalse("весовой — без упаковок");

        var sizes = Unknown();
        sizes.AddVariant("s1", "S", 2);
        ReceivingPackDefaults.ApplyToNew(sizes, ReceivingPackDefaultMode.Packs, 12).Should().BeFalse("строка с размерами — без упаковок");
    }

    [Fact]
    public void Apply_to_all_changes_allowed_lines_and_reports_what_was_skipped()
    {
        var a = Unknown("4602000000001");
        var b = Unknown("4602000000002");
        b.Quantity = 5; // уже введено количество
        var warehouse = Existing();
        var cardPack = Existing("cp");
        cardPack.SetCardPackSizeFromServer(6);
        var weight = new ReceivingLineVm { ProductName = "Сахар", Source = ReceivingSource.Unknown, Quantity = 1, IsWeight = true };
        var lines = new[] { a, b, warehouse, cardPack, weight };

        var (willChange, withValues) = ReceivingPackDefaults.Preview(lines, ReceivingPackDefaultMode.PackSale);
        willChange.Should().Be(2, "поштучная продажа — только два новых товара");
        // 2026-11-12 (аудит У-2): «введено» — и количество не «1», и цены; у обеих новых строк цены есть (20 / 30) — спросим про обе
        withValues.Should().Be(2, "у одной введено количество 5, у обеих — цены закупки и продажи: перед применением спросим");

        var outcome = ReceivingPackDefaults.ApplyToAll(lines, ReceivingPackDefaultMode.PackSale, 10);
        outcome.Applied.Should().Be(2);
        outcome.SkippedExistingProduct.Should().Be(1, "товар склада");
        outcome.SkippedCardPack.Should().Be(1);
        outcome.SkippedUnsupported.Should().Be(1, "весовой");
        a.PackSale.Should().BeTrue();
        b.PackSale.Should().BeTrue();
        b.Quantity.Should().Be(5, "введённое число упаковок остаётся");
        warehouse.PackMode.Should().BeFalse();

        // ещё раз то же — менять нечего
        ReceivingPackDefaults.Preview(lines, ReceivingPackDefaultMode.PackSale).WillChange.Should().Be(0);
        ReceivingPackDefaults.ApplyToAll(lines, ReceivingPackDefaultMode.PackSale, 10).Applied.Should().Be(0);
    }

    [Fact]
    public void Apply_to_all_in_packs_mode_includes_warehouse_products_and_back_to_pieces_restores_the_numbers()
    {
        var warehouse = Existing();
        warehouse.Quantity = 3;
        var fresh = Unknown();
        var lines = new[] { warehouse, fresh };

        ReceivingPackDefaults.ApplyToAll(lines, ReceivingPackDefaultMode.Packs, 6).Applied.Should().Be(2);
        warehouse.PackMode.Should().BeTrue();
        warehouse.PiecesPerPack.Should().Be(6);
        warehouse.Quantity.Should().Be(3, "число штук, не равное «1», остаётся (как при выборе в меню строки) и пересчитывается в упаковки");
        warehouse.PackCount.Should().Be(0.5);

        ReceivingPackDefaults.ApplyToAll(lines, ReceivingPackDefaultMode.Pieces, 6).Applied.Should().Be(2);
        warehouse.PackMode.Should().BeFalse();
        warehouse.Quantity.Should().Be(3, "выключили режим — штуки остаются как были");
        ReceivingPackDefaults.ModeOf(fresh).Should().Be(ReceivingPackDefaultMode.Pieces);
    }

    [Fact]
    public void From_pack_sale_to_packs_and_to_pieces_goes_through_the_lines_own_switches()
    {
        var l = Unknown();
        ReceivingPackDefaults.ApplyToNew(l, ReceivingPackDefaultMode.PackSale, 10).Should().BeTrue();
        ReceivingPackDefaults.ModeOf(l).Should().Be(ReceivingPackDefaultMode.PackSale);

        ReceivingPackDefaults.ApplyToAll(new[] { l }, ReceivingPackDefaultMode.Packs, 10).Applied.Should().Be(1);
        ReceivingPackDefaults.ModeOf(l).Should().Be(ReceivingPackDefaultMode.Packs);
        l.PackSale.Should().BeFalse();

        ReceivingPackDefaults.ApplyToAll(new[] { l }, ReceivingPackDefaultMode.Pieces, 10).Applied.Should().Be(1);
        ReceivingPackDefaults.ModeOf(l).Should().Be(ReceivingPackDefaultMode.Pieces);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(1e9, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(0.5, true)]
    [InlineData(24, true)]
    public void Default_size_must_be_positive_and_finite(double size, bool valid) =>
        ReceivingPackDefaults.IsValidSize(size).Should().Be(valid);
}
