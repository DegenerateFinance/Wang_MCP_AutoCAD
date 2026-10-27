using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Reading and changing entity properties, and erasing entities. Everything here selects by
/// handle: the caller queries first, then acts on what it found.
/// </summary>
internal static class PropertyTools
{
    private const int MaxHandles = 1000;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateGetPropertiesTool());
        registry.Add(CreateSetPropertiesTool());
        registry.Add(CreateDeleteEntitiesTool());
    }

    // ---- acad_get_entity_properties -----------------------------------------------------

    private static McpTool CreateGetPropertiesTool()
    {
        return new McpTool
        {
            Name = "acad_get_entity_properties",
            Description =
                "Reads the full properties and geometry of entities by handle: layer, colour, "
                + "linetype, lineweight, visibility, bounding box, and type-specific geometry "
                + "such as a line's endpoints or a block reference's attributes. All lengths "
                + "are millimetres and all angles degrees. Handles that cannot be resolved are "
                + "reported individually rather than failing the call.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Entity handles, as returned by acad_list_entities.", minItems: 1, maxItems: 500, required: true)
                .StringEnum(
                    "detail",
                    "How much to return per entity. \"summary\" omits type-specific geometry.",
                    EntityDescriber.DetailLevels,
                    defaultValue: EntityDescriber.DetailFull)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Query,
            Handler = GetProperties,
        };
    }

    private static ToolResult GetProperties(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, 500, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetEnum("detail", EntityDescriber.DetailLevels, EntityDescriber.DetailFull, out string detail, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray entities = new();
        JsonArray failures;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                // Read-only, so best-effort: a stale handle in the list should not deny the
                // caller everything else it asked for.
                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        OpenMode.ForRead,
                        requireAll: false,
                        out List<Entity> resolved,
                        out failures,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in resolved)
                {
                    entities.Add(EntityDescriber.Describe(acad, entity, detail));
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/get_entity_properties outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/get_entity_properties outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not read the entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/get_entity_properties requested={handles.Count} resolved={entities.Count} "
            + $"failed={failures.Count} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["requested"] = handles.Count;
        payload["resolved"] = entities.Count;
        payload["entities"] = entities;
        payload["failures"] = failures;

        return ToolResult.Ok(payload);
    }

    // ---- acad_set_entity_properties -----------------------------------------------------

    private static readonly string[] PropertyArgs =
    {
        "layer", "color_index", "linetype", "linetype_scale",
        "mm_lineweight", "transparency_percent", "visible",
    };

    private static McpTool CreateSetPropertiesTool()
    {
        return new McpTool
        {
            Name = "acad_set_entity_properties",
            Description =
                "Changes common properties on entities by handle: layer, colour, linetype, "
                + "linetype scale, lineweight, transparency or visibility. At least one "
                + "property must be given. Use this to move objects onto a different layer. "
                + "If any handle cannot be resolved, nothing is changed.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Entity handles to change.", minItems: 1, maxItems: MaxHandles, required: true)
                .String("layer", "Move the entities onto this layer. Must already exist.")
                .Integer("color_index", "AutoCAD Color Index. 256 is ByLayer, 0 is ByBlock.", minimum: 0, maximum: 256, defaultValue: 256)
                .String("linetype", "Linetype name. Must already be loaded.")
                .PositiveNumber("linetype_scale", "Linetype scale. Dimensionless.")
                .PositiveNumber("mm_lineweight", "Lineweight in millimetres, snapped to the nearest supported value.")
                .Integer("transparency_percent", "Transparency percentage, 0 (opaque) to 90.", minimum: 0, maximum: 90, defaultValue: 0)
                .Boolean("visible", "Whether the entities are visible.", true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Modify,
            Handler = SetProperties,
        };
    }

    private static ToolResult SetProperties(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.RequireAtLeastOneOf(PropertyArgs, "property to change", out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonObject applied = new();
        int changed;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        OpenMode.ForWrite,
                        requireAll: true,
                        out List<Entity> entities,
                        out JsonArray _,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryReadProperties(args, acad, tr, out PropertyChange change, out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    change.ApplyTo(entity);
                }

                changed = entities.Count;
                change.Describe(applied);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_entity_properties outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_entity_properties outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not change the entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/set_entity_properties changed={changed} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["changed"] = changed;
        payload["handles"] = ToJsonArray(handles);
        payload["applied"] = applied;

        return ToolResult.Ok(payload);
    }

    private static string DescribeAcadFailure(Autodesk.AutoCAD.Runtime.Exception acEx)
    {
        if (acEx.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
        {
            return "At least one entity is on a locked layer and cannot be changed. Unlock the "
                 + "layer with acad_set_layer_state first.";
        }

        return $"AutoCAD rejected the change: {acEx.ErrorStatus}.";
    }

    /// <summary>
    /// The set of property changes to apply, read and validated once rather than per entity.
    /// </summary>
    private sealed class PropertyChange
    {
        public string? Layer { get; init; }
        public int? ColorIndex { get; init; }
        public string? Linetype { get; init; }
        public double? LinetypeScale { get; init; }
        public int? LineWeightEnum { get; init; }
        public double? Mm_LineWeight { get; init; }
        public int? TransparencyPercent { get; init; }
        public bool? Visible { get; init; }

        public void ApplyTo(Entity entity)
        {
            if (Layer is not null)
            {
                entity.Layer = Layer;
            }

            if (ColorIndex is not null)
            {
                entity.ColorIndex = ColorIndex.Value;
            }

            if (Linetype is not null)
            {
                entity.Linetype = Linetype;
            }

            if (LinetypeScale is not null)
            {
                entity.LinetypeScale = LinetypeScale.Value;
            }

            if (LineWeightEnum is not null)
            {
                entity.LineWeight = (LineWeight)LineWeightEnum.Value;
            }

            if (TransparencyPercent is not null)
            {
                entity.Transparency = new Transparency(
                    Units.TransparencyPercentToAlpha(TransparencyPercent.Value));
            }

            if (Visible is not null)
            {
                entity.Visible = Visible.Value;
            }
        }

        public void Describe(JsonObject applied)
        {
            if (Layer is not null) { applied["layer"] = Layer; }
            if (ColorIndex is not null) { applied["color_index"] = ColorIndex.Value; }
            if (Linetype is not null) { applied["linetype"] = Linetype; }
            if (LinetypeScale is not null) { applied["linetype_scale"] = LinetypeScale.Value; }
            if (Mm_LineWeight is not null) { applied["mm_lineweight"] = Mm_LineWeight.Value; }
            if (TransparencyPercent is not null) { applied["transparency_percent"] = TransparencyPercent.Value; }
            if (Visible is not null) { applied["visible"] = Visible.Value; }
        }
    }

    private static bool TryReadProperties(
        ToolArgs args,
        AcadContext acad,
        Transaction tr,
        out PropertyChange change,
        out string error)
    {
        change = null!;
        error = "";

        string? layer = null;
        if (args.HasArgument("layer"))
        {
            if (!args.TryGetStringOrDefault("layer", null, out layer, out error))
            {
                return false;
            }

            if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out layer!, out error))
            {
                return false;
            }
        }

        int? colorIndex = null;
        if (args.HasArgument("color_index"))
        {
            if (!args.TryGetIntInRange("color_index", 256, 0, 256, out int value, out error))
            {
                return false;
            }

            colorIndex = value;
        }

        string? linetype = null;
        if (args.HasArgument("linetype"))
        {
            if (!args.TryGetStringOrDefault("linetype", null, out linetype, out error))
            {
                return false;
            }

            if (linetype is not null && !LinetypeExists(tr, acad.Db, linetype))
            {
                error = $"Linetype \"{linetype}\" is not loaded in this drawing. Load it in "
                      + "AutoCAD with the LINETYPE command first.";
                return false;
            }
        }

        double? linetypeScale = null;
        if (args.HasArgument("linetype_scale"))
        {
            if (!args.TryGetPositiveDouble("linetype_scale", out double value, out error))
            {
                return false;
            }

            linetypeScale = value;
        }

        int? lineWeightEnum = null;
        double? mm_LineWeight = null;
        if (args.HasArgument("mm_lineweight"))
        {
            if (!args.TryGetPositiveDouble("mm_lineweight", out double mm_Value, out error))
            {
                return false;
            }

            if (!LineWeights.TryNearest(mm_Value, out int enumValue, out double mm_Snapped, out error))
            {
                return false;
            }

            lineWeightEnum = enumValue;
            mm_LineWeight = mm_Snapped;
        }

        int? transparencyPercent = null;
        if (args.HasArgument("transparency_percent"))
        {
            if (!args.TryGetIntInRange("transparency_percent", 0, 0, 90, out int value, out error))
            {
                return false;
            }

            transparencyPercent = value;
        }

        bool? visible = null;
        if (args.HasArgument("visible"))
        {
            if (!args.TryGetBoolOrDefault("visible", true, out bool value, out error))
            {
                return false;
            }

            visible = value;
        }

        change = new PropertyChange
        {
            Layer = layer,
            ColorIndex = colorIndex,
            Linetype = linetype,
            LinetypeScale = linetypeScale,
            LineWeightEnum = lineWeightEnum,
            Mm_LineWeight = mm_LineWeight,
            TransparencyPercent = transparencyPercent,
            Visible = visible,
        };

        return true;
    }

    private static bool LinetypeExists(Transaction tr, Database db, string linetype)
    {
        LinetypeTable table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        return table.Has(linetype);
    }

    // ---- acad_delete_entities -----------------------------------------------------------

    private static McpTool CreateDeleteEntitiesTool()
    {
        return new McpTool
        {
            Name = "acad_delete_entities",
            Description =
                "Erases entities by handle. If any handle cannot be resolved, nothing is "
                + "erased. Pass confirm_count to guard against acting on a handle list that is "
                + "not the size you expect. Erasing a block reference does not affect the "
                + "block definition.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Entity handles to erase.", minItems: 1, maxItems: MaxHandles, required: true)
                .Integer(
                    "confirm_count",
                    "When given, must equal the number of handles supplied, or the call fails without erasing anything.",
                    minimum: 1,
                    maximum: MaxHandles,
                    defaultValue: 0)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Modify,
            Handler = DeleteEntities,
        };
    }

    private static ToolResult DeleteEntities(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (args.HasArgument("confirm_count"))
        {
            if (!args.TryGetIntInRange("confirm_count", 0, 1, MaxHandles, out int confirmCount, out error))
            {
                return ToolResult.Fail(error);
            }

            if (confirmCount != handles.Count)
            {
                return ToolResult.Fail(
                    $"confirm_count is {confirmCount} but {handles.Count} handles were supplied, "
                    + "so nothing was erased. Check the handle list is the one you meant.");
            }
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        int deleted;
        JsonArray erasedHandles = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        OpenMode.ForWrite,
                        requireAll: true,
                        out List<Entity> entities,
                        out JsonArray _,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    erasedHandles.Add(entity.Handle.ToString());
                    entity.Erase();
                }

                deleted = entities.Count;
                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/delete_entities outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/delete_entities outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not erase the entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/delete_entities deleted={deleted} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["deleted"] = deleted;
        payload["handles"] = erasedHandles;

        return ToolResult.Ok(payload);
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        JsonArray array = new();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
