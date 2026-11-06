using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06: «Приём товара» — цены по умолчанию (закупка: поставщик → карточка → 0, продажа из каталога, наценка из двух цен), ручной ввод не затирается
/// при смене поставщика, общая наценка не трогает цену существующего товара, вопрос «Поставщик не выбран». Без сети и без боевых данных.</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class ReceivingDefaultsTests
{
    private static ReceivingLineVm Existing(double cardSale, double cardPurchase)
    {
        var line = new ReceivingLineVm { OriginalSalePrice = cardSale, OriginalPurchasePrice = cardPurchase };
        line.ProductId = "p1";
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 1;
        line.PurchasePrice = cardPurchase;
        line.SalePrice = cardSale;
        return line;
    }

    private static ReceivingLineVm NewProduct()
    {
        var line = new ReceivingLineVm { Barcode = "4600000000001", ProductName = "Новый" };
        line.Source = ReceivingSource.Unknown;
        line.Quantity = 1;
        return line;
    }

    private static T WithMarkup<T>(double percent, Func<T> body)
    {
        var previous = ReceivingMarkupSettingsStore.Current.Clone();
        try
        {
            ReceivingMarkupSettingsStore.Override(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = percent, Rounding = SaleRounding.WholeUp });
            return body();
        }
        finally
        {
            ReceivingMarkupSettingsStore.Override(previous);
        }
    }

    private static PurchaseReceivingService.HistoryEntry Hist(string? supplier, double purchase, DateTime at, double? sale = null) =>
        new(at, 1, "шт", purchase, sale, supplier, null, "Касса", null);

    private static readonly DateTime Day1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Day2 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    // ------------------------------------------------------------------ закупка по умолчанию

    [Fact]
    public void Purchase_comes_from_the_selected_suppliers_latest_history_and_markup_is_recomputed()
    {
        WithMarkup(20, () =>
        {
            var line = Existing(cardSale: 100, cardPurchase: 80);
            var history = new[] { Hist("Поставщик А", 85, Day1), Hist("Поставщик А", 90, Day2), Hist("Поставщик Б", 70, Day2.AddDays(1)) };

            ReceivingDefaults.ApplyPurchase(line, ReceivingDefaults.FromHistory(history, "поставщик а")).Should().BeTrue();
            line.EnableReceivingPricing();

            line.PurchasePrice.Should().Be(90);
            line.PurchaseSource.Should().Be(PurchaseDefaultSource.SupplierHistory);
            line.SalePrice.Should().Be(100, "цена продажи — из каталога, закупка её не меняет");
            line.MarkupPercent.Should().BeApproximately(11.11, 0.005, "наценка считается из двух цен");
            return 0;
        });
    }

    [Fact]
    public void Purchase_falls_back_to_the_card_then_to_zero()
    {
        WithMarkup(20, () =>
        {
            var withCard = Existing(100, 80);
            ReceivingDefaults.ApplyPurchase(withCard, ReceivingDefaults.FromHistory(new[] { Hist("Другой", 70, Day2) }, "Поставщик А"));
            withCard.PurchasePrice.Should().Be(80, "у этого поставщика истории нет — закупка из карточки");
            withCard.PurchaseSource.Should().Be(PurchaseDefaultSource.Card);

            var nothing = Existing(100, 0);
            ReceivingDefaults.ApplyPurchase(nothing, null);
            nothing.PurchasePrice.Should().Be(0);
            nothing.PurchaseSource.Should().Be(PurchaseDefaultSource.None);
            return 0;
        });
    }

    [Fact]
    public void New_product_gets_no_history_price_and_is_priced_by_the_common_markup()
    {
        WithMarkup(20, () =>
        {
            var line = NewProduct();
            ReceivingDefaults.ApplyPurchase(line, new ReceivingDefaults.SupplierPrice(55, Day2)).Should().BeFalse("у нового товара истории нет");
            line.PurchasePrice.Should().Be(0);

            line.EnableReceivingPricing();
            line.SaleMode.Should().Be(SalePriceMode.Auto);
            line.SaleStatusText.Should().BeEmpty("цены ещё нет — подпись «авто» была бы неправдой");

            line.SetPurchaseText("100");
            line.SalePrice.Should().Be(120);
            line.SaleStatusText.Should().Contain("авто").And.Contain("20");
            return 0;
        });
    }

    [Fact]
    public void Manual_purchase_is_not_overwritten_when_the_supplier_changes()
    {
        WithMarkup(20, () =>
        {
            var line = Existing(100, 80);
            var supplierA = new ReceivingDefaults.SupplierPrice(90, Day2);
            var supplierB = new ReceivingDefaults.SupplierPrice(70, Day2);

            ReceivingDefaults.ApplyPurchase(line, supplierA);
            line.PurchasePrice.Should().Be(90);
            ReceivingDefaults.ApplyPurchase(line, supplierB); // пока не правили руками — цена следует за поставщиком
            line.PurchasePrice.Should().Be(70);
            ReceivingDefaults.ApplyPurchase(line, null); // у нового поставщика истории нет — карточка
            line.PurchasePrice.Should().Be(80);

            line.SetPurchaseText("95"); // кассир вписал свою
            line.PurchaseIsManual.Should().BeTrue();
            ReceivingDefaults.ApplyPurchase(line, supplierB).Should().BeFalse();
            line.PurchasePrice.Should().Be(95, "ручной ввод не затирается");
            return 0;
        });
    }

    [Fact]
    public void Locked_purchase_from_an_order_is_kept()
    {
        var line = Existing(100, 80);
        line.PurchasePrice = 77;
        line.LockPurchase();
        ReceivingDefaults.ApplyPurchase(line, new ReceivingDefaults.SupplierPrice(90, Day2)).Should().BeFalse();
        line.PurchasePrice.Should().Be(77);
    }

    // ------------------------------------------------------------------ общая наценка

    [Fact]
    public void Common_markup_does_not_change_the_price_of_an_existing_product()
    {
        WithMarkup(20, () =>
        {
            var line = Existing(cardSale: 50, cardPurchase: 40);
            line.EnableReceivingPricing();

            line.SaleMode.Should().Be(SalePriceMode.Card);
            line.SalePrice.Should().Be(50, "цена из карточки, а не 40 × 1,2 = 48");
            line.SaleStatusText.Should().Contain("карточки").And.Contain("50").And.NotContain("авто");

            line.SetPurchaseText("45"); // закупка выросла — продажа молча не меняется (подсказки цены — отдельно)
            line.SalePrice.Should().Be(50);
            ReceivingPricing.ApplyAutoToAll(new[] { line }).Should().Be(0, "смена общего процента строку «из карточки» не трогает");
            line.SalePrice.Should().Be(50);
            return 0;
        });
    }

    [Fact]
    public void Existing_product_without_card_price_uses_the_previous_purchase_sale_price_else_the_common_markup()
    {
        WithMarkup(20, () =>
        {
            var viaHistory = Existing(cardSale: 0, cardPurchase: 40);
            viaHistory.EnableReceivingPricing(historySale: 55);
            viaHistory.SalePrice.Should().Be(55);
            viaHistory.SaleMode.Should().Be(SalePriceMode.Card);
            viaHistory.SaleStatusText.Should().Contain("прошлой закупке").And.Contain("55");

            var unknown = Existing(cardSale: 0, cardPurchase: 40);
            unknown.EnableReceivingPricing();
            unknown.SaleMode.Should().Be(SalePriceMode.Auto);
            unknown.SalePrice.Should().Be(48, "цены нигде нет — по общей наценке");
            unknown.SaleStatusText.Should().Contain("авто");
            return 0;
        });
    }

    [Fact]
    public void Apply_to_all_recalculates_card_and_auto_lines_but_skips_hand_edited_ones()
    {
        WithMarkup(20, () =>
        {
            var card = Existing(50, 40);
            card.EnableReceivingPricing();
            var fresh = NewProduct();
            fresh.EnableReceivingPricing();
            fresh.SetPurchaseText("100"); // 120
            var manual = Existing(80, 60);
            manual.EnableReceivingPricing();
            manual.SetSaleText("99"); // вручную
            var noPurchase = Existing(30, 0);
            noPurchase.EnableReceivingPricing();

            var changed = ReceivingPricing.ApplyCommonMarkupToAll(new[] { card, fresh, manual, noPurchase }, out var skipped);

            changed.Should().Be(1, "изменилась только «из карточки»: 40 × 1,2 = 48; новая уже по наценке, у остальных — ручная и нулевая закупка");
            skipped.Should().Be(1);
            card.SalePrice.Should().Be(48);
            card.SaleStatusText.Should().Contain("авто");
            manual.SalePrice.Should().Be(99);
            noPurchase.SalePrice.Should().Be(30, "считать не от чего — прежняя цена остаётся");
            noPurchase.SaleStatusText.Should().NotContain("авто");
            return 0;
        });
    }

    // ------------------------------------------------------------------ история

    [Fact]
    public void History_lookup_ignores_other_suppliers_empty_names_and_picks_the_newest()
    {
        var history = new[] { Hist("Поставщик А", 85, Day1), Hist("Поставщик А ", 90, Day2), Hist(null, 10, Day2.AddDays(5)), Hist("Другой", 11, Day2.AddDays(6)) };

        ReceivingDefaults.FromHistory(history, "поставщик а")!.Value.Price.Should().Be(90);
        ReceivingDefaults.FromHistory(history, null).Should().BeNull("приход без поставщика не считается ценой поставщика");
        ReceivingDefaults.FromHistory(history, "Третий").Should().BeNull();

        var local = new ReceivingDefaults.SupplierPrice(90, Day2);
        var server = new ReceivingDefaults.SupplierPrice(95, Day2.AddDays(3));
        ReceivingDefaults.Newest(local, server)!.Value.Price.Should().Be(95);
        ReceivingDefaults.Newest(local, null)!.Value.Price.Should().Be(90);
        ReceivingDefaults.LastSalePrice(new[] { Hist("А", 1, Day1, sale: 40), Hist("А", 1, Day2, sale: 55), Hist("А", 1, Day2.AddDays(1)) }).Should().Be(55);
    }

    // ------------------------------------------------------------------ вопрос «Поставщик не выбран»

    [Fact]
    public void Supplier_question_logic_follows_the_three_buttons()
    {
        ReceivingSupplierRule.Resolve(supplierSelected: true, null).Should().Be(ReceivingSupplierRule.Step.PostWithSupplier, "с поставщиком вопроса нет");
        ReceivingSupplierRule.Resolve(false, null).Should().Be(ReceivingSupplierRule.Step.Ask);
        ReceivingSupplierRule.Resolve(false, NoSupplierChoice.ChooseSupplier).Should().Be(ReceivingSupplierRule.Step.OpenSupplierPicker);
        ReceivingSupplierRule.Resolve(false, NoSupplierChoice.WithoutSupplier).Should().Be(ReceivingSupplierRule.Step.PostWithoutSupplier);
        ReceivingSupplierRule.Resolve(false, NoSupplierChoice.Cancel).Should().Be(ReceivingSupplierRule.Step.Stop);

        ReceivingSupplierRule.DebtAllowed(supplierSelected: true).Should().BeTrue();
        ReceivingSupplierRule.DebtAllowed(supplierSelected: false).Should().BeFalse("долг поставщику без поставщика невозможен");
    }
}
