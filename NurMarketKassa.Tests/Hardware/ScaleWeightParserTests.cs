using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Hardware;

public sealed class ScaleWeightParserTests
{
    [Theory]
    [InlineData("ST,GS,+ 0.340 kg", 0.340)]
    [InlineData("NET: 0,340kg", 0.340)]
    [InlineData("S  +001.250", 1.250)]
    public void ParseWeightLine_ParsesCommonScaleFrames(string frame, double expectedKg)
    {
        ScaleWeightParser.ParseWeightLine(frame).Should().BeApproximately(expectedKg, 0.0001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("STABLE")]
    public void ParseWeightLine_ReturnsNullWithoutNumericPayload(string frame)
    {
        ScaleWeightParser.ParseWeightLine(frame).Should().BeNull();
    }
}
