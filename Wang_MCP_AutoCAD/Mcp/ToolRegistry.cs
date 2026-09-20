using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Name to tool. Populated once during Initialize() and read-only thereafter, so the
/// listener thread needs no lock to resolve a call.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);

    public int Count => _tools.Count;

    public void Add(McpTool tool)
    {
        _tools[tool.Name] = tool;
    }

    public bool TryGet(string name, out McpTool? tool) => _tools.TryGetValue(name, out tool);

    /// <summary>Tools in a stable order, so tools/list does not shuffle between calls.</summary>
    public JsonArray ToListArray()
    {
        JsonArray array = new();
        foreach (McpTool tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            array.Add(tool.ToListEntry());
        }

        return array;
    }
}
