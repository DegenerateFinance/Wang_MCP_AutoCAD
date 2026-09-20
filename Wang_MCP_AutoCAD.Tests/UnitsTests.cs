using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData(1, 25.4)]      // Inches
    [InlineData(2, 304.8)]     // Feet
    [InlineData(4, 1.0)]       // Millimeters
    [InlineData(5, 10.0)]      // Centimeters
    [InlineData(6, 1000.0)]    // Meters
    [InlineData(7, 1000000.0)] // Kilometers
    [InlineData(10, 914.4)]    // Yards
    [InlineData(13, 0.001)]    // Microns
    [InlineData(14, 100.0)]    // Decimeters
    public void TryGetMmPerDrawingUnit_KnownCodes(int code, double expected)
    {
        bool known = Units.TryGetMmPerDrawingUnit(code, out double mmPerDrawingUnit);

        Assert.True(known);
        Assert.Equal(expected, mmPerDrawingUnit, precision: 9);
    }

    [Fact]
    public void TryGetMmPerDrawingUnit_Unitless_ReturnsFalse()
    {
        // The whole point of the Du_/Mm_ split: a unitless drawing must not be silently
        // assumed to be millimetres.
        bool known = Units.TryGetMmPerDrawingUnit(0, out double _);

        Assert.False(known);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(-1)]
    [InlineData(999)]
    public void TryGetMmPerDrawingUnit_UnsupportedCodes_ReturnFalse(int code)
    {
        Assert.False(Units.TryGetMmPerDrawingUnit(code, out double _));
    }

    [Fact]
    public void DescribeUnsupportedUnits_ForUnitless_NamesInsunitsAndTellsTheUserWhatToDo()
    {
        string message = Units.DescribeUnsupportedUnits(0);

        Assert.Contains("INSUNITS", message);
        Assert.Contains("unitless", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1000.0, 1.0, 1000.0)]    // mm drawing: 1000 mm is 1000 du
    [InlineData(1000.0, 1000.0, 1.0)]    // metre drawing: 1000 mm is 1 du
    [InlineData(25.4, 25.4, 1.0)]        // inch drawing: 25.4 mm is 1 du
    public void MmToDrawingUnits_ConvertsWithTheDrawingsFactor(
        double mm_Value,
        double mmPerDrawingUnit,
        double expectedDu)
    {
        double du = Units.MmToDrawingUnits(mm_Value, mmPerDrawingUnit);

        Assert.Equal(expectedDu, du, precision: 9);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(1)]
    public void MmToDuAndBack_RoundTrips(int code)
    {
        Units.TryGetMmPerDrawingUnit(code, out double mmPerDrawingUnit);

        double mm_Original = 1234.5;
        double du = Units.MmToDrawingUnits(mm_Original, mmPerDrawingUnit);
        double mm_RoundTripped = Units.DrawingUnitsToMm(du, mmPerDrawingUnit);

        Assert.Equal(mm_Original, mm_RoundTripped, precision: 9);
    }

    [Fact]
    public void LineWeightToMm_IsHundredthsOfAMillimetre_AndIgnoresInsunits()
    {
        // LineWeight is stored in 1/100 mm regardless of the drawing's units. Running it
        // through the INSUNITS factor is a classic silent bug.
        Assert.Equal(0.25, Units.LineWeightToMm(25), precision: 9);
        Assert.Equal(0.5, Units.LineWeightToMm(50), precision: 9);
        Assert.Equal(2.11, Units.LineWeightToMm(211), precision: 9);
    }
}
