using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave13;

/// <summary>2026-11-06 (Л4-19, решение владельца): PIN владельца при запуске на ПК с кассой — ОПЦИЯ, по умолчанию выключена. Прежнее правило «только программа владельца и только на ПК с кассой»
/// (двухаргументный ShouldAskPin) сохранено.</summary>
public sealed class OwnerPinOptionTests
{
    [Fact]
    public void Pin_is_not_asked_unless_the_owner_turned_the_option_on()
    {
        OwnerStartupGate.ShouldAskPin(isOwnerMode: true, kassaInstalledHere: true, enabledByOwner: false).Should().BeFalse("по умолчанию выключено");
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]   // ПК без кассы — ничего не меняется
    [InlineData(false, true, true, false)]   // касса, а не программа владельца
    [InlineData(false, false, true, false)]
    public void Pin_is_asked_only_for_the_owner_program_on_a_kassa_pc_when_enabled(bool owner, bool kassa, bool enabled, bool expected)
    {
        OwnerStartupGate.ShouldAskPin(owner, kassa, enabled).Should().Be(expected);
    }

    [Fact]
    public void Previous_two_argument_rule_is_unchanged()
    {
        OwnerStartupGate.ShouldAskPin(isOwnerMode: true, kassaInstalledHere: true).Should().BeTrue();
        OwnerStartupGate.ShouldAskPin(isOwnerMode: true, kassaInstalledHere: false).Should().BeFalse();
    }
}
