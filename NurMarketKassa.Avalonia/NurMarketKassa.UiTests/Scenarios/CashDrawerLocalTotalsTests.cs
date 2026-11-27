using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-26 (владелец: «при внесении и изъятии на аналитике покажется только изъятие, а внесение исчезает»): журнал кассы (cash_history.json)
/// хранит внесение и изъятие «Прочее 1 000» одной смены ОБЕИМИ записями; суммы смены, суммы по статьям (окно закрытия) и «Касса:» в шапке видят обе, итого 0.
/// Журнал — во временной папке данных тестов.</summary>
[Collection("UiScenarios")]
public sealed class CashDrawerLocalTotalsTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public void Dispose() => PosApp.ActiveShiftId = _savedShift;

    private static CashOperationModel Op(string type, decimal amount) => new()
    {
        Type = type,
        Kind = CashOperationModel.ResolveKind(type),
        Amount = amount,
        Cashier = "тест",
        Reason = "Прочее",
        Comment = "Прочее — тест",
        ArticleKey = CashArticles.OtherKey,
        ArticleRu = "Прочее",
        ArticleComment = "тест",
    };

    [Fact]
    public void SameShift_DepositAndWithdrawal_Other1000_LocalTotalsKeepBoth_NetZero()
    {
        var shift = Guid.NewGuid().ToString();
        PosApp.ActiveShiftId = shift;
        var deposit = Op("Внесение", 1000m);
        var withdrawal = Op("Изъятие", 1000m);

        ShiftCashOperationsStore.Append(deposit);
        ShiftCashOperationsStore.Append(withdrawal);

        var (deposits, withdrawals) = ShiftCashOperationsStore.SumsForShift(shift);
        deposits.Should().Be(1000m, "внесение не должно пропасть из сумм смены");
        withdrawals.Should().Be(1000m);
        ShiftCashOperationsStore.ArticleSumsForShift(shift, withdrawal: false).Should().ContainSingle().Which.Amount.Should().Be(1000m);
        ShiftCashOperationsStore.ArticleSumsForShift(shift, withdrawal: true).Should().ContainSingle().Which.Amount.Should().Be(1000m,
            "«Прочее» внесением и «Прочее» изъятием считаются раздельно, не схлопываются");
        ShiftCashOperationsStore.NetForShift(shift).Should().Be(0m, "внесено 1 000 и изъято 1 000 — «Касса:» в шапке не меняется");
        ShiftCashOperationsStore.LoadAll().Where(o => o.Id == deposit.Id || o.Id == withdrawal.Id).Should().HaveCount(2);
    }
}
