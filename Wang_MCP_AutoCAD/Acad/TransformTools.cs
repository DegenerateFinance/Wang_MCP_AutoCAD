using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Moving, rotating, scaling, mirroring and copying entities by handle.
///
/// One acad_transform with a mode enum instead of five sibling tools: every mode is "build a
/// Matrix3d, apply it", so the only per-mode code is argument reading (TransformArgs) and
/// matrix construction (TransformApplier). acad_copy exists separately because a repeated
/// displacement with a count is a different question from a single transform.
/// </summary>
internal static class TransformTools
{
    /// <summary>Matches the other mutators' handle cap.</summary>
    private const int MaxHandles = 1000;

    /// <summary>
    /// acad_copy can multiply its input, so it gets its own bound on what one call creates.
    /// The listener is serialised on the document thread; an unbounded copy blocks everything.
    /// </summary>
    private const int MaxNewEntities = 1000;

    /// <summary>How many handles a log line names before summarising the rest as a count.</summary>
    private const int MaxLoggedHandles = 20;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateTransformTool());
        registry.Add(CreateCopyTool());
    }

    // ---- acad_transform ---------------------------------------------------------------------

    private static McpTool CreateTransformTool()
    {
        return new McpTool
        {
            Name = "acad_transform",
            Description =
                "Moves, rotates, scales or mirrors entities by handle. Pick a mode and pass only "
                + "that mode's arguments: move takes mm_from and mm_to; rotate takes mm_base and "
                + "deg_angle (counter-clockwise); scale takes mm_base and factor; mirror takes "
                + "mm_line_start and mm_line_end. Arguments belonging to another mode are "
                + "rejected. Set copy to true to transform copies and leave the originals in "
                + "place — AutoCAD's MIRROR command keeps the original by default, so pass "
                + "copy: true to match it. Mirroring (or a negative scale factor) makes text read "
                + "backwards; mirrored_text_count reports how many text entities were affected. "
                + "If any handle cannot be resolved, nothing is changed.",
            InputSchema = TransformArgs.AddTo(ToolSchema.Object()
                    .StringArray("handles", "Entity handles to transform.", minItems: 1, maxItems: MaxHandles, required: true))
                .Boolean("copy", "Transform copies instead of the originals. The new handles are returned in new_handles, in the same order as handles.", false)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Modify,
            Handler = Transform,
        };
    }

    private static ToolResult Transform(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!TransformArgs.TryRead(args, out TransformRequest? request, out string mode, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("copy", false, out bool copy, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        List<string> sourceHandles = new();
        List<string> newHandles = new();
        int mirroredTextCount = 0;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                // Copies only read their sources, so the sources may sit on a locked layer;
                // an in-place transform must open them for write.
                OpenMode openMode = copy ? OpenMode.ForRead : OpenMode.ForWrite;

                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        openMode,
                        requireAll: true,
                        out List<Entity> entities,
                        out JsonArray _,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryRejectDuplicates(entities, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (copy && !TryRejectUncopyable(tr, entities, out error))
                {
                    return ToolResult.Fail(error);
                }

                Matrix3d matrix = TransformApplier.BuildMatrix(acad, request!);

                if (request!.IsReflection)
                {
                    mirroredTextCount = TransformApplier.CountTextEntities(entities);
                }

                foreach (Entity entity in entities)
                {
                    sourceHandles.Add(entity.Handle.ToString());
                }

                if (copy)
                {
                    newHandles = TransformApplier.ApplyAsCopy(tr, acad.Db, entities, matrix);
                }
                else
                {
                    TransformApplier.Apply(entities, matrix);
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/transform mode={mode} copy={copy} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/transform mode={mode} copy={copy} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not transform the entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/transform mode={mode} copy={copy} entities={sourceHandles.Count} "
            + $"new_entities={newHandles.Count} mirrored_text={mirroredTextCount} "
            + $"handles={SummarizeHandles(sourceHandles)} new_handles={SummarizeHandles(newHandles)} "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["mode"] = mode;
        payload["copy"] = copy;
        payload["transformed"] = sourceHandles.Count;
        payload["handles"] = ToJsonArray(sourceHandles);
        payload["new_handles"] = ToJsonArray(newHandles);
        payload["parameters"] = DescribeRequest(request);
        payload["mirrored_text_count"] = mirroredTextCount;

        return ToolResult.Ok(payload);
    }

    /// <summary>Echoes the transform in wire units, so the caller can see what was applied.</summary>
    private static JsonObject DescribeRequest(TransformRequest request)
    {
        switch (request.Kind)
        {
            case TransformKind.Move:
                return new JsonObject
                {
                    ["mm_from"] = ToMmArray(request.Mm_Base),
                    ["mm_to"] = ToMmArray(request.Mm_Secondary),
                };

            case TransformKind.Rotate:
                return new JsonObject
                {
                    ["mm_base"] = ToMmArray(request.Mm_Base),
                    ["deg_angle"] = Units.RadToDeg(request.Rad_Angle),
                };

            case TransformKind.Scale:
                return new JsonObject
                {
                    ["mm_base"] = ToMmArray(request.Mm_Base),
                    ["factor"] = request.Factor,
                };

            case TransformKind.Mirror:
                return new JsonObject
                {
                    ["mm_line_start"] = ToMmArray(request.Mm_Base),
                    ["mm_line_end"] = ToMmArray(request.Mm_Secondary),
                };

            default:
                return new JsonObject();
        }
    }

    // ---- acad_copy --------------------------------------------------------------------------

    private static McpTool CreateCopyTool()
    {
        return new McpTool
        {
            Name = "acad_copy",
            Description =
                "Copies entities by handle, displaced by mm_displacement. With count greater "
                + "than 1 it makes a row of copies: copy N is displaced by N times "
                + "mm_displacement, so count 3 with a displacement of (100, 0) puts copies at "
                + "+100, +200 and +300 in X. The originals are untouched. Block references keep "
                + "their attributes. Returns the new handles grouped per copy. If any handle "
                + "cannot be resolved, nothing is created. For a rotated or mirrored copy use "
                + "acad_transform with copy: true.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Entity handles to copy.", minItems: 1, maxItems: MaxHandles, required: true)
                .PointMm("mm_displacement", "Offset from each original to its first copy. Must not be zero.", required: true)
                .Integer(
                    "count",
                    $"Number of copies to make along the displacement. At most {MaxNewEntities} new entities per call in total.",
                    minimum: 1,
                    maximum: CopyArrayPlan.MaxCount,
                    defaultValue: 1)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Modify,
            Handler = Copy,
        };
    }

    private static ToolResult Copy(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!PointArgs.TryReadPointMm(args, "mm_displacement", required: true, out Mm_Point mm_Displacement, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetIntInRange("count", 1, 1, CopyArrayPlan.MaxCount, out int count, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!CopyArrayPlan.TryBuild(
                mm_Displacement: mm_Displacement,
                count: count,
                handleCount: handles.Count,
                maxNewEntities: MaxNewEntities,
                mm_Offsets: out IReadOnlyList<Mm_Point> mm_Offsets,
                error: out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        List<string> sourceHandles = new();
        JsonArray copies = new();
        List<string> allNewHandles = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        OpenMode.ForRead,
                        requireAll: true,
                        out List<Entity> entities,
                        out JsonArray _,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryRejectDuplicates(entities, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryRejectUncopyable(tr, entities, out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    sourceHandles.Add(entity.Handle.ToString());
                }

                for (int i = 0; i < mm_Offsets.Count; i++)
                {
                    Mm_Point mm_Offset = mm_Offsets[i];
                    Point3d du_Offset = acad.ToPoint3d(mm_Offset);
                    Matrix3d matrix = Matrix3d.Displacement(du_Offset.GetAsVector());

                    // Every copy clones the ORIGINALS with the cumulative offset, rather than
                    // cloning the previous copy: no error accumulates down the row, and the
                    // set being cloned is the one that was validated.
                    List<string> newHandles = TransformApplier.ApplyAsCopy(tr, acad.Db, entities, matrix);
                    allNewHandles.AddRange(newHandles);

                    copies.Add(new JsonObject
                    {
                        ["index"] = i + 1,
                        ["mm_offset"] = ToMmArray(mm_Offset),
                        ["new_handles"] = ToJsonArray(newHandles),
                    });
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/copy count={count} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/copy count={count} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not copy the entities; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/copy count={count} entities={sourceHandles.Count} new_entities={allNewHandles.Count} "
            + $"handles={SummarizeHandles(sourceHandles)} new_handles={SummarizeHandles(allNewHandles)} "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handles"] = ToJsonArray(sourceHandles);
        payload["mm_displacement"] = ToMmArray(mm_Displacement);
        payload["count"] = count;
        payload["new_entity_count"] = allNewHandles.Count;
        payload["copies"] = copies;

        return ToolResult.Ok(payload);
    }

    // ---- shared -----------------------------------------------------------------------------

    /// <summary>
    /// Rejects entities that do not live directly in a block table record — an
    /// AttributeReference is owned by its BlockReference, and cloning one on its own produces
    /// an orphan. Copy the block reference instead; its attributes come with it.
    /// </summary>
    private static bool TryRejectUncopyable(
        Transaction tr,
        IReadOnlyList<Entity> entities,
        out string error)
    {
        error = "";
        List<string> offenders = new();

        foreach (Entity entity in entities)
        {
            DBObject owner = tr.GetObject(entity.OwnerId, OpenMode.ForRead);
            if (owner is not BlockTableRecord)
            {
                offenders.Add($"{entity.Handle} ({entity.GetRXClass().DxfName}, owned by a {owner.GetRXClass().DxfName})");
            }
        }

        if (offenders.Count == 0)
        {
            return true;
        }

        error = $"{offenders.Count} of {entities.Count} handles name entities that cannot be "
              + $"copied on their own, so nothing was created: {string.Join(", ", offenders)}. "
              + "Copy the block reference that owns them instead.";
        return false;
    }

    private static string DescribeAcadFailure(Autodesk.AutoCAD.Runtime.Exception acEx)
    {
        if (acEx.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
        {
            return "At least one entity is on a locked layer and cannot be transformed or "
                 + "copied. Unlock the layer with acad_set_layer_state first.";
        }

        return $"AutoCAD rejected the transform: {acEx.ErrorStatus}.";
    }

    private static string SummarizeHandles(IReadOnlyList<string> handles)
    {
        if (handles.Count == 0)
        {
            return "-";
        }

        if (handles.Count <= MaxLoggedHandles)
        {
            return string.Join(",", handles);
        }

        return $"{string.Join(",", handles.Take(MaxLoggedHandles))},+{handles.Count - MaxLoggedHandles}_more";
    }

    private static JsonArray ToMmArray(Mm_Point point)
    {
        return new JsonArray { point.Mm_X, point.Mm_Y, point.Mm_Z };
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
