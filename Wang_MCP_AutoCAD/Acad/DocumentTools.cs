using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Which drawings are open. The companion to the "drawing" argument every document tool
/// takes: this is where a caller gets the names to pass.
///
/// Deliberately no "activate drawing" tool. Reads target any drawing per call, by argument;
/// writes go to the active drawing only (verified live — see AcadDocumentGateway), and which
/// drawing is active is the user's choice, not the API's.
/// </summary>
internal static class DocumentTools
{
    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateListDrawingsTool());
    }

    // ---- acad_list_drawings -----------------------------------------------------------------

    private static McpTool CreateListDrawingsTool()
    {
        return new McpTool
        {
            Name = "acad_list_drawings",
            Description =
                "Lists the drawings open in AutoCAD: each one's file name, full path, whether it "
                + "is the active drawing, whether it is read-only, and its units. Read-only "
                + "tools (acad_list_entities, acad_get_entity_properties, acad_list_layers) "
                + "accept a drawing's name — or its path, when ambiguous_name is true — as "
                + "\"drawing\" to read any open drawing without switching. Every tool that "
                + "changes the drawing edits ONLY the active one; the user has to switch to "
                + "another drawing in AutoCAD before it can be edited. Call this first whenever "
                + "more than one drawing may be open: handles are only unique within one "
                + "drawing. Works with no drawing open.",
            InputSchema = ToolSchema.Object().Build(),
            // False on purpose: listing must work with zero drawings open, and it reads only
            // document properties, so it needs no lock.
            RequiresDocument = false,
            // Core, so it is advertised however a session filters groups: the "drawing"
            // argument appears on every document tool, and its names come from here.
            Group = ToolGroups.Core,
            Handler = ListDrawings,
        };
    }

    private static ToolResult ListDrawings(ToolArgs args)
    {
        Stopwatch sw = Stopwatch.StartNew();
        JsonArray drawings = new();
        string? activeName = null;

        try
        {
            List<Document> open = OpenDocuments.List();
            List<DrawingInfo> infos = OpenDocuments.Describe(open);
            HashSet<string> ambiguous = DrawingSelector.FindAmbiguousFileNames(infos);

            for (int i = 0; i < open.Count; i++)
            {
                Document doc = open[i];
                DrawingInfo info = infos[i];
                string fileName = DrawingSelector.GetFileName(info.Name);

                int insUnitsCode = (int)doc.Database.Insunits;
                bool unitsSupported = Units.TryGetMmPerDrawingUnit(insUnitsCode, out double mmPerDrawingUnit);

                JsonObject entry = new()
                {
                    ["drawing"] = fileName,
                    ["drawing_path"] = info.Name,
                    ["is_active"] = info.IsActive,
                    ["is_read_only"] = doc.IsReadOnly,
                    ["ambiguous_name"] = ambiguous.Contains(fileName),
                    ["insunits"] = insUnitsCode,
                    ["units_supported"] = unitsSupported,
                    ["mm_per_drawing_unit"] = unitsSupported ? mmPerDrawingUnit : null,
                };

                drawings.Add(entry);

                if (info.IsActive)
                {
                    activeName = fileName;
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/list_drawings outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/list_drawings outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not list the open drawings; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/list_drawings count={drawings.Count} active=\"{activeName}\" elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = new()
        {
            ["count"] = drawings.Count,
            ["active"] = activeName,
            ["drawings"] = drawings,
        };

        return ToolResult.Ok(payload);
    }
}
