using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// One open drawing as the protocol side sees it. <see cref="Name"/> is AutoCAD's
/// Document.Name: the full path for a saved drawing, or just "Drawing1.dwg" for one that has
/// never been saved.
/// </summary>
public readonly record struct DrawingInfo(string Name, bool IsActive);

/// <summary>
/// Picks which open drawing a tool call targets, and builds the "drawing" argument every
/// document tool accepts.
///
/// The rule, settled live (tests/results/PHASE3B_DRAWINGS_RESULTS.md): reads may target any
/// open drawing, writes only the active one. A write from inside MCPSTART to a non-active
/// drawing fails with eWrongDatabase; rather than make MCPSTART a session command to get
/// around that, the server edits the active drawing only. On a write tool "drawing" is
/// therefore a guard — it names the drawing the caller believes it is editing, and a
/// mismatch is refused before AutoCAD is touched. Handles are why the guard matters: they are
/// only unique within one drawing, so a handle queried in drawing A often names an unrelated
/// entity in B, and a user switching tabs between two calls would otherwise redirect the
/// second one silently.
/// </summary>
public static class DrawingSelector
{
    public const string ArgumentName = "drawing";

    public const string ReadDescription =
        "Which open drawing to read: its file name (\"Plan.dwg\", or \"Plan\") or full path, "
        + "as listed by acad_list_drawings. Any open drawing works; defaults to the active "
        + "one. Pass it whenever more than one drawing is open — handles are only unique "
        + "within one drawing.";

    public const string WriteDescription =
        "Guard: the drawing you expect to change, by file name or full path as listed by "
        + "acad_list_drawings. This tool edits ONLY the active drawing; if this names any "
        + "other drawing the call is refused and nothing changes. Defaults to the active "
        + "drawing. Pass it whenever more than one drawing is open, so a tab switch in "
        + "AutoCAD between two calls cannot redirect an edit.";

    /// <summary>
    /// Adds the optional "drawing" property to a document tool's input schema. Called by
    /// ToolRegistry.Add, so no tool has to remember it and none can be missed. The wording
    /// differs for read and write tools because the argument means different things. A tool
    /// that already declares the property keeps its own.
    /// </summary>
    public static void AddArgumentTo(JsonObject inputSchema, bool isReadOnly)
    {
        if (inputSchema["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            inputSchema["properties"] = properties;
        }

        if (properties.ContainsKey(ArgumentName))
        {
            return;
        }

        properties[ArgumentName] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = isReadOnly ? ReadDescription : WriteDescription,
        };
    }

    /// <summary>
    /// Reads the optional argument. Null means "the active drawing". An empty or blank string
    /// is an error rather than a silent default: it is never what a caller meant.
    /// </summary>
    public static bool TryReadArgument(ToolArgs args, out string? drawing, out string error)
    {
        if (!args.TryGetStringOrDefault(ArgumentName, null, out drawing, out error))
        {
            return false;
        }

        if (drawing is not null && string.IsNullOrWhiteSpace(drawing))
        {
            drawing = null;
            error = $"Argument \"{ArgumentName}\" must not be empty. Omit it to use the active "
                  + "drawing, or pass a name from acad_list_drawings.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves <paramref name="requested"/> against the open drawings. Tries, in order: the
    /// full name (case-insensitive, either slash direction), then the file name with or
    /// without ".dwg". A file name shared by two open drawings is refused as ambiguous rather
    /// than resolved to the first one — guessing here is exactly the failure this exists to
    /// prevent.
    ///
    /// With <paramref name="requireActive"/> (every write tool), a drawing that matches but is
    /// not the active one is refused. Matching runs first, so a typo still gets the "no open
    /// drawing matches" message rather than a misleading "not active".
    /// </summary>
    public static bool TrySelect(
        IReadOnlyList<DrawingInfo> open,
        string requested,
        bool requireActive,
        out int index,
        out string error)
    {
        if (!TryMatch(open, requested, out index, out error))
        {
            return false;
        }

        if (requireActive && !open[index].IsActive)
        {
            error = DescribeNotActive(open, open[index].Name);
            index = -1;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The refusal for a write aimed at a non-active drawing. Names both drawings and the fix,
    /// instead of the raw eWrongDatabase AutoCAD would otherwise return mid-transaction.
    /// </summary>
    public static string DescribeNotActive(IReadOnlyList<DrawingInfo> open, string targetName)
    {
        string active = "none";
        foreach (DrawingInfo drawing in open)
        {
            if (drawing.IsActive)
            {
                active = GetFileName(drawing.Name);
            }
        }

        return $"\"{GetFileName(targetName)}\" is not the active drawing, so nothing was changed. "
             + "This server edits the active drawing only: switch to it in AutoCAD, then retry. "
             + $"Active: {active}. Read-only tools can still read \"{GetFileName(targetName)}\" "
             + "without switching.";
    }

    private static bool TryMatch(
        IReadOnlyList<DrawingInfo> open,
        string requested,
        out int index,
        out string error)
    {
        index = -1;
        error = "";

        if (open.Count == 0)
        {
            error = "No drawing is open in AutoCAD.";
            return false;
        }

        string wanted = NormalizeSlashes(requested.Trim());

        for (int i = 0; i < open.Count; i++)
        {
            if (string.Equals(NormalizeSlashes(open[i].Name), wanted, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        List<int> byFileName = new();
        for (int i = 0; i < open.Count; i++)
        {
            if (FileNameMatches(open[i].Name, wanted))
            {
                byFileName.Add(i);
            }
        }

        if (byFileName.Count == 1)
        {
            index = byFileName[0];
            return true;
        }

        if (byFileName.Count > 1)
        {
            List<string> candidates = new();
            foreach (int i in byFileName)
            {
                candidates.Add(open[i].Name);
            }

            error = $"Drawing \"{requested}\" is ambiguous: {byFileName.Count} open drawings share "
                  + $"that file name ({string.Join(", ", candidates)}). Pass the full path instead.";
            return false;
        }

        error = $"No open drawing matches \"{requested}\". Open drawings: {DescribeOpen(open)}. "
              + "Call acad_list_drawings for the exact names.";
        return false;
    }

    /// <summary>
    /// The file names that more than one open drawing shares, so acad_list_drawings can flag
    /// the entries whose short name will not work as a "drawing" argument.
    /// </summary>
    public static HashSet<string> FindAmbiguousFileNames(IReadOnlyList<DrawingInfo> open)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (DrawingInfo drawing in open)
        {
            string fileName = GetFileName(drawing.Name);
            counts[fileName] = counts.TryGetValue(fileName, out int count) ? count + 1 : 1;
        }

        HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> pair in counts)
        {
            if (pair.Value > 1)
            {
                ambiguous.Add(pair.Key);
            }
        }

        return ambiguous;
    }

    /// <summary>The file-name part of a Document.Name, whichever slash it uses.</summary>
    public static string GetFileName(string name)
    {
        string normalized = NormalizeSlashes(name);
        int slash = normalized.LastIndexOf('\\');
        return slash < 0 ? normalized : normalized.Substring(slash + 1);
    }

    private static bool FileNameMatches(string name, string wanted)
    {
        string fileName = GetFileName(name);

        if (string.Equals(fileName, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(fileName, wanted + ".dwg", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSlashes(string value) => value.Replace('/', '\\');

    private static string DescribeOpen(IReadOnlyList<DrawingInfo> open)
    {
        List<string> parts = new();
        foreach (DrawingInfo drawing in open)
        {
            parts.Add(drawing.IsActive ? $"{drawing.Name} (active)" : drawing.Name);
        }

        return string.Join(", ", parts);
    }
}
