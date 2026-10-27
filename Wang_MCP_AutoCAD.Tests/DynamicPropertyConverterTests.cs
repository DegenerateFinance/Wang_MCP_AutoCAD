using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Dynamic property conversion. The unit tests that matter: a distance in a metre drawing is
/// divided by 1000 on the way in, and an angle is converted to radians — the two mistakes that
/// produce a block 1000× the size or a rotation of 5000 degrees.
/// </summary>
public sealed class DynamicPropertyConverterTests
{
    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ToolArgs Parse(string json) => new(Json(json));

    private static bool Convert(
        DynamicValueKind kind,
        DynamicUnitsKind units,
        string json,
        out object value,
        out string error,
        double mmPerDu = 1,
        params object[] allowed)
    {
        return DynamicPropertyConverter.TryConvert("P", kind, units, Json(json), mmPerDu, allowed, out value, out error);
    }

    [Fact]
    public void Distance_IsConvertedFromMmToDrawingUnits()
    {
        Assert.True(Convert(DynamicValueKind.Real, DynamicUnitsKind.Distance, "1500", out object value, out string error, mmPerDu: 1000), error);
        Assert.Equal(1.5, (double)value, precision: 12);
    }

    [Fact]
    public void Angle_IsConvertedFromDegreesToRadians()
    {
        Assert.True(Convert(DynamicValueKind.Real, DynamicUnitsKind.Angle, "90", out object value, out string error), error);
        Assert.Equal(Math.PI / 2, (double)value, precision: 12);
    }

    [Fact]
    public void Area_ScalesByTheSquareOfTheUnitFactor()
    {
        Assert.True(Convert(DynamicValueKind.Real, DynamicUnitsKind.Area, "2000000", out object value, out string error, mmPerDu: 1000), error);
        Assert.Equal(2.0, (double)value, precision: 12);
    }

    [Fact]
    public void Unitless_RealPassesThrough()
    {
        Assert.True(Convert(DynamicValueKind.Real, DynamicUnitsKind.None, "3.25", out object value, out string error, mmPerDu: 1000), error);
        Assert.Equal(3.25, (double)value);
    }

    [Fact]
    public void Real_AllowedListSnapsToTheStoredValue()
    {
        Assert.True(Convert(DynamicValueKind.Real, DynamicUnitsKind.Distance, "900.0000000001", out object value, out string error, 1, 600.0, 900.0), error);
        Assert.Equal(900.0, (double)value);
    }

    [Fact]
    public void Real_NotInAllowedList_ListsTheChoicesInWireUnits()
    {
        Assert.False(Convert(DynamicValueKind.Real, DynamicUnitsKind.Distance, "750", out _, out string error, 1000, 0.6, 0.9));
        Assert.Contains("600", error);
        Assert.Contains("900", error);
        Assert.Contains("mm", error);
    }

    [Fact]
    public void Real_StringIsRefused()
    {
        Assert.False(Convert(DynamicValueKind.Real, DynamicUnitsKind.Distance, "\"10\"", out _, out string error));
        Assert.Contains("number in mm", error);
    }

    [Fact]
    public void Text_MatchesCaseInsensitivelyButReturnsTheStoredSpelling()
    {
        Assert.True(Convert(DynamicValueKind.Text, DynamicUnitsKind.None, "\"open\"", out object value, out string error, 1, "Open", "Closed"), error);
        Assert.Equal("Open", value);
    }

    [Fact]
    public void Text_UnknownStateIsRefusedWithTheChoices()
    {
        Assert.False(Convert(DynamicValueKind.Text, DynamicUnitsKind.None, "\"Ajar\"", out _, out string error, 1, "Open", "Closed"));
        Assert.Contains("\"Open\"", error);
        Assert.Contains("\"Closed\"", error);
    }

    [Fact]
    public void Text_NumberIsRefused()
    {
        Assert.False(Convert(DynamicValueKind.Text, DynamicUnitsKind.None, "1", out _, out string error));
        Assert.Contains("string", error);
    }

