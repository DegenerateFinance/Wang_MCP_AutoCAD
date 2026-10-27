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

    /// <summary>
    /// The functional group this tool belongs to, used to narrow what a session advertises.
    ///
    /// Tool-selection accuracy falls off as the list grows, and every tool's schema sits in
    /// the context window on every request — so a session doing layout work should not have
    /// to carry the block-redefinition tools. <see cref="ToolGroups"/> holds the names.
    /// </summary>
    public string Group { get; init; } = ToolGroups.Core;

    /// <summary>The JSON Schema for the tool's arguments. Must be an object schema.</summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>
    /// True when the handler touches the AutoCAD database or editor, and must therefore be
    /// marshalled onto the document thread by the dispatcher. False for pure tools like
    /// acad_ping, which then work even with no drawing open.
    /// </summary>
    public bool RequiresDocument { get; init; } = true;

    /// <summary>
    /// True when the handler only reads the drawing. Only read-only tools may target a
    /// drawing that is not the active one: verified live, a write from inside MCPSTART to a
    /// non-active drawing fails with eWrongDatabase, so writes are confined to the active
    /// drawing and refused up front otherwise.
    ///
    /// Defaults to false on purpose — a new tool that forgets to set this gets the stricter
    /// rule, which fails loudly, rather than the looser one, which would fail deep inside a
    /// transaction.
    /// </summary>
    public bool IsReadOnly { get; init; } = false;

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
