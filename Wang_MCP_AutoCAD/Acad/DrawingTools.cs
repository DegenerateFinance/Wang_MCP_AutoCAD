using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// The tools that touch the drawing database. Each handler already runs on the document
/// thread with the active document locked — see <see cref="AcadDocumentGateway"/> — so the
/// bodies here are concerned only with the transaction and the unit conversion.
/// </summary>
internal static class DrawingTools
{
    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateDrawCircleTool());
        registry.Add(CreateListEntitiesTool());
    }

    private static McpTool CreateDrawCircleTool()
    {
        return new McpTool
        {
            Name = "acad_draw_circle",
            Description =
                "Draws a CIRCLE in model space of the active drawing, on the XY plane. All lengths "
                + "are millimetres and are converted to the drawing's own units using its INSUNITS "
                + "setting. Returns the new entity's handle, which other tools use to refer to it.",
            InputSchema = ToolSchema.Object()
                .Number("mm_center_x", "Centre X ordinate, in millimetres.", required: true)
                .Number("mm_center_y", "Centre Y ordinate, in millimetres.", required: true)
                .Number("mm_center_z", "Centre Z ordinate, in millimetres. Defaults to 0.")
                .PositiveNumber("mm_radius", "Radius, in millimetres. Must be greater than 0.", required: true)
                .String("layer", "Layer to draw on. Must already exist; defaults to the drawing's current layer.")
                .Build(),
            RequiresDocument = true,
            Handler = DrawCircle,
        };
    }

    private static ToolResult DrawCircle(ToolArgs args)
    {
        if (!args.TryGetDouble("mm_center_x", out double mm_CenterX, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDouble("mm_center_y", out double mm_CenterY, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDoubleOrDefault("mm_center_z", 0, out double mm_CenterZ, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetPositiveDouble("mm_radius", out double mm_Radius, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("layer", null, out string? layer, out error))
        {
            return ToolResult.Fail(error);
        }

        Document doc = Application.DocumentManager.MdiActiveDocument;
        Database db = doc.Database;

        int insUnitsCode = (int)db.Insunits;
        if (!Units.TryGetMmPerDrawingUnit(insUnitsCode, out double mmPerDrawingUnit))
        {
            return ToolResult.Fail(Units.DescribeUnsupportedUnits(insUnitsCode));
        }

        double du_CenterX = Units.MmToDrawingUnits(mm_CenterX, mmPerDrawingUnit);
        double du_CenterY = Units.MmToDrawingUnits(mm_CenterY, mmPerDrawingUnit);
        double du_CenterZ = Units.MmToDrawingUnits(mm_CenterZ, mmPerDrawingUnit);
        double du_Radius = Units.MmToDrawingUnits(mm_Radius, mmPerDrawingUnit);

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;

        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (layer is not null && !LayerExists(tr, db, layer))
                {
                    return ToolResult.Fail(
                        $"Layer \"{layer}\" does not exist in this drawing. Create it in AutoCAD first, "
                        + "or omit the layer argument to use the current layer.");
                }

                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

                Circle circle = new(new Point3d(du_CenterX, du_CenterY, du_CenterZ), Vector3d.ZAxis, du_Radius);
                if (layer is not null)
                {
                    circle.Layer = layer;
                }

                modelSpace.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);

                handle = circle.Handle.ToString();
                resolvedLayer = circle.Layer;

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_circle outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the circle: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_circle outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the circle; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/draw_circle handle={handle} layer={resolvedLayer} du_radius={du_Radius} "
            + $"entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = new()
        {
            ["handle"] = handle,
            ["layer"] = resolvedLayer,
            ["mm_center"] = new JsonArray { mm_CenterX, mm_CenterY, mm_CenterZ },
            ["mm_radius"] = mm_Radius,
            ["du_radius"] = du_Radius,
            ["mm_per_drawing_unit"] = mmPerDrawingUnit,
            ["insunits"] = insUnitsCode,
        };

        return ToolResult.Ok(payload);
    }

    private static McpTool CreateListEntitiesTool()
    {
        return new McpTool
        {
            Name = "acad_list_entities",
            Description =
                "Lists entities in model space of the active drawing, newest last, with each one's "
                + "handle, DXF type, layer and bounding box in millimetres. Optionally filter by "
                + "layer. Use this to find the handle of something before acting on it.",
            InputSchema = ToolSchema.Object()
                .String("layer", "Only list entities on this layer (exact, case-insensitive match).")
                .Integer("limit", "Maximum number of entities to return.", minimum: 1, maximum: 1000, defaultValue: 100)
                .Build(),
            RequiresDocument = true,
            Handler = ListEntities,
        };
    }

    private static ToolResult ListEntities(ToolArgs args)
    {
        if (!args.TryGetStringOrDefault("layer", null, out string? layerFilter, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetIntInRange("limit", fallback: 100, minimum: 1, maximum: 1000, out int limit, out error))
        {
            return ToolResult.Fail(error);
        }

        Document doc = Application.DocumentManager.MdiActiveDocument;
        Database db = doc.Database;

        int insUnitsCode = (int)db.Insunits;
        if (!Units.TryGetMmPerDrawingUnit(insUnitsCode, out double mmPerDrawingUnit))
        {
            return ToolResult.Fail(Units.DescribeUnsupportedUnits(insUnitsCode));
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray entities = new();
        int totalMatched = 0;

        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

                // Iterating the block table record rather than using Editor.SelectAll: the
                // Editor selection APIs assume document context and an idle command state,
                // and we are in application context here.
                foreach (ObjectId id in modelSpace)
                {
                    Entity? entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity is null)
                    {
                        continue;
                    }

                    if (layerFilter is not null
                        && !string.Equals(entity.Layer, layerFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    totalMatched++;

                    if (entities.Count >= limit)
                    {
                        continue;
                    }

                    entities.Add(Describe(entity, mmPerDrawingUnit));
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/list_entities outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/list_entities outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not list entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/list_entities layer={layerFilter ?? "(any)"} entities={entities.Count} "
            + $"matched={totalMatched} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = new()
        {
            ["drawing"] = doc.Name,
            ["totalMatched"] = totalMatched,
            ["returned"] = entities.Count,
            ["truncated"] = totalMatched > entities.Count,
            ["mm_per_drawing_unit"] = mmPerDrawingUnit,
            ["insunits"] = insUnitsCode,
            ["entities"] = entities,
        };

        return ToolResult.Ok(payload);
    }

    private static JsonObject Describe(Entity entity, double mmPerDrawingUnit)
    {
        JsonObject description = new()
        {
            ["handle"] = entity.Handle.ToString(),
            ["type"] = entity.GetRXClass().DxfName,
            ["layer"] = entity.Layer,
        };

        // GeometricExtents throws for degenerate entities (an empty MText, a zero-length
        // line). One bad entity must not fail the whole query.
        try
        {
            Extents3d du_Extents = entity.GeometricExtents;
            description["mm_min"] = new JsonArray
            {
                Units.DrawingUnitsToMm(du_Extents.MinPoint.X, mmPerDrawingUnit),
                Units.DrawingUnitsToMm(du_Extents.MinPoint.Y, mmPerDrawingUnit),
                Units.DrawingUnitsToMm(du_Extents.MinPoint.Z, mmPerDrawingUnit),
            };
            description["mm_max"] = new JsonArray
            {
                Units.DrawingUnitsToMm(du_Extents.MaxPoint.X, mmPerDrawingUnit),
                Units.DrawingUnitsToMm(du_Extents.MaxPoint.Y, mmPerDrawingUnit),
                Units.DrawingUnitsToMm(du_Extents.MaxPoint.Z, mmPerDrawingUnit),
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            description["mm_min"] = null;
            description["mm_max"] = null;
        }

        if (entity is Circle circle)
        {
            description["mm_radius"] = Units.DrawingUnitsToMm(circle.Radius, mmPerDrawingUnit);
        }

        return description;
    }

    private static bool LayerExists(Transaction tr, Database db, string layer)
    {
        LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        return layerTable.Has(layer);
    }
}