    [Fact]
    public void Short_IsBoxedAsShort()
    {
        Assert.True(Convert(DynamicValueKind.Short, DynamicUnitsKind.None, "1", out object value, out string error), error);
        Assert.IsType<short>(value);
    }

    [Fact]
    public void Short_OutOfRangeIsRefused()
    {
        Assert.False(Convert(DynamicValueKind.Short, DynamicUnitsKind.None, "40000", out _, out string error));
        Assert.Contains("between", error);
    }

    [Fact]
    public void Integer_FractionIsRefused()
    {
        Assert.False(Convert(DynamicValueKind.Integer, DynamicUnitsKind.None, "1.5", out _, out string error));
        Assert.Contains("whole number", error);
    }

    [Fact]
    public void Integer_AllowedListIsEnforced()
    {
        Assert.True(Convert(DynamicValueKind.Integer, DynamicUnitsKind.None, "1", out _, out string error, 1, 0, 1), error);
        Assert.False(Convert(DynamicValueKind.Integer, DynamicUnitsKind.None, "2", out _, out _, 1, 0, 1));
    }

    [Fact]
    public void Unsupported_IsRefused()
    {
        Assert.False(Convert(DynamicValueKind.Unsupported, DynamicUnitsKind.None, "1", out _, out string error));
        Assert.Contains("cannot set", error);
    }

    // ---- ToWire ---------------------------------------------------------------------------

    [Fact]
    public void ToWire_DistanceAndAngleConvertBack()
    {
        JsonNode? mm = DynamicPropertyConverter.ToWire(DynamicValueKind.Real, DynamicUnitsKind.Distance, 1.5, 1000);
        JsonNode? deg = DynamicPropertyConverter.ToWire(DynamicValueKind.Real, DynamicUnitsKind.Angle, Math.PI, 1);

        Assert.Equal(1500, mm!.GetValue<double>(), precision: 9);
        Assert.Equal(180, deg!.GetValue<double>(), precision: 9);
    }

    [Fact]
    public void ToWire_NeverThrowsOnAnUnexpectedType()
    {
        JsonNode? node = DynamicPropertyConverter.ToWire(DynamicValueKind.Real, DynamicUnitsKind.Distance, "not a double", 1);

        Assert.Equal("not a double", node!.GetValue<string>());
    }

    [Theory]
    [InlineData(DynamicUnitsKind.Distance, "mm")]
    [InlineData(DynamicUnitsKind.Angle, "deg")]
    [InlineData(DynamicUnitsKind.Area, "mm2")]
    [InlineData(DynamicUnitsKind.None, "none")]
    public void UnitsName_StatesTheWireUnit(DynamicUnitsKind units, string expected)
    {
        Assert.Equal(expected, DynamicPropertyConverter.UnitsName(units));
    }

    // ---- TryReadRequests ------------------------------------------------------------------

    [Fact]
    public void TryReadRequests_ReadsNumbersAndStrings()
    {
        bool ok = DynamicPropertyConverter.TryReadRequests(
            Parse("""{"properties":{"Distance1":1200,"Visibility1":"Open"}}"""),
            "properties",
            out IReadOnlyList<KeyValuePair<string, JsonElement>> requests,
            out string error);

        Assert.True(ok, error);
        Assert.Equal(2, requests.Count);
        Assert.Equal(JsonValueKind.Number, requests[0].Value.ValueKind);
        Assert.Equal("Open", requests[1].Value.GetString());
    }

    [Theory]
    [InlineData("""{}""", "missing")]
    [InlineData("""{"properties":{}}""", "at least one")]
    [InlineData("""{"properties":{"A":true}}""", "number or a string")]
    [InlineData("""{"properties":{"A":1,"a":2}}""", "more than once")]
    public void TryReadRequests_RefusesBadInput(string json, string expected)
    {
        bool ok = DynamicPropertyConverter.TryReadRequests(Parse(json), "properties", out _, out string error);

        Assert.False(ok);
        Assert.Contains(expected, error);
    }
}
