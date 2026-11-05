using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-05: исправления по «Живой приёмке волн 0–3» — автонаценка не снижает цену продажи существующего товара, месяц выписки по датам строк,
/// поставщик в описании платежа выбирается по самому длинному названию. Без сети и без боевых данных.</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class AcceptanceFixesTests
{
    private static ReceivingLineVm ExistingLine(double originalSale, double purchase)
    {
        var line = new ReceivingLineVm { OriginalSalePrice = originalSale, OriginalPurchasePrice = purchase };
        line.ProductId = "p1";
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 1;
        line.PurchasePrice = purchase;
        line.SalePrice = originalSale;
        return line;
    }

    private static T WithMarkup<T>(double percent, SaleRounding rounding, Func<T> body)
    {
        var previous = ReceivingMarkupSettingsStore.Current.Clone();
        try
        {
            ReceivingMarkupSettingsStore.Override(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = percent, Rounding = rounding });
            return body();
        }
        finally
        {
            ReceivingMarkupSettingsStore.Override(previous);
        }
    }

    [Fact]
    public void Auto_markup_does_not_lower_the_price_of_an_existing_product()
    {
        // закупка 0,125, цена 1,00, общая наценка 20 % с округлением до 0,5: автоцена 0,5 — ниже прежней 1,00
        var price = WithMarkup(20, SaleRounding.Half, () =>
        {
            var line = ExistingLine(1.00, 0.125);
            line.EnableAutoSale();
            return line.SalePrice;
        });
        price.Should().Be(1.00, "автонаценка только повышает цену существующего товара");
    }

    [Fact]
    public void Auto_markup_still_raises_the_price_of_an_existing_product()
    {
        var price = WithMarkup(30, SaleRounding.WholeUp, () =>
        {
            var line = ExistingLine(100, 80);
            line.SetPurchaseText("100"); // закупка выросла: 100 × 1,3 = 130 > 100
            line.EnableAutoSale();
            return line.SalePrice;
        });
        price.Should().Be(130);
    }

    [Fact]
    public void Return_to_auto_may_lower_the_price_on_explicit_request()
    {
        var price = WithMarkup(20, SaleRounding.Half, () =>
        {
            var line = ExistingLine(1.00, 0.125);
            line.EnableAutoSale();
            line.ReturnToAuto(); // «Как у всех» — явное действие
            return line.SalePrice;
        });
        price.Should().Be(0.5);
    }

    [Fact]
    public void Statement_month_is_the_one_with_most_transactions()
    {
        var lines = new[]
        {
            new BankLine(1, new DateTime(2026, 10, 30), 100, "a"),
            new BankLine(2, new DateTime(2026, 11, 2), 100, "b"),
            new BankLine(3, new DateTime(2026, 11, 3), 100, "c"),
            new BankLine(4, new DateTime(2026, 11, 20), -50, "d"),
        };
        BankReconciler.DominantMonth(lines).Should().Be(new DateTime(2026, 11, 1));
        BankReconciler.DominantMonth(Array.Empty<BankLine>()).Should().BeNull();
    }

    [Fact]
    public void Supplier_in_payment_description_is_matched_by_the_longest_name()
    {
        var bank = new[] { new BankLine(1, new DateTime(2026, 10, 5), -3.60, "Оплата поставщику ТЕСТ П26 за товар") };
        var result = BankReconciler.Reconcile(bank, Array.Empty<ShiftCashless>(), Array.Empty<SupplierOutgoing>(), new[] { "Тест", "ТЕСТ П26" }, Array.Empty<BankRule>());
        result.Lines.Should().ContainSingle();
        result.Lines[0].Supplier.Should().Be("ТЕСТ П26");
    }
}
