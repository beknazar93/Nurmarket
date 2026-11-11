using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-09 (владелец: «при повторном скане того же товара остановить сканер и спросить количество — и на закупках в десктопе»): чистая логика —
/// «тот же код сразу после добавления» (<see cref="ReceivingRepeatScanGuard"/>) и действия над строкой (<see cref="ReceivingRepeatScan"/>). Окна и сканер не нужны.</summary>
public sealed class ReceivingRepeatScanTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static ReceivingRepeatScanGuard Guard(Clock clock, bool enabled = true, TimeSpan? after = null) =>
        new(() => enabled, clock.Get, after);

    private static ReceivingLineVm Line(string barcode = "4600000000011", string unit = "шт", double qty = 1) =>
        new() { Barcode = barcode, ProductName = "Молоко", Unit = unit, Source = ReceivingSource.Warehouse, ProductId = "p1", Quantity = qty };

    // ------------------------------------------------------------------ guard

    [Fact]
    public void FirstScan_IsFresh()
    {
        var g = Guard(new Clock());
        g.Check("4600000000011").Should().Be(ReceivingScanVerdict.Fresh);
    }

    [Fact]
    public void SameCodeRightAfterCounted_IsRepeat_AndStaysRepeatUntilAnotherCode()
    {
        var g = Guard(new Clock());
        g.Counted("4600000000011");
        g.Check("4600000000011").Should().Be(ReceivingScanVerdict.Repeat);
        g.Check(" 4600000000011 ").Should().Be(ReceivingScanVerdict.Repeat); // пробелы по краям — тот же код
        g.Check("4600000000011").Should().Be(ReceivingScanVerdict.Repeat);
    }

    [Fact]
    public void AnotherCode_IsFresh_AndOldCodeIsNoLongerRepeat()
    {
        var g = Guard(new Clock());
        g.Counted("A-1");
        g.Check("B-2").Should().Be(ReceivingScanVerdict.Fresh);
        g.Counted("B-2"); // другой код засчитан — отсчёт у него
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Fresh);
        g.Check("B-2").Should().Be(ReceivingScanVerdict.Repeat);
    }

    [Fact]
    public void Pause_LongerThanResetAfter_MakesCodeFreshAgain()
    {
        var clock = new Clock();
        var g = Guard(clock);
        g.Counted("A-1");
        clock.Now += ReceivingRepeatScanGuard.DefaultResetAfter - TimeSpan.FromSeconds(1);
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Repeat);
        clock.Now += TimeSpan.FromSeconds(5);
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Fresh);
        g.LastCode.Should().BeNull();
    }

    [Fact]
    public void CustomPause_IsRespected()
    {
        var clock = new Clock();
        var g = Guard(clock, after: TimeSpan.FromSeconds(3));
        g.Counted("A-1");
        clock.Now += TimeSpan.FromSeconds(4);
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Fresh);
    }

    [Fact]
    public void Reset_WindowClosedOrListCleared_StartsOver()
    {
        var g = Guard(new Clock());
        g.Counted("A-1");
        g.Reset();
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Fresh);
    }

    [Fact]
    public void Disabled_ByPreference_NeverRepeat()
    {
        var g = Guard(new Clock(), enabled: false);
        g.Counted("A-1");
        g.Check("A-1").Should().Be(ReceivingScanVerdict.Fresh);
    }

    [Fact]
    public void Preference_DefaultIsOn()
    {
        new UserPreferences().AskQtyOnRepeatScan.Should().BeTrue();
    }

    [Fact]
    public void EmptyCode_IsFresh_AndCountedEmptyResets()
    {
        var g = Guard(new Clock());
        g.Counted("A-1");
        g.Check("").Should().Be(ReceivingScanVerdict.Fresh);
        g.Counted("  ");
        g.LastCode.Should().BeNull();
    }

    // ------------------------------------------------------------------ строка: единица, разбор, прибавление

    [Theory]
    [InlineData("шт", false)]
    [InlineData("л", true)]
    [InlineData("кг", true)]
    [InlineData("м", true)]
    public void IsFractional_ByUnit(string unit, bool expected) =>
        ReceivingRepeatScan.IsFractional(Line(unit: unit)).Should().Be(expected);

    [Fact]
    public void IsFractional_PackMode_IsWholePacks_EvenForLitres()
    {
        var l = Line(unit: "л");
        l.SetPackMode(true, 6);
        ReceivingRepeatScan.IsFractional(l).Should().BeFalse();
    }

    [Theory]
    [InlineData("2", false, 2.0)]
    [InlineData("2,5", true, 2.5)]
    [InlineData("2.5", true, 2.5)]
    [InlineData("1 234", false, 1234.0)]
    [InlineData("0,0004", true, null)] // округляется до 0 — не годится
    [InlineData("0", false, null)]
    [InlineData("-3", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("", false, null)]
    [InlineData("1000001", false, null)]
    [InlineData("4600000000011", false, null)] // штрихкод в поле — не количество
    public void Parse_AcceptsOnlyPositiveNumbers(string text, bool fractional, double? expected) =>
        ReceivingRepeatScan.Parse(text, fractional).Should().Be(expected);

    [Fact]
    public void Parse_Whole_RoundsToWhole()
    {
        ReceivingRepeatScan.Parse("2,4", false).Should().Be(2);
        ReceivingRepeatScan.Parse("2,6", false).Should().Be(3);
    }

    [Fact]
    public void AddTo_AddsToTheSameLine_NotNewOne()
    {
        var l = Line(qty: 3);
        ReceivingRepeatScan.AddTo(l, 5);
        l.Quantity.Should().Be(8);
    }

    [Fact]
    public void AddTo_Litres_Fractional_NoFloatNoise()
    {
        var l = Line(unit: "л", qty: 0.1);
        ReceivingRepeatScan.AddTo(l, 0.2);
        l.Quantity.Should().Be(0.3);
        ReceivingRepeatScan.AddTo(l, 1.5);
        l.Quantity.Should().Be(1.8);
    }

    [Fact]
    public void AddTo_PackMode_AddsPacks_PiecesFollow()
    {
        var l = Line(qty: 24);
        l.SetPackMode(true, 24); // 1 упаковка по 24
        l.PackCount.Should().Be(1);
        ReceivingRepeatScan.AddTo(l, 2);
        l.PackCount.Should().Be(3);
        l.Quantity.Should().Be(72);
    }

    [Fact]
    public void AddTo_PackMode_AfterPieceScan_KeepsPiecesExact()
    {
        var l = Line(qty: 25); // 1 упаковка и 1 штука (скан +1 в режиме упаковок добавляет штуку)
        l.SetPackMode(true, 24);
        ReceivingRepeatScan.AddTo(l, 2);
        l.Quantity.Should().Be(73, "25 + 2 × 24 штук ровно, без хвоста от пересчёта упаковок");
        ReceivingLineVm.QuantityTooPrecise(l.Quantity).Should().BeFalse();
    }

    [Fact]
    public void AddTo_IgnoresZeroAndNegative()
    {
        var l = Line(qty: 4);
        ReceivingRepeatScan.AddTo(l, 0);
        ReceivingRepeatScan.AddTo(l, -2);
        l.Quantity.Should().Be(4);
    }

    [Fact]
    public void Current_And_UnitLabel_FollowPackMode()
    {
        var l = Line(qty: 24);
        ReceivingRepeatScan.Current(l).Should().Be(24);
        ReceivingRepeatScan.UnitLabel(l).Should().NotBeNullOrWhiteSpace();
        l.SetPackMode(true, 24);
        ReceivingRepeatScan.Current(l).Should().Be(1);
        ReceivingRepeatScan.UnitLabel(l).Should().NotBe(UnitCatalog.Display("шт"));
    }

    [Fact]
    public void Message_SaysWhatIsAlreadyThere()
    {
        var l = Line(unit: "л", qty: 2.5);
        var text = ReceivingRepeatScan.Message(l);
        text.Should().Contain("2,5");
        text.Should().Contain(UnitCatalog.Display("л"));
    }

    [Fact]
    public void Title_NameOrBarcode()
    {
        ReceivingRepeatScan.Title(Line()).Should().Be("Молоко");
        var unnamed = Line();
        unnamed.ProductName = "";
        ReceivingRepeatScan.Title(unnamed).Should().Be("4600000000011");
    }

    [Fact]
    public void FindLine_ByBarcode_CaseInsensitive_AndNullWhenAbsent()
    {
        var a = Line("AB-1");
        var b = Line("CD-2");
        var list = new[] { a, b };
        ReceivingRepeatScan.FindLine(list, "ab-1").Should().BeSameAs(a);
        ReceivingRepeatScan.FindLine(list, " CD-2 ").Should().BeSameAs(b);
        ReceivingRepeatScan.FindLine(list, "ZZ-9").Should().BeNull();
        ReceivingRepeatScan.FindLine(list, "").Should().BeNull();
    }
}
