using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>Block listing and redefinition rules, insertion scale, and the new schema helpers.</summary>
public sealed class BlockRulesTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    // ---- ShouldList ---------------------------------------------------------------------------

    [Fact]
    public void ShouldList_OrdinaryBlockIsListed()
    {
        Assert.True(BlockRules.ShouldList(false, false, false, false, includeAnonymous: false, includeXrefs: false));
    }

    [Fact]
    public void ShouldList_LayoutIsNeverListedEvenWithEveryOptIn()
    {
        Assert.False(BlockRules.ShouldList(isLayout: true, false, false, false, includeAnonymous: true, includeXrefs: true));
    }

    [Fact]
    public void ShouldList_AnonymousNeedsOptIn()
    {
        Assert.False(BlockRules.ShouldList(false, isAnonymous: true, false, false, includeAnonymous: false, includeXrefs: false));
        Assert.True(BlockRules.ShouldList(false, isAnonymous: true, false, false, includeAnonymous: true, includeXrefs: false));
    }

    [Fact]
    public void ShouldList_XrefNeedsOptInAndMustBeLoaded()
    {
        Assert.False(BlockRules.ShouldList(false, false, isXref: true, false, includeAnonymous: false, includeXrefs: false));
        Assert.True(BlockRules.ShouldList(false, false, isXref: true, false, includeAnonymous: false, includeXrefs: true));
        Assert.False(BlockRules.ShouldList(false, false, isXref: true, isUnloaded: true, includeAnonymous: false, includeXrefs: true));
    }

    // ---- TryCheckRedefinable ------------------------------------------------------------------

    [Fact]
    public void Redefinable_OrdinaryBlockPasses()
    {
        Assert.True(BlockRules.TryCheckRedefinable("DOOR", false, false, false, false, out string error), error);
    }

    /// <summary>The refusal must point at the way forward, not just say no.</summary>
    [Fact]
    public void Redefinable_DynamicBlockIsRefusedAndPointsAtConvertToStatic()
    {
        bool ok = BlockRules.TryCheckRedefinable("DOOR", false, false, false, isDynamic: true, out string error);

        Assert.False(ok);
        Assert.Contains("dynamic", error);
        Assert.Contains("acad_convert_block_to_static", error);
    }

    [Theory]
    [InlineData(true, false, false, "layout")]
    [InlineData(false, true, false, "external reference")]
    [InlineData(false, false, true, "anonymous")]
    public void Redefinable_OtherRefusalsNameTheReason(bool isLayout, bool isXref, bool isAnonymous, string expected)
    {
        bool ok = BlockRules.TryCheckRedefinable("X", isLayout, isXref, isAnonymous, false, out string error);

        Assert.False(ok);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void SourceActions_DefaultIsReplaceAndListedFirst()
    {
        Assert.Equal("replace", BlockRules.DefaultSourceAction);
        Assert.Equal(new[] { "replace", "keep", "erase" }, BlockRules.SourceActions);
    }

    // ---- InsertScale --------------------------------------------------------------------------

    [Fact]
    public void InsertScale_DefaultsToOne()
    {
        bool ok = InsertScale.TryRead(Parse("{}"), out double x, out double y, out double z, out string error);

        Assert.True(ok, error);
        Assert.Equal((1.0, 1.0, 1.0), (x, y, z));
    }

    [Fact]
    public void InsertScale_UniformAppliesToEveryAxis()
    {
        bool ok = InsertScale.TryRead(Parse("""{"scale":2.5}"""), out double x, out double y, out double z, out string error);

        Assert.True(ok, error);
        Assert.Equal((2.5, 2.5, 2.5), (x, y, z));
    }

    [Fact]
    public void InsertScale_PerAxisDefaultsTheOthersToOne()
    {
        bool ok = InsertScale.TryRead(Parse("""{"scale_x":-1}"""), out double x, out double y, out double z, out string error);

        Assert.True(ok, error);
        Assert.Equal((-1.0, 1.0, 1.0), (x, y, z));
        Assert.False(InsertScale.IsUniform(x, y, z));
    }

    [Fact]
    public void InsertScale_MixingBothFormsIsRefused()
    {
        Assert.False(InsertScale.TryRead(Parse("""{"scale":2,"scale_y":3}"""), out _, out _, out _, out string error));
        Assert.Contains("not both", error);
    }

    [Theory]
    [InlineData("""{"scale":0}""")]
    [InlineData("""{"scale_z":0}""")]
    public void InsertScale_ZeroIsRefused(string json)
    {
        Assert.False(InsertScale.TryRead(Parse(json), out _, out _, out _, out string error));
        Assert.Contains("must not be 0", error);
    }

    [Fact]
    public void InsertScale_UniformRequiredMessageNamesTheBlockAndTheFix()
    {
        string message = InsertScale.DescribeUniformRequired("DOOR", 1, 2, 1);

        Assert.Contains("DOOR", message);
        Assert.Contains("\"scale\"", message);
    }

    // ---- ToolSchema helpers -------------------------------------------------------------------

    [Fact]
    public void ValueMap_AllowsStringsAndNumbers()
    {
        JsonObject schema = ToolSchema.Object().ValueMap("properties", "d", required: true).Build();
        JsonArray types = schema["properties"]!["properties"]!["additionalProperties"]!["type"]!.AsArray();

        Assert.Equal(new[] { "string", "number" }, types.Select(t => t!.GetValue<string>()));
        Assert.Contains(schema["required"]!.AsArray(), n => n!.GetValue<string>() == "properties");
    }

    [Fact]
    public void Property_StoresTheGivenSchemaVerbatim()
    {
        JsonObject custom = new() { ["type"] = "array" };
        JsonObject schema = ToolSchema.Object().Property("attributes", custom).Build();

        Assert.Equal("array", schema["properties"]!["attributes"]!["type"]!.GetValue<string>());
        Assert.Empty(schema["required"]!.AsArray());
    }
}
