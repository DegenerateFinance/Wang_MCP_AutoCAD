using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Angle, area and transparency conversion. Each of these is a silent-bug class: a missing
/// radian conversion draws a wildly wrong arc, a linear factor applied to an area is off by
/// the factor itself, and an inverted alpha makes entities vanish.
/// </summary>
public sealed class UnitsAngleAreaTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    [InlineData(270.0)]
    [InlineData(359.999)]
    [InlineData(-45.0)]
    [InlineData(720.0)]
    public void DegToRad_RoundTripsThroughRadToDeg(double deg_Value)
    {
        double rad = Units.DegToRad(deg_Value);
        double back = Units.RadToDeg(rad);

        Assert.Equal(deg_Value, back, precision: 12);
    }

    [Fact]
    public void DegToRad_NinetyDegreesIsHalfPi()
    {
        Assert.Equal(Math.PI / 2, Units.DegToRad(90), precision: 12);
    }

    [Fact]
    public void RadToDeg_PiIsOneHundredAndEighty()
    {
        Assert.Equal(180.0, Units.RadToDeg(Math.PI), precision: 12);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(90.0, 90.0)]
    [InlineData(359.9, 359.9)]
    [InlineData(360.0, 0.0)]
    [InlineData(-90.0, 270.0)]
    [InlineData(-0.0001, 359.9999)]
    [InlineData(450.0, 90.0)]
    [InlineData(-720.0, 0.0)]
    public void NormalizeDeg_WrapsIntoZeroTo360(double input, double expected)
    {
        Assert.Equal(expected, Units.NormalizeDeg(input), precision: 9);
    }

    /// <summary>
    /// The load-bearing area test: 1 square metre in a drawing whose unit is the metre is
    /// 1 square drawing unit. Applying the linear factor instead of its square gives 1000,
    /// which is exactly the bug this method exists to prevent.
    /// </summary>
    [Fact]
    public void Mm2ToDrawingUnits2_SquaresTheFactor()
    {
        double mm2_OneSquareMetre = 1_000_000.0;
        double mmPerDrawingUnit = 1000.0;

        double du2 = Units.Mm2ToDrawingUnits2(mm2_OneSquareMetre, mmPerDrawingUnit);

        Assert.Equal(1.0, du2, precision: 9);
    }

    [Fact]
    public void DrawingUnits2ToMm2_SquaresTheFactor()
    {
        Assert.Equal(1_000_000.0, Units.DrawingUnits2ToMm2(1.0, 1000.0), precision: 6);
    }

    [Theory]
    [InlineData(1.0)]      // millimetres
    [InlineData(10.0)]     // centimetres
    [InlineData(1000.0)]   // metres
    [InlineData(25.4)]     // inches
    public void Area_RoundTrips(double mmPerDrawingUnit)
    {
        double mm2_Original = 12_345.678;

        double du2 = Units.Mm2ToDrawingUnits2(mm2_Original, mmPerDrawingUnit);
        double back = Units.DrawingUnits2ToMm2(du2, mmPerDrawingUnit);

        Assert.Equal(mm2_Original, back, precision: 9);
    }

    [Fact]
    public void TransparencyPercentToAlpha_ZeroPercentIsFullyOpaque()
    {
        Assert.Equal((byte)255, Units.TransparencyPercentToAlpha(0));
    }

    [Fact]
    public void TransparencyPercentToAlpha_HundredPercentIsFullyTransparent()
    {
        Assert.Equal((byte)0, Units.TransparencyPercentToAlpha(100));
    }

    [Fact]
    public void TransparencyPercentToAlpha_NinetyPercentIsNearlyTransparent()
    {
        byte alpha = Units.TransparencyPercentToAlpha(90);

        Assert.InRange(alpha, (byte)20, (byte)30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(90)]
    [InlineData(100)]
    public void Transparency_RoundTripsWithinOnePercent(int percent)
    {
        byte alpha = Units.TransparencyPercentToAlpha(percent);
        int back = Units.AlphaToTransparencyPercent(alpha);

        Assert.InRange(back, percent - 1, percent + 1);
    }

    [Fact]
    public void TransparencyPercentToAlpha_ClampsOutOfRangeInput()
    {
        Assert.Equal((byte)255, Units.TransparencyPercentToAlpha(-10));
        Assert.Equal((byte)0, Units.TransparencyPercentToAlpha(150));
    }
}
