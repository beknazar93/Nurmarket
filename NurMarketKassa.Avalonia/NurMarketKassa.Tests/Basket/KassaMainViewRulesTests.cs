using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В7, «Касса — вид главной»): чистая логика вида — пилюля смены (Д4-01), «Вернуть (5)» (Д4-40), автосписок каталога (Д4-36), признак «одна строка» (Д4-03), имя корзины строки (ДС-2).</summary>
public sealed class KassaMainViewRulesTests
{
    [Fact]
    public void ShiftPill_ShowsOpenTime_OrJustWord()
    {
        KassaMainViewRules.ShiftPillLabel(new DateTime(2026, 11, 6, 9, 4, 0)).Should().EndWith("09:04");
        var noTime = KassaMainViewRules.ShiftPillLabel(null);
        noTime.Should().NotBeNullOrWhiteSpace();
        noTime.Should().NotContain(":", "время открытия неизвестно — пилюля без часов");
        KassaMainViewRules.ShiftPillTooltip(new DateTime(2026, 11, 6, 9, 4, 0)).Should().Contain("09:04");
        KassaMainViewRules.ShiftPillTooltip(null).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(100, 5)]
    [InlineData(1000, 4)]
    [InlineData(4200, 1)]
    [InlineData(5000, 0)]
    [InlineData(9000, 0)]
    public void UndoSecondsLeft_CountsDownRoundedUp_NeverNegative(int elapsedMs, int expected)
    {
        var shown = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        KassaMainViewRules.UndoSecondsLeft(shown, TimeSpan.FromSeconds(5), shown.AddMilliseconds(elapsedMs)).Should().Be(expected);
    }

    [Fact]
    public void UndoButtonLabel_AddsSeconds_UntilZero()
    {
        KassaMainViewRules.UndoButtonLabel("Вернуть", 5).Should().Be("Вернуть (5)");
        KassaMainViewRules.UndoButtonLabel("Вернуть", 0).Should().Be("Вернуть");
    }

    [Fact]
    public void AutoList_TurnsOnBelow1280_AndOffOnlyWithMargin()
    {
        KassaMainViewRules.UseAutoList(1100, currentlyAutoList: false, userChoseCards: false).Should().BeTrue("окно уже 1280 — список");
        KassaMainViewRules.UseAutoList(1280, currentlyAutoList: false, userChoseCards: false).Should().BeFalse("ровно 1280 — ещё плитки");
        KassaMainViewRules.UseAutoList(1366, currentlyAutoList: false, userChoseCards: false).Should().BeFalse();
        // запас на границе: уже включённый список не мигает при 1280..1303, обратно на плитки — от 1304
        KassaMainViewRules.UseAutoList(1290, currentlyAutoList: true, userChoseCards: false).Should().BeTrue();
        KassaMainViewRules.UseAutoList(1304, currentlyAutoList: true, userChoseCards: false).Should().BeFalse();
    }

    [Fact]
    public void AutoList_RespectsCashiersChoiceOfCards_AndUnknownWidth()
    {
        KassaMainViewRules.UseAutoList(900, currentlyAutoList: false, userChoseCards: true).Should().BeFalse("кассир сам выбрал плитки — выбор уважаем");
        KassaMainViewRules.UseAutoList(0, currentlyAutoList: false, userChoseCards: false).Should().BeFalse("ширина ещё не измерена — плитки, без мигания при запуске");
    }

    [Fact]
    public void SingleInputRow_IsOff_UntilFlagFileExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "nm-single-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            KassaMainViewRules.SingleInputRowEnabled(root).Should().BeFalse("по умолчанию нижняя строка ввода на месте");
            File.WriteAllText(Path.Combine(root, KassaMainViewRules.SingleInputFlagFile), "");
            KassaMainViewRules.SingleInputRowEnabled(root).Should().BeTrue();
            KassaMainViewRules.SearchWatermark(true).Should().NotBeNullOrWhiteSpace();
            KassaMainViewRules.SearchWatermark(false).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CartLine_RemoveNames_ContainProductTitle()
    {
        var line = new CartLineItemVm { Title = "Хлеб" };
        line.RemoveName.Should().EndWith(": Хлеб");
        line.RemoveConfirmName.Should().EndWith(": Хлеб");
        line.RemoveName.Should().NotBe(line.RemoveConfirmName, "после первого нажатия у кнопки другое имя — «Подтвердить удаление»");
    }
}
