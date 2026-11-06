using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Р22 — «1.234» в выписке: в колонке, где у остальных сумм копейки, это 1234 сом; без такой колонки — прежнее правило (1,234).</summary>
public sealed class BankAmountColumnTests
{
    private static string[] Row(params string[] cells) => cells;

    private static BankStatement Parse(params string[] amounts)
    {
        var rows = new List<string[]> { Row("Дата", "Сумма", "Назначение") };
        for (var i = 0; i < amounts.Length; i++)
            rows.Add(Row($"0{i + 1}.11.2026", amounts[i], "Оплата " + i));
        return BankStatementParser.Parse(rows);
    }

    [Fact]
    public void ColumnWithCents_DotThreeDigits_IsThousands()
    {
        var statement = Parse("1.234", "100.50", "250.00", "75.25");

        statement.Lines.Select(l => l.Amount).Should().Equal(1234d, 100.5d, 250d, 75.25d);
    }

    [Fact]
    public void ColumnWithCents_CommaThreeDigits_IsThousands()
    {
        Parse("2,500", "100,50", "40,00").Lines.Select(l => l.Amount).Should().Equal(2500d, 100.5d, 40d);
    }

    [Fact]
    public void ColumnWithoutCents_KeepsOldRule_DotIsDecimal()
    {
        Parse("1.250", "2.5").Lines.Select(l => l.Amount).Should().Equal(1.25d, 2.5d);
    }

    [Fact]
    public void ColumnMostlyThousandths_KeepsOldRule()
    {
        // три значения «x.xxx» и одно с копейками — копеек не большинство
        Parse("1.250", "2.500", "3.750", "10.50").Lines.Select(l => l.Amount).Should().Equal(1.25d, 2.5d, 3.75d, 10.5d);
    }

    [Theory]
    [InlineData("1.234", 1.234)]      // одиночное значение без контекста колонки — прежнее поведение (обратная совместимость)
    [InlineData("1,234", 1234)]
    [InlineData("1 234,56", 1234.56)]
    public void TryParseAmount_WithoutColumnContext_IsUnchanged(string text, double expected)
    {
        BankStatementParser.TryParseAmount(text, out var value).Should().BeTrue();
        value.Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void TryParseAmount_ColumnHasCents_DotThousands()
    {
        BankStatementParser.TryParseAmount("1.234", out var value, columnHasCents: true).Should().BeTrue();
        value.Should().Be(1234d);
        BankStatementParser.TryParseAmount("0.500", out var half, columnHasCents: true).Should().BeTrue();
        half.Should().Be(0.5d, "ведущий ноль — дробь, не разряды");
    }
}
