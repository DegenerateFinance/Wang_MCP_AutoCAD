using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Choosing the target drawing. The case that matters most is the ambiguous one: two open
/// drawings with the same file name must be refused, never resolved to whichever is first.
/// </summary>
[Collection(LogGlobalCollection.Name)]
public sealed class DrawingSelectorTests
{
    private static readonly DrawingInfo[] Open =
    {
        new(@"C:\Jobs\A\Plan.dwg", IsActive: true),
        new(@"C:\Jobs\A\Section.dwg", IsActive: false),
        new("Drawing1.dwg", IsActive: false),
    };

    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    [Theory]
    [InlineData(@"C:\Jobs\A\Section.dwg", 1)]
    [InlineData(@"c:\jobs\a\SECTION.DWG", 1)]
    [InlineData("C:/Jobs/A/Section.dwg", 1)]
    [InlineData("Section.dwg", 1)]
    [InlineData("section", 1)]
    [InlineData("Plan", 0)]
    [InlineData("Drawing1.dwg", 2)]
    [InlineData("Drawing1", 2)]
    [InlineData("  Plan.dwg  ", 0)]
    public void TrySelect_MatchesPathOrFileName(string requested, int expectedIndex)
    {
        bool ok = DrawingSelector.TrySelect(Open, requested, requireActive: false, out int index, out string error);

        Assert.True(ok, error);
        Assert.Equal(expectedIndex, index);
    }

    // ---- active-only writes (verified live: a write to a non-active drawing is eWrongDatabase)

    [Fact]
    public void TrySelect_WriteToNonActive_IsRefusedNamingBothDrawingsAndTheFix()
    {
        bool ok = DrawingSelector.TrySelect(Open, "Section", requireActive: true, out int index, out string error);

        Assert.False(ok);
        Assert.Equal(-1, index);
        Assert.Contains("\"Section.dwg\" is not the active drawing", error);
        Assert.Contains("nothing was changed", error);
        Assert.Contains("Active: Plan.dwg", error);
        Assert.Contains("switch to it in AutoCAD", error);
        Assert.DoesNotContain("WrongDatabase", error);
    }

    [Fact]
    public void TrySelect_WriteToActive_ByNameIsAllowed()
    {
        bool ok = DrawingSelector.TrySelect(Open, "Plan.dwg", requireActive: true, out int index, out string error);

        Assert.True(ok, error);
        Assert.Equal(0, index);
    }

    [Fact]
    public void TrySelect_ReadOfNonActive_IsAllowed()
    {
        bool ok = DrawingSelector.TrySelect(Open, "Section", requireActive: false, out int index, out string error);

        Assert.True(ok, error);
        Assert.Equal(1, index);
    }

    /// <summary>A typo on a write must say "no match", not a misleading "not active".</summary>
    [Fact]
    public void TrySelect_WriteWithUnknownName_ReportsNoMatchNotNotActive()
    {
        bool ok = DrawingSelector.TrySelect(Open, "Elevation", requireActive: true, out _, out string error);

        Assert.False(ok);
        Assert.Contains("No open drawing matches", error);
        Assert.DoesNotContain("not the active drawing", error);
    }

    [Fact]
    public void DescribeNotActive_WithNoActiveDrawing_SaysNone()
    {
        DrawingInfo[] open = { new(@"C:\Jobs\A\Plan.dwg", IsActive: false) };

        string message = DrawingSelector.DescribeNotActive(open, @"C:\Jobs\A\Plan.dwg");

        Assert.Contains("Active: none", message);
    }

    [Fact]
    public void TrySelect_NoMatch_ListsTheOpenDrawingsAndMarksTheActiveOne()
    {
        bool ok = DrawingSelector.TrySelect(Open, "Elevation", requireActive: false, out int index, out string error);

        Assert.False(ok);
        Assert.Equal(-1, index);
        Assert.Contains("Elevation", error);
        Assert.Contains(@"C:\Jobs\A\Plan.dwg (active)", error);
        Assert.Contains("Drawing1.dwg", error);
        Assert.Contains("acad_list_drawings", error);
    }

    /// <summary>A partial name is not a match — "Pla" must not resolve to Plan.dwg.</summary>
    [Fact]
    public void TrySelect_PrefixIsNotAMatch()
    {
        Assert.False(DrawingSelector.TrySelect(Open, "Pla", requireActive: false, out _, out _));
    }

    [Fact]
    public void TrySelect_SharedFileName_IsAmbiguousAndNamesBothPaths()
    {
        DrawingInfo[] open =
        {
            new(@"C:\Jobs\A\Plan.dwg", IsActive: true),
            new(@"C:\Jobs\B\Plan.dwg", IsActive: false),
        };

        bool ok = DrawingSelector.TrySelect(open, "Plan.dwg", requireActive: false, out int index, out string error);

        Assert.False(ok);
        Assert.Equal(-1, index);
        Assert.Contains("ambiguous", error);
        Assert.Contains(@"C:\Jobs\A\Plan.dwg", error);
        Assert.Contains(@"C:\Jobs\B\Plan.dwg", error);
        Assert.Contains("full path", error);
    }

