using System.Text.Json;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

public class ToolArgsTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    [Fact]
    public void TryGetDouble_Missing_ErrorNamesTheField()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetDouble("mm_radius", out double _, out string error);

        Assert.False(ok);
        Assert.Contains("mm_radius", error);
    }

    [Fact]
    public void TryGetDouble_WrongType_ErrorNamesTheFieldAndWhatItGot()
    {
        ToolArgs args = Parse("""{"mm_radius":"50"}""");

        bool ok = args.TryGetDouble("mm_radius", out double _, out string error);

        Assert.False(ok);
        Assert.Contains("mm_radius", error);
        Assert.Contains("a string", error);
    }

    [Fact]
    public void TryGetDouble_ReadsIntegersAndDecimals()
    {
        ToolArgs args = Parse("""{"a":50,"b":12.5,"c":-3}""");

        Assert.True(args.TryGetDouble("a", out double a, out string _));
        Assert.True(args.TryGetDouble("b", out double b, out string _));
        Assert.True(args.TryGetDouble("c", out double c, out string _));

        Assert.Equal(50, a);
        Assert.Equal(12.5, b);
        Assert.Equal(-3, c);
    }

    [Fact]
    public void TryGetDoubleOrDefault_AbsentOrNull_UsesTheFallback()
    {
        ToolArgs absent = Parse("{}");
        ToolArgs explicitNull = Parse("""{"mm_center_z":null}""");

        Assert.True(absent.TryGetDoubleOrDefault("mm_center_z", 0, out double a, out string _));
        Assert.True(explicitNull.TryGetDoubleOrDefault("mm_center_z", 0, out double b, out string _));

        Assert.Equal(0, a);
        Assert.Equal(0, b);
    }

    [Fact]
    public void TryGetPositiveDouble_Zero_IsRejectedWithTheValueInTheMessage()
    {
        ToolArgs args = Parse("""{"mm_radius":0}""");

        bool ok = args.TryGetPositiveDouble("mm_radius", out double _, out string error);

        Assert.False(ok);
        Assert.Contains("greater than 0", error);
    }

    [Fact]
    public void TryGetPositiveDouble_Negative_IsRejected()
    {
        ToolArgs args = Parse("""{"mm_radius":-5}""");

        bool ok = args.TryGetPositiveDouble("mm_radius", out double _, out string error);

        Assert.False(ok);
        Assert.Contains("-5", error);
    }

    [Fact]
    public void TryGetIntInRange_OutOfRange_IsRejectedAndKeepsTheFallback()
    {
        ToolArgs args = Parse("""{"limit":5000}""");

        bool ok = args.TryGetIntInRange("limit", fallback: 100, minimum: 1, maximum: 1000, out int value, out string error);

        Assert.False(ok);
        Assert.Equal(100, value);
        Assert.Contains("between 1 and 1000", error);
    }

    [Fact]
    public void TryGetIntInRange_Absent_ReturnsTheDefault()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetIntInRange("limit", fallback: 100, minimum: 1, maximum: 1000, out int value, out string _);

        Assert.True(ok);
        Assert.Equal(100, value);
    }

    [Fact]
    public void TryGetIntInRange_NonInteger_IsRejected()
    {
        ToolArgs args = Parse("""{"limit":12.7}""");

        bool ok = args.TryGetIntInRange("limit", fallback: 100, minimum: 1, maximum: 1000, out int _, out string error);

        Assert.False(ok);
        Assert.Contains("whole number", error);
    }

    [Fact]
    public void TryGetStringOrDefault_WrongType_IsRejected()
    {
        ToolArgs args = Parse("""{"layer":42}""");

        bool ok = args.TryGetStringOrDefault("layer", null, out string? _, out string error);

        Assert.False(ok);
        Assert.Contains("layer", error);
        Assert.Contains("a number", error);
    }

    [Fact]
    public void TryGetStringOrDefault_Present_ReturnsTheValue()
    {
        ToolArgs args = Parse("""{"layer":"WALLS"}""");

        Assert.True(args.TryGetStringOrDefault("layer", null, out string? value, out string _));
        Assert.Equal("WALLS", value);
    }

    [Fact]
    public void EmptyArgs_TreatsEveryRequiredFieldAsMissing()
    {
        // tools/call may omit "arguments" entirely for a no-argument tool.
        bool ok = ToolArgs.Empty.TryGetDouble("anything", out double _, out string error);

        Assert.False(ok);
        Assert.Contains("anything", error);
    }

    [Fact]
    public void NonObjectArguments_AreTreatedAsEmptyRatherThanThrowing()
    {
        ToolArgs args = Parse("\"not an object\"");

        Assert.False(args.TryGetDouble("x", out double _, out string _));
    }
}
