using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class AttributeSpecReaderTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    private static bool Read(string attributesJson, out IReadOnlyList<AttributeSpec> specs, out string error)
    {
        return AttributeSpecReader.TryReadList(Parse($$"""{"attributes":{{attributesJson}}}"""), "attributes", out specs, out error);
    }

    [Fact]
    public void Absent_IsAnEmptyList()
    {
        bool ok = AttributeSpecReader.TryReadList(Parse("{}"), "attributes", out IReadOnlyList<AttributeSpec> specs, out string error);

        Assert.True(ok, error);
        Assert.Empty(specs);
    }

    [Fact]
    public void Minimal_FillsDefaults()
    {
        bool ok = Read("""[{"tag":"tag","mm_x":1,"mm_y":2,"mm_height":5}]""", out IReadOnlyList<AttributeSpec> specs, out string error);

        Assert.True(ok, error);
        AttributeSpec spec = Assert.Single(specs);
        Assert.Equal("TAG", spec.Tag);
        Assert.Equal("TAG", spec.Prompt);
        Assert.Equal("", spec.DefaultValue);
        Assert.Equal(new Mm_Point(1, 2, 0), spec.Mm_Position);
        Assert.Equal(5, spec.Mm_Height);
        Assert.Equal("left", spec.Justify);
        Assert.Equal(10, spec.AttachmentPoint);
        Assert.False(spec.IsConstant);
        Assert.False(spec.IsInvisible);
    }

    [Fact]
    public void Full_ReadsEveryKey()
    {
        bool ok = Read(
            """[{"tag":"REV","prompt":"Revision","default":"A","mm_x":1,"mm_y":2,"mm_z":3,"mm_height":2.5,"justify":"middle_center","constant":false,"invisible":true}]""",
            out IReadOnlyList<AttributeSpec> specs,
            out string error);

        Assert.True(ok, error);
        AttributeSpec spec = specs[0];
        Assert.Equal("Revision", spec.Prompt);
        Assert.Equal("A", spec.DefaultValue);
        Assert.Equal(3, spec.Mm_Position.Mm_Z);
        Assert.Equal("middle_center", spec.Justify);
        Assert.Equal(5, spec.AttachmentPoint);
        Assert.True(spec.IsInvisible);
    }

    [Fact]
    public void TagWithASpace_IsRefused()
    {
        Assert.False(Read("""[{"tag":"PART NO","mm_x":0,"mm_y":0,"mm_height":5}]""", out _, out string error));
        Assert.Contains("space", error);
        Assert.Contains("item 0", error);
    }

    [Fact]
    public void DuplicateTag_IsRefusedCaseInsensitively()
    {
        Assert.False(Read("""[{"tag":"A","mm_x":0,"mm_y":0,"mm_height":5},{"tag":"a","mm_x":0,"mm_y":0,"mm_height":5}]""", out _, out string error));
        Assert.Contains("item 1", error);
        Assert.Contains("more than once", error);
    }

    /// <summary>A misspelt key ("value" for "default") must fail, not silently drop the value.</summary>
    [Fact]
    public void UnknownKey_IsRefusedAndTheAllowedKeysListed()
    {
        Assert.False(Read("""[{"tag":"A","value":"x","mm_x":0,"mm_y":0,"mm_height":5}]""", out _, out string error));
        Assert.Contains("\"value\"", error);
        Assert.Contains("default", error);
    }

    [Theory]
    [InlineData("""[{"tag":"A","mm_y":0,"mm_height":5}]""", "mm_x")]
    [InlineData("""[{"tag":"A","mm_x":0,"mm_y":0}]""", "mm_height")]
    [InlineData("""[{"mm_x":0,"mm_y":0,"mm_height":5}]""", "tag")]
    public void MissingRequiredKey_IsNamed(string json, string key)
    {
        Assert.False(Read(json, out _, out string error));
        Assert.Contains($"\"{key}\"", error);
    }

    [Fact]
    public void ZeroHeight_IsRefused()
    {
        Assert.False(Read("""[{"tag":"A","mm_x":0,"mm_y":0,"mm_height":0}]""", out _, out string error));
        Assert.Contains("greater than 0", error);
    }

    [Fact]
    public void BadJustify_ListsTheValidOnes()
    {
        Assert.False(Read("""[{"tag":"A","mm_x":0,"mm_y":0,"mm_height":5,"justify":"fit"}]""", out _, out string error));
        Assert.Contains("middle_center", error);
    }

    [Fact]
    public void ConstantWithoutDefault_IsRefused()
    {
        Assert.False(Read("""[{"tag":"A","mm_x":0,"mm_y":0,"mm_height":5,"constant":true}]""", out _, out string error));
        Assert.Contains("constant", error);
        Assert.Contains("non-empty", error);
    }

    [Fact]
    public void LineBreakInDefault_IsRefused()
    {
        Assert.False(Read("""[{"tag":"A","default":"a\nb","mm_x":0,"mm_y":0,"mm_height":5}]""", out _, out string error));
        Assert.Contains("line break", error);
    }

    [Fact]
    public void TooMany_IsRefused()
    {
        string items = string.Join(",", Enumerable.Range(0, AttributeSpecReader.MaxAttributes + 1)
            .Select(i => $$"""{"tag":"T{{i}}","mm_x":0,"mm_y":0,"mm_height":5}"""));

        Assert.False(Read($"[{items}]", out _, out string error));
        Assert.Contains("maximum", error);
    }

    [Fact]
    public void Schema_RequiresTheSameKeysTheReaderRequires()
    {
        JsonObject schema = AttributeSpecReader.BuildSchema("d");
        JsonObject item = schema["items"]!.AsObject();
        string[] required = item["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

        Assert.Equal(new[] { "tag", "mm_x", "mm_y", "mm_height" }, required);
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(10, item["properties"]!.AsObject().Count);
    }
}
