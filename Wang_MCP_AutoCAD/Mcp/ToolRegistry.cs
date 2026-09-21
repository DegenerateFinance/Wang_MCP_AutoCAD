using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Name to tool. Populated once during Initialize() and read-only thereafter, so the
/// listener thread needs no lock to resolve a call.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// The groups tools/list advertises. Null means every group. A tool that is filtered out
    /// is still callable by name — this narrows what a model is offered, it is not access
    /// control, and hiding a tool from a client that already knows its name would only make
    /// failures confusing.
    /// </summary>
    private HashSet<string>? _enabledGroups;

    public int Count => _tools.Count;

    /// <summary>The number of tools tools/list would currently advertise.</summary>
    public int AdvertisedCount => EnumerateAdvertised().Count();

    public void Add(McpTool tool)
    {
        _tools[tool.Name] = tool;
    }

    public bool TryGet(string name, out McpTool? tool) => _tools.TryGetValue(name, out tool);

    /// <summary>
    /// Restricts tools/list to the named groups. Core is always included, so a session can
    /// never filter away its own introspection. Passing null or an empty set restores all.
    /// </summary>
    public void SetEnabledGroups(IEnumerable<string>? groups)
    {
        if (groups is null)
        {
            _enabledGroups = null;
            return;
        }

        HashSet<string> enabled = new(StringComparer.Ordinal) { ToolGroups.Core };
        bool askedForAnything = false;

        foreach (string group in groups)
        {
            askedForAnything = true;

            if (ToolGroups.TryCanonicalize(group, out string canonical))
            {
                enabled.Add(canonical);
            }
        }

        // An empty sequence means "no filter"; a non-empty one that matched nothing is a
        // filter that legitimately leaves only core. Collapsing the second case back to
        // "advertise everything" would silently ignore a caller's restriction.
        _enabledGroups = askedForAnything ? enabled : null;
    }

    /// <summary>The currently advertised groups, or all of them when unfiltered.</summary>
    public IReadOnlyList<string> EnabledGroups =>
        _enabledGroups is null ? ToolGroups.All : _enabledGroups.OrderBy(g => g, StringComparer.Ordinal).ToList();

    /// <summary>Tools in a stable order, so tools/list does not shuffle between calls.</summary>
    public JsonArray ToListArray()
    {
        JsonArray array = new();
        foreach (McpTool tool in EnumerateAdvertised())
        {
            array.Add(tool.ToListEntry());
        }

        return array;
    }

    private IEnumerable<McpTool> EnumerateAdvertised()
    {
        return _tools.Values
            .Where(t => _enabledGroups is null || _enabledGroups.Contains(t.Group))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
    }
}
