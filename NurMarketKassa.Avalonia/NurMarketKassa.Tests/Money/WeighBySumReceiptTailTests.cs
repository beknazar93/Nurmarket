using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (Умар 16:20): receipt/ и pos/sales/{id}/ отдают line_total по правилу чека (без хвостов float; сумма строк = итог). Живые чеки «ТЕСТ Стандарт» QA-ВЕС-ПО-СУММЕ
/// (весовой товар «по сумме», 1,409 кг × 1100 с копейкой скидки и 0,909 кг × 1100,12) — копия чека не должна показывать «Округление весовых −0,01» и 1000,01.
/// Обходы под старый хвост (CartTotalsCalculator.SaleRoundingAdjustment, строка «Округление весовых») оставлены нетронутыми: их срабатывание здесь = регрессия сервера.</summary>
[Collection("UserPreferences")]
public sealed class WeighBySumReceiptTailTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static string Plain(string text) => text.Replace(" ", "").Replace(" ", "").Replace(" ", "");

    [Theory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public void Receipt_copy_of_weigh_by_sum_sale_has_no_rounding_row_and_exact_totals(AppLanguage lang)
    {
        UserPreferences.Instance.Language = lang;
        // продажа № 4: 0,909 × 1100,12 «по сумме 1000» — сервер отдаёт line_total 1000.0 (раньше 1000.01 в хвосте)
        using var exact = JsonDocument.Parse("""
            {"id":"w-4","status":"paid","subtotal":"1000.00","discount_total":"0.00","total":"1000.00","payment_method":"cash","cash_received":"1000.00","change":"0.00",
             "items":[{"product_name":"QA-ВЕС-ПО-СУММЕ","quantity":"0.909","price":"1100.12","discount":"0.00","line_discount":"0.00","line_total":1000.0,"is_weight":true,"unit":"кг"}]}
            """);
        var card = SaleReceiptCardData.Build(exact.RootElement, "000004", 0m, 1000m);

        card.BeforeTotal.Should().BeEmpty("сумма строк = итог — ни «Округление весовых», ни «Сумма без скидки»");
        Plain(card.Lines.Single().Total).Should().Be("1000", "1000, а не 1000,01");
        Plain(card.TotalText).Should().StartWith("1000").And.NotContain("1000,01");

        // продажа № 6: 1,409 × 1100 = 1549,90, скидка 0,02, итог 1549,88 — line_total уже после скидки, сумма строк = итог
        using var discounted = JsonDocument.Parse("""
            {"id":"w-6","status":"paid","subtotal":"1549.90","discount_total":"0.02","total":"1549.88","payment_method":"cash","cash_received":"5000.00","change":"3450.12",
             "items":[{"product_name":"QA-ВЕС-ПО-СУММЕ","quantity":"1.409","price":"1100.00","discount":"0.02","line_discount":"0.02","line_total":1549.88,"is_weight":true,"unit":"кг"}]}
            """);
        var withDiscount = SaleReceiptCardData.Build(discounted.RootElement, "000006", 0.02m, 1549.88m);

        withDiscount.BeforeTotal.Select(r => r.Label).Should().NotContain(CartTotalsCalculator.RoundingLabel(true)).And.NotContain(CartTotalsCalculator.RoundingLabel(false));
        Plain(withDiscount.Lines.Single().Total).Should().Be("1549,88");
        Plain(withDiscount.TotalText).Should().StartWith("1549,88");
    }
}
