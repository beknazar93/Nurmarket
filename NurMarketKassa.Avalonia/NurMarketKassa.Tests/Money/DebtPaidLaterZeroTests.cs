using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (жёсткая сверка): чек «в долг», долг по которому ПОЛНОСТЬЮ погашен позже (остаток 0) — «Погашено позже» должна быть видна
/// («Внесено 200 · Погашено позже 300 · Долг 0»), раньше строки были только при остатке &gt; 0. Копия чека в «Истории чеков» берёт решение у
/// SaleReceiptCardData.DebtTail, карточка чека (Build) — те же числа; ru / ky / uz.</summary>
[Collection("UserPreferences")]
public sealed class DebtPaidLaterZeroTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    [Fact]
    public void DebtTail_FullyRepaid_ShowsPaidLaterAndZeroDebt()
    {
        var (later, showDebt) = SaleReceiptCardData.DebtTail(500m, 200m, 0m);
        later.Should().Be(300m);
        showDebt.Should().BeTrue("«Долг 0» нужен, чтобы 200 + 300 сошлись с итогом 500");
    }

    [Fact]
    public void DebtTail_PartlyRepaid_AsBefore()
    {
        var (later, showDebt) = SaleReceiptCardData.DebtTail(1074.25m, 500m, 454.25m);
        later.Should().Be(120m);
        showDebt.Should().BeTrue();
    }

    [Fact]
    public void DebtTail_NoRepayments_DebtOnly_AsBefore()
    {
        var (later, showDebt) = SaleReceiptCardData.DebtTail(1074.25m, 500m, 574.25m);
        later.Should().BeNull();
        showDebt.Should().BeTrue();
    }

    [Fact]
    public void DebtTail_PaidInFullAtSale_NoRows()
    {
        // внесено всё при продаже, остаток 0, оплат долга не было — «Долг 0» не нужен
        var (later, showDebt) = SaleReceiptCardData.DebtTail(500m, 500m, 0m);
        later.Should().BeNull();
        showDebt.Should().BeFalse();
    }

    [Fact]
    public void DebtTail_PaidAtSaleUnknown_NoPaidLaterRow()
    {
        var (later, showDebt) = SaleReceiptCardData.DebtTail(500m, null, 0m);
        later.Should().BeNull("не знаем, сколько внесено при продаже — выдумывать «погашено позже» нельзя");
        showDebt.Should().BeFalse();
    }

    [Theory]
    [InlineData(AppLanguage.Russian, "Внесено", "Погашено позже (оплаты долга)")]
    [InlineData(AppLanguage.Kyrgyz, "Киргизилди", "Кийин төлөндү (карыз төлөмдөрү)")]
    [InlineData(AppLanguage.Uzbek, "Kiritildi", "Keyin to'landi (qarz to'lovlari)")]
    public void ReceiptCard_DebtFullyRepaid_ShowsPaidLaterRow(AppLanguage lang, string paidIn, string paidLaterLabel)
    {
        UserPreferences.Instance.Language = lang;
        using var doc = JsonDocument.Parse("""
            {"id":"sale-zero-1","status":"debt","payment_method":"debt","cash_received":"200.00","remaining_debt":"0.00","total_amount":"500.00","items":[]}
            """);
        var card = SaleReceiptCardData.Build(doc.RootElement, "000007", 0m, 500m);

        card.Payment.Select(r => r.Label).Should().ContainInOrder(paidIn, paidLaterLabel);
        card.Payment.Single(r => r.Label == paidLaterLabel).Value.Should().Be("300");
        card.Payment.Single(r => r.Label == paidIn).Value.Should().Be("200");
        // остаток 0 показан («Остаток долга сейчас 0»), долг «Погашено позже» не дублируется
        card.Payment.Count(r => r.Label == paidLaterLabel).Should().Be(1);
    }
}
