using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (аудит «Склад и Приёмка», Л-9, Л-10, Л-24, У-2; решение владельца: способ «Упаковками» не живёт между днями): чистая логика панели «Приём» —
/// «новый день / новое открытие → В штуках», что считается способом не по умолчанию (подсветка), размер для «Поштучной продажи» (целое от 2), «товар склада — в самой строке»,
/// «введено» для вопроса «Ко всем строкам» и откат этой кнопки. Окна и клавиши — в UiTests (ReceivingPackBarUiTests).</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingPackModeResetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-packreset-" + Guid.NewGuid().ToString("N"));

    public ReceivingPackModeResetTests()
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

    private static ReceivingLineVm Unknown(string code = "4602000000017", double qty = 1, double purchase = 20, double sale = 30) =>
        new() { ProductName = "Сигареты", Barcode = code, Source = ReceivingSource.Unknown, Quantity = qty, PurchasePrice = purchase, SalePrice = sale };

    private static ReceivingLineVm Existing() =>
        new() { ProductName = "Вода", Barcode = "4600000000011", StockBefore = 7, Quantity = 1, ProductId = "pa", Source = ReceivingSource.Warehouse, PurchasePrice = 25, SalePrice = 30 };

    // ------------------------------------------------------------------ Л-9: день и открытие

    [Theory]
    [InlineData(ReceivingPackDefaultMode.Packs)]
    [InlineData(ReceivingPackDefaultMode.PackSale)]
    public void Mode_chosen_today_stays_today_and_is_back_to_pieces_tomorrow(ReceivingPackDefaultMode chosen)
    {
        var morning = new DateTime(2026, 11, 12, 9, 0, 0);
        ReceivingPackDefaults.EffectiveMode(chosen, morning, new DateTime(2026, 11, 12, 23, 59, 0)).Should().Be(chosen, "в тот же день способ сохраняется");
        ReceivingPackDefaults.EffectiveMode(chosen, morning, new DateTime(2026, 11, 13, 0, 1, 0)).Should().Be(ReceivingPackDefaultMode.Pieces, "после полуночи — «В штуках»");
        ReceivingPackDefaults.EffectiveMode(chosen, morning, new DateTime(2026, 12, 12, 9, 0, 0)).Should().Be(ReceivingPackDefaultMode.Pieces, "через месяц — тем более");
    }

    [Fact]
    public void Pieces_is_never_highlighted_and_other_modes_are()
    {
        ReceivingPackDefaults.IsNonDefault(ReceivingPackDefaultMode.Pieces).Should().BeFalse();
        ReceivingPackDefaults.IsNonDefault(ReceivingPackDefaultMode.Packs).Should().BeTrue("«Упаковками» — заметная подсветка");
        ReceivingPackDefaults.IsNonDefault(ReceivingPackDefaultMode.PackSale).Should().BeTrue();
        ReceivingPackDefaults.EffectiveMode(ReceivingPackDefaultMode.Pieces, new DateTime(2026, 11, 12), new DateTime(2026, 11, 12)).Should().Be(ReceivingPackDefaultMode.Pieces);
    }

    // ------------------------------------------------------------------ Л-24: размер

    [Theory]
    [InlineData(0.5, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2.5, false)]
    [InlineData(12, true)]
    [InlineData(0, false)]
    [InlineData(double.NaN, false)]
    public void PackSale_size_is_a_whole_number_from_two(double size, bool valid)
    {
        ReceivingPackDefaults.IsValidSizeFor(ReceivingPackDefaultMode.PackSale, size).Should().Be(valid);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2.5)]
    public void Packs_mode_still_accepts_any_positive_size(double size)
    {
        ReceivingPackDefaults.IsValidSizeFor(ReceivingPackDefaultMode.Packs, size).Should().BeTrue();
        ReceivingPackDefaults.IsValidSizeFor(ReceivingPackDefaultMode.Pieces, size).Should().BeTrue();
    }

    [Fact]
    public void PackSale_with_an_unfit_default_size_does_not_put_that_size_into_the_line()
    {
        var one = Unknown();
        ReceivingPackDefaults.ApplyToNew(one, ReceivingPackDefaultMode.PackSale, 1).Should().BeTrue();
        one.PackSale.Should().BeTrue();
        one.PiecesPerPack.Should().Be(0, "1 для «Поштучной продажи» не подставляется — строка попросит ввести размер, а не уйдёт непроводимой");
        one.PackNeedsSize.Should().BeTrue();

        var half = Unknown("4602000000024");
        ReceivingPackDefaults.ApplyToNew(half, ReceivingPackDefaultMode.PackSale, 0.5).Should().BeTrue();
        half.PiecesPerPack.Should().Be(0);

        var good = Unknown("4602000000031");
        ReceivingPackDefaults.ApplyToNew(good, ReceivingPackDefaultMode.PackSale, 12).Should().BeTrue();
        good.PiecesPerPack.Should().Be(12, "12 подходит");
        good.PackNeedsSize.Should().BeFalse();
    }

    [Fact]
    public void Packs_mode_keeps_a_fractional_default_size()
    {
        var l = Unknown();
        ReceivingPackDefaults.ApplyToNew(l, ReceivingPackDefaultMode.Packs, 1.5).Should().BeTrue();
        l.PiecesPerPack.Should().Be(1.5, "для обычных упаковок дробное число — как раньше");
    }

    // ------------------------------------------------------------------ Л-10: товар склада

    [Fact]
    public void PackSale_over_a_warehouse_product_is_reported_as_skipped_existing_product()
    {
        ReceivingPackDefaults.IsSkippedExistingProduct(Existing(), ReceivingPackDefaultMode.PackSale).Should().BeTrue("панель скажет «включите в самой строке»");
        ReceivingPackDefaults.ApplyToNew(Existing(), ReceivingPackDefaultMode.PackSale, 12).Should().BeFalse("и строка остаётся в штуках");
    }

    [Fact]
    public void Skipped_existing_product_is_only_about_PackSale_and_only_for_existing_products()
    {
        ReceivingPackDefaults.IsSkippedExistingProduct(Unknown(), ReceivingPackDefaultMode.PackSale).Should().BeFalse("новому товару поштучную продажу можно");
        ReceivingPackDefaults.IsSkippedExistingProduct(Existing(), ReceivingPackDefaultMode.Packs).Should().BeFalse("«Упаковками» товару склада можно");
        ReceivingPackDefaults.IsSkippedExistingProduct(Existing(), ReceivingPackDefaultMode.Pieces).Should().BeFalse();
    }

    // ------------------------------------------------------------------ У-2: «введено» и «Вернуть»

    [Fact]
    public void Lines_with_prices_and_quantity_one_count_as_entered_and_are_asked_about()
    {
        var lines = Enumerable.Range(0, 5).Select(i => Unknown("460200000" + (1000 + i), qty: 1, purchase: 20, sale: 30)).ToList();
        var (willChange, withValues) = ReceivingPackDefaults.Preview(lines, ReceivingPackDefaultMode.Packs);
        willChange.Should().Be(5);
        withValues.Should().Be(5, "раньше строки с ценами и количеством 1 пересчитывались без вопроса");
    }

    [Fact]
    public void Untouched_lines_without_prices_are_not_asked_about()
    {
        var lines = new[] { Unknown("4602000001001", qty: 1, purchase: 0, sale: 0), Unknown("4602000001002", qty: 1, purchase: 0, sale: 0) };
        ReceivingPackDefaults.Preview(lines, ReceivingPackDefaultMode.Packs).Should().Be((2, 0));
        ReceivingPackDefaults.HasEnteredValues(lines[0]).Should().BeFalse();
    }

    [Fact]
    public void ApplyToAll_can_be_undone_with_mode_and_numbers_of_every_changed_line()
    {
        var a = Unknown("4602000002001", qty: 1, purchase: 20, sale: 30);
        var b = Unknown("4602000002002", qty: 5, purchase: 10, sale: 15);
        var lines = new[] { a, b };
        var snapshot = ReceivingPackDefaults.Capture(lines, ReceivingPackDefaultMode.Packs);
        snapshot.Count.Should().Be(2);

        ReceivingPackDefaults.ApplyToAll(lines, ReceivingPackDefaultMode.Packs, 12).Applied.Should().Be(2);
        a.PackMode.Should().BeTrue();
        a.Quantity.Should().Be(12, "1 упаковка × 12 — число изменилось, и вернуть его нужно");

        ReceivingPackDefaults.Restore(snapshot).Should().Be(2);
        a.PackMode.Should().BeFalse("способ вернулся «В штуках»");
        a.Quantity.Should().Be(1, "количество как было");
        a.PurchasePrice.Should().Be(20);
        a.SalePrice.Should().Be(30);
        b.PackMode.Should().BeFalse();
        b.Quantity.Should().Be(5);
        b.PurchasePrice.Should().Be(10);
        b.SalePrice.Should().Be(15);
    }

    [Fact]
    public void Capture_remembers_only_lines_that_will_change()
    {
        var already = Unknown("4602000003001");
        already.SetPackMode(true, 6);
        var plain = Unknown("4602000003002");
        ReceivingPackDefaults.Capture(new[] { already, plain }, ReceivingPackDefaultMode.Packs).Count.Should().Be(1);
    }

    [Fact]
    public void Undo_of_PackSale_apply_returns_the_line_to_pieces_with_its_old_prices()
    {
        var a = Unknown("4602000004001", qty: 3, purchase: 20, sale: 30);
        var snapshot = ReceivingPackDefaults.Capture(new[] { a }, ReceivingPackDefaultMode.PackSale);
        ReceivingPackDefaults.ApplyToAll(new[] { a }, ReceivingPackDefaultMode.PackSale, 10);
        a.PackSale.Should().BeTrue();

        ReceivingPackDefaults.Restore(snapshot);
        a.PackSale.Should().BeFalse();
        a.PackMode.Should().BeFalse();
        a.Quantity.Should().Be(3);
        a.PurchasePrice.Should().Be(20);
        a.SalePrice.Should().Be(30);
    }
}