    /// <summary>The full path still disambiguates when the file name cannot.</summary>
    [Fact]
    public void TrySelect_SharedFileName_FullPathStillWorks()
    {
        DrawingInfo[] open =
        {
            new(@"C:\Jobs\A\Plan.dwg", IsActive: true),
            new(@"C:\Jobs\B\Plan.dwg", IsActive: false),
        };

        bool ok = DrawingSelector.TrySelect(open, @"C:\Jobs\B\Plan.dwg", requireActive: false, out int index, out string error);

        Assert.True(ok, error);
        Assert.Equal(1, index);
    }

    [Fact]
    public void TrySelect_NothingOpen_SaysSo()
    {
        bool ok = DrawingSelector.TrySelect(Array.Empty<DrawingInfo>(), "Plan", requireActive: false, out _, out string error);

        Assert.False(ok);
        Assert.Contains("No drawing is open", error);
    }

    [Fact]
    public void FindAmbiguousFileNames_FlagsOnlySharedNames()
    {
        DrawingInfo[] open =
        {
            new(@"C:\Jobs\A\Plan.dwg", IsActive: true),
            new(@"C:\Jobs\B\plan.dwg", IsActive: false),
            new(@"C:\Jobs\A\Section.dwg", IsActive: false),
        };

        HashSet<string> ambiguous = DrawingSelector.FindAmbiguousFileNames(open);

        Assert.Single(ambiguous);
        Assert.Contains("Plan.dwg", ambiguous);
        Assert.DoesNotContain("Section.dwg", ambiguous);
    }

    [Theory]
    [InlineData(@"C:\Jobs\A\Plan.dwg", "Plan.dwg")]
    [InlineData("C:/Jobs/A/Plan.dwg", "Plan.dwg")]
    [InlineData("Drawing1.dwg", "Drawing1.dwg")]
    public void GetFileName_HandlesBothSlashesAndUnsavedDrawings(string name, string expected)
    {
        Assert.Equal(expected, DrawingSelector.GetFileName(name));
    }

    // ---- the argument ---------------------------------------------------------------------

    [Fact]
    public void TryReadArgument_AbsentMeansActiveDrawing()
    {
        bool ok = DrawingSelector.TryReadArgument(Parse("{}"), out string? drawing, out string error);

        Assert.True(ok, error);
        Assert.Null(drawing);
    }

    [Fact]
    public void TryReadArgument_ReadsTheName()
    {
        bool ok = DrawingSelector.TryReadArgument(Parse("""{"drawing":"Plan.dwg"}"""), out string? drawing, out string error);

        Assert.True(ok, error);
        Assert.Equal("Plan.dwg", drawing);
    }

    [Theory]
    [InlineData("""{"drawing":""}""")]
    [InlineData("""{"drawing":"   "}""")]
    public void TryReadArgument_BlankIsAnErrorNotASilentDefault(string json)
    {
        bool ok = DrawingSelector.TryReadArgument(Parse(json), out string? drawing, out string error);

        Assert.False(ok);
        Assert.Null(drawing);
        Assert.Contains("must not be empty", error);
    }

    [Fact]
    public void TryReadArgument_NonStringIsRejected()
    {
        bool ok = DrawingSelector.TryReadArgument(Parse("""{"drawing":3}"""), out _, out string error);

        Assert.False(ok);
        Assert.Contains("drawing", error);
    }

    // ---- schema injection -----------------------------------------------------------------

    private static McpTool MakeTool(
        string name,
        bool requiresDocument,
        JsonObject? schema = null,
        bool isReadOnly = false)
    {
        return new McpTool
        {
            Name = name,
            Description = "test",
            InputSchema = schema ?? ToolSchema.Object().Number("mm_x", "x").Build(),
            RequiresDocument = requiresDocument,
            IsReadOnly = isReadOnly,
            Handler = _ => ToolResult.Ok(new JsonObject()),
        };
    }

    /// <summary>A forgotten flag must fall on the strict side: write, active drawing only.</summary>
    [Fact]
    public void McpTool_DefaultsToWrite()
    {
        Assert.False(MakeTool("t", requiresDocument: true).IsReadOnly);
    }

    [Fact]
    public void Registry_WriteToolsDescribeDrawingAsAGuard()
    {
        ToolRegistry registry = new();
        registry.Add(MakeTool("write_tool", requiresDocument: true));

        string description = registry.ToListArray()[0]!["inputSchema"]!["properties"]!["drawing"]!["description"]!.GetValue<string>();

        Assert.Equal(DrawingSelector.WriteDescription, description);
        Assert.Contains("ONLY the active drawing", description);
    }

    [Fact]
    public void Registry_ReadToolsDescribeDrawingAsAnyOpenDrawing()
    {
        ToolRegistry registry = new();
        registry.Add(MakeTool("read_tool", requiresDocument: true, isReadOnly: true));

        string description = registry.ToListArray()[0]!["inputSchema"]!["properties"]!["drawing"]!["description"]!.GetValue<string>();

        Assert.Equal(DrawingSelector.ReadDescription, description);
        Assert.Contains("Any open drawing works", description);
    }

