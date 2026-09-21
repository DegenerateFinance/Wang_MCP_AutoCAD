using System.Text.Json;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// The accessors added for the expanded tool surface. Every error message is fed back to the
/// model as isError content, so the assertions here check the wording as much as the result —
/// a message that does not name the field and the problem cannot be self-corrected from.
/// </summary>
public class ToolArgsExtendedTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    // ---- TryGetString -------------------------------------------------------------------

    [Fact]
    public void TryGetString_ReadsAValue()
    {
        ToolArgs args = Parse("""{"name":"WALLS"}""");

        bool ok = args.TryGetString("name", out string value, out string error);

        Assert.True(ok, error);
        Assert.Equal("WALLS", value);
    }

    [Fact]
    public void TryGetString_Missing_NamesTheField()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetString("name", out _, out string error);

        Assert.False(ok);
        Assert.Contains("name", error);
        Assert.Contains("missing", error);
    }

    [Theory]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    public void TryGetString_RejectsEmptyAndWhitespace(string json)
    {
        ToolArgs args = Parse(json);

        bool ok = args.TryGetString("name", out _, out string error);

        Assert.False(ok);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void TryGetString_WrongType_SaysWhatItGot()
    {
        ToolArgs args = Parse("""{"name":42}""");

        bool ok = args.TryGetString("name", out _, out string error);

        Assert.False(ok);
        Assert.Contains("a number", error);
    }

    // ---- TryGetBoolOrDefault ------------------------------------------------------------

    [Theory]
    [InlineData("""{"closed":true}""", true)]
    [InlineData("""{"closed":false}""", false)]
    public void TryGetBoolOrDefault_ReadsBothValues(string json, bool expected)
    {
        ToolArgs args = Parse(json);

        bool ok = args.TryGetBoolOrDefault("closed", fallback: !expected, out bool value, out string error);

        Assert.True(ok, error);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"closed":null}""")]
    public void TryGetBoolOrDefault_AbsentOrNullUsesFallback(string json)
    {
        ToolArgs args = Parse(json);

        bool ok = args.TryGetBoolOrDefault("closed", fallback: true, out bool value, out string error);

        Assert.True(ok, error);
        Assert.True(value);
    }

    [Fact]
    public void TryGetBoolOrDefault_WrongType_IsRejected()
    {
        ToolArgs args = Parse("""{"closed":"yes"}""");

        bool ok = args.TryGetBoolOrDefault("closed", false, out _, out string error);

        Assert.False(ok);
        Assert.Contains("true or false", error);
        Assert.Contains("a string", error);
    }

    // ---- TryGetIntRequired --------------------------------------------------------------

    [Fact]
    public void TryGetIntRequired_Missing_IsAnError()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetIntRequired("count", 1, 500, out _, out string error);

        Assert.False(ok);
        Assert.Contains("count", error);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void TryGetIntRequired_OutOfRange_NamesBothBounds()
    {
        ToolArgs args = Parse("""{"count":9000}""");

        bool ok = args.TryGetIntRequired("count", 1, 500, out _, out string error);

        Assert.False(ok);
        Assert.Contains("between 1 and 500", error);
        Assert.Contains("9000", error);
    }

    [Fact]
    public void TryGetIntRequired_NonInteger_SaysWholeNumber()
    {
        ToolArgs args = Parse("""{"count":12.7}""");

        bool ok = args.TryGetIntRequired("count", 1, 500, out _, out string error);

        Assert.False(ok);
        Assert.Contains("whole number", error);
    }

    // ---- TryGetNonZeroDouble ------------------------------------------------------------

    [Fact]
    public void TryGetNonZeroDouble_RejectsZeroWithAnExplanation()
    {
        ToolArgs args = Parse("""{"factor":0}""");

        bool ok = args.TryGetNonZeroDouble("factor", out _, out string error);

        Assert.False(ok);
        Assert.Contains("collapses", error);
    }

    [Fact]
    public void TryGetNonZeroDouble_AllowsNegative()
    {
        ToolArgs args = Parse("""{"factor":-2}""");

        bool ok = args.TryGetNonZeroDouble("factor", out double value, out string error);

        Assert.True(ok, error);
        Assert.Equal(-2, value);
    }

    // ---- TryGetEnum ---------------------------------------------------------------------

    private static readonly string[] Modes = { "move", "rotate", "scale", "mirror" };

    [Fact]
    public void TryGetEnum_MatchesCaseInsensitively()
    {
        ToolArgs args = Parse("""{"mode":"ROTATE"}""");

        bool ok = args.TryGetEnum("mode", Modes, null, out string value, out string error);

        Assert.True(ok, error);
        Assert.Equal("rotate", value);
    }

    /// <summary>
    /// The canonical casing is what the Acad side switches on with ordinal comparison, so a
    /// caller's casing must never leak through.
    /// </summary>
    [Fact]
    public void TryGetEnum_ReturnsCanonicalCasingNotTheCallers()
    {
        ToolArgs args = Parse("""{"mode":"MiRrOr"}""");

        args.TryGetEnum("mode", Modes, null, out string value, out _);

        Assert.Equal("mirror", value);
    }

    [Fact]
    public void TryGetEnum_Unknown_ListsEveryAllowedValue()
    {
        ToolArgs args = Parse("""{"mode":"squash"}""");

        bool ok = args.TryGetEnum("mode", Modes, null, out _, out string error);

        Assert.False(ok);
        foreach (string mode in Modes)
        {
            Assert.Contains(mode, error);
        }

        Assert.Contains("squash", error);
    }

    [Fact]
    public void TryGetEnum_AbsentWithFallback_UsesFallback()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetEnum("space", new[] { "model" }, "model", out string value, out string error);

        Assert.True(ok, error);
        Assert.Equal("model", value);
    }

    [Fact]
    public void TryGetEnum_AbsentWithNullFallback_IsRequired()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetEnum("mode", Modes, null, out _, out string error);

        Assert.False(ok);
        Assert.Contains("missing", error);
        Assert.Contains("move", error);
    }

    [Fact]
    public void TryGetEnum_WrongType_StillListsAllowedValues()
    {
        ToolArgs args = Parse("""{"mode":7}""");

        bool ok = args.TryGetEnum("mode", Modes, null, out _, out string error);

        Assert.False(ok);
        Assert.Contains("a number", error);
        Assert.Contains("move", error);
    }

    // ---- TryGetStringArray --------------------------------------------------------------

    [Fact]
    public void TryGetStringArray_ReadsItems()
    {
        ToolArgs args = Parse("""{"handles":["1A7","2B"]}""");

        bool ok = args.TryGetStringArray("handles", 1, 1000, out IReadOnlyList<string> value, out string error);

        Assert.True(ok, error);
        Assert.Equal(new[] { "1A7", "2B" }, value);
    }

    [Fact]
    public void TryGetStringArray_NotAnArray_NamesTheField()
    {
        ToolArgs args = Parse("""{"handles":"1A7"}""");

        bool ok = args.TryGetStringArray("handles", 1, 1000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("handles", error);
        Assert.Contains("a string", error);
    }

    [Fact]
    public void TryGetStringArray_EmptyBelowMinimum_NamesTheCount()
    {
        ToolArgs args = Parse("""{"handles":[]}""");

        bool ok = args.TryGetStringArray("handles", 1, 1000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("between 1 and 1000", error);
        Assert.Contains("contained 0", error);
    }

    /// <summary>
    /// With fifty handles, "one of them is wrong" is useless — the index is what lets a
    /// caller find and fix the bad element.
    /// </summary>
    [Fact]
    public void TryGetStringArray_ElementWrongType_NamesTheIndex()
    {
        ToolArgs args = Parse("""{"handles":["1A7","2B",7,"4C"]}""");

        bool ok = args.TryGetStringArray("handles", 1, 1000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 2", error);
        Assert.Contains("a number", error);
    }

    [Fact]
    public void TryGetStringArray_EmptyElement_NamesTheIndex()
    {
        ToolArgs args = Parse("""{"handles":["1A7","  "]}""");

        bool ok = args.TryGetStringArray("handles", 1, 1000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void TryGetStringArray_ExceedsMaximum_IsRejected()
    {
        ToolArgs args = Parse("""{"handles":["a","b","c"]}""");

        bool ok = args.TryGetStringArray("handles", 1, 2, out _, out string error);

        Assert.False(ok);
        Assert.Contains("contained 3", error);
    }

    // ---- TryGetDoubleArray --------------------------------------------------------------

    [Fact]
    public void TryGetDoubleArray_ReadsNumbers()
    {
        ToolArgs args = Parse("""{"values":[1,2.5,-3]}""");

        bool ok = args.TryGetDoubleArray("values", 1, 10, out IReadOnlyList<double> value, out string error);

        Assert.True(ok, error);
        Assert.Equal(new[] { 1.0, 2.5, -3.0 }, value);
    }

    [Fact]
    public void TryGetDoubleArray_NonNumberElement_NamesTheIndex()
    {
        ToolArgs args = Parse("""{"values":[1,"two"]}""");

        bool ok = args.TryGetDoubleArray("values", 1, 10, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
    }

    // ---- Cross-field validation ---------------------------------------------------------

    [Fact]
    public void HasArgument_TreatsNullAsAbsent()
    {
        ToolArgs args = Parse("""{"a":null,"b":1}""");

        Assert.False(args.HasArgument("a"));
        Assert.True(args.HasArgument("b"));
        Assert.False(args.HasArgument("missing"));
    }

    [Fact]
    public void RequireExactlyOneOf_Both_IsRejected()
    {
        ToolArgs args = Parse("""{"scale":2,"scale_x":3}""");

        bool ok = args.RequireExactlyOneOf(new[] { "scale", "scale_x" }, out _, out string error);

        Assert.False(ok);
        Assert.Contains("Exactly one", error);
    }

    [Fact]
    public void RequireExactlyOneOf_Neither_IsRejected()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.RequireExactlyOneOf(new[] { "scale", "scale_x" }, out _, out string error);

        Assert.False(ok);
        Assert.Contains("is required", error);
        Assert.Contains("scale", error);
    }

    [Fact]
    public void RequireExactlyOneOf_One_ReportsWhichOne()
    {
        ToolArgs args = Parse("""{"scale_x":3}""");

        bool ok = args.RequireExactlyOneOf(new[] { "scale", "scale_x" }, out string chosen, out string error);

        Assert.True(ok, error);
        Assert.Equal("scale_x", chosen);
    }

    [Fact]
    public void RequireAtLeastOneOf_None_ListsEveryOption()
    {
        ToolArgs args = Parse("""{"handles":["1A7"]}""");

        bool ok = args.RequireAtLeastOneOf(
            new[] { "layer", "color_index", "visible" },
            subject: "property to change",
            out string error);

        Assert.False(ok);
        Assert.Contains("layer", error);
        Assert.Contains("color_index", error);
        Assert.Contains("visible", error);
        Assert.Contains("property to change", error);
    }

    [Fact]
    public void RequireAtLeastOneOf_OnePresent_Succeeds()
    {
        ToolArgs args = Parse("""{"layer":"WALLS"}""");

        bool ok = args.RequireAtLeastOneOf(new[] { "layer", "visible" }, "property to change", out string error);

        Assert.True(ok, error);
    }

    // ---- Element accessors --------------------------------------------------------------

    [Fact]
    public void TryGetObjectElement_AbsentIsSuccessButNotPresent()
    {
        ToolArgs args = Parse("{}");

        bool ok = args.TryGetObjectElement("attributes", out _, out bool present, out string error);

        Assert.True(ok, error);
        Assert.False(present);
    }

    [Fact]
    public void TryGetObjectElement_WrongType_IsRejected()
    {
        ToolArgs args = Parse("""{"attributes":[1,2]}""");

        bool ok = args.TryGetObjectElement("attributes", out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("an array", error);
    }

    [Fact]
    public void TryGetArrayElement_WrongType_IsRejected()
    {
        ToolArgs args = Parse("""{"mm_vertices":{}}""");

        bool ok = args.TryGetArrayElement("mm_vertices", out _, out string error);

        Assert.False(ok);
        Assert.Contains("an object", error);
    }
}
