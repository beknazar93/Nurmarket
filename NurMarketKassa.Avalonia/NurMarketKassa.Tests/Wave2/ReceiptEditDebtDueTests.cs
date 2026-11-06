using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Л4-22 — долг по доприходу при правке накладной сохраняет срок исходной накладной (если он впереди), иначе 30 дней от даты дохода.</summary>
public sealed class ReceiptEditDebtDueTests
{
    private static readonly DateTime At = new(2026, 11, 6, 15, 30, 0);

    [Fact]
    public void NoOriginalDue_ThirtyDaysFromReceipt()
    {
        var (due, days) = ReceiptEditService.DebtDue(At, null);
        due.Should().Be(new DateTime(2026, 12, 6));
        days.Should().Be(30);
    }

    [Fact]
    public void OriginalDueInFuture_IsKept()
    {
        var (due, days) = ReceiptEditService.DebtDue(At, new DateTime(2026, 11, 20, 9, 0, 0));
        due.Should().Be(new DateTime(2026, 11, 20));
        days.Should().Be(14);
    }

    [Theory]
    [InlineData(2026, 11, 6)]   // срок сегодня
    [InlineData(2026, 10, 1)]   // срок уже прошёл
    public void OriginalDueNotInFuture_FallsBackToThirtyDays(int y, int m, int d)
    {
        var (due, days) = ReceiptEditService.DebtDue(At, new DateTime(y, m, d));
        due.Should().Be(new DateTime(2026, 12, 6));
        days.Should().Be(30);
    }
}
