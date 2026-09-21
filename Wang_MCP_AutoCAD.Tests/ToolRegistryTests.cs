using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class ToolRegistryTests
{
    private static McpTool Tool(string name, string group)
    {
        return new McpTool
        {
            Name = name,
            Description = $"Test tool {name}.",
            InputSchema = ToolSchema.Object().Build(),
            Group = group,
            RequiresDocument = false,
            Handler = _ => ToolResult.Ok("ok"),
        };
    }

    private static List<string> NamesIn(JsonArray listing)
    {
        List<string> names = new();
        foreach (JsonNode? node in listing)
        {
            names.Add(node!["name"]!.GetValue<string>());
        }

        return names;
    }

    [Fact]
    public void ToListArray_IsSortedByName()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("zulu", ToolGroups.Draw));
        registry.Add(Tool("alpha", ToolGroups.Draw));
        registry.Add(Tool("mike", ToolGroups.Draw));

        Assert.Equal(new[] { "alpha", "mike", "zulu" }, NamesIn(registry.ToListArray()));
    }

    [Fact]
    public void Unfiltered_AdvertisesEverything()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("a", ToolGroups.Draw));
        registry.Add(Tool("b", ToolGroups.Blocks));

        Assert.Equal(2, registry.AdvertisedCount);
        Assert.Equal(ToolGroups.All, registry.EnabledGroups);
    }

    [Fact]
    public void SetEnabledGroups_NarrowsTheListing()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("draw_one", ToolGroups.Draw));
        registry.Add(Tool("block_one", ToolGroups.Blocks));
        registry.Add(Tool("layer_one", ToolGroups.Layers));

        registry.SetEnabledGroups(new[] { ToolGroups.Draw });

        Assert.Equal(new[] { "draw_one" }, NamesIn(registry.ToListArray()));
    }

    /// <summary>
    /// A session must never be able to filter away its own introspection, or it cannot report
    /// what it can do.
    /// </summary>
    [Fact]
    public void SetEnabledGroups_AlwaysKeepsCore()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("acad_ping", ToolGroups.Core));
        registry.Add(Tool("block_one", ToolGroups.Blocks));

        registry.SetEnabledGroups(new[] { ToolGroups.Draw });

        Assert.Contains("acad_ping", NamesIn(registry.ToListArray()));
    }

    [Fact]
    public void SetEnabledGroups_IsCaseInsensitive()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("draw_one", ToolGroups.Draw));

        registry.SetEnabledGroups(new[] { "DRAW" });

        Assert.Contains("draw_one", NamesIn(registry.ToListArray()));
    }

    [Fact]
    public void SetEnabledGroups_UnknownGroupIsIgnored()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("draw_one", ToolGroups.Draw));
        registry.Add(Tool("core_one", ToolGroups.Core));

        registry.SetEnabledGroups(new[] { "nonsense" });

        // Only core survives: the unknown name matched nothing, and core is always kept.
        Assert.Equal(new[] { "core_one" }, NamesIn(registry.ToListArray()));
    }

    [Fact]
    public void SetEnabledGroups_NullRestoresEverything()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("draw_one", ToolGroups.Draw));
        registry.Add(Tool("block_one", ToolGroups.Blocks));

        registry.SetEnabledGroups(new[] { ToolGroups.Draw });
        registry.SetEnabledGroups(null);

        Assert.Equal(2, registry.AdvertisedCount);
    }

    /// <summary>
    /// Filtering narrows what is advertised, not what exists: a client that already knows a
    /// tool's name still gets a working call rather than a confusing "unknown tool".
    /// </summary>
    [Fact]
    public void FilteredOutTool_IsStillResolvableByName()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("block_one", ToolGroups.Blocks));

        registry.SetEnabledGroups(new[] { ToolGroups.Draw });

        Assert.True(registry.TryGet("block_one", out McpTool? tool));
        Assert.NotNull(tool);
    }

    [Fact]
    public void DefaultGroup_IsCore()
    {
        McpTool tool = new()
        {
            Name = "untagged",
            Description = "No group given.",
            InputSchema = ToolSchema.Object().Build(),
            Handler = _ => ToolResult.Ok("ok"),
        };

        Assert.Equal(ToolGroups.Core, tool.Group);
    }

    [Fact]
    public void Add_LastWinsOnDuplicateName()
    {
        ToolRegistry registry = new();
        registry.Add(Tool("dup", ToolGroups.Draw));
        registry.Add(Tool("dup", ToolGroups.Blocks));

        Assert.Equal(1, registry.Count);
        registry.TryGet("dup", out McpTool? tool);
        Assert.Equal(ToolGroups.Blocks, tool!.Group);
    }

    [Theory]
    [InlineData("draw", true)]
    [InlineData("DRAW", true)]
    [InlineData("  blocks  ", true)]
    [InlineData("nope", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryCanonicalize_MatchesKnownGroups(string? raw, bool expected)
    {
        Assert.Equal(expected, ToolGroups.TryCanonicalize(raw, out _));
    }
}
