using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (Умар 16:20): GET pos/sales/{id}/ отдаёт debt_payments[] = {id, kind, amount, method, date, created_at, by} — оплаты долга по чеку.
/// «Погашено позже» берётся из суммы этих оплат (SaleReceiptCardData.DebtTail с serverPaidLater), под ней — список «дата · способ — сумма» (ru / ky / uz);
/// нет поля — прежний расчёт «итог − внесено − остаток».</summary>
[Collection("UserPreferences")]
public sealed class DebtPaymentsFromServerTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private const string SaleWithPayments = """
        {"id":"sale-dp-1","status":"debt","payment_method":"debt","cash_received":"200.00","remaining_debt":"0.00","total_amount":"500.00","items":[],
         "debt_payments":[
           {"id":"p1","kind":"pay","amount":"100.00","method":"transfer","date":"2026-11-26","created_at":"2026-11-26T08:33:46.858175+00:00","by":"Стандарт Тест"},
           {"id":"p2","kind":"pay","amount":"150.00","method":"cash","date":"2026-11-27","created_at":"2026-11-27T08:34:29+00:00","by":"Стандарт Тест"},
           {"id":"p3","kind":"pay","amount":"50.00","method":"cash","date":"2026-11-28","created_at":"2026-11-28T08:34:29+00:00","by":"Стандарт Тест"}]}
        """;

    [Fact]
    public void DebtPayments_reads_amount_method_and_date_and_skips_other_kinds()
    {
        using var doc = JsonDocument.Parse("""
            {"debt_payments":[{"kind":"pay","amount":"40.00","method":"cash","date":"2026-11-26"},{"kind":"reversal","amount":"40.00","method":"cash","date":"2026-11-26"},{"kind":"pay","amount":"0.00","method":"cash"}]}
            """);

        var list = SaleReceiptCardData.DebtPayments(doc.RootElement)!;

        list.Should().ContainSingle();
        list[0].Amount.Should().Be(40m);
        list[0].Method.Should().Be("cash");
        list[0].Date.Should().Be(new DateTime(2026, 11, 26));
    }

    [Fact]
    public void DebtPayments_is_null_without_the_field_so_the_old_calculation_stays()
    {
        using var doc = JsonDocument.Parse("""{"id":"s","payment_method":"debt"}""");
        SaleReceiptCardData.DebtPayments(doc.RootElement).Should().BeNull();
    }

    [Fact]
    public void DebtTail_server_sum_wins_over_the_calculation_and_zero_means_nothing_repaid()
    {
        // расчёт дал бы 300 (500 − 200 − 0), сервер говорит 250 — верим серверу
        SaleReceiptCardData.DebtTail(500m, 200m, 0m, 250m).PaidLater.Should().Be(250m);
        // сервер: оплат долга не было — строки «Погашено позже» нет, как бы ни считалась разница
        SaleReceiptCardData.DebtTail(500m, 200m, 100m, 0m).PaidLater.Should().BeNull();
        // поля нет — прежний расчёт
        SaleReceiptCardData.DebtTail(500m, 200m, 0m, null).PaidLater.Should().Be(300m);
    }

    [Theory]
    [InlineData(AppLanguage.Russian, "Погашено позже (оплаты долга)", "26.11.2026 · безналом", "27.11.2026 · наличными")]
    [InlineData(AppLanguage.Kyrgyz, "Кийин төлөндү (карыз төлөмдөрү)", "26.11.2026 · накталайсыз", "27.11.2026 · накталай")]
    [InlineData(AppLanguage.Uzbek, "Keyin to'landi (qarz to'lovlari)", "26.11.2026 · naqdsiz", "27.11.2026 · naqd")]
    public void ReceiptCard_lists_server_debt_payments_under_paid_later(AppLanguage lang, string paidLaterLabel, string firstRow, string secondRow)
    {
        UserPreferences.Instance.Language = lang;
        using var doc = JsonDocument.Parse(SaleWithPayments);

        var card = SaleReceiptCardData.Build(doc.RootElement, "000009", 0m, 500m);

        var labels = card.Payment.Select(r => r.Label).ToList();
        labels.Should().ContainInOrder(paidLaterLabel, firstRow, secondRow);
        card.Payment.Single(r => r.Label == paidLaterLabel).Value.Should().Be("300", "100 + 150 + 50 из debt_payments");
        card.Payment.Single(r => r.Label == firstRow).Value.Should().Be("100");
        card.Payment.Single(r => r.Label == secondRow).Value.Should().Be("150");
    }

    [Fact]
    public void ReceiptCard_without_debt_payments_keeps_the_old_paid_later_row_and_no_list()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        using var doc = JsonDocument.Parse("""
            {"id":"sale-dp-2","status":"debt","payment_method":"debt","cash_received":"200.00","remaining_debt":"0.00","total_amount":"500.00","items":[]}
            """);

        var card = SaleReceiptCardData.Build(doc.RootElement, "000010", 0m, 500m);

        card.Payment.Single(r => r.Label == "Погашено позже (оплаты долга)").Value.Should().Be("300");
        card.Payment.Any(r => r.Label.Contains(" · ")).Should().BeFalse("списка оплат нет — поля у сервера не было");
    }
}
