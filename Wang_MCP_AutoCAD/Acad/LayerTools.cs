using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Layer creation, listing and state. These come first in the tool surface because the
/// drawing tools refuse to draw on a layer that does not exist — without a way to list and
/// create layers, that rule leaves a caller stuck.
/// </summary>
internal static class LayerTools
{
    private const int ColorByBlock = 0;
    private const int ColorByLayer = 256;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateListLayersTool());
        registry.Add(CreateCreateLayerTool());
        registry.Add(CreateSetLayerStateTool());
        registry.Add(CreateSetCurrentLayerTool());
    }

    // ---- acad_list_layers ---------------------------------------------------------------

    private static McpTool CreateListLayersTool()
    {
        return new McpTool
        {
            Name = "acad_list_layers",
            Description =
                "Lists every layer in the active drawing with its colour, lineweight, linetype "
                + "and state (frozen, locked, off, plottable), and says which layer is current. "
                + "Drawing tools refuse to draw on a layer that does not exist, so use this to "
                + "find valid layer names before drawing.",
            InputSchema = ToolSchema.Object()
                .Boolean("include_unused", "Include layers no entity is currently on.", true)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Layers,
            Handler = ListLayers,
        };
    }

    private static ToolResult ListLayers(ToolArgs args)
    {
        if (!args.TryGetBoolOrDefault("include_unused", true, out bool includeUnused, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray layers = new();
        string currentLayer = "";

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)tr.GetObject(acad.Db.LayerTableId, OpenMode.ForRead);
                LayerTableRecord current = (LayerTableRecord)tr.GetObject(acad.Db.Clayer, OpenMode.ForRead);
                currentLayer = current.Name;

                foreach (ObjectId id in layerTable)
                {
                    LayerTableRecord record = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);

                    bool isUsed = record.IsUsed;
                    if (!includeUnused && !isUsed)
                    {
                        continue;
                    }

                    layers.Add(Describe(record, currentLayer, isUsed));
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/list_layers outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the layer query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/list_layers outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not list layers; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/list_layers layers={layers.Count} current={currentLayer} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["drawing"] = acad.Doc.Name;
        payload["count"] = layers.Count;
        payload["current_layer"] = currentLayer;
        payload["layers"] = layers;

        return ToolResult.Ok(payload);
    }

    private static JsonObject Describe(LayerTableRecord record, string currentLayer, bool isUsed)
    {
        return new JsonObject
        {
            ["name"] = record.Name,
            ["color_index"] = record.Color.IsByAci ? record.Color.ColorIndex : -1,
            ["color"] = record.Color.ToString(),
            ["description"] = record.Description,
            ["is_frozen"] = record.IsFrozen,
            ["is_locked"] = record.IsLocked,
            ["is_off"] = record.IsOff,
            ["is_plottable"] = record.IsPlottable,
            ["mm_lineweight"] = Units.LineWeightToMm((int)record.LineWeight),
            ["linetype"] = ResolveLinetypeName(record),
            ["transparency_percent"] = DescribeTransparency(record.Transparency),
            ["is_used"] = isUsed,
            ["is_current"] = string.Equals(record.Name, currentLayer, StringComparison.Ordinal),
        };
    }

    private static string ResolveLinetypeName(LayerTableRecord record)
    {
        try
        {
            using LinetypeTableRecord? linetype =
                record.LinetypeObjectId.GetObject(OpenMode.ForRead) as LinetypeTableRecord;

            return linetype?.Name ?? "";
        }
        catch (System.Exception)
        {
            return "";
        }
    }

    private static int DescribeTransparency(Transparency transparency)
    {
        // A ByLayer/ByBlock transparency has no alpha of its own to report.
        return transparency.IsByAlpha ? Units.AlphaToTransparencyPercent(transparency.Alpha) : 0;
    }

    // ---- acad_create_layer --------------------------------------------------------------

    private static McpTool CreateCreateLayerTool()
    {
        return new McpTool
        {
            Name = "acad_create_layer",
            Description =
                "Creates a layer in the active drawing. Drawing tools require the layer they "
                + "draw on to exist already, so call this first when drawing onto a new layer. "
                + "Lineweight is given in millimetres and is snapped to the nearest value "
                + "AutoCAD supports; the snapped value is returned.",
            InputSchema = ToolSchema.Object()
                .String("name", "Layer name. Cannot contain < > / \\ \" : ; ? * | , = ` or be blank.", required: true)
                .Integer("color_index", "AutoCAD Color Index, 1-255.", minimum: 1, maximum: 255, defaultValue: 7)
                .String("description", "Optional layer description.")
                .PositiveNumber("mm_lineweight", "Lineweight in millimetres, snapped to the nearest supported value.")
                .String("linetype", "Linetype name. Must already be loaded in the drawing. Defaults to Continuous.")
                .Boolean("is_plottable", "Whether entities on this layer plot.", true)
                .Integer("transparency_percent", "Transparency as a percentage, 0 (opaque) to 90.", minimum: 0, maximum: 90, defaultValue: 0)
                .StringEnum(
                    "if_exists",
                    "What to do when the layer already exists.",
                    new[] { "error", "ignore", "update" },
                    defaultValue: "error")
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Layers,
            Handler = CreateLayer,
        };
    }

    private static ToolResult CreateLayer(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetIntInRange("color_index", 7, 1, 255, out int colorIndex, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("description", null, out string? description, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("linetype", null, out string? linetype, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("is_plottable", true, out bool isPlottable, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetIntInRange("transparency_percent", 0, 0, 90, out int transparencyPercent, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetEnum("if_exists", new[] { "error", "ignore", "update" }, "error", out string ifExists, out error))
        {
            return ToolResult.Fail(error);
        }

        bool hasLineWeight = args.HasArgument("mm_lineweight");
        int lineWeightEnum = 0;
        double mm_SnappedLineWeight = 0;

        if (hasLineWeight)
        {
            if (!args.TryGetPositiveDouble("mm_lineweight", out double mm_LineWeight, out error))
            {
                return ToolResult.Fail(error);
            }

            if (!LineWeights.TryNearest(mm_LineWeight, out lineWeightEnum, out mm_SnappedLineWeight, out error))
            {
                return ToolResult.Fail(error);
            }
        }

        if (!LayerNaming.TryValidate(name, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        bool created;
        bool alreadyExisted;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)tr.GetObject(acad.Db.LayerTableId, OpenMode.ForRead);

                alreadyExisted = layerTable.Has(name);

                if (alreadyExisted && string.Equals(ifExists, "error", StringComparison.Ordinal))
                {
                    return ToolResult.Fail(
                        $"Layer \"{name}\" already exists. Pass if_exists=\"ignore\" to leave it "
                        + "alone, or if_exists=\"update\" to apply these properties to it.");
                }

                if (alreadyExisted && string.Equals(ifExists, "ignore", StringComparison.Ordinal))
                {
                    tr.Commit();

                    sw.Stop();
                    Log.Info($"op=tool/create_layer layer={name} outcome=already_exists elapsed_ms={sw.ElapsedMilliseconds}");

                    JsonObject ignored = acad.NewPayload();
                    ignored["name"] = name;
                    ignored["created"] = false;
                    ignored["already_existed"] = true;
                    return ToolResult.Ok(ignored);
                }

                LayerTableRecord record;
                if (alreadyExisted)
                {
                    record = (LayerTableRecord)tr.GetObject(layerTable[name], OpenMode.ForWrite);
                    created = false;
                }
                else
                {
                    layerTable.UpgradeOpen();
                    record = new LayerTableRecord { Name = name };
                    layerTable.Add(record);
                    tr.AddNewlyCreatedDBObject(record, true);
                    created = true;
                }

                record.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
                record.IsPlottable = isPlottable;

                if (description is not null)
                {
                    record.Description = description;
                }

                if (hasLineWeight)
                {
                    record.LineWeight = (LineWeight)lineWeightEnum;
                }

                if (transparencyPercent > 0)
                {
                    record.Transparency = new Transparency(Units.TransparencyPercentToAlpha(transparencyPercent));
                }

                if (linetype is not null)
                {
                    if (!TryApplyLinetype(tr, acad.Db, record, linetype, out error))
                    {
                        return ToolResult.Fail(error);
                    }
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/create_layer layer={name} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the layer: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/create_layer layer={name} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the layer; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/create_layer layer={name} created={created} color_index={colorIndex} "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = name;
        payload["created"] = created;
        payload["already_existed"] = alreadyExisted;
        payload["color_index"] = colorIndex;
        payload["is_plottable"] = isPlottable;
        payload["transparency_percent"] = transparencyPercent;

        if (hasLineWeight)
        {
            payload["mm_lineweight"] = mm_SnappedLineWeight;
        }

        if (linetype is not null)
        {
            payload["linetype"] = linetype;
        }

        return ToolResult.Ok(payload);
    }

    private static bool TryApplyLinetype(
        Transaction tr,
        Database db,
        LayerTableRecord record,
        string linetype,
        out string error)
    {
        error = "";

        LinetypeTable linetypeTable = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);

        if (!linetypeTable.Has(linetype))
        {
            error = $"Linetype \"{linetype}\" is not loaded in this drawing. Load it in AutoCAD "
                  + "with the LINETYPE command first, or omit the linetype argument for Continuous.";
            return false;
        }

        record.LinetypeObjectId = linetypeTable[linetype];
        return true;
    }

    // ---- acad_set_layer_state -----------------------------------------------------------

    private static readonly string[] LayerStateArgs =
    {
        "is_frozen", "is_locked", "is_off", "is_plottable", "color_index",
        "mm_lineweight", "description", "transparency_percent",
    };

    private static McpTool CreateSetLayerStateTool()
    {
        return new McpTool
        {
            Name = "acad_set_layer_state",
            Description =
                "Changes properties of an existing layer: frozen, locked, off, plottable, "
                + "colour, lineweight, description or transparency. At least one property must "
                + "be given. The current layer cannot be frozen.",
            InputSchema = ToolSchema.Object()
                .String("name", "Layer to change.", required: true)
                .Boolean("is_frozen", "Freeze the layer. The current layer cannot be frozen.", false)
                .Boolean("is_locked", "Lock the layer. Entities on a locked layer cannot be modified.", false)
                .Boolean("is_off", "Turn the layer off.", false)
                .Boolean("is_plottable", "Whether entities on this layer plot.", true)
                .Integer("color_index", "AutoCAD Color Index, 1-255.", minimum: 1, maximum: 255, defaultValue: 7)
                .PositiveNumber("mm_lineweight", "Lineweight in millimetres, snapped to the nearest supported value.")
                .String("description", "Layer description.")
                .Integer("transparency_percent", "Transparency percentage, 0 (opaque) to 90.", minimum: 0, maximum: 90, defaultValue: 0)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Layers,
            Handler = SetLayerState,
        };
    }

    private static ToolResult SetLayerState(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.RequireAtLeastOneOf(LayerStateArgs, "property to change", out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonObject applied = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)tr.GetObject(acad.Db.LayerTableId, OpenMode.ForRead);

                if (!layerTable.Has(name))
                {
                    return ToolResult.Fail(
                        $"Layer \"{name}\" does not exist in this drawing. Use acad_list_layers "
                        + "to see the layers that do.");
                }

                LayerTableRecord record = (LayerTableRecord)tr.GetObject(layerTable[name], OpenMode.ForWrite);
                LayerTableRecord current = (LayerTableRecord)tr.GetObject(acad.Db.Clayer, OpenMode.ForRead);
                bool isCurrent = string.Equals(record.Name, current.Name, StringComparison.Ordinal);

                if (!TryApplyState(args, record, isCurrent, applied, out error))
                {
                    return ToolResult.Fail(error);
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_layer_state layer={name} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the layer change: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_layer_state layer={name} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not change the layer; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/set_layer_state layer={name} changed={applied.Count} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = name;
        payload["applied"] = applied;

        return ToolResult.Ok(payload);
    }

    private static bool TryApplyState(
        ToolArgs args,
        LayerTableRecord record,
        bool isCurrent,
        JsonObject applied,
        out string error)
    {
        error = "";

        if (args.HasArgument("is_frozen"))
        {
            if (!args.TryGetBoolOrDefault("is_frozen", false, out bool isFrozen, out error))
            {
                return false;
            }

            // Freezing the current layer throws eCannotChangeActiveLayer; naming the layer
            // and the fix is far more useful than surfacing that status.
            if (isFrozen && isCurrent)
            {
                error = $"Layer \"{record.Name}\" is the current layer and cannot be frozen. "
                      + "Make another layer current first with acad_set_current_layer.";
                return false;
            }

            record.IsFrozen = isFrozen;
            applied["is_frozen"] = isFrozen;
        }

        if (args.HasArgument("is_locked"))
        {
            if (!args.TryGetBoolOrDefault("is_locked", false, out bool isLocked, out error))
            {
                return false;
            }

            record.IsLocked = isLocked;
            applied["is_locked"] = isLocked;
        }

        if (args.HasArgument("is_off"))
        {
            if (!args.TryGetBoolOrDefault("is_off", false, out bool isOff, out error))
            {
                return false;
            }

            record.IsOff = isOff;
            applied["is_off"] = isOff;
        }

        if (args.HasArgument("is_plottable"))
        {
            if (!args.TryGetBoolOrDefault("is_plottable", true, out bool isPlottable, out error))
            {
                return false;
            }

            record.IsPlottable = isPlottable;
            applied["is_plottable"] = isPlottable;
        }

        if (args.HasArgument("color_index"))
        {
            if (!args.TryGetIntInRange("color_index", 7, 1, 255, out int colorIndex, out error))
            {
                return false;
            }

            record.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
            applied["color_index"] = colorIndex;
        }

        if (args.HasArgument("mm_lineweight"))
        {
            if (!args.TryGetPositiveDouble("mm_lineweight", out double mm_LineWeight, out error))
            {
                return false;
            }

            if (!LineWeights.TryNearest(mm_LineWeight, out int lineWeightEnum, out double mm_Snapped, out error))
            {
                return false;
            }

            record.LineWeight = (LineWeight)lineWeightEnum;
            applied["mm_lineweight"] = mm_Snapped;
        }

        if (args.HasArgument("description"))
        {
            if (!args.TryGetStringOrDefault("description", null, out string? description, out error))
            {
                return false;
            }

            record.Description = description ?? "";
            applied["description"] = description ?? "";
        }

        if (args.HasArgument("transparency_percent"))
        {
            if (!args.TryGetIntInRange("transparency_percent", 0, 0, 90, out int percent, out error))
            {
                return false;
            }

            record.Transparency = new Transparency(Units.TransparencyPercentToAlpha(percent));
            applied["transparency_percent"] = percent;
        }

        return true;
    }

    // ---- acad_set_current_layer ---------------------------------------------------------

    private static McpTool CreateSetCurrentLayerTool()
    {
        return new McpTool
        {
            Name = "acad_set_current_layer",
            Description =
                "Makes a layer current. Drawing tools that are given no layer draw on the "
                + "current one.",
            InputSchema = ToolSchema.Object()
                .String("name", "Layer to make current. Must already exist and must not be frozen.", required: true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Layers,
            Handler = SetCurrentLayer,
        };
    }

    private static ToolResult SetCurrentLayer(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string previous;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)tr.GetObject(acad.Db.LayerTableId, OpenMode.ForRead);

                if (!layerTable.Has(name))
                {
                    return ToolResult.Fail(
                        $"Layer \"{name}\" does not exist in this drawing. Create it with "
                        + "acad_create_layer, or use acad_list_layers to see the layers that do.");
                }

                LayerTableRecord current = (LayerTableRecord)tr.GetObject(acad.Db.Clayer, OpenMode.ForRead);
                previous = current.Name;

                ObjectId targetId = layerTable[name];
                LayerTableRecord target = (LayerTableRecord)tr.GetObject(targetId, OpenMode.ForRead);

                // A frozen layer cannot be made current; AutoCAD reports eInvalidInput.
                if (target.IsFrozen)
                {
                    return ToolResult.Fail(
                        $"Layer \"{name}\" is frozen and cannot be made current. Thaw it first "
                        + "with acad_set_layer_state.");
                }

                acad.Db.Clayer = targetId;
                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_current_layer layer={name} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the change: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_current_layer layer={name} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not change the current layer; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/set_current_layer layer={name} previous={previous} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = name;
        payload["previous_layer"] = previous;

        return ToolResult.Ok(payload);
    }
}
