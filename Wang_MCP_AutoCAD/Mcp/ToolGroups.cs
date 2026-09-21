namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The functional groups a tool can belong to. A session can advertise a subset, which keeps
/// the effective tool count — and the schema bytes in every request's context — down without
/// giving up any capability.
/// </summary>
public static class ToolGroups
{
    /// <summary>Server introspection. Always advertised; cannot be filtered out.</summary>
    public const string Core = "core";

    /// <summary>Reading the drawing: listing entities, reading their properties.</summary>
    public const string Query = "query";

    /// <summary>Layer creation and state.</summary>
    public const string Layers = "layers";

    /// <summary>Creating geometry: lines, arcs, circles, polylines.</summary>
    public const string Draw = "draw";

    /// <summary>Creating and editing text.</summary>
    public const string Text = "text";

    /// <summary>Changing entity properties, moving, copying, deleting.</summary>
    public const string Modify = "modify";

    /// <summary>Block definitions, references, attributes and dynamic properties.</summary>
    public const string Blocks = "blocks";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Core, Query, Layers, Draw, Text, Modify, Blocks,
    };

    /// <summary>
    /// Matches a requested group name against the known list, case-insensitively, returning
    /// the canonical casing.
    /// </summary>
    public static bool TryCanonicalize(string? raw, out string group)
    {
        group = "";

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (string candidate in All)
        {
            if (string.Equals(candidate, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                group = candidate;
                return true;
            }
        }

        return false;
    }
}
