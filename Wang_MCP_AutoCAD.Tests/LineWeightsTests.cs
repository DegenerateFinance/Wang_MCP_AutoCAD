using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class LineWeightsTests
{
    [Theory]
    [InlineData(0.25, 25, 0.25)]
    [InlineData(0.30, 30, 0.30)]
    [InlineData(0.13, 13, 0.13)]
    [InlineData(2.11, 211, 2.11)]
    [InlineData(0.00, 0, 0.00)]
    public void TryNearest_KeepsExactRungs(double mm_Value, int expectedEnum, double expectedMm)
    {
        bool ok = LineWeights.TryNearest(mm_Value, out int enumValue, out double mm_Snapped, out string error);

        Assert.True(ok, error);
        Assert.Equal(expectedEnum, enumValue);
        Assert.Equal(expectedMm, mm_Snapped, precision: 6);
    }

    [Theory]
    [InlineData(0.27, 25)]   // closer to 25 than 30
    [InlineData(0.28, 30)]   // closer to 30 than 25
    [InlineData(0.26, 25)]
    [InlineData(0.01, 0)]
    [InlineData(0.06, 5)]
    [InlineData(5.00, 211)]  // above the top rung, clamps to it
    public void TryNearest_SnapsToTheNearestRung(double mm_Value, int expectedEnum)
    {
        bool ok = LineWeights.TryNearest(mm_Value, out int enumValue, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(expectedEnum, enumValue);
    }

    /// <summary>
    /// The tie-break must be deterministic rather than dependent on array order. 0.225 mm is
    /// the exact midpoint of the 20 and 25 rungs and survives the multiply by 100 without
    /// drifting off it, so it is a genuine tie rather than an almost-tie. (0.275 looks like
    /// one but is not: 0.275 * 100 is 27.500000000000004, marginally nearer 30.)
    /// </summary>
    [Fact]
    public void TryNearest_ExactTieKeepsTheLowerRung()
    {
        Assert.Equal(22.5, 0.225 * 100.0);

        bool ok = LineWeights.TryNearest(0.225, out int enumValue, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(20, enumValue);
    }

    [Fact]
    public void TryNearest_RejectsNegative()
    {
        bool ok = LineWeights.TryNearest(-1.0, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("negative", error);
        Assert.Contains("ByLayer", error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryNearest_RejectsNonFinite(double mm_Value)
    {
        bool ok = LineWeights.TryNearest(mm_Value, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("finite", error);
    }

    [Fact]
    public void IsExact_RecognisesRungsAndRejectsOthers()
    {
        Assert.True(LineWeights.IsExact(25));
        Assert.True(LineWeights.IsExact(211));
        Assert.False(LineWeights.IsExact(26));
        Assert.False(LineWeights.IsExact(-1));
    }
}