    [Fact]
    public void Registry_InjectsDrawingIntoEveryDocumentTool()
    {
        ToolRegistry registry = new();
        registry.Add(MakeTool("doc_tool", requiresDocument: true));

        JsonObject properties = registry.ToListArray()[0]!["inputSchema"]!["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("drawing"));
        Assert.True(properties.ContainsKey("mm_x"));
        Assert.Equal("string", properties["drawing"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Registry_DoesNotInjectDrawingIntoNonDocumentTools()
    {
        ToolRegistry registry = new();
        registry.Add(MakeTool("pure_tool", requiresDocument: false));

        JsonObject properties = registry.ToListArray()[0]!["inputSchema"]!["properties"]!.AsObject();

        Assert.False(properties.ContainsKey("drawing"));
    }

    /// <summary>The argument is optional: injecting it must never make it required.</summary>
    [Fact]
    public void Registry_InjectedDrawingIsNotRequired()
    {
        ToolRegistry registry = new();
        registry.Add(MakeTool("doc_tool", requiresDocument: true, ToolSchema.Object().Number("mm_x", "x", required: true).Build()));

        JsonArray required = registry.ToListArray()[0]!["inputSchema"]!["required"]!.AsArray();

        Assert.DoesNotContain(required, node => node!.GetValue<string>() == "drawing");
    }

    [Fact]
    public void Registry_KeepsAToolsOwnDrawingProperty()
    {
        JsonObject schema = ToolSchema.Object().String("drawing", "custom").Build();
        ToolRegistry registry = new();
        registry.Add(MakeTool("doc_tool", requiresDocument: true, schema));

        JsonObject properties = registry.ToListArray()[0]!["inputSchema"]!["properties"]!.AsObject();

        Assert.Equal("custom", properties["drawing"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void Registry_AddsPropertiesObjectWhenTheSchemaHasNone()
    {
        JsonObject schema = new() { ["type"] = "object" };
        ToolRegistry registry = new();
        registry.Add(MakeTool("doc_tool", requiresDocument: true, schema));

        JsonObject properties = registry.ToListArray()[0]!["inputSchema"]!["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("drawing"));
    }

    // ---- dispatcher pass-through ----------------------------------------------------------

    private static (McpDispatcher, FakeDocumentGateway) MakeDispatcher()
    {
        FakeDocumentGateway gateway = new();
        ToolRegistry registry = new();
        registry.Add(MakeTool("doc_tool", requiresDocument: true));
        registry.Add(MakeTool("read_tool", requiresDocument: true, isReadOnly: true));
        return (new McpDispatcher(registry, gateway, new McpServerOptions()), gateway);
    }

    [Fact]
    public void Dispatcher_WriteToolDemandsTheActiveDrawing()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway gateway) = MakeDispatcher();

        dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"doc_tool","arguments":{"drawing":"Plan"}}}""");

        Assert.True(gateway.LastRequireActiveDrawing);
    }

    [Fact]
    public void Dispatcher_ReadToolDoesNotDemandTheActiveDrawing()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway gateway) = MakeDispatcher();

        dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_tool","arguments":{"drawing":"Section"}}}""");

        Assert.False(gateway.LastRequireActiveDrawing);
        Assert.Equal("Section", gateway.LastDrawing);
    }

    [Fact]
    public void Dispatcher_PassesTheDrawingArgumentToTheGateway()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway gateway) = MakeDispatcher();

        dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"doc_tool","arguments":{"drawing":"Section.dwg"}}}""");

        Assert.Equal(1, gateway.RunCount);
        Assert.Equal("Section.dwg", gateway.LastDrawing);
    }

    [Fact]
    public void Dispatcher_NoDrawingArgumentPassesNull()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway gateway) = MakeDispatcher();

        dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"doc_tool","arguments":{}}}""");

        Assert.Equal(1, gateway.RunCount);
        Assert.Null(gateway.LastDrawing);
    }

    /// <summary>A bad "drawing" must fail before the gateway locks anything.</summary>
    [Fact]
    public void Dispatcher_BlankDrawingIsAToolErrorAndNeverReachesTheGateway()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway gateway) = MakeDispatcher();

        DispatchResult result = dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"doc_tool","arguments":{"drawing":""}}}""");

        Assert.True(result.IsToolError);
        Assert.Equal(0, gateway.RunCount);
        Assert.Contains("must not be empty", result.ResponseJson);
    }

    [Fact]
    public void Initialize_InstructionsMentionMultipleDrawings()
    {
        (McpDispatcher dispatcher, FakeDocumentGateway _) = MakeDispatcher();

        DispatchResult result = dispatcher.Dispatch("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        string instructions = JsonNode.Parse(result.ResponseJson!)!["result"]!["instructions"]!.GetValue<string>();

        Assert.Contains("acad_list_drawings", instructions);
        Assert.Contains("\"drawing\"", instructions);
        Assert.Contains("ONLY the active drawing", instructions);
    }
}
