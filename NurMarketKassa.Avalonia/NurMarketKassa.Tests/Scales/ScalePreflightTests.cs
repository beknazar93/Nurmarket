using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-13: «Проверка перед отправкой» в окне «Весы» (решение владельца по сравнению с Paloma365 и Rosta): все замечания одним списком — правила без окна и сети.</summary>
public sealed class ScalePreflightTests
{
    private static ScalePreflightContext Ctx(int maxPlu = 4000, long maxCode = 9_999_999, long maxUnits = ScalePriceLimits.DahuaMaxUnits, int decimals = 2, int maxProducts = 4000, decimal ceiling = 0m) =>
        new(maxPlu, maxCode, maxUnits, decimals, maxProducts, ceiling, ScaleNameChars.HasUnprintable);

    private static ScalePreflightRow Row(string id, string name = "Яблоки", decimal price = 100m, int? plu = 1, long? code = null, string? codeText = null) =>
        new(id, name, price, plu, code, codeText);

    private static List<ScalePreflightIssue> Check(ScalePreflightContext ctx, params ScalePreflightRow[] rows) => ScalePreflight.Check(rows, ctx);

    [Fact]
    public void CleanList_HasNoIssues()
    {
        Check(Ctx(), Row("a"), Row("b", plu: 2, code: 25, codeText: "25")).Should().BeEmpty();
        ScalePreflight.Summary(new List<ScalePreflightIssue>()).Should().Contain("замечаний нет");
    }

    [Fact]
    public void NoPlu_IsErrorWithFix()
    {
        var issue = Check(Ctx(), Row("a", plu: null)).Should().ContainSingle().Subject;
        issue.Kind.Should().Be(ScalePreflightKind.NoPlu);
        issue.Severity.Should().Be(ScalePreflightSeverity.Error);
        issue.Blocks.Should().BeTrue();
        issue.CanFix.Should().BeTrue();
    }

    [Fact]
    public void PluAboveScaleTable_IsError()
    {
        var issues = Check(Ctx(maxPlu: 4000), Row("a", plu: 4000), Row("b", plu: 4001, name: "Груши"));
        issues.Should().ContainSingle().Which.Should().Match<ScalePreflightIssue>(i => i.Kind == ScalePreflightKind.PluTooLong && i.ProductId == "b" && i.CanFix);
    }

