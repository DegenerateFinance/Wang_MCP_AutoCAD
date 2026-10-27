using System.Text.Json;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class AttributeAssignmentTests
{
    private static readonly AttributeSlot[] Slots =
    {
        new("TAG", IsConstant: false, IsMultiline: false),
        new("NOTES", IsConstant: false, IsMultiline: true),
        new("MAKER", IsConstant: true, IsMultiline: false),
    };

    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    private static Dictionary<string, string> Map(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public void TryPlan_SetsRequestedSlotsAndLeavesTheRestNull()
    {
        bool ok = AttributeAssignment.TryPlan(Slots, Map(("tag", "B7")), out string?[] values, out string error);

        Assert.True(ok, error);
        Assert.Equal("B7", values[0]);
        Assert.Null(values[1]);
        Assert.Null(values[2]);
    }

    [Fact]
    public void TryPlan_UnknownTag_ListsTheSettableTags()
    {
        bool ok = AttributeAssignment.TryPlan(Slots, Map(("NOPE", "x")), out _, out string error);

        Assert.False(ok);
        Assert.Contains("\"NOPE\"", error);
        Assert.Contains("\"TAG\"", error);
        Assert.Contains("\"NOTES\"", error);
        Assert.DoesNotContain("\"MAKER\", ", error.Split("settable")[1]);
        Assert.StartsWith("Nothing was changed", error);
    }

    [Fact]
    public void TryPlan_ConstantTagIsRefused()
    {
        bool ok = AttributeAssignment.TryPlan(Slots, Map(("MAKER", "x")), out _, out string error);

        Assert.False(ok);
        Assert.Contains("constant", error);
        Assert.Contains("\"MAKER\"", error);
    }

    [Fact]
    public void TryPlan_LineBreakIsRefusedOnSingleLineButAllowedOnMultiline()
    {
        Assert.False(AttributeAssignment.TryPlan(Slots, Map(("TAG", "a\nb")), out _, out string error));
        Assert.Contains("single-line", error);

        Assert.True(AttributeAssignment.TryPlan(Slots, Map(("NOTES", "a\nb")), out string?[] values, out error), error);
        Assert.Equal("a\nb", values[1]);
    }

    /// <summary>All problems in one message, so a caller fixes them in one retry.</summary>
    [Fact]
    public void TryPlan_ReportsEveryProblemAtOnce()
    {
        bool ok = AttributeAssignment.TryPlan(Slots, Map(("NOPE", "x"), ("MAKER", "y")), out string?[] values, out string error);

        Assert.False(ok);
        Assert.Contains("\"NOPE\"", error);
        Assert.Contains("\"MAKER\"", error);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void TryPlan_DuplicateTagSetsEverySlotWithIt()
    {
        AttributeSlot[] slots = { new("LINE", false, false), new("LINE", false, false) };

        bool ok = AttributeAssignment.TryPlan(slots, Map(("LINE", "same")), out string?[] values, out string error);

        Assert.True(ok, error);
        Assert.Equal(new[] { "same", "same" }, values);
    }

    [Fact]
    public void TryPlan_NoAttributesAtAll_SaysSo()
    {
        bool ok = AttributeAssignment.TryPlan(Array.Empty<AttributeSlot>(), Map(("TAG", "x")), out _, out string error);

        Assert.False(ok);
        Assert.Contains("no settable attributes", error);
    }

    [Fact]
    public void TryPlan_EmptyRequestIsANoOp()
    {
        bool ok = AttributeAssignment.TryPlan(Slots, Map(), out string?[] values, out string error);

        Assert.True(ok, error);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void TryReadValueMap_AbsentIsEmpty()
    {
        bool ok = AttributeAssignment.TryReadValueMap(Parse("{}"), "attributes", out IReadOnlyDictionary<string, string> values, out string error);

        Assert.True(ok, error);
        Assert.Empty(values);
    }

    [Fact]
    public void TryReadValueMap_ReadsStringsAndAllowsEmptyValues()
    {
        bool ok = AttributeAssignment.TryReadValueMap(Parse("""{"attributes":{"TAG":"A1","REV":""}}"""), "attributes", out IReadOnlyDictionary<string, string> values, out string error);

        Assert.True(ok, error);
        Assert.Equal("A1", values["tag"]);
        Assert.Equal("", values["REV"]);
    }

    [Fact]
    public void TryReadValueMap_NonStringValueIsRefused()
    {
        bool ok = AttributeAssignment.TryReadValueMap(Parse("""{"attributes":{"TAG":3}}"""), "attributes", out _, out string error);

        Assert.False(ok);
        Assert.Contains("must be a string", error);
    }

    [Fact]
    public void TryReadValueMap_CaseInsensitiveDuplicateIsRefused()
    {
        bool ok = AttributeAssignment.TryReadValueMap(Parse("""{"attributes":{"TAG":"a","tag":"b"}}"""), "attributes", out _, out string error);

        Assert.False(ok);
        Assert.Contains("more than once", error);
    }
}
