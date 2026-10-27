using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// The tools that touch the drawing database. Each handler already runs on the document
/// thread with the active document locked — see <see cref="AcadDocumentGateway"/> — so the
/// bodies here are concerned only with the transaction and the unit conversion.
/// </summary>
internal static class DrawingTools
{
    /// <summary>Handle caps, matching the limit acad_list_entities already advertises.</summary>
    private const int MaxVertices = 5000;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateDrawCircleTool());
        registry.Add(CreateListEntitiesTool());
        registry.Add(CreateDrawLineTool());
        registry.Add(CreateDrawArcTool());
        registry.Add(CreateDrawPolylineTool());
        registry.Add(CreateDrawRectangleTool());
    }

    // ---- acad_draw_line -----------------------------------------------------------------

    private static McpTool CreateDrawLineTool()
    {
        return new McpTool
        {
            Name = "acad_draw_line",
            Description =
                "Draws a LINE in model space between two points. All lengths are millimetres "
                + "and are converted to the drawing's own units using its INSUNITS setting. "
                + "Returns the new entity's handle.",
            InputSchema = ToolSchema.Object()
                .PointMm("mm_start", "Start point.", required: true)
                .PointMm("mm_end", "End point.", required: true)
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Draw,
            Handler = DrawLine,
        };
    }

    private static ToolResult DrawLine(ToolArgs args)
    {
        if (!PointArgs.TryReadPointMm(args, "mm_start", required: true, out Mm_Point mm_Start, out _, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!PointArgs.TryReadPointMm(args, "mm_end", required: true, out Mm_Point mm_End, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        if (mm_Start == mm_End)
        {
            return ToolResult.Fail(
                "The start and end points are identical, which would create a zero-length line.");
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;
        double mm_Length;
        double deg_Angle;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                Line line = new(acad.ToPoint3d(mm_Start), acad.ToPoint3d(mm_End));
                line.Layer = resolvedLayer;

                target.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);

                handle = line.Handle.ToString();
                mm_Length = acad.ToMm(line.Length);
                deg_Angle = Units.NormalizeDeg(Units.RadToDeg(line.Angle));

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_line outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the line: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_line outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the line; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/draw_line handle={handle} layer={resolvedLayer} mm_length={mm_Length} "
            + $"entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["mm_start"] = ToMmArray(mm_Start);
        payload["mm_end"] = ToMmArray(mm_End);
        payload["mm_length"] = mm_Length;
        payload["deg_angle"] = deg_Angle;

        return ToolResult.Ok(payload);
    }

    // ---- acad_draw_arc ------------------------------------------------------------------

    private static McpTool CreateDrawArcTool()
    {
        return new McpTool
        {
            Name = "acad_draw_arc",
            Description =
                "Draws an ARC in model space. Angles are in degrees, measured counter-clockwise "
                + "from the positive X axis. AutoCAD arcs always run COUNTER-CLOCKWISE from the "
                + "start angle to the end angle, so a start of 90 and an end of 0 gives a 270 "
                + "degree arc, not a 90 degree one. Lengths are millimetres.",
            InputSchema = ToolSchema.Object()
                .PointMm("mm_center", "Centre point.", required: true)
                .PositiveNumber("mm_radius", "Radius, in millimetres. Must be greater than 0.", required: true)
                .Number("deg_start_angle", "Start angle in degrees, counter-clockwise from the positive X axis.", required: true)
                .Number("deg_end_angle", "End angle in degrees. The arc runs counter-clockwise from the start angle to this one.", required: true)
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Draw,
            Handler = DrawArc,
        };
    }

    private static ToolResult DrawArc(ToolArgs args)
    {
        if (!PointArgs.TryReadPointMm(args, "mm_center", required: true, out Mm_Point mm_Center, out _, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetPositiveDouble("mm_radius", out double mm_Radius, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDouble("deg_start_angle", out double deg_Start, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDouble("deg_end_angle", out double deg_End, out error))
        {
            return ToolResult.Fail(error);
        }

        // Equal angles give AutoCAD no arc to draw; the caller almost certainly wants a circle.
        if (Units.NormalizeDeg(deg_Start) == Units.NormalizeDeg(deg_End))
        {
            return ToolResult.Fail(
                "The start and end angles are equal, so there is no arc to draw. For a full "
                + "circle use acad_draw_circle.");
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;
        double mm_Length;
        double deg_Total;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                // The database stores angles in radians; the wire carries degrees.
                Arc arc = new(
                    acad.ToPoint3d(mm_Center),
                    acad.ToDu(mm_Radius),
                    Units.DegToRad(deg_Start),
                    Units.DegToRad(deg_End));

                arc.Layer = resolvedLayer;

                target.AppendEntity(arc);
                tr.AddNewlyCreatedDBObject(arc, true);

                handle = arc.Handle.ToString();
                mm_Length = acad.ToMm(arc.Length);
                deg_Total = Units.RadToDeg(arc.TotalAngle);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_arc outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the arc: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_arc outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the arc; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/draw_arc handle={handle} layer={resolvedLayer} deg_total={deg_Total} "
            + $"entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["mm_center"] = ToMmArray(mm_Center);
        payload["mm_radius"] = mm_Radius;
        payload["deg_start_angle"] = Units.NormalizeDeg(deg_Start);
        payload["deg_end_angle"] = Units.NormalizeDeg(deg_End);
        payload["deg_total_angle"] = deg_Total;
        payload["mm_length"] = mm_Length;

        return ToolResult.Ok(payload);
    }

    // ---- acad_draw_polyline -------------------------------------------------------------

    private static McpTool CreateDrawPolylineTool()
    {
        return new McpTool
        {
            Name = "acad_draw_polyline",
            Description =
                "Draws a lightweight POLYLINE through a list of vertices. The polyline is 2D: "
                + "its Z comes from mm_elevation, not from the vertices. A vertex may carry a "
                + "bulge to make the segment that follows it an arc. Lengths are millimetres.",
            InputSchema = ToolSchema.Object()
                .VertexArrayMm(
                    "mm_vertices",
                    "The vertices, in order. Each is {mm_x, mm_y} with an optional bulge.",
                    minItems: 2,
                    maxItems: MaxVertices,
                    required: true)
                .Boolean("closed", "Close the polyline back to its first vertex.", false)
                .Number("mm_elevation", "Z ordinate of the whole polyline, in millimetres. Defaults to 0.")
                .PositiveNumber("mm_global_width", "A constant width applied to every segment, in millimetres.")
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Draw,
            Handler = DrawPolyline,
        };
    }

    private static ToolResult DrawPolyline(ToolArgs args)
    {
        if (!PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, MaxVertices, out IReadOnlyList<Mm_Vertex> vertices, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("closed", false, out bool closed, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDoubleOrDefault("mm_elevation", 0, out double mm_Elevation, out error))
        {
            return ToolResult.Fail(error);
        }

        bool hasWidth = args.HasArgument("mm_global_width");
        double mm_GlobalWidth = 0;
        if (hasWidth && !args.TryGetPositiveDouble("mm_global_width", out mm_GlobalWidth, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;
        double mm_Length;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                Polyline polyline = new();
                polyline.SetDatabaseDefaults();

                for (int i = 0; i < vertices.Count; i++)
                {
                    Mm_Vertex vertex = vertices[i];

                    // Widths are passed as 0 here and set globally below via ConstantWidth;
                    // mixing per-vertex widths with a constant width is a confusing mix.
                    polyline.AddVertexAt(
                        index: i,
                        pt: acad.ToPoint2d(vertex.Mm_X, vertex.Mm_Y),
                        bulge: vertex.Bulge,
                        startWidth: 0,
                        endWidth: 0);
                }

                polyline.Closed = closed;

                // Elevation and ConstantWidth are set AFTER the vertices are added: on some
                // code paths setting them on an empty polyline does not stick.
                polyline.Elevation = acad.ToDu(mm_Elevation);

                if (hasWidth)
                {
                    polyline.ConstantWidth = acad.ToDu(mm_GlobalWidth);
                }

                polyline.Layer = resolvedLayer;

                target.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);

                handle = polyline.Handle.ToString();
                mm_Length = acad.ToMm(polyline.Length);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_polyline outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the polyline: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_polyline outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the polyline; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/draw_polyline handle={handle} layer={resolvedLayer} vertices={vertices.Count} "
            + $"closed={closed} entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["vertex_count"] = vertices.Count;
        payload["closed"] = closed;
        payload["mm_elevation"] = mm_Elevation;
        payload["mm_length"] = mm_Length;

        return ToolResult.Ok(payload);
    }

    // ---- acad_draw_rectangle ------------------------------------------------------------

    private static McpTool CreateDrawRectangleTool()
    {
        return new McpTool
        {
            Name = "acad_draw_rectangle",
            Description =
                "Draws an axis-aligned rectangle as a closed POLYLINE, given two opposite "
                + "corners. A convenience over acad_draw_polyline for the common case. "
                + "Lengths are millimetres.",
            InputSchema = ToolSchema.Object()
                .PointMm("mm_corner1", "One corner.", required: true)
                .PointMm("mm_corner2", "The opposite corner.", required: true)
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Draw,
            Handler = DrawRectangle,
        };
    }

    private static ToolResult DrawRectangle(ToolArgs args)
    {
        if (!PointArgs.TryReadPointMm(args, "mm_corner1", required: true, out Mm_Point mm_Corner1, out _, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!PointArgs.TryReadPointMm(args, "mm_corner2", required: true, out Mm_Point mm_Corner2, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        double mm_Width = Math.Abs(mm_Corner2.Mm_X - mm_Corner1.Mm_X);
        double mm_Height = Math.Abs(mm_Corner2.Mm_Y - mm_Corner1.Mm_Y);

        if (mm_Width == 0 || mm_Height == 0)
        {
            return ToolResult.Fail(
                $"The two corners give a rectangle {mm_Width} by {mm_Height} mm, which has no "
                + "area. The corners must differ in both X and Y.");
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        double mm_MinX = Math.Min(mm_Corner1.Mm_X, mm_Corner2.Mm_X);
        double mm_MinY = Math.Min(mm_Corner1.Mm_Y, mm_Corner2.Mm_Y);
        double mm_MaxX = Math.Max(mm_Corner1.Mm_X, mm_Corner2.Mm_X);
        double mm_MaxY = Math.Max(mm_Corner1.Mm_Y, mm_Corner2.Mm_Y);

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                Polyline polyline = new();
                polyline.SetDatabaseDefaults();

                polyline.AddVertexAt(0, acad.ToPoint2d(mm_MinX, mm_MinY), 0, 0, 0);
                polyline.AddVertexAt(1, acad.ToPoint2d(mm_MaxX, mm_MinY), 0, 0, 0);
                polyline.AddVertexAt(2, acad.ToPoint2d(mm_MaxX, mm_MaxY), 0, 0, 0);
                polyline.AddVertexAt(3, acad.ToPoint2d(mm_MinX, mm_MaxY), 0, 0, 0);
                polyline.Closed = true;

                polyline.Elevation = acad.ToDu(mm_Corner1.Mm_Z);
                polyline.Layer = resolvedLayer;

                target.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);

                handle = polyline.Handle.ToString();
                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_rectangle outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the rectangle: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/draw_rectangle outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the rectangle; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/draw_rectangle handle={handle} layer={resolvedLayer} mm_width={mm_Width} "
            + $"mm_height={mm_Height} entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["mm_width"] = mm_Width;
        payload["mm_height"] = mm_Height;
        payload["mm2_area"] = mm_Width * mm_Height;
        payload["mm_min"] = new JsonArray { mm_MinX, mm_MinY, mm_Corner1.Mm_Z };
        payload["mm_max"] = new JsonArray { mm_MaxX, mm_MaxY, mm_Corner1.Mm_Z };

        return ToolResult.Ok(payload);
    }

    // ---- shared -------------------------------------------------------------------------

    /// <summary>Reads the layer and space arguments every creation tool shares.</summary>
    private static bool TryReadPlacement(
        ToolArgs args,
        out string? layer,
        out string space,
        out string error)
    {
        space = EntityResolver.DefaultSpace;

        if (!args.TryGetStringOrDefault("layer", null, out layer, out error))
        {
            return false;
        }

        return args.TryGetEnum(
            "space",
            EntityResolver.AllowedSpaces,
            EntityResolver.DefaultSpace,
            out space,
            out error);
    }

    private static JsonArray ToMmArray(Mm_Point point)
    {
        return new JsonArray { point.Mm_X, point.Mm_Y, point.Mm_Z };
    }

    private static McpTool CreateDrawCircleTool()
    {
        return new McpTool
        {
            Name = "acad_draw_circle",
            Description =
                "Draws a CIRCLE in model space, on the XY plane. All lengths "
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
            Group = ToolGroups.Draw,
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

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Database db = acad.Db;
        double mmPerDrawingUnit = acad.MmPerDrawingUnit;

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

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["mm_center"] = new JsonArray { mm_CenterX, mm_CenterY, mm_CenterZ };
        payload["mm_radius"] = mm_Radius;
        payload["du_radius"] = du_Radius;

        return ToolResult.Ok(payload);
    }

    private static McpTool CreateListEntitiesTool()
    {
        return new McpTool
        {
            Name = "acad_list_entities",
            Description =
                "Lists entities in model space, newest last, with each one's "
                + "handle, DXF type, layer and bounding box in millimetres. Optionally filter by "
                + "layer. Use this to find the handle of something before acting on it.",
            InputSchema = ToolSchema.Object()
                .String("layer", "Only list entities on this layer (exact, case-insensitive match).")
                .Integer("limit", "Maximum number of entities to return.", minimum: 1, maximum: 1000, defaultValue: 100)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Query,
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

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Database db = acad.Db;
        double mmPerDrawingUnit = acad.MmPerDrawingUnit;

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

        JsonObject payload = acad.NewPayload();
        payload["totalMatched"] = totalMatched;
        payload["returned"] = entities.Count;
        payload["truncated"] = totalMatched > entities.Count;
        payload["entities"] = entities;

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