    [Fact]
    public void DuplicatePlu_FlagsEveryProductWithIt()
    {
        var issues = Check(Ctx(), Row("a", plu: 7), Row("b", plu: 7, name: "Груши"), Row("c", plu: 8, name: "Сливы"));
        issues.Where(i => i.Kind == ScalePreflightKind.DuplicatePlu).Select(i => i.ProductId).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void EmptyBarcodeCode_IsFine_ButTextAndTooLongAreNot()
    {
        Check(Ctx(), Row("a", code: null, codeText: "")).Should().BeEmpty("пустой «Код в ШК» = номер ячейки");
        Check(Ctx(), Row("a", code: null, codeText: "abc")).Should().ContainSingle().Which.Kind.Should().Be(ScalePreflightKind.NoCode);
        var tooLong = Check(Ctx(maxCode: 999_999), Row("a", code: 1_000_000, codeText: "1000000")).Should().ContainSingle().Subject;
        tooLong.Kind.Should().Be(ScalePreflightKind.CodeTooLong);
        tooLong.CanFix.Should().BeTrue();
    }

    [Fact]
    public void ZeroPrice_IsErrorWithoutFix()
    {
        var issue = Check(Ctx(), Row("a", price: 0m)).Should().ContainSingle().Subject;
        issue.Kind.Should().Be(ScalePreflightKind.PriceZero);
        issue.Blocks.Should().BeTrue();
        issue.CanFix.Should().BeFalse("цена правится в карточке товара");
    }

    [Fact]
    public void PriceAboveScaleField_UsesScalePriceLimits()
    {
        // 6 цифр поля цены и 2 знака: 9999,99 помещается, 10000 — нет
        Check(Ctx(decimals: 2), Row("a", price: 9999.99m)).Should().BeEmpty();
        Check(Ctx(decimals: 2), Row("a", price: 10_000m)).Should().ContainSingle().Which.Kind.Should().Be(ScalePreflightKind.PriceTooHigh);
        Check(Ctx(decimals: 0), Row("a", price: 10_000m)).Should().BeEmpty("при 0 знаков поле вмещает 999 999");
    }

    [Fact]
    public void MoreProductsThanScaleHolds_IsOneListLevelIssue()
    {
        var rows = Enumerable.Range(1, 5).Select(i => Row("p" + i, plu: i, name: "Товар " + i)).ToArray();
        var issues = Check(Ctx(maxProducts: 4), rows);
        var over = issues.Should().ContainSingle().Subject;
        over.Kind.Should().Be(ScalePreflightKind.OverLimit);
        over.ProductId.Should().BeNull();
        over.Blocks.Should().BeFalse("замечание ко всему списку не выкидывает один товар");
        over.CanFix.Should().BeTrue();
        Check(Ctx(maxProducts: 0), rows).Should().BeEmpty("предел неизвестен (Штрих-ПРИНТ до связи) — не проверяем");
    }

    [Fact]
    public void AmountCeiling_WarnsWhenOneKilogramAlreadyDoesNotFit_AndTypeWithoutCeilingIsClean()
    {
        // Rongta тип 02: сумма 5 цифр в сотых → потолок 999,99 сом; тип 07 (по весу) потолка не имеет
        var type02 = RongtaBarcodeFormat.Find(2)!;
        var ceiling02 = RongtaBarcodeFormat.MaxAmountSom(type02, 2);
        ceiling02.Should().BeGreaterThan(0m);
        var issue = Check(Ctx(ceiling: ceiling02), Row("a", price: 1100m)).Should().ContainSingle().Subject;
        issue.Kind.Should().Be(ScalePreflightKind.AmountOverCeiling);
        issue.Severity.Should().Be(ScalePreflightSeverity.Warning);
        issue.CanFix.Should().BeTrue("«Исправить» — тип 07");
        Check(Ctx(ceiling: ceiling02), Row("b", price: 500m)).Should().BeEmpty();

        var type07 = RongtaBarcodeFormat.Find(RongtaBarcodeFormat.RecommendedTypeNumber)!;
        RongtaBarcodeFormat.MaxAmountSom(type07, 2).Should().Be(0m, "у типа 07 нет потолка суммы");
        Check(Ctx(ceiling: RongtaBarcodeFormat.MaxAmountSom(type07, 2)), Row("c", price: 5000m)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Мясо говядина", false)]
    [InlineData("Яблоки Ёлка 1кг, «свежие» №5", false)]
    [InlineData("Нан ү ө ң", true)]     // кыргызские буквы вне Windows-1251
    [InlineData("Сыр 🧀", true)]
    [InlineData("", false)]
    public void NameChars_DetectsLettersTheScaleCannotPrint(string name, bool unprintable)
    {
        ScaleNameChars.HasUnprintable(name).Should().Be(unprintable);
        var issues = Check(Ctx(), Row("a", name: name));
        if (unprintable)
            issues.Should().ContainSingle().Which.Should().Match<ScalePreflightIssue>(i => i.Kind == ScalePreflightKind.NameChars && i.Severity == ScalePreflightSeverity.Warning && !i.Blocks);
        else
            issues.Should().BeEmpty();
    }

    [Fact]
    public void EveryKind_HasTitleAndDescriptionOnFiveLanguages_AndSummaryCounts()
    {
        foreach (var kind in Enum.GetValues<ScalePreflightKind>())
        {
            ScalePreflight.Title(kind).Should().NotBeNullOrWhiteSpace().And.NotBe(kind.ToString());
            ScalePreflight.Describe(new ScalePreflightIssue(kind, ScalePreflightSeverity.Error, "x", "Тест", "5")).Should().NotBeNullOrWhiteSpace();
        }
        var issues = new List<ScalePreflightIssue>
        {
            new(ScalePreflightKind.NoPlu, ScalePreflightSeverity.Error, "a", "A", ""),
            new(ScalePreflightKind.NameChars, ScalePreflightSeverity.Warning, "b", "B", ""),
            new(ScalePreflightKind.NameChars, ScalePreflightSeverity.Warning, "c", "C", ""),
        };
        ScalePreflight.Summary(issues).Should().Contain("ошибок 1").And.Contain("предупреждений 2");
    }
}
