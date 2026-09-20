using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// A tool descriptor. The <see cref="Handler"/> delegate is the entire boundary between the
/// protocol stack and AutoCAD: this file never names an Autodesk type, and the handlers
/// supplied by the Acad side close over everything they need.
/// </summary>
public sealed class McpTool
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>The JSON Schema for the tool's arguments. Must be an object schema.</summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>
    /// True when the handler touches the AutoCAD database or editor, and must therefore be
    /// marshalled onto the document thread by the dispatcher. False for pure tools like
    /// acad_ping, which then work even with no drawing open.
    /// </summary>
    public bool RequiresDocument { get; init; } = true;

    public required Func<ToolArgs, ToolResult> Handler { get; init; }

    /// <summary>Renders the entry as it appears in a tools/list response.</summary>
    public JsonObject ToListEntry()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone(),
        };
    }
}
