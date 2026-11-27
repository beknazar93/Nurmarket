using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-26 (владелец: «после возврата товара поставщику долг от товара у поставщика не минусуется»): сервер уменьшает долг только при compensation=debt_offset (зачёт),
/// а по умолчанию окно ставило «cash» (деньги в кассу) — долг не менялся. Правила выбора по умолчанию и подсказки (SupplierReturnOffset).</summary>
public sealed class SupplierReturnOffsetTests
{
    [Fact]
    public void Default_is_offset_when_the_supplier_has_a_debt_and_cash_otherwise()
    {
        SupplierReturnOffset.DefaultCompensation(1500).Should().Be("debt_offset", "есть долг — по умолчанию зачёт в долг");
        SupplierReturnOffset.DefaultCompensation(0.01).Should().Be("debt_offset");
        SupplierReturnOffset.DefaultCompensation(0).Should().Be("cash", "долга нет — деньги в кассу");
        SupplierReturnOffset.DefaultCompensation(0.004).Should().Be("cash", "меньше полкопейки — долга нет");
        SupplierReturnOffset.DefaultCompensation(null).Should().Be("cash", "долг неизвестен — как раньше");
    }

    [Fact]
    public void Judge_explains_cash_with_debt_no_debt_and_return_over_debt()
    {
        SupplierReturnOffset.Judge(1000, 300, "debt_offset").Should().Be(SupplierReturnOffset.Advice.Fits);
        SupplierReturnOffset.Judge(1000, 1000, "debt_offset").Should().Be(SupplierReturnOffset.Advice.Fits, "возврат ровно в долг — укладывается");
        SupplierReturnOffset.Judge(1000, 1200, "debt_offset").Should().Be(SupplierReturnOffset.Advice.ExceedsDebt, "возврат больше долга — подсказка и вопрос");
        SupplierReturnOffset.Judge(0, 300, "debt_offset").Should().Be(SupplierReturnOffset.Advice.NoDebt, "долга нет — зачесть не в чем");
        SupplierReturnOffset.Judge(1000, 300, "cash").Should().Be(SupplierReturnOffset.Advice.CashWhileDebt, "деньги в кассу при долге — напоминание, что долг не изменится");
        SupplierReturnOffset.Judge(1000, 0, "cash").Should().Be(SupplierReturnOffset.Advice.CashWhileDebt, "вид денег выбирают раньше количества — напоминание уже при нулевой сумме");
        SupplierReturnOffset.Judge(0, 300, "cash").Should().Be(SupplierReturnOffset.Advice.Nothing);
        SupplierReturnOffset.Judge(1000, 300, "none").Should().Be(SupplierReturnOffset.Advice.Nothing);
        SupplierReturnOffset.Judge(null, 300, "debt_offset").Should().Be(SupplierReturnOffset.Advice.Nothing, "долг неизвестен — ничего не утверждаем");
    }

    [Fact]
    public void Only_a_non_covering_offset_asks_for_confirmation()
    {
        SupplierReturnOffset.NeedsConfirm(SupplierReturnOffset.Advice.NoDebt).Should().BeTrue();
        SupplierReturnOffset.NeedsConfirm(SupplierReturnOffset.Advice.ExceedsDebt).Should().BeTrue();
        SupplierReturnOffset.NeedsConfirm(SupplierReturnOffset.Advice.Fits).Should().BeFalse();
        SupplierReturnOffset.NeedsConfirm(SupplierReturnOffset.Advice.CashWhileDebt).Should().BeFalse("деньги в кассу — осознанный выбор");
        SupplierReturnOffset.NeedsConfirm(SupplierReturnOffset.Advice.Nothing).Should().BeFalse();
    }

    [Fact]
    public void Expected_debt_after_an_offset_goes_down_by_the_return_but_not_below_zero()
    {
        SupplierReturnOffset.ExpectedAfter(1000, 300, "debt_offset").Should().Be(700);
        SupplierReturnOffset.ExpectedAfter(1000, 1200, "debt_offset").Should().Be(0, "переплаты у поставщика не бывает");
        SupplierReturnOffset.ExpectedAfter(0.3, 0.1, "debt_offset").Should().Be(0.2, "без шума double");
        SupplierReturnOffset.ExpectedAfter(1000, 300, "cash").Should().Be(1000, "деньги в кассу — долг прежний");
        SupplierReturnOffset.ExpectedAfter(1000, 300, "none").Should().Be(1000);
        SupplierReturnOffset.ExpectedAfter(null, 300, "debt_offset").Should().BeNull();
    }

    [Fact]
    public void Classify_tells_reduced_from_not_reduced_after_the_return()
    {
        SupplierReturnOffset.Classify(1000, 700, "debt_offset").Should().Be(SupplierReturnOffset.Outcome.Reduced, "было 1000, стало 700 — долг минусовался");
        SupplierReturnOffset.Classify(1000, 1000, "debt_offset").Should().Be(SupplierReturnOffset.Outcome.NotReduced, "зачёт, а долг тот же — предупреждение");
        SupplierReturnOffset.Classify(1000, 1000, "cash").Should().Be(SupplierReturnOffset.Outcome.Unchanged, "деньги в кассу — долг и не должен меняться");
        SupplierReturnOffset.Classify(0, 0, "debt_offset").Should().Be(SupplierReturnOffset.Outcome.Unchanged, "зачесть было не в чем");
        SupplierReturnOffset.Classify(null, 700, "debt_offset").Should().Be(SupplierReturnOffset.Outcome.Unknown);
        SupplierReturnOffset.Classify(1000, null, "debt_offset").Should().Be(SupplierReturnOffset.Outcome.Unknown);
    }
}
