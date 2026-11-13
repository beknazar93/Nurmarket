using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-12 (Д-5, оценка UX/UI): вес и количество на экране — с запятой, а не с точкой («0.00» в окне взвешивания, «1.25» в чеке покупателя).
/// MoneyText.Weight — только показ (разбор вводимого по-прежнему принимает и запятую, и точку).</summary>
public sealed class WeightFormatD5Tests
{
    [Theory]
    [InlineData(1.25, "0.###", "1,25")]
    [InlineData(0.0, "0.00", "0,00")]
    [InlineData(10.0, "0.###", "10")]
    [InlineData(0.8333, "0.###", "0,833")]
    [InlineData(1234.5, "0.###", "1234,5")] // без разрядов: вес в поле ввода должен разбираться обратно
    [InlineData(2.0, "0.000", "2,000")]
    public void Weight_UsesCommaAndNoGrouping(double value, string format, string expected) =>
        MoneyText.Weight(value, format).Should().Be(expected);

    [Fact]
    public void Weight_NotANumber_IsDash() =>
        MoneyText.Weight(double.NaN).Should().Be(MoneyText.NotANumber);

    [Fact]
    public void Weight_Roundtrip_ParsesBackWithCommaReplace()
    {
        var shown = MoneyText.Weight(0.833);
        double.TryParse(shown.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var back)
            .Should().BeTrue();
        back.Should().BeApproximately(0.833, 1e-9);
    }
}
