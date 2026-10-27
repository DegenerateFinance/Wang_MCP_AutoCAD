using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Block definitions, references, attributes and dynamic properties.
///
/// Three AutoCAD behaviours shape almost everything here and are worth stating once:
///   - Entities inside a definition are relative to the block's origin, and Origin is NOT
///     applied as an offset — geometry is translated by minus the base point instead
///     (BlockSupport.CloneIntoDefinition).
///   - Attributes are not automatic on insertion, and changing a definition's attributes
///     does not reach existing references. Insert builds them; sync rebuilds them, keeping
///     values by tag (AttributeSyncPlan).
///   - A dynamic block's parameters and actions are not reachable from .NET, so a dynamic
///     block is never redefined — only its property values are read and written.
/// </summary>
internal static class BlockTools
{
    private const int MaxHandles = 1000;
    private const int MaxReadHandles = 500;
    private const int MaxLoggedHandles = 20;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateListBlocksTool());
        registry.Add(CreateGetDefinitionTool());
        registry.Add(CreateCreateBlockTool());
        registry.Add(CreateInsertBlockTool());
        registry.Add(CreateGetAttributesTool());
        registry.Add(CreateSetAttributesTool());
        registry.Add(CreateRedefineBlockTool());
        registry.Add(CreateSyncAttributesTool());
        registry.Add(CreateGetDynamicPropertiesTool());
        registry.Add(CreateSetDynamicPropertiesTool());
        registry.Add(CreateConvertToStaticTool());
    }

    // ---- acad_list_blocks -------------------------------------------------------------------

    private static McpTool CreateListBlocksTool()
    {
        return new McpTool
        {
            Name = "acad_list_blocks",
            Description =
                "Lists the block definitions in a drawing: name, whether it is dynamic, whether "
                + "it has attributes, how many references it has and how many entities it "
                + "contains. Layouts are never listed. Anonymous blocks (dynamic block variants, "
                + "dimension internals) and external references are hidden unless asked for.",
            InputSchema = ToolSchema.Object()
                .Boolean("include_anonymous", "Also list anonymous \"*\" blocks.", false)
                .Boolean("include_xrefs", "Also list loaded external references.", false)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Blocks,
            Handler = ListBlocks,
        };
    }

    private static ToolResult ListBlocks(ToolArgs args)
    {
        if (!args.TryGetBoolOrDefault("include_anonymous", false, out bool includeAnonymous, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("include_xrefs", false, out bool includeXrefs, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray blocks = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(acad.Db.BlockTableId, OpenMode.ForRead);

                foreach (ObjectId id in blockTable)
                {
                    BlockTableRecord definition = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);

                    if (!BlockRules.ShouldList(
                            isLayout: definition.IsLayout,
                            isAnonymous: definition.IsAnonymous,
                            isXref: definition.IsFromExternalReference,
                            isUnloaded: definition.IsUnloaded,
                            includeAnonymous: includeAnonymous,
                            includeXrefs: includeXrefs))
                    {
                        continue;
                    }

                    int entityCount = 0;
                    foreach (ObjectId _ in definition)
                    {
                        entityCount++;
                    }

                    blocks.Add(new JsonObject
                    {
                        ["name"] = definition.Name,
                        ["is_dynamic"] = definition.IsDynamicBlock,
                        ["is_anonymous"] = definition.IsAnonymous,
                        ["is_xref"] = definition.IsFromExternalReference,
                        ["has_attributes"] = definition.HasAttributeDefinitions,
                        ["reference_count"] = BlockSupport.GetAllReferenceIds(tr, definition).Count,
                        ["entity_count"] = entityCount,
                        ["uniform_scaling_only"] = definition.BlockScaling == BlockScaling.Uniform,
                        ["explodable"] = definition.Explodable,
                        ["description"] = definition.Comments,
                    });
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/list_blocks outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/list_blocks outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not list the blocks; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/list_blocks count={blocks.Count} include_anonymous={includeAnonymous} include_xrefs={includeXrefs} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["count"] = blocks.Count;
        payload["blocks"] = blocks;

        return ToolResult.Ok(payload);
    }

    // ---- acad_get_block_definition ----------------------------------------------------------

    private static McpTool CreateGetDefinitionTool()
    {
        return new McpTool
        {
            Name = "acad_get_block_definition",
            Description =
                "Reads one block definition: its attribute definitions (tag, prompt, default, "
                + "constant, position), its entities and how many references it has. Entity "
                + "coordinates are in the block's own space, relative to its base point, in "
                + "millimetres.",
            InputSchema = ToolSchema.Object()
                .String("name", "Block name, as listed by acad_list_blocks.", required: true)
                .Integer("limit", "Maximum number of entities to describe.", minimum: 0, maximum: 1000, defaultValue: 200)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Blocks,
            Handler = GetDefinition,
        };
    }

    private static ToolResult GetDefinition(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetIntInRange("limit", 200, 0, 1000, out int limit, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonObject payload = acad.NewPayload();
        int entityCount = 0;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!BlockSupport.TryResolveDefinition(tr, acad.Db, name, OpenMode.ForRead, out BlockTableRecord definition, out error))
                {
                    return ToolResult.Fail(error);
                }

                JsonArray entities = new();
                JsonArray attributeDefinitions = new();

                foreach (ObjectId id in definition)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    {
                        continue;
                    }

                    entityCount++;

                    if (entity is AttributeDefinition attributeDefinition)
                    {
                        attributeDefinitions.Add(DescribeAttributeDefinition(acad, attributeDefinition));
                    }

                    if (entities.Count < limit)
                    {
                        entities.Add(EntityDescriber.Describe(acad, entity, EntityDescriber.DetailSummary));
                    }
                }

                payload["name"] = definition.Name;
                payload["is_dynamic"] = definition.IsDynamicBlock;
                payload["is_anonymous"] = definition.IsAnonymous;
                payload["is_xref"] = definition.IsFromExternalReference;
                payload["is_layout"] = definition.IsLayout;
                payload["mm_origin"] = acad.ToMmArray(definition.Origin);
                payload["uniform_scaling_only"] = definition.BlockScaling == BlockScaling.Uniform;
                payload["explodable"] = definition.Explodable;
                payload["description"] = definition.Comments;
                payload["reference_count"] = BlockSupport.GetAllReferenceIds(tr, definition).Count;
                payload["entity_count"] = entityCount;
                payload["returned"] = entities.Count;
                payload["truncated"] = entityCount > entities.Count;
                payload["attribute_definitions"] = attributeDefinitions;
                payload["entities"] = entities;

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/get_block_definition name=\"{name}\" outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/get_block_definition name=\"{name}\" outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not read the block definition; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/get_block_definition name=\"{name}\" entities={entityCount} elapsed_ms={sw.ElapsedMilliseconds}");

        return ToolResult.Ok(payload);
    }

    private static JsonObject DescribeAttributeDefinition(AcadContext acad, AttributeDefinition attributeDefinition)
    {
        int attachmentPoint = (int)attributeDefinition.Justify;

        // For anything but baseline-left the authoritative point is AlignmentPoint.
        Point3d du_Position = TextJustification.UsesPositionOnly(attachmentPoint)
            ? attributeDefinition.Position
            : attributeDefinition.AlignmentPoint;

        return new JsonObject
        {
            ["tag"] = attributeDefinition.Tag,
            ["prompt"] = attributeDefinition.Prompt,
            ["default"] = attributeDefinition.TextString,
            ["is_constant"] = attributeDefinition.Constant,
            ["is_invisible"] = attributeDefinition.Invisible,
            ["is_preset"] = attributeDefinition.Preset,
            ["is_verifiable"] = attributeDefinition.Verifiable,
            ["is_multiline"] = attributeDefinition.IsMTextAttributeDefinition,
            ["mm_position"] = acad.ToMmArray(du_Position),
            ["mm_height"] = acad.ToMm(attributeDefinition.Height),
            ["justify"] = TextJustification.DescribeAttachmentPoint(attachmentPoint),
            ["handle"] = attributeDefinition.Handle.ToString(),
        };
    }

    // ---- acad_create_block ------------------------------------------------------------------

    private static McpTool CreateCreateBlockTool()
    {
        return new McpTool
        {
            Name = "acad_create_block",
            Description =
                "Creates a new block definition from existing entities (by handle) and/or new "
                + "attribute definitions. mm_base_point becomes the block's insertion point. "
                + "source_action decides what happens to the source entities: \"replace\" (the "
                + "default, like AutoCAD's BLOCK command) erases them and inserts a reference in "
                + "their place, \"erase\" erases them, \"keep\" leaves them. Attribute positions "
                + "are world millimetres, in the same frame as the source entities. Fails if the "
                + "name exists — use acad_redefine_block to change an existing block. If any "
                + "handle cannot be resolved, nothing is created.",
            InputSchema = ToolSchema.Object()
                .String("name", "New block name. Must not exist yet and must not start with '*'.", required: true)
                .StringArray("handles", "Entities to put in the block. They must all be in the same space.", minItems: 1, maxItems: MaxHandles)
                .Property("attributes", AttributeSpecReader.BuildSchema("Attribute definitions to add to the block."))
                .PointMm("mm_base_point", "The block's base point (insertion point), in world coordinates.", required: true)
                .StringEnum("source_action", "What to do with the source entities.", BlockRules.SourceActions, defaultValue: BlockRules.DefaultSourceAction)
                .String("description", "Optional block description.")
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = CreateBlock,
        };
    }

    private static ToolResult CreateBlock(ToolArgs args)
    {
        if (!TryReadDefinitionRequest(args, out DefinitionRequest request, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!BlockNaming.TryValidate(request.Name, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("description", null, out string? description, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        DefinitionOutcome outcome;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(acad.Db.BlockTableId, OpenMode.ForRead);
                if (blockTable.Has(request.Name))
                {
                    return ToolResult.Fail(
                        $"Block \"{request.Name}\" already exists. Use acad_redefine_block to change "
                        + "its contents, or choose another name.");
                }

                if (!TryResolveSources(tr, acad, request, out List<Entity> sources, out ObjectId ownerId, out error))
                {
                    return ToolResult.Fail(error);
                }

                BlockTableRecord definition = new()
                {
                    Name = request.Name,
                    Origin = Point3d.Origin,
                };

                if (!string.IsNullOrWhiteSpace(description))
                {
                    definition.Comments = description;
                }

                blockTable.UpgradeOpen();
                blockTable.Add(definition);
                tr.AddNewlyCreatedDBObject(definition, true);

                outcome = FillDefinition(tr, acad, definition, request, sources, ownerId);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/create_block name=\"{request.Name}\" outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "create the block"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/create_block name=\"{request.Name}\" outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the block; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/create_block name=\"{request.Name}\" entities={outcome.EntityCount} "
            + $"attributes={outcome.AttributeTags.Count} source_action={request.SourceAction} "
            + $"handles={SummarizeHandles(request.Handles)} inserted={outcome.InsertedHandle ?? "-"} "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = request.Name;
        AddOutcome(payload, request, outcome);

        return ToolResult.Ok(payload);
    }

    // ---- acad_redefine_block ----------------------------------------------------------------

    private static McpTool CreateRedefineBlockTool()
    {
        return new McpTool
        {
            Name = "acad_redefine_block",
            Description =
                "Replaces the contents of an existing block definition with entities (by handle) "
                + "and/or new attribute definitions. Every existing reference updates. By default "
                + "the references' attributes are then synced to the new definition, KEEPING "
                + "their current values by tag (sync_attributes, preserve_values). Refuses "
                + "dynamic blocks, xrefs, layouts, anonymous blocks, and any redefinition that "
                + "would make the block contain itself. Arguments otherwise match "
                + "acad_create_block.",
            InputSchema = ToolSchema.Object()
                .String("name", "The block to redefine.", required: true)
                .StringArray("handles", "Entities that become the block's new contents. They must all be in the same space.", minItems: 1, maxItems: MaxHandles)
                .Property("attributes", AttributeSpecReader.BuildSchema("Attribute definitions for the new contents. Existing attribute definitions are removed with the rest of the old contents, so list every attribute the block should keep."))
                .PointMm("mm_base_point", "The block's base point, in world coordinates.", required: true)
                .StringEnum("source_action", "What to do with the source entities.", BlockRules.SourceActions, defaultValue: BlockRules.DefaultSourceAction)
                .Boolean("sync_attributes", "Rebuild every reference's attributes from the new definition.", true)
                .Boolean("preserve_values", "When syncing, keep each reference's current value for tags that still exist. Turning this off resets every reference to the defaults.", true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = RedefineBlock,
        };
    }

    private static ToolResult RedefineBlock(ToolArgs args)
    {
        if (!TryReadDefinitionRequest(args, out DefinitionRequest request, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("sync_attributes", true, out bool syncAttributes, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("preserve_values", true, out bool preserveValues, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        DefinitionOutcome outcome;
        int removedCount = 0;
        SyncTotals totals = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!BlockSupport.TryResolveDefinition(tr, acad.Db, request.Name, OpenMode.ForWrite, out BlockTableRecord definition, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!BlockRules.TryCheckRedefinable(
                        name: definition.Name,
                        isLayout: definition.IsLayout,
                        isXref: definition.IsFromExternalReference || definition.IsFromOverlayReference,
                        isAnonymous: definition.IsAnonymous,
                        isDynamic: definition.IsDynamicBlock,
                        error: out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryResolveSources(tr, acad, request, out List<Entity> sources, out ObjectId ownerId, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryRejectSelfReference(tr, sources, definition, out error))
                {
                    return ToolResult.Fail(error);
                }

                // The references that exist now. Collected before the replacement reference
                // (if any) is inserted — that one is built from the new definition already.
                List<ObjectId> referenceIds = BlockSupport.GetAllReferenceIds(tr, definition);

                // Snapshot first, erase second: erasing while enumerating the record is undefined.
                List<ObjectId> oldContents = new();
                foreach (ObjectId id in definition)
                {
                    oldContents.Add(id);
                }

                foreach (ObjectId id in oldContents)
                {
                    DBObject old = tr.GetObject(id, OpenMode.ForWrite);
                    old.Erase();
                    removedCount++;
                }

                definition.Origin = Point3d.Origin;

                outcome = FillDefinition(tr, acad, definition, request, sources, ownerId);

                foreach (ObjectId referenceId in referenceIds)
                {
                    BlockReference reference = (BlockReference)tr.GetObject(referenceId, OpenMode.ForWrite);

                    if (syncAttributes)
                    {
                        totals.Add(BlockSupport.SyncReference(tr, acad.Db, reference, preserveValues));
                    }
                    else
                    {
                        reference.RecordGraphicsModified(true);
                    }
                }

                totals.References = referenceIds.Count;

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/redefine_block name=\"{request.Name}\" outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "redefine the block"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/redefine_block name=\"{request.Name}\" outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not redefine the block; see the MCP log.");
        }

        bool regenerated = TryRegen(acad, "redefine_block");

        sw.Stop();
        Log.Info(
            $"op=tool/redefine_block name=\"{request.Name}\" removed={removedCount} entities={outcome.EntityCount} "
            + $"attributes={outcome.AttributeTags.Count} references={totals.References} synced={syncAttributes} "
            + $"preserved={totals.Preserved} reset={totals.Reset} handles={SummarizeHandles(request.Handles)} "
            + $"regen={regenerated} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = request.Name;
        payload["removed_entity_count"] = removedCount;
        AddOutcome(payload, request, outcome);
        payload["references_updated"] = totals.References;
        payload["attributes_synced"] = syncAttributes;
        payload["sync"] = totals.Describe(preserveValues);
        payload["regenerated"] = regenerated;

        return ToolResult.Ok(payload);
    }

    private static bool TryRejectSelfReference(
        Transaction tr,
        IReadOnlyList<Entity> sources,
        BlockTableRecord definition,
        out string error)
    {
        error = "";

        foreach (Entity source in sources)
        {
            if (source is not BlockReference nested)
            {
                continue;
            }

            HashSet<ObjectId> visited = new();
            bool cycles = BlockSupport.ReferencesBlock(tr, nested.BlockTableRecord, definition.ObjectId, visited)
                || (nested.IsDynamicBlock && BlockSupport.ReferencesBlock(tr, nested.DynamicBlockTableRecord, definition.ObjectId, visited));

            if (cycles)
            {
                error = $"Handle {nested.Handle} is a block reference that contains \"{definition.Name}\" "
                      + "(directly or nested), so the block would contain itself. Nothing was changed.";
                return false;
            }
        }

        return true;
    }

    // ---- shared by create / redefine --------------------------------------------------------

    private sealed record DefinitionRequest(
        string Name,
        IReadOnlyList<string> Handles,
        IReadOnlyList<AttributeSpec> Attributes,
        Mm_Point Mm_BasePoint,
        string SourceAction);

    private sealed record DefinitionOutcome(
        int EntityCount,
        IReadOnlyList<string> AttributeTags,
        int ErasedCount,
        string? InsertedHandle);

    private static bool TryReadDefinitionRequest(ToolArgs args, out DefinitionRequest request, out string error)
    {
        request = null!;

        if (!args.TryGetString("name", out string name, out error))
        {
            return false;
        }

        if (!args.RequireAtLeastOneOf(new[] { "handles", "attributes" }, "source of block content", out error))
        {
            return false;
        }

        IReadOnlyList<string> handles = Array.Empty<string>();
        if (args.HasArgument("handles") && !args.TryGetStringArray("handles", 1, MaxHandles, out handles, out error))
        {
            return false;
        }

        if (!AttributeSpecReader.TryReadList(args, "attributes", out IReadOnlyList<AttributeSpec> attributes, out error))
        {
            return false;
        }

        if (handles.Count == 0 && attributes.Count == 0)
        {
            error = "The block would be empty: give at least one handle or one attribute.";
            return false;
        }

        if (!PointArgs.TryReadPointMm(args, "mm_base_point", required: true, out Mm_Point mm_BasePoint, out _, out error))
        {
            return false;
        }

        if (!args.TryGetEnum("source_action", BlockRules.SourceActions, BlockRules.DefaultSourceAction, out string sourceAction, out error))
        {
            return false;
        }

        request = new DefinitionRequest(name.Trim(), handles, attributes, mm_BasePoint, sourceAction);
        return true;
    }

    /// <summary>
    /// Resolves the source entities. They must all live directly in the same layout (model or
    /// paper space): DeepCloneObjects needs a single owner, "replace" needs one space to put
    /// the new reference in, and an attribute or a polyline vertex cannot become block content
    /// on its own.
    /// </summary>
    private static bool TryResolveSources(
        Transaction tr,
        AcadContext acad,
        DefinitionRequest request,
        out List<Entity> sources,
        out ObjectId ownerId,
        out string error)
    {
        sources = new List<Entity>();
        ownerId = ObjectId.Null;
        error = "";

        if (request.Handles.Count == 0)
        {
            return true;
        }

        OpenMode openMode = request.SourceAction == BlockRules.SourceKeep ? OpenMode.ForRead : OpenMode.ForWrite;

        if (!EntityResolver.TryResolveHandles(tr, acad.Db, request.Handles, openMode, requireAll: true, out sources, out JsonArray _, out error))
        {
            return false;
        }

        if (!EntityResolver.TryRejectDuplicates(sources, out error))
        {
            return false;
        }

        foreach (Entity source in sources)
        {
            if (tr.GetObject(source.OwnerId, OpenMode.ForRead) is not BlockTableRecord owner || !owner.IsLayout)
            {
                error = $"Handle {source.Handle} ({source.GetRXClass().DxfName}) is not directly in model "
                      + "or paper space — it belongs to another object or block — so it cannot become "
                      + "block content. Nothing was changed.";
                return false;
            }

            if (ownerId.IsNull)
            {
                ownerId = source.OwnerId;
            }
            else if (ownerId != source.OwnerId)
            {
                error = "The source entities are in different spaces (model and paper). Put all "
                      + "of a block's source entities in the same space. Nothing was changed.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Fills an empty definition from the request, then carries out the source action. Clone
    /// before erase: cloning an erased object fails.
    /// </summary>
    private static DefinitionOutcome FillDefinition(
        Transaction tr,
        AcadContext acad,
        BlockTableRecord definition,
        DefinitionRequest request,
        IReadOnlyList<Entity> sources,
        ObjectId ownerId)
    {
        Point3d du_Base = acad.ToPoint3d(request.Mm_BasePoint);

        int entityCount = BlockSupport.CloneIntoDefinition(tr, acad.Db, sources, definition, du_Base);
        List<string> tags = BlockSupport.AddAttributeDefinitions(tr, acad, definition, request.Attributes, request.Mm_BasePoint);

        int erased = 0;
        string? insertedHandle = null;

        if (sources.Count == 0 || request.SourceAction == BlockRules.SourceKeep)
        {
            return new DefinitionOutcome(entityCount + tags.Count, tags, erased, insertedHandle);
        }

        foreach (Entity source in sources)
        {
            source.Erase();
            erased++;
        }

        if (request.SourceAction == BlockRules.SourceReplace)
        {
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);
            LayerTableRecord currentLayer = (LayerTableRecord)tr.GetObject(acad.Db.Clayer, OpenMode.ForRead);
            List<AttributeDefinition> attributeDefinitions = BlockSupport.GetAttributeDefinitions(tr, definition);

            BlockReference reference = BlockSupport.InsertReference(
                tr: tr,
                db: acad.Db,
                space: space,
                definition: definition,
                attributeDefinitions: attributeDefinitions,
                valuesBySlot: new string?[attributeDefinitions.Count],
                du_Position: du_Base,
                rad_Rotation: 0,
                scale: new Scale3d(1),
                layer: currentLayer.Name);

            insertedHandle = reference.Handle.ToString();
        }

        return new DefinitionOutcome(entityCount + tags.Count, tags, erased, insertedHandle);
    }

    private static void AddOutcome(JsonObject payload, DefinitionRequest request, DefinitionOutcome outcome)
    {
        payload["entity_count"] = outcome.EntityCount;
        payload["attribute_tags"] = ToJsonArray(outcome.AttributeTags);
        payload["mm_base_point"] = ToMmArray(request.Mm_BasePoint);
        payload["source_action"] = request.SourceAction;
        payload["source_handles"] = ToJsonArray(request.Handles);
        payload["erased_source_count"] = outcome.ErasedCount;
        payload["inserted_handle"] = outcome.InsertedHandle;
    }

    // ---- acad_insert_block ------------------------------------------------------------------

    private static McpTool CreateInsertBlockTool()
    {
        return new McpTool
        {
            Name = "acad_insert_block",
            Description =
                "Inserts a reference to a block definition, with its attributes filled in. "
                + "Attributes not given in \"attributes\" get the definition's default; constant "
                + "attributes cannot be set and are reported separately. Rotation is degrees "
                + "counter-clockwise. Scale is either one uniform \"scale\" or per-axis "
                + "scale_x/scale_y/scale_z (refused for blocks that only allow uniform scaling). "
                + "Returns the reference's handle and each attribute's handle and value.",
            InputSchema = ToolSchema.Object()
                .String("name", "Block name, as listed by acad_list_blocks.", required: true)
                .PointMm("mm_position", "Insertion point.", required: true)
                .Number("deg_rotation", "Rotation in degrees, counter-clockwise. Defaults to 0.")
                .Number("scale", "Uniform scale factor. Dimensionless; must not be 0. Defaults to 1.")
                .Number("scale_x", "X scale factor, instead of \"scale\". Must not be 0.")
                .Number("scale_y", "Y scale factor, instead of \"scale\". Must not be 0.")
                .Number("scale_z", "Z scale factor, instead of \"scale\". Must not be 0.")
                .StringMap("attributes", "Attribute values by tag, e.g. {\"TAG\": \"A1\"}. Tags match case-insensitively.")
                .String("layer", "Layer for the reference. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to insert into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = InsertBlock,
        };
    }

    private static ToolResult InsertBlock(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (BlockNaming.IsAnonymousOrLayout(name))
        {
            return ToolResult.Fail(
                $"\"{name}\" is an anonymous block or a layout and cannot be inserted by name. "
                + "Insert the named block it belongs to.");
        }

        if (!PointArgs.TryReadPointMm(args, "mm_position", required: true, out Mm_Point mm_Position, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDoubleOrDefault("deg_rotation", 0, out double deg_Rotation, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!InsertScale.TryRead(args, out double scaleX, out double scaleY, out double scaleZ, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AttributeAssignment.TryReadValueMap(args, "attributes", out IReadOnlyDictionary<string, string> values, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("layer", null, out string? layer, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetEnum("space", EntityResolver.AllowedSpaces, EntityResolver.DefaultSpace, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonObject payload = acad.NewPayload();
        string handle;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!BlockSupport.TryResolveDefinition(tr, acad.Db, name, OpenMode.ForRead, out BlockTableRecord definition, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (definition.BlockScaling == BlockScaling.Uniform && !InsertScale.IsUniform(scaleX, scaleY, scaleZ))
                {
                    return ToolResult.Fail(InsertScale.DescribeUniformRequired(definition.Name, scaleX, scaleY, scaleZ));
                }

                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out string resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                List<AttributeDefinition> attributeDefinitions = BlockSupport.GetAttributeDefinitions(tr, definition);

                if (!AttributeAssignment.TryPlan(BlockSupport.ToSlots(attributeDefinitions), values, out string?[] valuesBySlot, out error))
                {
                    return ToolResult.Fail(error);
                }

                BlockReference reference = BlockSupport.InsertReference(
                    tr: tr,
                    db: acad.Db,
                    space: target,
                    definition: definition,
                    attributeDefinitions: attributeDefinitions,
                    valuesBySlot: valuesBySlot,
                    du_Position: acad.ToPoint3d(mm_Position),
                    rad_Rotation: Units.DegToRad(deg_Rotation),
                    scale: new Scale3d(scaleX, scaleY, scaleZ),
                    layer: resolvedLayer);

                handle = reference.Handle.ToString();

                JsonArray constants = new();
                foreach (AttributeDefinition attributeDefinition in attributeDefinitions)
                {
                    if (attributeDefinition.Constant)
                    {
                        constants.Add(new JsonObject
                        {
                            ["tag"] = attributeDefinition.Tag,
                            ["value"] = attributeDefinition.TextString,
                        });
                    }
                }

                payload["handle"] = handle;
                payload["block_name"] = definition.Name;
                payload["is_dynamic"] = definition.IsDynamicBlock;
                payload["layer"] = resolvedLayer;
                payload["mm_position"] = ToMmArray(mm_Position);
                payload["deg_rotation"] = Units.NormalizeDeg(deg_Rotation);
                payload["scale"] = new JsonArray { scaleX, scaleY, scaleZ };
                payload["attributes"] = BlockSupport.DescribeAttributes(BlockSupport.GetAttributeReferences(tr, reference, OpenMode.ForRead));
                payload["constant_attributes"] = constants;

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/insert_block name=\"{name}\" outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "insert the block"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/insert_block name=\"{name}\" outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not insert the block; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/insert_block name=\"{name}\" handle={handle} attributes_set={values.Count} "
            + $"entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        return ToolResult.Ok(payload);
    }

    // ---- acad_get_block_attributes ----------------------------------------------------------

    private static McpTool CreateGetAttributesTool()
    {
        return new McpTool
        {
            Name = "acad_get_block_attributes",
            Description =
                "Reads the attribute values of block references by handle, including the "
                + "constant attributes that live in the definition (without them an attributed "
                + "block can look like it has none). Also lists definition tags missing from a "
                + "reference — run acad_sync_block_attributes to add them. Handles that are not "
                + "block references are reported individually rather than failing the call.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Block reference handles.", minItems: 1, maxItems: MaxReadHandles, required: true)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Blocks,
            Handler = GetAttributes,
        };
    }

    private static ToolResult GetAttributes(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxReadHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray references = new();
        JsonArray failures;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(tr, acad.Db, handles, OpenMode.ForRead, requireAll: false, out List<Entity> entities, out failures, out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    if (entity is not BlockReference reference)
                    {
                        failures.Add(new JsonObject
                        {
                            ["handle"] = entity.Handle.ToString(),
                            ["reason"] = $"this is a {entity.GetRXClass().DxfName}, not a block reference",
                        });
                        continue;
                    }

                    references.Add(DescribeReferenceAttributes(tr, reference));
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/get_block_attributes outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/get_block_attributes outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not read the attributes; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/get_block_attributes requested={handles.Count} references={references.Count} failed={failures.Count} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["requested"] = handles.Count;
        payload["references"] = references;
        payload["failures"] = failures;

        return ToolResult.Ok(payload);
    }

    private static JsonObject DescribeReferenceAttributes(Transaction tr, BlockReference reference)
    {
        BlockTableRecord definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
        List<AttributeReference> attributes = BlockSupport.GetAttributeReferences(tr, reference, OpenMode.ForRead);

        HashSet<string> presentTags = new(StringComparer.OrdinalIgnoreCase);
        foreach (AttributeReference attribute in attributes)
        {
            presentTags.Add(attribute.Tag);
        }

        JsonArray constants = new();
        JsonArray missing = new();

        foreach (AttributeDefinition attributeDefinition in BlockSupport.GetAttributeDefinitions(tr, definition))
        {
            if (attributeDefinition.Constant)
            {
                constants.Add(new JsonObject
                {
                    ["tag"] = attributeDefinition.Tag,
                    ["value"] = attributeDefinition.TextString,
                    ["is_constant"] = true,
                });
            }
            else if (!presentTags.Contains(attributeDefinition.Tag))
            {
                missing.Add(attributeDefinition.Tag);
            }
        }

        return new JsonObject
        {
            ["handle"] = reference.Handle.ToString(),
            ["block_name"] = BlockSupport.GetEffectiveName(tr, reference),
            ["attributes"] = BlockSupport.DescribeAttributes(attributes),
            ["constant_attributes"] = constants,
            ["tags_missing_from_reference"] = missing,
        };
    }

    // ---- acad_set_block_attributes ----------------------------------------------------------

    private static McpTool CreateSetAttributesTool()
    {
        return new McpTool
        {
            Name = "acad_set_block_attributes",
            Description =
                "Sets attribute values on block references by handle. Only the tags given change; "
                + "the rest keep their values. Constant attributes cannot be set per reference. "
                + "Every handle must be a block reference carrying every given tag; if anything "
                + "is wrong — an unknown tag, a constant tag, a bad handle — nothing is changed.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Block reference handles. All get the same values.", minItems: 1, maxItems: MaxHandles, required: true)
                .StringMap("attributes", "Attribute values by tag, e.g. {\"TAG\": \"A1\"}. Tags match case-insensitively.", required: true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = SetAttributes,
        };
    }

    private static ToolResult SetAttributes(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!AttributeAssignment.TryReadValueMap(args, "attributes", out IReadOnlyDictionary<string, string> values, out error))
        {
            return ToolResult.Fail(error);
        }

        if (values.Count == 0)
        {
            return ToolResult.Fail("Argument \"attributes\" must give at least one tag and value.");
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        int attributesChanged = 0;
        List<string> changedHandles = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!TryResolveReferences(tr, acad, handles, OpenMode.ForWrite, out List<BlockReference> references, out error))
                {
                    return ToolResult.Fail(error);
                }

                // Plan every reference before writing any: all-or-nothing.
                List<(List<AttributeReference> Attributes, string?[] Values)> plans = new();

                foreach (BlockReference reference in references)
                {
                    List<AttributeReference> attributes = BlockSupport.GetAttributeReferences(tr, reference, OpenMode.ForWrite);

                    List<AttributeSlot> slots = new();
                    foreach (AttributeReference attribute in attributes)
                    {
                        slots.Add(new AttributeSlot(attribute.Tag, IsConstant: false, attribute.IsMTextAttribute));
                    }

                    BlockTableRecord definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                    foreach (AttributeDefinition attributeDefinition in BlockSupport.GetAttributeDefinitions(tr, definition))
                    {
                        if (attributeDefinition.Constant)
                        {
                            slots.Add(new AttributeSlot(attributeDefinition.Tag, IsConstant: true, IsMultiline: false));
                        }
                    }

                    if (!AttributeAssignment.TryPlan(slots, values, out string?[] valuesBySlot, out error))
                    {
                        return ToolResult.Fail(
                            $"Block reference {reference.Handle} ({BlockSupport.GetEffectiveName(tr, reference)}): {error} "
                            + "If a tag was added to the block definition after this reference was "
                            + "inserted, run acad_sync_block_attributes first.");
                    }

                    plans.Add((attributes, valuesBySlot));
                }

                for (int r = 0; r < references.Count; r++)
                {
                    (List<AttributeReference> attributes, string?[] valuesBySlot) = plans[r];

                    for (int i = 0; i < attributes.Count; i++)
                    {
                        string? value = valuesBySlot[i];
                        if (value is null)
                        {
                            continue;
                        }

                        BlockSupport.SetAttributeValue(acad.Db, attributes[i], value);
                        attributesChanged++;
                    }

                    changedHandles.Add(references[r].Handle.ToString());
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_block_attributes outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "change the attributes"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_block_attributes outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not change the attributes; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/set_block_attributes references={changedHandles.Count} attributes_changed={attributesChanged} "
            + $"handles={SummarizeHandles(changedHandles)} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["changed"] = changedHandles.Count;
        payload["attributes_changed"] = attributesChanged;
        payload["handles"] = ToJsonArray(changedHandles);

        return ToolResult.Ok(payload);
    }

    // ---- acad_sync_block_attributes ---------------------------------------------------------

    private static McpTool CreateSyncAttributesTool()
    {
        return new McpTool
        {
            Name = "acad_sync_block_attributes",
            Description =
                "The ATTSYNC equivalent: rebuilds the attributes of every reference to a block "
                + "from its current definition, so attributes added to the definition appear, "
                + "removed ones disappear, and position, height and style follow the definition. "
                + "With preserve_values (the default) each reference KEEPS its current value for "
                + "tags that still exist; turning it off resets every reference to the defaults.",
            InputSchema = ToolSchema.Object()
                .String("name", "The block whose references to sync.", required: true)
                .Boolean("preserve_values", "Keep each reference's current values for tags that still exist.", true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = SyncAttributes,
        };
    }

    private static ToolResult SyncAttributes(ToolArgs args)
    {
        if (!args.TryGetString("name", out string name, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("preserve_values", true, out bool preserveValues, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        SyncTotals totals = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!BlockSupport.TryResolveDefinition(tr, acad.Db, name, OpenMode.ForRead, out BlockTableRecord definition, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (definition.IsLayout || definition.IsFromExternalReference || definition.IsAnonymous)
                {
                    return ToolResult.Fail(
                        $"\"{definition.Name}\" is a layout, an xref or an anonymous block; only named "
                        + "block definitions can have their attributes synced.");
                }

                // A dynamic block's references point at anonymous variants, each carrying its
                // own copy of the attribute definitions. Refresh those copies first.
                if (definition.IsDynamicBlock)
                {
                    definition.UpgradeOpen();
                    definition.UpdateAnonymousBlocks();
                }

                List<ObjectId> referenceIds = BlockSupport.GetAllReferenceIds(tr, definition);
                foreach (ObjectId referenceId in referenceIds)
                {
                    BlockReference reference = (BlockReference)tr.GetObject(referenceId, OpenMode.ForWrite);
                    totals.Add(BlockSupport.SyncReference(tr, acad.Db, reference, preserveValues));
                }

                totals.References = referenceIds.Count;

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/sync_block_attributes name=\"{name}\" outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "sync the attributes"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/sync_block_attributes name=\"{name}\" outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not sync the attributes; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/sync_block_attributes name=\"{name}\" references={totals.References} preserved={totals.Preserved} "
            + $"reset={totals.Reset} added={totals.AddedTotal} removed={totals.RemovedTotal} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["name"] = name;
        payload["references_synced"] = totals.References;
        payload["sync"] = totals.Describe(preserveValues);

        return ToolResult.Ok(payload);
    }

    /// <summary>Sync results summed across references, for one payload object.</summary>
    private sealed class SyncTotals
    {
        private readonly Dictionary<string, int> _added = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _removed = new(StringComparer.OrdinalIgnoreCase);

        public int References { get; set; }

        public int Preserved { get; private set; }

        public int Reset { get; private set; }

        public int AddedTotal { get; private set; }

        public int RemovedTotal { get; private set; }

        public void Add(ReferenceSyncResult result)
        {
            Preserved += result.Preserved;
            Reset += result.Reset;

            foreach (string tag in result.AddedTags)
            {
                _added[tag] = _added.TryGetValue(tag, out int count) ? count + 1 : 1;
                AddedTotal++;
            }

            foreach (string tag in result.RemovedTags)
            {
                _removed[tag] = _removed.TryGetValue(tag, out int count) ? count + 1 : 1;
                RemovedTotal++;
            }
        }

        public JsonObject Describe(bool preserveValues)
        {
            return new JsonObject
            {
                ["preserve_values"] = preserveValues,
                ["values_preserved"] = Preserved,
                ["values_reset_to_default"] = Reset,
                ["added_by_tag"] = ToCountObject(_added),
                ["removed_by_tag"] = ToCountObject(_removed),
            };
        }

        private static JsonObject ToCountObject(Dictionary<string, int> counts)
        {
            JsonObject result = new();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }
    }

    // ---- acad_get_dynamic_block_properties --------------------------------------------------

    private static McpTool CreateGetDynamicPropertiesTool()
    {
        return new McpTool
        {
            Name = "acad_get_dynamic_block_properties",
            Description =
                "Reads the dynamic properties (distances, angles, visibility states, lookups, "
                + "flips) of dynamic block references by handle: name, current value, allowed "
                + "values and whether it can be set. Distances are millimetres, angles degrees, "
                + "areas mm². Handles that are not dynamic block references are reported "
                + "individually rather than failing the call.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Dynamic block reference handles.", minItems: 1, maxItems: MaxReadHandles, required: true)
                .Build(),
            RequiresDocument = true,
            IsReadOnly = true,
            Group = ToolGroups.Blocks,
            Handler = GetDynamicProperties,
        };
    }

    private static ToolResult GetDynamicProperties(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxReadHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray references = new();
        JsonArray failures;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(tr, acad.Db, handles, OpenMode.ForRead, requireAll: false, out List<Entity> entities, out failures, out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    if (entity is not BlockReference reference || !reference.IsDynamicBlock)
                    {
                        failures.Add(new JsonObject
                        {
                            ["handle"] = entity.Handle.ToString(),
                            ["reason"] = "not a dynamic block reference",
                        });
                        continue;
                    }

                    JsonArray properties = new();
                    foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                    {
                        properties.Add(DescribeDynamicProperty(acad, property));
                    }

                    references.Add(new JsonObject
                    {
                        ["handle"] = reference.Handle.ToString(),
                        ["block_name"] = BlockSupport.GetEffectiveName(tr, reference),
                        ["properties"] = properties,
                    });
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/get_dynamic_block_properties outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the query: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/get_dynamic_block_properties outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not read the dynamic properties; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/get_dynamic_block_properties requested={handles.Count} references={references.Count} failed={failures.Count} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["requested"] = handles.Count;
        payload["references"] = references;
        payload["failures"] = failures;

        return ToolResult.Ok(payload);
    }

    private static JsonObject DescribeDynamicProperty(AcadContext acad, DynamicBlockReferenceProperty property)
    {
        DynamicValueKind kind = KindOf(property.Value);
        DynamicUnitsKind units = UnitsOf(property);

        JsonArray allowed = new();
        foreach (object value in property.GetAllowedValues())
        {
            allowed.Add(DynamicPropertyConverter.ToWire(kind, units, value, acad.MmPerDrawingUnit));
        }

        return new JsonObject
        {
            ["name"] = property.PropertyName,
            ["description"] = property.Description,
            ["kind"] = DynamicPropertyConverter.KindName(kind),
            ["units"] = DynamicPropertyConverter.UnitsName(units),
            ["value"] = DynamicPropertyConverter.ToWire(kind, units, property.Value, acad.MmPerDrawingUnit),
            ["allowed_values"] = allowed,
            ["read_only"] = property.ReadOnly || kind == DynamicValueKind.Unsupported,
        };
    }

    private static DynamicValueKind KindOf(object? value)
    {
        return value switch
        {
            double => DynamicValueKind.Real,
            int => DynamicValueKind.Integer,
            short => DynamicValueKind.Short,
            string => DynamicValueKind.Text,
            _ => DynamicValueKind.Unsupported,
        };
    }

    private static DynamicUnitsKind UnitsOf(DynamicBlockReferenceProperty property)
    {
        return property.UnitsType switch
        {
            DynamicBlockReferencePropertyUnitsType.Distance => DynamicUnitsKind.Distance,
            DynamicBlockReferencePropertyUnitsType.Angular => DynamicUnitsKind.Angle,
            DynamicBlockReferencePropertyUnitsType.Area => DynamicUnitsKind.Area,
            _ => DynamicUnitsKind.None,
        };
    }

    // ---- acad_set_dynamic_block_properties --------------------------------------------------

    private static McpTool CreateSetDynamicPropertiesTool()
    {
        return new McpTool
        {
            Name = "acad_set_dynamic_block_properties",
            Description =
                "Sets dynamic properties on dynamic block references by handle, e.g. "
                + "{\"Distance1\": 1200, \"Visibility1\": \"Open\"}. Distances are millimetres, "
                + "angles degrees, areas mm²; a property with an allowed-value list only accepts "
                + "those values. Read acad_get_dynamic_block_properties first for the names. If "
                + "any property is unknown, read-only or out of range on any reference, nothing "
                + "is changed. Returns the values AutoCAD actually applied.",
            InputSchema = ToolSchema.Object()
                .StringArray("handles", "Dynamic block reference handles. All get the same values.", minItems: 1, maxItems: MaxHandles, required: true)
                .ValueMap("properties", "Property values by name. Names match case-insensitively.", required: true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = SetDynamicProperties,
        };
    }

    private static ToolResult SetDynamicProperties(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!DynamicPropertyConverter.TryReadRequests(args, "properties", out IReadOnlyList<KeyValuePair<string, JsonElement>> requests, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray results = new();

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!TryResolveReferences(tr, acad, handles, OpenMode.ForWrite, out List<BlockReference> references, out error))
                {
                    return ToolResult.Fail(error);
                }

                // Plan everything before touching anything: all-or-nothing.
                List<List<(DynamicBlockReferenceProperty Property, object Value)>> plans = new();

                foreach (BlockReference reference in references)
                {
                    if (!reference.IsDynamicBlock)
                    {
                        return ToolResult.Fail(
                            $"Handle {reference.Handle} ({BlockSupport.GetEffectiveName(tr, reference)}) is not a "
                            + "dynamic block reference, so nothing was changed.");
                    }

                    if (!TryPlanDynamicChanges(acad, reference, requests, out List<(DynamicBlockReferenceProperty, object)> plan, out error))
                    {
                        return ToolResult.Fail($"Block reference {reference.Handle}: {error} Nothing was changed.");
                    }

                    plans.Add(plan);
                }

                for (int r = 0; r < references.Count; r++)
                {
                    JsonObject applied = new();

                    foreach ((DynamicBlockReferenceProperty property, object value) in plans[r])
                    {
                        property.Value = value;

                        // Read back: AutoCAD snaps to increments and clamps to the
                        // parameter's range, so the caller should see what really applied.
                        DynamicValueKind kind = KindOf(property.Value);
                        applied[property.PropertyName] = DynamicPropertyConverter.ToWire(kind, UnitsOf(property), property.Value, acad.MmPerDrawingUnit);
                    }

                    results.Add(new JsonObject
                    {
                        ["handle"] = references[r].Handle.ToString(),
                        ["block_name"] = BlockSupport.GetEffectiveName(tr, references[r]),
                        ["applied"] = applied,
                    });
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_dynamic_block_properties outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "set the dynamic properties"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_dynamic_block_properties outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not set the dynamic properties; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/set_dynamic_block_properties references={results.Count} properties={requests.Count} "
            + $"handles={SummarizeHandles(handles)} elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["changed"] = results.Count;
        payload["references"] = results;

        return ToolResult.Ok(payload);
    }

    private static bool TryPlanDynamicChanges(
        AcadContext acad,
        BlockReference reference,
        IReadOnlyList<KeyValuePair<string, JsonElement>> requests,
        out List<(DynamicBlockReferenceProperty, object)> plan,
        out string error)
    {
        plan = new List<(DynamicBlockReferenceProperty, object)>();
        error = "";

        List<DynamicBlockReferenceProperty> properties = new();
        foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
        {
            properties.Add(property);
        }

        foreach (KeyValuePair<string, JsonElement> request in requests)
        {
            DynamicBlockReferenceProperty? match = properties.FirstOrDefault(
                p => string.Equals(p.PropertyName, request.Key, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                error = $"no dynamic property named \"{request.Key}\". Properties: "
                      + $"{string.Join(", ", properties.Select(p => $"\"{p.PropertyName}\""))}.";
                return false;
            }

            DynamicValueKind kind = KindOf(match.Value);

            if (match.ReadOnly || kind == DynamicValueKind.Unsupported)
            {
                error = $"dynamic property \"{match.PropertyName}\" is read-only.";
                return false;
            }

            List<object> allowed = new();
            foreach (object value in match.GetAllowedValues())
            {
                allowed.Add(value);
            }

            if (!DynamicPropertyConverter.TryConvert(
                    propertyName: match.PropertyName,
                    kind: kind,
                    units: UnitsOf(match),
                    value: request.Value,
                    mmPerDrawingUnit: acad.MmPerDrawingUnit,
                    allowedDbValues: allowed,
                    dbValue: out object converted,
                    error: out error))
            {
                return false;
            }

            plan.Add((match, converted));
        }

        return true;
    }

    // ---- acad_convert_block_to_static -------------------------------------------------------

    private static McpTool CreateConvertToStaticTool()
    {
        return new McpTool
        {
            Name = "acad_convert_block_to_static",
            Description =
                "Converts one dynamic block reference into a reference to a new, ordinary "
                + "(static) block that freezes its current geometry and attributes. Use this when "
                + "a dynamic block needs redefining: acad_redefine_block refuses dynamic blocks, "
                + "but can redefine the static block this creates. Other references to the "
                + "dynamic block are unaffected.",
            InputSchema = ToolSchema.Object()
                .String("handle", "The dynamic block reference to convert.", required: true)
                .String("new_block_name", "Name for the new static block. Must not exist yet.", required: true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Blocks,
            Handler = ConvertToStatic,
        };
    }

    private static ToolResult ConvertToStatic(ToolArgs args)
    {
        if (!args.TryGetString("handle", out string handle, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetString("new_block_name", out string newBlockName, out error))
        {
            return ToolResult.Fail(error);
        }

        newBlockName = newBlockName.Trim();
        if (!BlockNaming.TryValidate(newBlockName, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string previousName;

        try
        {
            // Two transactions on purpose. ConvertToStaticBlock writes the block table and the
            // block records itself; anything this call still holds open ForRead — the block
            // table from the name check, the definitions GetEffectiveName reads — makes it
            // fail with eWasOpenForRead (Phase 4 live). So check first, close everything, then
            // convert with only the reference open.
            ObjectId referenceId;
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(acad.Db.BlockTableId, OpenMode.ForRead);
                if (blockTable.Has(newBlockName))
                {
                    return ToolResult.Fail($"Block \"{newBlockName}\" already exists. Choose another name.");
                }

                if (!TryResolveReferences(tr, acad, new[] { handle }, OpenMode.ForRead, out List<BlockReference> references, out error))
                {
                    return ToolResult.Fail(error);
                }

                BlockReference reference = references[0];
                if (!reference.IsDynamicBlock)
                {
                    return ToolResult.Fail($"Handle {reference.Handle} is not a dynamic block reference; it is already static.");
                }

                previousName = BlockSupport.GetEffectiveName(tr, reference);
                referenceId = reference.ObjectId;

                tr.Commit();
            }

            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                BlockReference reference = (BlockReference)tr.GetObject(referenceId, OpenMode.ForWrite);
                reference.ConvertToStaticBlock(newBlockName);

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/convert_block_to_static handle={handle} outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx, "convert the block"));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/convert_block_to_static handle={handle} outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not convert the block; see the MCP log.");
        }

        sw.Stop();
        Log.Info($"op=tool/convert_block_to_static handle={handle} from=\"{previousName}\" to=\"{newBlockName}\" entities=1 elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["previous_block_name"] = previousName;
        payload["new_block_name"] = newBlockName;

        return ToolResult.Ok(payload);
    }

    // ---- shared -----------------------------------------------------------------------------

    /// <summary>Resolves handles that must ALL be block references, for the mutating tools.</summary>
    private static bool TryResolveReferences(
        Transaction tr,
        AcadContext acad,
        IReadOnlyList<string> handles,
        OpenMode openMode,
        out List<BlockReference> references,
        out string error)
    {
        references = new List<BlockReference>();

        if (!EntityResolver.TryResolveHandles(tr, acad.Db, handles, openMode, requireAll: true, out List<Entity> entities, out JsonArray _, out error))
        {
            return false;
        }

        if (!EntityResolver.TryRejectDuplicates(entities, out error))
        {
            return false;
        }

        List<string> offenders = new();
        foreach (Entity entity in entities)
        {
            if (entity is BlockReference reference)
            {
                references.Add(reference);
            }
            else
            {
                offenders.Add($"{entity.Handle} ({entity.GetRXClass().DxfName})");
            }
        }

        if (offenders.Count > 0)
        {
            error = $"{offenders.Count} of {entities.Count} handles are not block references, so nothing "
                  + $"was changed: {string.Join(", ", offenders)}.";
            references.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Regenerates after a redefinition so existing references repaint. Whether AutoCAD
    /// repaints them without this is not knowable from metadata (TODO: live check), so do it
    /// rather than leave stale graphics. A failure here never fails the call — the database
    /// change is already committed.
    /// </summary>
    private static bool TryRegen(AcadContext acad, string operation)
    {
        try
        {
            acad.Doc.Editor.Regen();
            return true;
        }
        catch (System.Exception ex)
        {
            Log.Warn($"op=tool/{operation} step=regen outcome=failed", ex);
            return false;
        }
    }

    private static string DescribeAcadFailure(Autodesk.AutoCAD.Runtime.Exception acEx, string action)
    {
        if (acEx.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
        {
            return $"Could not {action}: at least one entity involved is on a locked layer. Unlock "
                 + "the layer with acad_set_layer_state first. Nothing was changed.";
        }

        return $"AutoCAD rejected the request to {action}: {acEx.ErrorStatus}. Nothing was changed.";
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
